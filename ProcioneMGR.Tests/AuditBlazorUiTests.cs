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
/// <remarks>
/// UN FALLIMENTO INTERMITTENTE CON CAUSA **NON IDENTIFICATA** (2026-07-28), annotato qui perché chi lo
/// rivedrà non ricominci da zero. <c>Trading_ConfirmPendingOrder_CallsEngine_WithCorrectOrderId</c> è
/// fallito **una volta** in una suite intera (1832/1833) e non si è più ripresentato in tre suite
/// successive; non è riproducibile da solo, con la sua classe, né forzando la concorrenza con le
/// classi sospette.
///
/// **Ipotesi già ESCLUSA, non ripercorrerla**: «<see cref="TradingLanes.Count"/> è uno static di
/// processo che <c>TradingLanesCountTests</c> cambia mentre questa classe renderizza». Non può
/// accadere: quella collezione è dichiarata con <c>DisableParallelization = true</c> (vedi
/// <c>TradingLanesCollection</c>), quindi non gira insieme a nessun'altra. Ci ero cascato, e la
/// verifica del meccanismo — non la sua plausibilità — l'ha smentita.
///
/// **Sospetto residuo, non verificato**: qui si fa <c>Render</c> e subito
/// <c>FindAll("button").Single(...)</c>. Se sotto carico un render asincrono non fosse ancora
/// completo, <c>Single</c> troverebbe zero elementi ed esploderebbe esattamente così. La strada, se
/// tornasse, è <c>WaitForAssertion</c> al posto del click immediato.
/// </remarks>
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

        /// <summary>Modalità in cui la corsia si trova (non quella scelta nel form).</summary>
        public TradingMode Mode { get; set; } = TradingMode.Paper;

        public List<Order> PendingToReturn { get; set; } = [];
        public TradingMode? StartedWith { get; private set; }
        public bool StopCalled { get; private set; }
        public string? LastEmergencyReason { get; private set; }
        public (string OrderId, string? UserId)? LastConfirmed { get; private set; }
        public (string OrderId, string? UserId)? LastRejected { get; private set; }

        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new TradingEngineStatus { Mode = Mode, IsRunning = IsRunning, Symbol = "BTC/USDT" });
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
        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => Task.CompletedTask;
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

    /// <summary>Elenco corsie fittizio: una scheda per corsia, senza toccare il database.</summary>
    private sealed class FakeLaneDirectory : ILaneDirectory
    {
        public Task<IReadOnlyList<LaneSummary>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LaneSummary>>(
                [.. Enumerable.Range(0, TradingLanes.Count)
                    .Select(i => new LaneSummary(i, "BTC/USDT", "1h", "Paper", false))]);
    }

    private (Infrastructure.FakeEngineConfigStore Store, FakeTradingEngine[] Engines) RegisterTradingServices()
    {
        Services.AddLogging();
        Services.AddMediator();
        var engines = new FakeTradingEngine[TradingLanes.Count];
        for (var lane = 0; lane < TradingLanes.Count; lane++)
        {
            engines[lane] = new FakeTradingEngine(lane);
            Services.AddKeyedSingleton<ITradingEngine>(lane, engines[lane]);
        }
        // [E1] Le soglie arrivano dallo store del MOTORE, non più da IOptionsMonitor del guscio.
        // Default sani (>0) così l'apertura del pannello non è già in stato invalido.
        var store = new Infrastructure.FakeEngineConfigStore();
        store.Seed("Trading:Safety", new SafetyConfiguration
        {
            MaxPositionSizePercent = 10m, MaxTotalExposurePercent = 50m, MaxDailyLossPercent = 5m,
            MaxDrawdownPercent = 20m, MaxOpenPositions = 5, MaxLeverageAllowed = 5,
        });
        Services.AddSingleton<IEngineConfigStore>(store);
        Services.AddSingleton<IPromotionEvaluator>(new FakePromotionEvaluator([
            ReadyPaperLane(0), ReadyPaperLane(1), ReadyPaperLane(2),
        ]));
        Services.AddSingleton<ILanePromoter>(new ThrowingPromoter());
        Services.AddSingleton<ILaneQuarantineStore>(new Infrastructure.FakeLaneQuarantineStore());
        // Elenco corsie del selettore: qui non è l'oggetto del test, ma la pagina non renderizza
        // senza. Restituisce le corsie configurate quanto basta perché le schede abbiano un'etichetta.
        Services.AddSingleton<ILaneDirectory>(new FakeLaneDirectory());
        Services.AddSingleton<ProcioneMGR.Services.Security.IMasterKeyProbe>(new Infrastructure.FakeMasterKeyProbe());
        Services.AddScoped<TradingPageService>();
        // [B3] Diagnostica delle uscite protettive. La factory di DbContext qui LANCIA, ed e'
        // voluto: cosi' ogni render della pagina verifica anche che una diagnostica rotta non porti
        // giu' la pagina da cui si comanda il motore — la proprieta' che conta piu' del contenuto
        // del pannello, e che il servizio garantisce catturando e loggando.
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext>>(new ThrowingDbFactory());
        Services.AddSingleton<ProtectiveExitLagAnalyzer>();
        Services.AddScoped<ProtectiveExitDiagnosticsService>();
        return (store, engines);
    }

    private sealed class ThrowingPromoter : ILanePromoter
    {
        public Task PromoteLaneAsync(int laneId, TradingMode newMode, string reason, CancellationToken ct = default)
            => throw new InvalidOperationException("Il test non deve promuovere davvero.");
    }

    [Fact]
    public void Trading_SwitchingLane_AdoptsThatLaneMode_NotTheFormDefault()
    {
        // REGRESSIONE trovata provando la pagina dal vivo il 2026-07-25. Il radio "Modalità" è lo
        // stato di un form ("in che modalità avviare"), non del dominio, e restava sul suo default:
        // scegliendo la corsia 2, che gira in Testnet, continuava a dire Paper. Un "Ferma" seguito
        // da "Avvia" l'avrebbe fatta ripartire in PAPER senza che nulla lo dicesse — e siccome le
        // posizioni sono discriminate per modalità, quella corsia sarebbe diventata cieca alle
        // proprie posizioni Testnet.
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var (_, engines) = RegisterTradingServices();
        engines[2].Mode = TradingMode.Testnet;

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();

        // Prima: la corsia 0 è Paper e il radio dice Paper.
        Assert.True(cut.Find("input#m_Paper").HasAttribute("checked"));

        // Si passa alla corsia 2 (Testnet) cliccando la sua scheda.
        cut.FindAll("button.lane-chip")[2].Click();

        Assert.True(cut.Find("input#m_Testnet").HasAttribute("checked"));
        Assert.False(cut.Find("input#m_Paper").HasAttribute("checked"));
    }

    [Fact]
    public void Trading_PromotionsTable_HasNoLiveButtonAtAll_ButOffersTestnet()
    {
        // [F8, 2026-08-03] Il contratto è cambiato: il pulsante "Promuovi a Live" perennemente
        // disabilitato NON esiste più (un bottone morto è rumore che rassicura). La trappola di
        // sicurezza resta, più forte: dalla tabella promozioni non deve esistere ALCUN pulsante
        // verso Live — cliccabile o meno. L'unico percorso Live è il controllo di modalità con
        // conferma esplicita (test dedicato qui sotto).
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Live"));

        // Il percorso lecito invece c'è ed è abilitato: "→ Testnet" per la corsia Paper attiva.
        var testnetButtons = cut.FindAll("button").Where(b => b.TextContent.Contains("Testnet")).ToList();
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
        // [2026-08-17] La conferma è ora a DUE passi (vedi il test qui sotto): il primo clic apre la
        // domanda, il secondo invia. Il contratto che questo test difende — l'OrderId giusto arriva
        // al motore — resta identico.
        cut.FindAll("button").Single(b => b.TextContent.Contains("Conferma")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("SÌ, INVIA")).Click();

        Assert.Equal("confirm-me", engines[0].LastConfirmed?.OrderId);
    }

    /// <summary>
    /// [2026-08-17] «✔ Conferma» inviava un ordine REALE all'exchange con UN SOLO clic, in una
    /// tabella che il polling ridisegna ogni 2 secondi e che mette i più recenti IN CIMA: un ordine
    /// nuovo proposto dal worker faceva scorrere le righe sotto il cursore, e il clic partiva su un
    /// ordine che l'operatore non aveva mai letto. Era anche l'unica azione distruttiva della pagina
    /// a un passo solo — emergency stop, svuotamento corsia e chiusura orfane ne hanno due.
    /// </summary>
    [Fact]
    public void Trading_ConfirmPendingOrder_FirstClickOnlyAsks_AndNamesTheOrder()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var (_, engines) = RegisterTradingServices();
        engines[0].PendingToReturn = [new Order { OrderId = "ord-A", Side = OrderSide.Buy, Quantity = 120m, Price = 0.58m }];

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Conferma")).Click();

        // Nessun ordine è partito, e la domanda dice COSA si sta per inviare.
        Assert.Null(engines[0].LastConfirmed);
        Assert.Contains("con soldi veri?", cut.Markup);
        Assert.Contains("120", cut.Markup);
    }

    /// <summary>
    /// [2026-08-17] Le conferme ARMATE non attraversano il cambio di corsia. Era già la regola per
    /// il checkbox Live («la conferma esplicita non sopravvive a un cambio di contesto»), ma
    /// _confirmEmergency e _confirmClear erano rimasti fuori: restando armati, un clic su «SÌ, FERMA
    /// TUTTO» dopo il cambio scheda chiudeva TUTTE le posizioni di un'ALTRA corsia — e il testo di
    /// conferma non nominava nemmeno la corsia, quindi nulla lo diceva.
    /// </summary>
    [Fact]
    public void Trading_ArmedEmergencyConfirmation_DoesNotSurviveLaneSwitch()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var (_, engines) = RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();
        cut.FindAll("button").Single(b => b.TextContent.Contains("EMERGENCY STOP")).Click();
        Assert.Contains("SÌ, FERMA TUTTO", cut.Markup);

        cut.FindAll("button.lane-chip")[2].Click();

        // La conferma è disarmata, e nessuna corsia è stata toccata.
        Assert.DoesNotContain("SÌ, FERMA TUTTO", cut.Markup);
        Assert.Null(engines[0].LastEmergencyReason);
        Assert.Null(engines[2].LastEmergencyReason);
    }

    /// <summary>Il prompt d'emergenza deve dire su QUALE corsia sta per agire.</summary>
    [Fact]
    public void Trading_EmergencyPrompt_NamesTheLane()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();
        cut.FindAll("button.lane-chip")[2].Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("EMERGENCY STOP")).Click();

        Assert.Contains("della corsia 2", cut.Markup);
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
        var (store, _) = RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();

        // Modifica la soglia di drawdown e salva: lo store del MOTORE deve ricevere ESATTAMENTE
        // il nuovo valore sulla sezione giusta (E1: niente più file del guscio).
        cut.Find("#safety_maxdd").Change("12.5");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Salva configurazione")).Click();

        var (section, options) = Assert.Single(store.Saved);
        Assert.Equal("Trading:Safety", section);
        Assert.Equal(12.5m, ((SafetyConfiguration)options).MaxDrawdownPercent);
        Assert.Contains("salvate", cut.Markup);
    }

    [Fact]
    public void Trading_SaveSafetyForm_InvalidValues_ShowError_AndDoNotPersist()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        var (store, _) = RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();

        // Max size a 0: validazione server-side del componente -> messaggio d'errore, nessun salvataggio.
        cut.Find("#safety_maxpos").Change("0");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Salva configurazione")).Click();

        Assert.Empty(store.Saved);
        Assert.Contains("Valori non validi", cut.Markup);
    }

    // --- [B3] Diagnostica delle uscite protettive ------------------------------------------------

    /// <summary>
    /// Il pannello deve esserci ANCHE quando la diagnostica non riesce a leggere niente (qui la
    /// factory di DbContext lancia), e deve dire che il verdetto di B3 e' quello misurato: le uscite
    /// sono guidate dalle candele per MISURA, non in attesa di una misura.
    ///
    /// Il pannello vuoto e' un caso legittimo e frequente — su queste corsie le uscite protettive
    /// sono pochi eventi al mese — quindi deve distinguersi da un guasto: "non e' ancora successo"
    /// non e' "non funziona".
    /// </summary>
    [Fact]
    public void Trading_PannelloRitardoUscite_CePureSenzaDati_ESpiegaIlVerdetto()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();

        Assert.Contains("Ritardo delle uscite protettive", cut.Markup);
        Assert.Contains("guidate dalle", cut.Markup);
        Assert.Contains("peggio", cut.Markup);                    // il verdetto misurato, non un forse
        Assert.Contains("non e' ancora successo".Replace("e'", "è"), cut.Markup);
        Assert.NotNull(cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Misura il ritardo")));
    }

    /// <summary>
    /// La pagina NON deve inventare un allarme di posizioni orfane quando non ce ne sono: la
    /// diagnostica qui fallisce (DB che lancia) e il blocco rosso non deve comparire lo stesso.
    /// Un allarme che appare per un errore di lettura e' peggio di nessun allarme, perche' insegna
    /// a ignorarlo.
    /// </summary>
    [Fact]
    public void Trading_SenzaOrfane_NessunBloccoRosso()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("auditor");
        auth.SetRoles(AppRoles.Admin);
        RegisterTradingServices();

        var cut = Render<ProcioneMGR.Components.Pages.Trading>();

        Assert.DoesNotContain("Posizioni orfane", cut.Markup);
    }

    /// <summary>
    /// REGRESSIONE trovata cliccando il pulsante sul serio, non leggendo il codice. L'esito della
    /// chiusura stava DENTRO il blocco delle posizioni orfane: quando la chiusura riesce la lista si
    /// svuota, il blocco sparisce, e con lui la conferma — l'operatore vedeva la riga svanire senza
    /// sapere a che prezzo fosse stata chiusa, ne' se fosse stata chiusa affatto.
    ///
    /// Il markup del messaggio deve quindi stare FUORI dal condizionale sulla lista.
    /// </summary>
    [Fact]
    public void Trading_EsitoDellaChiusuraOrfana_NonVive_DentroIlBloccoCheSparisce()
    {
        var sorgente = System.IO.File.ReadAllText(TrovaTradingRazor());

        var inizioBlocco = sorgente.IndexOf("@if (_orphans.Count > 0)", StringComparison.Ordinal);
        var posizioneMessaggio = sorgente.IndexOf("_orphanMessage is not null", StringComparison.Ordinal);

        Assert.True(inizioBlocco > 0, "blocco delle orfane non trovato");
        Assert.True(posizioneMessaggio > 0, "messaggio d'esito non trovato");
        Assert.True(posizioneMessaggio < inizioBlocco,
            "l'esito della chiusura deve stare PRIMA del blocco condizionato alla presenza di orfane: "
            + "dentro, sparirebbe proprio quando l'operazione riesce.");
    }

    private static string TrovaTradingRazor()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidato = System.IO.Path.Combine(dir, "ProcioneMGR", "Components", "Pages", "Trading.razor");
            if (System.IO.File.Exists(candidato)) return candidato;
            dir = System.IO.Directory.GetParent(dir)?.FullName;
        }
        throw new System.IO.FileNotFoundException("Trading.razor non trovato risalendo dall'output dei test.");
    }
}
