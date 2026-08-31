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

    private string? _lastBlockedReason;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue;
            if (opt.Enabled)
            {
                try { await TickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogError(ex, "Tick dell'orchestratore di flotta fallito; ritento al prossimo."); }
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
    internal static string DescribeAssignSource(Llm.Committee.CommitteeVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (verdict.ByQuorum) return "committee";

        var validi = verdict.Votes.Count(v => v.Valid);
        if (verdict.Votes.Count == 0) return "default:non-interrogato";   // budget esaurito, zero provider con chiave
        if (validi == 0) return "default:tutti-astenuti";                 // interrogati, nessuna risposta valida
        return "default:quorum-mancato";                                  // hanno risposto, la maggioranza non si è formata
    }

    /// <summary>Un tick completo. Pubblico per i test di integrazione e per un futuro "Esegui ora".</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var opt = options.CurrentValue;
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
                assignSource = DescribeAssignSource(verdict);

                if (verdict.ByQuorum && Guid.TryParseExact(verdict.ChosenOptionId, "N", out var chosen)
                    && chosen != menu.DefaultRunId
                    && menu.Eligible.FirstOrDefault(c => c.RunId == chosen) is { } elected)
                {
                    // Il verdetto è GIÀ garantito dentro il menù (doppia validazione nel comitato);
                    // qui si sostituisce solo l'azione corrispondente, mai altro.
                    plan = plan with
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

        var confirmedRetires = ApplyRetireHysteresis(plan, Math.Max(1, opt.RetireConfirmTicks));

        // [AF2b] Il budget di esecuzione del giro: una corsia per volta, per poter distinguere una
        // decisione giusta da un guasto del lettore di stato.
        var executionBudget = Math.Max(1, opt.MaxExecutionsPerTick);

        foreach (var action in plan.Actions)
        {
            switch (action)
            {
                case AssignCandidateToLane assign:
                    if (WhyNotExecutedAssignment(opt, assign.LaneId, executionBudget,
                        hasKey: assign.CandidateKey is not null, hasDeployer: greyDeployer is not null, isGrey: false) is { } percheAssign)
                    {
                        await JournalAsync(new OrchestratorDecision
                        {
                            AtUtc = DateTime.UtcNow, Kind = "Assign", LaneId = assign.LaneId, RunId = assign.RunId,
                            Source = assignSource, VotesJson = votesJson,
                            Reason = $"[{percheAssign}] {assign.Reason}", DryRun = true, Applied = false,
                        }, ct);
                        logger.LogInformation("[{Perche}] Assegnerei il run {Run} alla corsia {Lane}: {Reason}",
                            percheAssign, assign.RunId, assign.LaneId, assign.Reason);
                    }
                    else
                    {
                        executionBudget--;
                        await ExecuteAssignAsync(assign.RunId, assign.CandidateKey!, assign.LaneId,
                            isGrey: false, assign.Reason, assignSource, votesJson, ct);
                    }
                    break;

                case AssignGreyCandidateToLane greyAssign:
                    if (WhyNotExecutedAssignment(opt, greyAssign.LaneId, executionBudget,
                        hasKey: true, hasDeployer: greyDeployer is not null, isGrey: true) is { } percheGrey)
                    {
                        await JournalAsync(new OrchestratorDecision
                        {
                            AtUtc = DateTime.UtcNow, Kind = "Assign", LaneId = greyAssign.LaneId, RunId = greyAssign.RunId,
                            Source = assignSource, VotesJson = votesJson,
                            Reason = $"[{percheGrey}] {greyAssign.Reason}", DryRun = true, Applied = false,
                        }, ct);
                        logger.LogInformation("[{Perche}] Schiererei il grigio {Run} sulla corsia {Lane}: {Reason}",
                            percheGrey, greyAssign.RunId, greyAssign.LaneId, greyAssign.Reason);
                    }
                    else
                    {
                        executionBudget--;
                        await ExecuteAssignAsync(greyAssign.RunId, greyAssign.CandidateKey, greyAssign.LaneId,
                            isGrey: true, greyAssign.Reason, assignSource, votesJson, ct);
                    }
                    break;

                case StopAndFreeLane retire when confirmedRetires.Contains(retire.LaneId):
                    if (WhyNotExecuted(opt, retire.LaneId, executionBudget) is { } perche)
                    {
                        await JournalAsync(new OrchestratorDecision
                        {
                            AtUtc = DateTime.UtcNow, Kind = "Retire", LaneId = retire.LaneId,
                            Reason = $"[{perche}] {retire.Reason}", DryRun = true, Applied = false,
                        }, ct);
                        logger.LogWarning("[{Perche}] Ritirerei la corsia {Lane}: {Reason}", perche, retire.LaneId, retire.Reason);
                    }
                    else
                    {
                        executionBudget--;
                        await ExecuteRetireAsync(retire, ct);
                    }
                    break;

                case StopAndFreeLane pending:
                    // Verdetto non ancora confermato dall'isteresi: si annota solo nel log.
                    logger.LogInformation("Ritiro corsia {Lane} in attesa di conferma ({Streak}/{Needed}): {Reason}",
                        pending.LaneId, _retireStreak.GetValueOrDefault(pending.LaneId), Math.Max(1, opt.RetireConfirmTicks), pending.Reason);
                    break;

                case ProposeGreyCandidate grey:
                    await JournalAsync(new OrchestratorDecision
                    {
                        AtUtc = DateTime.UtcNow, Kind = "ProposeGrey", RunId = grey.RunId,
                        Reason = grey.Reason, DryRun = opt.DryRun, Applied = true, // la proposta È l'azione
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
                    if (!string.Equals(blocked.Reason, _lastBlockedReason, StringComparison.Ordinal))
                    {
                        _lastBlockedReason = blocked.Reason;
                        await JournalAsync(new OrchestratorDecision
                        {
                            AtUtc = DateTime.UtcNow, Kind = "Blocked", Reason = blocked.Reason,
                            DryRun = opt.DryRun, Applied = false,
                        }, ct);
                        logger.LogInformation("Flotta bloccata: {Reason}", blocked.Reason);
                    }
                    break;
            }
        }

        if (!plan.Actions.OfType<FleetNoOp>().Any())
        {
            _lastBlockedReason = null; // il blocco è rientrato: il prossimo si journalizza di nuovo
        }

        await WatchCarryAsync(ct);
    }

    /// <summary>
    /// [J13/J14] L'esecuzione di un'assegnazione: lo STESSO deployer del click umano F5
    /// (bracket automatico, frequenza attesa, rilettura fail-closed della corsia), con
    /// <c>Source="fleet"</c>. Il journal lo scrive QUI il worker — non il deployer — perché la
    /// riga deve portare i voti del comitato e la fonte della scelta, che il deployer non conosce:
    /// due righe per la stessa azione sarebbero rumore nel posto dove si va a cercare cosa è
    /// successo.
    /// </summary>
    private async Task ExecuteAssignAsync(
        Guid runId, string candidateKey, int laneId, bool isGrey,
        string reason, string assignSource, string votesJson, CancellationToken ct)
    {
        string? error = null;
        var message = string.Empty;
        try
        {
            // allowSurvivor: il braccio «pass» schiera sopravvissuti; quello grigio solo grigi.
            var result = await greyDeployer!.DeployAsync(runId, candidateKey, laneId,
                startPaper: true, ct, source: "fleet", allowSurvivor: !isGrey, journal: false);
            message = result.Message;
            if (!result.Success) error = result.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            error = ex.Message;
            message = ex.Message;
        }

        await JournalAsync(new OrchestratorDecision
        {
            AtUtc = DateTime.UtcNow, Kind = "Assign", LaneId = laneId, RunId = runId,
            Source = assignSource == "rules" ? "fleet" : assignSource, VotesJson = votesJson,
            Reason = $"{reason} Esito: {message}", DryRun = false, Applied = error is null, Error = error,
        }, ct);

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
    /// </summary>
    private async Task ExecuteRetireAsync(StopAndFreeLane retire, CancellationToken ct)
    {
        string? error = null;
        try
        {
            var engine = serviceProvider.GetRequiredKeyedService<Trading.ITradingEngine>(retire.LaneId);
            var status = await engine.GetStatusAsync(ct);

            if (status.Mode != Trading.TradingMode.Paper)
            {
                error = $"corsia in {status.Mode}, non Paper: l'orchestratore non ferma corsie che non governa";
            }
            else if (!status.IsRunning)
            {
                error = "corsia già ferma: niente da fare";
            }
            else
            {
                await engine.StopAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        await JournalAsync(new OrchestratorDecision
        {
            AtUtc = DateTime.UtcNow, Kind = "Retire", LaneId = retire.LaneId,
            Reason = retire.Reason, DryRun = false, Applied = error is null, Error = error,
        }, ct);

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
    private HashSet<int> ApplyRetireHysteresis(FleetPlan plan, int confirmTicks)
    {
        var votedNow = plan.Actions.OfType<StopAndFreeLane>().Select(a => a.LaneId).ToHashSet();

        foreach (var lane in _retireStreak.Keys.Where(l => !votedNow.Contains(l)).ToList())
        {
            _retireStreak.Remove(lane);
        }

        var confirmed = new HashSet<int>();
        foreach (var lane in votedNow)
        {
            var streak = _retireStreak.GetValueOrDefault(lane) + 1;
            _retireStreak[lane] = streak;
            if (streak >= confirmTicks) confirmed.Add(lane);
        }
        return confirmed;
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
