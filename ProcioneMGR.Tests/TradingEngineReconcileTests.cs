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
/// Regressione dei bug CRITICI C2/H2 (audit 2026-07): dopo un esito di rete INCERTO il motore
/// controllava solo <c>GetOpenOrdersAsync</c> — ma un MARKET riempito durante il blip NON è tra
/// gli ordini aperti, quindi veniva scambiato per "mai piazzato": posizione reale non tracciata
/// (nessuno stop la gestisce) + ordine DUPLICATO alla candela successiva (apertura), oppure
/// posizione locale aperta per sempre con retry in oversell (chiusura). Il fix interroga lo STATO
/// per clientOrderId (<see cref="OrderStatusResult"/>) e adotta il fill reale.
///
/// I client sono scriptati a CODE: ogni lookup/piazzamento consuma il prossimo esito previsto;
/// un accesso oltre lo script fa fallire il test (fail loudly, nessun default silenzioso).
/// </summary>
[Collection("Postgres")]
public sealed class TradingEngineReconcileTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public TradingEngineReconcileTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

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

    /// <summary>Esiti scriptati: Place/Status consumano dalla coda; coda vuota = flusso imprevisto → throw.</summary>
    private sealed class ScriptedSpotClient : IExchangeClient
    {
        public Queue<PlaceOrderResult> PlaceResults { get; } = new();
        public Queue<OrderStatusResult> StatusResults { get; } = new();
        public List<string> PlacedClientIds { get; } = new();
        public List<string> CancelledClientIds { get; } = new();

        public ExchangeName Exchange => ExchangeName.Binance;
        public int MaxCandlesPerRequest => 1000;
        public Task<List<Ohlcv>> FetchOhlcvAsync(string symbol, string timeframe, long since, int limit, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GetSymbolsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
        {
            PlacedClientIds.Add(request.ClientOrderId);
            return Task.FromResult(PlaceResults.Count > 0 ? PlaceResults.Dequeue()
                : throw new InvalidOperationException("PlaceOrderAsync oltre lo script del test."));
        }
        public Task<CancelOrderResult> CancelOrderAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default)
        {
            CancelledClientIds.Add(clientOrderId);
            return Task.FromResult(new CancelOrderResult { Success = true });
        }
        public Task<List<OpenOrder>> GetOpenOrdersAsync(string symbol, TradingCredentials creds, CancellationToken ct = default) => Task.FromResult(new List<OpenOrder>());
        public Task<OrderStatusResult> GetOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default)
            => Task.FromResult(StatusResults.Count > 0 ? StatusResults.Dequeue()
                : throw new InvalidOperationException("GetOrderStatusAsync oltre lo script del test."));
        public Task<AccountBalance> GetBalanceAsync(TradingCredentials creds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SymbolFilters> GetSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)
            => Task.FromResult(new SymbolFilters { StepSize = 0.00001m, MinQty = 0.00001m, TickSize = 0.01m, MinNotional = 0.0001m });
    }

    /// <summary>Variante futures dello scripted client (GetPositionAsync sempre null: liquidazione stimata in locale).</summary>
    private sealed class ScriptedFuturesClient : IFuturesExchangeClient
    {
        public Queue<PlaceOrderResult> PlaceResults { get; } = new();
        public Queue<OrderStatusResult> StatusResults { get; } = new();
        public List<string> CancelledClientIds { get; } = new();

        public ExchangeName Exchange => ExchangeName.Binance;
        public Task<SetLeverageResult> SetLeverageAsync(string symbol, int leverage, TradingCredentials credentials, CancellationToken ct = default)
            => Task.FromResult(new SetLeverageResult { Success = true, Leverage = leverage });
        public Task<PlaceOrderResult> PlaceFuturesOrderAsync(PlaceOrderRequest request, bool reduceOnly, CancellationToken ct = default)
            => Task.FromResult(PlaceResults.Count > 0 ? PlaceResults.Dequeue()
                : throw new InvalidOperationException("PlaceFuturesOrderAsync oltre lo script del test."));
        public Task<PlaceOrderResult> PlaceFuturesTriggerOrderAsync(PlaceOrderRequest request, bool isStopLoss, CancellationToken ct = default)
            => Task.FromResult(new PlaceOrderResult { Success = true });
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
            => Task.FromResult(StatusResults.Count > 0 ? StatusResults.Dequeue()
                : throw new InvalidOperationException("GetFuturesOrderStatusAsync oltre lo script del test."));
        public Task<FuturesBalance> GetFuturesBalanceAsync(TradingCredentials credentials, CancellationToken ct = default)
            => Task.FromResult(new FuturesBalance());
        public Task<SymbolFilters> GetFuturesSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)
            => Task.FromResult(new SymbolFilters { StepSize = 0.00001m, MinQty = 0.00001m, TickSize = 0.01m, MinNotional = 0.0001m });
        public Task<decimal> GetFundingRateAsync(string symbol, bool testnet, CancellationToken ct = default) => Task.FromResult(0m);
    }

    private sealed class FakeExchangeClientFactory(IExchangeClient? spot, IFuturesExchangeClient? futures) : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => spot ?? throw new NotImplementedException();
        public IExchangeClient Create(string exchangeName) => spot ?? throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => futures ?? throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => futures ?? throw new NotImplementedException();
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

    // --- Esiti scriptati ---------------------------------------------------------------------

    private static PlaceOrderResult PlaceUncertain() => new() { Success = false, NetworkUncertain = true, Error = "timeout simulato" };
    private static PlaceOrderResult PlaceFilled(decimal price, decimal qty) => new() { Success = true, FilledPrice = price, FilledQuantity = qty, ExchangeOrderId = "ex-ok" };
    private static OrderStatusResult StatusUncertain() => new() { Found = false, NetworkUncertain = true, Error = "lookup 5xx simulato" };
    private static OrderStatusResult StatusNotFound() => new() { Found = false };
    private static OrderStatusResult StatusOpen() => new() { Found = true, Status = "Open" };
    private static OrderStatusResult StatusFilled(decimal price, decimal qty) => new() { Found = true, Status = "Filled", FilledPrice = price, FilledQuantity = qty, ExchangeOrderId = "ex-rec" };

    // --- Setup -------------------------------------------------------------------------------

    private async Task<(TradingEngine Engine, IDbContextFactory<ApplicationDbContext> DbFactory)> BuildAsync(
        Func<int, Signal> script, ScriptedSpotClient? spot = null, ScriptedFuturesClient? futures = null)
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
            db.Users.Add(new ApplicationUser { Id = "u1", UserName = "t", Email = "t@t.io" });
            db.ExchangeCredentials.Add(new ExchangeCredential
            {
                UserId = "u1", ExchangeName = ExchangeName.Binance, IsTestnet = true, Label = "test",
                ApiKey = "k", ApiSecret = "s",
            });
            await db.SaveChangesAsync();
        }

        var isFutures = futures is not null;
        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h",
            TotalCapital = isFutures ? 10_000m : 100_000m,
            IsFutures = isFutures, Leverage = isFutures ? 5 : 1,
            Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true }],
        };
        var safety = new SafetyConfiguration
        {
            MinOrderIntervalSeconds = 0,
            PositionSizePercent = 8m,
            MaxPositionSizePercent = 50m,
            MaxTotalExposurePercent = 100m,
            MaxLeverageAllowed = 5,
        };

        var engine = new TradingEngine(
            0, dbFactory, new ScriptedStrategyFactory(script), new TechnicalIndicatorsService(),
            new FakeExchangeClientFactory(spot, futures), new FakeEnsembleManager(config),
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

    /// <summary>Candele 0..4 a prezzo 100: l'indice 4 è il primo valutato dalle strategie.</summary>
    private static async Task RunToSignalAsync(TradingEngine engine)
    {
        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
    }

    private static async Task<List<TradingAuditLog>> AuditAsync(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.TradingAuditLogs.AsNoTracking().ToListAsync();
    }

    // --- Test: APERTURA spot (C2) --------------------------------------------------------------

    [Fact]
    public async Task OpenUncertain_ThenLookupFilled_AdoptsRealFill()
    {
        var spot = new ScriptedSpotClient();
        spot.PlaceResults.Enqueue(PlaceUncertain());
        spot.StatusResults.Enqueue(StatusUncertain());          // 1° lookup: ancora giù
        spot.StatusResults.Enqueue(StatusFilled(101.5m, 80m));  // 2° lookup: era stato riempito!
        var (engine, dbFactory) = await BuildAsync(i => i == 4 ? Signal.Long : Signal.Hold, spot);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);

        // PRIMA del fix: GetOpenOrders vuoto → "mai piazzato" → nessuna posizione locale per
        // un fill REALE sull'exchange (posizione fantasma senza stop-loss).
        var pos = Assert.Single(await engine.GetOpenPositionsAsync());
        Assert.Equal(101.5m, pos.EntryPrice);
        Assert.Equal(80m, pos.Quantity);
        Assert.Contains(await AuditAsync(dbFactory), a => a.Action == "OrderReconciledFilled");
    }

    [Fact]
    public async Task OpenUncertain_NotFound_SafeReject_NextCandleOpensExactlyOnePosition()
    {
        var spot = new ScriptedSpotClient();
        spot.PlaceResults.Enqueue(PlaceUncertain());
        spot.StatusResults.Enqueue(StatusNotFound());        // l'exchange dichiara: mai esistito
        spot.PlaceResults.Enqueue(PlaceFilled(100m, 80m));   // retry alla candela dopo
        var (engine, dbFactory) = await BuildAsync(i => i >= 4 ? Signal.Long : Signal.Hold, spot);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);
        Assert.Empty(await engine.GetOpenPositionsAsync());   // rifiuto sicuro, nessuna posizione

        await engine.ProcessCandleAsync(Candle(5, 100m));     // la strategia rivaluta Long

        var pos = Assert.Single(await engine.GetOpenPositionsAsync());   // UNA sola posizione
        Assert.Equal(100m, pos.EntryPrice);
        Assert.Equal(2, spot.PlacedClientIds.Count);
        Assert.NotEqual(spot.PlacedClientIds[0], spot.PlacedClientIds[1]);   // nuovo ClientOrderId
        Assert.Contains(await AuditAsync(dbFactory),
            a => a.Action == "OrderRejected" && a.Details.Contains("network-uncertain-not-found"));
    }

    [Fact]
    public async Task OpenUncertain_StillOpenOnExchange_CancelledThenFillAdopted()
    {
        var spot = new ScriptedSpotClient();
        spot.PlaceResults.Enqueue(PlaceUncertain());
        spot.StatusResults.Enqueue(StatusOpen());                // vivo sull'exchange
        spot.StatusResults.Enqueue(StatusFilled(102m, 80m));     // riempito tra lookup e cancel
        var (engine, _) = await BuildAsync(i => i == 4 ? Signal.Long : Signal.Hold, spot);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);

        // La cancellazione chiude la finestra di duplicazione; il fill arrivato comunque è adottato.
        Assert.Single(spot.CancelledClientIds);
        var pos = Assert.Single(await engine.GetOpenPositionsAsync());
        Assert.Equal(102m, pos.EntryPrice);
    }

    [Fact]
    public async Task OpenUncertain_LookupAlwaysUncertain_BestEffortCancel_AuditCritical_NoPosition()
    {
        var spot = new ScriptedSpotClient();
        spot.PlaceResults.Enqueue(PlaceUncertain());
        spot.StatusResults.Enqueue(StatusUncertain());
        spot.StatusResults.Enqueue(StatusUncertain());
        spot.StatusResults.Enqueue(StatusUncertain());   // 3 tentativi, tutti al buio
        var (engine, dbFactory) = await BuildAsync(i => i == 4 ? Signal.Long : Signal.Hold, spot);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);

        Assert.Empty(await engine.GetOpenPositionsAsync());
        Assert.Single(spot.CancelledClientIds);          // cancellazione best-effort inviata
        Assert.Contains(await AuditAsync(dbFactory), a => a.Action == "OrderReconcileUncertain");

        await using var db = await dbFactory.CreateDbContextAsync();
        var order = await db.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Rejected, order.Status);
    }

    // --- Test: CHIUSURA spot (H2) --------------------------------------------------------------

    [Fact]
    public async Task CloseUncertain_LookupFilled_FinalizesWithRealExitPrice_AndRefundsCapital()
    {
        var spot = new ScriptedSpotClient();
        spot.PlaceResults.Enqueue(PlaceFilled(100m, 80m));   // apertura pulita @100
        spot.PlaceResults.Enqueue(PlaceUncertain());          // chiusura: blip di rete
        spot.StatusResults.Enqueue(StatusFilled(99m, 80m));   // ma era stata eseguita @99
        var (engine, dbFactory) = await BuildAsync(
            i => i == 4 ? Signal.Long : (i == 5 ? Signal.Close : Signal.Hold), spot);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);                       // apre @100
        await engine.ProcessCandleAsync(Candle(5, 100m));     // chiude (riconciliato @99)

        // PRIMA del fix: la posizione restava aperta PER SEMPRE (ogni retry = oversell rifiutato).
        Assert.Empty(await engine.GetOpenPositionsAsync());

        await using var db = await dbFactory.CreateDbContextAsync();
        var trade = await db.TradeRecords.SingleAsync();
        Assert.Equal(99m, trade.ExitPrice);

        // Cassa: 100'000 − (8'000 + 8 fee) + (7'920 − 7.92 fee) = 99'904.08.
        var status = await engine.GetStatusAsync();
        Assert.Equal(99_904.08m, status.AvailableCapital, 2);
        Assert.Contains(await AuditAsync(dbFactory), a => a.Action == "CloseReconciledFilled");
    }

    [Fact]
    public async Task CloseUncertain_NotFound_PositionStays_NextCandleRetriesAndCloses()
    {
        var spot = new ScriptedSpotClient();
        spot.PlaceResults.Enqueue(PlaceFilled(100m, 80m));   // apertura
        spot.PlaceResults.Enqueue(PlaceUncertain());          // 1ª chiusura: blip
        spot.StatusResults.Enqueue(StatusNotFound());         // mai arrivata all'exchange
        spot.PlaceResults.Enqueue(PlaceFilled(99m, 80m));     // 2ª chiusura: eseguita
        var (engine, dbFactory) = await BuildAsync(
            i => i == 4 ? Signal.Long : (i >= 5 ? Signal.Close : Signal.Hold), spot);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);
        await engine.ProcessCandleAsync(Candle(5, 100m));     // chiusura fallita in sicurezza

        Assert.Single(await engine.GetOpenPositionsAsync());  // la posizione NON viene finalizzata
        Assert.Contains(await AuditAsync(dbFactory), a => a.Action == "CloseUncertain");

        await engine.ProcessCandleAsync(Candle(6, 100m));     // retry: nuovo ordine di chiusura

        Assert.Empty(await engine.GetOpenPositionsAsync());
        await using var db = await dbFactory.CreateDbContextAsync();
        var trade = await db.TradeRecords.SingleAsync();
        Assert.Equal(99m, trade.ExitPrice);
    }

    // --- Test: APERTURA futures (C2, stesso helper via call-site futures) -----------------------

    [Fact]
    public async Task FuturesOpenUncertain_LookupFilled_AdoptsRealFill_WithIsolatedMargin()
    {
        var fut = new ScriptedFuturesClient();
        fut.PlaceResults.Enqueue(PlaceUncertain());
        fut.StatusResults.Enqueue(StatusFilled(101m, 40m));
        var (engine, dbFactory) = await BuildAsync(i => i == 4 ? Signal.Long : Signal.Hold, futures: fut);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);

        var pos = Assert.Single(await engine.GetOpenPositionsAsync());
        Assert.Equal(101m, pos.EntryPrice);
        Assert.Equal(40m, pos.Quantity);
        Assert.Equal(5, pos.Leverage);
        Assert.Equal(808m, pos.MarginBalance, 3);   // 40 × 101 / 5: margine dal fill REALE
        Assert.Contains(await AuditAsync(dbFactory), a => a.Action == "OrderReconciledFilled");
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
