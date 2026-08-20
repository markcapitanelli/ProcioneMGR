using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Llm.Committee;
using ProcioneMGR.Services.Monitoring.Drift;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Health;

/// <summary>
/// Stato di attivazione di un agente autonomo. I quattro casi sono DIVERSI e vanno detti in modo
/// diverso — collassarli è precisamente il difetto che questa sonda esiste per chiudere.
/// </summary>
public enum AgentActivation
{
    /// <summary>Il gate è spento: non c'è nulla da dire.</summary>
    Spento,

    /// <summary>Acceso, ma senza soggetti su cui agire. È lo stato più insidioso: sembra acceso e non lo è nei fatti.</summary>
    AccesoInerte,

    /// <summary>Acceso e con soggetti: può agire da solo, senza che nessuno prema nulla.</summary>
    AccesoOperante,

    /// <summary>Non determinabile (una fonte non ha risposto). NON è «spento»: è ignoranza, e va detta.</summary>
    NonDeterminabile,
}

/// <summary>Verdetto su un singolo agente.</summary>
public sealed record AgentState(string Name, AgentActivation Activation, string Detail);

/// <summary>Fotografia dello stato degli agenti autonomi del guscio.</summary>
public sealed record AgentStateReport(IReadOnlyList<AgentState> Agents, DateTime CheckedAtUtc)
{
    /// <summary>Almeno un agente può agire da solo.</summary>
    public bool AnyOperating => Agents.Any(a => a.Activation == AgentActivation.AccesoOperante);

    /// <summary>Almeno un agente ha il gate acceso (operante o inerte che sia).</summary>
    public bool AnyOn => Agents.Any(a => a.Activation is AgentActivation.AccesoOperante or AgentActivation.AccesoInerte);

    /// <summary>Riga unica per il log: leggibile senza aprire nulla.</summary>
    public string Summary => Agents.Count == 0
        ? "nessun agente censito"
        : string.Join(" · ", Agents.Select(a => $"{a.Name}: {Describe(a.Activation)} — {a.Detail}"));

    private static string Describe(AgentActivation a) => a switch
    {
        AgentActivation.Spento => "SPENTO",
        AgentActivation.AccesoInerte => "ACCESO MA INERTE",
        AgentActivation.AccesoOperante => "ACCESO E OPERANTE",
        _ => "NON DETERMINABILE",
    };
}

