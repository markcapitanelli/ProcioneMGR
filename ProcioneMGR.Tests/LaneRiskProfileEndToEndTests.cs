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
using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R3] Verifica che il profilo di rischio arrivi DAVVERO fino alle decisioni del motore.
///
/// I test di <c>RiskProfileTests</c> provano che il profilo compone le soglie giuste; questi
/// provano che quelle soglie governano il comportamento reale della corsia. È la differenza fra
/// "il calcolo è corretto" e "il calcolo è collegato a qualcosa" — e la seconda è quella che
/// fallisce silenziosamente quando il cablaggio si rompe.
/// </summary>
[Collection("Postgres")]
public sealed class LaneRiskProfileEndToEndTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public LaneRiskProfileEndToEndTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

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
        public Task UpdateConfigurationAsync(EnsembleConfiguration c, CancellationToken ct = default) => Task.CompletedTask;
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

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>Soglie globali DELIBERATAMENTE permissive: se un limite scatta, viene dal profilo.</summary>
    private static SafetyConfiguration PermissiveGlobal() => new()
    {
        PositionSizePercent = 50m,
        MaxPositionSizePercent = 90m,
        MaxTotalExposurePercent = 100m,
        MaxDailyLossPercent = 90m,
        MaxDrawdownPercent = 90m,
        MaxOpenPositions = 20,
        MinOrderIntervalSeconds = 0,
        MaxLeverageAllowed = 20,
    };

    private async Task<(TradingEngine Engine, LaneSafetyMonitor Safety)> BuildAsync(
        string? profileName, Func<int, Signal> script)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 100_000m,
            RiskProfileName = profileName,
            Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true }],
        };

        var laneSafety = new LaneSafetyMonitor(PermissiveGlobal().AsMonitor());

        var engine = new TradingEngine(
            0, dbFactory, new ScriptedStrategyFactory(script), new TechnicalIndicatorsService(),
            new ThrowingExchangeClientFactory(), new FakeEnsembleManager(config),
            laneSafety,
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance,
            riskProfileSink: laneSafety);

        return (engine, laneSafety);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => Null.Instance;
        private sealed class Null : IDisposable { public static readonly Null Instance = new(); public void Dispose() { } }
    }

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    [Fact]
    public async Task StartingALane_ActivatesItsRiskProfile()
    {
        var (engine, safety) = await BuildAsync(RiskProfiles.Prudente, _ => Signal.Hold);
        Assert.Null(safety.Profile);   // prima dell'avvio: soglie globali

        await engine.StartAsync(TradingMode.Paper);

        Assert.Equal(RiskProfiles.Conservative, safety.Profile);
        Assert.Equal(RiskProfiles.Conservative.PositionSizePercent, safety.CurrentValue.PositionSizePercent);
        Assert.Equal(RiskProfiles.Conservative.MaxDrawdownPercent, safety.CurrentValue.MaxDrawdownPercent);
    }

    [Fact]
    public async Task LaneWithoutProfile_KeepsGlobalThresholds()
    {
        // Regressione sul comportamento di TUTTE le corsie esistenti prima di R3.
        var (engine, safety) = await BuildAsync(profileName: null, _ => Signal.Hold);

        await engine.StartAsync(TradingMode.Paper);

        Assert.Null(safety.Profile);
        Assert.Equal(50m, safety.CurrentValue.PositionSizePercent);
        Assert.Equal(0, safety.CurrentValue.MinOrderIntervalSeconds);
    }

    [Fact]
    public async Task ProfileGovernsPositionSize_OfRealOrders()
    {
        // Il profilo Prudente impegna il 5% del capitale; le soglie globali direbbero 50%.
        // Se il cablaggio si rompesse, la posizione aperta sarebbe dieci volte più grande.
        var (engine, _) = await BuildAsync(RiskProfiles.Prudente, i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));

        var pos = Assert.Single(await engine.GetOpenPositionsAsync());
        var notional = pos.Quantity * pos.EntryPrice;
        Assert.InRange(notional, 4_500m, 5_500m);   // ≈5% di 100.000, non ≈50.000
    }

    [Fact]
    public async Task ProfileTurnoverCap_ThrottlesNewEntries()
    {
        // "Prudente" consente ~0,5 operazioni al giorno (24h fra ingressi). Su candele ORARIE, la
        // prima apertura passa e le RIENTRATE successive devono essere rifiutate dal SafetyChecker.
        //
        // Lo script alterna Long/Close di proposito: con un Long costante il motore aprirebbe una
        // volta sola e non ritenterebbe più finché la posizione resta aperta, quindi il tetto non
        // verrebbe mai messo alla prova. È la chiusura che rimette il motore in condizione di
        // tentare un nuovo ingresso — ed è lì che il limite deve mordere.
        var (engine, _) = await BuildAsync(RiskProfiles.Prudente, i => i % 2 == 0 ? Signal.Long : Signal.Close);
        await engine.StartAsync(TradingMode.Paper);

        for (var i = 0; i <= 10; i++) await engine.ProcessCandleAsync(Candle(i, 100m));

        await using var db = await _provider!.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
        var rejected = await db.Orders.AsNoTracking().CountAsync(o => o.Status == OrderStatus.Rejected);
        var filled = await db.Orders.AsNoTracking().CountAsync(o => o.Status == OrderStatus.Filled);

        Assert.True(filled >= 1, "almeno la prima apertura deve passare");
        Assert.True(rejected > 0, "il tetto di turnover del profilo deve aver rifiutato le aperture successive");
    }

    [Fact]
    public async Task UnknownProfileName_FallsBackToGlobal_WithoutBlockingTheLane()
    {
        // Un profilo rinominato o rimosso non deve impedire l'avvio della corsia.
        var (engine, safety) = await BuildAsync("ProfiloInesistente", _ => Signal.Hold);

        await engine.StartAsync(TradingMode.Paper);

        Assert.Null(safety.Profile);
        Assert.Equal(50m, safety.CurrentValue.PositionSizePercent);
        Assert.True((await engine.GetStatusAsync()).IsRunning);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
