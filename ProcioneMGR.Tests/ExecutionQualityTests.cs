using System.Diagnostics.Metrics;
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
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Services.Trading.Internal;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [Fase 1 — docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Prima del fix, l'implementation shortfall
/// esisteva solo per gli ordini eseguiti a fette: gli ordini di corsia — cioè quasi tutti —
/// catturavano il prezzo di fill ma lo usavano solo come guardia anti-fill-patologico, mai come
/// misura di costo. Il costo assunto in selezione non aveva quindi alcun riscontro con quello
/// pagato davvero.
///
/// Due trappole sono il vero oggetto di questi test:
/// 1. <b>il segno sugli short</b> — vendere più a buon mercato del riferimento è un COSTO, non un
///    guadagno, e una formula senza segno lo registrerebbe col verso sbagliato dimezzando (o
///    invertendo) la stima del costo su un campione con entrambi i lati;
/// 2. <b>la chiusura</b> — lì il prezzo di riferimento veniva letteralmente sovrascritto dal fill
///    (<c>exitPrice = ...</c>), cancellando il termine di paragone prima che qualcuno potesse
///    misurarlo.
/// </summary>
public sealed class ExecutionQualitySignTests
{
    [Fact]
    public void Buy_FilledAboveArrival_IsPositiveCost()
    {
        // Comprato a 101 avendo deciso a 100: 100 bps di costo.
        var bps = ExecutionQuality.ShortfallBps(OrderSide.Buy, 100m, 101m);
        Assert.Equal(100m, bps);
    }

    [Fact]
    public void Sell_FilledBelowArrival_IsPositiveCost()
    {
        // LA TRAPPOLA: venduto a 99 avendo deciso a 100. È un costo esattamente come sopra, e va
        // registrato con lo STESSO segno, altrimenti su un campione misto long/short i due lati si
        // annullano e il costo medio misurato tende a zero — cioè alla risposta più sbagliata
        // possibile, perché conferma il modello invece di correggerlo.
        var bps = ExecutionQuality.ShortfallBps(OrderSide.Sell, 100m, 99m);
        Assert.Equal(100m, bps);
    }

    [Fact]
    public void PriceImprovement_IsNegative()
    {
        // Comprato MEGLIO del riferimento: shortfall negativo (price improvement), non zero.
        Assert.Equal(-50m, ExecutionQuality.ShortfallBps(OrderSide.Buy, 100m, 99.5m));
        Assert.Equal(-50m, ExecutionQuality.ShortfallBps(OrderSide.Sell, 100m, 100.5m));
    }

    [Fact]
    public void MissingOrDegenerateInputs_AreNull_NotZero()
    {
        // Null significa "non misurabile" (tipicamente Paper, o ordini anteriori alla Fase 1).
        // Restituire 0 sarebbe peggio che tacere: inquinerebbe le statistiche con un'esecuzione
        // perfetta mai avvenuta.
        Assert.Null(ExecutionQuality.ShortfallBps(OrderSide.Buy, null, 101m));
        Assert.Null(ExecutionQuality.ShortfallBps(OrderSide.Buy, 100m, null));
        Assert.Null(ExecutionQuality.ShortfallBps(OrderSide.Buy, 0m, 101m));
        Assert.Null(ExecutionQuality.ShortfallBps(OrderSide.Buy, 100m, 0m));
    }

    [Fact]
    public void MatchesExecutionJobConvention()
    {
        // Stessa formula del ramo ExecutionJob in TradingEngine.FinalizeJobIfDone: le due misure
        // finiscono negli stessi grafici e devono essere confrontabili senza girare segni a mano.
        const decimal arrival = 250m, filled = 251.25m;
        var sign = 1m;   // Buy
        var expected = sign * (filled - arrival) / arrival * 10_000m;

        Assert.Equal(expected, ExecutionQuality.ShortfallBps(OrderSide.Buy, arrival, filled));
    }

    [Fact]
    public void OrderExposesShortfall_OnlyWhenArrivalPriceIsKnown()
    {
        var measurable = new Order { Side = OrderSide.Buy, ArrivalPrice = 100m, FilledPrice = 100.5m };
        Assert.Equal(50m, measurable.ShortfallBps);

        var paperLike = new Order { Side = OrderSide.Buy, ArrivalPrice = null, FilledPrice = 100.5m };
        Assert.Null(paperLike.ShortfallBps);
    }
}

/// <summary>
/// Verifica end-to-end sul motore: l'arrival price viene fissato prima della chiamata all'exchange,
/// sopravvive alla chiusura e produce la metrica. Stesso impianto di
/// <see cref="ProtectiveExitMetricTests"/> (client scriptato + sonda sul meter).
/// </summary>
[Collection("Postgres")]
public sealed class ExecutionQualityEngineTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public ExecutionQualityEngineTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    /// <summary>Raccoglie le misure di un istogramma per nome, con i tag che ci interessano.</summary>
    private sealed class HistogramProbe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<(double Value, string Mode, string Action)> _measurements = [];
        private readonly Lock _sync = new();

        public HistogramProbe(string instrument)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (inst, l) =>
                {
                    if (inst.Meter.Name == ProcioneMetrics.MeterName && inst.Name == instrument)
                    {
                        l.EnableMeasurementEvents(inst);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<double>((inst, value, tags, _) =>
            {
                var mode = string.Empty;
                var action = string.Empty;
                foreach (var tag in tags)
                {
                    if (tag.Key is "mode" or "exchange") mode = tag.Value?.ToString() ?? string.Empty;
                    if (tag.Key is "action" or "outcome") action = tag.Value?.ToString() ?? string.Empty;
                }
                lock (_sync) { _measurements.Add((value, mode, action)); }
            });
            _listener.Start();
        }

        public IReadOnlyList<(double Value, string Mode, string Action)> All
        {
            get { lock (_sync) { return _measurements.ToList(); } }
        }

        public void Dispose() => _listener.Dispose();
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
        public Task<IReadOnlyList<Services.Monitoring.DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>Fill scriptati in ordine: il 1° è l'apertura, i successivi le chiusure.</summary>
    private sealed class ScriptedFillClient(params decimal[] fillPrices) : IExchangeClient
    {
        private int _calls;

        public ExchangeName Exchange => ExchangeName.Binance;
        public int MaxCandlesPerRequest => 1000;
        public Task<List<Ohlcv>> FetchOhlcvAsync(string s, string t, long since, int limit, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GetSymbolsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            if (index >= fillPrices.Length) throw new InvalidOperationException("PlaceOrderAsync oltre lo script del test.");
            return Task.FromResult(new PlaceOrderResult
            {
                Success = true,
                FilledPrice = fillPrices[index],
                FilledQuantity = request.Quantity,
                ExchangeOrderId = $"ex-{index}",
            });
        }

        public Task<CancelOrderResult> CancelOrderAsync(string s, string id, TradingCredentials c, CancellationToken ct = default)
            => Task.FromResult(new CancelOrderResult { Success = true });
        public Task<List<OpenOrder>> GetOpenOrdersAsync(string s, TradingCredentials c, CancellationToken ct = default) => Task.FromResult(new List<OpenOrder>());
        public Task<OrderStatusResult> GetOrderStatusAsync(string s, string id, TradingCredentials c, CancellationToken ct = default)
            => Task.FromResult(new OrderStatusResult { Found = false });
        public Task<AccountBalance> GetBalanceAsync(TradingCredentials c, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SymbolFilters> GetSymbolFiltersAsync(string s, bool testnet, CancellationToken ct = default)
            => Task.FromResult(new SymbolFilters { StepSize = 0.00001m, MinQty = 0.00001m, TickSize = 0.01m, MinNotional = 0.0001m });
    }

    private sealed class FakeExchangeClientFactory(IExchangeClient spot) : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => spot;
        public IExchangeClient Create(string exchangeName) => spot;
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotImplementedException();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => Null.Instance;
        private sealed class Null : IDisposable { public static readonly Null Instance = new(); public void Dispose() { } }
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<(TradingEngine Engine, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync(
        ProcioneMetrics metrics, IExchangeClient spot, TradingMode mode)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            if (mode != TradingMode.Paper)
            {
                db.Users.Add(new ApplicationUser { Id = "u1", UserName = "t", Email = "t@t.io" });
                db.ExchangeCredentials.Add(new ExchangeCredential
                {
                    UserId = "u1", ExchangeName = ExchangeName.Binance, IsTestnet = true,
                    Label = "test", ApiKey = "k", ApiSecret = "s",
                });
                await db.SaveChangesAsync();
            }
        }

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 100_000m,
            Strategies = [new EnsembleStrategy
            {
                StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted",
                IsActive = true, StopLossPercent = 5m,
            }],
        };

        var engine = new TradingEngine(
            0, dbFactory, new ScriptedStrategyFactory(i => i == 4 ? Signal.Long : Signal.Hold),
            new TechnicalIndicatorsService(), new FakeExchangeClientFactory(spot),
            new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(new SafetyConfiguration { MinOrderIntervalSeconds = 0, PositionSizePercent = 8m }),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance, metrics);

        return (engine, dbFactory);
    }

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    [Fact]
    public async Task TestnetOpen_RecordsArrivalPriceAndShortfall()
    {
        // Decisione a 100 (chiusura di candela), eseguito a 100,20 ⇒ 20 bps di costo reale.
        using var metrics = new ProcioneMetrics();
        using var slippage = new HistogramProbe("procione.trading.slippage_bps");
        var (engine, dbFactory) = await BuildAsync(metrics, new ScriptedFillClient(100.20m), TradingMode.Testnet);
        await engine.StartAsync(TradingMode.Testnet);

        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));

        await using var db = await dbFactory.CreateDbContextAsync();
        var order = await db.Orders.AsNoTracking().SingleAsync();
        Assert.Equal(100m, order.ArrivalPrice);
        Assert.Equal(100.20m, order.FilledPrice);
        Assert.Equal(20m, order.ShortfallBps);
        Assert.NotNull(order.SubmitLatencyMs);

        var measured = Assert.Single(slippage.All);
        Assert.Equal(20d, measured.Value, precision: 6);
        Assert.Equal("Open", measured.Action);
    }

    [Fact]
    public async Task PaperOpen_LeavesExecutionUnmeasured()
    {
        // In Paper il fill È il riferimento: uno shortfall zero non è una misura, è una tautologia.
        // Registrarlo gonfierebbe il campione di esecuzioni perfette mai avvenute e trascinerebbe
        // verso lo zero proprio la statistica che deve dire quanto costa eseguire davvero.
        using var metrics = new ProcioneMetrics();
        using var slippage = new HistogramProbe("procione.trading.slippage_bps");
        var (engine, dbFactory) = await BuildAsync(metrics, new ScriptedFillClient(), TradingMode.Paper);
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));

        await using var db = await dbFactory.CreateDbContextAsync();
        var order = await db.Orders.AsNoTracking().SingleAsync();
        Assert.Null(order.ArrivalPrice);
        Assert.Null(order.ShortfallBps);
        Assert.Empty(slippage.All);
    }

    [Fact]
    public async Task Close_KeepsArrivalPrice_WhichTheFillUsedToOverwrite()
    {
        // REGRESSIONE della Fase 1: in chiusura `exitPrice` (il riferimento) veniva SOSTITUITO dal
        // prezzo di fill prima di finire nell'ordine — quindi a posteriori il costo di una chiusura
        // era, letteralmente, non ricostruibile. Qui lo stop scatta a 90 e l'exchange riempie a
        // 89,55: 45 bps di costo su una VENDITA, che devono risultare POSITIVI.
        using var metrics = new ProcioneMetrics();
        using var slippage = new HistogramProbe("procione.trading.slippage_bps");
        var (engine, dbFactory) = await BuildAsync(metrics, new ScriptedFillClient(100m, 89.55m), TradingMode.Testnet);
        await engine.StartAsync(TradingMode.Testnet);

        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
        Assert.Single(await engine.GetOpenPositionsAsync());

        await engine.ProcessCandleAsync(Candle(5, 90m));   // sotto lo stop del 5%
        Assert.Empty(await engine.GetOpenPositionsAsync());

        await using var db = await dbFactory.CreateDbContextAsync();
        var closeOrder = await db.Orders.AsNoTracking().SingleAsync(o => o.Side == OrderSide.Sell);
        Assert.Equal(90m, closeOrder.ArrivalPrice);
        Assert.Equal(89.55m, closeOrder.FilledPrice);
        Assert.Equal(50m, closeOrder.ShortfallBps);

        var closeMeasure = Assert.Single(slippage.All, m => m.Action == "Close");
        Assert.Equal(50d, closeMeasure.Value, precision: 6);
    }

    [Fact]
    public async Task OrderLatency_IsRecordedWithOutcome()
    {
        using var metrics = new ProcioneMetrics();
        using var latency = new HistogramProbe("procione.trading.order_latency_ms");
        var (engine, _) = await BuildAsync(metrics, new ScriptedFillClient(100m), TradingMode.Testnet);
        await engine.StartAsync(TradingMode.Testnet);

        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));

        var measured = Assert.Single(latency.All);
        Assert.True(measured.Value >= 0d);
        Assert.Equal("ok", measured.Action);          // esito, non solo durata
        Assert.Equal("Binance", measured.Mode);       // exchange
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