/// <summary>
/// I fatti da cui si deriva il verdetto, raccolti dal worker e passati alla parte PURA. Sono un
/// record apposta: il giudizio si prova senza database, senza opzioni e senza orologio.
/// </summary>
/// <param name="ChampionCount">
/// [I2] Modelli in stage <c>Champion</c>. È il conteggio da cui dipendono quattro funzioni che i
/// pannelli davano per attive: il ritiro automatico da drift, la union dei fattori del Champion,
/// il dual-read ML e le corsie configurate su <c>MlChampion</c>. <c>null</c> = non determinabile.
/// </param>
/// <param name="CommitteeProvidersWithKey">
/// <c>null</c> = non determinabile (il keyring non ha risposto): non è «zero provider», ed è la
/// differenza che impedisce alla sonda di dichiarare inerte un comitato che magari funziona.
/// </param>
/// <param name="CampaignsWaitingForTrigger">
/// Campagne abilitate in <c>WaitingForTrigger</c>. Tenute separate da <c>Observing</c> perché
/// <c>CampaignPlanner.WakeAsync</c> filtra <c>Status != Observing</c>: una campagna in osservazione
/// non viene MAI risvegliata da un trigger, e dirle entrambe «in attesa di trigger» descriverebbe
/// un percorso di riavvio che per metà di loro non esiste.
/// </param>
/// <param name="RegimeTriggerEnabled">
/// Senza il trigger contestuale nessun wake avviene mai: una campagna in <c>WaitingForTrigger</c>
/// resta ferma per sempre. Il gate va guardato, non presunto acceso.
/// </param>
/// <param name="RegimeTriggerArms">
/// [A5b, 2026-08-20] **Acceso non basta.** Il trigger ha due bracci e ciascuno ha i suoi
/// prerequisiti: il K-means pretende un regime nello snapshot di baseline E un modello di regime
/// attivo per quella serie, la volatilità pretende un forecast positivo. Se cadono entrambi il wake
/// non arriva mai, e fino a oggi questa sonda diceva comunque «un wake la rimette in rotazione da
/// solo» perché guardava il solo flag. Null quando l'armamento non è stato interrogato (nessun
/// rilevatore iniettato): in quel caso si ricade sul vecchio giudizio, dichiarandolo.
/// </param>
/// <param name="FleetExecutionImplemented">
/// Da <see cref="Fleet.FleetOrchestratorWorker.RetirementArmImplemented"/>. <c>Fleet:DryRun</c> dice
/// che cosa è stato CHIESTO; questo dice che cosa il codice sa FARE. Dedurre il secondo dal primo
/// è come la sonda mentiva prima della revisione del 2026-08-18.
/// </param>
/// <param name="FleetAuthorizedLanes">
/// [AF2b] Quante corsie sono in <c>Fleet:ExecutionLanes</c>. Il braccio esiste ma è un permesso per
/// corsia: <b>zero corsie autorizzate = nessuna esecuzione</b>, anche col dry-run spento. Senza
/// questo numero la sonda direbbe «esecuzione attiva» di una macchina che non può toccare nulla —
/// la stessa bugia di prima, spostata di un flag.
/// </param>
/// <param name="CommitteeVotesInWindow">
/// Voti realmente emessi dal comitato nella finestra dichiarata, letti dal journal della flotta.
/// È il FATTO contro cui misurare: un comitato armato e interrogabile che non ha mai votato è
/// acceso e inerte, e sapere che i suoi flag sono a posto non lo rende operante.
/// <c>null</c> = non determinabile.
/// </param>
public sealed record AgentStateFacts(
    bool CampaignEnabled,
    int CampaignsEnabled,
    int CampaignsRotating,
    int CampaignsWaitingForTrigger,
    bool RegimeTriggerEnabled,
    RegimeTriggerHealth? RegimeTriggerArms,
    bool FleetEnabled,
    bool FleetDryRun,
    bool FleetExecutionImplemented,
    int FleetAuthorizedLanes,
    bool FleetUseCommittee,
    int FleetGovernedLanes,
    bool CommitteeEnabled,
    int CommitteeProviders,
    int? CommitteeProvidersWithKey,
    int CommitteeMinValidVotes,
    int? CommitteeVotesInWindow,
    int CommitteeWindowDays,
    bool DriftEnabled,
    bool DriftRetireChampionOnAlert,
    int SavedModelCount,
    int? ChampionCount);

