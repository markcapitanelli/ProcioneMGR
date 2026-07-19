using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del Campaign Planner (Fase 1, PRD Autonomia Operativa §4) con motore pipeline FAKE
/// (stesso approccio dei PipelineSchedulerWorkerTests): rotazione su 0 sopravvissuti, backoff,
/// stop-su-successo (Observing + avvio corsie Paper), rotazione-esaurita → WaitingForTrigger,
/// ripresa-su-wake con trigger "Event", gate globale e per-campagna, slot singolo occupato.
/// </summary>
[Collection("Postgres")]
public sealed class CampaignPlannerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public CampaignPlannerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    // --- Fakes -------------------------------------------------------------------------------

    private sealed class ScriptedPipelineEngine : IPipelineEngine
    {
        public List<(int ConfigId, string Trigger, string? UserId)> Started { get; } = new();
        public bool SlotBusy { get; set; }
        public Guid NextRunId { get; set; } = Guid.NewGuid();

        public Task<Guid> StartRunAsync(int configurationId, string trigger = "Manual", string? userId = null, CancellationToken ct = default)
        {
            if (SlotBusy) throw new InvalidOperationException("Un run è già in corso.");
            Started.Add((configurationId, trigger, userId));
            return Task.FromResult(NextRunId);
        }

        public Task<Guid> ResumeRunAsync(Guid runId, string? userId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public void RequestPause(Guid runId) => throw new NotImplementedException();
        public void Cancel(Guid runId) => throw new NotImplementedException();
        public PipelineLiveStatus? GetLiveStatus() => null;
        public List<string> ValidateConfiguration(IReadOnlyList<StageConfig> stages) => [];
        public Task<int> RecoverOrphanedRunsAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class ScriptedApplyEvaluator : IRunApplyEvaluator
    {
        public RunApplyOutcome Outcome { get; set; } = new() { HadCandidate = true, Applied = true, LanesUsed = 1, Message = "applicato (fake)" };
        public List<Guid> Evaluated { get; } = new();

        public Task<RunApplyOutcome> EvaluateAndMaybeApplyAsync(Guid runId, CancellationToken ct = default)
        {
            Evaluated.Add(runId);
            return Task.FromResult(Outcome);
        }
    }

    /// <summary>Motore corsia fake: registra l'avvio Paper, può fingersi già in esecuzione o in quarantena.</summary>
    private sealed class RecordingLaneEngine(int laneId) : ITradingEngine
    {
        public int LaneId => laneId;
        public bool IsRunning { get; set; }
        public bool ThrowOnStart { get; set; }
        public TradingMode? StartedWith { get; private set; }

        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new TradingEngineStatus { IsRunning = IsRunning, Mode = TradingMode.Paper });
        public Task StartAsync(TradingMode mode, CancellationToken ct = default)
        {
            if (ThrowOnStart) throw new InvalidOperationException("Corsia 0 in QUARANTENA (fake).");
            StartedWith = mode;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    // --- Setup -------------------------------------------------------------------------------

    private async Task<(CampaignPlanner Planner, ScriptedPipelineEngine Engine, ScriptedApplyEvaluator Evaluator,
        IDbContextFactory<ApplicationDbContext> DbFactory, RecordingLaneEngine[] Lanes)> BuildAsync(bool enabled = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var lanes = new RecordingLaneEngine[TradingLanes.Count];
        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            lanes[lane] = new RecordingLaneEngine(lane);
            services.AddKeyedSingleton<ITradingEngine>(lane, lanes[lane]);
        }
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var engine = new ScriptedPipelineEngine();
        var evaluator = new ScriptedApplyEvaluator();
        var planner = new CampaignPlanner(
            dbFactory, engine, evaluator, provider,
            new CampaignOptions { Enabled = enabled }.AsMonitor(),
            NullLogger<CampaignPlanner>.Instance);
        return (planner, engine, evaluator, dbFactory, lanes);
    }

    private static async Task<(int CampaignId, int Config1, int Config2)> SeedCampaignAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, bool campaignEnabled = true, int backoffHours = 12,
        bool autoStartPaper = true, string executionMode1 = "Paper")
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var cfg1 = new PipelineConfiguration { Name = "Caccia A", CreatedBy = "u1", ExecutionMode = executionMode1 };
        var cfg2 = new PipelineConfiguration { Name = "Caccia B", CreatedBy = "u1", ExecutionMode = "Paper" };
        db.PipelineConfigurations.AddRange(cfg1, cfg2);
        await db.SaveChangesAsync();

        var campaign = new VettingCampaign
        {
            Name = "Test",
            CreatedBy = "u1",
            Enabled = campaignEnabled,
            BackoffHours = backoffHours,
            AutoStartPaperLanes = autoStartPaper,
            ConfigStatesJson = CampaignPlanner.SerializeConfigStates(
            [
                new CampaignConfigState { ConfigurationId = cfg1.Id },
                new CampaignConfigState { ConfigurationId = cfg2.Id },
            ]),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        db.VettingCampaigns.Add(campaign);
        await db.SaveChangesAsync();
        return (campaign.Id, cfg1.Id, cfg2.Id);
    }

    private static async Task<VettingCampaign> LoadAsync(IDbContextFactory<ApplicationDbContext> dbFactory, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.VettingCampaigns.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private static async Task CompletePendingRunAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, int campaignId, int survivors, string status = "Completed")
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var campaign = await db.VettingCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        var recommendation = new PipelineRecommendation();
        for (var i = 0; i < survivors; i++)
        {
            recommendation.EnsembleLegs.Add(new ProposedLeg
            {
                StrategyName = "RsiOversold", DisplayName = $"Leg {i}", Symbol = "BTC/USDT", Timeframe = "1h",
                WeightPercent = 100m / Math.Max(1, survivors), HoldoutSharpe = 1.2m,
            });
        }
        db.PipelineRuns.Add(new PipelineRun
        {
            Id = campaign.PendingRunId!.Value,
            ConfigurationId = 0,
            StartedAt = DateTime.UtcNow.AddMinutes(-30),
            CompletedAt = status == "Completed" ? DateTime.UtcNow : null,
            Status = status,
            Trigger = "Campaign",
            RecommendationJson = JsonSerializer.Serialize(recommendation),
        });
        await db.SaveChangesAsync();
    }

    // --- Gate --------------------------------------------------------------------------------

    [Fact]
    public async Task Tick_GlobalGateOff_DoesNothing()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync(enabled: false);
        await SeedCampaignAsync(dbFactory);

        await planner.TickAsync();

        Assert.Empty(engine.Started);
    }

    [Fact]
    public async Task Tick_CampaignDisabled_DoesNothing()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        await SeedCampaignAsync(dbFactory, campaignEnabled: false);

        await planner.TickAsync();

        Assert.Empty(engine.Started);
    }

    // --- Rotazione ---------------------------------------------------------------------------

    [Fact]
    public async Task Tick_StartsFirstConfig_WithCampaignTrigger_AndSetsPending()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, config1, _) = await SeedCampaignAsync(dbFactory);

        await planner.TickAsync();

        var started = Assert.Single(engine.Started);
        Assert.Equal(config1, started.ConfigId);
        Assert.Equal("Campaign", started.Trigger);
        Assert.Equal("u1", started.UserId);

        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Equal(engine.NextRunId, campaign.PendingRunId);
        var state = CampaignPlanner.ParseConfigStates(campaign.ConfigStatesJson).Single(s => s.ConfigurationId == config1);
        Assert.Equal(1, state.Attempts);
        Assert.Equal(engine.NextRunId, state.LastRunId);
    }

    [Fact]
    public async Task Tick_PendingRunStillRunning_Waits()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0, status: "Running");

        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();

        Assert.Single(engine.Started); // nessun secondo run
        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.NotNull(campaign.PendingRunId);
    }

    [Fact]
    public async Task NoSurvivors_RotatesToNextConfig_ThenExhaustion_WaitsForTrigger()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, config1, config2) = await SeedCampaignAsync(dbFactory);

        // Run 1 (config A): 0 sopravvissuti.
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0);
        await planner.TickAsync(); // valuta l'esito, libera il pending
        var afterFirst = await LoadAsync(dbFactory, campaignId);
        Assert.Null(afterFirst.PendingRunId);
        Assert.Equal(CampaignStatus.Rotating, afterFirst.Status);
        Assert.Equal("NoSurvivors", CampaignPlanner.ParseConfigStates(afterFirst.ConfigStatesJson)
            .Single(s => s.ConfigurationId == config1).LastOutcome);

        // Run 2 (config B, la A è in backoff): 0 sopravvissuti.
        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();
        Assert.Equal(config2, engine.Started[1].ConfigId);
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0);
        await planner.TickAsync();

        // Rotazione esaurita (entrambe in backoff 12h) → in attesa di trigger.
        await planner.TickAsync();
        var exhausted = await LoadAsync(dbFactory, campaignId);
        Assert.Equal(CampaignStatus.WaitingForTrigger, exhausted.Status);
        Assert.Equal(2, engine.Started.Count); // nessun terzo run
    }

    [Fact]
    public async Task Survivors_Applied_StopsRotation_Observing_StartsOnlyStoppedPaperLanes()
    {
        var (planner, engine, evaluator, dbFactory, lanes) = await BuildAsync();
        var (campaignId, config1, _) = await SeedCampaignAsync(dbFactory);
        evaluator.Outcome = new RunApplyOutcome { HadCandidate = true, Applied = true, LanesUsed = 2, Message = "ok" };
        lanes[0].IsRunning = true; // corsia 0 già in esecuzione: NON va toccata

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 3);
        await planner.TickAsync();

        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Equal(CampaignStatus.Observing, campaign.Status);
        Assert.Null(campaign.PendingRunId);
        Assert.Equal("Applied", CampaignPlanner.ParseConfigStates(campaign.ConfigStatesJson)
            .Single(s => s.ConfigurationId == config1).LastOutcome);
        Assert.Single(evaluator.Evaluated);

        Assert.Null(lanes[0].StartedWith);                    // già running: mai riavviata
        Assert.Equal(TradingMode.Paper, lanes[1].StartedWith); // avviata in Paper
        Assert.Null(lanes[2].StartedWith);                    // fuori da LanesUsed

        // In osservazione: nessun nuovo run ai tick successivi.
        await planner.TickAsync();
        Assert.Single(engine.Started);
    }

    [Fact]
    public async Task Survivors_QuarantinedLane_ApplyProceeds_StartFailureIsNotFatal()
    {
        var (planner, _, evaluator, dbFactory, lanes) = await BuildAsync();
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);
        evaluator.Outcome = new RunApplyOutcome { HadCandidate = true, Applied = true, LanesUsed = 1, Message = "ok" };
        lanes[0].ThrowOnStart = true; // es. quarantena Fase 0-A3

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 1);
        await planner.TickAsync();

        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Equal(CampaignStatus.Observing, campaign.Status); // la campagna non muore per l'avvio fallito
        Assert.Null(lanes[0].StartedWith);
    }

    [Fact]
    public async Task Survivors_NotApplied_RotationContinues()
    {
        var (planner, engine, evaluator, dbFactory, lanes) = await BuildAsync();
        var (campaignId, config1, config2) = await SeedCampaignAsync(dbFactory);
        evaluator.Outcome = new RunApplyOutcome { HadCandidate = true, Applied = false, Vetoed = true, Message = "VETO del supervisore AI." };

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 2);
        await planner.TickAsync();

        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Equal(CampaignStatus.Rotating, campaign.Status); // scostamento documentato dal PRD: senza schieramento la caccia continua
        Assert.Equal("NotApplied", CampaignPlanner.ParseConfigStates(campaign.ConfigStatesJson)
            .Single(s => s.ConfigurationId == config1).LastOutcome);
        Assert.All(lanes, l => Assert.Null(l.StartedWith));

        // Il giro dopo parte la config B.
        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();
        Assert.Equal(config2, engine.Started[1].ConfigId);
    }

    [Fact]
    public async Task FailedRun_MarksConfig_AndRotationContinues()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, config1, config2) = await SeedCampaignAsync(dbFactory);

        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0, status: "Failed");
        await planner.TickAsync();

        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Null(campaign.PendingRunId);
        Assert.Equal("Failed", CampaignPlanner.ParseConfigStates(campaign.ConfigStatesJson)
            .Single(s => s.ConfigurationId == config1).LastOutcome);

        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();
        Assert.Equal(config2, engine.Started[1].ConfigId);
    }

    // --- Wake (trigger contestuale / operatore) ------------------------------------------------

    [Fact]
    public async Task Wake_ResumesWaitingCampaign_NextRunHasEventTrigger_AndBypassesBackoff()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, config1, config2) = await SeedCampaignAsync(dbFactory);

        // Esaurisce la rotazione (2 config, 0 sopravvissuti ciascuna).
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0);
        await planner.TickAsync();
        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 0);
        await planner.TickAsync();
        await planner.TickAsync();
        Assert.Equal(CampaignStatus.WaitingForTrigger, (await LoadAsync(dbFactory, campaignId)).Status);

        // Il trigger contestuale (Fase 2) sveglia il planner.
        var woken = await planner.WakeAsync("Cambio regime K-means: cluster 2 → 0");
        Assert.Equal(1, woken);

        engine.NextRunId = Guid.NewGuid();
        await planner.TickAsync();

        var lastStart = engine.Started[^1];
        Assert.Equal("Event", lastStart.Trigger);           // run visibile con ⚡ nello storico
        Assert.Equal(config1, lastStart.ConfigId);          // round-robin: si riparte dalla successiva all'ultima (B) → A
        var campaign = await LoadAsync(dbFactory, campaignId);
        Assert.Equal(CampaignStatus.Rotating, campaign.Status);
        Assert.Null(campaign.PendingWakeReason);            // consumato
        Assert.NotNull(campaign.PendingRunId);
        _ = config2;
    }

    [Fact]
    public async Task Wake_DoesNotTouchObservingCampaigns()
    {
        var (planner, _, evaluator, dbFactory, _) = await BuildAsync();
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);
        evaluator.Outcome = new RunApplyOutcome { HadCandidate = true, Applied = true, LanesUsed = 1, Message = "ok" };
        await planner.TickAsync();
        await CompletePendingRunAsync(dbFactory, campaignId, survivors: 1);
        await planner.TickAsync();
        Assert.Equal(CampaignStatus.Observing, (await LoadAsync(dbFactory, campaignId)).Status);

        var woken = await planner.WakeAsync("Cambio regime");

        Assert.Equal(0, woken);
        Assert.Equal(CampaignStatus.Observing, (await LoadAsync(dbFactory, campaignId)).Status);
    }

    // --- Difese ------------------------------------------------------------------------------

    [Fact]
    public async Task SlotBusy_RetriesOnNextTick()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (campaignId, _, _) = await SeedCampaignAsync(dbFactory);
        engine.SlotBusy = true;

        await planner.TickAsync();
        Assert.Empty(engine.Started);
        Assert.Null((await LoadAsync(dbFactory, campaignId)).PendingRunId);

        engine.SlotBusy = false;
        await planner.TickAsync();
        Assert.Single(engine.Started);
    }

    [Fact]
    public async Task LiveConfig_IsSkipped_NextConfigUsed()
    {
        var (planner, engine, _, dbFactory, _) = await BuildAsync();
        var (_, _, config2) = await SeedCampaignAsync(dbFactory, executionMode1: "Live");

        await planner.TickAsync();

        var started = Assert.Single(engine.Started);
        Assert.Equal(config2, started.ConfigId); // la config Live non parte MAI da un automatismo
    }
}
