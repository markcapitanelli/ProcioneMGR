using Cronos;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Agents;
using ProcioneMGR.Services.Ensemble;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>Opzioni della ri-applica automatica dell'ensemble (sezione di config <c>AutoReapply</c>).</summary>
public sealed class AutoReapplyOptions
{
    /// <summary>
    /// Interruttore globale della ri-applica automatica. DEFAULT false (safety): finché non lo
    /// abiliti esplicitamente, lo scheduler lancia i run ma NON schiera mai da solo un ensemble —
    /// l'utente applica a mano da /pipeline, come prima.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Quanti giorni indietro guardare per i run completati non ancora valutati.</summary>
    public int LookbackDays { get; set; } = 7;

    /// <summary>Massimo numero di run valutati per tick (limita il fan-out).</summary>
    public int MaxPerTick { get; set; } = 3;
}

/// <summary>Kind dell'artifact che registra la decisione di ri-applica automatica di un run.</summary>
public static class AutoReapplyArtifactKinds
{
    public const string Decision = "AutoReapplyDecision";
}

/// <summary>
/// Worker schedulato: (1) valuta periodicamente le <see cref="PipelineConfiguration"/> con
/// <see cref="PipelineConfiguration.ScheduleEnabled"/> attivo e lancia quelle dovute; (2) RI-APPLICA
/// automaticamente l'ensemble migliore trovato dai run completati (se <c>AutoReapply:Enabled</c>).
///
/// SAFETY non negoziabile: un run schedulato non esegue MAI in Live — viene saltato (non declassato
/// silenziosamente) se la config è in Live. La ri-applica automatica scrive SOLO la configurazione
/// dell'ensemble sulle corsie (mai avvia trading, mai passa in Live, mai tocca SafetyChecker);
/// l'apertura reale resta dietro conferma manuale in /trading. La sostituzione avviene solo se
/// SIA il confronto oggettivo (<see cref="IEnsembleComparator"/>) SIA il supervisore AI
/// (<see cref="IPipelineSupervisorAgent"/>, che può solo porre un veto) sono d'accordo.
///
/// Il motore (<see cref="IPipelineEngine"/>) è a slot singolo: niente lock per-config qui, la
/// concorrenza dei run è già gestita da <c>PipelineEngine</c>. L'applicazione dell'ensemble è resa
/// atomica da un <see cref="SemaphoreSlim"/> globale, così due run valutati nello stesso tick non
/// scrivono le corsie in contemporanea.
/// </summary>
public sealed class PipelineSchedulerWorker(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPipelineEngine engine,
    IPipelineApplier applier,
    IEnsembleComparator comparator,
    IPipelineSupervisorAgent supervisor,
    Microsoft.Extensions.Options.IOptionsMonitor<AutoReapplyOptions> autoReapply,
    ILogger<PipelineSchedulerWorker> logger,
    ProcioneMGR.Services.Observability.ProcioneMetrics? metrics = null) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>Serializza l'applicazione dell'ensemble sulle corsie (atomicità globale tra run/tick).</summary>
    private static readonly SemaphoreSlim ApplyGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PipelineSchedulerWorker avviato (check ogni {Interval}, ri-applica automatica={Auto}).",
            CheckInterval, autoReapply.CurrentValue.Enabled);

        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ciclo dello scheduler pipeline fallito; ritento al prossimo tick.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("PipelineSchedulerWorker fermato.");
    }

    /// <summary>Un tick completo: valuta le config schedulate e poi processa i run completati per la ri-applica. Pubblico per test.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var configs = await db.PipelineConfigurations
                .Where(c => c.ScheduleEnabled && c.Schedule != null && c.Schedule != "")
                .ToListAsync(ct);

            foreach (var config in configs)
            {
                ct.ThrowIfCancellationRequested();
                await EvaluateConfigAsync(config, db, now, ct);
            }
        }

        // Letto a ogni tick (hot-reload da /admin/autonomy): accendere/spegnere la ri-applica
        // automatica non richiede riavvio.
        if (autoReapply.CurrentValue.Enabled)
        {
            await ProcessCompletedRunsAsync(ct);
        }
    }

    private async Task EvaluateConfigAsync(PipelineConfiguration config, ApplicationDbContext db, DateTime now, CancellationToken ct)
    {
        if (!IsDue(config, now))
        {
            return;
        }

        var nextRun = ComputeNextRun(config.Schedule!, now);
        if (nextRun is null)
        {
            logger.LogError("Espressione di schedulazione non valida per la config {Id} '{Name}': '{Schedule}'.",
                config.Id, config.Name, config.Schedule);
            return;
        }

        if (config.ExecutionMode == "Live")
        {
            logger.LogWarning(
                "Config pipeline {Id} '{Name}' è in modalità Live: il run schedulato viene SALTATO per sicurezza (i run automatici non eseguono mai in Live). Passa a Paper o Disabled per abilitare la schedulazione.",
                config.Id, config.Name);
            config.NextRunAt = nextRun;
            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            var runId = await engine.StartRunAsync(config.Id, "Scheduled", config.CreatedBy, ct);
            config.NextRunAt = nextRun;
            await db.SaveChangesAsync(ct);
            metrics?.RecordPipelineRun("Scheduled");
            logger.LogInformation("Run schedulato avviato: config {Id} '{Name}' -> run {RunId}.", config.Id, config.Name, runId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("già in corso", StringComparison.Ordinal))
        {
            logger.LogInformation("Run schedulato rimandato per config {Id} '{Name}': un altro run è già in corso.", config.Id, config.Name);
        }
        catch (Exception ex)
        {
            config.NextRunAt = nextRun;
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "Run schedulato fallito per config {Id} '{Name}'.", config.Id, config.Name);
        }
    }

    // ------------------------------------------------------------ auto re-apply

    /// <summary>
    /// Trova i run schedulati COMPLETATI di recente senza una decisione di ri-applica registrata e li
    /// valuta uno per uno (confronto oggettivo + supervisore AI). Pubblico per test.
    /// </summary>
    public async Task ProcessCompletedRunsAsync(CancellationToken ct)
    {
        var opt = autoReapply.CurrentValue;
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, opt.LookbackDays));
        List<Guid> pending;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Run schedulati completati che non hanno ancora una decisione di ri-applica (left-anti-join).
            // Solo i run "Scheduled": la ri-applica automatica agisce sui cicli che l'utente ha già
            // messo in automazione, non sui run manuali (quelli si applicano a mano da /pipeline).
            pending = await db.PipelineRuns
                .Where(r => r.Status == "Completed" && r.Trigger == "Scheduled" && r.CompletedAt != null && r.CompletedAt >= since)
                .Where(r => !db.PipelineArtifacts.Any(a => a.RunId == r.Id && a.Kind == AutoReapplyArtifactKinds.Decision))
                .OrderBy(r => r.CompletedAt)
                .Select(r => r.Id)
                .Take(Math.Max(1, opt.MaxPerTick))
                .ToListAsync(ct);
        }

        foreach (var runId in pending)
        {
            ct.ThrowIfCancellationRequested();
            try { await EvaluateAndMaybeApplyAsync(runId, ct); }
            catch (Exception ex) { logger.LogError(ex, "Valutazione ri-applica fallita per il run {RunId}.", runId); }
        }
    }

    /// <summary>Valuta un singolo run e, se giustificato, ne schiera l'ensemble. Registra sempre una decisione (idempotente).</summary>
    public async Task EvaluateAndMaybeApplyAsync(Guid runId, CancellationToken ct)
    {
        PipelineRun? run;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            run = await db.PipelineRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        }
        if (run is null) return;

        var candidate = DeserializeRecommendation(run.RecommendationJson);
        if (candidate is null || candidate.EnsembleLegs.Count == 0)
        {
            await RecordDecisionAsync(runId, applied: false, "Run senza ensemble applicabile: nessuna azione.", null, null, ct);
            return;
        }

        var candidateSummary = applier.SummarizeRecommendation(candidate);
        var currentSummary = await applier.GetCurrentEnsembleSummaryAsync(ct);

        // 1. Supervisore AI (può solo porre un veto; su errore/assenza approva → decidono le metriche).
        var judgment = await supervisor.AnalyzeRunAsync(run, currentSummary, candidateSummary, ct);

        // 2. Confronto oggettivo con hysteresis.
        var comparison = comparator.Compare(currentSummary.IsEmpty ? null : currentSummary, candidateSummary);

        var applied = false;
        string message;
        if (comparison.ShouldReplace && judgment.ApproveReplacement)
        {
            await ApplyGate.WaitAsync(ct);
            try
            {
                var result = await applier.ApplyRecommendationAsync(candidate, ct);
                applied = true;
                message = $"Ensemble sostituito automaticamente. {comparison.Reason} {result.Message}";
                logger.LogInformation("Ri-applica automatica ESEGUITA per il run {RunId}: {Reason}", runId, comparison.Reason);
            }
            finally { ApplyGate.Release(); }
        }
        else if (comparison.ShouldReplace && !judgment.ApproveReplacement)
        {
            message = $"Ensemble corrente mantenuto: VETO del supervisore AI. {judgment.Summary}";
            logger.LogInformation("Ri-applica automatica VETATA dal supervisore per il run {RunId}.", runId);
        }
        else
        {
            message = comparison.Reason;
            logger.LogInformation("Ri-applica automatica NON eseguita per il run {RunId}: {Reason}", runId, comparison.Reason);
        }

        await RecordDecisionAsync(runId, applied, message, comparison, judgment, ct);
    }

    /// <summary>Persiste la decisione come <see cref="PipelineArtifact"/> (marker idempotente + fonte per la UI). Nessuna nuova tabella.</summary>
    private async Task RecordDecisionAsync(Guid runId, bool applied, string message, EnsembleComparison? comparison, SupervisorJudgment? judgment, CancellationToken ct)
    {
        var payload = new AutoReapplyDecisionArtifact
        {
            Applied = applied,
            Message = message,
            Comparison = comparison,
            Judgment = judgment,
            DecidedAtUtc = DateTime.UtcNow,
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exists = await db.PipelineArtifacts.AnyAsync(a => a.RunId == runId && a.Kind == AutoReapplyArtifactKinds.Decision, ct);
        if (exists) return; // idempotente
        db.PipelineArtifacts.Add(new PipelineArtifact
        {
            RunId = runId,
            StageName = "AutoReapply",
            Kind = AutoReapplyArtifactKinds.Decision,
            PayloadJson = JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static PipelineRecommendation? DeserializeRecommendation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
        try { return JsonSerializer.Deserialize<PipelineRecommendation>(json); }
        catch { return null; }
    }

    // ------------------------------------------------------------ pure helpers

    /// <summary>Vero se il prossimo run è dovuto: mai calcolato (null) o nel passato. Pura, testabile in isolamento.</summary>
    public static bool IsDue(PipelineConfiguration config, DateTime nowUtc)
        => config.NextRunAt is null || config.NextRunAt <= nowUtc;

    /// <summary>Prossima occorrenza UTC per un'espressione cron standard a 5 campi, o null se non valida. Pura, testabile in isolamento.</summary>
    public static DateTime? ComputeNextRun(string schedule, DateTime fromUtc)
    {
        try
        {
            return CronExpression.Parse(schedule).GetNextOccurrence(fromUtc, TimeZoneInfo.Utc);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}

/// <summary>Payload dell'artifact di decisione della ri-applica automatica (persistito come JSON).</summary>
public sealed class AutoReapplyDecisionArtifact
{
    public bool Applied { get; set; }
    public string Message { get; set; } = string.Empty;
    public EnsembleComparison? Comparison { get; set; }
    public SupervisorJudgment? Judgment { get; set; }
    public DateTime DecidedAtUtc { get; set; }
}
