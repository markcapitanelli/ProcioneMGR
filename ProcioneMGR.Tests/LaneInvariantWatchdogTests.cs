using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Regressione della Fase 0-A3 (PRD Autonomia Operativa §3): il watchdog degli invarianti
/// contabili deve accorgersi DA SOLO dello stato in cui la corsia 2 è rimasta per ore il
/// 2026-07-18 (PnL -1,8M su capitale 10k) — quarantena persistita, trading fermato, posizioni
/// LASCIATE aperte, audit scritto — e <c>TradingEngine.StartAsync</c> deve rifiutare il riavvio
/// finché un umano non rimuove la quarantena (che un riavvio azzererebbe capitale/PnL,
/// cancellando l'evidenza).
/// </summary>
[Collection("Postgres")]
public sealed class LaneInvariantWatchdogTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public LaneInvariantWatchdogTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    // --- Fakes -------------------------------------------------------------------------------

    /// <summary>Solo StopAsync è lecito: il watchdog non deve MAI chiudere posizioni o fare altro.</summary>
    private sealed class StopOnlyEngine(int laneId) : ITradingEngine
    {
        public int LaneId => laneId;
        public int StopCalls { get; private set; }

        /// <summary>[E6] Status pilotabile per i test del battito; null = il fake non lo espone (come prima).</summary>
        public TradingEngineStatus? StatusToReturn { get; set; }

        public Task StopAsync(CancellationToken ct = default) { StopCalls++; return Task.CompletedTask; }

        public Task StartAsync(TradingMode mode, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default) =>
            StatusToReturn is { } s ? Task.FromResult(s) : throw new NotImplementedException();
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class HoldStrategy : IStrategy
    {
        public string Name => "Hold";
        public string DisplayName => "Hold";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];
        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct) => Task.CompletedTask;
        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp) => Signal.Hold;
    }

    private sealed class HoldStrategyFactory : IStrategyFactory
    {
        public IReadOnlyList<IStrategy> Prototypes => [];
        public IStrategy Create(string strategyName) => new HoldStrategy();
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

    private sealed class ThrowingExchangeFactory : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => throw new NotImplementedException();
        public IExchangeClient Create(string exchangeName) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotImplementedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotImplementedException();
    }

    // --- Setup -------------------------------------------------------------------------------

    private sealed class RecordingNotifier : ProcioneMGR.Services.Notifications.INotifier
    {
        public List<(ProcioneMGR.Services.Notifications.NotificationSeverity Severity, string Title)> Sent { get; } = new();
        public Task NotifyAsync(ProcioneMGR.Services.Notifications.NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Sent.Add((severity, title));
            return Task.CompletedTask;
        }
    }

    private async Task<(LaneInvariantWatchdog Watchdog, IDbContextFactory<ApplicationDbContext> DbFactory,
        LaneQuarantineStore Store, StopOnlyEngine[] Engines)> BuildAsync(
        LaneInvariantOptions? options = null, ProcioneMGR.Services.Notifications.INotifier? notifier = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var engines = new StopOnlyEngine[TradingLanes.Count];
        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            engines[lane] = new StopOnlyEngine(lane);
            services.AddKeyedSingleton<ITradingEngine>(lane, engines[lane]);
        }
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var store = new LaneQuarantineStore(dbFactory, NullLogger<LaneQuarantineStore>.Instance);
        var watchdog = new LaneInvariantWatchdog(
            provider, dbFactory, store,
            (options ?? new LaneInvariantOptions()).AsMonitor(),
            NullLogger<LaneInvariantWatchdog>.Instance,
            notifier)
        {
            // Il watchdog nasce ADESSO, ma i test verificano digiuni di ore: senza spostare
            // indietro l'avvio del processo, la grazia post-riavvio li assolverebbe tutti e questi
            // test passerebbero senza provare nulla.
            StartedAtUtc = DateTime.UtcNow.AddDays(-7),
        };
        return (watchdog, dbFactory, store, engines);
    }

    /// <summary>Lo stato REALE della corsia 2 del 2026-07-18 (docs/TEST-UI-2026-07-18.md).</summary>
    private static TradingEngineState CorruptedCorsia2State() => new()
    {
        LaneId = 2,
        Mode = TradingMode.Testnet,
        MarketType = MarketType.Futures,
        Leverage = 2,
        IsRunning = true,
        ExchangeName = "Binance",
        Symbol = "ETH/USDT",
        Timeframe = "1h", // [J19] una corsia in corsa senza timeframe è ora una violazione a sé
        TotalCapital = 10_000m,
        AvailableCapital = -1_807_925.81m,
        RealizedPnl = -1_817_925.81m,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static TradingEngineState HealthyRunningState(int laneId) => new()
    {
        LaneId = laneId,
        Mode = TradingMode.Paper,
        IsRunning = true,
        // [J19] Una corsia sana in corsa ha simbolo e timeframe (vedi LaneInvariantChecker).
        Symbol = "BTC/USDT",
        Timeframe = "1h",
        Leverage = 1,
        TotalCapital = 10_000m,
        AvailableCapital = 9_500m,
        RealizedPnl = 120m,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static async Task SeedStateAsync(IDbContextFactory<ApplicationDbContext> dbFactory, params TradingEngineState[] states)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.TradingEngineStates.AddRange(states);
        await db.SaveChangesAsync();
    }

    // --- Test: il caso reale della corsia 2 --------------------------------------------------

    [Fact]
    public async Task Tick_RealCorsia2State_QuarantinesLane_StopsEngine_LeavesPositionsOpen()
    {
        var (watchdog, dbFactory, _, engines) = await BuildAsync();
        await SeedStateAsync(dbFactory, HealthyRunningState(0), CorruptedCorsia2State());
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            // La posizione ETH adottata dal fill patologico: deve restare APERTA (mai chiusure forzate).
            db.OpenPositions.Add(new OpenPosition
            {
                LaneId = 2, Symbol = "ETH/USDT", Quantity = 1_039.77125m, EntryPrice = 1_748.18m,
                CurrentPrice = 1_748.18m, OpenedInMode = TradingMode.Testnet, OpenedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await watchdog.TickAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        var quarantine = Assert.Single(await check.LaneQuarantines.AsNoTracking().ToListAsync());
        Assert.Equal(2, quarantine.LaneId);
        Assert.Contains("AvailableCapital negativo", quarantine.Reason);
        Assert.Contains("PnL totale", quarantine.Reason);
        Assert.Contains("Nozionale aperto fuori scala", quarantine.Reason);

        var audit = Assert.Single(await check.TradingAuditLogs.AsNoTracking().Where(a => a.Action == "LaneQuarantined").ToListAsync());
        Assert.Equal(2, audit.LaneId);
        Assert.Equal(TradingMode.Testnet, audit.Mode);

        Assert.Equal(1, engines[2].StopCalls);   // trading fermato...
        Assert.Equal(0, engines[0].StopCalls);   // ...solo della corsia violata
        Assert.Equal(1, await check.OpenPositions.CountAsync()); // posizioni MAI chiuse dal watchdog
    }

    [Fact]
    public async Task Tick_HealthyAndStoppedLanes_NoAction()
    {
        var (watchdog, dbFactory, _, engines) = await BuildAsync();
        var stoppedCorrupted = CorruptedCorsia2State();
        stoppedCorrupted.IsRunning = false; // corsia ferma: si azzera al prossimo StartAsync, non si quarantena
        await SeedStateAsync(dbFactory, HealthyRunningState(0), stoppedCorrupted);

        await watchdog.TickAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.Empty(await check.LaneQuarantines.AsNoTracking().ToListAsync());
        Assert.All(engines, e => Assert.Equal(0, e.StopCalls));
    }

    [Fact]
    public async Task Tick_SecondPass_DoesNotDuplicateQuarantineOrStop()
    {
        var (watchdog, dbFactory, _, engines) = await BuildAsync();
        await SeedStateAsync(dbFactory, CorruptedCorsia2State());

        await watchdog.TickAsync(CancellationToken.None);
        await watchdog.TickAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.Single(await check.LaneQuarantines.AsNoTracking().ToListAsync());
        Assert.Single(await check.TradingAuditLogs.AsNoTracking().Where(a => a.Action == "LaneQuarantined").ToListAsync());
        Assert.Equal(1, engines[2].StopCalls);
    }

    [Fact]
    public async Task Tick_Disabled_NoActionEvenOnCorruptedLane()
    {
        var (watchdog, dbFactory, _, engines) = await BuildAsync(new LaneInvariantOptions { Enabled = false });
        await SeedStateAsync(dbFactory, CorruptedCorsia2State());

        await watchdog.TickAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.Empty(await check.LaneQuarantines.AsNoTracking().ToListAsync());
        Assert.Equal(0, engines[2].StopCalls);
    }

    [Fact]
    public async Task Tick_PositionsOfOtherMode_NotCountedInExposure()
    {
        // Filtro M2: una riga Paper residua su una corsia Testnet non deve quarantenare la corsia
        // (il motore stesso non la vede — EnsureLoadedAsync la purgherebbe).
        var (watchdog, dbFactory, _, engines) = await BuildAsync();
        var state = HealthyRunningState(1);
        state.Mode = TradingMode.Testnet;
        await SeedStateAsync(dbFactory, state);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.OpenPositions.Add(new OpenPosition
            {
                LaneId = 1, Symbol = "BTC/USDT", Quantity = 1_000m, EntryPrice = 100_000m,
                CurrentPrice = 100_000m, OpenedInMode = TradingMode.Paper, OpenedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await watchdog.TickAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.Empty(await check.LaneQuarantines.AsNoTracking().ToListAsync());
        Assert.Equal(0, engines[1].StopCalls);
    }

    [Fact]
    public async Task Tick_Quarantine_EmitsCriticalNotification()
    {
        // Fase 4 (PRD §7): la quarantena è il producer col valore più alto — deve chiamare l'umano.
        var notifier = new RecordingNotifier();
        var (watchdog, dbFactory, _, _) = await BuildAsync(notifier: notifier);
        await SeedStateAsync(dbFactory, CorruptedCorsia2State());

        await watchdog.TickAsync(CancellationToken.None);

        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(ProcioneMGR.Services.Notifications.NotificationSeverity.Critical, sent.Severity);
        Assert.Contains("QUARANTENA", sent.Title);
    }

    // --- Test: StartAsync rifiuta una corsia in quarantena ------------------------------------

    [Fact]
    public async Task StartAsync_QuarantinedLane_Refuses_UntilHumanClears()
    {
        var (_, dbFactory, store, _) = await BuildAsync();
        await store.TryQuarantineAsync(0, "AvailableCapital negativo: -1807925.81", "{}");

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 10_000m,
            Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "Hold", DisplayName = "Hold", IsActive = true }],
        };
        var engine = new TradingEngine(
            0, dbFactory, new HoldStrategyFactory(), new TechnicalIndicatorsService(),
            new ThrowingExchangeFactory(), new FakeEnsembleManager(config),
            new SafetyConfiguration { PositionSizePercent = 8m, MaxPositionSizePercent = 50m, MaxTotalExposurePercent = 100m }.AsMonitor(),
            new LiveExecutionOptions().AsMonitor(),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(TradingMode.Paper));
        Assert.Contains("QUARANTENA", ex.Message);

        // L'audit della rimozione porta il nome di chi decide.
        Assert.True(await store.ClearAsync(0, "admin-user"));
        await using (var check = await dbFactory.CreateDbContextAsync())
        {
            var cleared = Assert.Single(await check.TradingAuditLogs.AsNoTracking()
                .Where(a => a.Action == "LaneQuarantineCleared").ToListAsync());
            Assert.Equal("admin-user", cleared.UserId);
        }

        // Rimossa la quarantena, la corsia riparte normalmente.
        await engine.StartAsync(TradingMode.Paper);
        var status = await engine.GetStatusAsync();
        Assert.True(status.IsRunning);
    }

    // --- Test: posizioni su corsie che non esistono più ---------------------------------------

    /// <summary>
    /// Il caso reale trovato a database il 2026-07-28: dopo che le corsie sono state riorganizzate
    /// e <c>LaneCount</c> è tornato a 3, la corsia 3 è rimasta con una posizione Paper aperta su
    /// DOT/USDT — stop, take profit e trailing configurati, e nessun motore a valutarli. Il
    /// <c>CurrentPrice</c> era fermo al prezzo d'ingresso da più di un giorno.
    ///
    /// Il ciclo principale del watchdog non poteva vederla: itera su <c>0..LaneCount-1</c>, e una
    /// corsia fuori range non è fra quelle. Il suo commento — «uno stato corrotto a corsia ferma
    /// non può peggiorare, e verrà comunque azzerato dal prossimo StartAsync» — non vale per una
    /// corsia che non può più essere avviata: quel prossimo StartAsync non arriverà mai.
    /// </summary>
    [Fact]
    public async Task Tick_PosizioneSuCorsiaFuoriRange_AllertaSenzaChiudereNulla()
    {
        var notifier = new RecordingNotifier();
        var (watchdog, dbFactory, _, engines) = await BuildAsync(notifier: notifier);
        await SeedStateAsync(dbFactory, HealthyRunningState(0));

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.OpenPositions.Add(new OpenPosition
            {
                LaneId = TradingLanes.Count,        // la corsia appena oltre l'ultima configurata
                Symbol = "DOT/USDT",
                Side = OrderSide.Buy,
                Quantity = 980.39215m,
                EntryPrice = 0.816m,
                CurrentPrice = 0.816m,              // mai aggiornato: nessuno la marca a mercato
                StopLoss = 0.7856448m,
                TakeProfit = 0.9002112m,
                TrailingStopPercent = 8m,
                OpenedInMode = TradingMode.Paper,
                OpenedAtUtc = DateTime.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        await watchdog.TickAsync(CancellationToken.None);

        var alert = Assert.Single(notifier.Sent);
        Assert.Equal(ProcioneMGR.Services.Notifications.NotificationSeverity.Critical, alert.Severity);
        Assert.Contains("orfane", alert.Title);

        await using var check = await dbFactory.CreateDbContextAsync();

        // NESSUNA azione automatica: la posizione resta dov'è, nessuna quarantena inventata su una
        // corsia che non esiste, nessun motore fermato. Stessa filosofia della difesa inversa —
        // su uno stato che non capiamo, il gesto irreversibile è quello sbagliato.
        Assert.Single(await check.OpenPositions.AsNoTracking().ToListAsync());
        Assert.Empty(await check.LaneQuarantines.AsNoTracking().ToListAsync());
        Assert.All(engines, e => Assert.Equal(0, e.StopCalls));
    }

    /// <summary>
    /// L'allarme non si ripete a ogni tick. Un critico che arriva ogni trenta secondi smette di
    /// essere letto entro il primo pomeriggio, e allora tanto vale non averlo (stessa regola del
    /// watchdog di staleness del feed, che allerta una volta per transizione).
    /// </summary>
    [Fact]
    public async Task Tick_PosizioneOrfana_AllertaUnaVoltaSola()
    {
        var notifier = new RecordingNotifier();
        var (watchdog, dbFactory, _, _) = await BuildAsync(notifier: notifier);
        await SeedStateAsync(dbFactory, HealthyRunningState(0));

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.OpenPositions.Add(new OpenPosition
            {
                LaneId = TradingLanes.Count, Symbol = "DOT/USDT", Side = OrderSide.Buy,
                Quantity = 1m, EntryPrice = 1m, CurrentPrice = 1m,
                OpenedInMode = TradingMode.Paper, OpenedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await watchdog.TickAsync(CancellationToken.None);
        await watchdog.TickAsync(CancellationToken.None);
        await watchdog.TickAsync(CancellationToken.None);

        Assert.Single(notifier.Sent);
    }

    /// <summary>
    /// Il controllo nuovo non deve inventare allarmi su una piattaforma sana: posizioni sulle
    /// corsie configurate, per quanto numerose, non sono orfane.
    /// </summary>
    [Fact]
    public async Task Tick_PosizioniSulleCorsieConfigurate_NessunAllarmeDiOrfane()
    {
        var notifier = new RecordingNotifier();
        var (watchdog, dbFactory, _, _) = await BuildAsync(notifier: notifier);
        await SeedStateAsync(dbFactory, HealthyRunningState(0));

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            for (var lane = 0; lane < TradingLanes.Count; lane++)
            {
                db.OpenPositions.Add(new OpenPosition
                {
                    LaneId = lane, Symbol = "BTC/USDT", Side = OrderSide.Buy,
                    Quantity = 0.01m, EntryPrice = 60_000m, CurrentPrice = 60_000m,
                    OpenedInMode = TradingMode.Paper, OpenedAtUtc = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        await watchdog.TickAsync(CancellationToken.None);

        Assert.Empty(notifier.Sent);
    }

    /// <summary>
    /// [E6] Il battito sul motore VERO: null all'avvio (onestà: questo processo non ha ancora
    /// valutato nulla — un valore ereditato rassicurerebbe su un'attività che non c'è), e uguale
    /// all'apertura della candela dopo la prima valutazione. Lo status porta anche il timeframe,
    /// senza il quale il ritardo non è interpretabile.
    /// </summary>
    [Fact]
    public async Task ProcessCandle_AggiornaIlBattito_ELoStatusLoEspone()
    {
        var (_, dbFactory, _, _) = await BuildAsync();
        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "1h", TotalCapital = 10_000m,
            Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "Hold", DisplayName = "Hold", IsActive = true }],
        };
        var engine = new TradingEngine(
            0, dbFactory, new HoldStrategyFactory(), new TechnicalIndicatorsService(),
            new ThrowingExchangeFactory(), new FakeEnsembleManager(config),
            new SafetyConfiguration { PositionSizePercent = 8m, MaxPositionSizePercent = 50m, MaxTotalExposurePercent = 100m }.AsMonitor(),
            new LiveExecutionOptions().AsMonitor(),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance);

        await engine.StartAsync(TradingMode.Paper);
        var before = await engine.GetStatusAsync();
        Assert.True(before.IsRunning);
        Assert.Equal("1h", before.Timeframe);
        Assert.Null(before.LastProcessedCandleUtc);

        var ts = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-90), DateTimeKind.Utc);
        await engine.ProcessCandleAsync(new OhlcvData
        {
            Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = ts,
            Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 1m,
        });

        var after = await engine.GetStatusAsync();
        Assert.Equal(ts, after.LastProcessedCandleUtc);
    }

    // --- [E6] Inedia di valutazione ------------------------------------------------------------
    // `running=true` è un flag d'intento: se le candele smettono di arrivare la corsia resta verde
    // ovunque mentre stop e trailing non li valuta nessuno. Il battito (LastProcessedCandleUtc) si
    // giudica con la regola unica di SeriesFreshness, più il discriminatore stantio-E-fermo che
    // distingue il digiuno dalla rincorsa del replay.

    [Fact]
    public async Task Tick_CorsiaRunningColBattitoStantioEFermo_AllertaUnaVolta_SenzaQuarantena()
    {
        var notifier = new RecordingNotifier();
        var (watchdog, dbFactory, _, engines) = await BuildAsync(notifier: notifier);
        await SeedStateAsync(dbFactory, HealthyRunningState(0)); // Timeframe default "1h"
        engines[0].StatusToReturn = new TradingEngineStatus
        {
            IsRunning = true,
            Timeframe = "1h",
            LastProcessedCandleUtc = DateTime.UtcNow.AddHours(-10), // ben oltre le 3 barre di tolleranza
        };

        // Primo sguardo: si registra il battito, non si giudica (serve il confronto col giro prima).
        await watchdog.TickAsync(CancellationToken.None);
        Assert.Empty(notifier.Sent);

        // Secondo sguardo: stantio E fermo → un allarme.
        await watchdog.TickAsync(CancellationToken.None);
        var alert = Assert.Single(notifier.Sent);
        Assert.Equal(ProcioneMGR.Services.Notifications.NotificationSeverity.Critical, alert.Severity);
        Assert.Contains("affamata", alert.Title);

        // Terzo sguardo: già detto, silenzio.
        await watchdog.TickAsync(CancellationToken.None);
        Assert.Single(notifier.Sent);

        // MAI quarantena né stop per inedia: la corsia non è corrotta, è a digiuno.
        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.Empty(await check.LaneQuarantines.AsNoTracking().ToListAsync());
        Assert.Equal(0, engines[0].StopCalls);
    }

    [Fact]
    public async Task Tick_BattitoVecchioMaCheAvanza_EIlReplayDiAvvio_NonUnDigiuno()
    {
        // Dopo un riavvio la corsia Paper rigioca 30 giorni di candele: il battito è vecchissimo
        // ma AVANZA a ogni giro. Allarmare qui significherebbe un falso critico a ogni riavvio.
        var notifier = new RecordingNotifier();
        var (watchdog, dbFactory, _, engines) = await BuildAsync(notifier: notifier);
        await SeedStateAsync(dbFactory, HealthyRunningState(0));

        for (var giorniIndietro = 30; giorniIndietro >= 28; giorniIndietro--)
        {
            engines[0].StatusToReturn = new TradingEngineStatus
            {
                IsRunning = true,
                Timeframe = "1h",
                LastProcessedCandleUtc = DateTime.UtcNow.AddDays(-giorniIndietro),
            };
            await watchdog.TickAsync(CancellationToken.None);
        }

        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task Tick_BattitoFresco_NessunAllarme_ERiarmoDopoIlRecupero()
    {
        var notifier = new RecordingNotifier();
        var (watchdog, dbFactory, _, engines) = await BuildAsync(notifier: notifier);
        await SeedStateAsync(dbFactory, HealthyRunningState(0));

        // Digiuno → allarme (due sguardi).
        var stale = new TradingEngineStatus { IsRunning = true, Timeframe = "1h", LastProcessedCandleUtc = DateTime.UtcNow.AddHours(-10) };
        engines[0].StatusToReturn = stale;
        await watchdog.TickAsync(CancellationToken.None);
        await watchdog.TickAsync(CancellationToken.None);
        Assert.Single(notifier.Sent);

        // Recupero: battito fresco → nessun allarme nuovo, e l'allarme si RIARMA.
        engines[0].StatusToReturn = new TradingEngineStatus { IsRunning = true, Timeframe = "1h", LastProcessedCandleUtc = DateTime.UtcNow };
        await watchdog.TickAsync(CancellationToken.None);
        Assert.Single(notifier.Sent);

        // Nuovo digiuno → nuovo allarme: la transizione è ripetibile, non un one-shot.
        engines[0].StatusToReturn = stale;
        await watchdog.TickAsync(CancellationToken.None);
        await watchdog.TickAsync(CancellationToken.None);
        Assert.Equal(2, notifier.Sent.Count);
    }

    [Fact]
    public async Task Tick_CorsiaRunningCheNonHaMaiValutato_AllertaDopoDueSguardi()
    {
        // running=true e battito null su due giri consecutivi: affamata dal principio (per esempio
        // serie della corsia senza candele a database). Il null NON deve valere "aggiornata" —
        // stessa trappola del confronto numerico chiusa in SeriesFreshness.
        var notifier = new RecordingNotifier();
        var (watchdog, dbFactory, _, engines) = await BuildAsync(notifier: notifier);
        await SeedStateAsync(dbFactory, HealthyRunningState(0));
        engines[0].StatusToReturn = new TradingEngineStatus { IsRunning = true, Timeframe = "1h", LastProcessedCandleUtc = null };

        await watchdog.TickAsync(CancellationToken.None);
        Assert.Empty(notifier.Sent);
        await watchdog.TickAsync(CancellationToken.None);

        var alert = Assert.Single(notifier.Sent);
        Assert.Equal(ProcioneMGR.Services.Notifications.NotificationSeverity.Critical, alert.Severity);
        Assert.Contains("affamata", alert.Title);
    }

    /// <summary>
    /// [2026-08-17] Da quando il feed non rigioca più il passato dopo un riavvio, fra il riavvio e
    /// la chiusura della barra successiva una corsia legittimamente non valuta NULLA — fino a
    /// quattro ore su una 4h. Senza una grazia, il watchdog gridava «AFFAMATA» su OGNI corsia a
    /// ogni riavvio: verificato dal vivo, sette allarmi critici in un colpo. Un allarme che urla
    /// quando non c'è niente che non va logora quelli veri.
    /// </summary>
    [Theory]
    [InlineData("15m", 60)]      // 15m × (1 + 3 di tolleranza) = 60 minuti
    [InlineData("4h", 960)]      // 4h × 4 = 16 ore
    [InlineData("1d", 5760)]     // 1d × 4 = 4 giorni
    public void GraceAfterStart_IsOneFullBarPlusTolerance(string timeframe, int minutiAttesi)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutiAttesi), LaneInvariantWatchdog.GraceAfterStart(timeframe));
    }

    [Fact]
    public void GraceAfterStart_UnknownTimeframe_IsPrudentNeverZero()
    {
        var grazia = LaneInvariantWatchdog.GraceAfterStart("timeframe-inventato");

        Assert.True(grazia > TimeSpan.Zero, "una grazia nulla riaprirebbe l'allarme falso a ogni riavvio");
        Assert.Equal(TimeSpan.FromHours(1), grazia);
    }
}
