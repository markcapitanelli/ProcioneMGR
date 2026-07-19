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
/// Regressione del bug CRITICO B1 (docs/TEST-UI-2026-07-18.md): la corsia 2 Testnet è andata a
/// PnL -1,8M su capitale 10k perché il testnet ha risposto "Filled" con quantità cumulative
/// (100x+) e prezzo 0, e il motore le ha adottate così com'erano. Il fix (FillSanityCheck) valida
/// il fill di RITORNO contro la quantità richiesta e il prezzo corrente: fuori banda l'APERTURA
/// viene rifiutata come esito incerto (audit FillSanityRejected, mai adottare il fill), la
/// CHIUSURA si finalizza al prezzo di riferimento locale (rifiutarla riaprirebbe l'oversell H2).
///
/// Stesso pattern di <see cref="TradingEngineReconcileTests"/>: client scriptati a code, un
/// accesso oltre lo script fa fallire il test (fail loudly, nessun default silenzioso).
/// </summary>
[Collection("Postgres")]
public sealed class TradingEngineFillSanityTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public TradingEngineFillSanityTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

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

    /// <summary>
    /// Variante futures. <see cref="Position"/> è impostabile dal test: la riconciliazione
    /// per-candela (<see cref="Services.Trading.Internal.FuturesPositionReconciler"/>) interroga
    /// GetPositionAsync e con un null forzerebbe la chiusura esterna della posizione locale —
    /// nei test di CHIUSURA va quindi valorizzata prima della candela successiva all'apertura.
    /// </summary>
    private sealed class ScriptedFuturesClient : IFuturesExchangeClient
    {
        public Queue<PlaceOrderResult> PlaceResults { get; } = new();
        public Queue<OrderStatusResult> StatusResults { get; } = new();
        public List<string> CancelledClientIds { get; } = new();
        public FuturesPosition? Position { get; set; }

        public ExchangeName Exchange => ExchangeName.Binance;
        public Task<SetLeverageResult> SetLeverageAsync(string symbol, int leverage, TradingCredentials credentials, CancellationToken ct = default)
            => Task.FromResult(new SetLeverageResult { Success = true, Leverage = leverage });
        public Task<PlaceOrderResult> PlaceFuturesOrderAsync(PlaceOrderRequest request, bool reduceOnly, CancellationToken ct = default)
            => Task.FromResult(PlaceResults.Count > 0 ? PlaceResults.Dequeue()
                : throw new InvalidOperationException("PlaceFuturesOrderAsync oltre lo script del test."));
        public Task<PlaceOrderResult> PlaceFuturesTriggerOrderAsync(PlaceOrderRequest request, bool isStopLoss, CancellationToken ct = default)
            => Task.FromResult(new PlaceOrderResult { Success = true });
        public Task<FuturesPosition?> GetPositionAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)
            => Task.FromResult(Position);
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

    // --- Test: APERTURA — fill implausibile mai adottato ---------------------------------------

    [Fact]
    public async Task SpotOpen_FillPriceZero_Rejected_NoPosition_CapitalUntouched()
    {
        // Il caso reale del bug B1: "Sell Market 171.673.819 @ 0,00 → Filled" dal testnet.
        var spot = new ScriptedSpotClient();
        spot.PlaceResults.Enqueue(PlaceFilled(0m, 80m));
        var (engine, dbFactory) = await BuildAsync(i => i == 4 ? Signal.Long : Signal.Hold, spot);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);

        Assert.Empty(await engine.GetOpenPositionsAsync());
        var status = await engine.GetStatusAsync();
        Assert.Equal(100_000m, status.AvailableCapital);
        Assert.Contains(await AuditAsync(dbFactory), a => a.Action == "FillSanityRejected");

        await using var db = await dbFactory.CreateDbContextAsync();
        var order = await db.Orders.SingleAsync();
        Assert.Equal(OrderStatus.Rejected, order.Status);
    }

    [Fact]
    public async Task SpotOpen_FillQuantity100x_Rejected_NoPosition_CapitalUntouched()
    {
        // Quantità cumulativa dal testnet: 100x la richiesta (richiesti 80 @100 = nozionale 8k,
        // riportati 8000 = nozionale 800k). Prima del fix: capitale corrotto di -800k.
        var spot = new ScriptedSpotClient();
        spot.PlaceResults.Enqueue(PlaceFilled(100m, 8_000m));
        var (engine, dbFactory) = await BuildAsync(i => i == 4 ? Signal.Long : Signal.Hold, spot);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);

        Assert.Empty(await engine.GetOpenPositionsAsync());
        var status = await engine.GetStatusAsync();
        Assert.Equal(100_000m, status.AvailableCapital);
        Assert.Contains(await AuditAsync(dbFactory),
            a => a.Action == "FillSanityRejected" && a.Details.Contains("tolleranza"));
    }

    [Fact]
    public async Task SpotOpen_ReconciledFillInsane_Rejected_NoPosition()
    {
        // Anche il fill che arriva dalla RICONCILIAZIONE (outcome.FillPrice/FillQty) è sospetto:
        // stessa verifica del fill diretto, mai adottato.
        var spot = new ScriptedSpotClient();
        spot.PlaceResults.Enqueue(PlaceUncertain());
        spot.StatusResults.Enqueue(StatusFilled(0m, 80m));   // "Filled @ 0" dopo il blip di rete
        var (engine, dbFactory) = await BuildAsync(i => i == 4 ? Signal.Long : Signal.Hold, spot);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);

        Assert.Empty(await engine.GetOpenPositionsAsync());
        var audit = await AuditAsync(dbFactory);
        Assert.Contains(audit, a => a.Action == "OrderReconciledFilled");   // traccia cronologica
        Assert.Contains(audit, a => a.Action == "FillSanityRejected");      // ...ma MAI adottato
        var status = await engine.GetStatusAsync();
        Assert.Equal(100_000m, status.AvailableCapital);
    }

    [Fact]
    public async Task FuturesOpen_FillQuantity100x_Rejected_NoPosition_CapitalUntouched()
    {
        // L'incidente reale era su una corsia FUTURES (leva 2x): "Buy 1.039,77 ETH" con nozionale
        // ~1,8M ≈ il PnL perso. Qui: richiesti 40 @100, riportati 4000.
        var fut = new ScriptedFuturesClient();
        fut.PlaceResults.Enqueue(PlaceFilled(100m, 4_000m));
        var (engine, dbFactory) = await BuildAsync(i => i == 4 ? Signal.Long : Signal.Hold, futures: fut);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);

        Assert.Empty(await engine.GetOpenPositionsAsync());
        var status = await engine.GetStatusAsync();
        Assert.Equal(10_000m, status.AvailableCapital);
        Assert.Contains(await AuditAsync(dbFactory), a => a.Action == "FillSanityRejected");
    }

    [Fact]
    public async Task SpotOpen_SaneSlippage_StillAdopted()
    {
        // Guardia anti-regressione sulla banda: uno slippage normale (2%) resta adottato.
        var spot = new ScriptedSpotClient();
        spot.PlaceResults.Enqueue(PlaceFilled(102m, 80m));
        var (engine, dbFactory) = await BuildAsync(i => i == 4 ? Signal.Long : Signal.Hold, spot);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);

        var pos = Assert.Single(await engine.GetOpenPositionsAsync());
        Assert.Equal(102m, pos.EntryPrice);
        Assert.DoesNotContain(await AuditAsync(dbFactory), a => a.Action == "FillSanityRejected");
    }

    // --- Test: CHIUSURA — fill implausibile ⇒ finalizza al prezzo di riferimento ---------------

    [Fact]
    public async Task SpotClose_FillPriceInsane_FinalizedAtReferencePrice()
    {
        var spot = new ScriptedSpotClient();
        spot.PlaceResults.Enqueue(PlaceFilled(100m, 80m));        // apertura pulita @100
        spot.PlaceResults.Enqueue(PlaceFilled(1_000_000m, 80m));  // chiusura: prezzo assurdo
        var (engine, dbFactory) = await BuildAsync(
            i => i == 4 ? Signal.Long : (i == 5 ? Signal.Close : Signal.Hold), spot);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);                           // apre @100
        await engine.ProcessCandleAsync(Candle(5, 100m));         // chiude

        // La chiusura si FINALIZZA (l'ordine è andato a buon fine: tenerla aperta = oversell H2)
        // ma al prezzo di riferimento locale, non al fill patologico (+80M di PnL fantasma).
        Assert.Empty(await engine.GetOpenPositionsAsync());
        await using var db = await dbFactory.CreateDbContextAsync();
        var trade = await db.TradeRecords.SingleAsync();
        Assert.Equal(100m, trade.ExitPrice);

        // Cassa: 100'000 − (8'000 + 8 fee) + (8'000 − 8 fee) = 99'984.
        var status = await engine.GetStatusAsync();
        Assert.Equal(99_984m, status.AvailableCapital, 2);
        Assert.Contains(await AuditAsync(dbFactory), a => a.Action == "FillSanityRejected");
    }

    [Fact]
    public async Task SpotClose_ReconciledFillPriceInsane_FinalizedAtReferencePrice()
    {
        var spot = new ScriptedSpotClient();
        spot.PlaceResults.Enqueue(PlaceFilled(100m, 80m));            // apertura pulita
        spot.PlaceResults.Enqueue(PlaceUncertain());                  // chiusura: blip di rete
        spot.StatusResults.Enqueue(StatusFilled(1_000_000m, 80m));    // riconciliata con prezzo assurdo
        var (engine, dbFactory) = await BuildAsync(
            i => i == 4 ? Signal.Long : (i == 5 ? Signal.Close : Signal.Hold), spot);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);
        await engine.ProcessCandleAsync(Candle(5, 100m));

        Assert.Empty(await engine.GetOpenPositionsAsync());
        await using var db = await dbFactory.CreateDbContextAsync();
        var trade = await db.TradeRecords.SingleAsync();
        Assert.Equal(100m, trade.ExitPrice);
        var audit = await AuditAsync(dbFactory);
        Assert.Contains(audit, a => a.Action == "CloseReconciledFilled");
        Assert.Contains(audit, a => a.Action == "FillSanityRejected");
    }

    [Fact]
    public async Task FuturesClose_FillPriceZero_FinalizedAtReferencePrice()
    {
        var fut = new ScriptedFuturesClient();
        fut.PlaceResults.Enqueue(PlaceFilled(100m, 40m));   // apertura pulita @100 (margine 800, 5x)
        fut.PlaceResults.Enqueue(PlaceFilled(0m, 40m));     // chiusura: "Filled @ 0" dal testnet
        var (engine, dbFactory) = await BuildAsync(
            i => i == 4 ? Signal.Long : (i == 5 ? Signal.Close : Signal.Hold), futures: fut);
        await engine.StartAsync(TradingMode.Testnet);

        await RunToSignalAsync(engine);                     // apre @100
        // La riconciliazione per-candela interroga GetPositionAsync: la posizione deve
        // "esistere" lato exchange, altrimenti verrebbe chiusa d'ufficio come esterna.
        fut.Position = new FuturesPosition { Symbol = "BTC/USDT", Quantity = 40m, Side = "LONG", EntryPrice = 100m, Leverage = 5 };
        await engine.ProcessCandleAsync(Candle(5, 100m));   // chiude

        Assert.Empty(await engine.GetOpenPositionsAsync());
        await using var db = await dbFactory.CreateDbContextAsync();
        var trade = await db.TradeRecords.SingleAsync();
        Assert.Equal(100m, trade.ExitPrice);
        Assert.False(trade.WasLiquidated);

        // Cassa: 10'000 − (800 margine + 4 fee) + (800 + PnL −8) = 9'988.
        var status = await engine.GetStatusAsync();
        Assert.Equal(9_988m, status.AvailableCapital, 2);
        Assert.Contains(await AuditAsync(dbFactory), a => a.Action == "FillSanityRejected");
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
