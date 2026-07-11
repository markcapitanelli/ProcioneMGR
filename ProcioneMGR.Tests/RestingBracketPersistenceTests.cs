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
/// [M3] Persistenza degli id dei bracket "resting" (<see cref="OpenPosition.StopOrderId"/>/
/// <see cref="OpenPosition.TakeProfitOrderId"/>): prima erano [NotMapped] — un riavvio perdeva i
/// clientOrderId dei trigger REALI ancora armati sull'exchange, e la chiusura non poteva più
/// cancellarli (ordini orfani reduce-only pronti a scattare su una posizione ormai chiusa).
/// Vedi <c>RestingStopOrderTests</c> per la costruzione delle richieste lato client.
/// </summary>
[Collection("Postgres")]
public sealed class RestingBracketPersistenceTests : IAsyncDisposable
{
    private readonly string _connString;
    private readonly List<ServiceProvider> _providers = new();

    public RestingBracketPersistenceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

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
    /// Futures client per il flusso resting: place sempre riempito al prezzo richiesto (fallback
    /// 100), trigger orders OK, cancellazioni REGISTRATE (è l'oggetto del test M3).
    /// </summary>
    private sealed class RecordingFuturesClient : IFuturesExchangeClient
    {
        public List<string> TriggerClientIds { get; } = new();
        public List<string> CancelledClientIds { get; } = new();

        public ExchangeName Exchange => ExchangeName.Binance;
        public Task<SetLeverageResult> SetLeverageAsync(string symbol, int leverage, TradingCredentials credentials, CancellationToken ct = default)
            => Task.FromResult(new SetLeverageResult { Success = true, Leverage = leverage });
        public Task<PlaceOrderResult> PlaceFuturesOrderAsync(PlaceOrderRequest request, bool reduceOnly, CancellationToken ct = default)
            => Task.FromResult(new PlaceOrderResult { Success = true, FilledPrice = 100m, FilledQuantity = request.Quantity, ExchangeOrderId = "ex-1" });
        public Task<PlaceOrderResult> PlaceFuturesTriggerOrderAsync(PlaceOrderRequest request, bool isStopLoss, CancellationToken ct = default)
        {
            TriggerClientIds.Add(request.ClientOrderId);
            return Task.FromResult(new PlaceOrderResult { Success = true, ExchangeOrderId = "plan-" + TriggerClientIds.Count });
        }
        public Task<FuturesPosition?> GetPositionAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)
            => Task.FromResult<FuturesPosition?>(null);
        public Task<CancelOrderResult> CancelFuturesOrderAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = default)
        {
            CancelledClientIds.Add(clientOrderId);
            return Task.FromResult(new CancelOrderResult { Success = true });
        }
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

    private readonly RecordingFuturesClient _futuresClient = new();

    private async Task<(TradingEngine Engine, IDbContextFactory<ApplicationDbContext> DbFactory)> NewEngineInstanceAsync(Func<int, Signal> script)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            if (!await db.Users.AnyAsync())
            {
                db.Users.Add(new ApplicationUser { Id = "u1", UserName = "t", Email = "t@t.io" });
                db.ExchangeCredentials.Add(new ExchangeCredential
                {
                    UserId = "u1", ExchangeName = ExchangeName.Binance, IsTestnet = true, Label = "test",
                    ApiKey = "k", ApiSecret = "s",
                });
                await db.SaveChangesAsync();
            }
        }

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 10_000m,
            IsFutures = true, Leverage = 5,
            Strategies =
            [
                new EnsembleStrategy
                {
                    StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true,
                    StopLossPercent = 2m, TakeProfitPercent = 4m,   // servono i livelli per i bracket
                },
            ],
        };
        var safety = new SafetyConfiguration
        {
            MinOrderIntervalSeconds = 0,
            PositionSizePercent = 8m,
            MaxPositionSizePercent = 50m,
            MaxTotalExposurePercent = 100m,
            MaxLeverageAllowed = 5,
            UseExchangeRestingStops = true,   // la feature M3 sotto test
        };

        var engine = new TradingEngine(
            0, dbFactory, new ScriptedStrategyFactory(script), new TechnicalIndicatorsService(),
            new FakeExchangeClientFactory(_futuresClient), new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(safety),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance);
        return (engine, dbFactory);
    }

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    [Fact]
    public async Task BracketIds_PersistedOnPlacement_CancelUsesExactlyThoseIds()
    {
        var (engine, dbFactory) = await NewEngineInstanceAsync(i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Testnet);
        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));   // apre + 2 trigger

        Assert.Equal(2, _futuresClient.TriggerClientIds.Count);   // stop + take profit

        // Gli id sono già sul DB (non solo in-memory): è il cuore del fix M3.
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var row = await db.OpenPositions.SingleAsync();
            Assert.Equal(_futuresClient.TriggerClientIds[0], row.StopOrderId);
            Assert.Equal(_futuresClient.TriggerClientIds[1], row.TakeProfitOrderId);
        }

        await engine.ClosePositionAsync((await engine.GetOpenPositionsAsync()).Single().PositionId);

        // La chiusura cancella ESATTAMENTE i trigger piazzati (stessi clientOrderId).
        Assert.Equal(_futuresClient.TriggerClientIds, _futuresClient.CancelledClientIds);
        Assert.Empty(await engine.GetOpenPositionsAsync());
    }

    [Fact]
    public async Task BracketIds_SurviveRestart()
    {
        var (engine, _) = await NewEngineInstanceAsync(i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Testnet);
        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
        Assert.Equal(2, _futuresClient.TriggerClientIds.Count);

        // "Riavvio": istanza nuova sullo stesso DB. Prima del fix ([NotMapped]) gli id tornavano null.
        var (restarted, _) = await NewEngineInstanceAsync(_ => Signal.Hold);
        var pos = Assert.Single(await restarted.GetOpenPositionsAsync());

        Assert.Equal(_futuresClient.TriggerClientIds[0], pos.StopOrderId);
        Assert.Equal(_futuresClient.TriggerClientIds[1], pos.TakeProfitOrderId);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers) await p.DisposeAsync();
    }
}
