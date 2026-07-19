using Bunit;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Audit FASE 4 — test bUnit dei componenti critici:
///  1. La Dashboard renderizza con dati fittizi (evidenze di promozione incluse) e la UI è in italiano.
///  2. Il pulsante "Promuovi a Live" in /trading è SEMPRE disabilitato, e "Avvia trading" in
///     modalità Live resta disabilitato finché l'operatore non spunta la conferma esplicita.
///  3. Il form dati della Dashboard valida lato client (intervallo invertito, symbol mancante)
///     SENZA invocare il servizio di ingestione.
/// </summary>
public class AuditBlazorUiTests : BunitContext
{
    public AuditBlazorUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // --- Fake condivisi -------------------------------------------------------------------------

    private sealed class FakePromotionEvaluator(IReadOnlyList<PromotionDecision> decisions) : IPromotionEvaluator
    {
        public Task<PromotionDecision> EvaluateLaneAsync(int laneId, CancellationToken ct = default)
            => Task.FromResult(decisions.First(d => d.LaneId == laneId));

        public Task<IReadOnlyList<PromotionDecision>> EvaluateAllLanesAsync(CancellationToken ct = default)
            => Task.FromResult(decisions);
    }

    private sealed class RecordingIngestion : IOhlcvIngestionService
    {
        public int Calls;

