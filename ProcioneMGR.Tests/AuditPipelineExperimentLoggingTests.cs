using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Audit FASE 3.3 — ogni run di pipeline (successo O fallimento) deve comporre un
/// <c>ExperimentRun</c> accanto al <c>PipelineRun</c>: parametri (universo, modalità, seed),
/// metriche (stage/sopravvissuti) e stato finale coerente. È ciò che rende i run confrontabili
/// in modo deterministico nella tabella di /experiments. Il wiring esiste in
/// <c>PipelineEngine.FinalizeRun</c> ma nessun test lo verificava end-to-end.
/// </summary>
[Collection("Postgres")]
public sealed class AuditPipelineExperimentLoggingTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public AuditPipelineExperimentLoggingTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class NoopStage : IPipelineStage
    {
        public string Name => "Noop";
        public string DisplayName => "Noop";
        public string Description => "";
        public int DefaultOrder => 1;
        public IReadOnlyList<StageDependency> Dependencies => [];
        public IReadOnlyList<StageParameterDefinition> ParameterDefinitions => [];
        public string? ValidateInput(PipelineContext ctx) => null;
        public Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct) => Task.CompletedTask;
        public StageSummary Summarize(PipelineContext ctx) => new() { StageName = Name, DisplayName = DisplayName };
    }

    private sealed class FailingStage : IPipelineStage
    {
        public string Name => "Failing";
        public string DisplayName => "Failing";
        public string Description => "";
        public int DefaultOrder => 1;
        public IReadOnlyList<StageDependency> Dependencies => [];
        public IReadOnlyList<StageParameterDefinition> ParameterDefinitions => [];
        public string? ValidateInput(PipelineContext ctx) => null;
        public Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
            => throw new InvalidOperationException("Guasto simulato dello stage.");
        public StageSummary Summarize(PipelineContext ctx) => new() { StageName = Name, DisplayName = DisplayName };
    }

    /// <summary>Popola il contesto con un candidato sopravvissuto (metriche holdout + gate anti-overfitting),
    /// così la finalizzazione può loggarne il profilo nell'ExperimentRun gemello.</summary>
    private sealed class SurvivorStage : IPipelineStage
    {
        public string Name => "Survivor";
        public string DisplayName => "Survivor";
        public string Description => "";
        public int DefaultOrder => 1;
        public IReadOnlyList<StageDependency> Dependencies => [];
        public IReadOnlyList<StageParameterDefinition> ParameterDefinitions => [];
        public string? ValidateInput(PipelineContext ctx) => null;
        public Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
        {
            ctx.Validated.Add(new ProcioneMGR.Services.Pipeline.ValidatedCandidate
            {
                StrategyName = "Alpha", Symbol = "BTC/USDT", Timeframe = "1h",
                Survived = true, HoldoutSharpe = 1.5m, HoldoutReturn = 0.22m, HoldoutMaxDrawdown = 8m,
                DeflatedSharpe = 0.7, PanelPbo = 0.12,
            });
            ctx.Validated.Add(new ProcioneMGR.Services.Pipeline.ValidatedCandidate
            {
                StrategyName = "Beta", Symbol = "ETH/USDT", Timeframe = "1h",
                Survived = false, HoldoutSharpe = 0.3m, PanelPbo = 0.12, RejectReason = "sotto soglia",
            });
            return Task.CompletedTask;
        }
        public StageSummary Summarize(PipelineContext ctx) => new() { StageName = Name, DisplayName = DisplayName };
    }

    private sealed class SingleStageCatalog(IPipelineStage stage) : IPipelineStageCatalog
    {
        public IReadOnlyList<IPipelineStage> Prototypes => [stage];
        public IPipelineStage Create(IServiceProvider scopedProvider, string name) => stage;
        public List<StageConfig> DefaultStages() => [];
    }

    private async Task<(PipelineEngine Engine, IDbContextFactory<ApplicationDbContext> DbFactory)> BuildAsync(IPipelineStage stage)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var engine = new PipelineEngine(
            dbFactory,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new SingleStageCatalog(stage),
            new Services.Experiments.ExperimentTracker(dbFactory),
            NullLogger<PipelineEngine>.Instance);
        return (engine, dbFactory);
    }

    private static async Task<int> SeedConfigAsync(IDbContextFactory<ApplicationDbContext> dbFactory, string stageType)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var cfg = new PipelineConfiguration
        {
            Name = $"Audit {stageType}",
            CreatedBy = "auditor",
            ExecutionMode = "Paper",
            UniverseJson = """[{"Symbol":"BTC/USDT","Timeframe":"1h"}]""",
            DateRangesJson = "{}",
            StagesJson = """[{"Type":"STAGE","Order":1,"Enabled":true,"Parameters":{}}]""".Replace("STAGE", stageType),
        };
        db.PipelineConfigurations.Add(cfg);
        await db.SaveChangesAsync();
        return cfg.Id;
    }

    /// <summary>Attende che il run in background raggiunga uno stato terminale (il motore finalizza in un Task.Run).</summary>
    private static async Task<PipelineRun> WaitForTerminalRunAsync(IDbContextFactory<ApplicationDbContext> dbFactory, Guid runId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var run = await db.PipelineRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId);
            if (run is not null && run.Status is not ("Running" or "Paused")) return run;
            await Task.Delay(150);
        }
        throw new TimeoutException("Il run di pipeline non è arrivato a uno stato terminale entro 30s.");
    }

    /// <summary>
    /// L'ExperimentRun gemello è scritto DOPO che il PipelineRun diventa terminale (finalizzazione
    /// best-effort): senza questa seconda attesa il test correrebbe contro il Task.Run del motore.
    /// </summary>
    private static async Task<List<Services.Experiments.ExperimentRun>> WaitForExperimentRunsAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, int expectedCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var runs = await db.ExperimentRuns.AsNoTracking()
                .Where(r => r.Kind == "Pipeline" && r.CompletedAt != null).ToListAsync();
            if (runs.Count >= expectedCount) return runs;
            await Task.Delay(100);
        }
        throw new TimeoutException($"ExperimentRun di pipeline non comparsi ({expectedCount} attesi) entro 15s.");
    }

    [Theory]
    [InlineData("Noop", "Completed")]
    [InlineData("Failing", "Failed")]
    public async Task PipelineRun_ComposesExperimentRun_WithParamsMetricsAndMatchingStatus(string stageType, string expectedStatus)
    {
        IPipelineStage stage = stageType == "Noop" ? new NoopStage() : new FailingStage();
        var (engine, dbFactory) = await BuildAsync(stage);
        var configId = await SeedConfigAsync(dbFactory, stageType);

        var runId = await engine.StartRunAsync(configId, "Manual", "auditor");
        var run = await WaitForTerminalRunAsync(dbFactory, runId);
        Assert.Equal(expectedStatus, run.Status);

        // Il tracker DEVE contenere un run gemello con stato identico e payload confrontabile.
        var exp = Assert.Single(await WaitForExperimentRunsAsync(dbFactory, 1));
        Assert.Equal(expectedStatus, exp.Status);
        Assert.NotNull(exp.CompletedAt);
        Assert.Equal("auditor", exp.CreatedBy);

        // Parametri: universo + modalità di esecuzione + seed (JSON camelCase, chiavi del payload).
        Assert.Contains("BTC/USDT", exp.ParametersJson);
        Assert.Contains("Paper", exp.ParametersJson);
        Assert.Contains("seed", exp.ParametersJson);
        Assert.False(string.IsNullOrEmpty(exp.ParametersHash));

        // Metriche minime di confronto: numero di stage e sopravvissuti.
        Assert.Contains("Stages", exp.MetricsJson);
        Assert.Contains("Survivors", exp.MetricsJson);

        if (expectedStatus == "Failed")
        {
            Assert.Contains("Guasto simulato", exp.ErrorLog);
        }
    }

    [Fact]
    public async Task CompletedRunWithSurvivor_LogsRichMetrics_BestProfileAndPanelPbo()
    {
        var (engine, dbFactory) = await BuildAsync(new SurvivorStage());
        var configId = await SeedConfigAsync(dbFactory, "Survivor");

        var runId = await engine.StartRunAsync(configId, "Manual", "auditor");
        var run = await WaitForTerminalRunAsync(dbFactory, runId);
        Assert.Equal("Completed", run.Status);

        var exp = Assert.Single(await WaitForExperimentRunsAsync(dbFactory, 1));
        var metrics = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(exp.MetricsJson)!;

        // Conteggi + profilo del MIGLIORE sopravvissuto (per Sharpe holdout) + PBO del pannello.
        Assert.Equal(2m, metrics["Candidates"]);
        Assert.Equal(1m, metrics["Survivors"]);
        Assert.Equal(1.5m, metrics["BestHoldoutSharpe"]);       // il sopravvissuto "Alpha", non "Beta"
        Assert.Equal(0.7m, metrics["BestDeflatedSharpe"]);
        Assert.Equal(0.12m, metrics["PanelPbo"]);
    }

    [Fact]
    public async Task TwoRuns_SameConfiguration_ProduceSameParametersHash()
    {
        // Il confronto deterministico si fonda sull'hash dei parametri: due run della STESSA
        // configurazione devono collidere sull'hash (così la UI riconosce le config identiche).
        var (engine, dbFactory) = await BuildAsync(new NoopStage());
        var configId = await SeedConfigAsync(dbFactory, "Noop");

        var run1 = await engine.StartRunAsync(configId, "Manual", "auditor");
        await WaitForTerminalRunAsync(dbFactory, run1);
        // Lo slot in-memory del motore si libera DOPO la scrittura dello stato terminale su DB:
        // un secondo avvio immediato può ancora vedere "run in corso" per qualche millisecondo.
        Guid run2 = Guid.Empty;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try { run2 = await engine.StartRunAsync(configId, "Manual", "auditor"); break; }
            catch (InvalidOperationException) { await Task.Delay(100); }
        }
        Assert.NotEqual(Guid.Empty, run2);
        await WaitForTerminalRunAsync(dbFactory, run2);

        var hashes = (await WaitForExperimentRunsAsync(dbFactory, 2)).Select(r => r.ParametersHash).ToList();
        Assert.Equal(2, hashes.Count);
        Assert.Equal(hashes[0], hashes[1]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
