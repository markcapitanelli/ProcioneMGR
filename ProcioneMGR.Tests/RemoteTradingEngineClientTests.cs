using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="RemoteTradingEngineClient"/> (Fase 2b microservizi), sui due aspetti che NON
/// passano da gRPC (quello è coperto da <see cref="TradingGrpcRoundTripTests"/>):
///
/// 1. Le due letture di ordini che bypassano il servizio e interrogano Postgres direttamente,
///    confrontate CONTRO IL MOTORE VERO sullo stesso database. Nate come prova dell'affermazione
///    "identiche riga per riga" quando il client portava una COPIA delle query; oggi entrambi i
///    lati compongono da TradingOrderQueries e la deriva è impossibile per costruzione — il
///    confronto resta come cintura: fallirebbe se qualcuno reintroducesse una query locale
///    scavalcando l'helper. (Nota onesta sul suo limite, ed è parte del perché l'helper esiste:
///    il confronto vede solo le dimensioni presenti nei dati seminati — un filtro aggiunto su una
///    colonna che qui non varia produrrebbe risultati identici comunque.)
/// 2. I due metodi del ciclo worker, che devono lanciare invece di fingere un no-op.
/// </summary>
[Collection("Postgres")]
public class RemoteTradingEngineClientTests(PostgresFixture pg)
{
    private sealed class FakeMasterKeyStatus : IMasterKeyStatus
    {
        public bool IsDefaultDevKey => false;
    }

    /// <summary>
    /// Un provider col cono completo del motore su un DB reale isolato, così da poter mettere a
    /// confronto il TradingEngine vero e il client remoto sugli stessi dati.
    /// </summary>
    private ServiceProvider BuildProvider(out string connectionString)
    {
        connectionString = pg.CreateDatabase();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgresConnection"] = connectionString,
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddOptions();
        services.Configure<SafetyConfiguration>(config.GetSection("Trading:Safety"));
        services.Configure<LiveExecutionOptions>(config.GetSection("Trading:LiveExecution"));
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddSingleton<IMasterKeyStatus, FakeMasterKeyStatus>();
        services.AddProcioneDatabase(config);
        services.AddExchangeClients();
        services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();
        services.AddSingleton<IMarketFeatureExtractor, MarketFeatureExtractor>();
        services.AddSingleton<IRegimeDetector, RegimeDetector>();
        services.AddSingleton<IStrategyFactory, StrategyFactory>();
        services.AddSingleton<IAlphaFactorFactory, AlphaFactorFactory>();
        services.AddSingleton<IStrategyDecayMonitor, StrategyDecayMonitor>();
        services.AddSingleton<IExecutionAlgorithmFactory, ExecutionAlgorithmFactory>();
        services.AddSingleton<IFactorCache>(_ => new FactorCache(new FactorCacheOptions()));
        services.AddScoped<IBacktestEngine, BacktestEngine>();
        services.AddSingleton(new ModelRegistryOptions());
        services.AddSingleton<IModelRegistry, ModelRegistry>();
        services.AddSingleton<ProcioneMetrics>();
        // Toggle off: vogliamo il TradingEngine VERO come termine di paragone.
        services.AddTradingLanes(config);

        return services.BuildServiceProvider();
    }

    private static RemoteTradingEngineClient ClientFor(int laneId, IServiceProvider sp) =>
        new(laneId,
            // I due metodi sotto test non toccano gRPC: il canale non viene mai usato.
            client: null!,
            sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            NullLogger<RemoteTradingEngineClient>.Instance);

    private static Order NewOrder(int laneId, string id, DateTime createdAt, OrderStatus status, TradingMode mode) => new()
    {
        LaneId = laneId,
        OrderId = id,
        ClientOrderId = id,
        PositionId = "pos-" + id,
        StrategyId = "s",
        Symbol = "BTCUSDT",
        Side = OrderSide.Buy,
        Type = OrderType.Market,
        Quantity = 1m,
        Price = 100m,
        Status = status,
        CreatedAtUtc = createdAt,
        Mode = mode,
    };

    [Fact]
    public async Task GetOrderHistoryAsync_MatchesTheRealEngine_OnTheSameDatabase()
    {
        await using var sp = BuildProvider(out _);
        var dbFactory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var t0 = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            db.Orders.AddRange(
                NewOrder(0, "a", t0, OrderStatus.Filled, TradingMode.Paper),
                NewOrder(0, "b", t0.AddHours(2), OrderStatus.Pending, TradingMode.Live),
                NewOrder(0, "c", t0.AddHours(1), OrderStatus.Cancelled, TradingMode.Testnet),
                // Corsia diversa: non deve comparire in nessuna delle due letture.
                NewOrder(1, "other", t0.AddHours(3), OrderStatus.Filled, TradingMode.Paper));
            await db.SaveChangesAsync();
        }

