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
/// Test unitari sulle funzioni pure di <see cref="PipelineSchedulerWorker"/> (nessun DB).
/// </summary>
public class PipelineSchedulerWorkerStaticTests
{
    [Fact]
    public void IsDue_NextRunAtNull_ReturnsTrue()
    {
        var config = new PipelineConfiguration { NextRunAt = null };
        Assert.True(PipelineSchedulerWorker.IsDue(config, DateTime.UtcNow));
    }

    [Fact]
    public void IsDue_NextRunAtInPast_ReturnsTrue()
    {
        var now = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
        var config = new PipelineConfiguration { NextRunAt = now.AddMinutes(-1) };
        Assert.True(PipelineSchedulerWorker.IsDue(config, now));
    }

    [Fact]
    public void IsDue_NextRunAtInFuture_ReturnsFalse()
    {
        var now = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
        var config = new PipelineConfiguration { NextRunAt = now.AddMinutes(1) };
        Assert.False(PipelineSchedulerWorker.IsDue(config, now));
    }

    [Fact]
    public void ComputeNextRun_DailyExpression_ReturnsNextMidnight()
    {
        var from = new DateTime(2026, 7, 4, 10, 0, 0, DateTimeKind.Utc);
        var next = PipelineSchedulerWorker.ComputeNextRun("0 3 * * *", from);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 7, 5, 3, 0, 0, DateTimeKind.Utc), next!.Value);
    }

    [Fact]
    public void ComputeNextRun_InvalidExpression_ReturnsNull()
    {
        Assert.Null(PipelineSchedulerWorker.ComputeNextRun("not a cron expression", DateTime.UtcNow));
    }

    [Fact]
    public void ComputeNextRun_IsDeterministic_SameInputsSameOutput()
    {
        var from = new DateTime(2026, 7, 4, 10, 0, 0, DateTimeKind.Utc);
        var a = PipelineSchedulerWorker.ComputeNextRun("*/5 * * * *", from);
        var b = PipelineSchedulerWorker.ComputeNextRun("*/5 * * * *", from);
        Assert.Equal(a, b);
    }
}

