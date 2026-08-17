using Grpc.Core;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'orchestrazione estratta da Trading.razor (P1-5, audit consolidamento 2026-07-17):
/// prima di questa estrazione, questa logica viveva nel @code del componente e non aveva test
/// indipendenti da Blazor — solo i comportamenti visibili in markup erano coperti (bUnit,
/// AuditBlazorUiTests). Qui si verificano i dettagli di comportamento che il markup non esercita
/// direttamente: la gestione della staleness gRPC, la validazione delle soglie di sicurezza, il
/// parsing degli edit SL/TP/Trailing.
/// </summary>
public class TradingPageServiceTests
{
    private sealed class FakeTradingEngine(int laneId) : ITradingEngine
    {
        public int LaneId => laneId;
        public TradingEngineStatus StatusToReturn { get; set; } = new() { Mode = TradingMode.Paper };
        public List<OpenPosition> PositionsToReturn { get; set; } = [];
        public List<Order> OrdersToReturn { get; set; } = [];
        public List<Order> PendingToReturn { get; set; } = [];
        public TradingPerformance PerformanceToReturn { get; set; } = new();
        public Exception? ThrowOnRefresh { get; set; }
        public Exception? ThrowOnStart { get; set; }
        /// <summary>[2026-08-17] Fa fallire ogni comando: è il motore remoto che non risponde.</summary>
        public Exception? ThrowOnCommand { get; set; }
        public (decimal? Sl, decimal? Tp, decimal? Tsl)? LastSlTp { get; private set; }
        public string? LastConfirmedOrderId { get; private set; }
        public string? LastConfirmedUserId { get; private set; }
        public string? LastRejectedOrderId { get; private set; }
        public string? LastRejectedUserId { get; private set; }
        public string? LastClosedPositionId { get; private set; }
        public string? LastEmergencyReason { get; private set; }
        public TradingMode? StartedWith { get; private set; }
        public bool StopCalled { get; private set; }

        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
            => ThrowOnRefresh is not null ? Task.FromException<TradingEngineStatus>(ThrowOnRefresh) : Task.FromResult(StatusToReturn);
        public Task StartAsync(TradingMode mode, CancellationToken ct = default)
        {
            if (ThrowOnStart is not null) return Task.FromException(ThrowOnStart);
            StartedWith = mode;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct = default)
        {
            if (ThrowOnCommand is not null) return Task.FromException(ThrowOnCommand);
            StopCalled = true; return Task.CompletedTask;
        }
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default)
        {
            if (ThrowOnCommand is not null) return Task.FromException(ThrowOnCommand);
            LastEmergencyReason = reason; return Task.CompletedTask;
        }
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => Task.FromResult(PositionsToReturn);
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default)
        {
            if (ThrowOnCommand is not null) return Task.FromException(ThrowOnCommand);
            LastClosedPositionId = positionId; return Task.CompletedTask;
        }
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default)
        {
            if (ThrowOnCommand is not null) return Task.FromException(ThrowOnCommand);
            LastSlTp = (stopLoss, takeProfit, trailingStopPercent);
            return Task.CompletedTask;
        }
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => Task.FromResult(PendingToReturn);
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default)
        {
            if (ThrowOnCommand is not null) return Task.FromException(ThrowOnCommand);
            LastConfirmedOrderId = orderId;
            LastConfirmedUserId = userId;
            return Task.CompletedTask;
        }
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default)
        {
            if (ThrowOnCommand is not null) return Task.FromException(ThrowOnCommand);
            LastRejectedOrderId = orderId;
            LastRejectedUserId = userId;
            return Task.CompletedTask;
        }
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(OrdersToReturn);
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(PerformanceToReturn);
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakePromotionEvaluator(IReadOnlyList<PromotionDecision> decisions) : IPromotionEvaluator
    {
        public Task<PromotionDecision> EvaluateLaneAsync(int laneId, CancellationToken ct = default)
            => Task.FromResult(decisions.First(d => d.LaneId == laneId));
        public Task<IReadOnlyList<PromotionDecision>> EvaluateAllLanesAsync(CancellationToken ct = default)
            => Task.FromResult(decisions);
    }

    private sealed class RecordingPromoter : ILanePromoter
    {
        public (int LaneId, TradingMode Mode, string Reason)? LastPromotion { get; private set; }
        public Task PromoteLaneAsync(int laneId, TradingMode newMode, string reason, CancellationToken ct = default)
        {
            LastPromotion = (laneId, newMode, reason);
            return Task.CompletedTask;
        }
    }

    private static (TradingPageService Service, FakeTradingEngine Engine0) Build(
        SafetyConfiguration? safety = null, IPromotionEvaluator? promotionEval = null,
        ILanePromoter? promoter = null, FakeEngineConfigStore? engineConfig = null,
        ILaneQuarantineStore? quarantineStore = null)
    {
        var engine0 = new FakeTradingEngine(0);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton<ITradingEngine>(0, engine0);
        services.AddMediator();
        var provider = services.BuildServiceProvider();

        // [E1] Le soglie arrivano dallo store del MOTORE, non più da IOptionsMonitor del guscio:
        // il fake le tiene in memoria come farebbe il motore col suo file. Se il test ha già
        // seminato la sezione, quella vince.
        var store = engineConfig ?? new FakeEngineConfigStore();
        if (!store.Sections.ContainsKey("Trading:Safety"))
        {
            store.Seed("Trading:Safety",
                safety ?? new SafetyConfiguration { MaxPositionSizePercent = 10m, MaxTotalExposurePercent = 50m, MaxOpenPositions = 5, MaxLeverageAllowed = 5 });
        }

        var service = new TradingPageService(
            provider.GetRequiredService<IMediator>(),
            promotionEval ?? new FakePromotionEvaluator([]),
            promoter ?? new RecordingPromoter(),
            store,
            quarantineStore ?? new FakeLaneQuarantineStore());
        return (service, engine0);
    }

    /// <summary>
    /// [2026-08-17] Harness a DUE corsie keyed: il <see cref="Build"/> storico ne registra una sola,
    /// e i difetti di identità della corsia non erano quindi nemmeno esprimibili come test.
    /// </summary>
    private static (TradingPageService Service, FakeTradingEngine Engine0, FakeTradingEngine Engine1) BuildTwoLanes()
    {
        var engine0 = new FakeTradingEngine(0);
        var engine1 = new FakeTradingEngine(1);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton<ITradingEngine>(0, engine0);
        services.AddKeyedSingleton<ITradingEngine>(1, engine1);
        services.AddMediator();
        var provider = services.BuildServiceProvider();

        var store = new FakeEngineConfigStore();
        store.Seed("Trading:Safety", new SafetyConfiguration());

        var service = new TradingPageService(
            provider.GetRequiredService<IMediator>(),
            new FakePromotionEvaluator([]),
            new RecordingPromoter(),
            store,
            new FakeLaneQuarantineStore());
        return (service, engine0, engine1);
    }

    // --- RefreshAsync: successo e staleness -----------------------------------------------------

    [Fact]
    public async Task RefreshAsync_Success_PopulatesStateAndClearsStaleness()
    {
        var (service, engine) = Build();
        engine.StatusToReturn = new TradingEngineStatus { Mode = TradingMode.Testnet, IsRunning = true };
        engine.PositionsToReturn = [new OpenPosition { PositionId = "p1" }];

        await service.RefreshAsync(0);

        Assert.Equal(TradingMode.Testnet, service.Status!.Mode);
        Assert.Single(service.Positions);
        Assert.Null(service.StaleSince);
        Assert.Null(service.LastStaleReason);
    }

    [Fact]
    public async Task RefreshAsync_RpcException_SetsStaleSinceOnlyOnFirstFailure()
    {
        var (service, engine) = Build();
        engine.ThrowOnRefresh = new RpcException(new Status(StatusCode.Unavailable, "down"));

        await service.RefreshAsync(0);
        var firstStale = service.StaleSince;
        Assert.NotNull(firstStale);
        Assert.Equal("Unavailable", service.LastStaleReason);

        // Un secondo fallimento consecutivo NON deve spostare in avanti l'istante di inizio: il
        // banner "da quanti secondi" deve contare dal PRIMO fallimento, non dall'ultimo.
        await Task.Delay(10);
        await service.RefreshAsync(0);
        Assert.Equal(firstStale, service.StaleSince);
    }

    [Fact]
    public async Task RefreshAsync_SuccessAfterFailure_ClearsStaleness()
    {
        var (service, engine) = Build();
        engine.ThrowOnRefresh = new RpcException(new Status(StatusCode.Unavailable, "down"));
        await service.RefreshAsync(0);
        Assert.NotNull(service.StaleSince);

        engine.ThrowOnRefresh = null;
        await service.RefreshAsync(0);

        Assert.Null(service.StaleSince);
        Assert.Null(service.LastStaleReason);
    }

    /// <summary>
    /// [2026-08-17] Questo test è stato RIMIRATO sul suo intento reale. Prima pretendeva che un
    /// fallimento non-gRPC lasciasse la staleness INTATTA, e con ciò consacrava un buco nella
    /// regola 5 del progetto («degradare dicendolo»): in topologia in-process nessun guasto è una
    /// RpcException, quindi il banner era irraggiungibile e un Postgres giù lasciava a schermo
    /// equity, PnL e posizioni dell'ultimo giro riuscito spacciandoli per attuali.
    ///
    /// L'intento legittimo che il test difendeva — non accusare il gRPC quando il gRPC non c'entra
    /// — è ora un campo esplicito, <c>StaleIsTransport</c>, invece del silenzio.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_NonRpcException_DeclaresStaleness_WithoutBlamingGrpc()
    {
        var (service, engine) = Build();
        engine.ThrowOnRefresh = new InvalidOperationException("bug locale");

        await service.RefreshAsync(0);

        Assert.NotNull(service.StaleSince);
        Assert.Equal(nameof(InvalidOperationException), service.LastStaleReason);
        Assert.False(service.StaleIsTransport);   // non è il motore remoto a non rispondere
    }

    [Fact]
    public async Task RefreshAsync_RpcException_MarksStalenessAsTransport()
    {
        var (service, engine) = Build();
        engine.ThrowOnRefresh = new RpcException(new Status(StatusCode.Unavailable, "down"));

        await service.RefreshAsync(0);

        Assert.NotNull(service.StaleSince);
        Assert.True(service.StaleIsTransport);
    }

    /// <summary>
    /// [2026-08-17] Un fallimento a corsia INVARIATA deve conservare gli ultimi numeri buoni
    /// (svuotare la pagina durante un riavvio di pochi secondi sarebbe peggio) — ma dichiarandoli
    /// vecchi. Le due metà vanno provate insieme: una senza l'altra è il difetto.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_Failure_KeepsLastGoodValues_AndDeclaresThemStale()
    {
        var (service, engine) = Build();
        engine.PositionsToReturn = [new OpenPosition { PositionId = "p1", Symbol = "ADA/USDT" }];
        await service.RefreshAsync(0);
        Assert.Single(service.Positions);

        engine.ThrowOnRefresh = new InvalidOperationException("Postgres giù");
        await service.RefreshAsync(0);

        Assert.Single(service.Positions);          // i numeri restano...
        Assert.NotNull(service.StaleSince);        // ...ma sono dichiarati vecchi
    }

    /// <summary>
    /// [2026-08-17, trovato nel BROWSER] Il polling è ogni 2s e la deadline di lettura è 10s: quando
    /// il motore è lento, ogni giro viene superato dai successivi. Guardando lo stantio col token di
    /// generazione, NESSUN fallimento arrivava più a schermo — la pagina restava muta e vuota
    /// esattamente nel caso che il banner esiste per raccontare. Un fallimento non è un dato da
    /// pubblicare in ordine: è una notizia sulla corsia, e vale finché la corsia è quella.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_SupersededFailure_StillDeclaresStaleness_ForTheSameLane()
    {
        var (service, engine) = Build();
        engine.ThrowOnRefresh = new RpcException(new Status(StatusCode.DeadlineExceeded, "lento"));

        // Due giri sulla STESSA corsia si sovrappongono: il primo è superato dal secondo.
        var lento = service.RefreshAsync(0);
        var nuovo = service.RefreshAsync(0);
        await Task.WhenAll(lento, nuovo);

        Assert.NotNull(service.StaleSince);
        Assert.Equal("DeadlineExceeded", service.LastStaleReason);
    }

    // --- Identità della corsia -----------------------------------------------------------------

    /// <summary>
    /// [2026-08-17] Cambiando corsia, i dati della PRECEDENTE non devono restare a schermo sotto
    /// l'etichetta della nuova. Prima non c'era alcun tag di provenienza: se la lettura della corsia
    /// nuova falliva, KPI, posizioni e ordini restavano quelli di prima mentre l'intestazione, il
    /// grafico e la quarantena erano già della nuova — e i pulsanti di riga mandavano al motore
    /// della corsia B il positionId della corsia A, che il motore non trova e ignora in silenzio.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_LaneChanged_DoesNotShowThePreviousLaneData()
    {
        var (service, engine0, engine1) = BuildTwoLanes();
        engine0.PositionsToReturn = [new OpenPosition { PositionId = "p0", Symbol = "ADA/USDT" }];
        await service.RefreshAsync(0);
        Assert.Single(service.Positions);
        Assert.Equal(0, service.LoadedLaneId);

        // La corsia 1 non risponde: l'unica cosa vera da mostrare è «nessun dato per questa corsia».
        engine1.ThrowOnRefresh = new RpcException(new Status(StatusCode.Unavailable, "down"));
        await service.RefreshAsync(1);

        Assert.Empty(service.Positions);
        Assert.Null(service.Status);
        Assert.Equal(1, service.LoadedLaneId);
    }

    [Fact]
    public async Task RefreshAsync_LaneChanged_ClearsPendingEdits()
    {
        var (service, engine0, _) = BuildTwoLanes();
        var pos = new OpenPosition { PositionId = "p0", StopLoss = 10m };
        engine0.PositionsToReturn = [pos];
        await service.RefreshAsync(0);
        service.SetSlEdit("p0", "9");
        Assert.Equal("9", service.SlValue(pos));

        await service.RefreshAsync(1);
        await service.RefreshAsync(0);

        // Un edit in sospeso appartiene alla corsia su cui è nato: tornando indietro non deve
        // riapparire come se l'operatore l'avesse appena digitato.
        Assert.Equal("10", service.SlValue(pos));
    }

    /// <summary>
    /// L'esito di un comando NON viene azzerato dal refresh che lo segue (altrimenti nessun
    /// messaggio sarebbe mai visibile), ma la pagina lo azzera al cambio di corsia: quei testi non
    /// nominano la corsia, e resterebbero a descrivere un'azione avvenuta altrove.
    /// </summary>
    [Fact]
    public async Task ClearMessage_RemovesTheOutcomeOfThePreviousCommand()
    {
        var (service, _) = Build();
        await service.StopAsync(0);
        Assert.NotNull(service.Message);

        service.ClearMessage();

        Assert.Null(service.Message);
        Assert.False(service.IsError);
    }

    // --- Start/Stop/Emergency/Close/Confirm/Reject: pass-through al motore ---------------------
    // Questa è l'esatta superficie che la Fase 1 (CQRS/Mediator) sposterà dietro
    // IMediator.Send(...): senza una rete di regressione qui, un refactor che sbagliasse verbo o
    // argomento passerebbe inosservato fino alla UI.

    [Fact]
    public async Task StartAsync_CallsEngineStart_WithGivenMode_AndSetsSuccessMessage()
    {
        var (service, engine) = Build();

        await service.StartAsync(0, TradingMode.Testnet);

        Assert.Equal(TradingMode.Testnet, engine.StartedWith);
        Assert.False(service.IsError);
        Assert.Contains("Testnet", service.Message);
    }

    [Fact]
    public async Task StartAsync_EngineThrows_SetsErrorMessage()
    {
        var (service, engine) = Build();
        engine.ThrowOnStart = new InvalidOperationException("credenziali mancanti");

        await service.StartAsync(0, TradingMode.Testnet);

        Assert.True(service.IsError);
        Assert.Contains("credenziali mancanti", service.Message);
    }

    [Fact]
    public async Task StopAsync_CallsEngineStop_AndSetsMessage()
    {
        var (service, engine) = Build();

        await service.StopAsync(0);

        Assert.True(engine.StopCalled);
        Assert.False(service.IsError);
    }

    [Fact]
    public async Task EmergencyAsync_CallsEngineEmergencyStop_WithFixedReason()
    {
        var (service, engine) = Build();

        await service.EmergencyAsync(0);

        Assert.Equal("Stop manuale dall'operatore", engine.LastEmergencyReason);
        Assert.False(service.IsError);
    }

    [Fact]
    public async Task CloseAsync_CallsEngineClosePosition_WithGivenPositionId()
    {
        var (service, engine) = Build();

        await service.CloseAsync(0, "p1");

        Assert.Equal("p1", engine.LastClosedPositionId);
    }

    [Fact]
    public async Task ConfirmAsync_CallsEngineConfirmOrder_WithGivenOrderIdAndUserId()
    {
        var (service, engine) = Build();

        await service.ConfirmAsync(0, "o1", "user-42");

        Assert.Equal("o1", engine.LastConfirmedOrderId);
        Assert.Equal("user-42", engine.LastConfirmedUserId);
        Assert.False(service.IsError);
    }

    [Fact]
    public async Task RejectAsync_CallsEngineRejectOrder_WithGivenOrderIdAndUserId()
    {
        var (service, engine) = Build();

        await service.RejectAsync(0, "o1", "user-42");

        Assert.Equal("o1", engine.LastRejectedOrderId);
        Assert.Equal("user-42", engine.LastRejectedUserId);
        Assert.False(service.IsError);
    }

    // --- SaveSafetyAsync: validazione --------------------------------------------------------

    [Theory]
    [InlineData(0, 50, 5, 5)]   // MaxPositionSizePercent <= 0
    [InlineData(10, 0, 5, 5)]   // MaxTotalExposurePercent <= 0
    [InlineData(10, 50, 0, 5)]  // MaxOpenPositions < 1
    [InlineData(10, 50, 5, 0)]  // MaxLeverageAllowed < 1
    public async Task SaveSafetyAsync_InvalidValues_RejectsWithoutCallingWriter(
        decimal maxPos, decimal maxExposure, int maxOpen, int maxLeverage)
    {
        var store = new FakeEngineConfigStore();
        var (service, _) = Build(engineConfig: store);
        await service.ReloadSafetyAsync();
        service.Safety.MaxPositionSizePercent = maxPos;
        service.Safety.MaxTotalExposurePercent = maxExposure;
        service.Safety.MaxOpenPositions = maxOpen;
        service.Safety.MaxLeverageAllowed = maxLeverage;

        await service.SaveSafetyAsync();

        Assert.Empty(store.Saved);
        Assert.True(service.IsError);
        Assert.Contains("non validi", service.Message);
    }

    [Fact]
    public async Task SaveSafetyAsync_NegativeFeePercent_RejectsWithoutCallingWriter()
    {
        // P2-8: zero è un valore lecito (promozione a fee zero, test), negativo no — nessun exchange
        // paga per tradare in questo contesto, e un fee negativo alimenterebbe un PnL live gonfiato.
        var store = new FakeEngineConfigStore();
        var (service, _) = Build(engineConfig: store);
        await service.ReloadSafetyAsync();
        service.Safety.FeePercent = -0.1m;

        await service.SaveSafetyAsync();

        Assert.Empty(store.Saved);
        Assert.True(service.IsError);
    }

    [Fact]
    public async Task SaveSafetyAsync_ZeroFeePercent_IsAccepted()
    {
        var store = new FakeEngineConfigStore();
        var (service, _) = Build(engineConfig: store);
        await service.ReloadSafetyAsync();
        service.Safety.FeePercent = 0m;

        await service.SaveSafetyAsync();

        Assert.Single(store.Saved);
        Assert.False(service.IsError);
    }

    // --- Regressione: un errore non deve sopravvivere alla propria causa ------------------------

    /// <summary>Valutatore che fallisce le prime N volte e poi funziona: simula un guasto transitorio.</summary>
    private sealed class FlakyPromotionEvaluator(int failuresBeforeSuccess) : IPromotionEvaluator
    {
        private int _calls;

        public Task<PromotionDecision> EvaluateLaneAsync(int laneId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PromotionDecision>> EvaluateAllLanesAsync(CancellationToken ct = default)
        {
            if (_calls++ < failuresBeforeSuccess)
            {
                throw new InvalidOperationException("Error connecting to subchannel.");
            }
            IReadOnlyList<PromotionDecision> ok = [new PromotionDecision { LaneId = 0, Symbol = "AAVE/USDT" }];
            return Task.FromResult(ok);
        }
    }

    [Fact]
    public async Task RefreshPromotions_AfterAFailure_ClearsTheErrorOnTheNextSuccess()
    {
        // Difetto visto dal vivo il 2026-07-27: caduto il port-forward verso il core in-cluster la
        // valutazione delle promozioni falliva; ristabilito il tunnel tutte le altre query tornavano
        // a completare, ma il banner rosso restava a schermo perche' il messaggio non veniva mai
        // azzerato. Un errore che sopravvive alla propria causa fa credere a un guasto ancora in corso.
        var (service, _) = Build(promotionEval: new FlakyPromotionEvaluator(failuresBeforeSuccess: 1));

        await service.RefreshPromotionsAsync();
        Assert.True(service.PromoIsError);
        Assert.Contains("Valutazione promozioni fallita", service.PromoMessage);

        await service.RefreshPromotionsAsync();

        Assert.False(service.PromoIsError);
        Assert.Null(service.PromoMessage);
        Assert.Single(service.Promotions);
    }

    [Fact]
    public async Task RefreshPromotions_KeepsReportingWhileTheFailurePersists()
    {
        // Il complemento: azzerare l'esito precedente non deve nascondere un guasto ANCORA in corso.
        var (service, _) = Build(promotionEval: new FlakyPromotionEvaluator(failuresBeforeSuccess: 5));

        await service.RefreshPromotionsAsync();
        await service.RefreshPromotionsAsync();

        Assert.True(service.PromoIsError);
        Assert.Contains("Valutazione promozioni fallita", service.PromoMessage);
    }

    /// <summary>
    /// [2026-08-17] Il caso reale: il tunnel gRPC cade un istante all'apertura della pagina, il form
    /// si riempie dei DEFAULT DEL CODICE, poi il canale torna. Un salvataggio in quel momento
    /// riusciva e SOSTITUIVA l'intera sezione — leva massima, drawdown, posizioni aperte —
    /// riportando tutto ai default, con un messaggio verde «soglie salvate sul MOTORE».
    /// </summary>
    [Fact]
    public async Task SaveSafetyAsync_AfterFailedRead_RefusesToOverwriteTheEngineWithCodeDefaults()
    {
        var store = new FakeEngineConfigStore(remote: true);
        store.Seed("Trading:Safety", new SafetyConfiguration { MaxLeverageAllowed = 2, MaxDrawdownPercent = 8m, MaxOpenPositions = 1 });
        var (service, _) = Build(engineConfig: store);

        store.Reachable = false;
        await service.ReloadSafetyAsync();          // lettura fallita: il form ha i default
        Assert.False(service.SafetyReachable);

        store.Reachable = true;                     // il canale si riapre: la SCRITTURA riuscirebbe
        await service.SaveSafetyAsync();

        Assert.Empty(store.Saved);                  // nulla è stato scritto
        Assert.True(service.IsError);
        Assert.Contains("default del codice", service.Message);
    }

    /// <summary>Dopo una scrittura riuscita la spia «sono i default» non deve sopravvivere alla propria causa.</summary>
    [Fact]
    public async Task SaveSafetyAsync_Success_ClearsTheUnreachableFlag()
    {
        var store = new FakeEngineConfigStore(remote: true);
        store.Seed("Trading:Safety", new SafetyConfiguration());
        var (service, _) = Build(engineConfig: store);
        await service.ReloadSafetyAsync();

        await service.SaveSafetyAsync();

        Assert.True(service.SafetyReachable);
        Assert.Null(service.SafetyError);
        Assert.False(service.IsError);
    }

    /// <summary>
    /// Zero non è «nessun limite»: il SafetyChecker confronta con <c>&gt;=</c>, quindi
    /// MaxDrawdownPercent = 0 fa scattare l'emergency stop al primo ordine (0 &gt;= 0) e la corsia
    /// diventa inutilizzabile senza che il motivo sia leggibile da nessuna parte.
    /// </summary>
    [Theory]
    [InlineData(0, 5)]
    [InlineData(20, 0)]
    public async Task SaveSafetyAsync_ZeroCriticalThresholds_AreRefused(int maxDd, int maxDailyLoss)
    {
        var store = new FakeEngineConfigStore();
        store.Seed("Trading:Safety", new SafetyConfiguration());
        var (service, _) = Build(engineConfig: store);
        await service.ReloadSafetyAsync();
        service.Safety.MaxDrawdownPercent = maxDd;
        service.Safety.MaxDailyLossPercent = maxDailyLoss;

        await service.SaveSafetyAsync();

        Assert.Empty(store.Saved);
        Assert.True(service.IsError);
    }

    [Fact]
    public async Task SaveSafetyAsync_ValidValues_PersistsAndReportsSuccess()
    {
        var store = new FakeEngineConfigStore();
        var (service, _) = Build(engineConfig: store);
        await service.ReloadSafetyAsync();
        service.Safety.MaxDrawdownPercent = 12.5m;

        await service.SaveSafetyAsync();

        var (section, options) = Assert.Single(store.Saved);
        Assert.Equal("Trading:Safety", section);
        Assert.Equal(12.5m, ((SafetyConfiguration)options).MaxDrawdownPercent);
        Assert.False(service.IsError);
        // La conferma dichiara DOVE è stata scritta, non un generico "salvato".
        Assert.Contains("salvate", service.Message);
    }

    [Fact]
    public async Task SaveSafetyAsync_WriterThrows_ReportsErrorMessage()
    {
        var store = new FakeEngineConfigStore { ThrowOnWrite = new InvalidOperationException("disco pieno") };
        var (service, _) = Build(engineConfig: store);
        await service.ReloadSafetyAsync();

        await service.SaveSafetyAsync();

        Assert.True(service.IsError);
        Assert.Contains("disco pieno", service.Message);
    }

    [Fact]
    public async Task SaveSafetyAsync_EngineWarning_IsSurfacedToTheOperator()
    {
        // In Kubernetes una env della ConfigMap vince sul file: il salvataggio riesce e non cambia
        // nulla. Il motore lo dice nel risultato, e il pannello DEVE ripeterlo — tacere sarebbe
        // esattamente la bugia che E1 corregge.
        var store = new FakeEngineConfigStore { WarningToReturn = "MaxDrawdownPercent arriva da variabili d'ambiente" };
        var (service, _) = Build(engineConfig: store);
        await service.ReloadSafetyAsync();

        await service.SaveSafetyAsync();

        Assert.False(service.IsError);
        Assert.Contains("variabili d'ambiente", service.Message);
    }

    [Fact]
    public async Task ReloadSafetyAsync_EngineUnreachable_DeclaresDefaultsInsteadOfPassingThemOffAsReal()
    {
        // Il motore remoto non risponde: Bind restituisce i default. Il servizio NON deve
        // spacciarli per le soglie applicate — SafetyReachable=false è ciò che il pannello mostra.
        var store = new FakeEngineConfigStore(remote: true, reachable: false);
        var (service, _) = Build(engineConfig: store);

        await service.ReloadSafetyAsync();

        Assert.False(service.SafetyReachable);
        Assert.NotNull(service.SafetyError);
        Assert.Equal(new SafetyConfiguration().MaxDrawdownPercent, service.Safety.MaxDrawdownPercent);
    }

    [Fact]
    public async Task ReloadSafetyAsync_ReadsWhatTheEngineApplies_NotTheShellFile()
    {
        // Il cuore di E1: il valore mostrato è quello del MOTORE (via store), non quello del guscio.
        var store = new FakeEngineConfigStore(remote: true);
        store.Seed("Trading:Safety", new SafetyConfiguration { MaxDrawdownPercent = 33m });
        var (service, _) = Build(engineConfig: store);

        await service.ReloadSafetyAsync();

        Assert.True(service.SafetyReachable);
        Assert.True(service.SafetyIsRemote);
        Assert.Equal(33m, service.Safety.MaxDrawdownPercent);
    }

    // --- Edit SL/TP/Trailing: fallback e parsing ----------------------------------------------

    [Fact]
    public void SlValue_NotEdited_FallsBackToPositionValue()
    {
        var (service, _) = Build();
        var pos = new OpenPosition { PositionId = "p1", StopLoss = 58000m };

        Assert.Equal("58000", service.SlValue(pos));
    }

    [Fact]
    public void SlValue_Edited_TakesPrecedenceOverPositionValue()
    {
        var (service, _) = Build();
        var pos = new OpenPosition { PositionId = "p1", StopLoss = 58000m };

        service.SetSlEdit("p1", "59500");

        Assert.Equal("59500", service.SlValue(pos));
    }

    [Theory]
    [InlineData("0", null)]      // zero non è un livello valido
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData("1234.5", 1234.5)]
    public void ParseLevel_ValidatesPositiveDecimalsOnly(string raw, double? expected)
    {
        var result = TradingPageService.ParseLevel(raw);
        Assert.Equal(expected is null ? (decimal?)null : (decimal)expected.Value, result);
    }

    /// <summary>
    /// [2026-08-17] La distinzione che <c>ParseLevel</c> da sola non può esprimere: «vuoto» è una
    /// richiesta legittima di rimuovere la protezione, «abc» e «-5» sono errori di battitura. Prima
    /// erano lo stesso null, ed è per questo che un refuso poteva disarmare uno stop loss.
    /// </summary>
    [Theory]
    [InlineData("", true, null)]        // campo svuotato: rimozione VOLUTA
    [InlineData("   ", true, null)]
    [InlineData("1234.5", true, 1234.5)]
    [InlineData("0", false, null)]      // non valido: nessuna modifica
    [InlineData("-5", false, null)]
    [InlineData("abc", false, null)]
    public void TryParseLevel_DistinguishesEmptyFromInvalid(string raw, bool expectedOk, double? expectedLevel)
    {
        var ok = TradingPageService.TryParseLevel(raw, out var level);

        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedLevel is null ? (decimal?)null : (decimal)expectedLevel.Value, level);
    }

    [Fact]
    public async Task SaveSlTpAsync_SendsEditedValues_ThenClearsThePendingEdit()
    {
        var (service, engine) = Build();
        var pos = new OpenPosition { PositionId = "p1", StopLoss = 58000m };
        engine.PositionsToReturn = [pos];
        await service.RefreshAsync(0); // popola service.Positions con "p1"

        service.SetSlEdit("p1", "59500");
        await service.SaveSlTpAsync(0, "p1");

        // Il motore ha ricevuto il valore MODIFICATO, non quello originale della posizione.
        Assert.Equal(59500m, engine.LastSlTp!.Value.Sl);
        // L'edit è stato ripulito: SlValue ora ricade sul valore della posizione (invariato nel fake
        // engine, che non applica davvero SetStopLossTakeProfitAsync al proprio stato) invece di
        // continuare a mostrare "59500" come se fosse ancora in sospeso.
        Assert.Equal("58000", service.SlValue(pos));
    }

    /// <summary>
    /// [2026-08-17] Il difetto che questo test blocca: <c>ParseLevel</c> collassava in <c>null</c>
    /// tre significati diversi — campo svuotato di proposito, testo non parsabile, valore ≤ 0 — e
    /// <c>SetSlEdit</c> scriveva comunque la chiave, quindi quel null vinceva sul valore esistente
    /// e arrivava al motore, che lo interpreta come AZZERAMENTO. Un «-59800» digitato per errore
    /// RIMUOVEVA lo stop loss, con un messaggio VERDE «SL/TP/Trailing aggiornati» a confermarlo.
    /// </summary>
    [Theory]
    [InlineData("-59800")]   // segno rimasto dal campo precedente
    [InlineData("0")]
    [InlineData("abc")]
    public async Task SaveSlTpAsync_InvalidStopLoss_DoesNotDisarmTheProtection(string raw)
    {
        var (service, engine) = Build();
        engine.PositionsToReturn = [new OpenPosition { PositionId = "p1", Side = OrderSide.Buy, EntryPrice = 61000m, StopLoss = 59500m }];
        await service.RefreshAsync(0);

        service.SetSlEdit("p1", raw);
        await service.SaveSlTpAsync(0, "p1");

        Assert.Null(engine.LastSlTp);          // nessun comando inviato
        Assert.True(service.IsError);          // e lo si dice, in rosso
        Assert.Contains("non valido", service.Message);
    }

    /// <summary>Il complemento: svuotare il campo È una richiesta legittima di RIMUOVERE la protezione.</summary>
    [Fact]
    public async Task SaveSlTpAsync_EmptyField_RemovesTheProtection_AndSaysSo()
    {
        var (service, engine) = Build();
        engine.PositionsToReturn = [new OpenPosition { PositionId = "p1", Side = OrderSide.Buy, EntryPrice = 61000m, StopLoss = 59500m }];
        await service.RefreshAsync(0);

        service.SetSlEdit("p1", "");
        await service.SaveSlTpAsync(0, "p1");

        Assert.NotNull(engine.LastSlTp);
        Assert.Null(engine.LastSlTp!.Value.Sl);
        Assert.Contains("RIMOSSA", service.Message);
        Assert.True(service.IsError);          // giallo/rosso: la posizione resta esposta
    }

    /// <summary>Uno stop dalla parte sbagliata del prezzo d'ingresso non è una protezione: è un'uscita immediata.</summary>
    [Fact]
    public async Task SaveSlTpAsync_StopOnTheWrongSide_IsRefused()
    {
        var (service, engine) = Build();
        engine.PositionsToReturn = [new OpenPosition { PositionId = "p1", Side = OrderSide.Buy, EntryPrice = 61000m }];
        await service.RefreshAsync(0);

        service.SetSlEdit("p1", "62000");   // long con stop SOPRA l'ingresso
        await service.SaveSlTpAsync(0, "p1");

        Assert.Null(engine.LastSlTp);
        Assert.True(service.IsError);
    }

    // --- Comandi: un fallimento non deve abbattere il circuito Blazor -------------------------

    /// <summary>
    /// [2026-08-17] In Blazor Server un'eccezione non gestita in un handler @onclick è fatale per il
    /// circuito, e nell'app non esiste alcun ErrorBoundary. Con il motore remoto in riavvio —
    /// evento ATTESO, il Deployment usa strategy Recreate — premere «EMERGENCY STOP» faceva morire
    /// la pagina, mentre il banner prometteva un fallimento gestito.
    /// </summary>
    [Theory]
    [InlineData("stop")]
    [InlineData("emergency")]
    [InlineData("close")]
    [InlineData("confirm")]
    [InlineData("reject")]
    [InlineData("sltp")]
    public async Task Commands_EngineUnreachable_ReportTheFailure_WithoutThrowing(string verbo)
    {
        var (service, engine) = Build();
        engine.PositionsToReturn = [new OpenPosition { PositionId = "p1", Side = OrderSide.Buy, EntryPrice = 100m }];
        await service.RefreshAsync(0);
        engine.ThrowOnCommand = new RpcException(new Status(StatusCode.Unavailable, "motore in riavvio"));

        var azione = verbo switch
        {
            "stop" => service.StopAsync(0),
            "emergency" => service.EmergencyAsync(0),
            "close" => service.CloseAsync(0, "p1"),
            "confirm" => service.ConfirmAsync(0, "o1", "u1"),
            "reject" => service.RejectAsync(0, "o1", "u1"),
            _ => service.SaveSlTpAsync(0, "p1"),
        };

        await azione;   // NON deve propagare

        Assert.True(service.IsError);
        Assert.NotNull(service.Message);
    }

    // --- PromoteAsync: refresh mirato -----------------------------------------------------------

    [Fact]
    public async Task PromoteAsync_TargetLaneMatchesViewedLane_RefreshesEngineStatus()
    {
        var promoter = new RecordingPromoter();
        var (service, engine) = Build(promoter: promoter);
        engine.StatusToReturn = new TradingEngineStatus { Mode = TradingMode.Testnet };

        await service.PromoteAsync(laneId: 0, newMode: TradingMode.Testnet, currentlyViewedLaneId: 0);

        Assert.Equal((0, TradingMode.Testnet, "Promozione manuale dall'operatore"), promoter.LastPromotion);
        Assert.Equal(TradingMode.Testnet, service.Status!.Mode); // RefreshAsync(0) è stato chiamato
        Assert.False(service.PromoIsError);
    }

    [Fact]
    public async Task PromoteAsync_TargetLaneDiffersFromViewedLane_DoesNotRefreshEngineStatus()
    {
        var (service, _) = Build();

        await service.PromoteAsync(laneId: 1, newMode: TradingMode.Testnet, currentlyViewedLaneId: 0);

        // Nessuna corsia 1 registrata nel provider di test: se il servizio avesse provato comunque a
        // risolvere Engine(1) per il refresh, questa asserzione (o un'eccezione di risoluzione DI a
        // monte) lo avrebbe rivelato. Status resta quello di default (mai popolato).
        Assert.Null(service.Status);
    }

    [Fact]
    public async Task PromoteAsync_PromoterThrows_ReportsErrorMessage_AndStopsBeingBusy()
    {
        var throwingPromoter = new ThrowingPromoter();
        var (service, _) = Build(promoter: throwingPromoter);

        await service.PromoteAsync(laneId: 0, newMode: TradingMode.Testnet, currentlyViewedLaneId: 0);

        Assert.True(service.PromoIsError);
        Assert.Contains("Promozione fallita", service.PromoMessage);
        Assert.False(service.PromoBusy);
    }

    private sealed class ThrowingPromoter : ILanePromoter
    {
        public Task PromoteLaneAsync(int laneId, TradingMode newMode, string reason, CancellationToken ct = default)
            => throw new InvalidOperationException("rifiutato dal dominio");
    }
}
