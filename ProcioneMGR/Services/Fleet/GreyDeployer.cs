using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// Un candidato grigio schierabile, come lo mostra il form.
///
/// <para>[Difetto C, 2026-08-22] <b><see cref="CandidateKey"/> è l'unica identità.</b> La terna
/// (strategia, simbolo, timeframe) NON è una chiave, e il form la usava come valore dell'option:
/// vedi <see cref="GreyDeployer.ResolveGrey"/> per la misura.</para>
///
/// <para>Deliberatamente <b>NON posizionale</b>: quattro <c>string</c> adiacenti in testa a un
/// record posizionale si scambiano senza che il compilatore fiati, e un default <c>= ""</c>
/// renderebbe legale anche l'omissione. Con <c>required init</c> entrambi sono errori di
/// compilazione.</para>
/// </summary>
public sealed record GreyChoice
{
    /// <summary>L'identità canonica (<see cref="PipelineCandidateKey"/>): è questa che torna indietro a <see cref="IGreyDeployer.DeployAsync"/>.</summary>
    public required string CandidateKey { get; init; }
    public required string StrategyName { get; init; }
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public required decimal HoldoutSharpe { get; init; }
    public required int HoldoutTrades { get; init; }
    public string? RejectReason { get; init; }

    /// <summary>
    /// [K57, 2026-09-02] La stabilità di QUESTA ipotesi fra le sue rimisurazioni. <c>null</c> =
    /// meno di cinque misure in archivio, cioè «non lo so» — che non è «va bene».
    ///
    /// <para>Il numero qui sopra (<see cref="HoldoutSharpe"/>) è quello del run che si sta
    /// guardando. Le finestre scorrono, quindi la stessa identica ipotesi viene rivalutata a ogni
    /// giro: misurato, 13-16 volte con un ventaglio mediano di 0,4-0,75 — contro un cancello posto
    /// a 0,5. Ordinare per il valore di un singolo run significa proporre, per costruzione, la
    /// notte in cui il ventaglio era al massimo.</para>
    /// </summary>
    public Research.StabilitaIpotesi? Stabilita { get; init; }
}

/// <summary>Esito dello schieramento, scritto per un umano.</summary>
public sealed record GreyDeployResult(bool Success, string Message);

/// <summary>
/// [F5] IL CLICK UMANO della fascia grigia: prende un candidato grigio da un run (identità +
/// parametri ESATTI validati), gli monta il bracket SL/TP data-driven (stesso <see cref="AutoBracket"/>
/// dell'applica) e lo scrive su una corsia di FLOTTA libera, avviandola in Paper se richiesto.
///
/// Confini (gli stessi della Queen Bee, qui applicati a una azione UMANA):
/// - solo corsie oltre l'impronta auto-apply, mai quarantene, mai corsie che girano;
/// - solo Paper: la modalità non è nemmeno un parametro;
/// - solo candidati che passano il filtro grigio del lettore (Sharpe holdout positivo, bocciati
///   per sola finestra corta) — questo servizio non è una porta di servizio per schierare
///   qualunque cosa, è il braccio della proposta F5.
/// Ogni schieramento finisce nel journal della flotta con Source="human".
/// </summary>
public interface IGreyDeployer
{
    Task<IReadOnlyList<GreyChoice>> ListGreyAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// [Difetto C, 2026-08-22] Si schiera per <paramref name="candidateKey"/>
    /// (<see cref="PipelineCandidateKey"/>), <b>mai per terna</b>. Simbolo e timeframe si LEGGONO
    /// dal candidato risolto: non sono più parametri, così non possono divergere da lui.
    /// </summary>
    /// <param name="source">
    /// [J14] Chi sta schierando: "human" (il click F5) o "fleet" (il braccio dell'orchestratore).
    /// Finisce nel journal: la provenienza di uno schieramento non si deve dedurre dal testo.
    /// </param>
    /// <param name="allowSurvivor">
    /// [J13] Ammette anche i candidati SOPRAVVISSUTI del run (per il braccio di assegnazione della
    /// banda «pass» a gamba singola). Default false: il click umano F5 resta il braccio della
    /// PROPOSTA grigia, non una porta di servizio.
    /// </param>
    /// <param name="journalId">
    /// [K51, 2026-09-02] L'id della riga di INTENTO gia' aperta dal chiamante, oppure <c>null</c>
    /// se deve aprirla (e chiuderla) questo servizio.
    ///
    /// <para>Ha sostituito un <c>bool journal</c>, e la differenza non e' cosmetica. Col booleano,
    /// «una riga per azione» era un <b>accordo fra due file</b>: il worker passava <c>false</c> e si
    /// impegnava a scriverla lui. Con l'handle e' una <b>conseguenza della forma</b> — chi apre
    /// l'intento e' anche chi lo chiude, e non esiste un cammino in cui nessuno dei due lo faccia.</para>
    /// </param>
    Task<GreyDeployResult> DeployAsync(
        Guid runId, string candidateKey,
        int laneId, bool startPaper, CancellationToken ct = default,
        string source = "human", bool allowSurvivor = false, int? journalId = null);
}

