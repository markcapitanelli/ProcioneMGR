using Grpc.Core;
using Mediator;
using Microsoft.Extensions.Options;
using ProcioneMGR.Services.Trading.Commands;
using ProcioneMGR.Services.Trading.Queries;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Orchestrazione di <c>Components/Pages/Trading.razor</c> (P1-5, audit consolidamento
/// 2026-07-17): tutte le chiamate a <see cref="ITradingEngine"/>/<see cref="IPromotionEvaluator"/>/
/// <see cref="ILanePromoter"/>/<see cref="ISafetyConfigWriter"/> e lo stato che ne deriva, così la
/// logica di orchestrazione ha test unitari indipendenti da Blazor (vedi
/// <c>TradingPageServiceTests</c>). Il componente resta responsabile solo di ciò che è
/// intrinsecamente Blazor: rendering, ciclo di vita (<c>OnInitializedAsync</c>/<c>Dispose</c>,
/// <c>PollingTimer</c>), <c>StateHasChanged</c>, e la manciata di stato puramente di UI (modalità
/// radio-selezionata, checkbox di conferma Live, corsia attualmente visualizzata) che non richiede
/// alcuna chiamata a servizio.
///
/// Registrato Scoped: in Blazor Server uno scope = un circuito, quindi un'istanza per sessione
/// utente — stessa granularità del componente che la consuma, senza stato condiviso fra utenti.
///
/// La corsia (<c>laneId</c>) NON è stato interno di questo servizio ma un parametro esplicito di
/// ogni metodo: è una selezione di navigazione della UI (quale corsia sto guardando), non uno stato
/// di dominio — tenerla fuori evita che un'istanza single-per-circuito "ricordi" una corsia stantia.
/// </summary>
public sealed class TradingPageService(
    IMediator mediator,
    IPromotionEvaluator promotionEval,
    ILanePromoter promoter,
    IOptionsMonitor<SafetyConfiguration> safetyMonitor,
    ISafetyConfigWriter safetyWriter,
    ILaneQuarantineStore quarantineStore)
{
    public TradingEngineStatus? Status { get; private set; }

    /// <summary>Quarantena attiva della corsia visualizzata (Fase 0-A3), null se la corsia è pulita.</summary>
    public LaneQuarantine? Quarantine { get; private set; }
    public List<OpenPosition> Positions { get; private set; } = [];
    public List<Order> Orders { get; private set; } = [];
    public List<Order> Pending { get; private set; } = [];
    public List<Indicators.IndicatorSeries> Equity { get; private set; } = [];
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    /// <summary>Da quando il servizio di trading remoto (Trading:UseRemoteTrading) non risponde; null se l'ultimo refresh è andato a buon fine.</summary>
    public DateTime? StaleSince { get; private set; }

    /// <summary>Codice di stato gRPC dell'ultimo fallimento: dice all'operatore se il servizio è giù o solo lento/rotto.</summary>
    public string? LastStaleReason { get; private set; }

    public List<PromotionDecision> Promotions { get; private set; } = [];
    public bool PromoBusy { get; private set; }
    public string? PromoMessage { get; private set; }
    public bool PromoIsError { get; private set; }

    /// <summary>Copia di lavoro delle soglie di sicurezza (form Admin) — vedi <see cref="ReloadSafety"/>/<see cref="SaveSafetyAsync"/>.</summary>
    public SafetyConfiguration Safety { get; private set; } = new();

    // Valori SL/TP/Trailing in modifica: sopravvivono al refresh automatico finché non salvati.
    private readonly Dictionary<string, decimal?> _slEdits = new();
    private readonly Dictionary<string, decimal?> _tpEdits = new();
    private readonly Dictionary<string, decimal?> _tslEdits = new();

    public async Task RefreshAsync(int laneId)
    {
        try
        {
            // Le cinque letture sono indipendenti: in parallelo, non in fila. In modalità remota
            // (Trading:UseRemoteTrading) tre di queste sono round-trip gRPC — sommarne le latenze
            // ogni 2 secondi era solo attesa gratuita. Il motore regge già le chiamate concorrenti
            // per costruzione: il TradingWorker gli parla in parallelo alla UI da sempre.
            // Tutte le letture e i comandi di questa classe passano ora da IMediator (Fase 1) —
            // nessuna risoluzione diretta di ITradingEngine resta in questo file.
            var statusTask = mediator.Send(new GetLaneStatusQuery(laneId)).AsTask();
            var positionsTask = mediator.Send(new GetOpenPositionsQuery(laneId)).AsTask();
            var ordersTask = mediator.Send(new GetOrderHistoryQuery(laneId)).AsTask();
            var pendingTask = mediator.Send(new GetPendingOrdersQuery(laneId)).AsTask();
            // Finestra di 90 giorni (stesso taglio di Ensemble.razor), NON tutto lo storico: questa
            // pagina di perf usa solo l'equity curve (già bounded a 10k punti dal motore) — i
            // TradeRecord non li legge nessuno qui (vedi anche P3-12: il motore stesso li tronca ora).
            var perfTask = mediator.Send(new GetPerformanceQuery(laneId, DateTime.UtcNow.AddDays(-90))).AsTask();
            await Task.WhenAll(statusTask, positionsTask, ordersTask, pendingTask, perfTask);

            Status = statusTask.Result;
            Positions = positionsTask.Result;
            Orders = ordersTask.Result;
            Pending = pendingTask.Result;
            var perf = perfTask.Result;
            Equity = perf.EquityCurve.Count > 0
                ?
                [
                    new Indicators.IndicatorSeries
                    {
                        Title = "Equity", Color = "#2962FF", Type = Indicators.IndicatorSeriesType.Line,
                        Points = perf.EquityCurve.Select(p => new Indicators.IndicatorPoint(
                            new DateTimeOffset(DateTime.SpecifyKind(p.Timestamp, DateTimeKind.Utc)).ToUnixTimeSeconds(), (double)p.Capital)).ToList(),
                    },
                ]
                : [];

            // Giro riuscito: quello a schermo è di nuovo lo stato reale.
            StaleSince = null;
            LastStaleReason = null;
        }
        catch (RpcException ex)
        {
            // QUALUNQUE fallimento gRPC, non solo Unavailable: che il servizio sia irraggiungibile
            // (rolling restart), lento (DeadlineExceeded) o rotto (Internal), il risultato per chi
            // guarda è identico — i numeri a schermo sono quelli dell'ultimo giro riuscito. I dati
            // restano (svuotare la pagina durante un riavvio di pochi secondi sarebbe peggio) ma
            // vanno dichiarati vecchi. Il primo fallimento fissa l'istante, così il banner mostra da
            // quanto dura.
            StaleSince ??= DateTime.UtcNow;
            LastStaleReason = ex.StatusCode.ToString();
        }
        catch { /* refresh resiliente */ }

        // Fuori dal blocco gRPC: la quarantena vive nel DB condiviso, si legge anche col servizio
        // di trading giù (anzi, È il momento in cui l'operatore deve poterla vedere).
        try { Quarantine = await quarantineStore.GetAsync(laneId); }
        catch { /* refresh resiliente */ }
    }

    /// <summary>Rimozione della quarantena (solo Admin, dopo verifica): audit con lo userId di chi decide.</summary>
    public async Task ClearQuarantineAsync(int laneId, string? userId)
    {
        var removed = await quarantineStore.ClearAsync(laneId, userId);
        SetMsg(removed
            ? $"Quarantena della corsia {laneId} rimossa. La corsia può essere riavviata."
            : "Nessuna quarantena attiva da rimuovere.", false);
        await RefreshAsync(laneId);
    }

    public async Task RefreshPromotionsAsync()
    {
        PromoBusy = true;
        try
        {
            Promotions = (await promotionEval.EvaluateAllLanesAsync()).ToList();
        }
        catch (Exception ex) { PromoMessage = $"Valutazione promozioni fallita: {ex.Message}"; PromoIsError = true; }
        finally { PromoBusy = false; }
    }

    /// <param name="laneId">Corsia da promuovere.</param>
    /// <param name="newMode">Modalità di destinazione (mai Live: <see cref="ILanePromoter"/> lo rifiuta).</param>
    /// <param name="currentlyViewedLaneId">Corsia attualmente selezionata nella UI: se combacia con <paramref name="laneId"/>, il refresh dei KPI a schermo segue la promozione.</param>
    public async Task PromoteAsync(int laneId, TradingMode newMode, int currentlyViewedLaneId)
    {
        PromoBusy = true;
        PromoMessage = null;
        try
        {
            await promoter.PromoteLaneAsync(laneId, newMode, "Promozione manuale dall'operatore");
            PromoIsError = false;
            PromoMessage = $"Corsia {laneId} promossa a {newMode}.";
            await RefreshPromotionsAsync();
            if (laneId == currentlyViewedLaneId) await RefreshAsync(laneId);
        }
        catch (Exception ex) { PromoIsError = true; PromoMessage = $"Promozione fallita: {ex.Message}"; }
        finally { PromoBusy = false; }
    }

    public async Task StartAsync(int laneId, TradingMode mode)
    {
        try
        {
            await mediator.Send(new StartLaneCommand(laneId, mode));
            var note = mode switch
            {
                TradingMode.Paper => "Paper trading avviato. Il worker sta riproducendo le candele reali…",
                TradingMode.Testnet => "Testnet avviato: gli ordini vengono piazzati su Binance Testnet con le tue credenziali.",
                _ => "Live avviato. ⚠️ In Live ogni ordine richiede conferma manuale (safety): gli ordini automatici vengono rifiutati finché non confermati.",
            };
            SetMsg(note, false);
            await RefreshAsync(laneId);
        }
        catch (Exception ex)
        {
            SetMsg($"Avvio {mode} fallito: {ex.Message}", true);
        }
    }

    public async Task StopAsync(int laneId)
    {
        await mediator.Send(new StopLaneCommand(laneId));
        SetMsg("Trading fermato (posizioni lasciate aperte).", false);
        await RefreshAsync(laneId);
    }

    public async Task EmergencyAsync(int laneId)
    {
        await mediator.Send(new EmergencyStopCommand(laneId, "Stop manuale dall'operatore"));
        SetMsg("EMERGENCY STOP eseguito: tutte le posizioni chiuse.", false);
        await RefreshAsync(laneId);
    }

    public async Task CloseAsync(int laneId, string positionId)
    {
        await mediator.Send(new ClosePositionCommand(laneId, positionId));
        await RefreshAsync(laneId);
    }

    // --- Edit in corso di SL/TP/Trailing (form posizioni aperte) ------------------------------

    public string? SlValue(OpenPosition p) =>
        (_slEdits.TryGetValue(p.PositionId, out var v) ? v : p.StopLoss)?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string? TpValue(OpenPosition p) =>
        (_tpEdits.TryGetValue(p.PositionId, out var v) ? v : p.TakeProfit)?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string? TslValue(OpenPosition p) =>
        (_tslEdits.TryGetValue(p.PositionId, out var v) ? v : p.TrailingStopPercent)?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public void SetSlEdit(string id, string? raw) => _slEdits[id] = ParseLevel(raw);
    public void SetTpEdit(string id, string? raw) => _tpEdits[id] = ParseLevel(raw);
    public void SetTslEdit(string id, string? raw) => _tslEdits[id] = ParseLevel(raw);

    public static decimal? ParseLevel(string? raw) =>
        decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0m
            ? d : (decimal?)null;

    public async Task SaveSlTpAsync(int laneId, string positionId)
    {
        var pos = Positions.FirstOrDefault(p => p.PositionId == positionId);
        var sl = _slEdits.TryGetValue(positionId, out var s) ? s : pos?.StopLoss;
        var tp = _tpEdits.TryGetValue(positionId, out var t) ? t : pos?.TakeProfit;
        var tsl = _tslEdits.TryGetValue(positionId, out var tr) ? tr : pos?.TrailingStopPercent;
        await mediator.Send(new SetStopLossTakeProfitCommand(laneId, positionId, sl, tp, tsl));
        _slEdits.Remove(positionId);
        _tpEdits.Remove(positionId);
        _tslEdits.Remove(positionId);
        SetMsg($"SL/TP/Trailing aggiornati (SL={sl?.ToString("N2") ?? "—"}, TP={tp?.ToString("N2") ?? "—"}, Trailing={tsl?.ToString("F1") ?? "—"}%).", false);
        await RefreshAsync(laneId);
    }

    public async Task ConfirmAsync(int laneId, string orderId, string? userId)
    {
        await mediator.Send(new ConfirmOrderCommand(laneId, orderId, userId));
        SetMsg("Ordine confermato e inviato all'exchange.", false);
        await RefreshAsync(laneId);
    }

    public async Task RejectAsync(int laneId, string orderId, string? userId)
    {
        await mediator.Send(new RejectOrderCommand(laneId, orderId, userId));
        SetMsg("Ordine rifiutato.", false);
        await RefreshAsync(laneId);
    }

    // --- Configurazione di sicurezza (pannello Admin) ------------------------------------------

    public void ReloadSafety()
    {
        var c = safetyMonitor.CurrentValue;
        Safety = new SafetyConfiguration
        {
            MaxPositionSizePercent = c.MaxPositionSizePercent,
            MaxTotalExposurePercent = c.MaxTotalExposurePercent,
            MaxDailyLossPercent = c.MaxDailyLossPercent,
            MaxDrawdownPercent = c.MaxDrawdownPercent,
            MaxOpenPositions = c.MaxOpenPositions,
            MinOrderIntervalSeconds = c.MinOrderIntervalSeconds,
            RequireManualConfirmationForLive = c.RequireManualConfirmationForLive,
            MaxLeverageAllowed = c.MaxLeverageAllowed,
            MaintenanceMarginPercent = c.MaintenanceMarginPercent,
            PositionSizePercent = c.PositionSizePercent,
            UseExchangeRestingStops = c.UseExchangeRestingStops,
            FeePercent = c.FeePercent,
            // Dosaggio sulla volatilità: vanno copiati anche questi, altrimenti il pannello
            // mostrerebbe i default e il salvataggio successivo cancellerebbe la configurazione vera.
            VolatilityTargetingEnabled = c.VolatilityTargetingEnabled,
            TargetAnnualVolatilityPercent = c.TargetAnnualVolatilityPercent,
            VolatilityLookbackBars = c.VolatilityLookbackBars,
            MinExposureMultiplier = c.MinExposureMultiplier,
            MaxExposureMultiplier = c.MaxExposureMultiplier,
        };
    }

    public async Task SaveSafetyAsync()
    {
        if (Safety.MaxPositionSizePercent <= 0 || Safety.MaxTotalExposurePercent <= 0 ||
            Safety.MaxOpenPositions < 1 || Safety.MaxLeverageAllowed < 1 || Safety.FeePercent < 0)
        {
            SetMsg("Valori non validi: size/esposizione devono essere > 0, almeno 1 posizione, leva massima >= 1 e fee >= 0.", true);
            return;
        }
        try
        {
            await safetyWriter.SaveAsync(Safety);
            SetMsg("Configurazione di sicurezza salvata (attiva entro pochi secondi).", false);
        }
        catch (Exception ex)
        {
            SetMsg($"Salvataggio fallito: {ex.Message}", true);
        }
    }

    private void SetMsg(string text, bool error) { Message = text; IsError = error; }
}