/// <summary>
/// Test di integrazione di <see cref="PipelineSchedulerWorker.TickAsync"/> con un DB Postgres reale
/// (Testcontainers) e un <see cref="IPipelineEngine"/> scriptato, per controllare esattamente quando
/// il motore viene invocato senza dipendere da un run pipeline reale (lento, non deterministico).
/// </summary>
[Collection("Postgres")]
public class PipelineSchedulerWorkerIntegrationTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public PipelineSchedulerWorkerIntegrationTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>Motore scriptato: StartRunAsync delega a una funzione fornita dal test; tutto il resto non serve qui.</summary>
    private sealed class ScriptedPipelineEngine(Func<int, string, Task<Guid>> onStart) : IPipelineEngine
    {
        public int StartCallCount { get; private set; }

        public Task<Guid> StartRunAsync(int configurationId, string trigger = "Manual", string? userId = null, CancellationToken ct = default)
        {
            StartCallCount++;
            return onStart(configurationId, trigger);
        }

        public Task<Guid> ResumeRunAsync(Guid runId, string? userId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public void RequestPause(Guid runId) => throw new NotImplementedException();
        public void Cancel(Guid runId) => throw new NotImplementedException();
        public PipelineLiveStatus? GetLiveStatus() => null;
        public List<string> ValidateConfiguration(IReadOnlyList<StageConfig> stages) => [];
    }

    /// <summary>Applier scriptato: registra le richieste di applicazione senza toccare le corsie reali.</summary>
    private sealed class FakeApplier(EnsembleSummary? current = null) : IPipelineApplier
    {
        public int ApplyCallCount { get; private set; }
        public int LaneCount => 3;
        public Task<ApplyResult> ApplyRecommendationAsync(PipelineRecommendation recommendation, CancellationToken ct = default)
        {
            ApplyCallCount++;
            return Task.FromResult(new ApplyResult { LanesUsed = 1, Message = "applicato (fake)" });
        }
        public Task<ApplyResult> ApplyRunAsync(Guid runId, CancellationToken ct = default) => ApplyRecommendationAsync(new PipelineRecommendation(), ct);
        public Task<EnsembleSummary> GetCurrentEnsembleSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(current ?? new EnsembleSummary());
        public EnsembleSummary SummarizeRecommendation(PipelineRecommendation recommendation)
            => new()
            {
                WeightedAverageSharpe = recommendation.EnsembleLegs.Count > 0 ? recommendation.EnsembleLegs.Average(l => l.HoldoutSharpe) : 0m,
                SurvivingLegs = recommendation.EnsembleLegs.Count,
                DistinctSymbols = recommendation.EnsembleLegs.Select(l => l.Symbol).Distinct().Count(),
                Legs = new List<LegSummary>(),
            };
    }

    private async Task<(PipelineSchedulerWorker Worker, ScriptedPipelineEngine Engine, IDbContextFactory<ApplicationDbContext> DbFactory)> BuildAsync(
        Func<int, string, Task<Guid>> onStart,
        AutoReapplyOptions? autoReapply = null,
        IPipelineApplier? applier = null,
        IPipelineSupervisorAgent? supervisor = null)
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

        var engine = new ScriptedPipelineEngine(onStart);
        var worker = new PipelineSchedulerWorker(
            dbFactory,
            engine,
            applier ?? new FakeApplier(),
            new EnsembleComparator(new EnsembleComparatorOptions()),
            supervisor ?? new LoggingSupervisorAgent(NullLogger<LoggingSupervisorAgent>.Instance),
            autoReapply ?? new AutoReapplyOptions(), // Enabled=false di default: comportamento invariato
            NullLogger<PipelineSchedulerWorker>.Instance);
        return (worker, engine, dbFactory);
    }

    private static PipelineConfiguration DueConfig(string executionMode = "Paper") => new()
    {
        Name = "Test config",
        CreatedBy = "user-1",
        ExecutionMode = executionMode,
        Schedule = "*/5 * * * *",
        ScheduleEnabled = true,
        NextRunAt = null, // mai schedulato -> dovuto subito
        UniverseJson = "[]",
        DateRangesJson = "{}",
        StagesJson = "[]",
    };

    [Fact]
    public async Task TickAsync_DueEnabledPaperConfig_LaunchesScheduledRun()
    {
        var (worker, engine, dbFactory) = await BuildAsync((id, trigger) => Task.FromResult(Guid.NewGuid()));
        int configId;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var cfg = DueConfig();
            db.PipelineConfigurations.Add(cfg);
            await db.SaveChangesAsync();
            configId = cfg.Id;
        }

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal(1, engine.StartCallCount);
        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        var reloaded = await verifyDb.PipelineConfigurations.FirstAsync(c => c.Id == configId);
        Assert.NotNull(reloaded.NextRunAt);
        Assert.True(reloaded.NextRunAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task TickAsync_NextRunInFuture_DoesNotLaunch()
    {
        var (worker, engine, dbFactory) = await BuildAsync((id, trigger) => Task.FromResult(Guid.NewGuid()));
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var cfg = DueConfig();
            cfg.NextRunAt = DateTime.UtcNow.AddHours(1);
            db.PipelineConfigurations.Add(cfg);
            await db.SaveChangesAsync();
        }

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal(0, engine.StartCallCount);
    }

    [Fact]
    public async Task TickAsync_ScheduleDisabled_DoesNotLaunch()
    {
        var (worker, engine, dbFactory) = await BuildAsync((id, trigger) => Task.FromResult(Guid.NewGuid()));
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var cfg = DueConfig();
            cfg.ScheduleEnabled = false;
            db.PipelineConfigurations.Add(cfg);
            await db.SaveChangesAsync();
        }

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal(0, engine.StartCallCount);
    }

    [Fact]
    public async Task TickAsync_LiveMode_SkipsLaunch_ButAdvancesNextRunAt()
    {
        var (worker, engine, dbFactory) = await BuildAsync((id, trigger) => Task.FromResult(Guid.NewGuid()));
        int configId;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var cfg = DueConfig(executionMode: "Live");
            db.PipelineConfigurations.Add(cfg);
            await db.SaveChangesAsync();
            configId = cfg.Id;
        }

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal(0, engine.StartCallCount); // MAI eseguito in Live: la garanzia non negoziabile
        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        var reloaded = await verifyDb.PipelineConfigurations.FirstAsync(c => c.Id == configId);
        Assert.NotNull(reloaded.NextRunAt); // ma non resta bloccata a martellare ogni tick
    }

    [Fact]
    public async Task TickAsync_TriggerIsScheduled_PassedToEngine()
    {
        string? capturedTrigger = null;
        var (worker, _, dbFactory) = await BuildAsync((id, trigger) => { capturedTrigger = trigger; return Task.FromResult(Guid.NewGuid()); });
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.PipelineConfigurations.Add(DueConfig());
            await db.SaveChangesAsync();
        }

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal("Scheduled", capturedTrigger);
    }

    [Fact]
    public async Task TickAsync_EngineBusy_DoesNotAdvanceNextRunAt_RetriesNextTick()
    {
        var (worker, engine, dbFactory) = await BuildAsync(
            (id, trigger) => throw new InvalidOperationException("Un run del pipeline è già in corso: attendere o annullarlo."));
        int configId;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var cfg = DueConfig();
            db.PipelineConfigurations.Add(cfg);
            await db.SaveChangesAsync();
            configId = cfg.Id;
        }

        await worker.TickAsync(CancellationToken.None); // non deve propagare l'eccezione

        Assert.Equal(1, engine.StartCallCount);
        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        var reloaded = await verifyDb.PipelineConfigurations.FirstAsync(c => c.Id == configId);
        Assert.Null(reloaded.NextRunAt); // resta dovuto: il prossimo tick ritenta
    }

    [Fact]
    public async Task TickAsync_EngineThrowsOtherError_DoesNotCrash_AdvancesNextRunAt()
    {
        var (worker, engine, dbFactory) = await BuildAsync(
            (id, trigger) => throw new InvalidOperationException("Configurazione non valida: universo vuoto."));
        int configId;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var cfg = DueConfig();
            db.PipelineConfigurations.Add(cfg);
            await db.SaveChangesAsync();
            configId = cfg.Id;
        }

        await worker.TickAsync(CancellationToken.None); // non deve propagare l'eccezione

        Assert.Equal(1, engine.StartCallCount);
        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        var reloaded = await verifyDb.PipelineConfigurations.FirstAsync(c => c.Id == configId);
        Assert.NotNull(reloaded.NextRunAt); // avanza: non martella ogni 5 minuti su un errore permanente
    }

    [Fact]
    public async Task TickAsync_InvalidCronExpression_DoesNotCrash_DoesNotLaunch()
    {
        var (worker, engine, dbFactory) = await BuildAsync((id, trigger) => Task.FromResult(Guid.NewGuid()));
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var cfg = DueConfig();
            cfg.Schedule = "not a valid cron";
            db.PipelineConfigurations.Add(cfg);
            await db.SaveChangesAsync();
        }

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal(0, engine.StartCallCount);
    }

    [Fact]
    public async Task TickAsync_MultipleDueConfigs_EachEvaluatedIndependently()
    {
        var (worker, engine, dbFactory) = await BuildAsync((id, trigger) => Task.FromResult(Guid.NewGuid()));
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.PipelineConfigurations.Add(DueConfig());
            db.PipelineConfigurations.Add(DueConfig());
            db.PipelineConfigurations.Add(new PipelineConfiguration
            {
                Name = "Not scheduled", CreatedBy = "user-1", ExecutionMode = "Paper",
                Schedule = null, ScheduleEnabled = false,
                UniverseJson = "[]", DateRangesJson = "{}", StagesJson = "[]",
            });
            await db.SaveChangesAsync();
        }

        await worker.TickAsync(CancellationToken.None);

        Assert.Equal(2, engine.StartCallCount); // solo le 2 config con schedulazione abilitata
    }

    // ------------------------------------------------------------ auto re-apply

    private sealed class VetoingSupervisor : IPipelineSupervisorAgent
    {
        public string Provider => "Test";
        public Task<SupervisorJudgment> AnalyzeRunAsync(PipelineRun run, EnsembleSummary? current, EnsembleSummary? candidate, CancellationToken ct = default)
            => Task.FromResult(new SupervisorJudgment { ApproveReplacement = false, Summary = "veto di test" });
    }

    private async Task<Guid> SeedCompletedScheduledRunAsync(IDbContextFactory<ApplicationDbContext> dbFactory, decimal sharpe)
    {
        var rec = new PipelineRecommendation
        {
            Survivors = 2,
            EnsembleLegs =
            {
                new ProposedLeg { StrategyName = "A", Symbol = "BTC/USDT", Timeframe = "4h", WeightPercent = 50m, HoldoutSharpe = sharpe },
                new ProposedLeg { StrategyName = "B", Symbol = "ETH/USDT", Timeframe = "4h", WeightPercent = 50m, HoldoutSharpe = sharpe },
            },
        };
        var runId = Guid.NewGuid();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.PipelineRuns.Add(new PipelineRun
        {
            Id = runId,
            ConfigurationId = 1,
            StartedAt = DateTime.UtcNow.AddMinutes(-30),
            CompletedAt = DateTime.UtcNow.AddMinutes(-5),
            Status = "Completed",
            Trigger = "Scheduled",
            RecommendationJson = System.Text.Json.JsonSerializer.Serialize(rec),
        });
        await db.SaveChangesAsync();
        return runId;
    }

    [Fact]
    public async Task ProcessCompletedRuns_BetterCandidate_AppliesAndRecordsDecision()
    {
        var applier = new FakeApplier(); // ensemble corrente vuoto → prima applicazione
        var (worker, _, dbFactory) = await BuildAsync(
            (id, t) => Task.FromResult(Guid.NewGuid()),
            new AutoReapplyOptions { Enabled = true },
            applier);
        var runId = await SeedCompletedScheduledRunAsync(dbFactory, sharpe: 1.5m);

        await worker.ProcessCompletedRunsAsync(CancellationToken.None);

        Assert.Equal(1, applier.ApplyCallCount);
        await using var db = await dbFactory.CreateDbContextAsync();
        var art = await db.PipelineArtifacts.SingleAsync(a => a.RunId == runId && a.Kind == AutoReapplyArtifactKinds.Decision);
        Assert.Contains("sostituito", art.PayloadJson);
    }

    [Fact]
    public async Task ProcessCompletedRuns_SupervisorVeto_DoesNotApply_ButRecordsDecision()
    {
        var applier = new FakeApplier();
        var (worker, _, dbFactory) = await BuildAsync(
            (id, t) => Task.FromResult(Guid.NewGuid()),
            new AutoReapplyOptions { Enabled = true },
            applier,
            new VetoingSupervisor());
        var runId = await SeedCompletedScheduledRunAsync(dbFactory, sharpe: 1.5m);

        await worker.ProcessCompletedRunsAsync(CancellationToken.None);

        Assert.Equal(0, applier.ApplyCallCount); // vetato
        await using var db = await dbFactory.CreateDbContextAsync();
        var art = await db.PipelineArtifacts.SingleAsync(a => a.RunId == runId && a.Kind == AutoReapplyArtifactKinds.Decision);
        Assert.Contains("VETO", art.PayloadJson);
    }

    [Fact]
    public async Task ProcessCompletedRuns_IsIdempotent_DoesNotReprocess()
    {
        var applier = new FakeApplier();
        var (worker, _, dbFactory) = await BuildAsync(
            (id, t) => Task.FromResult(Guid.NewGuid()),
            new AutoReapplyOptions { Enabled = true },
            applier);
        await SeedCompletedScheduledRunAsync(dbFactory, sharpe: 1.5m);

        await worker.ProcessCompletedRunsAsync(CancellationToken.None);
        await worker.ProcessCompletedRunsAsync(CancellationToken.None); // secondo giro: già deciso

        Assert.Equal(1, applier.ApplyCallCount);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
