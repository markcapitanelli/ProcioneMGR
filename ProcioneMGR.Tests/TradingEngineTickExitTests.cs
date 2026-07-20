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
/// [R1] Test del percorso a TICK real-time (<see cref="TradingEngine.ProcessPriceTickAsync"/>).
///
/// Due proprietà sono di sicurezza, non di comodità, e i test che le coprono valgono più di tutti
/// gli altri qui dentro:
///  - un tick NON apre mai una posizione (gli ingressi restano governati dalle candele chiuse,
///    l'unico percorso che il backtest valida);
///  - una raffica di tick sotto lo stop produce UNA sola chiusura, mai una cascata di ordini.
///
/// Il resto verifica che tick e candela decidano allo stesso modo, essendo passati dalla stessa
/// funzione pura (<c>ProtectiveExitEvaluator</c>).
/// </summary>
[Collection("Postgres")]
public class TradingEngineTickExitTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public TradingEngineTickExitTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

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

    /// <summary>In Paper il motore non tocca l'exchange: se lo facesse, questo esplode invece di mentire.</summary>
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
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ProcioneMGR.Services.Execution.ExecutionAlgorithmFactory(),
            NullLogger<TradingEngine>.Instance);

        return (engine, dbFactory);
    }

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

    private static DateTime TickTime(int seconds) =>
        new DateTime(2026, 1, 1, 5, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);

    /// <summary>Apre una posizione Long a 100 con lo stop/target richiesto, e restituisce il motore pronto.</summary>
    private async Task<(TradingEngine Engine, IDbContextFactory<ApplicationDbContext> Db)> OpenLongAsync(
        decimal? stopLossPercent = null, decimal? takeProfitPercent = null, decimal? trailingPercent = null)
    {
        var strat = new EnsembleStrategy
        {
            StrategyId = "s1",
            StrategyName = "Scripted",
            DisplayName = "Scripted",
            IsActive = true,
            StopLossPercent = stopLossPercent,
            TakeProfitPercent = takeProfitPercent,
            TrailingStopPercent = trailingPercent,
        };
        var (engine, db) = await BuildAsync(strat, i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i < 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
        await engine.ProcessCandleAsync(Candle(4, 100m)); // apertura Long a 100

        Assert.Single(await engine.GetOpenPositionsAsync());
        return (engine, db);
    }

    private static async Task<List<TradeRecord>> TradesAsync(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.TradeRecords.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Tick_BelowStop_ClosesPosition()
    {
        var (engine, db) = await OpenLongAsync(stopLossPercent: 5m); // stop a 95

        await engine.ProcessPriceTickAsync(94m, TickTime(1));

        Assert.Empty(await engine.GetOpenPositionsAsync());
        var trade = Assert.Single(await TradesAsync(db));
        Assert.Equal("StopLoss", trade.ExitReason);
    }

    [Fact]
    public async Task Tick_AboveStop_LeavesPositionOpen()
    {
        var (engine, _) = await OpenLongAsync(stopLossPercent: 5m); // stop a 95

        await engine.ProcessPriceTickAsync(96m, TickTime(1));

        Assert.Single(await engine.GetOpenPositionsAsync());
    }

    [Fact]
    public async Task TickBurst_BelowStop_ClosesExactlyOnce()
    {
        // Proprietà di SICUREZZA: il feed real-time consegna decine di tick al secondo. Se ognuno
        // potesse emettere una chiusura, si genererebbe una cascata di ordini reali sulla stessa
        // posizione (la classe di bug dell'oversell H2).
        var (engine, db) = await OpenLongAsync(stopLossPercent: 5m);

        for (var i = 0; i < 25; i++)
        {
            await engine.ProcessPriceTickAsync(90m - i, TickTime(i));
        }

        Assert.Empty(await engine.GetOpenPositionsAsync());
        Assert.Single(await TradesAsync(db));
    }

    [Fact]
    public async Task Tick_NeverOpensPosition_EvenWhenStrategyWouldSignalLong()
    {
        // Lo script segnala Long a OGNI indice: se il percorso a tick valutasse le strategie,
        // aprirebbe. Non deve, mai — gli ingressi appartengono alle candele chiuse.
        var strat = new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true };
        var (engine, db) = await BuildAsync(strat, _ => Signal.Long);
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i < 50; i++)
        {
            await engine.ProcessPriceTickAsync(100m + i, TickTime(i));
        }

        Assert.Empty(await engine.GetOpenPositionsAsync());
        await using var ctx = await db.CreateDbContextAsync();
        Assert.Empty(await ctx.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Tick_AboveTakeProfit_ClosesWithTakeProfitReason()
    {
        var (engine, db) = await OpenLongAsync(takeProfitPercent: 10m); // target a 110

        await engine.ProcessPriceTickAsync(111m, TickTime(1));

        Assert.Empty(await engine.GetOpenPositionsAsync());
        Assert.Equal("TakeProfit", Assert.Single(await TradesAsync(db)).ExitReason);
    }

    [Fact]
    public async Task Tick_HittingBothStopAndTarget_PrefersStop()
    {
        // Si mette il target SOTTO lo stop, così un unico prezzo li viola entrambi: a 94 lo stop
        // long scatta (prezzo <= 95) e anche il target scatta (prezzo >= 90). Quando succede non si
        // può sapere quale livello il mercato abbia toccato per primo, e la regola è assumere
        // l'esito peggiore: vince lo stop.
        var (engine, db) = await OpenLongAsync(stopLossPercent: 5m); // stop 95
        var pos = (await engine.GetOpenPositionsAsync()).Single();
        await engine.SetStopLossTakeProfitAsync(pos.PositionId, stopLoss: 95m, takeProfit: 90m);

        await engine.ProcessPriceTickAsync(94m, TickTime(1));

        Assert.Equal("StopLoss", Assert.Single(await TradesAsync(db)).ExitReason);
    }

    [Fact]
    public async Task Ticks_AdvanceTrailingStop_AndCloseOnPullback()
    {
        var (engine, db) = await OpenLongAsync(trailingPercent: 5m); // entry 100, best iniziale 100

        // Salita: il best si aggiorna a 120, quindi il livello di trail diventa 114.
        await engine.ProcessPriceTickAsync(120m, TickTime(1));
        Assert.Single(await engine.GetOpenPositionsAsync());
        Assert.Equal(120m, (await engine.GetOpenPositionsAsync()).Single().BestPriceSinceEntry);

        // 115 > 114: resta aperta.
        await engine.ProcessPriceTickAsync(115m, TickTime(2));
        Assert.Single(await engine.GetOpenPositionsAsync());

        // 113 < 114: il trailing scatta.
        await engine.ProcessPriceTickAsync(113m, TickTime(3));
        Assert.Empty(await engine.GetOpenPositionsAsync());
        Assert.Equal("StopLoss", Assert.Single(await TradesAsync(db)).ExitReason);
    }

    [Fact]
    public async Task Tick_WithNonPositivePrice_IsIgnored()
    {
        // Il testnet ha già mostrato di saper rispondere "prezzo 0" (bug B1): su un prezzo simile
        // non si decide nulla, altrimenti ogni posizione long verrebbe chiusa istantaneamente.
        var (engine, _) = await OpenLongAsync(stopLossPercent: 5m);

        await engine.ProcessPriceTickAsync(0m, TickTime(1));
        await engine.ProcessPriceTickAsync(-10m, TickTime(2));

        Assert.Single(await engine.GetOpenPositionsAsync());
    }

    [Fact]
    public async Task Tick_WhenEngineStopped_DoesNothing()
    {
        var (engine, db) = await OpenLongAsync(stopLossPercent: 5m);
        await engine.StopAsync();

        await engine.ProcessPriceTickAsync(50m, TickTime(1));

        // Il motore fermo non chiude nulla da solo: lo stato resta quello congelato dallo stop.
        Assert.Empty(await TradesAsync(db));
    }

    [Fact]
    public async Task Tick_AndCandle_ProduceTheSameExitLevel()
    {
        // Il punto del refactor: le due strade passano dalla stessa funzione pura, quindi decidono
        // identicamente. Qui si verifica sul confine esatto dello stop (95).
        var (tickEngine, tickDb) = await OpenLongAsync(stopLossPercent: 5m);
        await tickEngine.ProcessPriceTickAsync(95m, TickTime(1)); // esattamente AL livello: scatta
        Assert.Empty(await tickEngine.GetOpenPositionsAsync());
        Assert.Equal("StopLoss", Assert.Single(await TradesAsync(tickDb)).ExitReason);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
