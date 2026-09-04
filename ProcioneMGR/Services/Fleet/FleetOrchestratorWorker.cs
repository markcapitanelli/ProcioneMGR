using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Carry;
using ProcioneMGR.Services.Notifications;

namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [AF2] Il braccio della Queen Bee: ogni tick legge lo stato (reader), decide (core puro),
/// applica l'ISTERESI sui ritiri e scrive il journal. L'esecuzione è arrivata per incrementi,
/// nell'ordine deciso dal proprietario: prima il RITIRO (AF2b/I12, 2026-08-19), poi l'AVVIO a
/// candidato singolo (J13/J14, 2026-08-25) — sempre gattata da dry-run, <c>ExecutionLanes</c> e
/// budget per tick, coi due gate <see cref="WhyNotExecuted"/> e
/// <see cref="WhyNotExecutedAssignment"/> che dicono il perché di ogni rifiuto. Vive nel SOLO
/// monolite (è il cervello: scheduler, planner e promozioni stanno già qui).
/// </summary>
public sealed class FleetOrchestratorWorker(
    IFleetStateReader reader,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptionsMonitor<FleetOptions> options,
    IOptionsMonitor<CarryOptions> carryOptions,
    IServiceProvider serviceProvider,
    ILogger<FleetOrchestratorWorker> logger,
    INotifier? notifier = null,
    Llm.Committee.IAiCommittee? committee = null,
    Llm.Narration.IPostMortemService? postMortems = null,
    // [J13] Il braccio che AVVIA: lo stesso deployer del click umano F5, con Source="fleet".
    // Opzionale come il resto dei collaboratori: assente = il braccio si dichiara assente.
    IGreyDeployer? greyDeployer = null) : BackgroundService
{
    /// <summary>Verdetti di ritiro CONSECUTIVI per corsia (isteresi: si agisce solo alla conferma).</summary>
    private readonly Dictionary<int, int> _retireStreak = new();

    /// <summary>
    /// [K12, 2026-08-31] Le cause di blocco gia' journalizzate, un INSIEME e non una sola.
    ///
    /// <para>Con una stringa sola bastavano due FleetNoOp nello stesso piano — ora possibile, il
    /// ramo grigio ne ha quattro — perche' si alternassero: A scrive, B sovrascrive il ricordo, al
    /// tick dopo A sembra nuovo e riscrive. Novantasei righe al giorno per due cause che non
    /// cambiano mai, cioe' esattamente il rumore che la deduplica esisteva per togliere.</para>
    /// </summary>
    private HashSet<string> _lastBlockedReasons = new(StringComparer.Ordinal);
    private DateTime? _lastCarryAlertUtc;

    /// <summary>
    /// [AF2b, I12] <b>Il braccio che FERMA una corsia esiste</b> — dal 2026-08-19.
    ///
    /// <para>Agisce solo sulle corsie elencate in <see cref="FleetOptions.ExecutionLanes"/> (vuota
    /// di default), solo su corsie in Paper verificato al momento dell'azione, al più
    /// <see cref="FleetOptions.MaxExecutionsPerTick"/> per giro, e solo su verdetti già confermati
    /// dall'isteresi. Fermare è l'azione REVERSIBILE della coppia: la corsia resta configurata e si
    /// riavvia con un click.</para>
    /// </summary>
    public const bool RetirementArmImplemented = true;

    /// <summary>
    /// [J13, PRD autonomia-operativa 2026-08-25] <b>Il braccio che AVVIA una corsia esiste</b>, per
    /// schieramenti a CANDIDATO SINGOLO: banda «pass» a gamba singola e — dietro
    /// <c>Fleet:GreyAutoDeploy</c> (J14) — fascia grigia. Un ensemble multi-gamba resta di solo
    /// journal col motivo dichiarato: una corsia ha UN simbolo, e lo schieramento di un ensemble
    /// che ne attraversa più d'uno su una corsia sola non è definito — quello resta il territorio
    /// dell'applicatore dell'impronta.
    ///
    /// <para>L'esecuzione passa dallo STESSO deployer del click umano F5 (<see cref="IGreyDeployer"/>,
    /// con <c>Source="fleet"</c> a journal): bracket automatico, frequenza attesa scritta sulla
    /// gamba, rilettura fail-closed della corsia al momento dell'azione. Un secondo percorso di
    /// schieramento sarebbe una seconda verità su come si schiera.</para>
    ///
    /// <para>Era l'ordine deciso dal proprietario il 2026-08-19: prima il ritiro (2026-08-19), poi
    /// l'avvio (oggi). Le costanti restano dichiarate QUI, accanto ai rami che le implementano, e
    /// non dedotte da <c>Fleet:DryRun</c>: quel flag dice che cosa è stato <i>chiesto</i>, non che
    /// cosa il codice sa <i>fare</i>.</para>
    /// </summary>
    public const bool AssignmentArmImplemented = true;

    /// <summary>
    /// [AF2b] <b>Questo ritiro si esegue davvero, e se no perché.</b> Puro e statico: la decisione e
    /// la sua spiegazione escono dalla STESSA valutazione.
    ///
    /// <para>Tenerle separate — la condizione nell'<c>if</c>, il motivo ricalcolato nel ramo
    /// <c>else</c> — è la classe di difetto già pagata quattro volte in questa ondata: due regole
    /// per la stessa domanda divergono, e a divergere sarebbe un journal che dà una spiegazione
    /// diversa da quella vera, nel posto dove qualcuno andrà a cercare cosa è successo.</para>
    ///
    /// <para>Le condizioni sono quattro e tutte necessarie: il braccio deve esistere, il dry-run
    /// essere spento, la corsia essere <b>elencata</b> in <see cref="FleetOptions.ExecutionLanes"/>,
    /// e il budget del tick non essere esaurito. La terza è quella che rende l'accensione graduale
    /// invece che totale.</para>
    /// </summary>
    /// <returns><c>null</c> = si esegue; altrimenti il motivo del rifiuto, in italiano.</returns>
    internal static string? WhyNotExecuted(FleetOptions opt, int laneId, int budgetLeft) =>
        !RetirementArmImplemented ? "braccio esecutivo assente"
        : opt.DryRun ? "dry-run"
        : opt.ExecutionLanes.Count == 0 ? "nessuna corsia autorizzata"
        : !opt.ExecutionLanes.Contains(laneId) ? $"corsia {laneId} non autorizzata"
        : budgetLeft <= 0 ? "budget di esecuzione del tick esaurito"
        : null;

    /// <summary>Vero se in questo assetto l'orchestratore può eseguire qualcosa su qualche corsia.</summary>
    internal static bool CanExecute(FleetOptions opt) =>
        RetirementArmImplemented && !opt.DryRun && opt.ExecutionLanes.Count > 0;

    /// <summary>
    /// [J13] Il gate dell'ASSEGNAZIONE, gemello di <see cref="WhyNotExecuted"/> ma coi suoi
    /// prerequisiti in più: il deployer deve esistere nell'host, l'azione deve portare una CHIAVE
    /// (un ensemble multi-gamba non si schiera su una corsia sola), e per i grigi il flag J14 deve
    /// essere acceso ANCHE al momento dell'esecuzione (hot-reload: il piano può essere nato con un
    /// assetto diverso). Stesso contratto: null = si esegue, altrimenti il motivo in italiano.
    /// </summary>
    internal static string? WhyNotExecutedAssignment(
        FleetOptions opt, int laneId, int budgetLeft, bool hasKey, bool hasDeployer, bool isGrey) =>
        !AssignmentArmImplemented ? "braccio di assegnazione assente"
        : !hasDeployer ? "deployer non disponibile in questo host"
        : isGrey && !opt.GreyAutoDeploy ? "Fleet:GreyAutoDeploy spento"
        : !hasKey ? "ensemble multi-gamba: lo schieramento su una corsia sola non è definito"
        : opt.DryRun ? "dry-run"
        : opt.ExecutionLanes.Count == 0 ? "nessuna corsia autorizzata"
        : !opt.ExecutionLanes.Contains(laneId) ? $"corsia {laneId} non autorizzata"
        : budgetLeft <= 0 ? "budget di esecuzione del tick esaurito"
        : null;

    /// <summary>Ultimo piano deciso, per il pannello (/admin/autonomy).</summary>
    public FleetPlan? LastPlan { get; private set; }

    public DateTime? LastTickUtc { get; private set; }

    /// <summary>
    /// [I8] Perché l'ultimo tick non ha fatto nulla, coi quattro numeri contati dagli stessi
    /// predicati della decisione. <c>null</c> = nessun tick ancora eseguito da questo processo.
    /// </summary>
    public FleetSilence? LastSilence { get; private set; }

    /// <summary>
    /// [K42, 2026-09-01] <b>Le condanne al ritiro in corso</b>: corsia → conferme accumulate.
    ///
    /// <para>È lo stato che K20 chiedeva di rendere visibile. Vive in memoria e muore col guscio —
    /// il costo misurato di un riavvio è 0,2 minuti a <c>RetireConfirmTicks = 2</c>, contro un
    /// cancello di dieci giorni — ma finché esiste dev'essere <b>leggibile</b>: fino a oggi una
    /// corsia poteva essere a un tick dall'essere fermata e nessuna superficie lo diceva.</para>
    ///
    /// <para>Copia, non la mappa viva: la legge un circuito Blazor mentre il worker la muta.</para>
    /// </summary>
    public IReadOnlyDictionary<int, int> RetireStreaks => new Dictionary<int, int>(_retireStreak);

    /// <summary>
    /// [K46, PRD autonomia-piena — Fase 3, 2026-09-02] <b>Perché l'ultimo tick è fallito</b>, o
    /// <c>null</c> se è andato. Il pannello lo mostra: un orchestratore che non riesce a girare non
    /// deve poter sembrare un orchestratore che non ha niente da fare.
    ///
    /// <para><b>Il fatto che l'ha resa necessaria.</b> Dal 2026-09-01 sera la flotta ha smesso di
    /// scrivere qualunque riga di journal: il tick decideva un'azione e falliva sull'INSERT, perché
    /// <c>Source</c> era <c>varchar(16)</c> e <c>DescribeAssignSource</c> produce
    /// <c>default:quorum-mancato</c> (22 caratteri). L'eccezione finiva in un <c>LogError</c>, e da
    /// fuori il sintomo era «il pannello dice un'azione, il journal è muto» — indistinguibile da
    /// «non c'è niente da fare» per chiunque non andasse a leggere i log del processo.</para>
    ///
    /// <para><b>Il difetto strutturale non era la colonna, era il silenzio.</b> Un guasto che si
    /// annuncia solo in un log che nessuno rilegge è, per il sistema che si sorveglia da solo, un
    /// guasto che non esiste. Questo è lo stesso principio della regola 5 — degradare dicendolo — e
    /// il posto dove mancava era proprio il governo.</para>
    /// </summary>
    public string? LastTickError { get; private set; }

    /// <summary>Da quando fallisce senza interruzioni: un giro storto è rumore, dieci sono un guasto.</summary>
    public int ConsecutiveTickFailures => _tickConsecutiveFailures;

    private int _tickConsecutiveFailures;
    private bool _tickFailureNotified;

    /// <summary>
    /// [K46] Dichiara il fallimento del tick: log, stato leggibile dal pannello, e <b>una</b>
    /// notifica critica per episodio — non una ogni quindici minuti, che consumerebbe il budget
    /// degli allarmi veri (lezione già pagata con la staleness a 60s su STX).
    /// </summary>
    private async Task DeclareTickFailureAsync(Exception ex, CancellationToken ct)
    {
        _tickConsecutiveFailures++;
        LastTickError = $"{ex.GetType().Name}: {ex.Message}";
        logger.LogError(ex, "Tick dell'orchestratore di flotta fallito ({Falliti} di fila); ritento al prossimo.",
            _tickConsecutiveFailures);

        if (_tickFailureNotified || notifier is null) return;
        _tickFailureNotified = true;
        try
        {
            await notifier.NotifyAsync(NotificationSeverity.Critical,
                "L'orchestratore di flotta non riesce a completare un tick",
                "La Regina sta decidendo ma non riesce a portare a termine il giro, quindi non schiera, non ritira e "
                + $"non scrive il proprio journal. Ultimo errore: {LastTickError}. "
                + "Finché dura, il pannello di /admin/autonomy mostra numeri di un giro che non è arrivato in fondo.", ct);
        }
        catch (Exception notifyEx)
        {
            // Un notificatore rotto non deve nascondere il guasto che stava annunciando.
            logger.LogError(notifyEx, "Anche la notifica del tick fallito è fallita.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue;
            if (opt.Enabled)
            {
                try
                {
                    await TickAsync(stoppingToken);
                    // [K46] Il giro è andato: se prima era rotto, la guarigione è una notizia
                    // quanto il guasto — altrimenti il pannello resta rosso per sempre.
                    if (LastTickError is not null)
                    {
                        logger.LogInformation("Orchestratore di flotta: il tick è tornato a funzionare dopo {Falliti} giri falliti.",
                            _tickConsecutiveFailures);
                        LastTickError = null;
                        _tickConsecutiveFailures = 0;
                        _tickFailureNotified = false;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { await DeclareTickFailureAsync(ex, stoppingToken); }
            }

            var delay = TimeSpan.FromMinutes(Math.Clamp(opt.TickMinutes, 1, 720));
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// [I8] Da che cosa è venuta la scelta, distinguendo le tre cause che prima finivano tutte
    /// sotto la parola «default».
    ///
    /// <para>La differenza che conta per chi legge il journal è fra «il comitato ha deliberato e la
    /// maggioranza non si è formata» e «il comitato non ha funzionato»: nel primo caso il default è
    /// la risposta, nel secondo è un ripiego su un guasto. Con una parola sola per entrambi, sedici
    /// giorni di righe identiche non dicevano quale dei due fosse.</para>
    /// </summary>
    internal static string DescribeAssignSource(
        Llm.Committee.CommitteeVerdict verdict, IReadOnlyCollection<string>? provideConfermatiGuasti = null)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (verdict.ByQuorum) return "committee";

        var validi = verdict.Votes.Count(v => v.Valid);
        if (verdict.Votes.Count == 0) return "default:non-interrogato";   // budget esaurito, zero provider con chiave

        // [K52, 2026-09-02] Questo ramo va PRIMA degli altri due, per lo stesso motivo per cui in
        // K40 «non so leggere le corsie» precede «le corsie sono impegnate»: quando un votante è
        // morto in modo permanente, dire «la maggioranza non si è formata» descrive il sintomo e
        // nasconde la causa. La differenza per chi legge è tutta qui — «riprova più tardi» contro
        // «vai a cambiare il nome di un modello, o il comitato resterà un timbro per sempre».
        //
        // [Revisione 2026-09-03] ...ma SOLO se il guasto è CONFERMATO dall'isteresi. K53 ha misurato
        // che su NVIDIA il 404 arriva 4 volte su 10 su un modello che funziona: etichettare il
        // journal «provider-guasti» su una risposta sola mandava chi legge a cercare un modello
        // morto che non c'era, mentre la causa vera era un disaccordo. Senza conferme note
        // (chiamante che non le passa) il ramo non si accende: meglio «quorum-mancato» che una
        // diagnosi congetturale scritta come fatto.
        if (provideConfermatiGuasti is { Count: > 0 } confermati
            && Llm.Committee.CommitteeDiagnosis.VotantiGuasti(verdict.Votes)
                .Any(g => confermati.Contains(g.Provider, StringComparer.OrdinalIgnoreCase)))
        {
            return "default:provider-guasti";
        }

        if (validi == 0) return "default:tutti-astenuti";                 // interrogati, nessuna risposta valida
        return "default:quorum-mancato";                                  // hanno risposto, la maggioranza non si è formata
    }

    private bool _committeeFaultNotified;

    /// <summary>
    /// [K52] Lo stato del comitato all'ultima interrogazione, per il pannello. <c>null</c> = non è
    /// mai stato interrogato da questo processo (che non è «sta bene»: è «non lo so», ed è la
    /// distinzione che K40 ha già pagato per un'altra superficie).
    /// </summary>
    public CommitteeFaultReport? LastCommitteeFault { get; private set; }

    /// <summary>
    /// [K52] Lo stato dei votanti: chi è caduto in QUESTO giro, chi lo fa da abbastanza giri da
    /// poterlo chiamare guasto, e se con i superstiti il quorum sia ancora aritmeticamente
    /// possibile.
    /// </summary>
    /// <param name="Votanti">Provider interrogati in questo giro.</param>
    /// <param name="Sospetti">Caduti in questo giro con un errore di forma «configurazione», con la causa testuale.</param>
    /// <param name="Confermati">Quelli fra i sospetti che cadono da <see cref="ConfermaGuastoGiri"/> giri di fila.</param>
    /// <param name="Serie">Provider → giri consecutivi di caduta, per mostrare quanto manca alla conferma.</param>
    /// <param name="MinValidi">La soglia richiesta (<c>Committee:MinValidVotes</c>).</param>
    /// <param name="QuorumIrraggiungibile">Vero = <b>coi soli confermati</b>, anche se i superstiti fossero unanimi non basterebbero.</param>
    /// <param name="AtUtc">Quando è stata fatta l'ultima consultazione CON voti che ha prodotto questo quadro.</param>
    /// <param name="UltimoGiroSenzaVoti">Vero = dopo questo quadro c'è stata almeno una consultazione a zero voti (budget esaurito, comitato spento): il quadro è più vecchio dell'ultima interrogazione, e lo dice.</param>
    public sealed record CommitteeFaultReport(
        int Votanti,
        IReadOnlyList<(string Provider, string Causa)> Sospetti,
        IReadOnlyList<string> Confermati,
        IReadOnlyDictionary<string, int> Serie,
        int MinValidi,
        bool QuorumIrraggiungibile,
        DateTime? AtUtc = null,
        bool UltimoGiroSenzaVoti = false);

    /// <summary>
    /// [K52, corretto il 2026-09-02 dopo la misura] Giri consecutivi di caduta prima di chiamarlo
    /// guasto.
    ///
    /// <para><b>Perché non basta una volta, e il numero che lo dimostra.</b> La prima versione di
    /// K52 dichiarava il guasto alla prima risposta 404/410. Misurando NVIDIA dal vivo su un
    /// campione controllato di 10 tentativi identici, stesso modello e stessa chiave:
    /// <b>6 successi e 4 volte</b> <c>HTTP 404 «Function '…': Not found for account '…'»</c>, con il
    /// 404 restituito in 753 ms — è il livello di instradamento che rifiuta, non il modello che
    /// manca. Un 404 su quel provider <b>non prova affatto</b> che la configurazione sia stantia:
    /// con la regola vecchia la piattaforma avrebbe emesso una notifica critica «il modello non
    /// esiste più» ogni due giri, su un provider che funziona.</para>
    ///
    /// <para>La distinzione resta giusta — Groq era davvero morto per sedici giorni — ma la prova
    /// non è la singola risposta: è la <b>ripetizione</b>. È la stessa isteresi di K42 sul ritiro e
    /// di K46 sul tick: un giro storto è rumore, tre di fila sono un guasto.</para>
    ///
    /// <para><b>[Revisione 2026-09-03] «Giri» = CONSULTAZIONI del comitato, non tick.</b> Il
    /// comitato viene interrogato solo sui pareggi dell'orchestratore, che sono rari (due in sedici
    /// giorni nel caso che ha motivato K52): la conferma arriva alla terza consultazione con lo
    /// stesso errore, non «in 45 minuti». Il pannello lo dice. Chi vuole una conferma nel tempo
    /// preme «Prova il comitato» in /admin/ai-supervisor, che non è invece soggetto all'isteresi
    /// e va letto per ciò che è: una risposta, non una diagnosi.</para>
    /// </summary>
    public const int ConfermaGuastoGiri = 3;

    /// <summary>Provider → giri consecutivi in cui è caduto con un errore di forma «configurazione».</summary>
    private readonly Dictionary<string, int> _committeeFaultStreak = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// [K52] Dichiara che il comitato ha perso dei votanti per un guasto di configurazione — ma
    /// <b>solo dopo</b> <see cref="ConfermaGuastoGiri"/> giri consecutivi, perché un 404 isolato è
    /// rumore misurato e non una diagnosi. Una notifica per episodio, come K46, e la guarigione è
    /// una notizia quanto il guasto.
    ///
    /// <para>Il testo distingue il caso in cui il quorum è <b>aritmeticamente</b> irraggiungibile:
    /// è la differenza fra «il comitato è più fragile» e «il comitato non esiste più, e ogni
    /// decisione la sta prendendo il default deterministico».</para>
    /// </summary>
    private async Task DeclareCommitteeFaultAsync(
        Llm.Committee.CommitteeVerdict verdict, int minValidVotes, CancellationToken ct)
    {
        // [Revisione 2026-09-03] Un giro con ZERO voti (budget esaurito, comitato spento) non dice
        // nulla sui votanti: non tocca le serie, non cancella una conferma, non riarma la notifica.
        // Prima azzerava tutto e loggava «tornati a rispondere», e al 404 successivo partiva una
        // NUOVA notifica critica — una per ogni esaurimento del budget, su un guasto permanente.
        // Il quadro precedente resta, ma DICE di essere più vecchio dell'ultima interrogazione.
        if (verdict.Votes.Count == 0)
        {
            if (LastCommitteeFault is { } precedente && !precedente.UltimoGiroSenzaVoti)
            {
                LastCommitteeFault = precedente with { UltimoGiroSenzaVoti = true };
            }
            return;
        }

        var sospetti = Llm.Committee.CommitteeDiagnosis.VotantiGuasti(verdict.Votes);
        var caduti = sospetti.Select(s => s.Provider).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var interrogati = verdict.Votes.Select(v => v.Provider).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var voto in verdict.Votes)
        {
            if (caduti.Contains(voto.Provider))
            {
                _committeeFaultStreak[voto.Provider] = _committeeFaultStreak.GetValueOrDefault(voto.Provider) + 1;
            }
            else if (voto.Valid)
            {
                // Un voto valido azzera: il provider ha appena dimostrato di funzionare.
                _committeeFaultStreak.Remove(voto.Provider);
            }
            // Un'astensione per altra causa (timeout, scelta fuori menù) NON tocca la serie: non è
            // prova a favore né contro un guasto di configurazione, e trattarla come una delle due
            // sarebbe inventare un'informazione che quel voto non porta.
        }

        // [Revisione 2026-09-03] Confermato = serie ≥ soglia E interrogato in questo giro senza un
        // voto valido. NON «caduto con 404 anche in questo giro»: un provider morto che stavolta va
        // in timeout o 429 resta morto — un'astensione per altra causa non è una guarigione, e
        // trattarla come tale spegneva il riquadro e riarmava la notifica critica a ogni giro
        // storto. Ma un provider che NON è più fra i votanti (tolto dalla configurazione, chiave
        // rimossa: il rimedio che la notifica stessa suggerisce) esce dalla diagnosi — non è né
        // guasto né guarito, e contarlo fra i confermati falserebbe i superstiti e il quorum.
        var confermati = _committeeFaultStreak
            .Where(kv => kv.Value >= ConfermaGuastoGiri && interrogati.Contains(kv.Key))
            .Select(kv => kv.Key)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // L'irraggiungibilità si calcola SUI SOLI CONFERMATI: coi sospetti direbbe «il comitato non
        // esiste più» a ogni 404 di passaggio.
        var superstiti = verdict.Votes.Count - confermati.Count;
        var irraggiungibile = verdict.Votes.Count > 0 && confermati.Count > 0
                              && superstiti < Math.Max(1, minValidVotes);

        LastCommitteeFault = new CommitteeFaultReport(
            verdict.Votes.Count,
            sospetti.Select(g => (g.Provider, Causa: Troncato(g.Reason))).ToList(),
            confermati,
            new Dictionary<string, int>(_committeeFaultStreak, StringComparer.OrdinalIgnoreCase),
            minValidVotes,
            irraggiungibile,
            AtUtc: DateTime.UtcNow);

        if (confermati.Count == 0)
        {
            if (_committeeFaultNotified)
            {
                logger.LogInformation("Comitato di flotta: i votanti guasti sono tornati a rispondere.");
                _committeeFaultNotified = false;
            }
            return;
        }

        logger.LogWarning(
            "Comitato di flotta: {Guasti} votanti su {Totale} cadono da almeno {Giri} giri ({Chi}). Quorum irraggiungibile: {Irr}.",
            confermati.Count, verdict.Votes.Count, ConfermaGuastoGiri, string.Join(", ", confermati), irraggiungibile);

        if (_committeeFaultNotified || notifier is null) return;
        _committeeFaultNotified = true;
        try
        {
            var elenco = string.Join(" · ", sospetti
                .Where(s => confermati.Contains(s.Provider, StringComparer.OrdinalIgnoreCase))
                .Select(g => $"{g.Provider}: {Troncato(g.Reason)}"));
            await notifier.NotifyAsync(NotificationSeverity.Critical,
                irraggiungibile
                    ? "Il comitato AI non può più raggiungere il quorum"
                    : "Il comitato AI ha perso dei votanti per un guasto di configurazione",
                (irraggiungibile
                    ? $"Restano {superstiti} votanti possibili contro i {minValidVotes} richiesti: "
                      + "ogni decisione della Regina la sta prendendo il default deterministico, e continuerà a prenderla "
                      + "finché la configurazione non cambia. "
                    : "Il comitato decide ancora, ma con meno voci di quante ne risultano configurate. ")
                + $"Non è un caso isolato: cadono da {ConfermaGuastoGiri} giri di fila con un errore che dice «il modello non esiste». {elenco}. "
                + "Si corregge in /admin/ai-supervisor, con «Scarica modelli» per l'elenco vero della propria chiave.", ct);
        }
        catch (Exception notifyEx)
        {
            logger.LogError(notifyEx, "Anche la notifica del comitato guasto è fallita.");
        }
    }

    /// <summary>[K52] Superficie di prova per l'isteresi: un giro di voti, senza toccare la flotta.</summary>
    internal Task ValutaComitatoPerTestAsync(Llm.Committee.CommitteeVerdict verdict, int minValidVotes)
        => DeclareCommitteeFaultAsync(verdict, minValidVotes, CancellationToken.None);

    /// <summary>[Revisione 2026-09-04] Superficie di prova per il ritiro con intento: ferma una corsia come farebbe il tick.</summary>
    internal Task RitiraPerTestAsync(StopAndFreeLane retire, CancellationToken ct = default)
        => ExecuteRetireAsync(retire, ct);

    private static string Troncato(string s) => s.Length <= 160 ? s : s[..160];

    /// <summary>Un tick completo. Pubblico per i test di integrazione e per un futuro "Esegui ora".</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var opt = options.CurrentValue;
        // [K51] Prima di decidere: chiudere i conti aperti del giro precedente.
        await RiconciliaIntentiAppesiAsync(opt.TickMinutes, ct);
        var state = await reader.ReadAsync(ct);
        var plan = FleetOrchestrator.Decide(state, opt);
        // [I8] La diagnosi si calcola SEMPRE, anche quando il piano contiene azioni: «perché non fa
        // nulla» va risposto anche quando qualcosa fa, altrimenti il pannello sarebbe vuoto proprio
        // nei giri interessanti.
        LastSilence = FleetOrchestrator.Explain(state, opt);

        // [AF3] Sui PAREGGI (più candidati idonei della stessa assegnazione) il comitato può
        // scegliere DENTRO il menù che il core ha già validato. Fonte a journal: "committee" se
        // il quorum ha scelto, "default" se è stato consultato ed è ricaduto sulla regola,
        // "rules" se non è mai stato interpellato.
        var assignSource = "rules";
        var votesJson = "[]";
        if (opt.UseCommittee && committee is not null && plan.Menu is { } menu)
        {
            // [Revisione 2026-09-03] Lo STESSO menù non si riconsulta a ogni tick. Da quando un
            // rifiuto non brucia più il candidato (dry-run, corsia non autorizzata), la coda resta
            // uguale fra un tick e l'altro: senza questa memoria il comitato veniva interrogato
            // 96 volte al giorno per una decisione che nessuno avrebbe eseguito, consumando il
            // budget LLM anche per le decisioni vere. Il verdetto precedente si riusa, voti compresi.
            var firmaMenu = $"{menu.LaneId}|{string.Join(",", menu.Eligible.Select(c => c.RunId.ToString("N")).OrderBy(x => x, StringComparer.Ordinal))}";
            var consulta = firmaMenu != _ultimoMenuConsultato || _ultimoVerdettoDelMenu is null;
            if (!consulta)
            {
                var precedente = _ultimoVerdettoDelMenu!.Value;
                assignSource = precedente.Source;
                votesJson = precedente.VotesJson;
                if (precedente.Eletto is Guid elettoPrima) plan = ApplicaEletto(plan, menu, elettoPrima);
                logger.LogDebug("Comitato: menù invariato dal giro precedente (corsia {Lane}), verdetto riusato senza una nuova consultazione.", menu.LaneId);
            }
            if (consulta)
            try
            {
                // [G4] Contesto in più: come sono andate le ultime operazioni in perdita DI QUESTA
                // corsia, riassunte per causa. È informazione, non potere: il menù, il quorum e il
                // default deterministico restano quelli di AF3, e un post-mortem assente o un
                // servizio muto lasciano la domanda esattamente com'era prima.
                var postMortem = string.Empty;
                if (postMortems is not null)
                {
                    try { postMortem = await postMortems.BuildCommitteeContextAsync(menu.LaneId, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogDebug(ex, "Contesto post-mortem non disponibile: il comitato decide senza."); }
                }

                var question = new Llm.Committee.CommitteeQuestion(
                    "fleet-assignment",
                    $"Corsia Paper {menu.LaneId} libera; un solo slot per questo tick. Candidati (run della pipeline, tutti validati):\n"
                    + string.Join("\n", menu.Eligible.Select(c =>
                        $"- {c.RunId:N}: {c.Summary}; ~{c.TradesPerMonth:F1} trade/mese; {c.Timeframe}; completato {c.CompletedAtUtc:yyyy-MM-dd}"))
                    + (string.IsNullOrEmpty(postMortem) ? "" : $"\n\n{postMortem}"),
                    menu.Eligible.Select(c => new Llm.Committee.CommitteeOption(c.RunId.ToString("N"),
                        $"{c.Summary} (~{c.TradesPerMonth:F1} trade/mese, {c.Timeframe})")).ToList(),
                    menu.DefaultRunId.ToString("N"));

                var verdict = await committee.AskAsync(question, ct);
                votesJson = System.Text.Json.JsonSerializer.Serialize(verdict.Votes);
                // [I8] «default» collassava tre cause diverse in una parola: nessun provider ha
                // risposto validamente, il quorum non è stato raggiunto, oppure il comitato non è
                // partito affatto (budget esaurito, nessun provider con chiave). Chi legge il
                // journal vedeva sempre lo stesso «default» e non poteva sapere se il comitato
                // avesse deliberato o non fosse mai stato interrogato — che è la differenza fra
                // «ha scelto la regola» e «non ha funzionato».
                // [K52, 2026-09-02] E poi lo si DICE. Il journal registra la causa da I8, ma il
                // journal lo legge chi va a cercarlo: un comitato ridotto a un votante su tre
                // continuava a sembrare un comitato da qualunque superficie della piattaforma.
                var minValidi = serviceProvider
                    .GetService<IOptionsMonitor<Llm.Committee.CommitteeOptions>>()?.CurrentValue.MinValidVotes ?? 2;
                await DeclareCommitteeFaultAsync(verdict, minValidi, ct);

                // [Revisione 2026-09-03] La fonte si descrive DOPO l'isteresi: «provider-guasti»
                // solo se il votante caduto è confermato, altrimenti un 404 isolato (rumore misurato)
                // finirebbe a journal come diagnosi.
                assignSource = DescribeAssignSource(verdict, LastCommitteeFault?.Confermati);

                Guid? eletto = null;
                if (verdict.ByQuorum && Guid.TryParseExact(verdict.ChosenOptionId, "N", out var chosen)
                    && chosen != menu.DefaultRunId
                    && menu.Eligible.Any(c => c.RunId == chosen))
                {
                    eletto = chosen;
                    plan = ApplicaEletto(plan, menu, chosen);
                }
                _ultimoMenuConsultato = firmaMenu;
                _ultimoVerdettoDelMenu = (assignSource, votesJson, eletto);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Il comitato non deve MAI poter rompere il tick: fallito = si resta sulla regola.
                logger.LogWarning(ex, "Comitato non consultabile: resta la scelta deterministica.");
                assignSource = "default";
            }
        }

        LastPlan = plan;
        LastTickUtc = DateTime.UtcNow;

        if (!opt.DryRun && opt.ExecutionLanes.Count == 0)
        {
            // Dirlo a voce alta batte il silenzio: chi ha spento il dry-run si aspetta che qualcosa
            // succeda, e senza corsie autorizzate non succede — per progetto, non per guasto.
            logger.LogWarning("Fleet:DryRun=false ma Fleet:ExecutionLanes è VUOTA: nessuna corsia autorizzata, il tick resta di solo journal.");
        }

        await EseguiPianoAsync(plan, opt, assignSource, votesJson, ct);
    }

    /// <summary>Firma dell'ultimo menù sottoposto al comitato, e il suo verdetto: si riusa finché il menù non cambia.</summary>
    private string? _ultimoMenuConsultato;
    private (string Source, string VotesJson, Guid? Eletto)? _ultimoVerdettoDelMenu;

    /// <summary>I rifiuti già scritti a journal nel tick precedente: si riscrivono solo se cambiano.</summary>
    private HashSet<string> _ultimiRifiuti = new(StringComparer.Ordinal);

    /// <summary>
    /// Sostituisce nel piano l'assegnazione della corsia del menù con l'eletto dal comitato. Il
    /// verdetto è GIÀ garantito dentro il menù (doppia validazione nel comitato): qui si sostituisce
    /// solo l'azione corrispondente, mai altro.
    /// </summary>
    private static FleetPlan ApplicaEletto(FleetPlan plan, FleetAssignmentMenu menu, Guid chosen)
    {
        if (menu.Eligible.FirstOrDefault(c => c.RunId == chosen) is not { } elected) return plan;
        return plan with
        {
            Actions = plan.Actions.Select(a => a switch
            {
                // [J13] La CHIAVE dell'eletto viaggia con la sostituzione: senza, il
                // comitato che sceglie renderebbe l'azione ineseguibile (key null).
                AssignCandidateToLane assign when assign.LaneId == menu.LaneId
                                => new AssignCandidateToLane(elected.RunId, menu.LaneId,
                                    $"Scelto dal comitato fra {menu.Eligible.Count} candidati: {elected.Summary} " +
                                    $"(~{elected.TradesPerMonth:F1} trade/mese, {elected.Timeframe}).",
                                    CandidateKey: elected.Identity),
                            // [J14] Il menù può essere dei GRIGI: stessa sostituzione, stessa azione.
                            AssignGreyCandidateToLane greyAssign when greyAssign.LaneId == menu.LaneId && elected.Identity is not null
                                => new AssignGreyCandidateToLane(elected.RunId, elected.Identity, menu.LaneId,
                                    $"[J14] Scelto dal comitato fra {menu.Eligible.Count} candidati grigi: {elected.Summary} " +
                                    $"(~{elected.TradesPerMonth:F1} trade/mese, {elected.Timeframe})."),
                            _ => a,
                        }).ToList(),
        };
    }

    /// <summary>
    /// L'esecuzione del piano: journal dell'isteresi, budget del giro, azioni. Separata dalla
    /// consultazione del comitato per leggibilità (revisione 2026-09-03).
    /// </summary>
    private async Task EseguiPianoAsync(FleetPlan plan, FleetOptions opt, string assignSource, string votesJson, CancellationToken ct)
    {
        var (confirmedRetires, cambiIsteresi) = ApplyRetireHysteresis(plan, Math.Max(1, opt.RetireConfirmTicks));

        // [K42] La condanna a metà strada si SCRIVE. Prima di ogni altra cosa del giro, perché è la
        // sola riga che dice cosa la flotta sta per fare — e perché finora non esisteva: il ramo
        // «verdetto non ancora confermato» si annotava solo nel log, e l'unica traccia di un'azione
        // ogni quindici minuti era un contatore in una pagina.
        foreach (var cambio in cambiIsteresi)
        {
            await JournalAsync(new OrchestratorDecision
            {
                AtUtc = DateTime.UtcNow,
                Kind = "RetirePending",
                LaneId = cambio.LaneId,
                Source = "rules",
                Reason = cambio.Reason,
                DryRun = opt.DryRun,
                // Non è un'azione applicata: è lo stato di una decisione in corso. Applied=true
                // qui significherebbe «la corsia è stata fermata», che è falso.
                Applied = false,
                // [Revisione 2026-09-03] Un'annotazione, non un'azione: senza Outcome esplicito la
                // riga nasceva col default e il pannello la mostrava «eseguita».
                Outcome = DecisionOutcome.Noted,
            }, ct);
            logger.LogInformation("Ritiro corsia {Lane}: serie {Da} → {A}. {Reason}",
                cambio.LaneId, cambio.Da, cambio.A, cambio.Reason);
        }

        // [AF2b] Il budget di esecuzione del giro: una corsia per volta, per poter distinguere una
        // decisione giusta da un guasto del lettore di stato.
        //
        // [K15, PRD autonomia-piena 2026-08-31] DUE budget, non uno. Fino a oggi ritiri e
        // assegnazioni pescavano dallo stesso contatore, e con il default a 1 questo significava
        // due cose, entrambe indesiderate:
        //  - il tick che LIBERA una corsia non poteva anche assegnarla: bisognava aspettare il
        //    giro dopo, quindici minuti in cui la corsia resta ferma per un dettaglio contabile;
        //  - a decidere chi prendeva l'unico posto era l'ORDINE di plan.Actions, cioe' una
        //    priorita' che nessuno ha mai scelto ne' scritto.
        // I due gesti hanno ragioni diverse per essere limitati — «non fermare quattro corsie
        // insieme» e «non schierare quattro candidati insieme» — e quindi due tetti. Nessuna
        // manopola nuova: MaxAssignmentsPerTick esiste gia', ha il suo pannello, e governa gia' il
        // numero di assegnazioni che il PIANO contiene: qui governa anche la loro esecuzione.
        var budgetRitiri = Math.Max(1, opt.MaxExecutionsPerTick);
        var budgetAssegnazioni = Math.Max(1, opt.MaxAssignmentsPerTick);
        // [K61] Un TERZO tetto, per la stessa ragione per cui K15 ne ha voluti due: una sostituzione
        // non è né solo un ritiro né solo un'assegnazione, e lasciarla pescare dai due tetti
        // esistenti significherebbe che alzare le assegnazioni alza anche quante corsie in corsa si
        // possono fermare in un colpo. Consuma comunque anche gli altri due: fa entrambe le cose.
        var budgetSostituzioni = Math.Max(1, opt.MaxReplacementsPerTick);

        var bloccatiInQuestoGiro = new HashSet<string>(StringComparer.Ordinal);
        // [Revisione 2026-09-03] I rifiuti si scrivono una volta per CAUSA, come i blocchi: da quando
        // un rifiuto non brucia più il candidato, lo stesso rifiuto si ripresenta a ogni tick, e
        // 96 righe al giorno identiche saturerebbero il journal e il digest.
        var rifiutiInQuestoGiro = new HashSet<string>(StringComparer.Ordinal);
        bool RifiutoNuovo(string chiave)
        {
            rifiutiInQuestoGiro.Add(chiave);
            return !_ultimiRifiuti.Contains(chiave);
        }

        foreach (var action in plan.Actions)
        {
            switch (action)
            {
                case AssignCandidateToLane assign:
                    if (WhyNotExecutedAssignment(opt, assign.LaneId, budgetAssegnazioni,
                        hasKey: assign.CandidateKey is not null, hasDeployer: greyDeployer is not null, isGrey: false) is { } percheAssign)
                    {
                        if (RifiutoNuovo($"Assign|{assign.LaneId}|{assign.RunId:N}|{percheAssign}"))
                        {
                            await JournalAsync(new OrchestratorDecision
                            {
                                AtUtc = DateTime.UtcNow, Kind = "Assign", LaneId = assign.LaneId, RunId = assign.RunId,
                                Source = assignSource, VotesJson = votesJson,
                                // [Revisione 2026-09-03] Rifiutata per regola: Outcome esplicito (prima
                                // nasceva col default e risultava «eseguita»), e DryRun dice se ERA in
                                // prova, non «non ho agito» — il difetto che K51 aveva già nominato.
                                Reason = $"[{percheAssign}] {assign.Reason}", DryRun = opt.DryRun, Applied = false,
                                Outcome = DecisionOutcome.Refused,
                            }, ct);
                            logger.LogInformation("[{Perche}] Assegnerei il run {Run} alla corsia {Lane}: {Reason}",
                                percheAssign, assign.RunId, assign.LaneId, assign.Reason);
                        }
                    }
                    else
                    {
                        budgetAssegnazioni--;
                        await ExecuteAssignAsync(assign.RunId, assign.CandidateKey!, assign.LaneId,
                            isGrey: false, assign.Reason, assignSource, votesJson, ct);
                    }
                    break;

                case AssignGreyCandidateToLane greyAssign:
                    if (WhyNotExecutedAssignment(opt, greyAssign.LaneId, budgetAssegnazioni,
                        hasKey: true, hasDeployer: greyDeployer is not null, isGrey: true) is { } percheGrey)
                    {
                        if (RifiutoNuovo($"AssignGrey|{greyAssign.LaneId}|{greyAssign.RunId:N}|{percheGrey}"))
                        {
                            await JournalAsync(new OrchestratorDecision
                            {
                                AtUtc = DateTime.UtcNow, Kind = "Assign", LaneId = greyAssign.LaneId, RunId = greyAssign.RunId,
                                Source = assignSource, VotesJson = votesJson,
                                Reason = $"[{percheGrey}] {greyAssign.Reason}", DryRun = opt.DryRun, Applied = false,
                                Outcome = DecisionOutcome.Refused,
                            }, ct);
                            logger.LogInformation("[{Perche}] Schiererei il grigio {Run} sulla corsia {Lane}: {Reason}",
                                percheGrey, greyAssign.RunId, greyAssign.LaneId, greyAssign.Reason);
                        }
                    }
                    else
                    {
                        budgetAssegnazioni--;
                        await ExecuteAssignAsync(greyAssign.RunId, greyAssign.CandidateKey, greyAssign.LaneId,
                            isGrey: true, greyAssign.Reason, assignSource, votesJson, ct);
                    }
                    break;

                case StopAndFreeLane retire when confirmedRetires.Contains(retire.LaneId):
                    if (WhyNotExecuted(opt, retire.LaneId, budgetRitiri) is { } perche)
                    {
                        if (RifiutoNuovo($"Retire|{retire.LaneId}|{perche}"))
                        {
                            await JournalAsync(new OrchestratorDecision
                            {
                                AtUtc = DateTime.UtcNow, Kind = "Retire", LaneId = retire.LaneId,
                                Reason = $"[{perche}] {retire.Reason}", DryRun = opt.DryRun, Applied = false,
                                Outcome = DecisionOutcome.Refused,
                            }, ct);
                            logger.LogWarning("[{Perche}] Ritirerei la corsia {Lane}: {Reason}", perche, retire.LaneId, retire.Reason);
                        }
                    }
                    else
                    {
                        budgetRitiri--;
                        await ExecuteRetireAsync(retire, ct);
                    }
                    break;

                case StopAndFreeLane pending:
                    // [K42] Verdetto non ancora confermato dall'isteresi. La riga di journal l'ha
                    // GIÀ scritta il blocco dei cambi di serie, qui sopra, e solo se la serie si è
                    // MOSSA: questo ramo si ripete a ogni tick finché la condanna dura, quindi
                    // journalizzare qui darebbe 96 righe al giorno per una decisione ferma. Resta il
                    // log, che è per chi sta guardando adesso.
                    logger.LogDebug("Ritiro corsia {Lane} in attesa di conferma ({Streak}/{Needed}): {Reason}",
                        pending.LaneId, _retireStreak.GetValueOrDefault(pending.LaneId), Math.Max(1, opt.RetireConfirmTicks), pending.Reason);
                    break;

                // [K61] SOSTITUZIONE confermata dall'isteresi: ferma l'inerte e schiera al suo posto.
                case ReplaceLaneOccupant replace when confirmedRetires.Contains(replace.LaneId):
                    // Due cancelli, perché l'azione è due cose: deve poter FERMARE (budget dei
                    // ritiri, corsia autorizzata) e deve poter SCHIERARE (budget delle assegnazioni,
                    // deployer presente). Passare da uno solo darebbe a questa azione un permesso
                    // che nessuna delle due metà ha da sola.
                    var percheReplace = WhyNotExecuted(opt, replace.LaneId, budgetRitiri)
                        ?? WhyNotExecutedAssignment(opt, replace.LaneId, budgetAssegnazioni,
                            hasKey: true, hasDeployer: greyDeployer is not null, isGrey: true)
                        ?? (budgetSostituzioni <= 0 ? "budget di sostituzione esaurito in questo giro" : null);

                    if (percheReplace is not null)
                    {
                        if (RifiutoNuovo($"Replace|{replace.LaneId}|{replace.RunId:N}|{percheReplace}"))
                        {
                            await JournalAsync(new OrchestratorDecision
                            {
                                AtUtc = DateTime.UtcNow, Kind = "Retire", LaneId = replace.LaneId, RunId = replace.RunId,
                                Source = assignSource, VotesJson = votesJson,
                                Reason = $"[{percheReplace}] {replace.Reason}", DryRun = opt.DryRun, Applied = false,
                                Outcome = DecisionOutcome.Refused,
                            }, ct);
                            logger.LogInformation("[{Perche}] Sostituirei l'occupante della corsia {Lane}: {Reason}",
                                percheReplace, replace.LaneId, replace.Reason);
                        }
                    }
                    else
                    {
                        budgetRitiri--;
                        budgetAssegnazioni--;
                        budgetSostituzioni--;
                        await ExecuteReplaceAsync(replace, opt, assignSource, votesJson, ct);
                    }
                    break;

                case ReplaceLaneOccupant pendingReplace:
                    // [K42] Come per il ritiro: la riga della serie l'ha già scritta il blocco dei
                    // cambi, e ripeterla qui darebbe 96 righe al giorno per una decisione ferma.
                    logger.LogDebug("Sostituzione corsia {Lane} in attesa di conferma ({Streak}/{Needed}): {Reason}",
                        pendingReplace.LaneId, _retireStreak.GetValueOrDefault(pendingReplace.LaneId),
                        Math.Max(1, opt.RetireConfirmTicks), pendingReplace.Reason);
                    break;

                case ProposeGreyCandidate grey:
                    await JournalAsync(new OrchestratorDecision
                    {
                        AtUtc = DateTime.UtcNow, Kind = "ProposeGrey", RunId = grey.RunId,
                        Reason = grey.Reason, DryRun = opt.DryRun, Applied = true, // la proposta È l'azione
                        Outcome = DecisionOutcome.Applied,
                    }, ct);
                    if (notifier is not null)
                    {
                        await notifier.NotifyAsync(NotificationSeverity.Info,
                            "Candidato in fascia grigia per il forward test",
                            $"{grey.Reason} Lo schieramento resta un click tuo (F5: mai automatico, mai oltre Paper).", ct);
                    }
                    break;

                case FleetNoOp blocked:
                    // Un blocco porta informazione, ma una volta per CAUSA, non 96 volte al giorno.
                    bloccatiInQuestoGiro.Add(blocked.Reason);
                    if (!_lastBlockedReasons.Contains(blocked.Reason))
                    {
                        await JournalAsync(new OrchestratorDecision
                        {
                            AtUtc = DateTime.UtcNow, Kind = "Blocked", Reason = blocked.Reason,
                            DryRun = opt.DryRun, Applied = false, Outcome = DecisionOutcome.Noted,
                        }, ct);
                        logger.LogInformation("Flotta bloccata: {Reason}", blocked.Reason);
                    }
                    break;
            }
        }

        // Le cause rientrate escono dal ricordo: se tornano, tornano a essere una notizia.
        _lastBlockedReasons = bloccatiInQuestoGiro;
        _ultimiRifiuti = rifiutiInQuestoGiro;

        await WatchCarryAsync(ct);
    }

    /// <summary>
    /// [J13/J14] L'esecuzione di un'assegnazione: lo STESSO deployer del click umano F5
    /// (bracket automatico, frequenza attesa, rilettura fail-closed della corsia), con
    /// <c>Source="fleet"</c>.
    ///
    /// <para>[K51, 2026-09-02] La riga di journal si apre <b>PRIMA</b> di chiamare il deployer, e si
    /// chiude con l'esito. Il worker la apre lui perché deve portare i voti del comitato e la fonte
    /// della scelta, che il deployer non conosce — ma ora l'handle passa, quindi «una riga per
    /// azione» non è più un accordo fra due file: è la forma.</para>
    ///
    /// <para><b>E se l'intento non si scrive, non si schiera.</b> È il fail-closed della regola 4
    /// applicato all'azione meno reversibile della piattaforma, e il costo del verso opposto è
    /// misurato: il 2026-08-31 due schieramenti su quattro sono avvenuti senza lasciare riga.</para>
    /// </summary>
    private async Task ExecuteAssignAsync(
        Guid runId, string candidateKey, int laneId, bool isGrey,
        string reason, string assignSource, string votesJson, CancellationToken ct)
    {
        // [K51] L'intento, prima di toccare la corsia.
        int intentoId;
        try
        {
            intentoId = await ApriIntentoAssegnazioneAsync(runId, laneId, assignSource, votesJson, reason, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Corsia {Lane}: impossibile aprire l'intento a journal; NON si schiera.", laneId);
            if (notifier is not null)
            {
                await notifier.NotifyAsync(NotificationSeverity.Critical,
                    "La flotta non riesce a registrare cosa sta per fare",
                    $"L'orchestratore stava per schierare {candidateKey} sulla corsia {laneId} e non e' riuscito a "
                    + $"scrivere l'intento nel journal ({ex.Message}). Lo schieramento NON e' avvenuto: riscrivere una "
                    + "corsia senza poterlo registrare perde per sempre la configurazione precedente e la provenienza "
                    + "di quella nuova.", ct);
            }
            return;
        }

        string? error = null;
        var message = string.Empty;
        try
        {
            // allowSurvivor: il braccio «pass» schiera sopravvissuti; quello grigio solo grigi.
            var result = await greyDeployer!.DeployAsync(runId, candidateKey, laneId,
                startPaper: true, ct, source: "fleet", allowSurvivor: !isGrey, journalId: intentoId);
            message = result.Message;
            if (!result.Success) error = result.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            error = ex.Message;
            message = ex.Message;
        }

        await ChiudiIntentoAssegnazioneAsync(intentoId, error, $"{reason} Esito: {message}", ct);

        if (error is null)
        {
            logger.LogWarning("Corsia {Lane}: candidato {Tipo} SCHIERATO dall'orchestratore ({Key}).",
                laneId, isGrey ? "GRIGIO" : "validato", candidateKey);
            if (notifier is not null)
            {
                await notifier.NotifyAsync(NotificationSeverity.Warning,
                    isGrey ? $"Flotta: candidato GRIGIO schierato sulla corsia {laneId}" : $"Flotta: candidato schierato sulla corsia {laneId}",
                    $"{reason}\n{message}\nAzione AUTONOMA dell'orchestratore (J13/J14): la corsia si ferma con un click da /trading.", ct);
            }
        }
        else
        {
            logger.LogWarning("Corsia {Lane}: schieramento automatico NON riuscito ({Errore}).", laneId, error);
            if (notifier is not null)
            {
                await notifier.NotifyAsync(NotificationSeverity.Warning,
                    $"Flotta: schieramento sulla corsia {laneId} non riuscito", error, ct);
            }
        }
    }

    /// <summary>
    /// [K61, 2026-09-04] <b>La SOSTITUZIONE: ferma l'occupante inerte e schiera al suo posto.</b>
    ///
    /// <para><b>L'ordine è obbligato e non è una preferenza.</b> <c>GreyDeployer.DeployAsync</c>
    /// rifiuta di schierare su una corsia che sta girando («fermala prima, o scegline una libera»),
    /// quindi non esiste uno schieramento atomico: si ferma, poi si schiera. Fra i due passi c'è una
    /// finestra in cui la corsia è ferma e configurata sull'ipotesi VECCHIA.</para>
    ///
    /// <para><b>La finestra è l'esito peggiore accettabile, e si è scelto quale.</b> Se lo
    /// schieramento fallisce, la corsia resta ferma e configurata come prima: è lo stesso stato in
    /// cui la lascerebbe un ritiro normale — reversibile con un clic da <c>/trading</c> — e al giro
    /// dopo il braccio ordinario la vede LIBERA e la riempie da sé. Il contrario (schierare prima e
    /// fermare dopo) non è possibile, e fermare senza aver verificato nulla sarebbe peggio.</para>
    ///
    /// <para><b>Le posizioni aperte vietano la sostituzione, e si rileggono ADESSO.</b> Non è
    /// prudenza generica: <c>StopAsync</c> lascia le posizioni aperte, e il successivo
    /// <c>StartAsync</c> in Paper esegue <c>OpenPositions.ExecuteDelete</c> senza filtro di modalità
    /// — la posizione sparirebbe <b>senza TradeRecord, senza PnL, senza audit</b>. È il danno che il
    /// doc-comment di K36 descrive parola per parola e che il 2026-08-31 si è già verificato sulla
    /// corsia 6 (short DOGE/USDT, 799 USDT di nozionale). Il piano nasce da una fotografia che può
    /// avere minuti: una posizione può essersi aperta nel frattempo, e il sorvegliante K36 non è la
    /// rete di sicurezza — se stop e schieramento cadono nello stesso giro la sua finestra di
    /// osservazione può non aprirsi mai.</para>
    ///
    /// <para><b>Non si appiattisce la posizione, di proposito.</b> <c>LanePromoter</c> chiama
    /// <c>CloseAllPositionsAsync</c> prima dello stop, ed è giusto lì: un cambio di modalità deve
    /// spostare la corsia intera e non ha alternative. Qui invece l'alternativa c'è ed è gratis —
    /// aspettare. Chiudere a mercato una posizione viva realizzerebbe un PnL a metà ipotesi e
    /// sporcherebbe proprio il forward test che questa regola dice di non voler consumare. Una corsia
    /// con una posizione aperta, del resto, <b>sta operando</b>: non è il bersaglio di una regola che
    /// si chiama «sostituisci ciò che è inerte».</para>
    /// </summary>
    private async Task ExecuteReplaceAsync(
        ReplaceLaneOccupant replace, FleetOptions opt, string assignSource, string votesJson, CancellationToken ct)
    {
        Trading.ITradingEngine engine;
        string? rifiuto = null;
        try
        {
            engine = serviceProvider.GetRequiredKeyedService<Trading.ITradingEngine>(replace.LaneId);
            var status = await engine.GetStatusAsync(ct);

            if (status.Mode != Trading.TradingMode.Paper)
            {
                rifiuto = $"corsia in {status.Mode}, non Paper: l'orchestratore non sostituisce corsie che non governa";
            }
            else if (!status.IsRunning)
            {
                rifiuto = "corsia già ferma: non è una sostituzione, la riempie il braccio ordinario";
            }
            else if (status.OpenPositionCount > 0)
            {
                rifiuto = $"la corsia ha {status.OpenPositionCount} posizioni APERTE: fermarla e riscriverla le "
                    + "cancellerebbe senza scrivere alcun TradeRecord (danno K36). Una corsia con una posizione viva "
                    + "sta operando, e non è inerte per definizione";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await JournalAsync(new OrchestratorDecision
            {
                AtUtc = DateTime.UtcNow, Kind = "Retire", LaneId = replace.LaneId, RunId = replace.RunId,
                Source = assignSource, VotesJson = votesJson,
                Reason = replace.Reason, DryRun = false, Applied = false, Error = ex.Message,
                Outcome = DecisionOutcome.Failed,
            }, ct);
            logger.LogWarning("Sostituzione sulla corsia {Lane} NON eseguita ({Error}).", replace.LaneId, ex.Message);
            return;
        }

        if (rifiuto is not null)
        {
            await JournalAsync(new OrchestratorDecision
            {
                AtUtc = DateTime.UtcNow, Kind = "Retire", LaneId = replace.LaneId, RunId = replace.RunId,
                Source = assignSource, VotesJson = votesJson,
                Reason = replace.Reason, DryRun = false, Applied = false, Error = rifiuto,
                Outcome = DecisionOutcome.Refused,
            }, ct);
            logger.LogWarning("Sostituzione sulla corsia {Lane} NON eseguita ({Error}).", replace.LaneId, rifiuto);
            return;
        }

        // --- Metà 1: fermare. Intento PRIMA dello stop, come il ritiro dopo la revisione K51. ----
        int intentoStop;
        try
        {
            intentoStop = await ApriIntentoAsync("Retire", replace.RunId, replace.LaneId, assignSource, votesJson,
                replace.Reason, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Corsia {Lane}: impossibile aprire l'intento di sostituzione a journal; NON si ferma.", replace.LaneId);
            if (notifier is not null)
            {
                await notifier.NotifyAsync(NotificationSeverity.Critical,
                    "La flotta non riesce a registrare cosa sta per fare",
                    $"L'orchestratore stava per sostituire l'occupante della corsia {replace.LaneId} e non è riuscito "
                    + $"a scrivere l'intento nel journal ({ex.Message}). Niente è avvenuto.", ct);
            }
            return;
        }

        string? erroreStop = null;
        try
        {
            await engine.StopAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            erroreStop = ex.Message;
        }

        await ChiudiIntentoAssegnazioneAsync(intentoStop, erroreStop, replace.Reason, ct);

        if (erroreStop is not null)
        {
            // La corsia gira ancora: niente è cambiato, e il candidato non è stato consumato.
            logger.LogWarning("Corsia {Lane}: sostituzione interrotta, lo stop non è riuscito ({Errore}).",
                replace.LaneId, erroreStop);
            if (notifier is not null)
            {
                await notifier.NotifyAsync(NotificationSeverity.Warning,
                    $"Flotta: sostituzione sulla corsia {replace.LaneId} non riuscita",
                    $"Lo stop non è riuscito ({erroreStop}). La corsia sta ancora girando sull'ipotesi precedente.", ct);
            }
            return;
        }

        logger.LogWarning("Corsia {Lane} FERMATA per sostituzione: {Reason}", replace.LaneId, replace.Reason);

        // --- Metà 2: schierare. Stesso percorso del clic umano e del braccio grigio. -------------
        await ExecuteAssignAsync(replace.RunId, replace.CandidateKey, replace.LaneId,
            isGrey: true, replace.Reason, assignSource, votesJson, ct);
    }

    /// <summary>
    /// [AF2b] <b>Ferma davvero una corsia.</b> L'unica azione che questo worker esegue.
    ///
    /// <para><b>La modalità si rilegge ADESSO, dal motore.</b> Il piano è stato deciso su una
    /// fotografia che può avere minuti: se nel frattempo la corsia è passata a Testnet o Live —
    /// per una promozione, o per una mano umana — fermarla sarebbe l'orchestratore che tocca una
    /// corsia che non gli appartiene. Fail-closed: modalità non leggibile ⇒ non si tocca. È la
    /// stessa disciplina del lettore di stato, che marca intoccabile ciò che non riesce a leggere,
    /// applicata nel punto dove le conseguenze sono reali.</para>
    ///
    /// <para>Il journal riceve l'esito VERO: <c>Applied=true</c> solo se lo stop è andato a buon
    /// fine, altrimenti la riga porta l'errore. Un journal che dichiara applicato ciò che è fallito
    /// è la classe di difetto «controllo che rassicura» nel posto peggiore — quello dove qualcuno
    /// andrà a cercare cosa è successo.</para>
    ///
    /// <para>[Revisione 2026-09-03] <b>Anche il ritiro scrive l'intento PRIMA di fermare.</b> K51
    /// l'aveva fatto solo per le assegnazioni: qui lo stop precedeva il journal, e un INSERT
    /// fallito (o un processo morto fra i due) lasciava la corsia ferma senza riga — esattamente i
    /// «quattro arresti su quattro senza riga» che avevano motivato K51. Fail-closed come
    /// l'assegnazione: se l'intento non si scrive, non si ferma. I rifiuti per modalità o corsia
    /// già ferma non aprono intenti: non toccano nulla, e si scrivono come <c>Refused</c>.</para>
    /// </summary>
    private async Task ExecuteRetireAsync(StopAndFreeLane retire, CancellationToken ct)
    {
        Trading.ITradingEngine engine;
        string? rifiuto = null;
        try
        {
            engine = serviceProvider.GetRequiredKeyedService<Trading.ITradingEngine>(retire.LaneId);
            var status = await engine.GetStatusAsync(ct);

            if (status.Mode != Trading.TradingMode.Paper)
            {
                rifiuto = $"corsia in {status.Mode}, non Paper: l'orchestratore non ferma corsie che non governa";
            }
            else if (!status.IsRunning)
            {
                rifiuto = "corsia già ferma: niente da fare";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Stato non leggibile: non si tocca (fail-closed), e si dice come è finita.
            await JournalAsync(new OrchestratorDecision
            {
                AtUtc = DateTime.UtcNow, Kind = "Retire", LaneId = retire.LaneId,
                Reason = retire.Reason, DryRun = false, Applied = false, Error = ex.Message,
                Outcome = DecisionOutcome.Failed,
            }, ct);
            logger.LogWarning("Ritiro della corsia {Lane} NON eseguito ({Error}): {Reason}", retire.LaneId, ex.Message, retire.Reason);
            return;
        }

        if (rifiuto is not null)
        {
            await JournalAsync(new OrchestratorDecision
            {
                AtUtc = DateTime.UtcNow, Kind = "Retire", LaneId = retire.LaneId,
                Reason = retire.Reason, DryRun = false, Applied = false, Error = rifiuto,
                Outcome = DecisionOutcome.Refused,
            }, ct);
            logger.LogWarning("Ritiro della corsia {Lane} NON eseguito ({Error}): {Reason}", retire.LaneId, rifiuto, retire.Reason);
            return;
        }

        int intentoId;
        try
        {
            intentoId = await ApriIntentoAsync("Retire", null, retire.LaneId, "rules", "[]", retire.Reason, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Corsia {Lane}: impossibile aprire l'intento di ritiro a journal; NON si ferma.", retire.LaneId);
            if (notifier is not null)
            {
                await notifier.NotifyAsync(NotificationSeverity.Critical,
                    "La flotta non riesce a registrare cosa sta per fare",
                    $"L'orchestratore stava per fermare la corsia {retire.LaneId} e non e' riuscito a scrivere "
                    + $"l'intento nel journal ({ex.Message}). Lo stop NON e' avvenuto: una corsia fermata senza "
                    + "riga e' esattamente il caso del 2026-08-31.", ct);
            }
            return;
        }

        string? error = null;
        try
        {
            await engine.StopAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        await ChiudiIntentoAssegnazioneAsync(intentoId, error, retire.Reason, ct);

        if (error is null)
        {
            logger.LogWarning("Corsia {Lane} FERMATA dall'orchestratore: {Reason}", retire.LaneId, retire.Reason);
            if (notifier is not null)
            {
                await notifier.NotifyAsync(NotificationSeverity.Warning,
                    $"Corsia {retire.LaneId} ritirata dall'orchestratore",
                    $"{retire.Reason} La corsia resta CONFIGURATA: si riavvia con un click da /trading.", ct);
            }
        }
        else
        {
            logger.LogWarning("Ritiro della corsia {Lane} NON eseguito ({Error}): {Reason}", retire.LaneId, error, retire.Reason);
        }
    }

    /// <summary>
    /// L'isteresi dei ritiri: un verdetto va ripetuto per N tick CONSECUTIVI prima di valere.
    /// Le corsie che questo tick NON condanna azzerano la propria serie — uno Sharpe che oscilla
    /// attorno alla soglia non accumula mai la conferma.
    /// </summary>
    /// <summary>
    /// [K42, PRD autonomia-piena — Fase 3, 2026-09-01] Un <b>cambio</b> della serie di conferme:
    /// da quante ne aveva a quante ne ha. <c>A = 0</c> significa <b>assolta</b> — il verdetto non si
    /// è ripetuto e la corsia esce dall'isteresi.
    /// </summary>
    internal readonly record struct RetireStreakChange(int LaneId, int Da, int A, string Reason);

    /// <summary>
    /// [K51] Apre l'intento di assegnazione: la riga esiste PRIMA che la corsia venga toccata, e
    /// porta gia' tutto cio' che e' deciso prima dell'azione (corsia, run, fonte, voti, motivo).
    /// Ciò che non e' ancora noto e' solo l'esito.
    /// </summary>
    private Task<int> ApriIntentoAssegnazioneAsync(
        Guid runId, int laneId, string assignSource, string votesJson, string reason, CancellationToken ct)
        => ApriIntentoAsync("Assign", runId, laneId, assignSource, votesJson, reason, ct);

    /// <summary>[Revisione 2026-09-03] L'intento, per qualunque azione sulla corsia: Assign e Retire.</summary>
    private async Task<int> ApriIntentoAsync(
        string kind, Guid? runId, int laneId, string assignSource, string votesJson, string reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var riga = new OrchestratorDecision
        {
            AtUtc = DateTime.UtcNow,
            Kind = kind,
            LaneId = laneId,
            RunId = runId,
            Source = assignSource == "rules" ? "fleet" : assignSource,
            VotesJson = votesJson,
            Outcome = DecisionOutcome.Intended,
            Applied = false,
            DryRun = false,
            Reason = $"INTENTO. {reason}",
        };
        db.OrchestratorDecisions.Add(riga);
        await db.SaveChangesAsync(ct);
        return riga.Id;
    }

    /// <summary>
    /// [K51] Chiude l'intento con l'esito. Se questa non arriva — perche' il processo e' morto a
    /// meta' — la riga resta <c>Intended</c>, e <b>quella e' l'informazione</b>: la riconciliazione
    /// del tick successivo la marchera' <c>Unknown</c>, mai <c>Applied</c> per somiglianza.
    /// </summary>
    private async Task ChiudiIntentoAssegnazioneAsync(int journalId, string? error, string reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var riga = await db.OrchestratorDecisions.FirstOrDefaultAsync(d => d.Id == journalId, ct);
        if (riga is null) return;
        riga.Outcome = error is null ? DecisionOutcome.Applied : DecisionOutcome.Failed;
        riga.Applied = error is null;
        riga.Error = error;
        riga.Reason = reason;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// [K51] <b>Gli intenti rimasti aperti si dichiarano, non si indovinano.</b>
    ///
    /// <para>Un intento aperto oltre due tick non puo' piu' chiudersi da solo: il processo che
    /// l'aveva aperto non c'e' piu'. Si marca <c>Unknown</c> — <b>mai</b> <c>Applied</c> per
    /// somiglianza con lo stato della corsia, che sarebbe una deduzione presentata come misura, la
    /// trappola gia' pagata piu' volte in questo progetto.</para>
    ///
    /// <para>E' anche la prima superficie in assoluto che rende visibile un crash a meta'
    /// schieramento: prima del 2026-09-02 quello stato non era esprimibile, quindi non esisteva.</para>
    /// </summary>
    /// <summary>Superficie per i test: la riconciliazione e' l'unico pezzo del tick che vive da solo.</summary>
    internal Task RiconciliaPerTestAsync(int tickMinutes, CancellationToken ct = default)
        => RiconciliaIntentiAppesiAsync(tickMinutes, ct);

    private async Task RiconciliaIntentiAppesiAsync(int tickMinutes, CancellationToken ct)
    {
        var limite = DateTime.UtcNow.AddMinutes(-2 * Math.Max(1, tickMinutes));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var appesi = await db.OrchestratorDecisions
            .Where(d => d.Outcome == DecisionOutcome.Intended && d.AtUtc < limite)
            .ToListAsync(ct);
        if (appesi.Count == 0) return;

        foreach (var riga in appesi)
        {
            riga.Outcome = DecisionOutcome.Unknown;
            riga.Error = "esito ignoto: il processo e' terminato fra l'intento e la conferma. "
                       + "Lo stato attuale della corsia NON viene usato per dedurre l'esito: sarebbe una "
                       + "deduzione presentata come misura.";
        }
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Riconciliati {N} intenti rimasti aperti oltre due tick: marcati «esito ignoto».", appesi.Count);
    }

    /// <summary>
    /// L'isteresi del ritiro, e da [K42] anche <b>le sue transizioni</b>.
    ///
    /// <para><b>Perché servono.</b> Fino a oggi il ramo <c>StopAndFreeLane</c> non ancora confermato
    /// «si annotava solo nel log»: una condanna a metà strada esisteva, contava fra le azioni del
    /// piano, e non era leggibile da nessuna parte. Osservato in esercizio il 2026-09-01: il
    /// pannello diceva «azioni ultimo piano: 1» e il journal non aveva una riga da sei ore, anche
    /// dopo un riavvio del guscio che azzera la deduplica dei motivi — un'azione ogni quindici
    /// minuti che non lasciava traccia.</para>
    ///
    /// <para><b>Perché i CAMBI e non lo stato.</b> Journalizzare a ogni tick darebbe 96 righe al
    /// giorno per una condanna che non si muove: è lo stesso rumore che la deduplica dei
    /// <c>Blocked</c> esiste per togliere. E per la stessa ragione la serie <b>si ferma alla
    /// conferma</b> invece di crescere all'infinito: oltre <paramref name="confirmTicks"/> non c'è
    /// più niente di nuovo da dire, e da lì in poi parla il ramo del ritiro vero.</para>
    ///
    /// <para><b>La serie vive in memoria e muore col guscio</b>, ed è una scelta: il costo misurato
    /// di un riavvio è <b>0,2 minuti</b> di ritardo a <c>RetireConfirmTicks = 2</c>, contro un
    /// cancello di dieci giorni. Quello che serviva non era persistere il contatore, era vedere che
    /// esiste — e dopo un riavvio il primo tick scrive una serie nuova, che è la verità.</para>
    /// </summary>
    private (HashSet<int> Confirmed, List<RetireStreakChange> Cambi) ApplyRetireHysteresis(
        FleetPlan plan, int confirmTicks)
    {
        var votati = plan.Actions.OfType<StopAndFreeLane>().ToDictionary(a => a.LaneId, a => a.Reason);

        // [K61, 2026-09-04] La SOSTITUZIONE passa dalla stessa isteresi del ritiro, e non è un
        // dettaglio: la sua metà distruttiva è uno stop — esattamente l'azione che K42 ha voluto
        // veder confermata per due giri prima di eseguirla. Un ramo che fermasse una corsia senza
        // passare di qui avrebbe una guardia in MENO del ritiro, pur facendo la stessa cosa e una
        // in più.
        foreach (var sostituzione in plan.Actions.OfType<ReplaceLaneOccupant>())
        {
            votati.TryAdd(sostituzione.LaneId, sostituzione.Reason);
        }

        var cambi = new List<RetireStreakChange>();

        foreach (var lane in _retireStreak.Keys.Where(l => !votati.ContainsKey(l)).ToList())
        {
            // Assolta: il verdetto non si è ripetuto. Va detto, perché una condanna che sparisce in
            // silenzio è indistinguibile da una condanna che non c'è mai stata.
            var prima = _retireStreak[lane];
            _retireStreak.Remove(lane);
            if (prima > 0)
            {
                cambi.Add(new RetireStreakChange(lane, prima, 0,
                    $"Corsia {lane}: il verdetto di ritiro NON si è ripetuto ({prima}/{confirmTicks} conferme), "
                    + "la serie si azzera e la corsia resta in corsa."));
            }
        }

        var confirmed = new HashSet<int>();
        foreach (var (lane, motivo) in votati)
        {
            var prima = _retireStreak.GetValueOrDefault(lane);
            // Ferma alla conferma: oltre non c'è nulla di nuovo da dire, e continuare a contare
            // produrrebbe una riga ogni quindici minuti finché qualcuno non interviene.
            var dopo = Math.Min(prima + 1, confirmTicks);
            _retireStreak[lane] = dopo;
            if (dopo >= confirmTicks) confirmed.Add(lane);
            if (dopo != prima)
            {
                cambi.Add(new RetireStreakChange(lane, prima, dopo,
                    $"Corsia {lane}: condanna al ritiro, conferma {dopo}/{confirmTicks}"
                    + (dopo >= confirmTicks ? " — CONFERMATA, il ritiro può essere eseguito." : " (in attesa del prossimo tick).")
                    + $" {motivo}"));
            }
        }
        return (confirmed, cambi);
    }

    /// <summary>
    /// Il carry non si GESTISCE da qui (vive nell'host del motore), si SORVEGLIA: se è abilitato
    /// ma non decide da troppo, qualcosa è rotto e va detto. Lo stato vivo è in-process: in
    /// topologia remota da questo host non si vede, e si dichiara il limite invece di fingere.
    /// </summary>
    /// <summary>Dichiarazione una-tantum per processo dell'inapplicabilità del guardiano del carry (J18).</summary>
    private bool _carryWatchInapplicabilityDeclared;

    private async Task WatchCarryAsync(CancellationToken ct)
    {
        var opt = options.CurrentValue;
        if (!carryOptions.CurrentValue.Enabled) return;

        // [K8, 2026-08-31] Prima si guarda il BATTITO PERSISTITO, poi il worker in-process.
        //
        // È l'inversione che rende il guardiano applicabile nella topologia in cui la piattaforma
        // gira davvero: il carry vive nel pod, il guardiano qui, e l'unico canale fra i due è il
        // database. Il worker in-process resta come sorgente per l'assetto monolitico (carry e
        // guardiano nello stesso processo), dove il battito potrebbe non essere ancora arrivato.
        DateTime? battito = null;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            battito = await db.HostHeartbeats.AsNoTracking()
                .Where(h => h.Host == Data.HostHeartbeat.CarryRole)
                .Select(h => (DateTime?)h.LastUtc)
                .FirstOrDefaultAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Non poter leggere il battito NON è «il carry è morto»: è ignoranza, e si tace
            // l'allarme invece di inventarlo (fail-open sulla diagnostica, regola 4).
            logger.LogWarning(ex, "Guardiano del carry: battito non leggibile, salto questo giro.");
            return;
        }

        var carry = serviceProvider.GetService<CarryWorker>();
        if (battito is null && carry is null)
        {
            // [J18, PRD autonomia-operativa 2026-08-25] IL GUARDIANO ERA MUTO PER COSTRUZIONE, e
            // nella topologia in cui la piattaforma GIRA. Con Trading:UseRemoteTrading=true il
            // CarryWorker è registrato solo nel pod del motore: qui GetService restituisce SEMPRE
            // null, e il LogDebug (invisibile ai livelli di default) faceva sì che
            // Fleet:CarrySilenceAlertHours — presente e amministrabile in UI — non potesse
            // scattare MAI. Se il carry nel pod muore, nessuno lo apprende: controllo che
            // rassicura, in forma pura. Peggio: il Carry:Enabled letto qui è quello del GUSCIO,
            // che non comanda il carry (il pod legge la SUA configurazione).
            //
            // Da qui non si può sorvegliare senza dati che il motore non persiste (lo stato del
            // carry vive nei log del pod, ritenzione ~10h): il rimedio VERO è un heartbeat del
            // carry scritto dal motore — lavoro pod-side, in passi operativi del PRD. Intanto:
            // l'inapplicabilità si DICHIARA, una volta per processo, a un livello che si vede.
            if (!_carryWatchInapplicabilityDeclared)
            {
                _carryWatchInapplicabilityDeclared = true;
                logger.LogWarning(
                    "Guardiano del carry NON ANCORA APPLICABILE: il worker vive nell'host del motore "
                    + "(Trading:UseRemoteTrading) e non ha ancora scritto un battito su HostHeartbeats['{Ruolo}']. "
                    + "Fleet:CarrySilenceAlertHours={Ore}h non può scattare finché quella riga non compare — "
                    + "il motore in esecuzione è precedente a K8, oppure il carry non ha mai valutato.",
                    Data.HostHeartbeat.CarryRole, opt.CarrySilenceAlertHours);
            }
            return;
        }

        var silence = TimeSpan.FromHours(Math.Max(1, opt.CarrySilenceAlertHours));
        // Il battito persistito VINCE sul testimone in-process: nell'assetto remoto è l'unico che
        // parli del processo che decide davvero.
        var last = battito ?? carry?.LastEvaluationUtc;
        var mute = last is null || DateTime.UtcNow - last > silence;
        var alreadyAlertedRecently = _lastCarryAlertUtc is DateTime prev && DateTime.UtcNow - prev < silence;

        if (mute && !alreadyAlertedRecently)
        {
            _lastCarryAlertUtc = DateTime.UtcNow;
            logger.LogWarning("Carry abilitato ma muto: ultima decisione {Last}.", last?.ToString("u") ?? "mai");
            if (notifier is not null)
            {
                await notifier.NotifyAsync(NotificationSeverity.Warning, "Carry abilitato ma muto",
                    $"Il worker del carry non decide da oltre {opt.CarrySilenceAlertHours}h (ultima valutazione: {last?.ToString("u") ?? "mai"}). " +
                    "Controlla /admin/autonomy (sezione carry) e i dati di funding.", ct);
            }
        }
    }

    private async Task JournalAsync(OrchestratorDecision decision, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.OrchestratorDecisions.Add(decision);
        await db.SaveChangesAsync(ct);
    }
}
