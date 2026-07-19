using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Services.Trading.Internal;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test DEDICATO di <see cref="FuturesPositionReconciler"/> — colma il gap segnalato ma non chiuso in
/// Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.8: "resta senza un test reale dedicato — copertura
/// solo indiretta via ProcessCandleAsync") ed emerso di nuovo nell'audit leggero §8: il fake futures di
/// <see cref="TradingEngineReconcileTests"/> ritorna <c>GetPositionAsync = null</c> fisso e l'unico test
/// futures apre una posizione e si ferma PRIMA che la riconciliazione scatti — quindi i tre rami reali
/// del riconciliatore (chiusura forzata su flat remoto, allerta-una-volta su posizione remota non
/// tracciata, no-op su posizione combaciante) non erano mai esercitati direttamente.
///
/// È un percorso a soldi veri: il ramo "flat remoto + aperta in locale" forza la chiusura al miglior
/// prezzo noto come <c>Liquidation/ExternalClose</c> e registra un trade liquidato. Qui il collaboratore
/// è testato in isolamento — esattamente ciò per cui l'Intervento B lo ha estratto — con un
/// <c>GetPositionAsync</c> controllabile e un delegato di chiusura che registra le invocazioni invece di
/// muovere davvero capitale.
/// </summary>
[Collection("Postgres")]
public sealed class FuturesPositionReconcilerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;
    private IDbContextFactory<ApplicationDbContext>? _dbFactory;

    public FuturesPositionReconcilerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private static readonly TradingCredentials Creds = new("k", "s", null, IsTestnet: true);
    private const decimal LastKnownPrice = 123.45m;

    // --- Fakes -------------------------------------------------------------------------------

    /// <summary>Futures client il cui solo <see cref="GetPositionAsync"/> è significativo e controllabile.</summary>
    private sealed class ConfigurableFuturesClient : IFuturesExchangeClient
    {
        public Func<FuturesPosition?> PositionProvider { get; set; } = static () => null;
        public bool ThrowOnGetPosition { get; set; }
        public int GetPositionCalls { get; private set; }

        public ExchangeName Exchange => ExchangeName.Binance;

        public Task<FuturesPosition?> GetPositionAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)
        {
            GetPositionCalls++;
            if (ThrowOnGetPosition) throw new InvalidOperationException("lettura posizione futures giù (simulato)");
            return Task.FromResult(PositionProvider());
        }

        public Task<SetLeverageResult> SetLeverageAsync(string symbol, int leverage, TradingCredentials credentials, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PlaceOrderResult> PlaceFuturesOrderAsync(PlaceOrderRequest request, bool reduceOnly, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PlaceOrderResult> PlaceFuturesTriggerOrderAsync(PlaceOrderRequest request, bool isStopLoss, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CancelOrderResult> CancelFuturesOrderAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<OpenOrder>> GetOpenFuturesOrdersAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<OrderStatusResult> GetFuturesOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FuturesBalance> GetFuturesBalanceAsync(TradingCredentials credentials, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SymbolFilters> GetFuturesSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<decimal> GetFundingRateAsync(string symbol, bool testnet, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FuturesOnlyFactory(IFuturesExchangeClient futures) : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => throw new NotImplementedException();
        public IExchangeClient Create(string exchangeName) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => futures;
        public IFuturesExchangeClient CreateFutures(string exchangeName) => futures;
    }

    // --- Setup -------------------------------------------------------------------------------

    private async Task<TradingPersistence> BuildPersistenceAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();
        _dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }
        return new TradingPersistence(_dbFactory, laneId: 0);
    }

    private static TradingEngineState FuturesState(TradingMode mode = TradingMode.Testnet, string symbol = "BTC/USDT") => new()
    {
        LaneId = 0,
        Mode = mode,
        MarketType = MarketType.Futures,
        ExchangeName = "Binance",
        Symbol = symbol,
    };

    private static OpenPosition Position(string id, string symbol = "BTC/USDT", OrderSide side = OrderSide.Buy) => new()
    {
        PositionId = id,
        StrategyId = "s1",
        Symbol = symbol,
        Side = side,
        Quantity = 1m,
        EntryPrice = 100m,
        OpenedInMode = TradingMode.Testnet,
        Leverage = 5,
    };

    private static FuturesPosition RemotePosition(string symbol = "BTC/USDT") =>
        new() { Symbol = symbol, Side = "LONG", Quantity = 1m, EntryPrice = 100m };

    private FuturesPositionReconciler BuildReconciler(IFuturesExchangeClient futures, TradingPersistence persistence) =>
        new(new FuturesOnlyFactory(futures), NullLogger.Instance, persistence);

    private async Task<List<TradingAuditLog>> AuditsAsync()
    {
        await using var db = await _dbFactory!.CreateDbContextAsync();
        return await db.TradingAuditLogs.AsNoTracking().ToListAsync();
    }

    private static (List<(OpenPosition Pos, decimal Price, string Reason, bool AlreadyClosed)> Log,
        Func<OpenPosition, decimal, string, DateTime, CancellationToken, bool, Task> Delegate) CloseRecorder()
    {
        var log = new List<(OpenPosition, decimal, string, bool)>();
        Task Close(OpenPosition pos, decimal price, string reason, DateTime ts, CancellationToken ct, bool alreadyClosed)
        {
            log.Add((pos, price, reason, alreadyClosed));
            return Task.CompletedTask;
        }
        return (log, Close);
    }

    // --- Ramo 1: FLAT remoto + APERTA in locale → chiusura forzata -----------------------------

    [Fact]
    public async Task RemoteFlat_LocalOpen_ForceClosesAtLastKnownPrice_AsExternalClose()
    {
        var persistence = await BuildPersistenceAsync();
        var futures = new ConfigurableFuturesClient { PositionProvider = () => null };   // flat sull'exchange
        var (closes, closeDelegate) = CloseRecorder();
        var positions = new List<OpenPosition> { Position("p1") };

        var alerted = await BuildReconciler(futures, persistence).ReconcileAsync(
            FuturesState(), positions, Creds, closeDelegate,
            untrackedRemoteAlerted: false, LastKnownPrice, DateTime.UtcNow, CancellationToken.None);

        Assert.False(alerted);
        var (pos, price, reason, already) = Assert.Single(closes);
        Assert.Equal("p1", pos.PositionId);
        Assert.Equal(LastKnownPrice, price);
        Assert.Equal("Liquidation/ExternalClose", reason);
        Assert.True(already);   // alreadyClosedOnExchange = true: nessun ordine di chiusura reale va inviato
    }

    [Fact]
    public async Task RemoteFlat_ClosesOnlyPositionsForThisSymbol()
    {
        var persistence = await BuildPersistenceAsync();
        var futures = new ConfigurableFuturesClient { PositionProvider = () => null };
        var (closes, closeDelegate) = CloseRecorder();
        var positions = new List<OpenPosition>
        {
            Position("btc", "BTC/USDT"),
            Position("eth", "ETH/USDT"),   // corsia diversa: NON deve essere toccata
        };

        await BuildReconciler(futures, persistence).ReconcileAsync(
            FuturesState(symbol: "BTC/USDT"), positions, Creds, closeDelegate,
            untrackedRemoteAlerted: false, LastKnownPrice, DateTime.UtcNow, CancellationToken.None);

        var closed = Assert.Single(closes);
        Assert.Equal("btc", closed.Pos.PositionId);
    }

    // --- Ramo 2: APERTA sull'exchange + SCONOSCIUTA al motore → allerta una sola volta ----------

    [Fact]
    public async Task RemoteOpen_LocalUnknown_NotYetAlerted_AlertsOnceAndAudits()
    {
        var persistence = await BuildPersistenceAsync();
        var futures = new ConfigurableFuturesClient { PositionProvider = () => RemotePosition() };
        var (closes, closeDelegate) = CloseRecorder();

        var alerted = await BuildReconciler(futures, persistence).ReconcileAsync(
            FuturesState(), positions: [], Creds, closeDelegate,
            untrackedRemoteAlerted: false, LastKnownPrice, DateTime.UtcNow, CancellationToken.None);

        Assert.True(alerted);                 // la difesa inversa NON chiude d'ufficio: solo allerta
        Assert.Empty(closes);
        Assert.Contains(await AuditsAsync(), a => a.Action == "UntrackedRemotePosition");
    }

    [Fact]
    public async Task RemoteOpen_LocalUnknown_AlreadyAlerted_StaysAlerted_NoDuplicateAudit()
    {
        var persistence = await BuildPersistenceAsync();
        var futures = new ConfigurableFuturesClient { PositionProvider = () => RemotePosition() };
        var (closes, closeDelegate) = CloseRecorder();

        var alerted = await BuildReconciler(futures, persistence).ReconcileAsync(
            FuturesState(), positions: [], Creds, closeDelegate,
            untrackedRemoteAlerted: true, LastKnownPrice, DateTime.UtcNow, CancellationToken.None);

        Assert.True(alerted);
        Assert.Empty(closes);
        Assert.Empty(await AuditsAsync());    // idempotente: nessun secondo log finché la condizione persiste
    }

    // --- Ramo 3: APERTA sull'exchange + tracciata in locale → nessuna azione, reset allerta ------

    [Fact]
    public async Task RemoteOpen_LocalKnown_NoAction_ResetsAlertFlag()
    {
        var persistence = await BuildPersistenceAsync();
        var futures = new ConfigurableFuturesClient { PositionProvider = () => RemotePosition() };
        var (closes, closeDelegate) = CloseRecorder();
        var positions = new List<OpenPosition> { Position("p1") };

        var alerted = await BuildReconciler(futures, persistence).ReconcileAsync(
            FuturesState(), positions, Creds, closeDelegate,
            untrackedRemoteAlerted: true, LastKnownPrice, DateTime.UtcNow, CancellationToken.None);

        Assert.False(alerted);                // combacia: l'allerta pregressa si spegne
        Assert.Empty(closes);
        Assert.Empty(await AuditsAsync());
    }

    // --- Guardie: nessuna chiamata all'exchange, stato di allerta invariato ---------------------

    [Fact]
    public async Task PaperMode_SkipsEntirely_NoExchangeCall()
    {
        var persistence = await BuildPersistenceAsync();
        var futures = new ConfigurableFuturesClient { PositionProvider = () => null };
        var (closes, closeDelegate) = CloseRecorder();
        var positions = new List<OpenPosition> { Position("p1") };

        var alerted = await BuildReconciler(futures, persistence).ReconcileAsync(
            FuturesState(TradingMode.Paper), positions, Creds, closeDelegate,
            untrackedRemoteAlerted: true, LastKnownPrice, DateTime.UtcNow, CancellationToken.None);

        Assert.True(alerted);                 // input restituito invariato
        Assert.Equal(0, futures.GetPositionCalls);
        Assert.Empty(closes);
    }

    [Fact]
    public async Task NoCredentials_SkipsEntirely_NoExchangeCall()
    {
        var persistence = await BuildPersistenceAsync();
        var futures = new ConfigurableFuturesClient { PositionProvider = () => null };
        var (closes, closeDelegate) = CloseRecorder();
        var positions = new List<OpenPosition> { Position("p1") };

        var alerted = await BuildReconciler(futures, persistence).ReconcileAsync(
            FuturesState(), positions, credsOrNull: null, closeDelegate,
            untrackedRemoteAlerted: false, LastKnownPrice, DateTime.UtcNow, CancellationToken.None);

        Assert.False(alerted);
        Assert.Equal(0, futures.GetPositionCalls);
        Assert.Empty(closes);
    }

    // --- Errore di rete sulla lettura posizione → salta il ciclo, non chiude nulla --------------

    [Fact]
    public async Task NetworkError_OnGetPosition_SkipsCycle_LeavesAlertUnchanged_NoClose()
    {
        var persistence = await BuildPersistenceAsync();
        var futures = new ConfigurableFuturesClient { ThrowOnGetPosition = true };
        var (closes, closeDelegate) = CloseRecorder();
        var positions = new List<OpenPosition> { Position("p1") };

        var alerted = await BuildReconciler(futures, persistence).ReconcileAsync(
            FuturesState(), positions, Creds, closeDelegate,
            untrackedRemoteAlerted: true, LastKnownPrice, DateTime.UtcNow, CancellationToken.None);

        Assert.True(alerted);                 // il fallimento di rete non altera lo stato: si ritenta al ciclo dopo
        Assert.Empty(closes);                 // MAI chiudere una posizione su una lettura fallita
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
