using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Trading.Internal;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Trading engine (Fase 8) per UNA corsia di trading isolata (<paramref name="laneId"/>).
/// Implementa la modalità PAPER (simulazione con dati reali, nessun soldo vero). Registrato come
/// Keyed Singleton (una istanza per corsia — vedi Program.cs) invece di un singolo Singleton
/// globale come prima del supporto multi-coppia: thread-safe via <see cref="SemaphoreSlim"/> come
/// prima, ma ora ogni istanza filtra/imposta <see cref="OpenPosition.LaneId"/> (e l'equivalente
/// su Order/TradeRecord/TradingEngineState/TradingAuditLog) con il PROPRIO
/// <paramref name="laneId"/>, così due corsie non vedono/toccano mai le posizioni o gli ordini
/// l'una dell'altra — anche condividendo lo stesso database. Le righe esistenti PRIMA di questo
/// supporto hanno LaneId=0 (default di migrazione): sono automaticamente la corsia 0, la sessione
/// di trading già in corso non viene toccata da questo refactor.
///
/// SAFETY: ogni apertura passa da <see cref="SafetyChecker"/>; le violazioni critiche
/// (daily loss, drawdown) attivano l'emergency stop che CHIUDE TUTTE le posizioni DI QUESTA
/// corsia (mai delle altre). AUDIT: ogni azione è loggata con la propria LaneId.
/// Idempotenza ordini: ogni ordine ha un ClientOrderId univoco.
///
/// Paper: gli ordini market sono eseguiti immediatamente al prezzo di chiusura della candela.
/// </summary>
public sealed class TradingEngine(
    int laneId,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IStrategyFactory strategyFactory,
    ITechnicalIndicatorsService indicators,
    IExchangeClientFactory exchangeFactory,
    IEnsembleManager ensemble,
    IOptionsMonitor<SafetyConfiguration> safety,
    IOptionsMonitor<LiveExecutionOptions> liveExecution,
    IExecutionAlgorithmFactory executionAlgorithms,
    ILogger<TradingEngine> logger,
    ProcioneMGR.Services.Observability.ProcioneMetrics? metrics = null,
    IModelRegistry? modelRegistry = null,
    IAlphaFactorFactory? alphaFactorFactory = null,
    IFactorCache? factorCache = null,
    ProcioneMGR.Services.Security.IMasterKeyStatus? masterKeyStatus = null,
    ProcioneMGR.Services.ML.IMlComparisonClient? mlComparisonClient = null,
    IOptionsMonitor<ProcioneMGR.Services.ML.MlComparisonOptions>? mlComparisonOptions = null,
    ProcioneMGR.Services.Security.IExchangeCredentialReader? credentialReader = null,
    // [R3] Destinatario del profilo di rischio della corsia (tipicamente lo STESSO oggetto passato
    // come `safety`, cioè un LaneSafetyMonitor). Opzionale: se assente, la corsia resta sulle
    // soglie globali — il comportamento di prima di R3.
    ILaneRiskProfileSink? riskProfileSink = null) : ITradingEngine
{
    public int LaneId => laneId;

    /// <summary>
    /// Nome sentinella di strategia (non nello switch di <see cref="IStrategyFactory"/>): risolve il
    /// Champion del registry via <see cref="IModelRegistry"/> e lo esegue come <see cref="MlStrategy"/>
    /// su questa lane. CONSENTITO SOLO Paper/Testnet — mai Live (vedi <see cref="ResolveChampionStrategyAsync"/>).
    /// </summary>
    public const string ChampionStrategyName = "MlChampion";

    /// <summary>
    /// Cache per-lane del Champion materializzato. Il payload pesante (deserializzazione del modello
    /// dal blob) si ricarica SOLO quando cambia il modello (<see cref="SavedMlModel.Id"/> o
    /// <see cref="SavedMlModel.Version"/>): un controllo leggero a ogni candela, ricostruzione solo al cambio.
    /// </summary>
    private sealed record ChampionCacheEntry(int ModelId, int Version, MlStrategy Strategy, IReturnPredictor Predictor);
    private ChampionCacheEntry? _championCache;

    private const int BufferSize = 400;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Intervento B (Fase 1, PRD §4.5): operazioni di persistenza DB condivise da tutta la cascata estratta.</summary>
    private TradingPersistence Persistence => new(dbFactory, laneId);

    /// <summary>
    /// Intervento B (Fase 1, PRD §4.5): collaboratore estratto per il piazzamento/cancellazione dei
    /// bracket resting. Istanziato ad ogni chiamata (stateless, nessun costo reale) invece che come
    /// campo: un inizializzatore di campo non può referenziare metodi d'istanza (CS0236) prima che
    /// "this" sia costruito.
    /// </summary>
    private BracketOrderManager BracketOrderManager => new(
        exchangeFactory, logger,
        (action, details, ts, ct) => Persistence.AuditAsync(action, details, _state.Mode, ts, ct),
        Persistence.UpdatePositionRowAsync);

    // Dual-read ML (Fase 2a): 0/1 via Interlocked. Garantisce UN SOLO confronto remoto in volo per
    // lane — se il remoto è lento, la candela successiva salta il confronto invece di accodarne
    // un altro (mai una coda che cresce col servizio ml giù).
    private int _mlComparisonInFlight;

    private TradingEngineState _state = new();
    private readonly List<OpenPosition> _positions = new();

    /// <summary>Piani di esecuzione live "a fette" Running per questa corsia (cache, come _positions).</summary>
    private readonly List<ExecutionJob> _executionJobs = new();
    private readonly List<OhlcvData> _buffer = new();
    private readonly List<EquityPoint> _equity = new();
    private List<EnsembleStrategy> _active = new();
    private bool _loaded;
    private TradingCredentials? _creds;   // valorizzate in Testnet/Live
    private SymbolFilters? _filters;      // LOT_SIZE/PRICE_FILTER del simbolo (Testnet/Live)
    private bool _untrackedRemoteAlerted; // dedup dell'allerta "posizione remota sconosciuta"

    // P2-8: prima era una const fissa, scollegata dal fee reale e da BacktestConfiguration.FeePercent
    // (parametrico) — vedi il doc-comment di SafetyConfiguration.FeePercent. Hot-reload via CurrentValue,
    // come ogni altra soglia di SafetyConfiguration.
    private decimal FeeFrac => safety.CurrentValue.FeePercent / 100m;

    /// <summary>
    /// Idempotenza: al primo accesso ripristina stato e posizioni aperte dal DB, così dopo
    /// un riavvio il sistema non riparte da zero (e non duplica ordini).
    /// </summary>
    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.TradingEngineStates.Where(s => s.LaneId == laneId).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (row is not null) _state = row;
        var positions = await db.OpenPositions.AsNoTracking().Where(p => p.LaneId == laneId).ToListAsync(ct);

        // [M2] Discriminatore anti-mescolamento: si ricaricano SOLO le posizioni aperte nella
        // modalità corrente della corsia. Righe di un'altra modalità (residuo di una promozione/
        // retrocessione interrotta a metà) vengono PURGATE con audit: una posizione simulata
        // Paper non deve mai sembrare esposizione reale Testnet, né viceversa.
        var stale = positions.Where(p => p.OpenedInMode != _state.Mode).ToList();
        if (stale.Count > 0)
        {
            var ids = stale.Select(p => p.Id).ToList();
            await db.OpenPositions.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync(ct);
            logger.LogWarning("Purgate {N} posizioni di una modalità diversa ({Modes}) dalla corsia in {Mode}.",
                stale.Count, string.Join(",", stale.Select(p => p.OpenedInMode).Distinct()), _state.Mode);
            await AuditAsync("StalePositionsPurged", new
            {
                mode = _state.Mode.ToString(),
                purged = stale.Select(p => new { p.PositionId, p.Symbol, openedInMode = p.OpenedInMode.ToString(), p.Quantity }),
            }, DateTime.UtcNow, ct);
        }

        _positions.Clear();
        _positions.AddRange(positions.Where(p => p.OpenedInMode == _state.Mode));
        // Solo i job Running (i chiusi sono storia, mai riletti a runtime): cache piccola per sempre.
        var jobs = await db.ExecutionJobs.AsNoTracking().Where(j => j.LaneId == laneId && j.Status == "Running").ToListAsync(ct);
        _executionJobs.Clear();
        _executionJobs.AddRange(jobs);
        _loaded = true;
        logger.LogInformation("TradingEngine: stato ripristinato dal DB (running={Run}, emergency={Emg}, posizioni={N}).",
            _state.IsRunning, _state.IsEmergencyStopped, _positions.Count);
    }

    // ---------------------------------------------------------------- lifecycle

    public async Task StartAsync(TradingMode mode, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Fase 0-A3 (PRD Autonomia): una corsia in quarantena NON riparte finché un umano non
            // rimuove la quarantena in /trading (solo Admin). Il controllo sta QUI e non solo nel
            // watchdog perché StartAsync rigenera lo stato da zero (capitale/PnL azzerati):
            // riavviare cancellerebbe proprio l'evidenza contabile che ha fatto scattare l'allarme.
            await using (var qdb = await dbFactory.CreateDbContextAsync(ct))
            {
                var quarantine = await qdb.LaneQuarantines.AsNoTracking()
                    .FirstOrDefaultAsync(q => q.LaneId == laneId, ct);
                if (quarantine is not null)
                {
                    throw new InvalidOperationException(
                        $"Corsia {laneId} in QUARANTENA dal {quarantine.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC: {quarantine.Reason}. " +
                        "Verifica lo stato contabile (posizioni/ordini/audit) e rimuovi la quarantena in /trading (solo Admin) prima di riavviare.");
                }
            }

            // Con la master key placeholder del template (pubblica su git) le credenziali
            // exchange "cifrate" sono in chiaro di fatto: soldi veri MAI su quella base.
            // Paper/Testnet restano permessi (comodi in sviluppo).
            if (mode == TradingMode.Live && masterKeyStatus?.IsDefaultDevKey == true)
            {
                throw new InvalidOperationException(
                    "Trading LIVE bloccato: Security:MasterKey è ancora il placeholder di sviluppo del template. " +
                    "Genera una chiave reale (base64 di 32 byte, env PROCIONE_MGR_MASTER_KEY) e ricifra le credenziali.");
            }

            var cfg = await ensemble.GetConfigurationAsync(ct);

            // [R3] Il profilo di rischio della corsia entra in vigore QUI, prima di ogni controllo
            // sottostante: leva massima e coerenza del sizing devono essere validate contro le
            // soglie EFFETTIVE della corsia, non contro quelle globali. Nome sconosciuto o assente
            // ⇒ null ⇒ soglie globali, cioè il comportamento di prima di R3.
            var laneProfile = Risk.RiskProfiles.Find(cfg.RiskProfileName);
            riskProfileSink?.SetProfile(laneProfile);
            if (laneProfile is not null)
            {
                logger.LogInformation("Lane {Lane}: profilo di rischio '{Profile}' attivo (≤{Trades:F0} operazioni/giorno).",
                    laneId, laneProfile.DisplayName, laneProfile.MaxTradesPerDay);
            }

            var capital = cfg.TotalCapital > 0 ? cfg.TotalCapital : 10_000m;
            var marketType = cfg.IsFutures ? MarketType.Futures : MarketType.Spot;
            var requestedLeverage = marketType == MarketType.Futures ? Math.Max(1, cfg.Leverage) : 1;

            // Rifiuta subito una leva oltre il limite di sicurezza (meglio bloccare l'avvio che
            // scoprirlo al primo ordine): l'utente deve alzare il limite consapevolmente in
            // Trading (solo Admin) se vuole davvero operare con più leva.
            if (marketType == MarketType.Futures && requestedLeverage > safety.CurrentValue.MaxLeverageAllowed)
            {
                throw new InvalidOperationException(
                    $"Leva richiesta {requestedLeverage}x oltre il limite di sicurezza {safety.CurrentValue.MaxLeverageAllowed}x " +
                    "(configurazione Ensemble). Alza il limite in Trading (solo Admin) per procedere consapevolmente, o riduci la leva.");
            }

            // Coerenza sizing vs safety: il nozionale per posizione (margine × leva sui Futures)
            // deve stare sotto i cap del SafetyChecker, altrimenti OGNI ordine verrebbe rifiutato
            // ("Posizione troppo grande") e la corsia non farebbe mai trading — meglio bloccare
            // l'avvio con una spiegazione che scoprirlo dal silenzio degli ordini.
            var sizePct = safety.CurrentValue.PositionSizePercent;
            var notionalPct = marketType == MarketType.Futures ? sizePct * requestedLeverage : sizePct;
            if (notionalPct > safety.CurrentValue.MaxPositionSizePercent || notionalPct > safety.CurrentValue.MaxTotalExposurePercent)
            {
                throw new InvalidOperationException(
                    $"Sizing incoerente: {sizePct}% di {(marketType == MarketType.Futures ? "margine" : "capitale")}" +
                    $"{(marketType == MarketType.Futures ? $" × leva {requestedLeverage}x" : "")} = nozionale {notionalPct}% per posizione, " +
                    $"oltre MaxPositionSizePercent ({safety.CurrentValue.MaxPositionSizePercent}%) o MaxTotalExposurePercent " +
                    $"({safety.CurrentValue.MaxTotalExposurePercent}%): ogni ordine verrebbe rifiutato dal SafetyChecker. " +
                    "Alza i limiti nel pannello sicurezza di /trading (solo Admin) o riduci leva/size.");
            }

            _state = new TradingEngineState
            {
                LaneId = laneId,
                Mode = mode,
                MarketType = marketType,
                Leverage = requestedLeverage,
                IsRunning = true,
                ExchangeName = cfg.ExchangeName,
                Symbol = cfg.Symbol,
                Timeframe = cfg.Timeframe,
                TotalCapital = capital,
                AvailableCapital = capital,
                RealizedPnl = 0m,
                PeakEquity = capital,
                DailyPnl = 0m,
                DailyAnchorUtc = DateTime.UtcNow,
                StartedAtUtc = DateTime.UtcNow,
                IsEmergencyStopped = false,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            _active = cfg.Strategies.Where(s => s.IsActive).ToList();
            _positions.Clear();
            _buffer.Clear();
            _equity.Clear();
            _loaded = true;

            // Testnet/Live: carica le credenziali firmate + i filtri del simbolo (+ leva per i Futures).
            _creds = null;
            _filters = null;
            if (mode != TradingMode.Paper)
            {
                var testnet = mode == TradingMode.Testnet;
                try
                {
                    _creds = await LoadCredentialsAsync(cfg.ExchangeName, testnet, ct);
                }
                catch (InvalidOperationException)
                {
                    // Credenziale presente ma NON decifrabile (master key diversa): stesso
                    // cleanup del caso "assente" — la corsia non parte e lo stato lo riflette.
                    _state.IsRunning = false;
                    await SaveStateAsync(ct);
                    throw;
                }
                if (_creds is null)
                {
                    _state.IsRunning = false;
                    await SaveStateAsync(ct);
                    throw new InvalidOperationException(
                        $"Nessuna credenziale {cfg.ExchangeName} ({(testnet ? "testnet" : "live")}) trovata. Aggiungile in /settings/exchanges.");
                }

                if (marketType == MarketType.Futures)
                {
                    var futuresClient = exchangeFactory.CreateFutures(cfg.ExchangeName);
                    var levResult = await futuresClient.SetLeverageAsync(cfg.Symbol, requestedLeverage, _creds.Value, ct);
                    if (!levResult.Success)
                    {
                        _state.IsRunning = false;
                        await SaveStateAsync(ct);
                        throw new InvalidOperationException($"Impostazione leva {requestedLeverage}x su {cfg.Symbol} fallita: {levResult.Error}");
                    }
                    // L'exchange può accettare una leva diversa da quella richiesta (cap per simbolo).
                    _state.Leverage = levResult.Leverage;
                    _filters = await futuresClient.GetFuturesSymbolFiltersAsync(cfg.Symbol, testnet, ct);
                    logger.LogInformation("Futures {Symbol}: leva impostata a {Lev}x, step={Step}, minQty={MinQ}.",
                        cfg.Symbol, _state.Leverage, _filters.StepSize, _filters.MinQty);
                }
                else
                {
                    _filters = await exchangeFactory.Create(cfg.ExchangeName).GetSymbolFiltersAsync(cfg.Symbol, testnet, ct);
                    logger.LogInformation("Filtri {Symbol}: step={Step}, minQty={MinQ}, minNotional={MinN}.",
                        cfg.Symbol, _filters.StepSize, _filters.MinQty, _filters.MinNotional);
                }
            }

            // Paper: parti da stato pulito (nessuna posizione residua) — SOLO per questa corsia.
            // CRITICO: senza il filtro LaneId, avviare/riavviare una corsia in Paper cancellerebbe
            // le posizioni aperte di TUTTE le altre corsie condividendo lo stesso DB.
            // SOLO Paper: su Testnet/Live le posizioni sono REALI sull'exchange — cancellare la riga
            // locale a un riavvio perderebbe traccia di un'esposizione ancora aperta (la cache
            // in-memory viene comunque ripopolata da EnsureLoadedAsync, che ricarica dal DB).
            if (mode == TradingMode.Paper)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                await db.OpenPositions.Where(p => p.LaneId == laneId).ExecuteDeleteAsync(ct);
            }

            // Abbandona i piani di esecuzione orfani di una sessione precedente: il contesto
            // (credenziali/exchange/modalità) può essere cambiato, non vanno ripresi.
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                await db.ExecutionJobs
                    .Where(j => j.LaneId == laneId && j.Status == "Running")
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, "Cancelled")
                        .SetProperty(j => j.CompletedAtUtc, (DateTime?)DateTime.UtcNow)
                        .SetProperty(j => j.FailureReason, "Sessione precedente abbandonata al riavvio del motore."), ct);
            }
            _executionJobs.Clear();

            await SaveStateAsync(ct);
            await AuditAsync("StartEngine", new { mode = mode.ToString(), capital, strategies = _active.Count }, DateTime.UtcNow, ct);
            logger.LogInformation("Trading engine avviato in modalità {Mode} con {N} strategie, capitale {Cap}.", mode, _active.Count, capital);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            _state.IsRunning = false;
            await SaveStateAsync(ct);
            await AuditAsync("StopEngine", new { }, DateTime.UtcNow, ct);
            // Il predictor ML del Champion resta vivo solo mentre la corsia gira: allo stop lo
            // liberiamo subito invece di aspettare il prossimo cambio Champion (che potrebbe non
            // arrivare mai su una lane fermata). Azzerare il riferimento, non solo il dispose, evita
            // che un riavvio con lo STESSO Champion (stesso ModelId/Version) trovi in cache un
            // predictor già disposto e provi a riusarlo (vedi il confronto in ResolveChampionStrategyAsync).
            _championCache?.Predictor.Dispose();
            _championCache = null;
            logger.LogInformation("Trading engine fermato (posizioni lasciate aperte).");
        }
        finally { _gate.Release(); }
    }

    public async Task EmergencyStopAsync(string reason, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await EnsureLoadedAsync(ct); await EmergencyInternalAsync(reason, DateTime.UtcNow, ct); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// [M2] Chiude tutte le posizioni della corsia al miglior prezzo noto SENZA toccare i flag di
    /// emergenza: è il "flatten" usato dalla promozione/retrocessione di corsia (LanePromoter),
    /// dove fermare la corsia non è un'emergenza. EmergencyStop condivide lo stesso loop di
    /// chiusura via <see cref="CloseAllInternalAsync"/>.
    /// </summary>
    public async Task CloseAllPositionsAsync(string reason, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var ts = DateTime.UtcNow;
            var count = _positions.Count;
            await CloseAllInternalAsync(reason, ts, ct);
            await SaveStateAsync(ct);
            if (count > 0)
            {
                await AuditAsync("CloseAllPositions", new { reason, requested = count, remaining = _positions.Count }, ts, ct);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Loop di chiusura condiviso tra EmergencyStop e flatten: chiusura market al miglior prezzo
    /// noto. Una chiusura può NON riuscire (rete incerta / rifiuto exchange): la posizione resta
    /// in <see cref="_positions"/> e il chiamante decide come procedere (retry, log, ecc.).
    /// </summary>
    private async Task CloseAllInternalAsync(string reason, DateTime ts, CancellationToken ct)
    {
        foreach (var pos in _positions.ToList())
        {
            var exit = pos.CurrentPrice > 0m ? pos.CurrentPrice : pos.EntryPrice;
            await ClosePositionAsync(pos, exit, reason, ts, ct);
        }
    }

    private async Task EmergencyInternalAsync(string reason, DateTime ts, CancellationToken ct)
    {
        _state.IsEmergencyStopped = true;
        _state.IsRunning = false;
        _state.EmergencyStopReason = reason;

        // Chiudi TUTTE le posizioni al prezzo corrente (market). ClosePositionAsync annulla anche
        // il piano di esecuzione eventualmente associato a ciascuna posizione (vedi §1 del design).
        await CloseAllInternalAsync("EmergencyStop", ts, ct);

        // Difesa: qualunque piano ancora Running (posizione non trovata, caso limite) va comunque annullato.
        foreach (var job in _executionJobs.ToList())
        {
            job.Status = "Cancelled";
            job.CompletedAtUtc = ts;
            _executionJobs.Remove(job);
            await PersistExecutionJobAsync(job, ct);
            metrics?.RecordExecutionJob(job.Algorithm, "Cancelled");
        }

        await SaveStateAsync(ct);
        await AuditAsync("EmergencyStop", new { reason }, ts, ct);
        logger.LogCritical("EMERGENCY STOP: {Reason}. Tutte le posizioni chiuse.", reason);
    }

    // ---------------------------------------------------------------- candle processing

    public async Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (!_state.IsRunning || _state.IsEmergencyStopped)
            {
                return;
            }
            if (_buffer.Count > 0 && candle.TimestampUtc <= _buffer[^1].TimestampUtc)
            {
                return; // niente replay di candele già viste
            }

            _buffer.Add(candle);
            if (_buffer.Count > BufferSize) _buffer.RemoveAt(0);

            var price = candle.Close;
            var ts = DateTime.SpecifyKind(candle.TimestampUtc, DateTimeKind.Utc);

            // Futures Testnet/Live: rileva liquidazioni forzate dall'exchange (o chiusure
            // manuali fatte fuori dalla piattaforma) PRIMA di valutare qualsiasi altra cosa,
            // così lo stato locale non "mente" su una posizione che non esiste più.
            await ReconcileFuturesPositionsAsync(price, ts, ct);

            // Mark-to-market + liquidazione (solo Futures) + stop loss / take profit.
            //
            // La DECISIONE vive in ProtectiveExitEvaluator (funzione pura): stessa identica logica
            // usata dal percorso a tick real-time (ProcessPriceTickAsync), così le due strade non
            // possono divergere. Qui resta solo l'esecuzione (chiusura + persistenza).
            //
            // Stop/target sono valutati INTRABAR su High/Low della candela chiusa, come la
            // liquidazione e come il motore di backtest: un wick può bucare lo stop anche se la
            // candela chiude al di là.
            await ApplyProtectiveExitsAsync(candle.Open, candle.High, candle.Low, price, ts, "candle", ct);

            // Valuta i segnali delle strategie.
            var closes = _buffer.Select(c => c.Close).ToList();
            if (closes.Count >= 5)
            {
                foreach (var strat in _active)
                {
                    if (_state.IsEmergencyStopped) break;
                    var s = strat.StrategyName == ChampionStrategyName
                        ? await ResolveChampionStrategyAsync(ct)
                        : strategyFactory.Create(strat.StrategyName);
                    await s.InitializeAsync(closes, _buffer, strat.Parameters, indicators, ct);
                    var sig = s.EvaluateSignal(closes.Count - 1, price, ts);

                    // Dual-read ML (Fase 2a): confronto OSSERVATIVO col servizio remoto. Fire-and-forget,
                    // non tocca 'sig' né alcuna decisione — vedi FireAndForgetMlComparison.
                    if (strat.StrategyName == ChampionStrategyName && s is MlStrategy mlStrat)
                    {
                        FireAndForgetMlComparison(mlStrat, closes.Count - 1);
                    }

                    var pos = _positions.FirstOrDefault(p => p.StrategyId == strat.StrategyId);
                    switch (sig)
                    {
                        case Signal.Long:
                            if (pos is { Side: OrderSide.Sell }) await ClosePositionAsync(pos, price, "Signal", ts, ct);
                            if (_positions.All(p => p.StrategyId != strat.StrategyId)) await TryOpenAsync(strat, OrderSide.Buy, price, ts, ct);
                            break;
                        case Signal.Short:
                            if (pos is { Side: OrderSide.Buy }) await ClosePositionAsync(pos, price, "Signal", ts, ct);
                            if (_positions.All(p => p.StrategyId != strat.StrategyId)) await TryOpenAsync(strat, OrderSide.Sell, price, ts, ct);
                            break;
                        case Signal.Close:
                            if (pos is not null) await ClosePositionAsync(pos, price, "Signal", ts, ct);
                            break;
                    }
                }
            }

            // Equity + drawdown.
            var equity = ComputeEquity(price);
            _equity.Add(new EquityPoint { Timestamp = ts, Capital = equity });
            TrimEquity(_equity);   // M1: la curva in-memory non cresce senza limite (candela ∞)
            if (equity > _state.PeakEquity) _state.PeakEquity = equity;
            var dd = _state.PeakEquity > 0m ? (_state.PeakEquity - equity) / _state.PeakEquity * 100m : 0m;
            if (dd > _state.MaxDrawdownPercent) _state.MaxDrawdownPercent = dd;   // persistito: sopravvive al riavvio

            await SaveStateAsync(ct);

            if (dd >= safety.CurrentValue.MaxDrawdownPercent && !_state.IsEmergencyStopped)
            {
                await EmergencyInternalAsync($"Max drawdown {dd:F1}% superato", ts, ct);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// [R1] Elabora un TICK di prezzo real-time. Valuta ESCLUSIVAMENTE le uscite protettive
    /// (liquidazione, stop loss, take profit, trailing) sulle posizioni già aperte.
    ///
    /// CONFINE NON NEGOZIABILE: da qui non si apre MAI una posizione e non si valuta MAI un segnale
    /// di strategia. <see cref="IStrategy"/> è per-candela con indicatori precalcolati ed è quello
    /// che il backtest valida: valutarlo sui tick introdurrebbe un divario backtest/live nuovo,
    /// esattamente l'opposto di ciò che questo percorso serve a chiudere. Gli ingressi restano
    /// governati da <see cref="ProcessCandleAsync"/>.
    ///
    /// Il tick è passato all'evaluator come barra DEGENERE (open=high=low=prezzo): la stessa
    /// funzione pura decide per candela e per tick, e il fill collassa spontaneamente sul prezzo
    /// corrente di mercato — il prezzo realistico di esecuzione in quell'istante.
    ///
    /// COALESCENZA: se il motore è occupato (una chiusura in corso fa I/O di rete tenendo il gate)
    /// il tick viene SCARTATO invece di accodarsi. Ne arriva un altro entro millisecondi, mentre una
    /// coda di tick vecchi farebbe decidere su prezzi ormai stantii. Questo è anche il latch che
    /// impedisce a una raffica di tick di emettere due chiusure sulla stessa posizione: chi non
    /// entra non decide, e chi entra dopo non trova più la posizione chiusa in <see cref="_positions"/>.
    /// </summary>
    public async Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default)
    {
        if (price <= 0m)
        {
            return; // prezzo non plausibile: mai decidere un'uscita su di esso
        }

        if (!await _gate.WaitAsync(0, ct))
        {
            return; // motore occupato: si scarta questo tick, non si accoda
        }
        try
        {
            await EnsureLoadedAsync(ct);
            if (!_state.IsRunning || _state.IsEmergencyStopped || _positions.Count == 0)
            {
                return;
            }

            var ts = DateTime.SpecifyKind(tsUtc, DateTimeKind.Utc);
            await ApplyProtectiveExitsAsync(price, price, price, price, ts, "tick", ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Mark-to-market e applicazione delle uscite protettive a tutte le posizioni aperte della
    /// corsia. Il CHIAMANTE deve già detenere <see cref="_gate"/>.
    ///
    /// La decisione è delegata a <see cref="ProtectiveExitEvaluator"/> (pura, condivisa fra percorso
    /// a candela e percorso a tick); qui resta solo l'esecuzione. Il ciclo itera su una COPIA perché
    /// <see cref="ClosePositionAsync"/> rimuove da <see cref="_positions"/>.
    /// </summary>
    private async Task ApplyProtectiveExitsAsync(
        decimal open, decimal high, decimal low, decimal markPrice, DateTime ts, string source, CancellationToken ct)
    {
        var isFutures = _state.MarketType == MarketType.Futures;

        foreach (var pos in _positions.ToList())
        {
            MarkToMarket(pos, markPrice);

            var liquidation = ProtectiveExitEvaluator.EvaluateLiquidation(pos, high, low, isFutures);
            if (liquidation.ShouldClose)
            {
                await CloseAndCountAsync(pos, liquidation, source, ts, ct);
                continue;
            }

            var exit = ProtectiveExitEvaluator.EvaluateStopAndTarget(pos, open, high, low);
            if (exit.ShouldClose)
            {
                await CloseAndCountAsync(pos, exit, source, ts, ct);
            }
            else
            {
                // Solo se NESSUNA uscita è scattata: il trailing resta causale.
                ProtectiveExitEvaluator.UpdateBestSinceEntry(pos, high, low);
            }
        }
    }

    /// <summary>
    /// Chiude e conta l'uscita SOLO se la chiusura è andata a buon fine, cioè se la posizione ha
    /// davvero lasciato <see cref="_positions"/>.
    ///
    /// La distinzione conta: <see cref="ClosePositionAsync"/> può rinunciare (rete incerta, rifiuto
    /// dell'exchange) lasciando la posizione aperta per il retry. Contando comunque, un ordine di
    /// chiusura che continua a fallire genererebbe una registrazione a OGNI valutazione — e sul
    /// percorso a tick, dove le valutazioni sono decine al secondo invece di una per candela,
    /// gonfierebbe di migliaia di conteggi proprio la metrica che serve a confrontare tick e candela.
    /// </summary>
    private async Task CloseAndCountAsync(
        OpenPosition pos, Internal.ProtectiveExit exit, string source, DateTime ts, CancellationToken ct)
    {
        await ClosePositionAsync(pos, exit.FillPrice, exit.Reason, ts, ct);
        if (!_positions.Contains(pos))
        {
            metrics?.RecordProtectiveExit(source, exit.Reason);
        }
    }

    /// <summary>
    /// Risolve il Champion del registry come <see cref="MlStrategy"/> per questa lane.
    ///
    /// CONFINE DI SICUREZZA NON NEGOZIABILE: se la lane è in <see cref="TradingMode.Live"/> il
    /// caricamento è RIFIUTATO con throw esplicito — mai un fallback silenzioso. Il Champion può
    /// alimentare SOLO Paper/Testnet (stesso stile dei confini in <c>LanePromoter</c>/
    /// <c>PromotionEvaluator</c>): il codice, non una convenzione, impedisce che un modello del
    /// registry apra posizioni con soldi veri.
    ///
    /// La cache per-lane evita di rideserializzare il modello a ogni candela: si ricostruisce solo
    /// quando il Champion cambia (Id/Version), come richiesto perché <see cref="MlStrategy"/> senza
    /// cache ricaricherebbe l'intero payload a ogni tick.
    /// </summary>
    private async Task<IStrategy> ResolveChampionStrategyAsync(CancellationToken ct)
    {
        if (_state.Mode == TradingMode.Live)
        {
            throw new InvalidOperationException(
                "CONFINE DI SICUREZZA: il Champion del registry non può MAI alimentare una lane Live. Consentito solo Paper/Testnet.");
        }
        if (modelRegistry is null || alphaFactorFactory is null)
        {
            throw new InvalidOperationException(
                $"La strategia '{ChampionStrategyName}' richiede IModelRegistry e IAlphaFactorFactory: dipendenze non iniettate in questo TradingEngine.");
        }

        var champion = await modelRegistry.GetChampionAsync(_state.Symbol, _state.Timeframe, ct)
            ?? throw new InvalidOperationException(
                $"Nessun Champion nel registry per {_state.Symbol} {_state.Timeframe}: impossibile risolvere '{ChampionStrategyName}'.");

        // Controllo leggero a ogni candela; payload pesante ricaricato solo al cambio di modello.
        if (_championCache is null || _championCache.ModelId != champion.Id || _championCache.Version != champion.Version)
        {
            _championCache?.Predictor.Dispose();
            var (strategy, predictor) = await MlModelLoader.LoadAsync(champion, alphaFactorFactory, factorCache, ct);
            _championCache = new ChampionCacheEntry(champion.Id, champion.Version, strategy, predictor);
            logger.LogInformation("Lane {Lane}: Champion caricato — modello {Id} v{Ver} ({Type}) per {Sym} {Tf}.",
                laneId, champion.Id, champion.Version, champion.ModelType, _state.Symbol, _state.Timeframe);
        }
        return _championCache.Strategy;
    }

    /// <summary>
    /// Confronto dual-read col servizio ml remoto (Fase 2a): PURAMENTE osservativo. Non fa await (il
    /// ciclo di trading prosegue subito), non ritorna nulla, non può propagare eccezioni al chiamante.
    /// Un solo confronto in volo per lane (Interlocked): se il remoto è lento, la candela dopo salta.
    /// La predizione locale è calcolata QUI, sincrona (modello in cache), per confrontarla col remoto.
    /// </summary>
    private void FireAndForgetMlComparison(MlStrategy mlStrat, int index)
    {
        if (mlComparisonClient is null) return;                          // client non registrato
        if (mlComparisonOptions?.CurrentValue.Enabled != true) return;   // toggle spento (hot-reload)
        if (_championCache is null) return;

        if (Interlocked.CompareExchange(ref _mlComparisonInFlight, 1, 0) != 0) return; // già in volo

        var input = mlStrat.TryGetPredictorInput(index);
        if (input is null) { Interlocked.Exchange(ref _mlComparisonInFlight, 0); return; } // warm-up

        float localPredicted;
        try { localPredicted = _championCache.Predictor.Predict(input); }
        catch { Interlocked.Exchange(ref _mlComparisonInFlight, 0); return; }

        var championId = _championCache.ModelId;
        var symbol = _state.Symbol;
        var timeframe = _state.Timeframe;

        // NON await: il ciclo di trading prosegue subito. L'unica mutazione condivisa è il flag
        // Interlocked; input punta a dati immutabili (array non riscritto dopo la costruzione).
        _ = Task.Run(async () =>
        {
            try
            {
                await mlComparisonClient.CompareAsync(laneId, symbol, timeframe, championId, input, localPredicted, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Lane {Lane}: confronto ml remoto (fire-and-forget) fallito, ignorato.", laneId);
            }
            finally
            {
                Interlocked.Exchange(ref _mlComparisonInFlight, 0);
            }
        });
    }

    // ---------------------------------------------------------------- open / close

    private SignalOrderBuilder SignalOrderBuilder => new(logger, Persistence, safety);

    /// <summary>
    /// Le chiusure passate a <see cref="SignalOrderBuilder"/> vengono dal buffer del motore, cioè
    /// dalle STESSE candele che la strategia ha già visto: il dosaggio sulla volatilità non può
    /// quindi guardare avanti più di quanto guardi il segnale che sta dimensionando.
    /// </summary>
    private Task TryOpenAsync(EnsembleStrategy strat, OrderSide side, decimal price, DateTime ts, CancellationToken ct) =>
        SignalOrderBuilder.TryOpenAsync(_state, _filters, TryBuildAndStartExecutionPlanAsync, strat, side, price, ts, ct,
            _buffer.Select(c => c.Close).ToList());

    /// <summary>
    /// Applica automaticamente lo stop-loss/take-profit/trailing validati nel backtest (se
    /// configurati sulla <see cref="EnsembleStrategy"/> di questo ordine) alla posizione appena
    /// aperta. Gira SOLO qui, una volta, alla creazione: nessun altro punto del motore rimette
    /// mano a questi valori, quindi una modifica manuale successiva da <c>/trading</c>
    /// (<see cref="SetStopLossTakeProfitAsync"/>) resta sempre l'ultima parola per quella
    /// posizione — non serve un controllo esplicito di "già impostato", perché a questo punto
    /// la posizione non esiste ancora.
    /// </summary>
    private void ApplyAutoStops(OpenPosition pos, Order order) => AutoStopApplier.Apply(pos, order, _active);

    // ---------------------------------------------------------------- riconciliazione ordini incerti

    /// <summary>Delegato all'Intervento B (Fase 1, PRD §4.5): vedi <see cref="OrderReconciler"/>.</summary>
    private Task<ReconcileOutcome> ReconcileUncertainOrderAsync(string symbol, string clientOrderId, bool futures, TradingCredentials creds, CancellationToken ct) =>
        new OrderReconciler(exchangeFactory).ReconcileUncertainOrderAsync(_state.ExchangeName, symbol, clientOrderId, futures, creds, ct);

    /// <summary>
    /// Esegue effettivamente l'apertura: safety condivisa, poi dispatch Spot/Futures. Ritorna true
    /// se il fill è avvenuto (posizione creata o accresciuta). <paramref name="mergeInto"/> null =
    /// crea una nuova posizione (comportamento INVARIATO); non-null = fonde il fill in una posizione
    /// esistente via media ponderata (path delle fette 2..K di un ExecutionJob).
    /// </summary>
    private async Task<bool> ExecuteOpenAsync(Order order, string strategyName, decimal currentPrice, DateTime ts, CancellationToken ct, bool isExisting, OpenPosition? mergeInto = null)
    {
        var status = BuildSafetyStatus(currentPrice);
        var check = SafetyChecker.Evaluate(order, status, safety.CurrentValue, ts);
        if (!check.IsAllowed)
        {
            order.Status = OrderStatus.Rejected;
            order.ErrorMessage = string.Join(" | ", check.Violations);
            await SaveOrderAsync(order, isExisting, ct);
            await AuditAsync("OrderRejected", new { order.ClientOrderId, strategyName, check.Violations }, ts, ct);
            if (check.RequiresEmergencyStop)
            {
                await EmergencyInternalAsync("Safety critico: " + string.Join("; ", check.Violations), ts, ct);
            }
            return false;
        }

        return _state.MarketType == MarketType.Futures
            ? await ExecuteFuturesOpenAsync(order, strategyName, currentPrice, ts, ct, isExisting, mergeInto)
            : await ExecuteSpotOpenAsync(order, strategyName, currentPrice, ts, ct, isExisting, mergeInto);
    }

    private PositionOpener PositionOpener => new(exchangeFactory, logger, Persistence, metrics, safety);

    /// <summary>Apertura SPOT. mergeInto=null crea una nuova posizione (INVARIATO); non-null fonde il fill via media ponderata.</summary>
    private Task<bool> ExecuteSpotOpenAsync(Order order, string strategyName, decimal currentPrice, DateTime ts, CancellationToken ct, bool isExisting, OpenPosition? mergeInto = null) =>
        PositionOpener.ExecuteSpotOpenAsync(_state, _positions, _active, _creds, FeeFrac, order, strategyName, currentPrice, ts, ct, isExisting, mergeInto);

    /// <summary>
    /// Apertura FUTURES: margine ISOLATO (solo il margine viene sottratto ad AvailableCapital,
    /// non l'intero nozionale leveraged), prezzo di liquidazione dalla fonte di verità
    /// dell'exchange (con fallback alla stima locale <see cref="MarginMath"/>).
    /// </summary>
    private Task<bool> ExecuteFuturesOpenAsync(Order order, string strategyName, decimal currentPrice, DateTime ts, CancellationToken ct, bool isExisting, OpenPosition? mergeInto = null) =>
        PositionOpener.ExecuteFuturesOpenAsync(_state, _positions, _active, _creds, FeeFrac, order, strategyName, currentPrice, ts, ct, isExisting, mergeInto);

    /// <summary>
    /// [P0-5] Piazza sull'exchange gli ordini TRIGGER reduce-only (stop-market e take-profit-market) che
    /// replicano <see cref="OpenPosition.StopLoss"/>/<see cref="OpenPosition.TakeProfit"/>. Invocato solo se
    /// <see cref="SafetyConfiguration.UseExchangeRestingStops"/> è attivo (default OFF). Mai bloccante: ogni
    /// fallimento è solo loggato e gli stop software restano la fonte di verità.
    /// </summary>
    private Task TryPlaceRestingBracketAsync(OpenPosition pos, TradingCredentials creds, DateTime ts, CancellationToken ct) =>
        BracketOrderManager.TryPlaceRestingBracketAsync(pos, creds, _state.ExchangeName, ts, ct);

    /// <summary>
    /// [P0-5] Cancella gli ordini TRIGGER resting prima di chiudere a mercato, così non restano
    /// ordini orfani sull'exchange. INERTE se non ci sono id (feature off, default).
    /// </summary>
    private Task TryCancelRestingBracketAsync(OpenPosition pos, TradingCredentials creds, CancellationToken ct) =>
        BracketOrderManager.TryCancelRestingBracketAsync(pos, creds, _state.ExchangeName, ct);

    // ---------------------------------------------------------------- esecuzione a fette (TWAP/VWAP/Iceberg)

    /// <summary>
    /// Decide fra apertura IMMEDIATA (comportamento odierno) ed esecuzione a fette. L'aggancio è QUI,
    /// dopo il gate di conferma manuale Live, così lo slicing non lo scavalca mai. Rif. ROADMAP-QLIB §1.2.
    /// </summary>
    private ExecutionSlicePlanner ExecutionSlicePlanner => new(executionAlgorithms, liveExecution, safety, metrics, Persistence, laneId);

    private Task TryBuildAndStartExecutionPlanAsync(Order order, EnsembleStrategy? strat, string strategyName, decimal price, DateTime ts, CancellationToken ct, bool isExisting) =>
        ExecutionSlicePlanner.TryBuildAndStartExecutionPlanAsync(
            _state, _positions, _executionJobs,
            BuildSafetyStatus,
            (o, sn, p, t, c, ie, mi) => ExecuteOpenAsync(o, sn, p, t, c, ie, mi),
            EmergencyInternalAsync,
            order, strat, strategyName, price, ts, ct, isExisting);

    /// <summary>
    /// Avanza le fette dovute di ogni piano Running di questa corsia. Chiamato dall'<c>ExecutionWorker</c>.
    /// Stesso pattern-guard di <see cref="ProcessCandleAsync"/> (gate + IsRunning/IsEmergencyStopped),
    /// quindi serializzato con tutto il resto del motore.
    /// </summary>
    public async Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (!_state.IsRunning || _state.IsEmergencyStopped || _state.Mode == TradingMode.Paper) return;
            if (!liveExecution.CurrentValue.Enabled) return;

            var now = DateTime.UtcNow;
            foreach (var job in _executionJobs.Where(j => j.Status == "Running").ToList())
            {
                var slices = ExecutionJobSlices.Deserialize(job.SlicesJson);
                var due = slices.FirstOrDefault(s => s.Status == "Pending" && job.CreatedAtUtc.AddSeconds(s.OffsetSeconds) <= now);
                if (due is null) continue;

                var pos = _positions.FirstOrDefault(p => p.PositionId == job.PositionId);
                if (pos is null)
                {
                    // Posizione chiusa da un altro path prima del completamento (già coperto da
                    // ClosePositionAsync, questo è un secondo cordone difensivo).
                    job.Status = "Cancelled"; job.CompletedAtUtc = now;
                    _executionJobs.Remove(job);
                    await PersistExecutionJobAsync(job, ct);
                    metrics?.RecordExecutionJob(job.Algorithm, "Cancelled");
                    continue;
                }

                var qty = due.Quantity;
                if (_filters is not null)
                {
                    qty = _filters.RoundQuantity(qty);
                    if (!_filters.IsTradable(qty, pos.CurrentPrice))
                    {
                        // Dust sotto i minimi: assorbita dalla prossima fetta Pending (o abbandonata se ultima).
                        due.Status = "MergedIntoNext";
                        var next = slices.FirstOrDefault(s => s.Status == "Pending" && !ReferenceEquals(s, due));
                        if (next is not null) next.Quantity += due.Quantity; else due.Status = "Abandoned";
                        FinalizeJobIfDone(job, slices, now);
                        job.SlicesJson = ExecutionJobSlices.Serialize(slices);
                        await PersistExecutionJobAsync(job, ct);
                        continue;
                    }
                }

                var order = new Order
                {
                    PositionId = job.PositionId, StrategyId = job.StrategyId, Symbol = _state.Symbol,
                    Side = job.Side, Type = OrderType.Market, Quantity = qty, Price = pos.CurrentPrice,
                    Status = OrderStatus.Pending, CreatedAtUtc = now, Mode = _state.Mode, MarketType = _state.MarketType,
                    Leverage = _state.MarketType == MarketType.Futures ? _state.Leverage : 1,
                };
                var strat = _active.FirstOrDefault(s => s.StrategyId == job.StrategyId);
                var filled = await ExecuteOpenAsync(order, strat?.StrategyName ?? job.StrategyId, pos.CurrentPrice, now, ct, isExisting: false, mergeInto: pos);

                if (filled)
                {
                    due.Status = "Filled";
                    due.ClientOrderId = order.ClientOrderId;
                    due.FilledQty = order.FilledQuantity;
                    due.FilledPrice = order.FilledPrice;
                    job.FilledQuantity += order.FilledQuantity ?? 0m;
                    job.EntryPriceWeightedAvg = pos.EntryPrice;
                    FinalizeJobIfDone(job, slices, now);
                }
                else if (now > job.CreatedAtUtc.AddSeconds(job.WindowSeconds + liveExecution.CurrentValue.AbandonGraceMinutes * 60))
                {
                    due.Status = "Abandoned";
                    await AuditAsync("ExecutionSliceAbandoned", new { job.Id, due.OffsetSeconds }, now, ct);
                    if (slices.All(s => s.Status is "Filled" or "MergedIntoNext" or "Abandoned"))
                    {
                        job.Status = "Failed"; job.CompletedAtUtc = now; job.FailureReason = "Fette residue non piazzabili entro finestra+grazia.";
                        _executionJobs.Remove(job);
                        metrics?.RecordExecutionJob(job.Algorithm, "Failed");
                        logger.LogError("ExecutionJob {Id} fallito: fette abbandonate dopo finestra+grazia.", job.Id);
                    }
                }
                // else: rifiutata ma dentro la grazia → resta Pending, ritentata al prossimo tick.

                job.SlicesJson = ExecutionJobSlices.Serialize(slices);
                await PersistExecutionJobAsync(job, ct);
            }
            await SaveStateAsync(ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Segna il job Completed se tutte le fette sono riempite/assorbite, rimuovendolo dalla cache.</summary>
    private void FinalizeJobIfDone(ExecutionJob job, List<ExecutionJobSlice> slices, DateTime now)
    {
        if (slices.All(s => s.Status is "Filled" or "MergedIntoNext"))
        {
            job.Status = "Completed";
            job.CompletedAtUtc = now;
            _executionJobs.Remove(job);
            metrics?.RecordExecutionJob(job.Algorithm, "Completed");

            // Implementation shortfall del job: prezzo medio ponderato realizzato vs prezzo di arrivo
            // (t0), segnato come costo per il lato dell'ordine — stessa convenzione di ExecutionSimulator.
            // Saltato se ArrivalPrice non è disponibile (job sopravvissuto a un riavvio, vedi ExecutionJob).
            if (job.ArrivalPrice > 0m && job.EntryPriceWeightedAvg > 0m)
            {
                var sign = job.Side == OrderSide.Buy ? 1m : -1m;
                var bps = sign * (job.EntryPriceWeightedAvg - job.ArrivalPrice) / job.ArrivalPrice * 10_000m;
                metrics?.RecordExecutionSlippage((double)bps, job.Algorithm);
            }
        }
    }

    // ---------------------------------------------------------------- conferma ordini Live

    public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => GetPendingInternalAsync(ct);

    private Task<List<Order>> GetPendingInternalAsync(CancellationToken ct) => Persistence.GetPendingOrdersAsync(ct);

    public async Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            Order? order;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.LaneId == laneId && o.OrderId == orderId && o.Status == OrderStatus.Pending, ct);
            }
            if (order is null) return;

            order.ManuallyConfirmed = true;
            var ts = DateTime.UtcNow;
            var price = _buffer.Count > 0 ? _buffer[^1].Close : (order.Price ?? 0m);
            await AuditAsync("OrderConfirmed", new { order.ClientOrderId, userId }, ts, ct);
            var confirmStrat = _active.FirstOrDefault(s => s.StrategyId == order.StrategyId);
            await TryBuildAndStartExecutionPlanAsync(order, confirmStrat, order.StrategyId, price, ts, ct, isExisting: true);
            await SaveStateAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var order = await db.Orders.FirstOrDefaultAsync(o => o.LaneId == laneId && o.OrderId == orderId && o.Status == OrderStatus.Pending, ct);
            if (order is null) return;
            order.Status = OrderStatus.Cancelled;
            order.ErrorMessage = "Rifiutato dall'operatore.";
            await db.SaveChangesAsync(ct);
            await AuditAsync("OrderRejectedByOperator", new { order.ClientOrderId, userId }, DateTime.UtcNow, ct);
        }
        finally { _gate.Release(); }
    }

    private Task SaveOrderAsync(Order order, bool isExisting, CancellationToken ct) => Persistence.SaveOrderAsync(order, isExisting, ct);

    private async Task ClosePositionAsync(OpenPosition pos, decimal exitPrice, string reason, DateTime ts, CancellationToken ct, bool alreadyClosedOnExchange = false)
    {
        if (!_positions.Contains(pos)) return;

        // Se un piano di esecuzione a fette è ancora in corso su questa posizione, va annullato PRIMA
        // di chiuderla: qualunque path di chiusura (SL/TP/trailing/liquidazione/segnale/manuale/
        // emergency/reconcile) converge qui, quindi una sola guardia li copre tutti — evita che il
        // worker, al tick dopo, riapra una posizione che la strategia non ha deciso di aprire.
        var job = _executionJobs.FirstOrDefault(j => j.PositionId == pos.PositionId && j.Status == "Running");
        if (job is not null)
        {
            job.Status = "Cancelled";
            job.CompletedAtUtc = ts;
            _executionJobs.Remove(job);
            await PersistExecutionJobAsync(job, ct);
            metrics?.RecordExecutionJob(job.Algorithm, "Cancelled");
            await AuditAsync("ExecutionJobCancelled", new { job.Id, job.PositionId, reason }, ts, ct);
        }

        if (_state.MarketType == MarketType.Futures)
        {
            await CloseFuturesPositionAsync(pos, exitPrice, reason, ts, ct, alreadyClosedOnExchange);
        }
        else
        {
            await CloseSpotPositionAsync(pos, exitPrice, reason, ts, ct);
        }

        // La chiusura può rientrare senza chiudere (rete incerta / rifiuto exchange): la posizione
        // resta in _positions. Registro il trade di chiusura SOLO se la posizione è stata rimossa.
        if (!_positions.Contains(pos))
            metrics?.RecordTradeExecuted(_state.Mode.ToString(), pos.Side.ToString(), "Close");
    }

    private PositionCloser PositionCloser => new(exchangeFactory, logger, Persistence, safety);

    /// <summary>Chiusura SPOT (comportamento INVARIATO rispetto a prima dell'introduzione dei Futures).</summary>
    private Task CloseSpotPositionAsync(OpenPosition pos, decimal exitPrice, string reason, DateTime ts, CancellationToken ct) =>
        PositionCloser.CloseSpotPositionAsync(_state, _positions, _creds, FeeFrac, pos, exitPrice, reason, ts, ct);

    /// <summary>
    /// Chiusura FUTURES: ordine reduceOnly opposto (salvo <paramref name="alreadyClosedOnExchange"/>,
    /// usato dalla riconciliazione quando l'exchange ha già liquidato/chiuso la posizione), rimborso
    /// del margine isolato (non del nozionale) + PnL, PnL% calcolata sul margine.
    /// </summary>
    private Task CloseFuturesPositionAsync(OpenPosition pos, decimal exitPrice, string reason, DateTime ts, CancellationToken ct, bool alreadyClosedOnExchange) =>
        PositionCloser.CloseFuturesPositionAsync(_state, _positions, _creds, FeeFrac, pos, exitPrice, reason, ts, ct, alreadyClosedOnExchange);

    /// <summary>
    /// Ogni candela (solo Futures, Testnet/Live), verifica sull'exchange che le posizioni locali
    /// siano ancora aperte. L'exchange può liquidare/chiudere una posizione indipendentemente dal
    /// ciclo del motore (es. liquidazione forzata più rapida del prossimo controllo locale): se
    /// risulta flat lato exchange ma aperta localmente, la chiudiamo qui con il miglior prezzo
    /// noto (stesso approccio "honest fallback" di EmergencyInternalAsync).
    /// </summary>
    private async Task ReconcileFuturesPositionsAsync(decimal lastKnownPrice, DateTime ts, CancellationToken ct)
    {
        _untrackedRemoteAlerted = await new FuturesPositionReconciler(exchangeFactory, logger, Persistence)
            .ReconcileAsync(_state, _positions, _creds, ClosePositionAsync, _untrackedRemoteAlerted, lastKnownPrice, ts, ct);
    }

    // ---------------------------------------------------------------- queries

    public async Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var price = _buffer.Count > 0 ? _buffer[^1].Close : 0m;
            var equity = ComputeEquity(price);
            var dd = _state.PeakEquity > 0m ? (_state.PeakEquity - equity) / _state.PeakEquity * 100m : 0m;

            int total; decimal winRate;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                total = await db.TradeRecords.CountAsync(t => t.LaneId == laneId, ct);
                var wins = await db.TradeRecords.CountAsync(t => t.LaneId == laneId && t.Pnl > 0m, ct);
                winRate = total > 0 ? (decimal)wins / total * 100m : 0m;
            }

            return new TradingEngineStatus
            {
                Mode = _state.Mode,
                MarketType = _state.MarketType,
                Leverage = _state.Leverage,
                IsRunning = _state.IsRunning,
                ExchangeName = _state.ExchangeName,
                Symbol = _state.Symbol,
                TotalCapital = _state.TotalCapital,
                AvailableCapital = _state.AvailableCapital,
                UsedCapital = _state.MarketType == MarketType.Futures
                    ? _positions.Sum(p => p.MarginBalance)
                    : _positions.Sum(p => p.Quantity * p.EntryPrice),
                TotalPnl = equity - _state.TotalCapital,
                TotalPnlPercent = _state.TotalCapital > 0m ? (equity - _state.TotalCapital) / _state.TotalCapital * 100m : 0m,
                DailyPnl = _state.DailyPnl,
                MaxDrawdown = dd,
                TotalTrades = total,
                OpenPositionCount = _positions.Count,
                WinRate = winRate,
                StartedAtUtc = _state.StartedAtUtc,
                LastOrderUtc = _state.LastOrderUtc,
                IsEmergencyStopped = _state.IsEmergencyStopped,
                EmergencyStopReason = _state.EmergencyStopReason,
            };
        }
        finally { _gate.Release(); }
    }

    public async Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await EnsureLoadedAsync(ct); return _positions.ToList(); }
        finally { _gate.Release(); }
    }

    public async Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var pos = _positions.FirstOrDefault(p => p.PositionId == positionId);
            if (pos is null) return;
            pos.StopLoss = stopLoss is > 0m ? stopLoss : null;
            pos.TakeProfit = takeProfit is > 0m ? takeProfit : null;

            // Un nuovo/cambiato trailing % riparte dal prezzo corrente: usare il vecchio
            // BestPriceSinceEntry (calcolato per una % diversa) produrrebbe un livello iniziale
            // incoerente con quanto l'operatore si aspetta impostandolo ora.
            var newTrailing = trailingStopPercent is > 0m ? trailingStopPercent : null;
            if (newTrailing != pos.TrailingStopPercent)
            {
                pos.BestPriceSinceEntry = pos.CurrentPrice > 0m ? pos.CurrentPrice : pos.EntryPrice;
            }
            pos.TrailingStopPercent = newTrailing;

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.OpenPositions.FirstOrDefaultAsync(p => p.LaneId == laneId && p.PositionId == positionId, ct);
            if (row is not null)
            {
                row.StopLoss = pos.StopLoss;
                row.TakeProfit = pos.TakeProfit;
                row.TrailingStopPercent = pos.TrailingStopPercent;
                row.BestPriceSinceEntry = pos.BestPriceSinceEntry;
                await db.SaveChangesAsync(ct);
            }
            await AuditAsync("EditStopLossTakeProfit",
                new { positionId, stopLoss = pos.StopLoss, takeProfit = pos.TakeProfit, trailingStopPercent = pos.TrailingStopPercent },
                DateTime.UtcNow, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task ClosePositionAsync(string positionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var pos = _positions.FirstOrDefault(p => p.PositionId == positionId);
            if (pos is null) return;
            var price = pos.CurrentPrice > 0m ? pos.CurrentPrice : pos.EntryPrice;
            await ClosePositionAsync(pos, price, "Manual", DateTime.UtcNow, ct);
            await SaveStateAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default)
    {
        // Criterio in TradingOrderQueries, condiviso col client remoto: mai due copie che derivano.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await TradingOrderQueries.History(db.Orders, laneId, from).ToListAsync(ct);
    }

    public async Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)
    {
        List<TradeRecord> trades;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var q = db.TradeRecords.Where(t => t.LaneId == laneId);
            if (from is DateTime f) q = q.Where(t => t.ClosedAtUtc >= f);
            trades = await q.OrderBy(t => t.ClosedAtUtc).ToListAsync(ct);
        }

        var wins = trades.Where(t => t.Pnl > 0m).ToList();
        var losses = trades.Where(t => t.Pnl < 0m).ToList();
        var grossWin = wins.Sum(t => t.Pnl);
        var grossLoss = Math.Abs(losses.Sum(t => t.Pnl));

        // Snapshot ATOMICO di curva e stato sotto il gate — poi si calcola fuori. Prima si leggeva
        // _equity viva in TRE punti (ToList, SharpeRatio, MaxDrawdown) mentre ProcessCandleAsync
        // poteva farci Add sotto il SUO gate: una collisione lancia ("collection modified") o copia
        // una curva strappata. In locale l'eccezione moriva nel catch del refresh della UI; in
        // remoto diventava una RpcException(Unknown) sul filo — banner "dati non aggiornati" per un
        // falso allarme e un ciclo di PromotionEvaluator saltato. Il gate è tenuto per il tempo di
        // una copia (≤10k elementi, vedi TrimEquity), non per i calcoli: nessun impatto percepibile
        // sul TradingWorker. Bonus: curva restituita e metriche calcolate sono ORA lo stesso
        // istante, prima potevano differire di qualche candela fra loro.
        List<EquityPoint> equity;
        decimal totalCapital, realizedPnl, sessionMaxDrawdown;
        string timeframe;
        await _gate.WaitAsync(ct);
        try
        {
            equity = _equity.ToList();
            totalCapital = _state.TotalCapital;
            realizedPnl = _state.RealizedPnl;
            sessionMaxDrawdown = _state.MaxDrawdownPercent;
            timeframe = _state.Timeframe;
        }
        finally { _gate.Release(); }

        var ppy = Statistics.PeriodsPerYear(timeframe);

        return new TradingPerformance
        {
            EquityCurve = equity,
            TotalReturn = totalCapital > 0m ? realizedPnl / totalCapital * 100m : 0m,
            // Sharpe calcolato sulla FINESTRA ritenuta della curva (bounded, vedi TrimEquity):
            // per una metrica di promozione la storia recente è quella che conta. Il MaxDrawdown
            // invece è il PEGGIORE tra ricalcolo locale e valore di sessione persistito — un
            // riavvio (curva vuota) o il trim non possono più "amnesiare" un drawdown già subito.
            SharpeRatio = Statistics.SharpeRatio(equity, ppy),
            MaxDrawdown = Math.Max(sessionMaxDrawdown, MaxDrawdown(equity)),
            TotalTrades = trades.Count,
            WinRate = trades.Count > 0 ? (decimal)wins.Count / trades.Count * 100m : 0m,
            AverageWin = wins.Count > 0 ? wins.Average(t => t.Pnl) : 0m,
            AverageLoss = losses.Count > 0 ? losses.Average(t => t.Pnl) : 0m,
            ProfitFactor = grossLoss > 0m ? grossWin / grossLoss : 0m,
            // P3-12: le metriche sopra usano già la lista COMPLETA (trades.Count, wins, losses) —
            // solo la lista esposta è tagliata, ai 500 più recenti (query ordinata ascendente per
            // ClosedAtUtc, quindi TakeLast = i più recenti). Nessun consumer oggi legge questo campo
            // (Trading.razor usa EquityCurve, PromotionEvaluator le metriche aggregate — vedi i loro
            // commenti), ma un domani che lo legga trova comunque uno storico recente utile, non un
            // payload che su una lane longeva era arrivato a giustificare un tetto gRPC a 64MB.
            Trades = trades.TakeLast(500).ToList(),
        };
    }

    // ---------------------------------------------------------------- helpers

    private static void MarkToMarket(OpenPosition pos, decimal price)
    {
        pos.CurrentPrice = price;
        pos.UnrealizedPnl = (pos.Side == OrderSide.Buy ? price - pos.EntryPrice : pos.EntryPrice - price) * pos.Quantity;
        var notional = pos.Quantity * pos.EntryPrice;
        pos.UnrealizedPnlPercent = notional > 0m ? pos.UnrealizedPnl / notional * 100m : 0m;
    }

    private decimal ComputeEquity(decimal price)
    {
        var eq = _state.AvailableCapital;
        foreach (var pos in _positions)
        {
            // Dopo un riavvio il buffer può essere vuoto e il chiamante passa 0: si ricade
            // sull'ultimo prezzo noto della posizione, mai su un mark-to-market a prezzo nullo.
            var mark = price > 0m ? price : (pos.CurrentPrice > 0m ? pos.CurrentPrice : pos.EntryPrice);
            if (_state.MarketType == MarketType.Futures)
            {
                // Margine ISOLATO: l'apertura sottrae solo margine+fee e la chiusura restituisce
                // margine+PnL, quindi l'equity è disponibile + margine bloccato + PnL non
                // realizzato — MAI il nozionale (±qty·prezzo è il modello di cassa dello Spot:
                // applicato a uno short leveraged renderebbe l'equity profondamente negativa e
                // farebbe scattare un falso MaxDrawdown emergency stop alla candela di apertura).
                var upnl = (pos.Side == OrderSide.Buy ? mark - pos.EntryPrice : pos.EntryPrice - mark) * pos.Quantity;
                eq += pos.MarginBalance + upnl;
            }
            else
            {
                eq += pos.Side == OrderSide.Buy ? pos.Quantity * mark : -pos.Quantity * mark;
            }
        }
        return eq;
    }

    private TradingEngineStatus BuildSafetyStatus(decimal price)
    {
        var equity = ComputeEquity(price);
        var dd = _state.PeakEquity > 0m ? (_state.PeakEquity - equity) / _state.PeakEquity * 100m : 0m;
        return new TradingEngineStatus
        {
            Mode = _state.Mode,
            MarketType = _state.MarketType,
            Leverage = _state.Leverage,
            TotalCapital = _state.TotalCapital,
            // Futures: margine bloccato (non nozionale). NB: SafetyChecker.MaxTotalExposurePercent
            // somma questo a order.Notional (leveraged) per il nuovo ordine — asimmetria
            // volutamente conservativa (fa scattare il limite prima, mai dopo), non un bug.
            UsedCapital = _state.MarketType == MarketType.Futures
                ? _positions.Sum(p => p.MarginBalance)
                : _positions.Sum(p => p.Quantity * p.EntryPrice),
            DailyPnl = _state.DailyPnl,
            MaxDrawdown = dd,
            OpenPositionCount = _positions.Count,
            LastOrderUtc = _state.LastOrderUtc,
            IsEmergencyStopped = _state.IsEmergencyStopped,
            EmergencyStopReason = _state.EmergencyStopReason,
        };
    }

    private static decimal MaxDrawdown(List<EquityPoint> curve)
    {
        decimal peak = decimal.MinValue, maxDd = 0m;
        foreach (var p in curve)
        {
            if (p.Capital > peak) peak = p.Capital;
            if (peak > 0m) { var d = (peak - p.Capital) / peak * 100m; if (d > maxDd) maxDd = d; }
        }
        return maxDd;
    }

    /// <summary>
    /// [M1] Ritenzione bounded della curva equity in-memory: oltre <paramref name="maxPoints"/>
    /// punti si scarta il BLOCCO più vecchio (una RemoveRange ogni <paramref name="trimBlock"/>
    /// candele, non una RemoveAt per candela). A 5m sono ~34 giorni di storia: abbastanza per
    /// Sharpe/drawdown recenti; il MaxDrawdown di sessione resta comunque esatto perché è
    /// tracciato incrementalmente in <see cref="TradingEngineState.MaxDrawdownPercent"/>.
    /// </summary>
    internal static void TrimEquity(List<EquityPoint> curve, int maxPoints = 10_000, int trimBlock = 2_000)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (curve.Count > maxPoints)
        {
            curve.RemoveRange(0, Math.Min(trimBlock, curve.Count - 1));
        }
    }

    // ---------------------------------------------------------------- persistenza

    private async Task SaveStateAsync(CancellationToken ct)
    {
        _state.LaneId = laneId;
        _state.UpdatedAtUtc = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.TradingEngineStates.Where(s => s.LaneId == laneId).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            db.TradingEngineStates.Add(_state);
        }
        else
        {
            _state.Id = row.Id;
            db.Entry(row).CurrentValues.SetValues(_state);
        }
        await db.SaveChangesAsync(ct);
    }

    private Task PersistOrderAsync(Order order, CancellationToken ct) => Persistence.PersistOrderAsync(order, ct);

    private Task PersistNewPositionAsync(OpenPosition pos, CancellationToken ct) => Persistence.PersistNewPositionAsync(pos, ct);

    /// <summary>Aggiorna la riga di una posizione ESISTENTE dopo un fill fuso (media ponderata di una fetta).</summary>
    private Task UpdatePositionRowAsync(OpenPosition pos, CancellationToken ct) => Persistence.UpdatePositionRowAsync(pos, ct);

    /// <summary>Inserisce o aggiorna la riga di un ExecutionJob (idempotente per Id).</summary>
    private Task PersistExecutionJobAsync(ExecutionJob job, CancellationToken ct) => Persistence.PersistExecutionJobAsync(job, ct);

    private Task RemovePositionAsync(OpenPosition pos, CancellationToken ct) => Persistence.RemovePositionAsync(pos, ct);

    private Task PersistTradeAsync(TradeRecord trade, CancellationToken ct) => Persistence.PersistTradeAsync(trade, ct);

    private Task AuditAsync(string action, object details, DateTime ts, CancellationToken ct) => Persistence.AuditAsync(action, details, _state.Mode, ts, ct);

    /// <summary>
    /// Carica le credenziali firmate dell'exchange, decifrate RIGA PER RIGA (bug B2): una riga
    /// cifrata con una master key diversa non deve abbattere l'avvio con una
    /// AuthenticationTagMismatchException grezza, ma produrre un errore che spiega il rimedio.
    /// Se accanto alla riga indecifrabile ne esiste una decifrabile (credenziali reinserite dopo
    /// il cambio chiave), si usa quella.
    /// </summary>
    private async Task<TradingCredentials?> LoadCredentialsAsync(string exchangeName, bool testnet, CancellationToken ct)
    {
        if (!Enum.TryParse<ExchangeName>(exchangeName, out var ex))
        {
            return null;
        }

        if (credentialReader is not null)
        {
            var found = await credentialReader.FindForTradingAsync(ex, testnet, ct);
            if (found is null)
            {
                return null;
            }
            if (!found.IsDecryptable)
            {
                throw CredentialsUndecryptable(exchangeName, testnet, found.Label);
            }
            return new TradingCredentials(found.ApiKey!, found.ApiSecret!, found.Passphrase, testnet);
        }

        // Fallback senza reader (vecchi harness di test che costruiscono il motore a mano): la
        // decifratura avviene nel converter EF durante la materializzazione — un fallimento
        // crypto va comunque tradotto nello stesso errore chiaro, mai propagato grezzo.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        try
        {
            var cred = await db.ExchangeCredentials.FirstOrDefaultAsync(c => c.ExchangeName == ex && c.IsTestnet == testnet, ct);
            return cred is null ? null : new TradingCredentials(cred.ApiKey, cred.ApiSecret, cred.Passphrase, testnet);
        }
        catch (Exception e) when (IsDecryptFailure(e))
        {
            throw CredentialsUndecryptable(exchangeName, testnet, label: null);
        }
    }

    /// <summary>EF può propagare l'errore del converter sia diretto sia wrappato: si scorre tutta la catena.</summary>
    private static bool IsDecryptFailure(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Cryptography.CryptographicException or FormatException)
            {
                return true;
            }
        }
        return false;
    }

    private static InvalidOperationException CredentialsUndecryptable(string exchangeName, bool testnet, string? label) => new(
        $"Credenziale {exchangeName} ({(testnet ? "testnet" : "live")}){(label is null ? "" : $" '{label}'")} presente ma " +
        "NON decifrabile con la master key corrente: fu cifrata con una Security:MasterKey/PROCIONE_MGR_MASTER_KEY diversa. " +
        "Reinserisci le credenziali in /settings/exchanges (o ripristina la chiave originale) e riavvia la corsia.");
}
