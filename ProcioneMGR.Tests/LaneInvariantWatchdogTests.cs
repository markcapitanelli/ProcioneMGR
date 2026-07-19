using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Regressione della Fase 0-A3 (PRD Autonomia Operativa §3): il watchdog degli invarianti
/// contabili deve accorgersi DA SOLO dello stato in cui la corsia 2 è rimasta per ore il
/// 2026-07-18 (PnL -1,8M su capitale 10k) — quarantena persistita, trading fermato, posizioni
/// LASCIATE aperte, audit scritto — e <c>TradingEngine.StartAsync</c> deve rifiutare il riavvio
/// finché un umano non rimuove la quarantena (che un riavvio azzererebbe capitale/PnL,
/// cancellando l'evidenza).
/// </summary>
[Collection("Postgres")]
public sealed class LaneInvariantWatchdogTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public LaneInvariantWatchdogTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    // --- Fakes -------------------------------------------------------------------------------

    /// <summary>Solo StopAsync è lecito: il watchdog non deve MAI chiudere posizioni o fare altro.</summary>
    private sealed class StopOnlyEngine(int laneId) : ITradingEngine
    {
        public int LaneId => laneId;
        public int StopCalls { get; private set; }

        public Task StopAsync(CancellationToken ct = default) { StopCalls++; return Task.CompletedTask; }

        public Task StartAsync(TradingMode mode, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
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

    private sealed class HoldStrategy : IStrategy
    {
        public string Name => "Hold";
        public string DisplayName => "Hold";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];
        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct) => Task.CompletedTask;
        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp) => Signal.Hold;
    }

    private sealed class HoldStrategyFactory : IStrategyFactory
    {
        public IReadOnlyList<IStrategy> Prototypes => [];
        public IStrategy Create(string strategyName) => new HoldStrategy();
    }

    private sealed class FakeEnsembleManager(EnsembleConfiguration config) : IEnsembleManager
    {
        public int LaneId => 0;
        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default) => Task.FromResult(config);
        public Task UpdateConfigurationAsync(EnsembleConfiguration c, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProcioneMGR.Services.Monitoring.DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class ThrowingExchangeFactory : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => throw new NotImplementedException();
        public IExchangeClient Create(string exchangeName) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotImplementedException();
    }

    // --- Setup -------------------------------------------------------------------------------

    private async Task<(LaneInvariantWatchdog Watchdog, IDbContextFactory<ApplicationDbContext> DbFactory,
        LaneQuarantineStore Store, StopOnlyEngine[] Engines)> BuildAsync(LaneInvariantOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var engines = new StopOnlyEngine[TradingLanes.Count];
        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            engines[lane] = new StopOnlyEngine(lane);
            services.AddKeyedSingleton<ITradingEngine>(lane, engines[lane]);
        }
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var store = new LaneQuarantineStore(dbFactory, NullLogger<LaneQuarantineStore>.Instance);
        var watchdog = new LaneInvariantWatchdog(
            provider, dbFactory, store,
            (options ?? new LaneInvariantOptions()).AsMonitor(),
            NullLogger<LaneInvariantWatchdog>.Instance);
        return (watchdog, dbFactory, store, engines);
    }

    /// <summary>Lo stato REALE della corsia 2 del 2026-07-18 (docs/TEST-UI-2026-07-18.md).</summary>
    private static TradingEngineState CorruptedCorsia2State() => new()
    {
        LaneId = 2,
        Mode = TradingMode.Testnet,
        MarketType = MarketType.Futures,
        Leverage = 2,
        IsRunning = true,
        ExchangeName = "Binance",
        Symbol = "ETH/USDT",
        TotalCapital = 10_000m,
        AvailableCapital = -1_807_925.81m,
        RealizedPnl = -1_817_925.81m,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static TradingEngineState HealthyRunningState(int laneId) => new()
    {
        LaneId = laneId,
        Mode = TradingMode.Paper,
        IsRunning = true,
        Leverage = 1,
        TotalCapital = 10_000m,
        AvailableCapital = 9_500m,
        RealizedPnl = 120m,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static async Task SeedStateAsync(IDbContextFactory<ApplicationDbContext> dbFactory, params TradingEngineState[] states)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.TradingEngineStates.AddRange(states);
        await db.SaveChangesAsync();
    }

    // --- Test: il caso reale della corsia 2 --------------------------------------------------

    [Fact]
    public async Task Tick_RealCorsia2State_QuarantinesLane_StopsEngine_LeavesPositionsOpen()
    {
        var (watchdog, dbFactory, _, engines) = await BuildAsync();
        await SeedStateAsync(dbFactory, HealthyRunningState(0), CorruptedCorsia2State());
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            // La posizione ETH adottata dal fill patologico: deve restare APERTA (mai chiusure forzate).
            db.OpenPositions.Add(new OpenPosition
            {
                LaneId = 2, Symbol = "ETH/USDT", Quantity = 1_039.77125m, EntryPrice = 1_748.18m,
                CurrentPrice = 1_748.18m, OpenedInMode = TradingMode.Testnet, OpenedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await watchdog.TickAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        var quarantine = Assert.Single(await check.LaneQuarantines.AsNoTracking().ToListAsync());
        Assert.Equal(2, quarantine.LaneId);
        Assert.Contains("AvailableCapital negativo", quarantine.Reason);
        Assert.Contains("PnL totale", quarantine.Reason);
        Assert.Contains("Nozionale aperto fuori scala", quarantine.Reason);

        var audit = Assert.Single(await check.TradingAuditLogs.AsNoTracking().Where(a => a.Action == "LaneQuarantined").ToListAsync());
        Assert.Equal(2, audit.LaneId);
        Assert.Equal(TradingMode.Testnet, audit.Mode);

        Assert.Equal(1, engines[2].StopCalls);   // trading fermato...
        Assert.Equal(0, engines[0].StopCalls);   // ...solo della corsia violata
        Assert.Equal(1, await check.OpenPositions.CountAsync()); // posizioni MAI chiuse dal watchdog
    }

    [Fact]
    public async Task Tick_HealthyAndStoppedLanes_NoAction()
    {
        var (watchdog, dbFactory, _, engines) = await BuildAsync();
        var stoppedCorrupted = CorruptedCorsia2State();
        stoppedCorrupted.IsRunning = false; // corsia ferma: si azzera al prossimo StartAsync, non si quarantena
        await SeedStateAsync(dbFactory, HealthyRunningState(0), stoppedCorrupted);

        await watchdog.TickAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.Empty(await check.LaneQuarantines.AsNoTracking().ToListAsync());
        Assert.All(engines, e => Assert.Equal(0, e.StopCalls));
    }

    [Fact]
    public async Task Tick_SecondPass_DoesNotDuplicateQuarantineOrStop()
    {
        var (watchdog, dbFactory, _, engines) = await BuildAsync();
        await SeedStateAsync(dbFactory, CorruptedCorsia2State());

        await watchdog.TickAsync(CancellationToken.None);
        await watchdog.TickAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.Single(await check.LaneQuarantines.AsNoTracking().ToListAsync());
        Assert.Single(await check.TradingAuditLogs.AsNoTracking().Where(a => a.Action == "LaneQuarantined").ToListAsync());
        Assert.Equal(1, engines[2].StopCalls);
    }

    [Fact]
    public async Task Tick_Disabled_NoActionEvenOnCorruptedLane()
    {
        var (watchdog, dbFactory, _, engines) = await BuildAsync(new LaneInvariantOptions { Enabled = false });
        await SeedStateAsync(dbFactory, CorruptedCorsia2State());

        await watchdog.TickAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.Empty(await check.LaneQuarantines.AsNoTracking().ToListAsync());
        Assert.Equal(0, engines[2].StopCalls);
    }

    [Fact]
    public async Task Tick_PositionsOfOtherMode_NotCountedInExposure()
    {
        // Filtro M2: una riga Paper residua su una corsia Testnet non deve quarantenare la corsia
        // (il motore stesso non la vede — EnsureLoadedAsync la purgherebbe).
        var (watchdog, dbFactory, _, engines) = await BuildAsync();
        var state = HealthyRunningState(1);
        state.Mode = TradingMode.Testnet;
        await SeedStateAsync(dbFactory, state);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.OpenPositions.Add(new OpenPosition
            {
                LaneId = 1, Symbol = "BTC/USDT", Quantity = 1_000m, EntryPrice = 100_000m,
                CurrentPrice = 100_000m, OpenedInMode = TradingMode.Paper, OpenedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await watchdog.TickAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.Empty(await check.LaneQuarantines.AsNoTracking().ToListAsync());
        Assert.Equal(0, engines[1].StopCalls);
    }

    // --- Test: StartAsync rifiuta una corsia in quarantena ------------------------------------

    [Fact]
    public async Task StartAsync_QuarantinedLane_Refuses_UntilHumanClears()
    {
        var (_, dbFactory, store, _) = await BuildAsync();
        await store.TryQuarantineAsync(0, "AvailableCapital negativo: -1807925.81", "{}");

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 10_000m,
            Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "Hold", DisplayName = "Hold", IsActive = true }],
        };
        var engine = new TradingEngine(
            0, dbFactory, new HoldStrategyFactory(), new TechnicalIndicatorsService(),
            new ThrowingExchangeFactory(), new FakeEnsembleManager(config),
            new SafetyConfiguration { PositionSizePercent = 8m, MaxPositionSizePercent = 50m, MaxTotalExposurePercent = 100m }.AsMonitor(),
            new LiveExecutionOptions().AsMonitor(),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(TradingMode.Paper));
        Assert.Contains("QUARANTENA", ex.Message);

        // L'audit della rimozione porta il nome di chi decide.
        Assert.True(await store.ClearAsync(0, "admin-user"));
        await using (var check = await dbFactory.CreateDbContextAsync())
        {
            var cleared = Assert.Single(await check.TradingAuditLogs.AsNoTracking()
                .Where(a => a.Action == "LaneQuarantineCleared").ToListAsync());
            Assert.Equal("admin-user", cleared.UserId);
        }

        // Rimossa la quarantena, la corsia riparte normalmente.
        await engine.StartAsync(TradingMode.Paper);
        var status = await engine.GetStatusAsync();
        Assert.True(status.IsRunning);
    }
}
