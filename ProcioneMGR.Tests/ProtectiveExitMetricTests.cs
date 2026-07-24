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
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R1/R2] La metrica <c>procione.trading.protective_exits</c> è la PROVA del valore del feed
/// real-time: confrontando <c>source=tick</c> con <c>source=candle</c> si vede quanto ritardo è
/// stato tolto agli stop. Perché quel confronto significhi qualcosa, il conteggio deve registrare
/// le uscite RIUSCITE, non i tentativi.
///
/// La distinzione non è accademica. Una chiusura può fallire (rete incerta, rifiuto dell'exchange)
/// lasciando la posizione aperta per il retry. Contando comunque, un ordine che continua a fallire
/// registrerebbe a ogni valutazione — e sul percorso a tick, dove le valutazioni sono decine al
/// secondo invece di una per candela, gonfierebbe di migliaia di conteggi proprio la metrica che
/// serve a confrontare i due percorsi. La metrica direbbe "il tick funziona benissimo" esattamente
/// quando il tick non sta riuscendo a chiudere niente.
/// </summary>
[Collection("Postgres")]
public sealed class ProtectiveExitMetricTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public ProtectiveExitMetricTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    /// <summary>Raccoglie le misure di un contatore per nome, con i suoi tag.</summary>
    private sealed class CounterProbe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly string _instrument;
        private readonly List<(long Value, string Source, string Reason)> _measurements = [];
        private readonly Lock _sync = new();

        public CounterProbe(string instrument)
        {
            _instrument = instrument;
            _listener = new MeterListener
            {
                InstrumentPublished = (inst, l) =>
                {
                    if (inst.Meter.Name == ProcioneMetrics.MeterName && inst.Name == _instrument)
                    {
                        l.EnableMeasurementEvents(inst);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
            {
                var source = string.Empty;
                var reason = string.Empty;
                foreach (var tag in tags)
                {
                    if (tag.Key == "source") source = tag.Value?.ToString() ?? string.Empty;
                    if (tag.Key == "reason") reason = tag.Value?.ToString() ?? string.Empty;
                }
                lock (_sync) { _measurements.Add((value, source, reason)); }
            });
            _listener.Start();
        }

        public long TotalFor(string source)
        {
            lock (_sync) { return _measurements.Where(m => m.Source == source).Sum(m => m.Value); }
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
        public Task<IReadOnlyList<ProcioneMGR.Services.Monitoring.DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>Apertura sempre riuscita; chiusura sempre RIFIUTATA: la posizione non se ne va mai.</summary>
    private sealed class CloseAlwaysRejectedClient : IExchangeClient
    {
        private int _calls;

        public ExchangeName Exchange => ExchangeName.Binance;
        public int MaxCandlesPerRequest => 1000;
        public Task<List<Ohlcv>> FetchOhlcvAsync(string s, string t, long since, int limit, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GetSymbolsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
        {
            // Il primo ordine è l'apertura: passa. Tutti i successivi sono tentativi di chiusura.
            var isOpen = Interlocked.Increment(ref _calls) == 1;
            return Task.FromResult(isOpen
                ? new PlaceOrderResult { Success = true, FilledPrice = 100m, FilledQuantity = request.Quantity, ExchangeOrderId = "open" }
                : new PlaceOrderResult { Success = false, Error = "exchange indisponibile (simulato)" });
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

    private async Task<TradingEngine> BuildAsync(ProcioneMetrics metrics, IExchangeClient? spot, TradingMode mode)
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

        return new TradingEngine(
            0, dbFactory, new ScriptedStrategyFactory(i => i == 4 ? Signal.Long : Signal.Hold),
            new TechnicalIndicatorsService(),
            new FakeExchangeClientFactory(spot ?? new CloseAlwaysRejectedClient()),
            new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(new SafetyConfiguration { MinOrderIntervalSeconds = 0, PositionSizePercent = 8m }),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance, metrics);
    }

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    [Fact]
    public async Task SuccessfulTickExit_IsCountedOnce()
    {
        using var metrics = new ProcioneMetrics();
        using var probe = new CounterProbe("procione.trading.protective_exits");
        var engine = await BuildAsync(metrics, spot: null, TradingMode.Paper);   // Paper: nessun exchange
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
        Assert.Single(await engine.GetOpenPositionsAsync());

        // Raffica di tick sotto lo stop (95): una sola chiusura, quindi un solo conteggio.
        for (var i = 0; i < 30; i++)
        {
            await engine.ProcessPriceTickAsync(90m, Candle(5, 90m).TimestampUtc.AddSeconds(i));
        }

        Assert.Empty(await engine.GetOpenPositionsAsync());
        Assert.Equal(1, probe.TotalFor("tick"));
        Assert.Equal(0, probe.TotalFor("candle"));
    }

    [Fact]
    public async Task FailedTickExit_IsNotCounted_EvenWhenRetriedManyTimes()
    {
        // REGRESSIONE: l'exchange rifiuta ogni chiusura, quindi la posizione resta aperta e ogni
        // tick successivo ritenta. Contare i TENTATIVI qui produrrebbe 30 conteggi per zero uscite
        // effettive — e su un feed reale a ~90 tick/s sarebbero migliaia.
        using var metrics = new ProcioneMetrics();
        using var probe = new CounterProbe("procione.trading.protective_exits");
        var engine = await BuildAsync(metrics, new CloseAlwaysRejectedClient(), TradingMode.Testnet);
        await engine.StartAsync(TradingMode.Testnet);

        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
        Assert.Single(await engine.GetOpenPositionsAsync());

        for (var i = 0; i < 30; i++)
        {
            await engine.ProcessPriceTickAsync(90m, Candle(5, 90m).TimestampUtc.AddSeconds(i));
        }

        // La posizione è ancora aperta (nessuna chiusura è riuscita) e il contatore lo riflette.
        Assert.Single(await engine.GetOpenPositionsAsync());
        Assert.Equal(0, probe.TotalFor("tick"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
