using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Regressione del bug CRITICO C1 (audit 2026-07): <c>ComputeEquity</c> applicava il modello di
/// cassa dello SPOT (±qty·prezzo) anche ai FUTURES a margine isolato. Su uno short leveraged
/// l'equity crollava del nozionale intero alla candela di APERTURA (es. leva 5, size 8%:
/// −40% di equity istantaneo) → falso "Max drawdown superato" → emergency stop immediato con
/// chiusura forzata di una posizione perfettamente sana. Il fix somma margine bloccato + PnL non
/// realizzato, coerente con il modello di cassa di apertura/chiusura (margine+fee giù, margine+PnL su).
/// </summary>
[Collection("Postgres")]
public sealed class TradingEngineFuturesEquityTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public TradingEngineFuturesEquityTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    // --- Fakes -------------------------------------------------------------------------------

    private sealed class ScriptedStrategy(Func<int, Signal> script) : IStrategy
    {
        public string Name => "Scripted";
        public string DisplayName => "Scripted";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];
        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct) => Task.CompletedTask;
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

    /// <summary>
    /// Client futures che CONTA le chiamate di piazzamento: in modalità Paper il motore non deve
    /// MAI toccare l'exchange, quindi ogni metodo può restituire valori benigni.
    /// </summary>
    private sealed class FakeFuturesClient : IFuturesExchangeClient
    {
        public int PlaceCalls { get; private set; }
        public ExchangeName Exchange => ExchangeName.Binance;
        public Task<SetLeverageResult> SetLeverageAsync(string symbol, int leverage, TradingCredentials credentials, CancellationToken ct = default)
            => Task.FromResult(new SetLeverageResult { Success = true, Leverage = leverage });
        public Task<PlaceOrderResult> PlaceFuturesOrderAsync(PlaceOrderRequest request, bool reduceOnly, CancellationToken ct = default)
        {
            PlaceCalls++;
            return Task.FromResult(new PlaceOrderResult { Success = true, FilledPrice = request.Quantity, FilledQuantity = request.Quantity });
        }
        public Task<PlaceOrderResult> PlaceFuturesTriggerOrderAsync(PlaceOrderRequest request, bool isStopLoss, CancellationToken ct = default)
            => Task.FromResult(new PlaceOrderResult { Success = true });
        public Task<FuturesPosition?> GetPositionAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)
            => Task.FromResult<FuturesPosition?>(null);
        public Task<CancelOrderResult> CancelFuturesOrderAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = default)
            => Task.FromResult(new CancelOrderResult { Success = true });
        public Task<List<OpenOrder>> GetOpenFuturesOrdersAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)
            => Task.FromResult(new List<OpenOrder>());
        public Task<OrderStatusResult> GetFuturesOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = default)
            => Task.FromResult(new OrderStatusResult { Found = false });
        public Task<FuturesBalance> GetFuturesBalanceAsync(TradingCredentials credentials, CancellationToken ct = default)
            => Task.FromResult(new FuturesBalance());
        public Task<SymbolFilters> GetFuturesSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)
            => Task.FromResult(new SymbolFilters { StepSize = 0.00001m, MinQty = 0.00001m, TickSize = 0.01m, MinNotional = 0.0001m });
        public Task<decimal> GetFundingRateAsync(string symbol, bool testnet, CancellationToken ct = default) => Task.FromResult(0m);
    }

    private sealed class FakeExchangeClientFactory(IFuturesExchangeClient futures) : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => throw new NotImplementedException();
        public IExchangeClient Create(string exchangeName) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => futures;
        public IFuturesExchangeClient CreateFutures(string exchangeName) => futures;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;
        private sealed class NullDisposable : IDisposable { public static readonly NullDisposable Instance = new(); public void Dispose() { } }
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    // --- Setup -------------------------------------------------------------------------------

    private readonly FakeFuturesClient _futuresClient = new();

    /// <summary>
    /// Motore futures Paper: capitale 10k, leva 5, size 8% (margine) → nozionale 4'000 (40%),
    /// fee 0.1% = 4. I limiti safety sono alzati QUANTO BASTA a far passare il gate di coerenza
    /// H1 (40% ≤ MaxPositionSize 50%), ma MaxDrawdownPercent resta al default 20%: è proprio la
    /// soglia che il bug C1 faceva scattare in falso.
    /// </summary>
    private async Task<TradingEngine> BuildFuturesEngineAsync(Func<int, Signal> script)
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
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 10_000m,
            IsFutures = true, Leverage = 5,
            Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true }],
        };
        var safety = new SafetyConfiguration
        {
            MinOrderIntervalSeconds = 0,
            PositionSizePercent = 8m,
            MaxPositionSizePercent = 50m,
            MaxTotalExposurePercent = 100m,
            MaxLeverageAllowed = 5,
            // MaxDrawdownPercent resta 20 (default): il test verifica che NON scatti.
        };

        return new TradingEngine(
            0, dbFactory, new ScriptedStrategyFactory(script), new TechnicalIndicatorsService(),
            new FakeExchangeClientFactory(_futuresClient), new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(safety),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance);
    }

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    /// <summary>4 candele di warm-up + candela indice 4 (prima valutata) a prezzo 100 → apertura.</summary>
    private static async Task OpenAtIndex4Async(TradingEngine engine)
    {
        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
    }

    // --- Test --------------------------------------------------------------------------------

    // Capitale 10'000, margine 800 (8%), leva 5 → qty 40 @100, nozionale 4'000, fee 4.
    // Available dopo l'apertura = 10'000 − 800 − 4 = 9'196.

    [Fact]
    public async Task ShortFlat_EquityIsCapitalMinusFee_NoInstantEmergencyStop()
    {
        var engine = await BuildFuturesEngineAsync(i => i == 4 ? Signal.Short : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);
        await OpenAtIndex4Async(engine);
        await engine.ProcessCandleAsync(Candle(5, 100m));   // candela successiva, prezzo piatto

        var status = await engine.GetStatusAsync();

        // Con il bug: equity = 9'196 − 4'000 = 5'196 → drawdown 48% → emergency stop immediato.
        Assert.False(status.IsEmergencyStopped);
        Assert.Single(await engine.GetOpenPositionsAsync());   // la posizione è ancora viva
        Assert.Equal(-4m, status.TotalPnl, 3);                 // solo la fee di apertura
        Assert.True(status.TotalPnl + status.TotalCapital > 0m, "l'equity non deve mai andare negativa su uno short appena aperto");
        Assert.True(status.MaxDrawdown < 1m, $"drawdown atteso ~0.04%, trovato {status.MaxDrawdown}%");
        Assert.Equal(9_196m, status.AvailableCapital, 3);
        Assert.Equal(0, _futuresClient.PlaceCalls);            // Paper: mai l'exchange reale
    }

    [Fact]
    public async Task LongFlat_TotalPnlIsMinusFee_NotPlusNotional()
    {
        var engine = await BuildFuturesEngineAsync(i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);
        await OpenAtIndex4Async(engine);
        await engine.ProcessCandleAsync(Candle(5, 100m));

        var status = await engine.GetStatusAsync();

        // Con il bug: equity = 9'196 + 4'000 = 13'196 → TotalPnl +3'196 dal nulla (prezzo piatto!).
        Assert.Equal(-4m, status.TotalPnl, 3);
        Assert.False(status.IsEmergencyStopped);

        var pos = Assert.Single(await engine.GetOpenPositionsAsync());
        Assert.Equal(800m, pos.MarginBalance, 3);
        Assert.Equal(40m, pos.Quantity, 5);
    }

    [Fact]
    public async Task ShortPriceDrops2Percent_EquityGainsUnrealizedPnl()
    {
        var engine = await BuildFuturesEngineAsync(i => i == 4 ? Signal.Short : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);
        await OpenAtIndex4Async(engine);
        await engine.ProcessCandleAsync(Candle(5, 98m));    // −2%: lo short guadagna

        var status = await engine.GetStatusAsync();

        // equity = available 9'196 + margine 800 + uPnL (100−98)×40 = 10'076 → TotalPnl +76.
        Assert.Equal(76m, status.TotalPnl, 3);
        Assert.False(status.IsEmergencyStopped);
        Assert.Single(await engine.GetOpenPositionsAsync());
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
