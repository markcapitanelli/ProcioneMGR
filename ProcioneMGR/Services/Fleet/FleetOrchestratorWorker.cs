using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Carry;
using ProcioneMGR.Services.Notifications;

namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [AF2] Il braccio della Queen Bee: ogni tick legge lo stato (reader), decide (core puro),
/// applica l'ISTERESI sui ritiri e scrive il journal. In questo incremento (AF2a) NON esegue
/// nulla — nemmeno con <c>Fleet:DryRun=false</c>: l'esecuzione arriva con AF2b, e un flag girato
/// in anticipo deve produrre un avviso, non un'azione non collaudata. Vive nel SOLO monolite
/// (è il cervello: scheduler, planner e promozioni stanno già qui).
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
    Llm.Narration.IPostMortemService? postMortems = null) : BackgroundService
{
    /// <summary>Verdetti di ritiro CONSECUTIVI per corsia (isteresi: si agisce solo alla conferma).</summary>
    private readonly Dictionary<int, int> _retireStreak = new();

    private string? _lastBlockedReason;
    private DateTime? _lastCarryAlertUtc;

    /// <summary>
    /// [AF2b] <b>Il braccio esecutivo esiste?</b> Oggi <b>no</b>: con <c>Fleet:DryRun=false</c>
    /// questo worker emette un warning e journalizza comunque <c>DryRun=true, Applied=false</c> —
    /// nessun ramo avvia o ferma una corsia.
    ///
    /// È dichiarato QUI, accanto al ramo che lo implementerà, e non dedotto altrove da
    /// <c>Fleet:DryRun</c>, perché quel flag dice che cosa è stato <i>chiesto</i>, non che cosa il
    /// codice sa <i>fare</i>. Una sonda che leggesse il flag direbbe «esecuzione attiva» di un
    /// braccio inesistente: è il difetto della classe «controllo che rassicura», e la revisione
    /// avversaria del 2026-08-18 lo ha trovato proprio così in <c>AgentStateProbe</c>.
    /// Chi implementa AF2b cambia questa costante e la sonda dice la verità senza altre modifiche.
    /// </summary>
    public const bool ExecutionArmImplemented = false;

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
                        Actions = plan.Actions.Select(a => a is AssignCandidateToLane assign && assign.LaneId == menu.LaneId
                            ? new AssignCandidateToLane(elected.RunId, menu.LaneId,
                                $"Scelto dal comitato fra {menu.Eligible.Count} candidati: {elected.Summary} " +
                                $"(~{elected.TradesPerMonth:F1} trade/mese, {elected.Timeframe}).")
                            : a).ToList(),
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

        if (!opt.DryRun && !ExecutionArmImplemented)
        {
            // AF2a: il braccio esecutivo non esiste ancora. Dirlo a voce alta batte eseguire
            // qualcosa di non collaudato — e batte anche il silenzio.
            logger.LogWarning("Fleet:DryRun=false ma l'esecuzione delle azioni arriva con AF2b: questo tick resta di solo journal.");
        }

        var confirmedRetires = ApplyRetireHysteresis(plan, Math.Max(1, opt.RetireConfirmTicks));

        foreach (var action in plan.Actions)
        {
            switch (action)
            {
                case AssignCandidateToLane assign:
                    await JournalAsync(new OrchestratorDecision
                    {
                        AtUtc = DateTime.UtcNow, Kind = "Assign", LaneId = assign.LaneId, RunId = assign.RunId,
                        Source = assignSource, VotesJson = votesJson,
                        Reason = assign.Reason, DryRun = true, Applied = false,
                    }, ct);
                    logger.LogInformation("[DRY-RUN] Assegnerei il run {Run} alla corsia {Lane}: {Reason}",
                        assign.RunId, assign.LaneId, assign.Reason);
                    break;

                case StopAndFreeLane retire when confirmedRetires.Contains(retire.LaneId):
                    await JournalAsync(new OrchestratorDecision
                    {
                        AtUtc = DateTime.UtcNow, Kind = "Retire", LaneId = retire.LaneId,
                        Reason = retire.Reason, DryRun = true, Applied = false,
                    }, ct);
                    logger.LogWarning("[DRY-RUN] Ritirerei la corsia {Lane}: {Reason}", retire.LaneId, retire.Reason);
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
    private async Task WatchCarryAsync(CancellationToken ct)
    {
        var opt = options.CurrentValue;
        if (!carryOptions.CurrentValue.Enabled) return;

        var carry = serviceProvider.GetService<CarryWorker>();
        if (carry is null)
        {
            logger.LogDebug("Carry abilitato ma il worker vive nell'host del motore: sorveglianza non disponibile da qui.");
            return;
        }

        var silence = TimeSpan.FromHours(Math.Max(1, opt.CarrySilenceAlertHours));
        var last = carry.LastEvaluationUtc;
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
