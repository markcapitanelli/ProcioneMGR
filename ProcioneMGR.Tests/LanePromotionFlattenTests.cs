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
/// [M2] Promozione/retrocessione di corsia senza mescolare i mondi: flatten PRIMA del cambio
/// modalità (senza emergency stop), discriminatore <see cref="OpenPosition.OpenedInMode"/> con
/// purge delle righe di un'altra modalità al load, e i confini del <see cref="LanePromoter"/>
/// (ordine delle chiamate, Live sempre vietato).
/// </summary>
[Collection("Postgres")]
public sealed class LanePromotionFlattenTests : IAsyncDisposable
{
    private readonly string _connString;
    private readonly List<ServiceProvider> _providers = new();

    public LanePromotionFlattenTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    // --- Fakes engine reale (Paper) ------------------------------------------------------------

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

    private async Task<(TradingEngine Engine, IDbContextFactory<ApplicationDbContext> DbFactory)> BuildRealEngineAsync(Func<int, Signal> script)
    {
        var provider = BuildProvider();
        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 100_000m,
            Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "Scripted", DisplayName = "Scripted", IsActive = true }],
        };
        var engine = new TradingEngine(
            0, dbFactory, new ScriptedStrategyFactory(script), new TechnicalIndicatorsService(),
            new ThrowingExchangeClientFactory(), new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(new SafetyConfiguration { MinOrderIntervalSeconds = 0 }),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance);
        return (engine, dbFactory);
    }

    private ServiceProvider BuildProvider(Action<ServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        extra?.Invoke(services);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close, Low = close, Close = close, Volume = 100m,
    };

    // --- Test: CloseAllPositionsAsync (flatten senza emergenza) ---------------------------------

    [Fact]
    public async Task CloseAll_ClosesEverything_WithoutEmergencyStop()
    {
        var (engine, dbFactory) = await BuildRealEngineAsync(i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);
        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));   // apre
        Assert.Single(await engine.GetOpenPositionsAsync());

        await engine.CloseAllPositionsAsync("LaneModeChange:Paper->Testnet");

        Assert.Empty(await engine.GetOpenPositionsAsync());
        var status = await engine.GetStatusAsync();
        Assert.False(status.IsEmergencyStopped);   // flatten ≠ emergenza: la corsia resta usabile
        Assert.True(status.IsRunning);             // e non viene nemmeno fermata (lo fa il promoter dopo)

        await using var db = await dbFactory.CreateDbContextAsync();
        var trade = await db.TradeRecords.SingleAsync();
        Assert.Equal("LaneModeChange:Paper->Testnet", trade.ExitReason);
        var audits = await db.TradingAuditLogs.Select(a => a.Action).ToListAsync();
        Assert.Contains("CloseAllPositions", audits);
        Assert.DoesNotContain("EmergencyStop", audits);
    }

    [Fact]
    public async Task CloseAll_NoPositions_NoAuditNoise()
    {
        var (engine, dbFactory) = await BuildRealEngineAsync(_ => Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);

        await engine.CloseAllPositionsAsync("flatten");

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.DoesNotContain(await db.TradingAuditLogs.Select(a => a.Action).ToListAsync(),
            a => a == "CloseAllPositions");   // niente rumore di audit se non c'era nulla da chiudere
    }

    // --- Test: filtro OpenedInMode + purge al load ----------------------------------------------

    [Fact]
    public async Task EnsureLoaded_PurgesPositionsFromOtherMode_WithAudit()
    {
        // Sessione Paper con una posizione vera.
        var (engine, dbFactory) = await BuildRealEngineAsync(i => i == 4 ? Signal.Long : Signal.Hold);
        await engine.StartAsync(TradingMode.Paper);
        for (var i = 0; i <= 4; i++) await engine.ProcessCandleAsync(Candle(i, 100m));

        // Riga "estranea": stessa lane ma aperta in Testnet (residuo di un cambio modalità a metà).
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.OpenPositions.Add(new OpenPosition
            {
                LaneId = 0, PositionId = "ghost-1", StrategyId = "s1", Symbol = "BTC/USDT",
                Side = OrderSide.Buy, EntryPrice = 90m, Quantity = 1m, OpenedAtUtc = DateTime.UtcNow,
                CurrentPrice = 90m, OpenedInMode = TradingMode.Testnet, MarginBalance = 90m,
            });
            await db.SaveChangesAsync();
        }

        // "Riavvio": istanza nuova sullo stesso DB → EnsureLoaded filtra e purga.
        var (restarted, _) = await BuildRealEngineAsync(_ => Signal.Hold);
        var positions = await restarted.GetOpenPositionsAsync();

        var survivor = Assert.Single(positions);
        Assert.Equal(TradingMode.Paper, survivor.OpenedInMode);   // solo la posizione della modalità corrente

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            Assert.Equal(1, await db.OpenPositions.CountAsync());                 // ghost eliminato dal DB
            Assert.Null(await db.OpenPositions.FirstOrDefaultAsync(p => p.PositionId == "ghost-1"));
            Assert.Contains(await db.TradingAuditLogs.ToListAsync(),
                a => a.Action == "StalePositionsPurged" && a.Details.Contains("ghost-1"));
        }
    }

    // --- Test: LanePromoter (ordine chiamate + confine Live) ------------------------------------

    /// <summary>Engine finto che REGISTRA le chiamate: verifica l'ordine flatten → stop → start.</summary>
    private sealed class RecordingEngine : ITradingEngine
    {
        public List<string> Calls { get; } = new();
        public TradingMode Mode { get; set; } = TradingMode.Paper;

        public int LaneId => 0;
        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
        {
            Calls.Add("GetStatus");
            return Task.FromResult(new TradingEngineStatus { Mode = Mode, Symbol = "BTC/USDT", IsRunning = true });
        }
        public Task StartAsync(TradingMode mode, CancellationToken ct = default) { Calls.Add($"Start:{mode}"); Mode = mode; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { Calls.Add("Stop"); return Task.CompletedTask; }
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) { Calls.Add("Emergency"); return Task.CompletedTask; }
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => Task.FromResult(new List<OpenPosition>());
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) { Calls.Add("ClosePosition"); return Task.CompletedTask; }
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) { Calls.Add($"CloseAll:{reason}"); return Task.CompletedTask; }
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new TradingPerformance());
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private (LanePromoter Promoter, RecordingEngine Engine, IDbContextFactory<ApplicationDbContext> DbFactory) BuildPromoter()
    {
        var engine = new RecordingEngine();
        var provider = BuildProvider(s => s.AddKeyedSingleton<ITradingEngine>(0, engine));
        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var promoter = new LanePromoter(provider, dbFactory, new PromotionEvaluatorOptions().AsMonitor(), NullLogger<LanePromoter>.Instance);
        return (promoter, engine, dbFactory);
    }

    [Fact]
    public async Task Promoter_FlattensBeforeStopAndRestart_InOrder()
    {
        var (promoter, engine, dbFactory) = BuildPromoter();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        await promoter.PromoteLaneAsync(0, TradingMode.Testnet, "criteri soddisfatti");

        var closeAll = engine.Calls.FindIndex(c => c.StartsWith("CloseAll:"));
        var stop = engine.Calls.IndexOf("Stop");
        var start = engine.Calls.IndexOf("Start:Testnet");
        Assert.True(closeAll >= 0, "flatten mai invocato");
        Assert.True(closeAll < stop && stop < start, $"ordine sbagliato: {string.Join(" → ", engine.Calls)}");
        Assert.Contains("Paper->Testnet", engine.Calls[closeAll]);   // la reason documenta la transizione
        Assert.DoesNotContain("Emergency", engine.Calls);
    }

    [Fact]
    public async Task Promoter_ToLive_StillForbidden_NoEngineCalls()
    {
        var (promoter, engine, _) = BuildPromoter();

        await Assert.ThrowsAsync<InvalidOperationException>(() => promoter.PromoteLaneAsync(0, TradingMode.Live, "mai"));

        Assert.Empty(engine.Calls);   // il confine scatta PRIMA di toccare il motore
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers) await p.DisposeAsync();
    }
}
