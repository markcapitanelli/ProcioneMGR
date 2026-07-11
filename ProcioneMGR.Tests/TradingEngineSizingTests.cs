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
/// Regressione H1 (audit 2026-07): con la size hard-coded all'8% e leva 5, il nozionale per
/// posizione era il 40% del capitale — sopra MaxPositionSizePercent (10%) — quindi il
/// SafetyChecker rifiutava OGNI ordine e la corsia futures non faceva mai trading, in silenzio.
/// Il fix rende la size configurabile (<see cref="SafetyConfiguration.PositionSizePercent"/>) e
/// valida la coerenza a <see cref="TradingEngine.StartAsync"/>: meglio un errore azionabile
/// all'avvio che il silenzio degli ordini.
/// </summary>
[Collection("Postgres")]
public sealed class TradingEngineSizingTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public TradingEngineSizingTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    // --- Fakes (stesso pattern di TradingEngineFuturesEquityTests) -----------------------------

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
        private sealed class NullDisposable : IDisposable { public static readonly NullDisposable Instance = new(); public void Dispose() { } }
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    // --- Setup -------------------------------------------------------------------------------

    private async Task<TradingEngine> BuildAsync(bool futures, int leverage, SafetyConfiguration safety, Func<int, Signal>? script = null)
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
            IsFutures = futures, Leverage = leverage,
            Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true }],
        };

        return new TradingEngine(
            0, dbFactory, new ScriptedStrategyFactory(script ?? (_ => Signal.Hold)), new TechnicalIndicatorsService(),
            new ThrowingExchangeClientFactory(), new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(safety),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance);
    }

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    // --- Test --------------------------------------------------------------------------------

    [Fact]
    public async Task Futures_DefaultSafety_Leverage5_StartFailsFastWithActionableMessage()
    {
        // Config DEFAULT: size 8% (margine) × leva 5 = nozionale 40% > MaxPositionSizePercent 10%.
        // Prima del fix: StartAsync passava e il SafetyChecker rifiutava ogni ordine in silenzio.
        var engine = await BuildAsync(futures: true, leverage: 5, new SafetyConfiguration());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(TradingMode.Paper));

        Assert.Contains("Sizing incoerente", ex.Message);
        Assert.Contains("MaxPositionSizePercent", ex.Message);   // dice DOVE intervenire
        var status = await engine.GetStatusAsync();
        Assert.False(status.IsRunning);
    }

    [Fact]
    public async Task Futures_RaisedLimits_StartsAndFirstOrderPassesSafetyChecker()
    {
        var safety = new SafetyConfiguration
        {
            MinOrderIntervalSeconds = 0,
            PositionSizePercent = 8m,
            MaxPositionSizePercent = 50m,     // 8% × 5 = 40% ≤ 50%: coerente
            MaxTotalExposurePercent = 100m,
            MaxLeverageAllowed = 5,
        };
        var engine = await BuildAsync(futures: true, leverage: 5, safety, i => i == 4 ? Signal.Long : Signal.Hold);

        await engine.StartAsync(TradingMode.Paper);
        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));

        // Il primo ordine NON viene rifiutato dal SafetyChecker: la posizione esiste.
        var pos = Assert.Single(await engine.GetOpenPositionsAsync());
        Assert.Equal(40m, pos.Quantity, 5);   // margine 800 × leva 5 / prezzo 100
    }

    [Fact]
    public async Task Spot_SizeAboveMaxPositionSize_StartFailsFast()
    {
        // Spot: nessuna leva, ma una size (12%) sopra MaxPositionSizePercent (10%) produce lo
        // stesso stallo silenzioso. Stessa validazione, messaggio senza la parte "× leva".
        var safety = new SafetyConfiguration { PositionSizePercent = 12m };
        var engine = await BuildAsync(futures: false, leverage: 1, safety);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(TradingMode.Paper));

        Assert.Contains("Sizing incoerente", ex.Message);
    }

    [Fact]
    public async Task Spot_DefaultSafety_StartsNormally()
    {
        // La config di default (8% ≤ 10% ≤ 50%) resta coerente: nessuna regressione sull'avvio spot.
        var engine = await BuildAsync(futures: false, leverage: 1, new SafetyConfiguration());

        await engine.StartAsync(TradingMode.Paper);

        var status = await engine.GetStatusAsync();
        Assert.True(status.IsRunning);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
