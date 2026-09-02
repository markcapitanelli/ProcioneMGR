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
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [Fase 4] Il router visto dal motore, non in isolamento. Copre il caso che una prima stesura di
/// questo codice sbagliava: filtrare saltando l'intero giro della strategia quando NON c'era una
/// posizione aperta sembrava equivalente, ma lasciava passare il caso peggiore — con una posizione
/// aperta il filtro non veniva nemmeno consultato, e su un segnale di inversione il motore chiudeva
/// e <b>riapriva dal lato opposto</b>, cioè apriva in un regime vietato proprio perché c'era una
/// posizione.
///
/// L'altra metà dell'invariante è che le CHIUSURE restino sempre permesse: un router che potesse
/// impedirle sarebbe un rischio, non un filtro.
/// </summary>
[Collection("Postgres")]
public sealed class RegimeRouterEngineTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public RegimeRouterEngineTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    /// <summary>Router scriptato: dichiara un regime noto e una lista di strategie ammesse.</summary>
    private sealed class ScriptedRouter(bool known, params string[] allowed) : ILaneRegimeRouter
    {
        public Task<RegimeRoutingDecision> DecideAsync(
            string symbol, string timeframe, IReadOnlyList<OhlcvData> recentCandles, CancellationToken ct = default)
            => Task.FromResult(known
                ? new RegimeRoutingDecision(true, 1, "test", allowed, false) { HasRule = true }
                : RegimeRoutingDecision.Unknown("test"));
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
        public Task UpdateConfigurationAsync(EnsembleConfiguration c, ProcioneMGR.Services.Ensemble.ConfigWriteContext writtenBy, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Services.Monitoring.DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
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

    private sealed class NoExchange : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => throw new NotImplementedException();
        public IExchangeClient Create(string exchangeName) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotImplementedException();
    }

    private async Task<(TradingEngine Engine, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync(
        Func<int, Signal> script, ILaneRegimeRouter? router)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 100_000m,
            Strategies = [new EnsembleStrategy
            {
                StrategyId = "s1", StrategyName = "Supertrend", DisplayName = "Supertrend", IsActive = true,
            }],
        };

        var engine = new TradingEngine(
            0, dbFactory, new ScriptedStrategyFactory(script), new TechnicalIndicatorsService(),
            new NoExchange(), new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(new SafetyConfiguration { MinOrderIntervalSeconds = 0, PositionSizePercent = 8m }),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance,
            regimeRouter: router);

        return (engine, dbFactory);
    }

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    private static async Task<List<TradingAuditLog>> AuditAsync(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.TradingAuditLogs.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task DisallowedStrategy_NeverOpens()
    {
        var (engine, dbFactory) = await BuildAsync(
            i => i == 4 ? Signal.Long : Signal.Hold,
            new ScriptedRouter(known: true, "BollingerMeanReversion"));   // Supertrend NON ammessa
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));

        Assert.Empty(await engine.GetOpenPositionsAsync());
        Assert.Contains(await AuditAsync(dbFactory), a => a.Action == "RegimeRouterSkipped");
    }

    [Fact]
    public async Task AllowedStrategy_OpensNormally()
    {
        var (engine, _) = await BuildAsync(
            i => i == 4 ? Signal.Long : Signal.Hold,
            new ScriptedRouter(known: true, "Supertrend"));
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));

        Assert.Single(await engine.GetOpenPositionsAsync());
    }

    [Fact]
    public async Task ReversalCannotSmuggleAnOpeningIntoAForbiddenRegime()
    {
        // IL BUG che questa struttura evita. La strategia apre long alla barra 4 mentre il regime la
        // ammette; poi il regime cambia e non la ammette più, e alla barra 6 arriva un segnale
        // opposto. La chiusura DEVE avvenire (è protettiva); la riapertura short NO.
        var router = new SwitchableRouter(allowed: "Supertrend");
        var (engine, dbFactory) = await BuildAsync(
            i => i == 4 ? Signal.Long : i == 6 ? Signal.Short : Signal.Hold, router);
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));
        Assert.Single(await engine.GetOpenPositionsAsync());

        router.Allowed = "BollingerMeanReversion";   // il regime cambia: Supertrend non è più ammessa
        await engine.ProcessCandleAsync(Candle(5, 100m));
        Assert.Single(await engine.GetOpenPositionsAsync());   // la posizione aperta resta: non si chiude d'imperio

        await engine.ProcessCandleAsync(Candle(6, 100m));      // segnale di inversione

        Assert.Empty(await engine.GetOpenPositionsAsync());    // chiusa...
        var audit = await AuditAsync(dbFactory);
        Assert.Contains(audit, a => a.Action == "ClosePosition");
        Assert.Contains(audit, a => a.Action == "RegimeRouterSkipped");   // ...ma NON riaperta short
    }

    [Fact]
    public async Task UnknownRegime_BehavesExactlyAsBeforeTheRouter()
    {
        var (engine, dbFactory) = await BuildAsync(
            i => i == 4 ? Signal.Long : Signal.Hold, new ScriptedRouter(known: false));
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));

        Assert.Single(await engine.GetOpenPositionsAsync());
        Assert.DoesNotContain(await AuditAsync(dbFactory), a => a.Action == "RegimeRouterSkipped");
    }

    [Fact]
    public async Task NoRouterAtAll_IsTheOldBehaviour()
    {
        var (engine, _) = await BuildAsync(i => i == 4 ? Signal.Long : Signal.Hold, router: null);
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));

        Assert.Single(await engine.GetOpenPositionsAsync());
    }

    /// <summary>Router il cui verdetto può cambiare a test in corso (il regime cambia sotto la posizione).</summary>
    private sealed class SwitchableRouter(string allowed) : ILaneRegimeRouter
    {
        public string Allowed { get; set; } = allowed;

        public Task<RegimeRoutingDecision> DecideAsync(
            string symbol, string timeframe, IReadOnlyList<OhlcvData> recentCandles, CancellationToken ct = default)
            => Task.FromResult(new RegimeRoutingDecision(true, 1, "test", [Allowed], false) { HasRule = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
