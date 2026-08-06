using Grpc.Core;
using Mediator;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Services.Trading.Commands;
using ProcioneMGR.Services.Trading.Queries;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Orchestrazione di <c>Components/Pages/Trading.razor</c> (P1-5, audit consolidamento
/// 2026-07-17): tutte le chiamate a <see cref="ITradingEngine"/>/<see cref="IPromotionEvaluator"/>/
/// <see cref="ILanePromoter"/>/<see cref="IEngineConfigStore"/> e lo stato che ne deriva, così la
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
    IEngineConfigStore engineConfig,
    ILaneQuarantineStore quarantineStore,
    IServiceProvider? serviceProvider = null,   // opzionale: ClearLaneAsync + LaneStory (manager keyed); i test storici non cambiano
    Microsoft.EntityFrameworkCore.IDbContextFactory<Data.ApplicationDbContext>? dbFactory = null)   // opzionale: candele del grafico + provenienza dal journal
{
    public TradingEngineStatus? Status { get; private set; }

    /// <summary>Quarantena attiva della corsia visualizzata (Fase 0-A3), null se la corsia è pulita.</summary>
    public LaneQuarantine? Quarantine { get; private set; }
    public List<OpenPosition> Positions { get; private set; } = [];
    public List<Order> Orders { get; private set; } = [];

    /// <summary>
    /// [2026-08-05] Falso (default): la tabella ordini mostra solo il TEST CORRENTE, come i KPI.
    /// Vero: tutta la vita della corsia, comprese le configurazioni precedenti su altri simboli —
    /// utile per un'indagine, fuorviante come vista di partenza.
    /// </summary>
    public bool ShowAllOrders { get; private set; }

    /// <summary>Alterna fra la finestra del test corrente e lo storico completo. Il chiamante ricarica.</summary>
    public void ToggleOrderHistory() => ShowAllOrders = !ShowAllOrders;

    /// <summary>
    /// [2026-08-06] Gli episodi della corsia: un tratto di vita per ogni avvio del motore, dal più
    /// recente. Popolato solo in modalità storico completo — sul test corrente c'è un episodio solo
    /// e raggrupparlo sarebbe cerimonia inutile.
    /// </summary>
    public IReadOnlyList<LaneEpisode> Episodes { get; private set; } = [];

    /// <summary>Gli ordini di un episodio, per la tabella raggruppata.</summary>
    public IReadOnlyList<Order> OrdersOf(LaneEpisode ep) =>
        [.. Orders.Where(o => o.CreatedAtUtc >= ep.StartedAtUtc
                              && (ep.EndedAtUtc is null || o.CreatedAtUtc < ep.EndedAtUtc))];

    /// <summary>
    /// I confini degli episodi vengono dagli avvii del motore già registrati in
    /// <c>TradingAuditLogs</c>. Come la carta d'identità della corsia: se questa lettura fallisce
    /// la pagina resta funzionante e la tabella torna piatta, perché è contesto e non controllo.
    /// </summary>
    private async Task<IReadOnlyList<LaneEpisode>> LoadEpisodesAsync(int laneId)
    {
        if (dbFactory is null) return [];
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var avvii = await db.TradingAuditLogs.AsNoTracking()
                .Where(a => a.LaneId == laneId && a.Action == "StartEngine")
                .OrderBy(a => a.TimestampUtc)
                .ToListAsync();
            return LaneEpisodeBuilder.Build(avvii, Orders);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadEpisodes corsia {laneId} fallito: {ex.Message}");
            return [];
        }
    }
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

    /// <summary>Copia di lavoro delle soglie di sicurezza (form Admin) — vedi <see cref="ReloadSafetyAsync"/>/<see cref="SaveSafetyAsync"/>.</summary>
    public SafetyConfiguration Safety { get; private set; } = new();

    /// <summary>
    /// Falso quando il motore remoto non ha risposto all'ultima lettura: i valori nel form sono i
    /// DEFAULT, non le soglie applicate — e il pannello lo deve dire invece di spacciarli per vere.
    /// </summary>
    public bool SafetyReachable { get; private set; } = true;

    /// <summary>Motivo dell'irraggiungibilità quando <see cref="SafetyReachable"/> è falso.</summary>
    public string? SafetyError { get; private set; }

    /// <summary>Sorgente prevalente della sezione ("appsettings.json", "variabili d'ambiente"…), per spiegare perché salvare può non bastare.</summary>
    public string? SafetySource { get; private set; }

    /// <summary>Vero se le soglie vivono in un altro processo (cambia solo cosa dire all'operatore).</summary>
    public bool SafetyIsRemote => engineConfig.IsRemote;

    /// <summary>La "carta d'identità" della corsia: cosa gira, con che aspettative, da dove viene.</summary>
    public sealed record LaneStoryStrategy(
        string DisplayName, decimal? ExpectedSharpe, decimal? ExpectedProfitFactor, decimal? ExpectedMaxDrawdown,
        decimal? StopLossPercent, decimal? TakeProfitPercent, decimal? TrailingStopPercent);

    public sealed record LaneStoryInfo(
        string Symbol, string Timeframe,
        IReadOnlyList<LaneStoryStrategy> Strategies,
        string? Provenance, string? ProvenanceSource, DateTime? ProvenanceAtUtc);

    public LaneStoryInfo? Story { get; private set; }

    /// <summary>Candele per il grafico prezzi+operazioni (ultime ~300 del simbolo/timeframe della corsia).</summary>
    public List<Data.OhlcvData> ChartCandles { get; private set; } = [];

    /// <summary>
    /// [2026-08-03, richiesta proprietario] Carica la storia della corsia (configurazione con le
    /// aspettative + provenienza dal journal della flotta — lo stesso testo delle proposte
    /// Telegram) e le candele per il grafico delle operazioni. Chiamata al cambio corsia e al
    /// refresh lento (~30s), NON a ogni battito da 2s: due query in più al battito sarebbero
    /// rumore per un dato che cambia di rado.
    /// </summary>
    public async Task LoadLaneStoryAsync(int laneId, CancellationToken ct = default)
    {
        try
        {
            // La configurazione con le aspettative (ExpectedSharpe/PF/DD arrivano dall'holdout
            // del candidato: sono la "promessa" contro cui il forward test misura la realtà).
            if (serviceProvider is not null)
            {
                var manager = Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions
                    .GetRequiredKeyedService<Ensemble.IEnsembleManager>(serviceProvider, laneId);
                var cfg = await manager.GetConfigurationAsync(ct);
                var strategies = cfg.Strategies
                    .Where(s => s.IsActive)
                    .Select(s => new LaneStoryStrategy(
                        string.IsNullOrWhiteSpace(s.DisplayName) ? s.StrategyName : s.DisplayName,
                        s.ExpectedSharpe, s.ExpectedProfitFactor, s.ExpectedMaxDrawdown,
                        s.StopLossPercent, s.TakeProfitPercent, s.TrailingStopPercent))
                    .ToList();

                string? provenance = null;
                string? provenanceSource = null;
                DateTime? provenanceAt = null;
                if (dbFactory is not null)
                {
                    // La provenienza: l'ultima assegnazione della flotta per questa corsia — lo
                    // STESSO testo della proposta/schieramento arrivato su Telegram.
                    await using var db = await dbFactory.CreateDbContextAsync(ct);
                    var assign = await db.OrchestratorDecisions.AsNoTracking()
                        .Where(d => d.LaneId == laneId && d.Kind == "Assign")
                        .OrderByDescending(d => d.AtUtc)
                        .FirstOrDefaultAsync(ct);
                    provenance = assign?.Reason;
                    provenanceSource = assign?.Source;
                    provenanceAt = assign?.AtUtc;
                }

                Story = string.IsNullOrEmpty(cfg.Symbol)
                    ? null
                    : new LaneStoryInfo(cfg.Symbol, cfg.Timeframe, strategies, provenance, provenanceSource, provenanceAt);
            }

            // Le candele del grafico: ultime ~300 del simbolo/timeframe correnti.
            if (dbFactory is not null && Story is not null)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var recent = await db.OhlcvData.AsNoTracking()
                    .Where(c => c.Symbol == Story.Symbol && c.Timeframe == Story.Timeframe)
                    .OrderByDescending(c => c.TimestampUtc)
                    .Take(300)
                    .ToListAsync(ct);
                recent.Reverse();
                ChartCandles = recent;
            }
            else
            {
                ChartCandles = [];
            }
        }
        catch (Exception ex)
        {
            // La carta d'identità è contesto, non controllo: un guasto qui non deve rompere la pagina.
            Story = null;
            ChartCandles = [];
            System.Diagnostics.Debug.WriteLine($"LoadLaneStory fallito: {ex.Message}");
        }
    }

    /// <summary>
    /// [2026-08-03] Svuota la configurazione di una corsia FERMA (mai la rimuove: l'id è identità —
    /// posizioni e storico vi restano agganciati, ed è la lezione delle posizioni orfane di luglio).
    /// La corsia torna "non configurata", quindi libera per la flotta; lo storico resta a database.
    /// </summary>
    public async Task ClearLaneAsync(int laneId, CancellationToken ct = default)
    {
        try
        {
            var status = await mediator.Send(new GetLaneStatusQuery(laneId), ct);
            if (status.IsRunning)
            {
                Message = $"La corsia {laneId} sta girando: fermala prima di svuotarla.";
                IsError = true;
                return;
            }
            if (serviceProvider is null)
            {
                Message = "Svuotamento non disponibile in questo assetto (nessun contenitore corsie).";
                IsError = true;
                return;
            }

            var manager = Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions
                .GetRequiredKeyedService<Ensemble.IEnsembleManager>(serviceProvider, laneId);
            var cfg = await manager.GetConfigurationAsync(ct);
            cfg.Symbol = string.Empty;
            cfg.Timeframe = string.Empty;
            cfg.Strategies = [];
            await manager.UpdateConfigurationAsync(cfg, ct);

            Message = $"Corsia {laneId} svuotata: configurazione azzerata, storico conservato — libera per la flotta.";
            IsError = false;
        }
        catch (Exception ex)
        {
            Message = $"Svuotamento corsia {laneId} fallito: {ex.Message}";
            IsError = true;
        }
    }

    // Valori SL/TP/Trailing in modifica: sopravvivono al refresh automatico finché non salvati.
    private readonly Dictionary<string, decimal?> _slEdits = new();
    private readonly Dictionary<string, decimal?> _tpEdits = new();
    private readonly Dictionary<string, decimal?> _tslEdits = new();

    /// <summary>
    /// [2026-08-03] Perf del TEST CORRENTE (da StartedAtUtc): la stessa base della decisione di
    /// promozione. I KPI della pagina la usano al posto dei totali di Status, che sommano tutte le
    /// vite precedenti della corsia (corsia 0 mostrava «159 trade» dei run di luglio accanto a
    /// un'equity vuota: tre finestre diverse nella stessa pagina, trovate dal proprietario).
    /// </summary>
    public TradingPerformance? Perf { get; private set; }

    public async Task RefreshAsync(int laneId)
    {
        try
        {
            // Lo STATUS per primo (serve StartedAtUtc per la finestra di perf); le altre quattro
            // letture in parallelo — in modalità remota sono round-trip gRPC, e sommarne le
            // latenze ogni 2 secondi era solo attesa gratuita. Il motore regge le chiamate
            // concorrenti per costruzione (il TradingWorker gli parla in parallelo da sempre).
            // Tutte le letture e i comandi passano da IMediator (Fase 1).
            Status = await mediator.Send(new GetLaneStatusQuery(laneId));

            var positionsTask = mediator.Send(new GetOpenPositionsQuery(laneId)).AsTask();
            // [2026-08-03] UNA finestra per tutta la pagina: dal TEST CORRENTE (StartedAtUtc),
            // stessa base della tabella promozioni. Fallback 90gg per una corsia mai avviata.
            var perfFrom = Status?.StartedAtUtc ?? DateTime.UtcNow.AddDays(-90);

            // [2026-08-05] Gli ordini seguono la STESSA finestra dei KPI. Prima la query partiva
            // senza `from` — pur essendo il parametro già previsto — e la tabella mostrava tutta
            // la vita della corsia: ordini di mesi prima, su SIMBOLI DIVERSI, indistinguibili dai
            // presenti perché la colonna del simbolo non c'era. Segnalato dal proprietario:
            // «a quali operazioni si riferiscono?». Domanda a cui la tabella non sapeva rispondere.
            // Con ShowAllOrders si torna allo storico intero, ma è una scelta esplicita.
            var ordersFrom = ShowAllOrders ? (DateTime?)null : perfFrom;
            var ordersTask = mediator.Send(new GetOrderHistoryQuery(laneId, ordersFrom)).AsTask();
            var pendingTask = mediator.Send(new GetPendingOrdersQuery(laneId)).AsTask();
            var perfTask = mediator.Send(new GetPerformanceQuery(laneId, perfFrom)).AsTask();
            await Task.WhenAll(positionsTask, ordersTask, pendingTask, perfTask);
            Positions = positionsTask.Result;
            Orders = ordersTask.Result;
            Pending = pendingTask.Result;

            // [2026-08-06] Gli episodi: solo quando si guarda tutta la storia, che è il caso in cui
            // servono. I confini vengono dagli avvii del motore già nel registro di audit — nessuna
            // tabella nuova, nessuna migrazione: l'informazione c'era, mancava chi la leggesse.
            Episodes = ShowAllOrders ? await LoadEpisodesAsync(laneId) : [];
            var perf = perfTask.Result;
            Perf = perf;
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
        // L'esito PRECEDENTE va azzerato prima di riprovare, altrimenti un errore vecchio resta a
        // schermo anche dopo un tentativo riuscito. Visto dal vivo il 2026-07-27: caduto il
        // port-forward verso il core in-cluster la valutazione falliva; ristabilito il tunnel tutte
        // le altre query tornavano a completare, ma il banner rosso delle promozioni restava lì,
        // facendo credere a un guasto ancora in corso. Un messaggio d'errore che sopravvive alla
        // propria causa è peggio di nessun messaggio.
        PromoMessage = null;
        PromoIsError = false;
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
    //
    // [E1, 2026-07-31] Fino a oggi questo pannello leggeva IOptionsMonitor del GUSCIO e scriveva il
    // file del GUSCIO («attiva entro pochi secondi») — ma la SafetyChecker che applica le soglie
    // ordine per ordine vive nell'host del MOTORE, che col trading remoto è un altro processo: la
    // manopola non muoveva nulla. Stessa forma del difetto n. 5 dell'audit backend↔frontend. Ora si
    // passa da IEngineConfigStore: si legge da chi esegue, si scrive su chi esegue, e la risposta è
    // la sezione RILETTA dal motore — non serve fidarsi.

    public async Task ReloadSafetyAsync(CancellationToken ct = default)
    {
        var snapshot = await engineConfig.ReadAsync(["Trading:Safety"], ct);
        SafetyReachable = snapshot.Reachable;
        SafetyError = snapshot.Error;
        SafetySource = snapshot.SourceOf("Trading:Safety");
        // Con motore irraggiungibile Bind restituisce i DEFAULT: il form resta usabile ma
        // SafetyReachable=false dice al pannello di non spacciarli per le soglie applicate.
        Safety = snapshot.Bind<SafetyConfiguration>("Trading:Safety");
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
            var result = await engineConfig.WriteAsync("Trading:Safety", Safety);

            // La verità del motore, non l'eco del form: ciò che mostra il pannello da qui in poi
            // è la sezione come il motore l'ha riletta dopo la scrittura.
            try
            {
                Safety = System.Text.Json.JsonSerializer.Deserialize<SafetyConfiguration>(
                    result.AppliedJson, EngineConfigSnapshot.JsonOptions) ?? Safety;
            }
            catch (System.Text.Json.JsonException) { /* si tiene la copia di lavoro */ }

            var dove = engineConfig.IsRemote ? "sul MOTORE (riletta dal processo che la applica)" : "in appsettings.json";
            SetMsg(result.Warning is { } warning
                ? $"Soglie di sicurezza salvate {dove}, MA — {warning}"
                : $"Soglie di sicurezza salvate {dove}: valgono dal prossimo ordine valutato.", false);
        }
        catch (Exception ex)
        {
            SetMsg($"Salvataggio fallito: {ex.Message}", true);
        }
    }

    private void SetMsg(string text, bool error) { Message = text; IsError = error; }
}