public sealed class GreyDeployer(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IServiceProvider serviceProvider,
    IPipelineApplier applier,
    ILaneQuarantineStore quarantineStore,
    ExcursionAnalyzer excursion,
    ILaneDirectory laneDirectory,
    IOptionsMonitor<FleetOptions> options,
    ILogger<GreyDeployer> logger,
    // [K57] La stabilità fra rimisurazioni. Opzionale come gli altri collaboratori: assente ⇒
    // l'ordinamento resta quello storico, per Sharpe del singolo run.
    Research.IStabilitaReader? stabilita = null) : IGreyDeployer
{
    public async Task<IReadOnlyList<GreyChoice>> ListGreyAsync(Guid runId, CancellationToken ct = default)
    {
        var grey = await LoadGreyCandidatesAsync(runId, ct);

        // [K57, 2026-09-02] SI ORDINA PER LA MEDIANA DELLE RIMISURAZIONI, non per il valore del
        // singolo run — e le instabili scendono in fondo invece di sparire.
        //
        // Misurato sulle 324 chiavi giudicabili del motore corrente: 111 passano la soglia di 0,5
        // col MASSIMO delle loro misure, 87 con la MEDIANA. Le 24 di differenza — il 22% — passano
        // solo perché è esistita una notte fortunata. Ordinare per il singolo run le mette in
        // cima, ed è così che la corsia 6 ha ricevuto un'aspettativa di 1,875 contro una mediana
        // di 0,479.
        //
        // Non si CANCELLANO: è una lista che un umano legge e da cui sceglie. Toglierle
        // nasconderebbe che esistono; metterle in fondo, con il loro racconto accanto, lascia la
        // decisione a chi guarda. Chi non ha abbastanza misure resta dov'era: l'ignoranza non
        // retrocede nessuno.
        var stab = stabilita is null
            ? new Dictionary<string, Research.StabilitaIpotesi>(StringComparer.Ordinal)
            : await stabilita.ReadAsync([.. grey.Select(c => c.Key)], ct);

        return grey
            .OrderBy(c => stab.TryGetValue(c.Key, out var st) && st.Instabile)
            .ThenByDescending(c => stab.TryGetValue(c.Key, out var st) ? st.Mediana : c.HoldoutSharpe)
            // [Difetto C] La chiave viaggia FINO al form: è il valore che tornerà indietro in
            // DeployAsync. Senza, l'ordinamento per Sharpe di questa riga e l'ordine dell'artifact
            // letto in DeployAsync producevano due liste diverse sulla stessa terna.
            .Select(c => new GreyChoice
            {
                CandidateKey = c.Key,
                StrategyName = c.StrategyName,
                Symbol = c.Symbol,
                Timeframe = c.Timeframe,
                HoldoutSharpe = c.HoldoutSharpe,
                HoldoutTrades = c.HoldoutTrades,
                RejectReason = c.RejectReason,
                Stabilita = stab.TryGetValue(c.Key, out var s) ? s : null,
            })
            .ToList();
    }

    public async Task<GreyDeployResult> DeployAsync(
        Guid runId, string candidateKey,
        int laneId, bool startPaper, CancellationToken ct = default,
        string source = "human", bool allowSurvivor = false, int? journalId = null)
    {
        // --- La corsia: di flotta, libera, senza vincoli. Si RILEGGE lo stato adesso, non ci si
        // fida della lista mostrata al momento del render (l'operatore può aver cliccato tardi).
        if (laneId < applier.LaneCount || laneId >= TradingLanes.Count)
        {
            return new(false, $"La corsia {laneId} non è una corsia di flotta (valide: {applier.LaneCount}..{TradingLanes.Count - 1} — le prime {applier.LaneCount} sono l'impronta dell'auto-apply).");
        }
        if (await quarantineStore.GetAsync(laneId, ct) is not null)
        {
            return new(false, $"La corsia {laneId} è in QUARANTENA: va prima esaminata e liberata da /trading.");
        }
        var engine = serviceProvider.GetRequiredKeyedService<ITradingEngine>(laneId);
        TradingEngineStatus status;
        try { status = await engine.GetStatusAsync(ct); }
        catch (Exception ex)
        {
            return new(false, $"Stato della corsia {laneId} non leggibile ({ex.Message}): non si schiera su una corsia di cui non si sa nulla.");
        }
        if (status.IsRunning)
        {
            return new(false, $"La corsia {laneId} sta GIRANDO ({status.Symbol}): fermala prima, o scegline una libera.");
        }

        // --- Il candidato: deve esistere nel run ED essere grigio per il filtro del lettore.
        // Risolto per IDENTITÀ, non per terna, e fail-closed su entrambi i lati. Vedi ResolveGrey.
        var (candidate, resolveError) = ResolveGrey(await LoadDeployableCandidatesAsync(runId, allowSurvivor, ct), candidateKey);
        if (candidate is null)
        {
            return new(false, resolveError!);
        }
        // Simbolo e timeframe vengono dal candidato risolto: unica fonte di verità.
        var symbol = candidate.Symbol;
        var timeframe = candidate.Timeframe;

        // --- [K33, 2026-09-01] LA STESSA IPOTESI NON OCCUPA DUE CORSIE.
        //
        // Il 31/08 GridMeanReversion DOGE/USDT 15m con parametri identici e ExpectedSharpe uguale a
        // ventotto cifre è finita sulle corsie 4 E 6: 20.000 USDT nominali su una stima da 14 trade,
        // due slot del tetto grigio, e una flotta che sembrava larga cinque ipotesi mentre ne
        // portava quattro. Nessun controllo, in nessuno degli otto scrittori di corsia, confrontava
        // una corsia con le altre — l'unico che esisteva (EnsemblePageService.AddFromGreyAsync)
        // guarda DENTRO la stessa corsia.
        //
        // La guardia sta QUI e non nel chiamante perché questa è la strozzatura di entrambe le porte
        // che schierano un grigio: il click F5 e il braccio della flotta. Il giudizio è puro
        // (HypothesisGuard) e collaudabile senza il circuito.
        var duplicato = HypothesisGuard.Check(
            await laneDirectory.ListAsync(ct), laneId, candidate.Key, options.CurrentValue.BlockDuplicateTriple);
        if (duplicato.Blocked)
        {
            return new(false, duplicato.Reason!);
        }

        // --- Il bracket: stesso calcolo dell'applica. Senza protezioni derivabili non si parte.
        var (sl, tp) = await AutoBracket.ComputeAsync(dbFactory, excursion, symbol, timeframe, ct);
        if (sl <= 0m && tp <= 0m)
        {
            return new(false, $"Bracket SL/TP non derivabile per {symbol} {timeframe} (dati insufficienti): un forward test senza protezioni non si schiera da un click.");
        }

        // [I11] La frequenza ATTESA, derivata dalla stessa finestra di holdout che il lettore della
        // flotta usa per candidare, e scritta SULLA GAMBA: fino a oggi questo numero moriva al
        // momento dello schieramento, e da lì in poi nessuno sapeva più quanti trade quella gamba
        // dovesse fare. È il denominatore che il ritiro per inedia leggerà.
        var (attesiAlMese, fonteAttesi) = await HoldoutWindow.ForCandidateAsync(dbFactory, runId, candidate.HoldoutTrades, ct);

        // --- Scrittura della configurazione (solo configurazione: l'avvio è il passo dopo).
        var manager = serviceProvider.GetRequiredKeyedService<IEnsembleManager>(laneId);
        var cfg = await manager.GetConfigurationAsync(ct);
        cfg.Symbol = symbol;
        cfg.Timeframe = timeframe;
        cfg.Strategies =
        [
            new EnsembleStrategy
            {
                StrategyName = candidate.StrategyName,
                DisplayName = $"{candidate.StrategyName} (fascia grigia, run {runId.ToString()[..8]})",
                Parameters = new(candidate.Parameters),
                CurrentAllocation = 100m,
                IsActive = true,
                StopLossPercent = sl > 0m ? sl : null,
                TakeProfitPercent = tp > 0m ? tp : null,
                ExpectedSharpe = candidate.HoldoutSharpe != 0m ? candidate.HoldoutSharpe : null,
                ExpectedSharpeAtUtc = DateTime.UtcNow,   // [RF0] convenzione del numero, vedi MetricsConvention
                ExpectedProfitFactor = candidate.HoldoutProfitFactor != 0m ? candidate.HoldoutProfitFactor : null,
                ExpectedMaxDrawdown = candidate.HoldoutMaxDrawdown != 0m ? candidate.HoldoutMaxDrawdown : null,
                // [T1] stessa etichetta della pipeline: il badge non dipende dal percorso di
                // schieramento. [J13] E la provenienza VERA del candidato: un sopravvissuto
                // schierato dal braccio non deve portare l'etichetta grigia, né viceversa.
                SourceVerdict = candidate.Survived ? "Survived" : "Grey",
                // [I11] Il denominatore, e la sua provenienza in chiaro: una derivazione dichiarata,
                // non una misura. null = non derivabile, e in quel caso nessun consumatore agisce.
                ExpectedTradesPerMonth = attesiAlMese,
                ExpectedTradesSource = fonteAttesi,
            },
        ];
        // --- [K51, 2026-09-02] L'INTENTO SI SCRIVE PRIMA. E se non si scrive, la corsia non si tocca.
        //
        // Riscrivere la configurazione di una corsia non e' ricostruibile guardandola dopo:
        // EnsembleStates tiene un solo ConfigurationJson e la versione precedente non esiste piu'.
        // Un'azione irreversibile e non ricostruibile senza il suo registro e' esattamente quella a
        // cui si applica il fail-closed della regola 4 — e il costo del verso opposto e' misurato:
        // sono le corsie 4 e 6 del 2026-08-31, di cui K37 ha dovuto dichiarare la provenienza NON
        // ACCERTABILE, su un campo che governa il tetto grigio.
        //
        // Il rifiuto e' RUMOROSO: sostituire un buco silenzioso con un altro non sarebbe un
        // progresso.
        var mioIntento = journalId;
        if (mioIntento is null)
        {
            try
            {
                mioIntento = await ApriIntentoAsync(runId, laneId, source, candidate.Key, sl, tp, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Corsia {Lane}: impossibile scrivere l'intento nel journal; lo schieramento NON avviene.", laneId);
                return new(false,
                    $"Non riesco a registrare l'intento nel journal ({ex.Message}): la corsia {laneId} NON e' stata "
                    + "toccata. Riscrivere una corsia senza poterlo registrare significa perdere per sempre la "
                    + "configurazione precedente e la provenienza di quella nuova — e' successo il 2026-08-31.");
            }
        }

        await manager.UpdateConfigurationAsync(cfg, ConfigWriteContext.Create(
            ConfigWriteSources.GreyDeployer,
            $"schieramento {(source == "fleet" ? "automatico della flotta" : "da click F5")} del candidato {candidate.Key} (run {runId.ToString()[..8]})"), ct);

        var startedText = "configurata, DA AVVIARE da /trading";
        string? error = null;
        if (startPaper)
        {
            try
            {
                await engine.StartAsync(TradingMode.Paper, ct);
                startedText = "avviata in Paper";
            }
            catch (Exception ex)
            {
                error = ex.Message;
                startedText = $"configurata ma NON avviata ({ex.Message})";
            }
        }

        // [K51] L'intento si CHIUDE con l'esito. Se l'ha aperto il chiamante (il worker, che vi
        // aggiunge i voti del comitato), e' lui a chiuderlo: una riga per azione, per costruzione.
        if (journalId is null && mioIntento is int idIntento)
        {
            await ChiudiIntentoAsync(idIntento, error, startedText, candidate, sl, tp, source, duplicato.Reason, ct);
        }

        logger.LogInformation("Candidato grigio schierato: {Candidato} → corsia {Lane} ({Stato}).",
            candidate.Key, laneId, startedText);
        return new(error is null, $"{candidate.Key} → corsia {laneId}: {startedText}. SL {sl:F2}% / TP {tp:F2}% (bracket automatico).");
    }

    /// <summary>
    /// [K51] Apre la riga d'intento PRIMA di toccare la corsia. Se questa fallisce, il chiamante
    /// rinuncia: e' il fail-closed della regola 4 applicato all'azione meno reversibile che c'e'.
    /// </summary>
    private async Task<int> ApriIntentoAsync(
        Guid runId, int laneId, string source, string candidateKey, decimal sl, decimal tp, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var riga = new OrchestratorDecision
        {
            AtUtc = DateTime.UtcNow,
            Kind = "Assign",
            LaneId = laneId,
            RunId = runId,
            Source = source,
            Outcome = DecisionOutcome.Intended,
            Applied = false,
            DryRun = false,
            Reason = $"[{(source == "fleet" ? "J14, flotta" : "F5, click umano")}] INTENTO: {candidateKey} -> corsia {laneId} "
                   + $"in Paper, SL {sl:F2}% / TP {tp:F2}%. L'esito arriva alla chiusura di questa riga.",
        };
        db.OrchestratorDecisions.Add(riga);
        await db.SaveChangesAsync(ct);
        return riga.Id;
    }

    /// <summary>
    /// [K51] Chiude l'intento con l'esito. Una riga che resta <c>Intended</c> non e' un difetto del
    /// journal: e' l'informazione che il processo e' morto a meta' schieramento, e prima di oggi
    /// quello stato non era esprimibile — quindi era invisibile.
    /// </summary>
    private async Task ChiudiIntentoAsync(
        int journalId, string? error, string startedText, ValidatedCandidate candidate,
        decimal sl, decimal tp, string source, string? avvisoGuardia, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var riga = await db.OrchestratorDecisions.FirstOrDefaultAsync(d => d.Id == journalId, ct);
        if (riga is null) return;
        riga.Outcome = error is null ? DecisionOutcome.Applied : DecisionOutcome.Failed;
        riga.Applied = error is null;
        riga.Error = error;
        riga.Reason = $"[{(source == "fleet" ? "J14, flotta" : "F5, click umano")}] {candidate.Key} -> corsia {riga.LaneId}, {startedText}. "
                    + $"Sharpe holdout {candidate.HoldoutSharpe:F2} su {candidate.HoldoutTrades} trade; SL {sl:F2}% / TP {tp:F2}%."
                    + (avvisoGuardia is not null ? $" [!] {avvisoGuardia}" : string.Empty);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// [Difetto C, 2026-08-22] Il candidato si risolve per IDENTITÀ
    /// (<see cref="PipelineCandidateKey"/>), mai per la terna.
    ///
    /// <para><b>La terna non è una chiave.</b> <c>CreativeDiscoveryStage</c> conferma più specifiche
    /// distinte della stessa meta-strategia sulla stessa serie — è esattamente il motivo per cui la
    /// chiave canonica esiste. Misurato sugli artifact il 2026-08-22: su 1.414 righe grigie in 146
    /// run ci sono <b>12 terne ambigue distinte</b>, che ricompaiono in 119 run-istanze perché la
    /// caccia notturna ritrova ogni notte la stessa griglia (le istanze non sono problemi: è lo
    /// stesso errore di conteggio già rettificato su <c>GreyZone.DsrFloor</c>).</para>
    ///
    /// <para>Il danno operativo è concentrato: dei 10 run raggiungibili dal menù, <b>2 hanno la
    /// terna ambigua sulla riga PRESELEZIONATA</b> e schieravano la gamba peggiore senza alcun
    /// errore dell'operatore — su <c>b49a4c8c</c> il journal propone <c>Composite XLM/USDT 4h</c>
    /// Sharpe 1,29 su 8 trade, e il codice avrebbe schierato l'altra specifica della stessa terna,
    /// Sharpe 0,53 su 3 trade. <b>Nessuno schieramento sbagliato è mai avvenuto</b> (dei 6 click
    /// umani, il solo su terna ambigua aveva le due gambe con Sharpe identico): il valore di questa
    /// correzione è prospettico.</para>
    ///
    /// <para><b>Fail-closed su entrambi i lati.</b> Zero corrispondenze o più di una fanno
    /// RIFIUTARE. Il ramo «più di una» è <i>irraggiungibile per costruzione</i> con i generatori
    /// attuali — la chiave degenera nella terna solo con zero parametri, e nessuno dei produttori
    /// di <c>DiscoveryCandidate</c> ne emette senza (verificato: 0 righe su 14.492) — ma resta come
    /// guardia di contratto: se l'identità non discrimina, non si tira a sorte su un'azione che
    /// scrive su una corsia.</para>
    ///
    /// <para><b>Artifact vecchi.</b> Non esiste il caso «payload senza l'identificatore nuovo»:
    /// <see cref="ValidatedCandidate.Key"/> è calcolata dai <c>Parameters</c> ed è RICOSTRUITA alla
    /// deserializzazione, mai letta dal JSON. E <c>ListGreyAsync</c> e <c>DeployAsync</c>
    /// deserializzano lo STESSO payload, quindi le due liste hanno per costruzione le stesse chiavi.</para>
    ///
    /// Statica e pura per essere collaudabile senza il circuito.
    /// </summary>
    internal static (ValidatedCandidate? Candidate, string? Error) ResolveGrey(
        IReadOnlyList<ValidatedCandidate> grey, string candidateKey)
    {
        if (string.IsNullOrWhiteSpace(candidateKey))
        {
            return (null, "Nessun candidato selezionato: scegli una gamba dal menù.");
        }

        var matches = grey.Where(c => string.Equals(c.Key, candidateKey, StringComparison.Ordinal)).ToList();
        return matches.Count switch
        {
            1 => (matches[0], null),
            0 => (null, $"Candidato «{candidateKey}» non trovato fra i GRIGI di quel run: la lista può essere "
                        + "invecchiata (ricarica la pagina). Questo pulsante schiera solo le proposte della fascia grigia."),
            _ => (null, $"Identità AMBIGUA: «{candidateKey}» corrisponde a {matches.Count} candidati grigi di quel run — "
                        + "stessa terna e stessa impronta dei parametri. Non si schiera a caso: segnala l'artifact del run."),
        };
    }

    /// <summary>I candidati GRIGI del run, con lo STESSO filtro del lettore della flotta (nessuna doppia verità).</summary>
    private Task<List<ValidatedCandidate>> LoadGreyCandidatesAsync(Guid runId, CancellationToken ct)
        => LoadDeployableCandidatesAsync(runId, allowSurvivor: false, ct);

    /// <summary>
    /// [J13] I candidati schierabili del run: i grigi (filtro condiviso <see cref="FleetStateReader.IsGrey"/>)
    /// più, se richiesto, i SOPRAVVISSUTI — il braccio della banda «pass» schiera candidati che
    /// hanno superato la validazione piena, e rifiutarli qui sarebbe un filtro che contraddice il
    /// verdetto della pipeline.
    /// </summary>
    private async Task<List<ValidatedCandidate>> LoadDeployableCandidatesAsync(Guid runId, bool allowSurvivor, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var payload = await db.PipelineArtifacts.AsNoTracking()
            .Where(a => a.RunId == runId && a.Kind == "ValidatedCandidates")
            // [Difetto C, 2026-08-22] Un First su un filtro che NON è una chiave: non esiste indice
            // unico su (RunId, Kind), e in questa tabella i duplicati per quella coppia esistono
            // già per altri Kind (AutoResumeAttempt su 7 run, LlmAdvisory su 2). Per
            // ValidatedCandidates oggi il rapporto è 169 artifact / 169 run, quindi è latente —
            // ma senza ordinamento il menù e la risoluzione potrebbero essere serviti da due
            // payload diversi, che è di nuovo il difetto C sotto un'altra forma.
            .OrderBy(a => a.Id)
            .Select(a => a.PayloadJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(payload)) return [];

        List<ValidatedCandidate> validated;
        try { validated = JsonSerializer.Deserialize<List<ValidatedCandidate>>(payload) ?? []; }
        catch (JsonException) { return []; }

        return validated.Where(c => FleetStateReader.IsGrey(c) || (allowSurvivor && c.Survived)).ToList();
    }
}
