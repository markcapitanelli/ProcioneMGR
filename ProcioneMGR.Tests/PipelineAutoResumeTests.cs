using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Agents;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Fase 3-C1 (PRD Autonomia §6): i run "Paused" con trigger AUTOMATICO riprendono da soli —
/// l'evidenza della sessione 2026-07-18 è un run interrotto dallo spegnimento rimasto Paused
/// tutto il giorno (unico chiamante di ResumeRunAsync = il bottone in /pipeline). I Paused
/// MANUALI restano manuali; budget di tentativi con marker persistenti; a esaurimento notifica.
/// </summary>
[Collection("Postgres")]
public sealed class PipelineAutoResumeTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public PipelineAutoResumeTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    // --- Fakes -------------------------------------------------------------------------------

    private sealed class ScriptedEngine : IPipelineEngine
    {
        public List<Guid> Resumed { get; } = new();
        public Exception? ThrowOnResume { get; set; }
        public PipelineLiveStatus? LiveStatus { get; set; }

        public Task<Guid> ResumeRunAsync(Guid runId, string? userId = null, CancellationToken ct = default)
        {
            if (ThrowOnResume is not null) throw ThrowOnResume;
            Resumed.Add(runId);
            return Task.FromResult(runId);
        }

        public Task<Guid> StartRunAsync(int configurationId, string trigger = "Manual", string? userId = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public void RequestPause(Guid runId) => throw new NotImplementedException();
        public void Cancel(Guid runId) => throw new NotImplementedException();
        public PipelineLiveStatus? GetLiveStatus() => LiveStatus;
        public List<string> ValidateConfiguration(IReadOnlyList<StageConfig> stages) => [];
        public Task<int> RecoverOrphanedRunsAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NullApplier : IPipelineApplier
    {
        public int LaneCount => 3;
        public Task<ApplyResult> ApplyRecommendationAsync(PipelineRecommendation recommendation, CancellationToken ct = default) => Task.FromResult(new ApplyResult());
        public Task<ApplyResult> ApplyRunAsync(Guid runId, CancellationToken ct = default) => Task.FromResult(new ApplyResult());
        public Task<EnsembleSummary> GetCurrentEnsembleSummaryAsync(CancellationToken ct = default) => Task.FromResult(new EnsembleSummary());
        public EnsembleSummary SummarizeRecommendation(PipelineRecommendation recommendation) => new();
    }

    private sealed class RecordingNotifier : ProcioneMGR.Services.Notifications.INotifier
    {
        public List<string> Titles { get; } = new();
        public Task NotifyAsync(ProcioneMGR.Services.Notifications.NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Titles.Add(title);
            return Task.CompletedTask;
        }
    }

    // --- Setup -------------------------------------------------------------------------------

    private async Task<(PipelineSchedulerWorker Worker, ScriptedEngine Engine,
        IDbContextFactory<ApplicationDbContext> DbFactory, RecordingNotifier Notifier)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var engine = new ScriptedEngine();
        var notifier = new RecordingNotifier();
        var evaluator = new RunApplyEvaluator(dbFactory, new NullApplier(),
            new EnsembleComparator(new EnsembleComparatorOptions()),
            new LoggingSupervisorAgent(NullLogger<LoggingSupervisorAgent>.Instance),
            NullLogger<RunApplyEvaluator>.Instance);
        var worker = new PipelineSchedulerWorker(
            dbFactory, engine, evaluator,
            new AutoReapplyOptions().AsMonitor(),
            NullLogger<PipelineSchedulerWorker>.Instance,
            metrics: null, notifier: notifier);
        return (worker, engine, dbFactory, notifier);
    }

    private static async Task<Guid> SeedPausedRunAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        string trigger = "Scheduled", string executionMode = "Paper")
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var config = new PipelineConfiguration { Name = "Caccia", CreatedBy = "u1", ExecutionMode = executionMode };
        db.PipelineConfigurations.Add(config);
        await db.SaveChangesAsync();

        var run = new PipelineRun
        {
            Id = Guid.NewGuid(),
            ConfigurationId = config.Id,
            StartedAt = DateTime.UtcNow.AddHours(-3),
            Status = "Paused",
            Trigger = trigger,
        };
        db.PipelineRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static async Task<int> AttemptCountAsync(IDbContextFactory<ApplicationDbContext> dbFactory, Guid runId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PipelineArtifacts.CountAsync(a => a.RunId == runId && a.Kind == AutoResumeArtifactKinds.Attempt);
    }

    // --- Test --------------------------------------------------------------------------------

    [Fact]
    public async Task PausedScheduledRun_IsResumed_AttemptRecorded()
    {
        var (worker, engine, dbFactory, _) = await BuildAsync();
        var runId = await SeedPausedRunAsync(dbFactory);

        await worker.AutoResumePausedRunsAsync(CancellationToken.None);

        Assert.Equal([runId], engine.Resumed);
        Assert.Equal(1, await AttemptCountAsync(dbFactory, runId));
    }

    [Fact]
    public async Task PausedManualRun_IsNeverTouched()
    {
        // Il Paused manuale è una SCELTA dell'operatore: l'automazione non la scavalca.
        var (worker, engine, dbFactory, _) = await BuildAsync();
        await SeedPausedRunAsync(dbFactory, trigger: "Manual");

        await worker.AutoResumePausedRunsAsync(CancellationToken.None);

        Assert.Empty(engine.Resumed);
    }

    [Fact]
    public async Task LiveConfigRun_IsNeverTouched()
    {
        var (worker, engine, dbFactory, _) = await BuildAsync();
        await SeedPausedRunAsync(dbFactory, executionMode: "Live");

        await worker.AutoResumePausedRunsAsync(CancellationToken.None);

        Assert.Empty(engine.Resumed);
    }

    [Fact]
    public async Task SlotBusy_NoAttemptConsumed()
    {
        // Un run tipico dura ore: consumare il budget mentre lo slot è occupato sarebbe un
        // give-up garantito in 3 tick. Con lo slot pieno il check non parte proprio.
        var (worker, engine, dbFactory, _) = await BuildAsync();
        var runId = await SeedPausedRunAsync(dbFactory);
        engine.LiveStatus = new PipelineLiveStatus();

        await worker.AutoResumePausedRunsAsync(CancellationToken.None);

        Assert.Empty(engine.Resumed);
        Assert.Equal(0, await AttemptCountAsync(dbFactory, runId));

        engine.LiveStatus = null;
        await worker.AutoResumePausedRunsAsync(CancellationToken.None);
        Assert.Single(engine.Resumed);
    }

    [Fact]
    public async Task ResumeFailure_ConsumesAttempt_ThenGivesUpAndNotifiesOnce()
    {
        var (worker, engine, dbFactory, notifier) = await BuildAsync();
        var runId = await SeedPausedRunAsync(dbFactory);
        engine.ThrowOnResume = new InvalidOperationException("checkpoint corrotto (simulato)");

        for (var i = 0; i < PipelineSchedulerWorker.MaxAutoResumeAttempts; i++)
        {
            await worker.AutoResumePausedRunsAsync(CancellationToken.None);
        }
        Assert.Equal(PipelineSchedulerWorker.MaxAutoResumeAttempts, await AttemptCountAsync(dbFactory, runId));

        // Budget esaurito: give-up + notifica, UNA volta sola anche su tick ripetuti.
        await worker.AutoResumePausedRunsAsync(CancellationToken.None);
        await worker.AutoResumePausedRunsAsync(CancellationToken.None);

        Assert.Equal(PipelineSchedulerWorker.MaxAutoResumeAttempts, await AttemptCountAsync(dbFactory, runId));
        Assert.Single(notifier.Titles, t => t.Contains("Auto-resume abbandonato"));
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Single(await db.PipelineArtifacts.Where(a => a.RunId == runId && a.Kind == AutoResumeArtifactKinds.GaveUp).ToListAsync());
    }
}
