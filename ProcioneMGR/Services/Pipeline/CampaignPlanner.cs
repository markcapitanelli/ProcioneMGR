using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Il Campaign Planner (Fase 1, PRD Autonomia Operativa §4): la politica di reazione agli esiti
/// dei run. La pipeline conclude onestamente "0 sopravvissuti" e SI FERMA; nella sessione di
/// esercizio 2026-07-18 la mossa successiva l'ha decisa ogni volta l'operatore. Questo servizio
/// prende ESATTAMENTE quelle decisioni, sopra il motore (mai dentro):
///
/// - 0 sopravvissuti → prossima config della rotazione, con backoff (la stessa config non si
///   ripete prima di N ore; un "wake" di un trigger contestuale bypassa il backoff);
/// - sopravvissuti &gt; 0 → STESSA catena della ri-applica automatica (supervisore con veto +
///   isteresi + <see cref="IPipelineApplier"/> via <see cref="IRunApplyEvaluator"/>); se schierato:
///   rotazione ferma, stato "Observing", corsie avviate in Paper (solo quelle ferme, mai Live);
///   se NON schierato (veto/isteresi): la caccia continua — scostamento deliberato dal PRD §4,
///   che fermava la rotazione su qualunque sopravvissuto: fermarsi senza aver schierato nulla
///   lascerebbe la flotta ferma per un candidato rifiutato;
/// - rotazione esaurita (tutte le config in backoff) → "WaitingForTrigger": non si macina la
///   stessa rotazione all'infinito in un regime invariato, si aspetta il segnale di cambio
///   (Fase 2) o l'operatore.
///
/// SAFETY: gate globale <c>Campaign:Enabled</c> (default OFF) + gate per campagna; le config
/// in ExecutionMode "Live" vengono SALTATE (stessa regola dello scheduler); l'avvio corsie è
/// SOLO Paper e solo su corsie ferme (una corsia in quarantena rifiuta da sola: Fase 0-A3).
/// </summary>
public interface ICampaignPlanner
{
    /// <summary>Un giro di decisioni su tutte le campagne abilitate. Chiamato dal worker; pubblico per test.</summary>
    Task TickAsync(CancellationToken ct = default);

    /// <summary>
    /// Un trigger contestuale (Fase 2) chiede di anticipare la prossima esecuzione: le campagne
    /// in rotazione o in attesa tornano eleggibili SUBITO (backoff bypassato) e il prossimo run
    /// parte con trigger "Event". Le campagne in osservazione non vengono toccate (hanno già un
    /// ensemble schierato: il decadimento lo sorveglia il decay monitor). Ritorna quante campagne
    /// sono state svegliate.
    /// </summary>
    Task<int> WakeAsync(string reason, CancellationToken ct = default);
}

