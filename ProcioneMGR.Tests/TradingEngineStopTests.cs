using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del wiring automatico stop-loss/take-profit/trailing dal backtest (via EnsembleStrategy)
/// al TradingEngine live: applicazione all'apertura, priorità della modifica manuale, e
/// comportamento causale del trailing (livello calcolato sul best-since-entry PRIMA della
/// candela corrente, come nel motore di backtest — vedi BacktestEngineTests/BacktestStopLossTests).
/// </summary>
[Collection("Postgres")]
public class TradingEngineStopTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public TradingEngineStopTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    /// <summary>Segnale scriptato per indice di candela: deterministico, nessun bisogno di tarare un indicatore reale.</summary>
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

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<(TradingEngine Engine, IDbContextFactory<ApplicationDbContext> DbFactory)> BuildAsync(
        EnsembleStrategy strategy, Func<int, Signal> script)
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

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance",
            Symbol = "BTC/USDT",
            Timeframe = "1h",
            TotalCapital = 10_000m,
            Strategies = [strategy],
        };

        var engine = new TradingEngine(
            0,
            dbFactory,
            new ScriptedStrategyFactory(script),
            new TechnicalIndicatorsService(),
            new ThrowingExchangeClientFactory(),
            new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(new SafetyConfiguration()),
            new StaticOptionsMonitor<ProcioneMGR.Services.Trading.LiveExecutionOptions>(new ProcioneMGR.Services.Trading.LiveExecutionOptions()),
            new ProcioneMGR.Services.Execution.ExecutionAlgorithmFactory(),
            NullLogger<TradingEngine>.Instance);

        return (engine, dbFactory);
    }

    private static OhlcvData Candle(int i, decimal close) => Candle(i, close, close, close, close);

    private static OhlcvData Candle(int i, decimal open, decimal high, decimal low, decimal close) => new()
    {
        Symbol = "BTC/USDT",
        Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = 100m,
    };

    /// <summary>Le prime 4 candele riempiono solo il buffer (closes.Count &lt; 5): nessuna valutazione di segnale.
    /// La 5ª (indice 4) è la prima valutata, e lo script apre Long lì.</summary>
    private static async Task WarmUpAsync(TradingEngine engine, decimal price = 100m)
    {
        for (var i = 0; i < 4; i++) await engine.ProcessCandleAsync(Candle(i, price));
    }

    [Fact]
    public async Task AutoStopLoss_AppliedAtOpen_ClosesPositionWhenHit()
    {
        var strat = new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true, StopLossPercent = 5m };
        var (engine, _) = await BuildAsync(strat, i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);

        await WarmUpAsync(engine);
        await engine.ProcessCandleAsync(Candle(4, 100m)); // apertura Long a 100

        var positions = await engine.GetOpenPositionsAsync();
        Assert.Single(positions);
        Assert.Equal(95m, positions[0].StopLoss); // 100 * (1 - 5%), applicato SENZA alcuna chiamata manuale

        await engine.ProcessCandleAsync(Candle(5, 94m)); // sotto lo stop -> chiusura automatica
        Assert.Empty(await engine.GetOpenPositionsAsync());
    }

    [Fact]
    public async Task StopLoss_TriggersIntrabar_WhenWickPiercesButCloseIsAbove()
    {
        // P0-5: lo stop live è controllato su High/Low della candela chiusa (come il backtest), non solo
        // sulla Close. Un wick che buca lo stop chiude la posizione anche se la candela chiude più in alto.
        var strat = new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true, StopLossPercent = 5m };
        var (engine, _) = await BuildAsync(strat, i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);

        await WarmUpAsync(engine);
        await engine.ProcessCandleAsync(Candle(4, 100m)); // Long a 100 -> stop 95
        Assert.Equal(95m, (await engine.GetOpenPositionsAsync()).Single().StopLoss);

        // Close 97 (sopra lo stop) ma Low 94 (il wick buca lo stop 95): col vecchio controllo solo-Close
        // sarebbe rimasta aperta; ora scatta intrabar.
        await engine.ProcessCandleAsync(Candle(5, open: 97m, high: 98m, low: 94m, close: 97m));
        Assert.Empty(await engine.GetOpenPositionsAsync());
    }

    [Fact]
    public async Task ManualStopLoss_TakesPriorityOverAutomaticOne()
    {
        var strat = new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true, StopLossPercent = 5m };
        var (engine, _) = await BuildAsync(strat, i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);

        await WarmUpAsync(engine);
        await engine.ProcessCandleAsync(Candle(4, 100m));

        var pos = (await engine.GetOpenPositionsAsync()).Single();
        Assert.Equal(95m, pos.StopLoss);

        // L'operatore allarga manualmente lo stop a 90: da qui in poi vince la scelta manuale.
        await engine.SetStopLossTakeProfitAsync(pos.PositionId, 90m, null);

        // 94 avrebbe fatto scattare lo stop AUTOMATICO (95) ma non quello manuale (90): resta aperta.
        await engine.ProcessCandleAsync(Candle(5, 94m));
        Assert.Single(await engine.GetOpenPositionsAsync());

        // 89 rompe anche lo stop manuale: si chiude.
        await engine.ProcessCandleAsync(Candle(6, 89m));
        Assert.Empty(await engine.GetOpenPositionsAsync());
    }

    [Fact]
    public async Task TrailingStop_RatchetsUpAndClosesOnPullback()
    {
        var strat = new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true, TrailingStopPercent = 5m };
        var (engine, _) = await BuildAsync(strat, i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);

        await WarmUpAsync(engine);
        await engine.ProcessCandleAsync(Candle(4, 100m)); // entry a 100

        var pos = (await engine.GetOpenPositionsAsync()).Single();
        Assert.Equal(5m, pos.TrailingStopPercent);
        Assert.Equal(100m, pos.BestPriceSinceEntry);

        // Il prezzo sale a 120: il trail level per la PROSSIMA candela sarà 120*0.95=114, ma questa
        // candela stessa (120) non può far scattare un livello calcolato sul best PRECEDENTE (100*0.95=95).
        await engine.ProcessCandleAsync(Candle(5, 120m));
        Assert.Single(await engine.GetOpenPositionsAsync());
        pos = (await engine.GetOpenPositionsAsync()).Single();
        Assert.Equal(120m, pos.BestPriceSinceEntry);

        // 115 > 114 (trail level calcolato sul best=120): resta aperta.
        await engine.ProcessCandleAsync(Candle(6, 115m));
        Assert.Single(await engine.GetOpenPositionsAsync());

        // 113 < 114: il trailing scatta.
        await engine.ProcessCandleAsync(Candle(7, 113m));
        Assert.Empty(await engine.GetOpenPositionsAsync());
    }

    [Fact]
    public async Task NoStopConfigured_LegacyEnsemble_NeverSetsAutomaticStop()
    {
        // Ensemble creato prima di questi campi (StopLossPercent/TrailingStopPercent = null di default):
        // il comportamento deve restare esattamente quello di prima (nessuno stop finché non impostato a mano).
        var strat = new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true };
        var (engine, _) = await BuildAsync(strat, i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);

        await WarmUpAsync(engine);
        await engine.ProcessCandleAsync(Candle(4, 100m));

        var pos = (await engine.GetOpenPositionsAsync()).Single();
        Assert.Null(pos.StopLoss);
        Assert.Null(pos.TrailingStopPercent);

        // Anche un forte calo non chiude la posizione: nessuno stop attivo (comportamento invariato).
        await engine.ProcessCandleAsync(Candle(5, 50m));
        Assert.Single(await engine.GetOpenPositionsAsync());
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