/// <summary>
/// [I1] <b>La sonda che dice quali agenti autonomi sono vivi.</b>
///
/// Nasce da un errore concreto (2026-08-18): si è pianificata un'ondata di lavoro sulla premessa
/// che sei sottosistemi fossero «spenti per configurazione», e <b>tre erano accesi da settimane</b>.
/// Il <c>false</c> che tutti ricordavano vive in <c>appsettings.json.example</c>, che è un
/// documento; il file che il processo carica davvero diceva un'altra cosa. Nessuna superficie
/// diceva quale delle due fosse lo stato.
///
/// <para><b>Perché quattro stati e non due.</b> «Acceso» e «operante» non sono la stessa cosa, e la
/// differenza è tutto: il comitato AI era acceso da sedici giorni e non aveva mai emesso un voto —
/// non per il dry-run, che sul comitato non ha alcun effetto (viene interrogato PRIMA di quel ramo
/// del worker), ma perché arbitra i <i>pareggi</i> di <c>Decide</c> e la coda dell'orchestratore è
/// sempre vuota: senza almeno due candidati idonei e una corsia libera non nasce alcun menù, quindi
/// non c'è alcuna domanda. Un pannello che mostra solo la spunta è vero e inutile — ed è il motivo
/// per cui il verdetto sul comitato si misura sui voti realmente emessi, non sui suoi flag.
/// E «non determinabile» resta separato da
/// «spento» per la stessa ragione per cui lo tiene separato
/// <see cref="Trading.LaneCountCoherenceProbe"/>: un verdetto costruito sull'ignoranza è peggio di
/// nessun verdetto.</para>
///
/// <para><b>Perché non notifica.</b> Deliberato, e diverso da <see cref="Security.MasterKeyProbe"/>.
/// Lo stato degli agenti non cambia da solo: non è un <i>evento</i>, è una <i>condizione</i>. Le
/// condizioni si mostrano (log all'avvio + card in <c>/admin/autonomy</c>), gli eventi si
/// notificano — ed è la stessa distinzione che <c>SeriesFreshnessWatchWorker</c> codifica
/// notificando la TRANSIZIONE e non lo stato. Notificare a ogni avvio del guscio, che riparte a
/// ogni logon e sotto watchdog, brucerebbe il budget condiviso di
/// <c>Notifications:MaxPerHour</c> (20, item I4) che serve agli allarmi veri — il difetto già
/// pagato con la staleness su STX.</para>
///
/// Vive SOLO nel guscio: tutti e quattro gli agenti sono suoi (il motore non registra né la
/// sezione né il worker di nessuno dei quattro).
/// </summary>
public sealed class AgentStateProbe(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptionsMonitor<CampaignOptions> campaign,
    IOptionsMonitor<FleetOptions> fleet,
    IOptionsMonitor<CommitteeOptions> committee,
    IOptionsMonitor<DriftMonitorOptions> drift,
    IOptionsMonitor<RegimeTriggerOptions> regimeTrigger,
    IPipelineApplier applier,
    ILogger<AgentStateProbe> logger,
    IAiKeyStore? keyStore = null,
    IRegimeChangeDetector? regimeChangeDetector = null)
{
    /// <summary>
    /// Finestra su cui si contano i voti del comitato. Dichiarata e non configurabile: è un
    /// riferimento di lettura, e renderla una manopola permetterebbe di allargarla finché il
    /// verdetto diventa quello desiderato.
    /// </summary>
    public const int CommitteeVoteWindowDays = 14;

    /// <summary>Ultimo esito, per la card di <c>/admin/autonomy</c> (stesso patto di MasterKeyProbe.Result).</summary>
    public AgentStateReport? Result { get; private set; }

    /// <summary>
    /// Raccoglie i fatti e deriva il verdetto.
    /// </summary>
    /// <param name="log">
    /// <c>false</c> quando a chiedere è la UI: la card si riapre di continuo e riscrivere la riga
    /// a ogni apertura la trasformerebbe da segnale in rumore — lo stesso motivo per cui la sonda
    /// non notifica affatto. Il worker d'avvio è l'unico che scrive.
    /// </param>
    public async Task<AgentStateReport> ProbeAsync(bool log = true, CancellationToken ct = default)
    {
        var facts = await GatherAsync(ct);
        var report = Describe(facts, DateTime.UtcNow);
        Result = report;

        if (!log)
        {
            return report;
        }

        if (report.AnyOperating)
        {
            // Warning e non Information: è l'informazione che, mancando, ha prodotto un piano di
            // lavoro sbagliato. Deve saltare all'occhio in un log che scorre.
            logger.LogWarning("AGENTI AUTONOMI ATTIVI in questo processo — {Summary}", report.Summary);
        }
        else if (report.AnyOn)
        {
            logger.LogInformation("Agenti autonomi: nessuno può agire da solo. {Summary}", report.Summary);
        }
        else
        {
            logger.LogInformation("Agenti autonomi: tutti spenti. {Summary}", report.Summary);
        }

        return report;
    }

    /// <summary>
    /// La parte PURA: dai fatti al verdetto, senza database, opzioni né orologio. Ogni riga qui
    /// dentro è una regola che un test può puntare contro il caso che la motiva.
    /// </summary>
    public static AgentStateReport Describe(AgentStateFacts f, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(f);
        return new AgentStateReport([DescribeCampaign(f), DescribeFleet(f), DescribeCommittee(f), DescribeDrift(f)], nowUtc);
    }

    private static AgentState DescribeCampaign(AgentStateFacts f)
    {
        const string name = "Campaign Planner";
        if (!f.CampaignEnabled)
        {
            return new AgentState(name, AgentActivation.Spento,
                "Campaign:Enabled spento: nessun run parte da solo, qualunque sia lo stato delle campagne");
        }
        if (f.CampaignsEnabled == 0)
        {
            return new AgentState(name, AgentActivation.AccesoInerte,
                "Campaign:Enabled ACCESO ma nessuna campagna abilitata: il doppio gate non è soddisfatto e la rotazione non ha soggetti");
        }
        if (f.CampaignsRotating > 0)
        {
            return new AgentState(name, AgentActivation.AccesoOperante,
                $"{f.CampaignsEnabled} campagne abilitate, {f.CampaignsRotating} in rotazione: nuovi run partono da soli");
        }

        // Le due strade per ripartire NON sono la stessa cosa, e collassarle descriveva un percorso
        // che per metà delle campagne non esiste (WakeAsync filtra Status != Observing).
        if (f.CampaignsWaitingForTrigger > 0)
        {
            if (!f.RegimeTriggerEnabled)
            {
                return new AgentState(name, AgentActivation.AccesoInerte,
                    $"{f.CampaignsEnabled} campagne abilitate, {f.CampaignsWaitingForTrigger} in attesa di trigger, ma il trigger contestuale è SPENTO: non ripartono da sole");
            }

            // [A5b, 2026-08-20] Acceso non basta: se nessuno dei due bracci sa produrre un verdetto,
            // il wake non arriva MAI e la campagna resta ferma per sempre. Prima si concludeva
            // «le rimette in rotazione da solo» dal solo flag — la promessa che non poteva mantenere.
            // Il rischio è diventato concreto proprio togliendo la sveglia spuria di [A5]: fino ad
            // allora le campagne si svegliavano comunque, e un braccio cieco non si sarebbe visto.
            if (f.RegimeTriggerArms is { AnyArmArmed: false } cieco)
            {
                var perche = cieco.Reasons.Count > 0 ? " — " + string.Join("; ", cieco.Reasons) : "";
                return new AgentState(name, AgentActivation.AccesoInerte,
                    $"{f.CampaignsEnabled} campagne abilitate, {f.CampaignsWaitingForTrigger} in attesa di trigger: il trigger è ACCESO ma nessuno dei due bracci può esprimersi, quindi il wake non arriverà mai{perche}");
            }

            var bracci = f.RegimeTriggerArms switch
            {
                null => " (armamento dei bracci non interrogato)",
                { RegimeArmArmed: true, VolatilityArmArmed: true } => " (entrambi i bracci armati)",
                { RegimeArmArmed: true } => " (armato il solo braccio K-means: " + string.Join("; ", f.RegimeTriggerArms.Reasons) + ")",
                _ => " (armato il solo braccio volatilità: " + string.Join("; ", f.RegimeTriggerArms.Reasons) + ")",
            };
            return new AgentState(name, AgentActivation.AccesoOperante,
                $"{f.CampaignsEnabled} campagne abilitate, {f.CampaignsWaitingForTrigger} in attesa di trigger: un wake del trigger contestuale le rimette in rotazione da solo{bracci}");
        }

        // Restano le sole campagne in osservazione: la rotazione è ferma, ma al primo tick dopo un
        // riavvio il planner riallinea da solo le corsie Paper attese. È un'azione autonoma, quindi
        // «operante» — con la sua causa vera, che non è il wake.
        return new AgentState(name, AgentActivation.AccesoOperante,
            $"{f.CampaignsEnabled} campagne abilitate, nessuna in rotazione né in attesa di trigger (in osservazione): la rotazione è ferma, ma dopo un riavvio le corsie Paper attese vengono riallineate da sole una volta");
    }

    private static AgentState DescribeFleet(AgentStateFacts f)
    {
        const string name = "Orchestratore di flotta";
        if (!f.FleetEnabled)
        {
            return new AgentState(name, AgentActivation.Spento, "Fleet:Enabled spento");
        }
        if (f.FleetGovernedLanes <= 0)
        {
            return new AgentState(name, AgentActivation.AccesoInerte,
                "acceso ma nessuna corsia oltre l'impronta dell'auto-apply: non ha corsie da governare");
        }
        // Il dry-run NON è inerzia: decide, scrive il journal e produce proposte (e notifiche).
        // Dirlo «inerte» spiegherebbe male le proposte che l'operatore riceve.
        //
        // E il ramo «dry-run spento» NON si deduce dal flag: finché AF2b non esiste, spegnerlo non
        // accende alcuna esecuzione — il worker emette un warning e journalizza comunque
        // Applied=false. Leggere il flag e dichiarare «esecuzione attiva» era la bugia che la
        // revisione avversaria del 2026-08-18 ha trovato in questa riga.
        // [AF2b] Tre condizioni, non una: il braccio deve esistere, il dry-run essere spento E
        // almeno una corsia essere autorizzata. Guardarne meno di tre dichiarerebbe attiva
        // un'esecuzione che non può toccare nulla — la bugia del 2026-08-18 spostata di un flag.
        var detail = (f.FleetDryRun, f.FleetExecutionImplemented, f.FleetAuthorizedLanes > 0) switch
        {
            (true, _, _) => $"{f.FleetGovernedLanes} corsie sotto governo, in DRY-RUN: decide, journalizza e propone, non esegue",
            (false, false, _) => $"{f.FleetGovernedLanes} corsie sotto governo, DryRun spento ma il braccio esecutivo (AF2b) NON è implementato: il tick resta di solo journal, con un warning a ogni giro",
            (false, true, false) => $"{f.FleetGovernedLanes} corsie sotto governo, DryRun spento ma Fleet:ExecutionLanes è VUOTA: nessuna corsia autorizzata, resta di solo journal",
            (false, true, true) => $"{f.FleetGovernedLanes} corsie sotto governo, ESECUZIONE ATTIVA su {f.FleetAuthorizedLanes} corsie autorizzate: può FERMARLE da solo (l'avvio automatico non è implementato)",
        };
        return new AgentState(name, AgentActivation.AccesoOperante, detail);
    }

    private static AgentState DescribeCommittee(AgentStateFacts f)
    {
        const string name = "Comitato AI";
        if (!f.CommitteeEnabled)
        {
            return new AgentState(name, AgentActivation.Spento, "Committee:Enabled spento");
        }
        if (!f.FleetEnabled || !f.FleetUseCommittee)
        {
            var why = !f.FleetEnabled ? "l'orchestratore che lo interroga è spento" : "Fleet:UseCommittee è spento";
            return new AgentState(name, AgentActivation.AccesoInerte,
                $"acceso ma {why}: nessuno gli pone domande, e non voterà mai");
        }
        if (f.CommitteeProvidersWithKey is not int withKey)
        {
            return new AgentState(name, AgentActivation.NonDeterminabile,
                $"acceso, {f.CommitteeProviders} provider votanti, ma il keyring non ha risposto: quanti possano votare non è determinabile");
        }
        if (withKey < Math.Max(1, f.CommitteeMinValidVotes))
        {
            return new AgentState(name, AgentActivation.AccesoInerte,
                $"acceso, ma {withKey} provider con chiave su un quorum di {f.CommitteeMinValidVotes}: ogni giro cadrebbe sul default deterministico");
        }

        // Qui i flag sono tutti a posto — ed è esattamente lo stato in cui il comitato è rimasto
        // sedici giorni senza emettere un voto. Fermarsi ai flag darebbe la risposta rassicurante a
        // prescindere: il fatto che decide è quanti voti ha DAVVERO emesso, e sta nel journal.
        if (f.CommitteeVotesInWindow is not int votes)
        {
            return new AgentState(name, AgentActivation.NonDeterminabile,
                $"acceso e interrogabile ({withKey}/{f.CommitteeProviders} provider con chiave), ma il journal della flotta non ha risposto: quanti voti abbia emesso non è determinabile");
        }
        if (votes == 0)
        {
            return new AgentState(name, AgentActivation.AccesoInerte,
                $"acceso e interrogabile ({withKey}/{f.CommitteeProviders} provider con chiave, quorum {f.CommitteeMinValidVotes}), ma ZERO voti negli ultimi {f.CommitteeWindowDays} giorni: nessuno gli ha posto una domanda, perché la coda dell'orchestratore non produce pareggi");
        }
        return new AgentState(name, AgentActivation.AccesoOperante,
            $"acceso, {withKey}/{f.CommitteeProviders} provider con chiave, quorum {f.CommitteeMinValidVotes}: {votes} voti negli ultimi {f.CommitteeWindowDays} giorni");
    }

    private static AgentState DescribeDrift(AgentStateFacts f)
    {
        const string name = "Drift feature ML";
        // [I2] Il ciclo chiuso si dichiara SEMPRE, anche a monitor spento: è la manopola che
        // all'accensione entrerebbe in vigore col default del POCO (true) se la sezione Drift
        // manca dal file, ed è il modo in cui il ritiro automatico partirebbe senza che nessuno
        // lo abbia chiesto.
        var loop = f.DriftRetireChampionOnAlert
            ? f.ChampionCount switch
            {
                null => " · ritiro automatico del Champion ARMATO, Champion in carica non determinabile",
                0 => " · ritiro automatico del Champion ARMATO ma SENZA SOGGETTO (Champion in carica: 0)",
                var n => $" · ritiro automatico del Champion ARMATO su {n} Champion in carica",
            }
            : " · ritiro automatico del Champion spento";

        if (!f.DriftEnabled)
        {
            return new AgentState(name, AgentActivation.Spento, "Drift:Enabled spento" + loop);
        }
        if (f.SavedModelCount == 0)
        {
            return new AgentState(name, AgentActivation.AccesoInerte,
                "acceso ma NESSUN modello negli stage sorvegliati (Champion/Challenger): niente da confrontare finché non se ne schiera uno" + loop);
        }
        return new AgentState(name, AgentActivation.AccesoOperante,
            $"acceso su {f.SavedModelCount} modelli sorvegliati" + loop);
    }

    /// <summary>
    /// Raccoglie i fatti. Ogni fonte ha il suo try/catch e degrada a «non determinabile» invece di
    /// far fallire l'intera sonda: una fotografia parziale dichiarata vale più di nessuna
    /// fotografia (fail-open sulla diagnostica, regola 4 della piattaforma).
    /// </summary>
    private async Task<AgentStateFacts> GatherAsync(CancellationToken ct)
    {
        var campaignOpt = campaign.CurrentValue;
        var fleetOpt = fleet.CurrentValue;
        var committeeOpt = committee.CurrentValue;

        var driftOpt = drift.CurrentValue;
        var campaignsEnabled = 0;
        var campaignsRotating = 0;
        var campaignsWaiting = 0;
        var savedModels = 0;
        int? champions = null;
        int? committeeVotes = null;
        RegimeTriggerHealth? regimeArms = null;
        var since = DateTime.UtcNow.AddDays(-CommitteeVoteWindowDays);

        // [A5b] L'armamento dei bracci si interroga SOLO se c'è qualcuno che ne dipende: la domanda
        // costa una query e un caricamento di modello, e senza campagne in attesa non cambia il
        // verdetto. Fail-open come tutto il resto della sonda: se non risponde, si dichiara che non
        // è stato interrogato invece di dedurne un armamento che nessuno ha visto.
        try
        {
            await using var probeDb = await dbFactory.CreateDbContextAsync(ct);
            var attesa = await probeDb.VettingCampaigns.AsNoTracking()
                .CountAsync(c => c.Enabled && c.Status == CampaignStatus.WaitingForTrigger, ct);
            if (attesa > 0 && regimeChangeDetector is not null && regimeTrigger.CurrentValue.Enabled)
            {
                regimeArms = await regimeChangeDetector.DescribeHealthAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Sonda agenti: armamento del trigger contestuale non interrogabile.");
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            campaignsEnabled = await db.VettingCampaigns.AsNoTracking().CountAsync(c => c.Enabled, ct);
            campaignsRotating = await db.VettingCampaigns.AsNoTracking()
                .CountAsync(c => c.Enabled && c.Status == CampaignStatus.Rotating, ct);
            campaignsWaiting = await db.VettingCampaigns.AsNoTracking()
                .CountAsync(c => c.Enabled && c.Status == CampaignStatus.WaitingForTrigger, ct);
            // [I6c] Il conteggio che conta è quello dei modelli SORVEGLIATI, non di tutti i salvati:
            // «acceso su 158 modelli» mentre il worker ne guarda zero sarebbe la solita
            // rassicurazione. La regola è quella del worker, non una copia.
            var tuttiGliStage = await db.SavedMlModels.AsNoTracking()
                .Select(m => m.Stage)
                .ToListAsync(ct);
            savedModels = tuttiGliStage.Count(driftOpt.Monitors);
            champions = await db.SavedMlModels.AsNoTracking().CountAsync(m => m.Stage == ModelStage.Champion, ct);
            // Il voto lascia una traccia nel journal della flotta: è il fatto contro cui misurare
            // «il comitato funziona», invece dei suoi flag. Stringa vuota e "[]" valgono entrambe
            // «nessun voto» — il worker scrive l'una o l'altra a seconda del ramo.
            committeeVotes = await db.OrchestratorDecisions.AsNoTracking()
                .CountAsync(d => d.AtUtc >= since
                                 && d.VotesJson != null && d.VotesJson != "" && d.VotesJson != "[]", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sonda agenti: il database non ha risposto, la fotografia sarà parziale.");
        }

        var providers = committeeOpt.EffectiveProviders();
        int? withKey = null;
        if (keyStore is not null)
        {
            try
            {
                var found = 0;
                foreach (var p in providers)
                {
                    if (!string.IsNullOrWhiteSpace(await keyStore.GetKeyAsync(p, ct))) found++;
                }
                withKey = found;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sonda agenti: il keyring non ha risposto, i provider votanti restano non determinabili.");
            }
        }

        // Le corsie sotto governo dell'orchestratore sono quelle OLTRE l'impronta storica
        // dell'auto-apply: le 0..N-1 restano della pipeline, per costruzione (FleetOrchestrator).
        // L'impronta si chiede all'applier — è LUI che la definisce, e derivarla qui da un numero
        // scritto a mano sarebbe una seconda verità sullo stesso confine.
        var governed = Math.Max(0, TradingLanes.Count - applier.LaneCount);

        return new AgentStateFacts(
            CampaignEnabled: campaignOpt.Enabled,
            CampaignsEnabled: campaignsEnabled,
            CampaignsRotating: campaignsRotating,
            CampaignsWaitingForTrigger: campaignsWaiting,
            RegimeTriggerEnabled: regimeTrigger.CurrentValue.Enabled,
            RegimeTriggerArms: regimeArms,
            FleetEnabled: fleetOpt.Enabled,
            FleetDryRun: fleetOpt.DryRun,
            FleetExecutionImplemented: Fleet.FleetOrchestratorWorker.RetirementArmImplemented,
            FleetAuthorizedLanes: fleet.CurrentValue.ExecutionLanes.Count,
            FleetUseCommittee: fleetOpt.UseCommittee,
            FleetGovernedLanes: governed,
            CommitteeEnabled: committeeOpt.Enabled,
            CommitteeProviders: providers.Count,
            CommitteeProvidersWithKey: withKey,
            CommitteeMinValidVotes: committeeOpt.MinValidVotes,
            CommitteeVotesInWindow: committeeVotes,
            CommitteeWindowDays: CommitteeVoteWindowDays,
            DriftEnabled: driftOpt.Enabled,
            DriftRetireChampionOnAlert: driftOpt.RetireChampionOnAlert,
            SavedModelCount: savedModels,
            ChampionCount: champions);
    }
}

/// <summary>
/// Esegue la sonda una volta all'avvio, dopo una breve attesa (il database e il keyring possono non
/// essere ancora pronti). Una volta sola e basta: lo stato degli agenti è una condizione, non un
/// evento — e la card di <c>/admin/autonomy</c> lo rilegge a ogni apertura chiamando
/// <see cref="AgentStateProbe.ProbeAsync"/>.
/// </summary>
public sealed class AgentStateProbeWorker(
    AgentStateProbe probe,
    ILogger<AgentStateProbeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            await probe.ProbeAsync(log: true, stoppingToken);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sonda dello stato degli agenti fallita: la card di /admin/autonomy resta l'unica superficie.");
        }
    }
}