        var engine = sp.GetRequiredKeyedService<ITradingEngine>(0);
        var remote = ClientFor(0, sp);

        var fromEngine = await engine.GetOrderHistoryAsync();
        var fromRemote = await remote.GetOrderHistoryAsync();

        Assert.Equal(
            fromEngine.Select(o => o.OrderId).ToList(),
            fromRemote.Select(o => o.OrderId).ToList());
        // Ordinamento (più recenti prima) e isolamento di corsia, esplicitati per non dipendere solo
        // dal confronto: se entrambe le implementazioni sbagliassero allo stesso modo, l'uguaglianza
        // passerebbe comunque.
        Assert.Equal(new[] { "b", "c", "a" }, fromRemote.Select(o => o.OrderId));
        Assert.DoesNotContain(fromRemote, o => o.OrderId == "other");
    }

    [Fact]
    public async Task GetOrderHistoryAsync_HonoursTheFromFilter_LikeTheRealEngine()
    {
        await using var sp = BuildProvider(out _);
        var dbFactory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var t0 = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Orders.AddRange(
                NewOrder(0, "old", t0, OrderStatus.Filled, TradingMode.Paper),
                NewOrder(0, "new", t0.AddDays(5), OrderStatus.Filled, TradingMode.Paper));
            await db.SaveChangesAsync();
        }

        var cutoff = t0.AddDays(1);
        var fromEngine = await sp.GetRequiredKeyedService<ITradingEngine>(0).GetOrderHistoryAsync(cutoff);
        var fromRemote = await ClientFor(0, sp).GetOrderHistoryAsync(cutoff);

        Assert.Equal(fromEngine.Select(o => o.OrderId), fromRemote.Select(o => o.OrderId));
        Assert.Equal(new[] { "new" }, fromRemote.Select(o => o.OrderId));
    }

    [Fact]
    public async Task GetPendingOrdersAsync_MatchesTheRealEngine_OnlyLivePending()
    {
        await using var sp = BuildProvider(out _);
        var dbFactory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var t0 = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Orders.AddRange(
                // L'unico che deve comparire: Pending E Live.
                NewOrder(0, "live-pending", t0.AddHours(1), OrderStatus.Pending, TradingMode.Live),
                NewOrder(0, "live-filled", t0, OrderStatus.Filled, TradingMode.Live),
                NewOrder(0, "paper-pending", t0, OrderStatus.Pending, TradingMode.Paper),
                NewOrder(1, "other-lane", t0, OrderStatus.Pending, TradingMode.Live));
            await db.SaveChangesAsync();
        }

        var fromEngine = await sp.GetRequiredKeyedService<ITradingEngine>(0).GetPendingOrdersAsync();
        var fromRemote = await ClientFor(0, sp).GetPendingOrdersAsync();

        Assert.Equal(fromEngine.Select(o => o.OrderId), fromRemote.Select(o => o.OrderId));
        Assert.Equal(new[] { "live-pending" }, fromRemote.Select(o => o.OrderId));
    }

    [Fact]
    public async Task ProcessCandleAsync_Throws_BecauseTheWorkerLivesInTheRemoteService()
    {
        // Un no-op silenzioso qui farebbe sembrare che le candele vengano elaborate mentre nessuno
        // le elabora: un errore di composizione DI resterebbe invisibile fino al primo trade mancato.
        var remote = new RemoteTradingEngineClient(0, client: null!, dbFactory: null!, NullLogger<RemoteTradingEngineClient>.Instance);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => remote.ProcessCandleAsync(new OhlcvData()));
        Assert.Contains("TradingWorker", ex.Message);
    }

    [Fact]
    public async Task ProcessDueExecutionSlicesAsync_Throws_BecauseTheWorkerLivesInTheRemoteService()
    {
        var remote = new RemoteTradingEngineClient(0, client: null!, dbFactory: null!, NullLogger<RemoteTradingEngineClient>.Instance);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => remote.ProcessDueExecutionSlicesAsync());
        Assert.Contains("ExecutionWorker", ex.Message);
    }

    [Fact]
    public void LaneId_IsExposed_ForKeyedResolution()
    {
        var remote = new RemoteTradingEngineClient(2, client: null!, dbFactory: null!, NullLogger<RemoteTradingEngineClient>.Instance);
        Assert.Equal(2, remote.LaneId);
    }
}