        public Task<IngestionResult> IngestHistoricalDataAsync(string exchangeName, string symbol, string timeframe,
            DateTime from, DateTime to, IProgress<IngestionProgress>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new IngestionResult(0, false));
        }
    }

    private sealed class ThrowingDbFactory : Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => throw new InvalidOperationException("Il test non deve toccare il DB.");
    }

    private sealed class FakeExchangeFactory : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => throw new NotSupportedException();
        public IExchangeClient Create(string exchangeName) => throw new NotSupportedException();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotSupportedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotSupportedException();
    }

    private sealed class FakeTradingEngine(int laneId) : ITradingEngine
    {
        public int LaneId => laneId;
        public bool IsRunning { get; set; }
        public List<Order> PendingToReturn { get; set; } = [];
        public TradingMode? StartedWith { get; private set; }
        public bool StopCalled { get; private set; }
        public string? LastEmergencyReason { get; private set; }
        public (string OrderId, string? UserId)? LastConfirmed { get; private set; }
        public (string OrderId, string? UserId)? LastRejected { get; private set; }

        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new TradingEngineStatus { Mode = TradingMode.Paper, IsRunning = IsRunning, Symbol = "BTC/USDT" });
        public Task StartAsync(TradingMode mode, CancellationToken ct = default) { StartedWith = mode; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { StopCalled = true; return Task.CompletedTask; }
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) { LastEmergencyReason = reason; return Task.CompletedTask; }
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => Task.FromResult(new List<OpenPosition>());
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => Task.FromResult(PendingToReturn);
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default) { LastConfirmed = (orderId, userId); return Task.CompletedTask; }
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) { LastRejected = (orderId, userId); return Task.CompletedTask; }
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new TradingPerformance());
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingSafetyWriter : ISafetyConfigWriter
    {
        public SafetyConfiguration? Saved { get; private set; }
        public int Calls { get; private set; }

        public Task SaveAsync(SafetyConfiguration cfg, CancellationToken ct = default)
        {
            Saved = cfg;
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static PromotionDecision ReadyPaperLane(int lane = 0) => new()
    {
        LaneId = lane,
        Symbol = "BTC/USDT",
        CurrentMode = TradingMode.Paper,
        SuggestedMode = TradingMode.Testnet,
        ReadyForTestnet = true,
        IsRunning = true,
        Reason = "tutti i criteri soddisfatti",
        Metrics = new LaneMetrics { RealizedSharpe = 1.4m, TradeCount = 55, MaxDrawdown = 6m, WinRate = 0.58m, ObservationPeriod = TimeSpan.FromDays(30) },
    };

    private void RegisterDashboardServices(RecordingIngestion ingestion)
    {
        Services.AddSingleton<IOhlcvIngestionService>(ingestion);
        Services.AddSingleton<IExchangeClientFactory>(new FakeExchangeFactory());
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext>>(new ThrowingDbFactory());
        Services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();
        Services.AddSingleton<IPromotionEvaluator>(new FakePromotionEvaluator([ReadyPaperLane()]));
    }

    // --- Test 1: rendering della Dashboard con dati fittizi --------------------------------------

    [Fact]
    public void Dashboard_RendersWithFakeData_ShowsPromotionHighlight_InItalian()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        RegisterDashboardServices(new RecordingIngestion());

        var cut = Render<ProcioneMGR.Components.Pages.Dashboard>();

        Assert.Contains("Dashboard", cut.Find("h1").TextContent);
        // Widget promozioni (solo Admin/Manager) alimentato dal fake: corsia pronta per Testnet.
        Assert.Contains("Promozioni corsie", cut.Markup);
        Assert.Contains("Pronta per Testnet", cut.Markup);
        // Controlli del form dati presenti e in italiano.
        Assert.Contains("Carica simboli", cut.Markup);
        Assert.NotNull(cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Scarica dati")));
    }

    // --- Test 2: il percorso verso Live è sbarrato nella UI --------------------------------------

    private (RecordingSafetyWriter Writer, FakeTradingEngine[] Engines) RegisterTradingServices()
    {
        Services.AddLogging();
        Services.AddMediator();
        var engines = new FakeTradingEngine[TradingLanes.Count];
        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            engines[lane] = new FakeTradingEngine(lane);
            Services.AddKeyedSingleton<ITradingEngine>(lane, engines[lane]);
        }
        // Soglie di default sane (>0) così l'apertura del pannello non è già in stato invalido.
        Services.AddSingleton(new SafetyConfiguration
        {
            MaxPositionSizePercent = 10m, MaxTotalExposurePercent = 50m, MaxDailyLossPercent = 5m,
            MaxDrawdownPercent = 20m, MaxOpenPositions = 5, MaxLeverageAllowed = 5,
        }.AsMonitor());
        var writer = new RecordingSafetyWriter();
        Services.AddSingleton<ISafetyConfigWriter>(writer);
        Services.AddSingleton<IPromotionEvaluator>(new FakePromotionEvaluator([
            ReadyPaperLane(0), ReadyPaperLane(1), ReadyPaperLane(2),
        ]));
        Services.AddSingleton<ILanePromoter>(new ThrowingPromoter());
        Services.AddSingleton<ILaneQuarantineStore>(new Infrastructure.FakeLaneQuarantineStore());
        Services.AddSingleton<ProcioneMGR.Services.Security.IMasterKeyProbe>(new Infrastructure.FakeMasterKeyProbe());
        Services.AddScoped<TradingPageService>();
        return (writer, engines);
    }

    private sealed class ThrowingPromoter : ILanePromoter
    {
        public Task PromoteLaneAsync(int laneId, TradingMode newMode, string reason, CancellationToken ct = default)
            => throw new InvalidOperationException("Il test non deve promuovere davvero.");
    }

    [Fact]
    public void Trading_PromuoviALiveButton_IsAlwaysDisabled()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();

        var liveButtons = cut.FindAll("button").Where(b => b.TextContent.Contains("Promuovi a Live")).ToList();
        Assert.NotEmpty(liveButtons); // il pannello promozioni è renderizzato (fake con 3 corsie)
        Assert.All(liveButtons, b => Assert.True(b.HasAttribute("disabled"),
            "TRAPPOLA DI SICUREZZA VIOLATA: trovato un pulsante 'Promuovi a Live' cliccabile"));

        // Il pulsante 'Promuovi a Testnet' invece esiste ed è abilitato (il percorso lecito).
        var testnetButtons = cut.FindAll("button").Where(b => b.TextContent.Contains("Promuovi a Testnet")).ToList();
        Assert.NotEmpty(testnetButtons);
        Assert.Contains(testnetButtons, b => !b.HasAttribute("disabled"));
    }

    [Fact]
    public void Trading_StartInLiveMode_RequiresExplicitConfirmationCheckbox()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();

        // Seleziona la modalità Live: appare l'avviso "soldi veri" e Start si disabilita.
        cut.Find("#m_Live").Change(new Microsoft.AspNetCore.Components.ChangeEventArgs());
        Assert.Contains("Stai per tradare con soldi veri", cut.Markup);

        var start = cut.FindAll("button").Single(b => b.TextContent.Contains("Avvia trading"));
        Assert.True(start.HasAttribute("disabled"), "Avvia trading in Live NON deve essere avviabile senza conferma");

        // Solo la spunta esplicita "Confermo" sblocca l'avvio (doppio check manuale).
        cut.Find("#liveok").Change(true);
        start = cut.FindAll("button").Single(b => b.TextContent.Contains("Avvia trading"));
        Assert.False(start.HasAttribute("disabled"));

        // In Paper/Testnet la conferma non è richiesta (il vincolo è SOLO sul Live).
        cut.Find("#m_Paper").Change(new Microsoft.AspNetCore.Components.ChangeEventArgs());
        start = cut.FindAll("button").Single(b => b.TextContent.Contains("Avvia trading"));
        Assert.False(start.HasAttribute("disabled"));
    }

    // --- Test 2b: i 7 comandi Mediator arrivano davvero all'engine, cliccando l'UI reale
    //     (Fase 1 §4.6 — sostituisce lo smoke test manuale "avvio/apertura/chiusura ordine" con
    //     un test bUnit ripetibile: stesso percorso UI->TradingPageService->IMediator->handler
    //     ->ITradingEngine, ma su un fake, mai un ordine vero) ------------------------------------

    [Fact]
    public void Trading_ClickAvviaTrading_CallsEngineStart_ThroughMediator()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var (_, engines) = RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Avvia trading")).Click();

        Assert.Equal(TradingMode.Paper, engines[0].StartedWith);
    }

    [Fact]
    public void Trading_ClickFermaTrading_CallsEngineStop_ThroughMediator()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var (_, engines) = RegisterTradingServices();
        engines[0].IsRunning = true; // altrimenti la UI mostra "Avvia trading", non "Ferma trading"

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Ferma trading")).Click();

        Assert.True(engines[0].StopCalled);
    }

    [Fact]
    public void Trading_EmergencyStop_FirstClickOnlyAsksConfirmation_SecondClickCallsEngine()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var (_, engines) = RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();

        // Primo click: solo la richiesta di conferma, l'engine non deve ancora essere chiamato —
        // un doppio-click accidentale non deve mai chiudere posizioni reali.
        cut.FindAll("button").Single(b => b.TextContent.Contains("EMERGENCY STOP")).Click();
        Assert.Null(engines[0].LastEmergencyReason);
        Assert.Contains("Chiuderà TUTTE le posizioni", cut.Markup);

        // Solo la conferma esplicita chiama davvero EmergencyStopCommand.
        cut.FindAll("button").Single(b => b.TextContent.Contains("SÌ, FERMA TUTTO")).Click();
        Assert.NotNull(engines[0].LastEmergencyReason);
    }

    [Fact]
    public void Trading_ConfirmPendingOrder_CallsEngine_WithCorrectOrderId()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var (_, engines) = RegisterTradingServices();
        engines[0].PendingToReturn = [new Order { OrderId = "confirm-me", Side = OrderSide.Buy }];

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Conferma")).Click();

        Assert.Equal("confirm-me", engines[0].LastConfirmed?.OrderId);
    }

    [Fact]
    public void Trading_RejectPendingOrder_CallsEngine_WithCorrectOrderId()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var (_, engines) = RegisterTradingServices();
        engines[0].PendingToReturn = [new Order { OrderId = "reject-me", Side = OrderSide.Sell }];

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Rifiuta")).Click();

        Assert.Equal("reject-me", engines[0].LastRejected?.OrderId);
    }

    // --- Test 3: validazione client del form dati ------------------------------------------------

    [Fact]
    public void Dashboard_InvalidDateRange_ShowsItalianError_AndNeverCallsIngestion()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var ingestion = new RecordingIngestion();
        RegisterDashboardServices(ingestion);

        var cut = Render<ProcioneMGR.Components.Pages.Dashboard>();

        // 'A' prima di 'Da': il form deve rifiutare SENZA chiamare il servizio.
        var dates = cut.FindAll("input[type=date]");
        dates[0].Change(DateTime.Today.AddDays(-10).ToString("yyyy-MM-dd"));
        dates = cut.FindAll("input[type=date]");
        dates[1].Change(DateTime.Today.AddDays(-20).ToString("yyyy-MM-dd"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Scarica dati")).Click();

        Assert.Contains("L'intervallo non è valido", cut.Markup);
        Assert.Equal(0, ingestion.Calls);
    }

    [Fact]
    public void Dashboard_EmptySymbol_ShowsItalianError_AndNeverCallsIngestion()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var ingestion = new RecordingIngestion();
        RegisterDashboardServices(ingestion);

        var cut = Render<ProcioneMGR.Components.Pages.Dashboard>();

        cut.Find("input[list=symbols]").Change("   ");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Scarica dati")).Click();

        Assert.Contains("Inserisci un symbol", cut.Markup);
        Assert.Equal(0, ingestion.Calls);
    }

    // --- Test 4: form soglie di sicurezza (solo Admin) -------------------------------------------

    [Fact]
    public void Trading_SaveSafetyForm_PersistsEditedThreshold()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var (writer, _) = RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();

        // Modifica la soglia di drawdown e salva: il writer deve ricevere ESATTAMENTE il nuovo valore.
        cut.Find("#safety_maxdd").Change("12.5");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Salva configurazione")).Click();

        Assert.Equal(1, writer.Calls);
        Assert.NotNull(writer.Saved);
        Assert.Equal(12.5m, writer.Saved!.MaxDrawdownPercent);
        Assert.Contains("salvata", cut.Markup);
    }

    [Fact]
    public void Trading_SaveSafetyForm_InvalidValues_ShowError_AndDoNotPersist()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var (writer, _) = RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();

        // Max size a 0: validazione server-side del componente -> messaggio d'errore, nessun salvataggio.
        cut.Find("#safety_maxpos").Change("0");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Salva configurazione")).Click();

        Assert.Equal(0, writer.Calls);
        Assert.Contains("Valori non validi", cut.Markup);
    }
}
