using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica che le corsie di trading (LaneId) siano isolate a livello dati pur condividendo lo
/// stesso database (colonna discriminante LaneId invece di DbContext separati - vedi
/// docs/REPORT-MULTI-LANE.md): operazioni su una corsia non devono mai leggere, scrivere o
/// cancellare i dati di un'altra corsia.
/// </summary>
[Collection("Postgres")]
public class MultiLaneIsolationTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public MultiLaneIsolationTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class UnusedRegimeDetector : IRegimeDetector
    {
        public Task<RegimeModel> TrainAsync(TrainingConfiguration config, bool activate = true, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ActivateModelAsync(RegimeModel model, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MarketFeatures>> LabelFeaturesAsync(List<MarketFeatures> features, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RegimeModel?> LoadLatestModelAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class UnusedFeatureExtractor : IMarketFeatureExtractor
    {
        public Task<List<MarketFeatures>> ExtractFeaturesAsync(string exchangeName, string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class ScriptedStrategy(Func<int, Signal> script) : IStrategy
    {
        public string Name => "Scripted";
        public string DisplayName => "Scripted";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];
        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct)
            => Task.CompletedTask;
        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp) => script(index);
    }

    private sealed class ScriptedStrategyFactory(Func<int, Signal> script) : IStrategyFactory
    {
        public IReadOnlyList<IStrategy> Prototypes => [];
        public IStrategy Create(string strategyName) => new ScriptedStrategy(script);
    }

    private sealed class FakeEnsembleManager(int laneId, EnsembleConfiguration config) : IEnsembleManager
    {
        public int LaneId => laneId;
        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default) => Task.FromResult(config);
        public Task UpdateConfigurationAsync(EnsembleConfiguration c, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>Mai invocata in Paper mode (TradingEngine non tocca l'exchange): fallisce rumorosamente se qualcosa cambia.</summary>
    private sealed class ThrowingExchangeClientFactory : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => throw new NotImplementedException();
        public IExchangeClient Create(string exchangeName) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotImplementedException();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;
        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> BuildDbAsync()
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
        return dbFactory;
    }

    private static TradingEngine BuildEngine(int laneId, IDbContextFactory<ApplicationDbContext> dbFactory, EnsembleStrategy strategy, Func<int, Signal> script) =>
        new(
            laneId,
            dbFactory,
            new ScriptedStrategyFactory(script),
            new TechnicalIndicatorsService(),
            new ThrowingExchangeClientFactory(),
            new FakeEnsembleManager(laneId, new EnsembleConfiguration
            {
                ExchangeName = "Binance",
                Symbol = "BTC/USDT",
                Timeframe = "1h",
                TotalCapital = 10_000m,
                Strategies = [strategy],
            }),
            new StaticOptionsMonitor<SafetyConfiguration>(new SafetyConfiguration()),
            new StaticOptionsMonitor<ProcioneMGR.Services.Trading.LiveExecutionOptions>(new ProcioneMGR.Services.Trading.LiveExecutionOptions()),
            new ProcioneMGR.Services.Execution.ExecutionAlgorithmFactory(),
            NullLogger<TradingEngine>.Instance);

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT",
        Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close,
        High = close,
        Low = close,
        Close = close,
        Volume = 100m,
    };

    private static async Task WarmUpAsync(TradingEngine engine, decimal price = 100m)
    {
        for (var i = 0; i < 4; i++) await engine.ProcessCandleAsync(Candle(i, price));
    }

    [Fact]
    public async Task OpenPosition_OnOneLane_NotVisibleOnAnotherLane()
    {
        var dbFactory = await BuildDbAsync();
        var strat = new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true };

        var lane0 = BuildEngine(0, dbFactory, strat, i => i == 4 ? Signal.Long : Signal.Hold);
        var lane1 = BuildEngine(1, dbFactory, strat, _ => Signal.Hold);

        await lane0.StartAsync(TradingMode.Paper);
        await lane1.StartAsync(TradingMode.Paper);

        await WarmUpAsync(lane0);
        await lane0.ProcessCandleAsync(Candle(4, 100m)); // apre Long solo su lane0

        Assert.Single(await lane0.GetOpenPositionsAsync());
        Assert.Empty(await lane1.GetOpenPositionsAsync());
    }

    [Fact]
    public async Task StartAsync_Paper_OnlyWipesOwnLanePositions()
    {
        var dbFactory = await BuildDbAsync();
        var strat = new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true };

        var lane0 = BuildEngine(0, dbFactory, strat, i => i == 4 ? Signal.Long : Signal.Hold);
        var lane1 = BuildEngine(1, dbFactory, strat, i => i == 4 ? Signal.Long : Signal.Hold);

        await lane0.StartAsync(TradingMode.Paper);
        await lane1.StartAsync(TradingMode.Paper);
        await WarmUpAsync(lane0); await lane0.ProcessCandleAsync(Candle(4, 100m));
        await WarmUpAsync(lane1); await lane1.ProcessCandleAsync(Candle(4, 100m));

        Assert.Single(await lane0.GetOpenPositionsAsync());
        Assert.Single(await lane1.GetOpenPositionsAsync());

        // Riavvio Paper su lane0 deve azzerare SOLO le posizioni di lane0: prima del fix di
        // questa fase, ExecuteDeleteAsync su OpenPositions non filtrava per LaneId e avrebbe
        // distrutto anche le posizioni di lane1.
        await lane0.StartAsync(TradingMode.Paper);

        Assert.Empty(await lane0.GetOpenPositionsAsync());
        Assert.Single(await lane1.GetOpenPositionsAsync());
    }

    [Fact]
    public async Task TradeHistoryAndPerformance_AreIsolatedPerLane()
    {
        var dbFactory = await BuildDbAsync();
        var strat = new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true };

        // lane0: apre Long a 100 (indice 4), lo chiude con segnale Short a 105 (indice 5) -> 1 trade chiuso.
        var lane0 = BuildEngine(0, dbFactory, strat, i => i == 4 ? Signal.Long : i == 5 ? Signal.Short : Signal.Hold);
        var lane1 = BuildEngine(1, dbFactory, strat, _ => Signal.Hold); // lane1 non fa mai trade

        await lane0.StartAsync(TradingMode.Paper);
        await lane1.StartAsync(TradingMode.Paper);

        await WarmUpAsync(lane0);
        await lane0.ProcessCandleAsync(Candle(4, 100m));
        await lane0.ProcessCandleAsync(Candle(5, 105m));

        var perf0 = await lane0.GetPerformanceAsync();
        var perf1 = await lane1.GetPerformanceAsync();
        Assert.True(perf0.TotalTrades > 0, "lane0 deve avere almeno un trade chiuso");
        Assert.Equal(0, perf1.TotalTrades);

        var orders0 = await lane0.GetOrderHistoryAsync();
        var orders1 = await lane1.GetOrderHistoryAsync();
        Assert.NotEmpty(orders0);
        Assert.Empty(orders1);
    }

    [Fact]
    public async Task EnsembleManager_Configuration_IsIsolatedPerLane()
    {
        await BuildDbAsync();
        var scopeFactory = _provider!.GetRequiredService<IServiceScopeFactory>();

        var mgr0 = new EnsembleManager(0, scopeFactory, new UnusedRegimeDetector(), new UnusedFeatureExtractor(), new StrategyDecayMonitor(), NullLogger<EnsembleManager>.Instance);
        var mgr1 = new EnsembleManager(1, scopeFactory, new UnusedRegimeDetector(), new UnusedFeatureExtractor(), new StrategyDecayMonitor(), NullLogger<EnsembleManager>.Instance);

        var cfg0 = await mgr0.GetConfigurationAsync();
        cfg0.Symbol = "BTC/USDT";
        cfg0.Strategies = [new EnsembleStrategy { StrategyId = "a", StrategyName = "RsiOversold", DisplayName = "A", IsActive = true }];
        await mgr0.UpdateConfigurationAsync(cfg0);

        var cfg1 = await mgr1.GetConfigurationAsync();
        cfg1.Symbol = "ETH/USDT";
        cfg1.Strategies = [];
        await mgr1.UpdateConfigurationAsync(cfg1);

        var reloaded0 = await mgr0.GetConfigurationAsync();
        var reloaded1 = await mgr1.GetConfigurationAsync();

        Assert.Equal("BTC/USDT", reloaded0.Symbol);
        Assert.Single(reloaded0.Strategies);
        Assert.Equal("ETH/USDT", reloaded1.Symbol);
        Assert.Empty(reloaded1.Strategies);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
