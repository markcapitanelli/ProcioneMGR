using System.Diagnostics.Metrics;
using System.Reflection;
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
/// Test dell'esecuzione live "a fette" (TWAP/VWAP/Iceberg) nel <see cref="TradingEngine"/>
/// (rif. <c>docs/ROADMAP-QLIB.md §1.2</c>). Verifica gli invarianti critici trovati in fase di
/// design: media ponderata dopo N fette, emergency stop a metà piano che chiude SOLO il riempito
/// e annulla il job, riavvio che abbandona il job ma preserva la posizione reale, e il bypass di
/// MaxPositionSizePercent chiuso da un pre-check aggregato.
///
/// Il tempo è controllato backdatando i job in cache (riflessione, SOLO nel test) così tutte le
/// fette diventano "dovute" senza attese reali; il throttle MinOrderIntervalSeconds è messo a 0.
/// </summary>
[Collection("Postgres")]
public sealed class TradingEngineExecutionTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public TradingEngineExecutionTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

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

    /// <summary>Client spot deterministico: ogni PlaceOrder riempie l'intera quantità a un prezzo dalla coda.</summary>
    private sealed class FakeSpotClient(Queue<decimal> fillPrices) : IExchangeClient
    {
        public ExchangeName Exchange => ExchangeName.Binance;
        public int MaxCandlesPerRequest => 1000;
        public Task<List<Ohlcv>> FetchOhlcvAsync(string symbol, string timeframe, long since, int limit, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GetSymbolsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
        {
            var price = fillPrices.Count > 0 ? fillPrices.Dequeue() : 100m;
            return Task.FromResult(new PlaceOrderResult
            {
                Success = true, ExchangeOrderId = Guid.NewGuid().ToString("N"), Status = "FILLED",
                FilledPrice = price, FilledQuantity = request.Quantity,
            });
        }
        public Task<CancelOrderResult> CancelOrderAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<OpenOrder>> GetOpenOrdersAsync(string symbol, TradingCredentials creds, CancellationToken ct = default) => Task.FromResult(new List<OpenOrder>());
        public Task<OrderStatusResult> GetOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AccountBalance> GetBalanceAsync(TradingCredentials creds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SymbolFilters> GetSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)
            => Task.FromResult(new SymbolFilters { StepSize = 0.00001m, MinQty = 0.00001m, TickSize = 0.01m, MinNotional = 0.0001m });
    }

    private sealed class FakeExchangeClientFactory(IExchangeClient client) : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => client;
        public IExchangeClient Create(string exchangeName) => client;
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

    private async Task<(TradingEngine Engine, IDbContextFactory<ApplicationDbContext> DbFactory)> BuildAsync(
        EnsembleStrategy strategy, Func<int, Signal> script, Queue<decimal> fillPrices, SafetyConfiguration? safety = null, bool liveExecEnabled = true,
        ProcioneMGR.Services.Observability.ProcioneMetrics? metrics = null)
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
            // Utente + credenziali Binance testnet (necessarie a StartAsync(Testnet)).
            db.Users.Add(new ApplicationUser { Id = "u1", UserName = "t", Email = "t@t.io" });
            db.ExchangeCredentials.Add(new ExchangeCredential
            {
                UserId = "u1", ExchangeName = ExchangeName.Binance, IsTestnet = true, Label = "test",
                ApiKey = "k", ApiSecret = "s",
            });
            // Profilo storico: 4 candele BTC/USDT 1h → il piano TWAP produce 4 fette.
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 4; i++)
                db.OhlcvData.Add(new OhlcvData { Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(i), Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 100m });
            await db.SaveChangesAsync();
        }

        var config = new EnsembleConfiguration { ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 100_000m, Strategies = [strategy] };

        var engine = new TradingEngine(
            0, dbFactory, new ScriptedStrategyFactory(script), new TechnicalIndicatorsService(),
            new FakeExchangeClientFactory(new FakeSpotClient(fillPrices)), new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(safety ?? new SafetyConfiguration { MinOrderIntervalSeconds = 0 }),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions { Enabled = liveExecEnabled, DefaultWindowMinutes = 5, AbandonGraceMinutes = 5 }),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance, metrics);

        return (engine, dbFactory);
    }

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    private static async Task WarmUpAndSignalAsync(TradingEngine engine, decimal price = 100m)
    {
        for (var i = 0; i < 4; i++) await engine.ProcessCandleAsync(Candle(i, price));
        await engine.ProcessCandleAsync(Candle(4, price)); // indice 4 = primo valutato → Long → piano
    }

    /// <summary>Backdata i job in cache (riflessione, solo test) così tutte le fette sono "dovute".</summary>
    private static void BackdateJobs(TradingEngine engine)
    {
        var field = typeof(TradingEngine).GetField("_executionJobs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (ExecutionJob j in (System.Collections.IEnumerable)field.GetValue(engine)!)
            j.CreatedAtUtc = DateTime.UtcNow.AddHours(-1);
    }

    private static EnsembleStrategy TwapStrategy() => new()
    {
        StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true,
        ExecutionAlgorithmName = "Twap", ExecutionWindowMinutes = 5,
    };

    // --- Test --------------------------------------------------------------------------------

    [Fact]
    public async Task ExecutionPlan_Twap_AccumulatesWeightedAverageEntryAndFullQuantity()
    {
        var prices = new Queue<decimal>([100m, 102m, 104m, 106m]);
        var (engine, dbFactory) = await BuildAsync(TwapStrategy(), i => i == 4 ? Signal.Long : Signal.Hold, prices);
        await engine.StartAsync(TradingMode.Testnet);
        await WarmUpAndSignalAsync(engine);       // fetta #1 @100 → posizione qty 20, entry 100

        BackdateJobs(engine);
        for (var i = 0; i < 3; i++) await engine.ProcessDueExecutionSlicesAsync();  // fette @102, @104, @106

        var pos = Assert.Single(await engine.GetOpenPositionsAsync());
        Assert.Equal(80m, pos.Quantity);          // 4 × 20, quantità totale esatta
        Assert.Equal(103m, pos.EntryPrice, 4);    // media ponderata (100+102+104+106)/4

        await using var db = await dbFactory.CreateDbContextAsync();
        var job = await db.ExecutionJobs.SingleAsync();
        Assert.Equal("Completed", job.Status);
    }

    [Fact]
    public async Task EmergencyStop_MidPlan_ClosesFilledQuantityOnly_AndCancelsJob()
    {
        var prices = new Queue<decimal>([100m, 102m, 100m /*fetta 2*/, 100m /*chiusura*/]);
        var (engine, dbFactory) = await BuildAsync(TwapStrategy(), i => i == 4 ? Signal.Long : Signal.Hold, prices);
        await engine.StartAsync(TradingMode.Testnet);
        await WarmUpAndSignalAsync(engine);       // fetta #1 (qty 20)

        BackdateJobs(engine);
        await engine.ProcessDueExecutionSlicesAsync();  // fetta #2 (qty 20) → posizione qty 40

        await engine.EmergencyStopAsync("test");

        Assert.Empty(await engine.GetOpenPositionsAsync());  // posizione chiusa

        await using var db = await dbFactory.CreateDbContextAsync();
        var trade = await db.TradeRecords.SingleAsync();
        Assert.Equal(40m, trade.Quantity);        // SOLO il riempito (2 fette), non gli 80 del piano
        var job = await db.ExecutionJobs.SingleAsync();
        Assert.Equal("Cancelled", job.Status);

        // Un tick tardivo del worker non deve fare nulla (nessuna eccezione, nessuna nuova posizione).
        await engine.ProcessDueExecutionSlicesAsync();
        Assert.Empty(await engine.GetOpenPositionsAsync());
    }

    [Fact]
    public async Task StartAsync_OrphanedRunningJob_MarkedCancelled_PositionSurvives()
    {
        var prices = new Queue<decimal>([100m, 100m, 100m, 100m]);
        var (engine, dbFactory) = await BuildAsync(TwapStrategy(), i => i == 4 ? Signal.Long : Signal.Hold, prices);
        await engine.StartAsync(TradingMode.Testnet);
        await WarmUpAndSignalAsync(engine);       // fetta #1 → posizione reale (qty 20) + job Running

        await engine.StartAsync(TradingMode.Testnet);  // riavvio a metà piano

        await using var db = await dbFactory.CreateDbContextAsync();
        // Job orfano annullato…
        Assert.Empty(await db.ExecutionJobs.Where(j => j.LaneId == 0 && j.Status == "Running").ToListAsync());
        // …ma la posizione REALE sottostante sopravvive nel DB (fix del bug pre-esistente in StartAsync).
        var surviving = await db.OpenPositions.Where(p => p.LaneId == 0).ToListAsync();
        Assert.Single(surviving);
        Assert.Equal(20m, surviving[0].Quantity);
    }

    [Fact]
    public async Task ExecutionPlan_TotalExceedsMaxPositionSize_RejectedUpfront_NoJobCreated()
    {
        // Il caso "config incoerente fin dall'avvio" ora è bloccato da StartAsync (fail-fast H1),
        // ma la safety è hot-reloadable (IOptionsMonitor): se MaxPositionSizePercent viene
        // ABBASSATO a motore avviato, il pre-check AGGREGATO resta l'unica difesa contro il
        // bypass per fette (ogni fetta da sola starebbe sotto il limite). SafetyConfiguration è
        // mutabile e StaticOptionsMonitor restituisce la stessa istanza: mutarla simula il reload.
        var safety = new SafetyConfiguration { MinOrderIntervalSeconds = 0 };
        var prices = new Queue<decimal>([100m, 100m, 100m, 100m]);
        var (engine, dbFactory) = await BuildAsync(TwapStrategy(), i => i == 4 ? Signal.Long : Signal.Hold, prices, safety);
        await engine.StartAsync(TradingMode.Testnet);
        safety.MaxPositionSizePercent = 5m;   // hot-reload: il piano (8%) ora supera il limite
        await WarmUpAndSignalAsync(engine);

        Assert.Empty(await engine.GetOpenPositionsAsync());   // nessuna posizione: nemmeno la prima fetta parte

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Empty(await db.ExecutionJobs.ToListAsync());   // nessun job creato
        Assert.Contains(await db.TradingAuditLogs.ToListAsync(), a => a.Action == "ExecutionPlanRejected");
    }

    [Fact]
    public async Task Metrics_TwapExecutionAndClose_EmitTradeJobAndSlippageCounters()
    {
        // Osservabilità (follow-up Fase 2): il TradingEngine emette i contatori di ProcioneMetrics
        // sui punti-evento reali. Stesso pattern di ObservabilityTests, ma end-to-end nel motore:
        // apertura a fette (TWAP) → job Started/Completed + N trade → chiusura → 1 trade Close.
        using var metrics = new ProcioneMGR.Services.Observability.ProcioneMetrics();
        var prices = new Queue<decimal>([100m, 102m, 104m, 106m]);
        var (engine, _) = await BuildAsync(TwapStrategy(), i => i == 4 ? Signal.Long : Signal.Hold, prices, metrics: metrics);
        await engine.StartAsync(TradingMode.Testnet);

        var longs = new List<(string Name, long Value)>();
        var doubles = new List<(string Name, double Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == ProcioneMGR.Services.Observability.ProcioneMetrics.MeterName) l.EnableMeasurementEvents(inst);
            },
        };
        listener.SetMeasurementEventCallback<long>((inst, val, _, _) => longs.Add((inst.Name, val)));
        listener.SetMeasurementEventCallback<double>((inst, val, _, _) => doubles.Add((inst.Name, val)));
        listener.Start();

        await WarmUpAndSignalAsync(engine);       // fetta #1 @100 → apertura (1 trade) + job Started
        BackdateJobs(engine);
        for (var i = 0; i < 3; i++) await engine.ProcessDueExecutionSlicesAsync();  // fette @102/104/106 → 3 trade + job Completed

        var pos = Assert.Single(await engine.GetOpenPositionsAsync());
        await engine.ClosePositionAsync(pos.PositionId);   // chiusura → 1 trade Close

        // 4 aperture (fetta #1 + 3 fette) + 1 chiusura = 5 trade eseguiti.
        Assert.Equal(5, longs.Count(m => m.Name == "procione.trades.executed"));
        // Job: esattamente 1 "Started" + 1 "Completed".
        Assert.Equal(2, longs.Count(m => m.Name == "procione.execution.jobs"));
        // Implementation shortfall: media ponderata 103 vs prezzo di arrivo 100 = +300 bps (il buy paga di più).
        Assert.Contains(("procione.execution.slippage_bps", 300.0), doubles);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
