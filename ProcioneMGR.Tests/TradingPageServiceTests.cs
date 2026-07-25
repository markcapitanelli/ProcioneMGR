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

    private sealed class RecordingSafetyWriter : ISafetyConfigWriter
    {
        public SafetyConfiguration? Saved { get; private set; }
        public int Calls { get; private set; }
        public Exception? ThrowOnSave { get; set; }
        public Task SaveAsync(SafetyConfiguration cfg, CancellationToken ct = default)
        {
            if (ThrowOnSave is not null) return Task.FromException(ThrowOnSave);
            Saved = cfg;
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static (TradingPageService Service, FakeTradingEngine Engine0) Build(
        SafetyConfiguration? safety = null, IPromotionEvaluator? promotionEval = null,
        ILanePromoter? promoter = null, ISafetyConfigWriter? safetyWriter = null,
        ILaneQuarantineStore? quarantineStore = null)
    {
        var engine0 = new FakeTradingEngine(0);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton<ITradingEngine>(0, engine0);
        services.AddMediator();
        var provider = services.BuildServiceProvider();

        var service = new TradingPageService(
            provider.GetRequiredService<IMediator>(),
            promotionEval ?? new FakePromotionEvaluator([]),
            promoter ?? new RecordingPromoter(),
            (safety ?? new SafetyConfiguration { MaxPositionSizePercent = 10m, MaxTotalExposurePercent = 50m, MaxOpenPositions = 5, MaxLeverageAllowed = 5 }).AsMonitor(),
            safetyWriter ?? new RecordingSafetyWriter(),
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
        var writer = new RecordingSafetyWriter();
        var (service, _) = Build(safetyWriter: writer);
        service.ReloadSafety();
        service.Safety.MaxPositionSizePercent = maxPos;
        service.Safety.MaxTotalExposurePercent = maxExposure;
        service.Safety.MaxOpenPositions = maxOpen;
        service.Safety.MaxLeverageAllowed = maxLeverage;

        await service.SaveSafetyAsync();

        Assert.Equal(0, writer.Calls);
        Assert.True(service.IsError);
        Assert.Contains("non validi", service.Message);
    }

    [Fact]
    public async Task SaveSafetyAsync_NegativeFeePercent_RejectsWithoutCallingWriter()
    {
        // P2-8: zero è un valore lecito (promozione a fee zero, test), negativo no — nessun exchange
        // paga per tradare in questo contesto, e un fee negativo alimenterebbe un PnL live gonfiato.
        var writer = new RecordingSafetyWriter();
        var (service, _) = Build(safetyWriter: writer);
        service.ReloadSafety();
        service.Safety.FeePercent = -0.1m;

        await service.SaveSafetyAsync();

        Assert.Equal(0, writer.Calls);
        Assert.True(service.IsError);
    }

    [Fact]
    public async Task SaveSafetyAsync_ZeroFeePercent_IsAccepted()
    {
        var writer = new RecordingSafetyWriter();
        var (service, _) = Build(safetyWriter: writer);
        service.ReloadSafety();
        service.Safety.FeePercent = 0m;

        await service.SaveSafetyAsync();

        Assert.Equal(1, writer.Calls);
        Assert.False(service.IsError);
    }

    [Fact]
    public async Task SaveSafetyAsync_ValidValues_PersistsAndReportsSuccess()
    {
        var writer = new RecordingSafetyWriter();
        var (service, _) = Build(safetyWriter: writer);
        service.ReloadSafety();
        service.Safety.MaxDrawdownPercent = 12.5m;

        await service.SaveSafetyAsync();

        Assert.Equal(1, writer.Calls);
        Assert.Equal(12.5m, writer.Saved!.MaxDrawdownPercent);
        Assert.False(service.IsError);
    }

    [Fact]
    public async Task SaveSafetyAsync_WriterThrows_ReportsErrorMessage()
    {
        var writer = new RecordingSafetyWriter { ThrowOnSave = new InvalidOperationException("disco pieno") };
        var (service, _) = Build(safetyWriter: writer);
        service.ReloadSafety();

        await service.SaveSafetyAsync();

        Assert.True(service.IsError);
        Assert.Contains("disco pieno", service.Message);
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
