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
    Microsoft.EntityFrameworkCore.IDbContextFactory<Data.ApplicationDbContext>? dbFactory = null,   // opzionale: candele del grafico + provenienza dal journal
    ILogger<TradingPageService>? logger = null,   // opzionale: i test storici non lo passano
    // [I11] Opzionale: serve SOLO a leggere la soglia di trade che la regola di ritiro pretende, per
    // poter dire il tempo-al-verdetto col numero VERO invece che con un 20 ricopiato a mano accanto
    // alla manopola che lo definisce. Assente ⇒ si usa il predefinito del POCO, che è lo stesso.
    Microsoft.Extensions.Options.IOptionsMonitor<Fleet.FleetOptions>? fleetOptions = null)
{
    private int RetireMinTrades => Math.Max(1, fleetOptions?.CurrentValue.RetireMinTrades ?? new Fleet.FleetOptions().RetireMinTrades);

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
    /// [2026-08-06] Protezioni risultate toccate da barre CHIUSE con la posizione ancora aperta.
    /// Vuoto è il caso normale. Vedi <see cref="ProtectiveExitAudit"/> per il perché esiste.
    /// </summary>
    public IReadOnlyList<ProtectiveExitAnomaly> ExitAnomalies { get; private set; } = [];

    /// <summary>
    /// Confronta le posizioni aperte con le barre già chiuse del loro simbolo.
    ///
    /// <para>Il filtro sulle barre chiuse non è prudenza: passare qui la candela in formazione
    /// rimetterebbe nel controllo lo stesso difetto che il controllo deve scoprire — e per un
    /// attimo direbbe «target toccato» su un massimo parziale che poi rientra.</para>
    /// </summary>
    /// <summary>
    /// [2026-08-17] Perché il controllo non è stato eseguito, quando non lo è stato. Un elenco vuoto
    /// di anomalie è ESATTAMENTE il valore del caso «tutto a posto»: senza questo campo «controllo
    /// superato» e «controllo fallito» erano indistinguibili a schermo, e l'unica traccia era una
    /// <c>Debug.WriteLine</c> che il compilatore rimuove in Release, cioè nella configurazione con
    /// cui l'app gira davvero.
    /// </summary>
    public string? ExitAnomaliesError { get; private set; }

    private async Task<IReadOnlyList<ProtectiveExitAnomaly>> LoadExitAnomaliesAsync(
        IReadOnlyList<OpenPosition> posizioni, string? timeframeCorsia)
    {
        ExitAnomaliesError = null;
        if (dbFactory is null || posizioni.Count == 0) return [];
        try
        {
            var simboli = posizioni.Select(p => p.Symbol).Distinct().ToList();
            var timeframe = timeframeCorsia;
            if (string.IsNullOrWhiteSpace(timeframe))
            {
                ExitAnomaliesError = "timeframe della corsia sconosciuto";
                return [];
            }

            var ultimaChiusa = Ingestion.SeriesFreshness.LastClosedBarOpenUtc(timeframe, DateTime.UtcNow);
            if (ultimaChiusa is not DateTime chiusaFinoA)
            {
                ExitAnomaliesError = $"timeframe \"{timeframe}\" non riconosciuto";
                return [];
            }

            var daQuando = posizioni.Min(p => p.OpenedAtUtc);

            await using var db = await dbFactory.CreateDbContextAsync();
            var barre = await db.OhlcvData.AsNoTracking()
                .Where(c => simboli.Contains(c.Symbol) && c.Timeframe == timeframe
                            && c.TimestampUtc >= daQuando && c.TimestampUtc <= chiusaFinoA)
                .ToListAsync();

            return ProtectiveExitAudit.Find(posizioni, barre);
        }
        catch (Exception ex)
        {
            // Il guasto va DETTO: un elenco vuoto qui significa «nessuna anomalia», e restituirlo
            // dopo un errore trasformerebbe un controllo non eseguito in un controllo superato.
            ExitAnomaliesError = ex.Message;
            logger?.LogWarning(ex, "Controllo delle uscite protettive fallito.");
            return [];
        }
    }

    /// <summary>
    /// I confini degli episodi vengono dagli avvii del motore già registrati in
    /// <c>TradingAuditLogs</c>. Come la carta d'identità della corsia: se questa lettura fallisce
    /// la pagina resta funzionante e la tabella torna piatta, perché è contesto e non controllo.
    /// </summary>
    private async Task<IReadOnlyList<LaneEpisode>> LoadEpisodesAsync(int laneId, IReadOnlyList<Order> ordini)
    {
        if (dbFactory is null) return [];
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var avvii = await db.TradingAuditLogs.AsNoTracking()
                .Where(a => a.LaneId == laneId && a.Action == "StartEngine")
                .OrderBy(a => a.TimestampUtc)
                .ToListAsync();
            return LaneEpisodeBuilder.Build(avvii, ordini);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Caricamento degli episodi della corsia {Lane} fallito.", laneId);
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
        decimal? StopLossPercent, decimal? TakeProfitPercent, decimal? TrailingStopPercent,
        // [I11] La frequenza attesa, la sua provenienza, e la frase gia' composta col tempo-al-verdetto.
        //
        // Il TESTO viaggia gia' pronto invece del solo numero perche' comporlo richiede la soglia di
        // ritiro (FleetOptions.RetireMinTrades), che e' configurabile: se lo componesse il markup
        // dovrebbe conoscere quella manopola, e prima o poi ne ricopierebbe il valore — due regole
        // per la stessa domanda, il difetto che questo item esiste per NON ripetere.
        decimal? ExpectedTradesPerMonth = null, string? ExpectedTradesSource = null,
        string? TradeFrequencyText = null);

    public sealed record LaneStoryInfo(
        string Symbol, string Timeframe,
        IReadOnlyList<LaneStoryStrategy> Strategies,
        string? Provenance, string? ProvenanceSource, DateTime? ProvenanceAtUtc);

    public LaneStoryInfo? Story { get; private set; }

    /// <summary>
    /// [2026-08-17] Il profilo di rischio CONFIGURATO sulla corsia visualizzata (null = nessuno).
    ///
    /// Serve al pannello di sicurezza per non mentire: quel pannello legge e scrive la sezione
    /// GLOBALE <c>Trading:Safety</c>, ma se la corsia ha un profilo (la Modalità Semplice di /bot
    /// ne assegna sempre uno) le soglie che il motore applica davvero sono
    /// <c>profilo.Apply(globale)</c>, e il profilo SOVRASCRIVE otto degli undici campi mostrati.
    /// Senza dirlo, un Admin che stringe il drawdown dal 20% al 15% crede di aver stretto una
    /// corsia che sta girando al 10% — o, nel verso pericoloso, crede di aver alzato una leva
    /// massima che il profilo tiene a 1.
    /// </summary>
    public string? LaneRiskProfileName { get; private set; }

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
                        s.StopLossPercent, s.TakeProfitPercent, s.TrailingStopPercent,
                        s.ExpectedTradesPerMonth, s.ExpectedTradesSource,
                        Fleet.TradeFrequency.Describe(s.ExpectedTradesPerMonth, RetireMinTrades)))
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

                // Il profilo si legge dalla stessa configurazione, senza query aggiuntive.
                LaneRiskProfileName = Risk.RiskProfiles.Find(cfg.RiskProfileName)?.DisplayName;
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
            LaneRiskProfileName = null;
            logger?.LogWarning(ex, "Caricamento della carta d'identità della corsia {Lane} fallito.", laneId);
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

    /// <summary>
    /// [2026-08-17] La corsia a cui appartengono i dati attualmente esposti — un TAG DI PROVENIENZA,
    /// non un parametro operativo: i metodi continuano a ricevere <c>laneId</c> esplicito.
    ///
    /// Serve perché lo stato pubblicato non portava con sé la propria identità: se il refresh
    /// scatenato da un cambio corsia falliva, KPI, posizioni, ordini ed equity restavano quelli
    /// della corsia PRECEDENTE mentre l'intestazione, il grafico e la quarantena erano già della
    /// nuova. La pagina mostrava una corsia sola composta da due, e i pulsanti di riga mandavano
    /// al motore della corsia B il <c>positionId</c> della corsia A.
    /// </summary>
    public int? LoadedLaneId { get; private set; }

    /// <summary>
    /// Generazione del refresh: il tick da 2s e il click dell'operatore girano sullo stesso
    /// dispatcher ma si interlacciano a ogni <c>await</c>, quindi una risposta lenta per la corsia
    /// vecchia può atterrare DOPO quella per la nuova. Il commit finale è guardato da questo
    /// contatore: una risposta sorpassata viene scartata intera, mai a metà.
    /// </summary>
    private int _refreshToken;

    /// <summary>
    /// Vero quando lo stantio nasce da un guasto di TRASPORTO (il motore remoto non risponde),
    /// falso quando la lettura è fallita qui (DB, bug locale). Cambia solo cosa dire all'operatore:
    /// accusare il gRPC quando il gRPC non c'entra è una diagnosi sbagliata, e la topologia
    /// in-process non produce mai RpcException.
    /// </summary>
    public bool StaleIsTransport { get; private set; }

    private void ResetLaneScopedState()
    {
        Status = null;
        Perf = null;
        Quarantine = null;
        Positions = [];
        Orders = [];
        Pending = [];
        Equity = [];
        Episodes = [];
        ExitAnomalies = [];
        ExitAnomaliesError = null;
        StaleSince = null;
        LastStaleReason = null;
        StaleIsTransport = false;
        // NB: Message/IsError NON si azzerano qui. RefreshAsync viene chiamata anche in coda a ogni
        // comando (Start, Stop, Emergency…), e azzerare cancellerebbe l'esito appena comunicato.
        // L'esito appartiene comunque alla corsia su cui è nato: è la pagina a chiamare
        // ClearMessage() quando l'operatore cambia scheda. Vedi Trading.razor/OnLaneSelectedAsync.
        _slEdits.Clear();
        _tpEdits.Clear();
        _tslEdits.Clear();
        _slInvalid.Clear();
        _tpInvalid.Clear();
        _tslInvalid.Clear();
    }

    public async Task RefreshAsync(int laneId)
    {
        var token = ++_refreshToken;

        // Cambio corsia: quello che c'è in pancia appartiene a un'ALTRA corsia. Se la lettura
        // fallisce, «nessun dato per questa corsia» è l'unica cosa vera che si possa mostrare —
        // tenere i numeri della precedente sotto l'etichetta della nuova non è degradare, è
        // mentire. A corsia INVARIATA invece i dati restano (vedi il catch in fondo): svuotare la
        // pagina durante un riavvio di pochi secondi sarebbe peggio che dichiararla vecchia.
        if (LoadedLaneId != laneId)
        {
            ResetLaneScopedState();
            LoadedLaneId = laneId;
        }

        try
        {
            // Lo STATUS per primo (serve StartedAtUtc per la finestra di perf); le altre quattro
            // letture in parallelo — in modalità remota sono round-trip gRPC, e sommarne le
            // latenze ogni 2 secondi era solo attesa gratuita. Il motore regge le chiamate
            // concorrenti per costruzione (il TradingWorker gli parla in parallelo da sempre).
            // Tutte le letture e i comandi passano da IMediator (Fase 1).
            //
            // [2026-08-17] Si legge in LOCALI e si pubblica in blocco alla fine: prima `Status`
            // veniva assegnato subito e un fallimento delle altre quattro letture lasciava a
            // schermo intestazione e battito freschi accanto a posizioni, ordini, PnL ed equity
            // del giro precedente, senza una parola.
            var status = await mediator.Send(new GetLaneStatusQuery(laneId));

            var positionsTask = mediator.Send(new GetOpenPositionsQuery(laneId)).AsTask();
            // [2026-08-03] UNA finestra per tutta la pagina: dal TEST CORRENTE (StartedAtUtc),
            // stessa base della tabella promozioni. Fallback 90gg per una corsia mai avviata.
            var perfFrom = status?.StartedAtUtc ?? DateTime.UtcNow.AddDays(-90);

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
            var positions = positionsTask.Result;
            var orders = ordersTask.Result;
            var pending = pendingTask.Result;
            var perf = perfTask.Result;

            // [2026-08-06] Gli episodi: solo quando si guarda tutta la storia, che è il caso in cui
            // servono. I confini vengono dagli avvii del motore già nel registro di audit — nessuna
            // tabella nuova, nessuna migrazione: l'informazione c'era, mancava chi la leggesse.
            var episodes = ShowAllOrders ? await LoadEpisodesAsync(laneId, orders) : [];

            // [2026-08-06] Protezioni toccate ma non eseguite. Va calcolato a OGNI refresh, non
            // dietro un pulsante: è il controllo che il proprietario ha dovuto fare a occhio.
            var anomalies = await LoadExitAnomaliesAsync(positions, status?.Timeframe);

            var equity = perf.EquityCurve.Count > 0
                ?
                [
                    new Indicators.IndicatorSeries
                    {
                        Title = "Equity", Color = "#2962FF", Type = Indicators.IndicatorSeriesType.Line,
                        Points = perf.EquityCurve.Select(p => new Indicators.IndicatorPoint(
                            new DateTimeOffset(DateTime.SpecifyKind(p.Timestamp, DateTimeKind.Utc)).ToUnixTimeSeconds(), (double)p.Capital)).ToList(),
                    },
                ]
                : new List<Indicators.IndicatorSeries>();

            // COMMIT ATOMICO. Se nel frattempo è partito un altro refresh (cambio corsia, o
            // semplicemente il tick successivo) questa risposta è sorpassata: si scarta INTERA,
            // perché pubblicarne metà rimetterebbe in scena le «tre finestre diverse nella stessa
            // pagina» già corrette in passato.
            if (token != _refreshToken) return;

            Status = status;
            Positions = positions;
            Orders = orders;
            Pending = pending;
            Perf = perf;
            Episodes = episodes;
            ExitAnomalies = anomalies;
            Equity = equity;

            // Giro riuscito: quello a schermo è di nuovo lo stato reale.
            StaleSince = null;
            LastStaleReason = null;
            StaleIsTransport = false;
        }
        catch (RpcException ex)
        {
            // QUALUNQUE fallimento gRPC, non solo Unavailable: che il servizio sia irraggiungibile
            // (rolling restart), lento (DeadlineExceeded) o rotto (Internal), il risultato per chi
            // guarda è identico — i numeri a schermo sono quelli dell'ultimo giro riuscito. I dati
            // restano (svuotare la pagina durante un riavvio di pochi secondi sarebbe peggio) ma
            // vanno dichiarati vecchi. Il primo fallimento fissa l'istante, così il banner mostra da
            // quanto dura.
            //
            // [2026-08-17] La guardia qui NON è sul token ma sulla CORSIA, ed è una differenza che
            // si vede solo dall'app vera: il polling è ogni 2s e la deadline di lettura è 10s,
            // quindi quando il motore è lento ogni giro viene superato da quelli successivi. Con
            // `token != _refreshToken` nessun fallimento arrivava mai a schermo — la pagina restava
            // muta e vuota proprio nel caso che il banner esiste per raccontare. Il token serve a
            // non far sovrascrivere dati freschi da dati vecchi; un FALLIMENTO invece non è un dato
            // da pubblicare in ordine, è una notizia sulla corsia, e vale finché la corsia è quella.
            if (LoadedLaneId != laneId) return;
            StaleSince ??= DateTime.UtcNow;
            LastStaleReason = ex.StatusCode.ToString();
            StaleIsTransport = true;
        }
        catch (Exception ex)
        {
            // [2026-08-17] Prima qui c'era un `catch { }` nudo, e con esso un buco nella regola 5
            // («degradare dicendolo»): in topologia in-process NESSUN guasto è una RpcException,
            // quindi il banner di staleness era irraggiungibile e un Postgres giù lasciava a
            // schermo equity, PnL e posizioni dell'ultimo giro riuscito spacciandoli per attuali.
            // I dati restano — svuotare sarebbe peggio — ma ora vengono dichiarati vecchi, e
            // StaleIsTransport=false evita di accusare il gRPC quando il gRPC non c'entra.
            // Guardia sulla CORSIA e non sul token: vedi il ramo RpcException qui sopra.
            if (LoadedLaneId != laneId) return;
            StaleSince ??= DateTime.UtcNow;
            LastStaleReason = ex.GetType().Name;
            StaleIsTransport = false;
            logger?.LogWarning(ex, "Refresh della corsia {Lane} fallito: i dati a schermo sono dichiarati vecchi.", laneId);
        }

        // Fuori dal blocco gRPC: la quarantena vive nel DB condiviso, si legge anche col servizio
        // di trading giù (anzi, È il momento in cui l'operatore deve poterla vedere).
        try
        {
            var quarantena = await quarantineStore.GetAsync(laneId);
            if (token == _refreshToken) Quarantine = quarantena;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Lettura della quarantena della corsia {Lane} fallita.", laneId);
        }
    }

    /// <summary>Rimozione della quarantena (solo Admin, dopo verifica): audit con lo userId di chi decide.</summary>
    public async Task ClearQuarantineAsync(int laneId, string? userId)
    {
        try
        {
            var removed = await quarantineStore.ClearAsync(laneId, userId);
            SetMsg(removed
                ? $"Quarantena della corsia {laneId} rimossa. La corsia può essere riavviata."
                : "Nessuna quarantena attiva da rimuovere.", false);
        }
        catch (Exception ex)
        {
            SetMsg($"Rimozione della quarantena fallita: {ex.Message}. La corsia resta bloccata.", true);
        }
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

    // [2026-08-17] Tutti i verbi che seguono hanno la stessa forma di StartAsync: try/catch su
    // Exception (non solo RpcException — ClearQuarantine va su Postgres, Confirm/Reject toccano il
    // DB anche in-process) e messaggio all'operatore. Prima ne erano protetti solo due: in modalità
    // remota un Unavailable durante il riavvio del motore risaliva fino al gestore @onclick di
    // Blazor e ABBATTEVA IL CIRCUITO — la pagina moriva proprio mentre si premeva il pulsante
    // rosso, e il banner giallo intanto prometteva «i comandi falliranno», cioè un fallimento
    // gestito. Il RefreshAsync finale resta anche sul cammino d'errore: è già resiliente per conto
    // suo, e all'operatore serve vedere lo stato reale dopo il tentativo, non una pagina congelata.

    public async Task StopAsync(int laneId)
    {
        try
        {
            await mediator.Send(new StopLaneCommand(laneId));
            SetMsg("Trading fermato (posizioni lasciate aperte).", false);
        }
        catch (Exception ex)
        {
            SetMsg($"Arresto NON riuscito: {ex.Message}. La corsia può essere ancora in esecuzione.", true);
        }
        await RefreshAsync(laneId);
    }

    public async Task EmergencyAsync(int laneId)
    {
        try
        {
            await mediator.Send(new EmergencyStopCommand(laneId, "Stop manuale dall'operatore"));
            // Non si annuncia «tutte le posizioni chiuse»: la chiusura di massa è best-effort per
            // contratto (una singola chiusura può non riuscire e la posizione resta), quindi la
            // frase sarebbe falsa anche quando la chiamata riesce. Si dice cosa guardare.
            SetMsg($"EMERGENCY STOP inviato alla corsia {laneId}: verifica qui sotto che non restino posizioni aperte.", false);
        }
        catch (Exception ex)
        {
            SetMsg($"EMERGENCY STOP NON eseguito: {ex.Message}. Le posizioni possono essere ancora aperte — riprova o interviene sull'exchange.", true);
        }
        await RefreshAsync(laneId);
    }

    public async Task CloseAsync(int laneId, string positionId)
    {
        try
        {
            await mediator.Send(new ClosePositionCommand(laneId, positionId));
        }
        catch (Exception ex)
        {
            SetMsg($"Chiusura della posizione fallita: {ex.Message}. La posizione può essere ancora aperta.", true);
        }
        await RefreshAsync(laneId);
    }

    // --- Edit in corso di SL/TP/Trailing (form posizioni aperte) ------------------------------

    public string? SlValue(OpenPosition p) =>
        (_slEdits.TryGetValue(p.PositionId, out var v) ? v : p.StopLoss)?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string? TpValue(OpenPosition p) =>
        (_tpEdits.TryGetValue(p.PositionId, out var v) ? v : p.TakeProfit)?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string? TslValue(OpenPosition p) =>
        (_tslEdits.TryGetValue(p.PositionId, out var v) ? v : p.TrailingStopPercent)?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Le voci con un valore NON valido digitato dall'operatore: campo → sì/no.</summary>
    private readonly HashSet<string> _slInvalid = [];
    private readonly HashSet<string> _tpInvalid = [];
    private readonly HashSet<string> _tslInvalid = [];

    public void SetSlEdit(string id, string? raw) => Track(_slEdits, _slInvalid, id, raw);
    public void SetTpEdit(string id, string? raw) => Track(_tpEdits, _tpInvalid, id, raw);
    public void SetTslEdit(string id, string? raw) => Track(_tslEdits, _tslInvalid, id, raw);

    private static void Track(Dictionary<string, decimal?> edits, HashSet<string> invalid, string id, string? raw)
    {
        if (TryParseLevel(raw, out var level))
        {
            edits[id] = level;
            invalid.Remove(id);
        }
        else
        {
            // Non si scrive null: null significa «togli la protezione», e un errore di battitura
            // non è una richiesta di disarmare lo stop. L'edit resta invalido finché non è corretto.
            invalid.Add(id);
        }
    }

    /// <summary>
    /// [2026-08-17] Distingue i TRE casi che prima collassavano tutti in <c>null</c>: campo svuotato
    /// di proposito (rimozione voluta), valore valido, e input NON valido (non parsabile o ≤ 0).
    ///
    /// Il terzo era il pericoloso: <c>SetSlEdit</c> scriveva comunque la chiave, quindi il null
    /// vinceva sul valore esistente e <c>SaveSlTpAsync</c> lo mandava al motore, che lo interpreta
    /// come AZZERAMENTO — un «-59800» digitato per sbaglio RIMUOVEVA lo stop loss dalla posizione,
    /// con un messaggio verde «SL/TP/Trailing aggiornati» a confermare l'operazione.
    /// </summary>
    /// <returns>Falso se l'input non è utilizzabile; vero con <paramref name="level"/> null se il campo è vuoto.</returns>
    public static bool TryParseLevel(string? raw, out decimal? level)
    {
        level = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;   // campo svuotato = rimuovi la protezione
        if (decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0m)
        {
            level = d;
            return true;
        }
        return false;
    }

    /// <summary>Compatibilità con i chiamanti storici: <c>null</c> sia per «vuoto» sia per «non valido».</summary>
    public static decimal? ParseLevel(string? raw) => TryParseLevel(raw, out var level) ? level : null;

    public async Task SaveSlTpAsync(int laneId, string positionId)
    {
        // Fail-closed sull'input: meglio non fare nulla che disarmare una protezione per un refuso.
        var campiInvalidi = new List<string>();
        if (_slInvalid.Contains(positionId)) campiInvalidi.Add("stop loss");
        if (_tpInvalid.Contains(positionId)) campiInvalidi.Add("take profit");
        if (_tslInvalid.Contains(positionId)) campiInvalidi.Add("trailing");
        if (campiInvalidi.Count > 0)
        {
            SetMsg($"Valore non valido in {string.Join(" e ", campiInvalidi)}: usa un numero positivo, "
                   + "oppure svuota il campo se vuoi RIMUOVERE la protezione. Nessuna modifica inviata.", true);
            return;
        }

        var pos = Positions.FirstOrDefault(p => p.PositionId == positionId);
        var sl = _slEdits.TryGetValue(positionId, out var s) ? s : pos?.StopLoss;
        var tp = _tpEdits.TryGetValue(positionId, out var t) ? t : pos?.TakeProfit;
        var tsl = _tslEdits.TryGetValue(positionId, out var tr) ? tr : pos?.TrailingStopPercent;

        // Coerenza rispetto al lato: uno stop dalla parte sbagliata del prezzo di ingresso non è
        // una protezione, è un'uscita immediata in perdita. Si avverte e si rifiuta.
        // Solo con un prezzo d'ingresso noto: senza, «dalla parte sbagliata» non è giudicabile e
        // il controllo rifiuterebbe livelli perfettamente validi.
        if (pos is not null && pos.EntryPrice > 0m)
        {
            var isLong = pos.Side == OrderSide.Buy;
            if (sl is decimal slv && ((isLong && slv >= pos.EntryPrice) || (!isLong && slv <= pos.EntryPrice)))
            {
                SetMsg($"Stop loss {slv} dalla parte sbagliata per una posizione {(isLong ? "long" : "short")} "
                       + $"aperta a {pos.EntryPrice}: verrebbe colpito subito. Nessuna modifica inviata.", true);
                return;
            }
            if (tp is decimal tpv && ((isLong && tpv <= pos.EntryPrice) || (!isLong && tpv >= pos.EntryPrice)))
            {
                SetMsg($"Take profit {tpv} dalla parte sbagliata per una posizione {(isLong ? "long" : "short")} "
                       + $"aperta a {pos.EntryPrice}: chiuderebbe subito in perdita. Nessuna modifica inviata.", true);
                return;
            }
        }

        try
        {
            await mediator.Send(new SetStopLossTakeProfitCommand(laneId, positionId, sl, tp, tsl));
        }
        catch (Exception ex)
        {
            SetMsg($"Aggiornamento di SL/TP/Trailing fallito: {ex.Message}. I livelli precedenti restano in vigore.", true);
            await RefreshAsync(laneId);
            return;
        }

        _slEdits.Remove(positionId);
        _tpEdits.Remove(positionId);
        _tslEdits.Remove(positionId);

        // Una RIMOZIONE non è un aggiornamento come un altro: la si dichiara, e in giallo.
        var rimosse = new List<string>();
        if (sl is null) rimosse.Add("stop loss");
        if (tp is null) rimosse.Add("take profit");
        SetMsg(rimosse.Count > 0
            ? $"Protezione RIMOSSA ({string.Join(" e ", rimosse)}) — la posizione resta esposta. "
              + $"SL={sl?.ToString("N2") ?? "—"}, TP={tp?.ToString("N2") ?? "—"}, Trailing={tsl?.ToString("F1") ?? "—"}%."
            : $"SL/TP/Trailing aggiornati (SL={sl?.ToString("N2") ?? "—"}, TP={tp?.ToString("N2") ?? "—"}, Trailing={tsl?.ToString("F1") ?? "—"}%).",
            rimosse.Count > 0);
        await RefreshAsync(laneId);
    }

    public async Task ConfirmAsync(int laneId, string orderId, string? userId)
    {
        try
        {
            await mediator.Send(new ConfirmOrderCommand(laneId, orderId, userId));
        }
        catch (Exception ex)
        {
            SetMsg($"Conferma dell'ordine fallita: {ex.Message}. L'ordine NON è stato inviato all'exchange.", true);
            await RefreshAsync(laneId);
            return;
        }

        await RefreshAsync(laneId);

        // [2026-08-17] L'esito VERO, non l'eco del gesto. Il comando può riuscire e l'ordine
        // finire comunque Rejected — per esempio bocciato dal safety check al momento
        // dell'esecuzione — mentre prima il pannello annunciava sempre «confermato e inviato
        // all'exchange», contraddicendo la riga che nella tabella accanto diceva il contrario.
        var esito = Orders.FirstOrDefault(o => o.OrderId == orderId);
        if (esito is { Status: OrderStatus.Rejected })
        {
            SetMsg($"Ordine RIFIUTATO dopo la conferma: {esito.ErrorMessage}", true);
        }
        else
        {
            SetMsg("Ordine confermato e inviato all'exchange.", false);
        }
    }

    public async Task RejectAsync(int laneId, string orderId, string? userId)
    {
        try
        {
            await mediator.Send(new RejectOrderCommand(laneId, orderId, userId));
            SetMsg("Ordine rifiutato.", false);
        }
        catch (Exception ex)
        {
            SetMsg($"Rifiuto dell'ordine fallito: {ex.Message}. L'ordine può essere ancora in coda.", true);
        }
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

    /// <summary>
    /// Vero solo dopo una lettura RIUSCITA delle soglie dal motore. Non si riusa
    /// <see cref="SafetyReachable"/>, che nasce <c>true</c> prima di qualunque lettura.
    /// </summary>
    private bool _safetyLoadedFromEngine;

    public async Task ReloadSafetyAsync(CancellationToken ct = default)
    {
        var snapshot = await engineConfig.ReadAsync(["Trading:Safety"], ct);
        SafetyReachable = snapshot.Reachable;
        _safetyLoadedFromEngine = snapshot.Reachable;
        SafetyError = snapshot.Error;
        SafetySource = snapshot.SourceOf("Trading:Safety");
        // Con motore irraggiungibile Bind restituisce i DEFAULT: il form resta usabile ma
        // SafetyReachable=false dice al pannello di non spacciarli per le soglie applicate.
        Safety = snapshot.Bind<SafetyConfiguration>("Trading:Safety");
    }

    public async Task SaveSafetyAsync()
    {
        // [2026-08-17] Guardia OBBLIGATORIA, e sta qui e non sul `disabled` del pulsante perché è
        // il presidio vero. Con la lettura fallita il form contiene i DEFAULT DEL CODICE (leva 5x,
        // 5 posizioni, drawdown 20%...), non le soglie in vigore; la scrittura SOSTITUISCE l'intera
        // sezione Trading:Safety e può benissimo riuscire anche se la lettura era fallita (il
        // canale gRPC si riapre nel frattempo). Bastava ritoccare la fee per allargare in silenzio
        // tutti gli altri limiti, con un messaggio verde a rassicurare.
        if (!_safetyLoadedFromEngine)
        {
            SetMsg("Le soglie mostrate sono i default del codice, non quelle in vigore: il motore non ha "
                   + "risposto all'ultima lettura. Premi «Riprova» e attendi una lettura riuscita prima di "
                   + "salvare, altrimenti sovrascriveresti l'intera sezione di sicurezza del motore.", true);
            return;
        }

        if (Safety.MaxPositionSizePercent <= 0 || Safety.MaxTotalExposurePercent <= 0 ||
            Safety.MaxOpenPositions < 1 || Safety.MaxLeverageAllowed < 1 || Safety.FeePercent < 0)
        {
            SetMsg("Valori non validi: size/esposizione devono essere > 0, almeno 1 posizione, leva massima >= 1 e fee >= 0.", true);
            return;
        }

        // Le due soglie CRITICHE non ammettono lo zero: il SafetyChecker le confronta con `>=`,
        // quindi MaxDrawdownPercent = 0 fa scattare l'emergency stop al primo ordine (0 >= 0) e
        // MaxDailyLossPercent = 0 alla prima perdita di un centesimo. Non è un limite severo, è una
        // corsia inutilizzabile — e il motivo sarebbe illeggibile dal messaggio di rifiuto.
        if (Safety.MaxDrawdownPercent <= 0 || Safety.MaxDailyLossPercent <= 0)
        {
            SetMsg("Max drawdown e max perdita giornaliera devono essere > 0: a zero l'emergency stop "
                   + "scatterebbe immediatamente e la corsia non potrebbe operare.", true);
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

            // La scrittura è riuscita e la sezione è stata riletta: la spia «sono i default» non
            // deve sopravvivere alla propria causa.
            SafetyReachable = true;
            SafetyError = null;
            _safetyLoadedFromEngine = true;

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

    /// <summary>
    /// [2026-08-17] Azzera l'esito dell'ultimo comando. Serve al cambio corsia: quei messaggi non
    /// nominano quasi mai la corsia, quindi «Corsia svuotata» o «EMERGENCY STOP inviato» restavano
    /// a schermo mentre sotto era cambiata la corsia a cui si riferivano — lo stesso difetto già
    /// corretto per PromoMessage, che sopravviveva alla propria causa.
    /// </summary>
    public void ClearMessage()
    {
        Message = null;
        IsError = false;
    }
}