/// <inheritdoc cref="ICampaignPlanner"/>
public sealed class CampaignPlanner(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPipelineEngine engine,
    IRunApplyEvaluator applyEvaluator,
    IServiceProvider serviceProvider,
    IOptionsMonitor<CampaignOptions> options,
    ILogger<CampaignPlanner> logger,
    ProcioneMGR.Services.Notifications.INotifier? notifier = null) : ICampaignPlanner
{
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (!options.CurrentValue.Enabled) return;

        List<int> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            ids = await db.VettingCampaigns.Where(c => c.Enabled).OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(ct);
        }

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try { await ProcessCampaignAsync(id, ct); }
            catch (Exception ex) { logger.LogError(ex, "Decisione fallita per la campagna {Id}; ritento al prossimo tick.", id); }
        }
    }

    public async Task<int> WakeAsync(string reason, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var eligible = await db.VettingCampaigns
            .Where(c => c.Enabled && c.Status != CampaignStatus.Observing)
            .ToListAsync(ct);

        foreach (var campaign in eligible)
        {
            campaign.Status = CampaignStatus.Rotating;
            campaign.PendingWakeReason = reason;
            campaign.UpdatedAtUtc = DateTime.UtcNow;
            logger.LogInformation("Campagna {Id} '{Name}' svegliata da un trigger: {Reason}", campaign.Id, campaign.Name, reason);
        }
        await db.SaveChangesAsync(ct);
        return eligible.Count;
    }

    private async Task ProcessCampaignAsync(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var campaign = await db.VettingCampaigns.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null || !campaign.Enabled) return;

        if (campaign.PendingRunId is Guid runId)
        {
            // Una decisione per tick e per campagna: l'eventuale run successivo parte al giro dopo.
            await EvaluatePendingRunAsync(db, campaign, runId, ct);
            return;
        }

        if (campaign.Status == CampaignStatus.Rotating)
        {
            await TryStartNextConfigAsync(db, campaign, ct);
        }
    }

    // ------------------------------------------------------------ esito del run pendente

    private async Task EvaluatePendingRunAsync(ApplicationDbContext db, VettingCampaign campaign, Guid runId, CancellationToken ct)
    {
        var run = await db.PipelineRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            campaign.PendingRunId = null;
            SetOutcome(campaign, $"Run {runId} scomparso dal DB: rotazione ripresa.");
            await db.SaveChangesAsync(ct);
            return;
        }

        switch (run.Status)
        {
            case "Running":
            case "Paused": // la ripresa automatica dei Paused è la Fase 3-C1: qui si aspetta e basta
                return;

            case "Failed":
            case "Cancelled":
                MarkConfigOutcome(campaign, runId, "Failed");
                campaign.PendingRunId = null;
                SetOutcome(campaign, $"Run {run.Status} (config {run.ConfigurationId}): si passa alla prossima config della rotazione.");
                await db.SaveChangesAsync(ct);
                await NotifyAsync(Notifications.NotificationSeverity.Warning,
                    $"Campagna '{campaign.Name}': run {run.Status}", campaign.LastOutcome!, ct);
                return;

            case "Completed":
                await EvaluateCompletedRunAsync(db, campaign, run, ct);
                return;
        }
    }

    private async Task EvaluateCompletedRunAsync(ApplicationDbContext db, VettingCampaign campaign, PipelineRun run, CancellationToken ct)
    {
        var recommendation = RunApplyEvaluator.DeserializeRecommendation(run.RecommendationJson);
        var survivors = recommendation?.EnsembleLegs.Count ?? 0;

        if (survivors == 0)
        {
            MarkConfigOutcome(campaign, run.Id, "NoSurvivors");
            campaign.PendingRunId = null;
            SetOutcome(campaign,
                $"0 sopravvissuti all'holdout (config {run.ConfigurationId}): prossima config della rotazione " +
                $"(questa non si ripete prima di {campaign.BackoffHours}h).");
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Campagna {Id}: {Outcome}", campaign.Id, campaign.LastOutcome);
            return;
        }

        // Sopravvissuti: la STESSA catena valuta-e-applica della ri-applica automatica
        // (supervisore con veto → isteresi → applier). Idempotente per run.
        var outcome = await applyEvaluator.EvaluateAndMaybeApplyAsync(run.Id, ct);

        if (outcome.Applied)
        {
            MarkConfigOutcome(campaign, run.Id, "Applied");
            campaign.PendingRunId = null;
            campaign.Status = CampaignStatus.Observing;
            SetOutcome(campaign,
                $"{survivors} sopravvissuti (config {run.ConfigurationId}): ensemble schierato su {outcome.LanesUsed} corsie. " +
                "Rotazione ferma, campagna in osservazione.");
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Campagna {Id}: {Outcome}", campaign.Id, campaign.LastOutcome);
            await NotifyAsync(Notifications.NotificationSeverity.Info,
                $"Campagna '{campaign.Name}': ensemble schierato", campaign.LastOutcome!, ct);

            if (campaign.AutoStartPaperLanes)
            {
                await StartPaperLanesAsync(outcome.LanesUsed, ct);
            }
        }
        else
        {
            MarkConfigOutcome(campaign, run.Id, "NotApplied");
            campaign.PendingRunId = null;
            SetOutcome(campaign,
                $"{survivors} sopravvissuti (config {run.ConfigurationId}) ma ensemble NON schierato: {outcome.Message} " +
                "La rotazione continua.");
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Campagna {Id}: {Outcome}", campaign.Id, campaign.LastOutcome);
        }
    }

    // ------------------------------------------------------------ rotazione

    private async Task TryStartNextConfigAsync(ApplicationDbContext db, VettingCampaign campaign, CancellationToken ct)
    {
        var states = ParseConfigStates(campaign.ConfigStatesJson);
        if (states.Count == 0)
        {
            SetOutcome(campaign, "Campagna senza configurazioni: nessuna azione.");
            await db.SaveChangesAsync(ct);
            return;
        }

        var wake = campaign.PendingWakeReason is not null;
        var now = DateTime.UtcNow;
        var backoff = TimeSpan.FromHours(Math.Max(1, campaign.BackoffHours));

        // Round-robin: si riparte dalla config successiva all'ultima eseguita (ordine = rotazione).
        var lastIdx = -1;
        DateTime? lastRun = null;
        for (var i = 0; i < states.Count; i++)
        {
            if (states[i].LastRunAtUtc is DateTime t && (lastRun is null || t > lastRun)) { lastRun = t; lastIdx = i; }
        }

        for (var offset = 1; offset <= states.Count; offset++)
        {
            var state = states[(lastIdx + offset) % states.Count];
            var eligible = wake || state.LastRunAtUtc is null || state.LastRunAtUtc + backoff <= now;
            if (!eligible) continue;

            var config = await db.PipelineConfigurations.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == state.ConfigurationId, ct);
            if (config is null)
            {
                logger.LogWarning("Campagna {Id}: config {ConfigId} inesistente, saltata.", campaign.Id, state.ConfigurationId);
                continue;
            }
            if (config.ExecutionMode == "Live")
            {
                // Stessa regola non negoziabile dello scheduler: i run automatici non eseguono MAI in Live.
                logger.LogWarning("Campagna {Id}: config {ConfigId} '{Name}' in modalità Live SALTATA (i run automatici non eseguono mai in Live).",
                    campaign.Id, config.Id, config.Name);
                continue;
            }

            var trigger = wake ? "Event" : "Campaign";
            Guid runId;
            try
            {
                runId = await engine.StartRunAsync(config.Id, trigger, string.IsNullOrEmpty(campaign.CreatedBy) ? null : campaign.CreatedBy, ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("già in corso", StringComparison.Ordinal))
            {
                // Slot singolo del motore occupato (run manuale o schedulato): si riprova al prossimo tick.
                logger.LogInformation("Campagna {Id}: run rimandato, un altro run è già in corso.", campaign.Id);
                return;
            }

            state.Attempts++;
            state.LastRunId = runId;
            state.LastRunAtUtc = now;
            campaign.ConfigStatesJson = SerializeConfigStates(states);
            campaign.PendingRunId = runId;
            campaign.PendingWakeReason = null;
            SetOutcome(campaign, $"Run avviato (config {config.Id} '{config.Name}', trigger {trigger}).");
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Campagna {Id}: {Outcome}", campaign.Id, campaign.LastOutcome);
            return;
        }

        // Nessuna config eleggibile: le non-eseguite sono sempre eleggibili, quindi qui la
        // rotazione è ESAURITA (tutte già tentate e in backoff) → attesa di un trigger (Fase 2).
        if (campaign.Status != CampaignStatus.WaitingForTrigger)
        {
            campaign.Status = CampaignStatus.WaitingForTrigger;
            SetOutcome(campaign, "Rotazione esaurita senza ensemble schierato: in attesa di un trigger contestuale (cambio regime/vol) o dell'operatore.");
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Campagna {Id} '{Name}': {Outcome}", campaign.Id, campaign.Name, campaign.LastOutcome);
            await NotifyAsync(Notifications.NotificationSeverity.Warning,
                $"Campagna '{campaign.Name}': rotazione esaurita", campaign.LastOutcome!, ct);
        }
    }

    // ------------------------------------------------------------ avvio corsie (Paper, mai oltre)

    /// <summary>
    /// Avvia in PAPER le corsie appena configurate dall'applica (0..lanesUsed-1), SOLO se ferme:
    /// una corsia già in esecuzione non viene mai riavviata (riavviarla azzererebbe capitale/PnL
    /// della sessione in corso), una in quarantena rifiuta da sola (Fase 0-A3). Mai Testnet
    /// (backlog §8 del PRD), mai Live (invariante di piattaforma).
    /// </summary>
    private async Task StartPaperLanesAsync(int lanesUsed, CancellationToken ct)
    {
        for (var lane = 0; lane < Math.Min(lanesUsed, TradingLanes.Count); lane++)
        {
            try
            {
                var laneEngine = serviceProvider.GetRequiredKeyedService<ITradingEngine>(lane);
                var status = await laneEngine.GetStatusAsync(ct);
                if (status.IsRunning)
                {
                    logger.LogInformation("Corsia {Lane} già in esecuzione ({Mode}): non toccata dal planner.", lane, status.Mode);
                    continue;
                }
                await laneEngine.StartAsync(TradingMode.Paper, ct);
                logger.LogInformation("Corsia {Lane} avviata in Paper dal Campaign Planner.", lane);
            }
            catch (Exception ex)
            {
                // Es. quarantena attiva: l'ensemble resta configurato, l'avvio lo decide l'operatore.
                logger.LogWarning(ex, "Avvio Paper della corsia {Lane} non riuscito: {Msg}", lane, ex.Message);
            }
        }
    }

    // ------------------------------------------------------------ helpers puri

    /// <summary>Notifica best-effort (Fase 4): il dispatcher non propaga mai, ma il notifier può mancare (test/host senza canale).</summary>
    private async Task NotifyAsync(Notifications.NotificationSeverity severity, string title, string body, CancellationToken ct)
    {
        if (notifier is not null) await notifier.NotifyAsync(severity, title, body, ct);
    }

    private static void SetOutcome(VettingCampaign campaign, string message)
    {
        campaign.LastOutcome = message;
        campaign.LastActionAtUtc = DateTime.UtcNow;
        campaign.UpdatedAtUtc = DateTime.UtcNow;
    }

    private void MarkConfigOutcome(VettingCampaign campaign, Guid runId, string outcome)
    {
        var states = ParseConfigStates(campaign.ConfigStatesJson);
        var state = states.FirstOrDefault(s => s.LastRunId == runId);
        if (state is null) return;
        state.LastOutcome = outcome;
        campaign.ConfigStatesJson = SerializeConfigStates(states);
    }

    public static List<CampaignConfigState> ParseConfigStates(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<CampaignConfigState>>(json) ?? []; }
        catch { return []; }
    }

    public static string SerializeConfigStates(List<CampaignConfigState> states)
        => JsonSerializer.Serialize(states);
}
