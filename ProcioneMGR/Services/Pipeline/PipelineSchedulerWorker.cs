using Cronos;
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

/// <summary>Kind degli artifact dell'auto-resume (Fase 3-C1, PRD Autonomia): marker idempotenti per-run.</summary>
public static class AutoResumeArtifactKinds
{
    /// <summary>Un tentativo di ripresa automatica (il conteggio = numero di questi artifact).</summary>
    public const string Attempt = "AutoResumeAttempt";

    /// <summary>Tentativi esauriti: notificato e mai più toccato (resta all'operatore).</summary>
    public const string GaveUp = "AutoResumeGaveUp";
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
/// concorrenza dei run è già gestita da <c>PipelineEngine</c>. La catena valuta-e-applica
/// (supervisore → confronto con isteresi → applier, con gate di atomicità sulle corsie) vive in
/// <see cref="IRunApplyEvaluator"/>, CONDIVISA con il <see cref="CampaignPlanner"/> (Fase 1 PRD
/// Autonomia): una sola implementazione, nessuna deriva tra i percorsi automatici.
/// </summary>
public sealed class PipelineSchedulerWorker(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPipelineEngine engine,
    IRunApplyEvaluator applyEvaluator,
    Microsoft.Extensions.Options.IOptionsMonitor<AutoReapplyOptions> autoReapply,
    ILogger<PipelineSchedulerWorker> logger,
    ProcioneMGR.Services.Observability.ProcioneMetrics? metrics = null,
    ProcioneMGR.Services.Notifications.INotifier? notifier = null) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Fase 3-C1: tentativi di auto-resume per run prima di arrendersi e notificare. Più di 1
    /// (scostamento documentato dal PRD, che diceva "1 tentativo"): un run interrotto DUE volte da
    /// riavvii innocenti merita più di un tentativo; il tetto esiste perché un run che fa crashare
    /// il processo non deve diventare un crash-loop di riprese automatiche.
    /// </summary>
    public const int MaxAutoResumeAttempts = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PipelineSchedulerWorker avviato (check ogni {Interval}, ri-applica automatica={Auto}).",
            CheckInterval, autoReapply.CurrentValue.Enabled);

        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(CheckInterval);
        var orphansRecovered = false;
        do
        {
            try
            {
                // Bonifica one-shot dei run orfani ("Running" ereditati da un processo morto), NEL
                // loop e non prima: se il pod parte quando il DB non è ancora raggiungibile (l'ordine
                // di avvio in K8s non è garantito), il tentativo si ripete al tick successivo invece
                // di perdersi. Qui e non in TickAsync, che i test chiamano direttamente con una
                // semantica precisa (schedulazione + auto-reapply) da non allargare.
                if (!orphansRecovered)
                {
                    await engine.RecoverOrphanedRunsAsync(stoppingToken);
                    orphansRecovered = true;
                }

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

        // Fase 3-C1: i run Paused con trigger automatico riprendono da soli (nessun gate: utile
        // già da sola, come da PRD §6 — i Paused MANUALI restano manuali).
        await AutoResumePausedRunsAsync(ct);
    }

    // ------------------------------------------------------------ auto-resume (Fase 3-C1)

    /// <summary>
    /// Riprende i run "Paused" con trigger AUTOMATICO (Scheduled/Event/Campaign): tipicamente gli
    /// orfani di un riavvio, che <see cref="IPipelineEngine.RecoverOrphanedRunsAsync"/> marca
    /// Paused e che prima restavano lì finché un umano non premeva Riprendi (evidenza della
    /// sessione 2026-07-18: un run interrotto dallo spegnimento è rimasto Paused tutto il giorno).
    /// I Paused MANUALI (trigger "Manual") non vengono MAI toccati. Config in modalità Live:
    /// saltate (difesa in profondità — un run automatico su config Live non esiste per costruzione).
    /// Massimo <see cref="MaxAutoResumeAttempts"/> tentativi per run (marker su PipelineArtifacts,
    /// sopravvivono ai riavvii), poi notifica e stop. Pubblico per test.
    /// </summary>
    public async Task AutoResumePausedRunsAsync(CancellationToken ct)
    {
        // Slot del motore occupato: inutile (e dannoso) tentare — un run tipico dura ore e ogni
        // tentativo consumerebbe il budget di riprese. Si riprova al primo tick a slot libero.
        if (engine.GetLiveStatus() is not null) return;

        List<PipelineRun> paused;
        Dictionary<Guid, int> attempts;
        HashSet<Guid> gaveUp;
        Dictionary<int, PipelineConfiguration> configs;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var since = DateTime.UtcNow.AddDays(-7);
            paused = await db.PipelineRuns
                .Where(r => r.Status == "Paused"
                            && (r.Trigger == "Scheduled" || r.Trigger == "Event" || r.Trigger == "Campaign")
                            && r.StartedAt >= since)
                .OrderBy(r => r.StartedAt)
                .ToListAsync(ct);
            if (paused.Count == 0) return;

            var ids = paused.Select(r => r.Id).ToList();
            attempts = await db.PipelineArtifacts
                .Where(a => ids.Contains(a.RunId) && a.Kind == AutoResumeArtifactKinds.Attempt)
                .GroupBy(a => a.RunId)
                .Select(g => new { RunId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.RunId, x => x.Count, ct);
            gaveUp = (await db.PipelineArtifacts
                .Where(a => ids.Contains(a.RunId) && a.Kind == AutoResumeArtifactKinds.GaveUp)
                .Select(a => a.RunId)
                .ToListAsync(ct)).ToHashSet();
            var configIds = paused.Select(r => r.ConfigurationId).Distinct().ToList();
            configs = await db.PipelineConfigurations.AsNoTracking()
                .Where(c => configIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, ct);
        }

        foreach (var run in paused)
        {
            ct.ThrowIfCancellationRequested();
            if (gaveUp.Contains(run.Id)) continue;
            if (!configs.TryGetValue(run.ConfigurationId, out var config)) continue;
            if (config.ExecutionMode == "Live") continue; // mai auto-resume verso Live, per quanto impossibile

            var tried = attempts.GetValueOrDefault(run.Id);
            if (tried >= MaxAutoResumeAttempts)
            {
                await RecordAutoResumeGiveUpAsync(run, tried, ct);
                continue;
            }

            try
            {
                // Marker PRIMA della ripresa: se il run fa crashare il processo, il tentativo
                // risulta comunque consumato (altrimenti sarebbe un crash-loop di riprese).
                await RecordAutoResumeAttemptAsync(run.Id, tried + 1, ct);
                await engine.ResumeRunAsync(run.Id, config.CreatedBy, ct);
                logger.LogInformation("Auto-resume del run {RunId} (config '{Name}', tentativo {N}/{Max}).",
                    run.Id, config.Name, tried + 1, MaxAutoResumeAttempts);
                return; // slot singolo: un solo resume per tick
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("già in corso", StringComparison.Ordinal)
                                                       || ex.Message.Contains("già in esecuzione", StringComparison.Ordinal))
            {
                // Race residua col pre-check GetLiveStatus (un run manuale partito nel frattempo):
                // il marker è già scritto e il tentativo risulta consumato — raro e accettabile,
                // il budget serve contro i crash-loop, non contro questa finestra.
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auto-resume del run {RunId} fallito (tentativo {N}/{Max}).",
                    run.Id, tried + 1, MaxAutoResumeAttempts);
            }
        }
    }

    private async Task RecordAutoResumeAttemptAsync(Guid runId, int attemptNumber, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.PipelineArtifacts.Add(new PipelineArtifact
        {
            RunId = runId,
            StageName = "AutoResume",
            Kind = AutoResumeArtifactKinds.Attempt,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { attemptNumber, attemptedAtUtc = DateTime.UtcNow }),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordAutoResumeGiveUpAsync(PipelineRun run, int tried, CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var exists = await db.PipelineArtifacts.AnyAsync(a => a.RunId == run.Id && a.Kind == AutoResumeArtifactKinds.GaveUp, ct);
            if (exists) return;
            db.PipelineArtifacts.Add(new PipelineArtifact
            {
                RunId = run.Id,
                StageName = "AutoResume",
                Kind = AutoResumeArtifactKinds.GaveUp,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { attempts = tried, gaveUpAtUtc = DateTime.UtcNow }),
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        logger.LogWarning("Auto-resume ABBANDONATO per il run {RunId} dopo {N} tentativi: resta Paused, serve l'operatore.",
            run.Id, tried);
        if (notifier is not null)
        {
            await notifier.NotifyAsync(ProcioneMGR.Services.Notifications.NotificationSeverity.Warning,
                "Auto-resume abbandonato",
                $"Il run {run.Id} (trigger {run.Trigger}) resta Paused dopo {tried} riprese automatiche fallite: riprendilo o annullalo da /pipeline.", ct);
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

    /// <summary>Valuta un singolo run e, se giustificato, ne schiera l'ensemble (delega a <see cref="IRunApplyEvaluator"/>).</summary>
    public Task EvaluateAndMaybeApplyAsync(Guid runId, CancellationToken ct)
        => applyEvaluator.EvaluateAndMaybeApplyAsync(runId, ct);

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
