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
        public Task StopAsync(CancellationToken ct = default) { StopCalled = true; return Task.CompletedTask; }
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) { LastEmergencyReason = reason; return Task.CompletedTask; }
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => Task.FromResult(PositionsToReturn);
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) { LastClosedPositionId = positionId; return Task.CompletedTask; }
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default)
        {
            LastSlTp = (stopLoss, takeProfit, trailingStopPercent);
            return Task.CompletedTask;
        }
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => Task.FromResult(PendingToReturn);
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default)
        {
            LastConfirmedOrderId = orderId;
            LastConfirmedUserId = userId;
            return Task.CompletedTask;
        }
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default)
        {
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

    [Fact]
    public async Task RefreshAsync_NonRpcException_IsSwallowed_WithoutTouchingStaleness()
    {
        // Contratto esatto del @code originale: il catch generico non imposta StaleSince/LastStaleReason
        // (solo RpcException lo fa) — un'eccezione non-gRPC (es. bug nel mapping locale) non deve far
        // comparire il banner "servizio di trading non risponde", che parla specificamente di gRPC.
        var (service, engine) = Build();
        engine.ThrowOnRefresh = new InvalidOperationException("bug locale");

        await service.RefreshAsync(0);

        Assert.Null(service.StaleSince);
        Assert.Null(service.LastStaleReason);
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
    [InlineData("0", null)]      // zero non è un livello valido (azzeramento passa da un percorso esplicito)
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData("1234.5", 1234.5)]
    public void ParseLevel_ValidatesPositiveDecimalsOnly(string raw, double? expected)
    {
        var result = TradingPageService.ParseLevel(raw);
        Assert.Equal(expected is null ? (decimal?)null : (decimal)expected.Value, result);
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
