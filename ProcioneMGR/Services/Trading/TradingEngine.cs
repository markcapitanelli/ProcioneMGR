using System.Text.Json;
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
    IFactorCache? factorCache = null) : ITradingEngine
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

    private const decimal FeePercent = 0.1m;
    private const int BufferSize = 400;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new();

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

    private decimal FeeFrac => FeePercent / 100m;

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
            var cfg = await ensemble.GetConfigurationAsync(ct);
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
                _creds = await LoadCredentialsAsync(cfg.ExchangeName, testnet, ct);
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
            foreach (var pos in _positions.ToList())
            {
                MarkToMarket(pos, price);

                // Liquidazione: controllata su High/Low della candela (un wick intrabar può
                // toccare il prezzo di liquidazione anche se la Close resta al sicuro) — a
                // differenza di stop loss/take profit sotto, dove la semplificazione "solo
                // Close" è preesistente e qui non viene toccata.
                if (_state.MarketType == MarketType.Futures && pos.LiquidationPrice is decimal liq && liq > 0m)
                {
                    var hit = pos.Side == OrderSide.Buy ? candle.Low <= liq : candle.High >= liq;
                    if (hit)
                    {
                        await ClosePositionAsync(pos, liq, "Liquidation", ts, ct);
                        continue;
                    }
                }

                // Stop/target INTRABAR su High/Low della candela chiusa (come la liquidazione sopra e
                // come il motore di backtest), NON solo sulla Close: un wick può bucare lo stop anche
                // se la candela chiude al di là — prima questa asimmetria rendeva il live più ottimista
                // del backtest. Esecuzione al LIVELLO dello stop/target (o all'open se la candela apre
                // già oltre, per un gap), esito peggiore per la posizione.
                var high = candle.High;
                var low = candle.Low;

                // Trailing: livello calcolato sul best-since-entry delle candele PRECEDENTI (causale,
                // come il backtest); il best si aggiorna con QUESTA candela solo dopo il controllo.
                var effectiveStop = pos.StopLoss;
                if (pos.TrailingStopPercent is decimal trailPct && trailPct > 0m)
                {
                    var best = pos.BestPriceSinceEntry ?? pos.EntryPrice;
                    var trailLevel = pos.Side == OrderSide.Buy
                        ? best * (1m - trailPct / 100m)
                        : best * (1m + trailPct / 100m);
                    if (effectiveStop is null
                        || (pos.Side == OrderSide.Buy && trailLevel > effectiveStop.Value)
                        || (pos.Side == OrderSide.Sell && trailLevel < effectiveStop.Value))
                    {
                        effectiveStop = trailLevel;
                    }
                }

                // Stop PRIMA del target: se entrambi cadono nella stessa candela si assume lo stop.
                if (effectiveStop is decimal sl
                    && ((pos.Side == OrderSide.Buy && low <= sl) || (pos.Side == OrderSide.Sell && high >= sl)))
                {
                    var fill = pos.Side == OrderSide.Buy ? Math.Min(sl, candle.Open) : Math.Max(sl, candle.Open);
                    await ClosePositionAsync(pos, fill, "StopLoss", ts, ct);
                }
                else if (pos.TakeProfit is decimal tp
                    && ((pos.Side == OrderSide.Buy && high >= tp) || (pos.Side == OrderSide.Sell && low <= tp)))
                {
                    var fill = pos.Side == OrderSide.Buy ? Math.Max(tp, candle.Open) : Math.Min(tp, candle.Open);
                    await ClosePositionAsync(pos, fill, "TakeProfit", ts, ct);
                }
                else if (pos.TrailingStopPercent is > 0m)
                {
                    pos.BestPriceSinceEntry = pos.Side == OrderSide.Buy
                        ? Math.Max(pos.BestPriceSinceEntry ?? pos.EntryPrice, high)
                        : Math.Min(pos.BestPriceSinceEntry ?? pos.EntryPrice, low);
                }
            }

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

    // ---------------------------------------------------------------- open / close

    private async Task TryOpenAsync(EnsembleStrategy strat, OrderSide side, decimal price, DateTime ts, CancellationToken ct)
    {
        if (price <= 0m) return;

        // Spot: PositionSizePercent è il nozionale investito (leva implicita 1x).
        // Futures: PositionSizePercent è il MARGINE isolato; il nozionale (e quindi
        // l'esposizione reale) è margine × leva — stessa logica del motore di backtest.
        // La coerenza con MaxPositionSizePercent/MaxTotalExposurePercent è validata a StartAsync.
        var margin = _state.TotalCapital * safety.CurrentValue.PositionSizePercent / 100m;
        var notional = _state.MarketType == MarketType.Futures ? margin * _state.Leverage : margin;
        var qty = notional / price;

        // Arrotonda al LOT_SIZE reale del simbolo (da exchangeInfo) per Testnet/Live;
        // in Paper usa una precisione fissa ragionevole.
        if (_state.Mode != TradingMode.Paper && _filters is not null)
        {
            qty = _filters.RoundQuantity(qty);
            if (!_filters.IsTradable(qty, price))
            {
                logger.LogWarning("Ordine sotto i minimi del simbolo (qty {Qty}, notional {N}): saltato.", qty, qty * price);
                return;
            }
        }
        else
        {
            qty = Math.Round(qty, 5, MidpointRounding.ToZero);
        }
        if (qty <= 0m) return;

        var order = new Order
        {
            PositionId = Guid.NewGuid().ToString("N"),
            StrategyId = strat.StrategyId,
            Symbol = _state.Symbol,
            Side = side,
            Type = OrderType.Market,
            Quantity = qty,
            Price = price,
            Status = OrderStatus.Pending,
            CreatedAtUtc = ts,
            Mode = _state.Mode,
            MarketType = _state.MarketType,
            Leverage = _state.MarketType == MarketType.Futures ? _state.Leverage : 1,
        };

        // Live: l'apertura richiede conferma manuale dell'operatore -> resta Pending in coda.
        if (_state.Mode == TradingMode.Live && safety.CurrentValue.RequireManualConfirmationForLive)
        {
            // Un solo ordine in coda per strategia (niente duplicati se non si conferma subito).
            var pending = await GetPendingInternalAsync(ct);
            if (pending.Any(o => o.StrategyId == strat.StrategyId))
            {
                return;
            }
            await PersistOrderAsync(order, ct);
            await AuditAsync("PendingConfirmation",
                new { order.ClientOrderId, strat.StrategyName, side = side.ToString(), qty, price }, ts, ct);
            logger.LogInformation("Ordine Live {Cid} in attesa di conferma manuale.", order.ClientOrderId);
            return;
        }

        await TryBuildAndStartExecutionPlanAsync(order, strat, strat.StrategyName, price, ts, ct, isExisting: false);
    }

    /// <summary>
    /// Applica automaticamente lo stop-loss/take-profit/trailing validati nel backtest (se
    /// configurati sulla <see cref="EnsembleStrategy"/> di questo ordine) alla posizione appena
    /// aperta. Gira SOLO qui, una volta, alla creazione: nessun altro punto del motore rimette
    /// mano a questi valori, quindi una modifica manuale successiva da <c>/trading</c>
    /// (<see cref="SetStopLossTakeProfitAsync"/>) resta sempre l'ultima parola per quella
    /// posizione — non serve un controllo esplicito di "già impostato", perché a questo punto
    /// la posizione non esiste ancora.
    /// </summary>
    private void ApplyAutoStops(OpenPosition pos, Order order)
    {
        var strat = _active.FirstOrDefault(s => s.StrategyId == order.StrategyId);
        if (strat is null) return;

        if (strat.StopLossPercent is decimal slPct && slPct > 0m)
        {
            pos.StopLoss = pos.Side == OrderSide.Buy
                ? pos.EntryPrice * (1m - slPct / 100m)
                : pos.EntryPrice * (1m + slPct / 100m);
        }
        if (strat.TakeProfitPercent is decimal tpPct && tpPct > 0m)
        {
            pos.TakeProfit = pos.Side == OrderSide.Buy
                ? pos.EntryPrice * (1m + tpPct / 100m)
                : pos.EntryPrice * (1m - tpPct / 100m);
        }
        if (strat.TrailingStopPercent is decimal trailPct && trailPct > 0m)
        {
            pos.TrailingStopPercent = trailPct;
            pos.BestPriceSinceEntry = pos.EntryPrice;
        }
    }

    // ---------------------------------------------------------------- riconciliazione ordini incerti

    private enum ReconcileStatus { Filled, NotFound, TerminalUnfilled, Uncertain }

    private sealed record ReconcileOutcome(ReconcileStatus Status, decimal? FillPrice, decimal? FillQty, string? ExchangeOrderId);

    /// <summary>
    /// Riconcilia un ordine MARKET dall'esito di rete incerto interrogando lo STATO per
    /// clientOrderId (fino a 3 tentativi, pausa 2s). GetOpenOrders NON basta: un MARKET riempito
    /// durante il blip non è tra gli ordini "aperti" e verrebbe scambiato per "mai piazzato" —
    /// risultato: posizione reale non tracciata (nessuno stop la gestisce) E ordine duplicato alla
    /// candela successiva. Se l'ordine risulta ancora vivo viene CANCELLATO e ricontrollato, così
    /// non può riempirsi "alle nostre spalle" dopo che lo abbiamo dichiarato assente.
    /// </summary>
    private async Task<ReconcileOutcome> ReconcileUncertainOrderAsync(
        string symbol, string clientOrderId, bool futures, TradingCredentials creds, CancellationToken ct)
    {
        var spotClient = futures ? null : exchangeFactory.Create(_state.ExchangeName);
        var futuresClient = futures ? exchangeFactory.CreateFutures(_state.ExchangeName) : null;

        Task<OrderStatusResult> LookupAsync() => futures
            ? futuresClient!.GetFuturesOrderStatusAsync(symbol, clientOrderId, creds, ct)
            : spotClient!.GetOrderStatusAsync(symbol, clientOrderId, creds, ct);

        Task<CancelOrderResult> CancelAsync() => futures
            ? futuresClient!.CancelFuturesOrderAsync(symbol, clientOrderId, creds, ct)
            : spotClient!.CancelOrderAsync(symbol, clientOrderId, creds, ct);

        static bool HasFill(OrderStatusResult s) =>
            s.Status is "Filled" or "PartiallyFilled" && s.FilledQuantity is > 0m;

        const int MaxAttempts = 3;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var status = await LookupAsync();

            if (status.NetworkUncertain)
            {
                if (attempt < MaxAttempts) await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            if (!status.Found)
            {
                return new ReconcileOutcome(ReconcileStatus.NotFound, null, null, null);
            }
            if (HasFill(status))
            {
                return new ReconcileOutcome(ReconcileStatus.Filled, status.FilledPrice, status.FilledQuantity, status.ExchangeOrderId);
            }
            if (status.IsTerminalUnfilled)
            {
                return new ReconcileOutcome(ReconcileStatus.TerminalUnfilled, null, null, null);
            }

            // Ancora vivo: cancella per chiudere la finestra di duplicazione, poi un ultimo
            // lookup per catturare un fill avvenuto tra la query e la cancellazione.
            await CancelAsync();
            var after = await LookupAsync();
            if (!after.NetworkUncertain)
            {
                if (after.Found && HasFill(after))
                {
                    return new ReconcileOutcome(ReconcileStatus.Filled, after.FilledPrice, after.FilledQuantity, after.ExchangeOrderId);
                }
                return new ReconcileOutcome(ReconcileStatus.TerminalUnfilled, null, null, null);
            }
            if (attempt < MaxAttempts) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        // Stato ancora ignoto dopo tutti i tentativi: cancellazione best-effort per chiudere
        // comunque la finestra; il chiamante deve loggare CRITICAL per la verifica manuale.
        await CancelAsync();
        return new ReconcileOutcome(ReconcileStatus.Uncertain, null, null, null);
    }

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

    /// <summary>Apertura SPOT. mergeInto=null crea una nuova posizione (INVARIATO); non-null fonde il fill via media ponderata.</summary>
    private async Task<bool> ExecuteSpotOpenAsync(Order order, string strategyName, decimal currentPrice, DateTime ts, CancellationToken ct, bool isExisting, OpenPosition? mergeInto = null)
    {
        var side = order.Side;
        var qty = order.Quantity;

        var fillPrice = currentPrice;
        var fillQty = qty;
        string? exchangeOrderId = null;

        if (_state.Mode != TradingMode.Paper && _creds is TradingCredentials creds)
        {
            var client = exchangeFactory.Create(_state.ExchangeName);
            var res = await client.PlaceOrderAsync(new PlaceOrderRequest
            {
                Symbol = _state.Symbol,
                Side = side == OrderSide.Buy ? "BUY" : "SELL",
                Type = "MARKET",
                Quantity = qty,
                ClientOrderId = order.ClientOrderId,
                Credentials = creds,
            }, ct);

            if (res.NetworkUncertain)
            {
                var outcome = await ReconcileUncertainOrderAsync(_state.Symbol, order.ClientOrderId, futures: false, creds, ct);
                switch (outcome.Status)
                {
                    case ReconcileStatus.Filled:
                        logger.LogWarning("Ordine {Cid} riconciliato come ESEGUITO dopo errore di rete (fill {Price} x {Qty}).",
                            order.ClientOrderId, outcome.FillPrice, outcome.FillQty);
                        await AuditAsync("OrderReconciledFilled",
                            new { order.ClientOrderId, fillPrice = outcome.FillPrice, fillQty = outcome.FillQty }, ts, ct);
                        res = new PlaceOrderResult
                        {
                            Success = true,
                            FilledPrice = outcome.FillPrice,
                            FilledQuantity = outcome.FillQty,
                            ExchangeOrderId = outcome.ExchangeOrderId,
                        };
                        break;

                    case ReconcileStatus.Uncertain:
                        order.Status = OrderStatus.Rejected;
                        order.ErrorMessage = "Errore di rete: stato dell'ordine NON verificabile dopo la riconciliazione. " + res.Error;
                        await SaveOrderAsync(order, isExisting, ct);
                        await AuditAsync("OrderReconcileUncertain", new { order.ClientOrderId, res.Error }, ts, ct);
                        logger.LogCritical(
                            "Ordine {Cid}: stato NON verificabile dopo la riconciliazione (cancellazione best-effort inviata). VERIFICARE MANUALMENTE sull'exchange.",
                            order.ClientOrderId);
                        return false;

                    default: // NotFound / TerminalUnfilled: sicuro ritentare alla prossima candela
                        order.Status = OrderStatus.Rejected;
                        order.ErrorMessage = "Errore di rete: ordine NON riscontrato sull'exchange. " + res.Error;
                        await SaveOrderAsync(order, isExisting, ct);
                        await AuditAsync("OrderRejected", new
                        {
                            order.ClientOrderId,
                            reason = outcome.Status == ReconcileStatus.NotFound ? "network-uncertain-not-found" : "network-uncertain-terminal",
                            res.Error,
                        }, ts, ct);
                        return false;
                }
            }
            else if (!res.Success)
            {
                order.Status = OrderStatus.Rejected;
                order.ErrorMessage = res.Error;
                await SaveOrderAsync(order, isExisting, ct);
                await AuditAsync("OrderRejected", new { order.ClientOrderId, res.Error }, ts, ct);
                return false;
            }

            fillPrice = res.FilledPrice ?? currentPrice;
            fillQty = res.FilledQuantity ?? qty;
            exchangeOrderId = res.ExchangeOrderId;
        }

        var realNotional = fillQty * fillPrice;
        var fee = realNotional * FeeFrac;
        if (side == OrderSide.Buy) _state.AvailableCapital -= realNotional + fee;
        else _state.AvailableCapital += realNotional - fee;

        order.Status = OrderStatus.Filled;
        order.FilledPrice = fillPrice;
        order.FilledQuantity = fillQty;
        order.FilledAtUtc = ts;
        order.ExchangeOrderId = exchangeOrderId;
        metrics?.RecordTradeExecuted(_state.Mode.ToString(), side.ToString(), "Open");

        OpenPosition pos;
        if (mergeInto is null)
        {
            pos = new OpenPosition
            {
                PositionId = order.PositionId,
                StrategyId = order.StrategyId,
                Symbol = _state.Symbol,
                Side = side,
                EntryPrice = fillPrice,
                Quantity = fillQty,
                OpenedAtUtc = ts,
                CurrentPrice = fillPrice,
                ExchangeOrderId = exchangeOrderId,
                OpenedInMode = _state.Mode,
                Leverage = 1,
                MarginBalance = realNotional,
            };
            ApplyAutoStops(pos, order);   // SOLO alla creazione — mai su merge
            _positions.Add(pos);
        }
        else
        {
            pos = mergeInto;
            var newQty = pos.Quantity + fillQty;
            pos.EntryPrice = newQty > 0m ? (pos.Quantity * pos.EntryPrice + fillQty * fillPrice) / newQty : fillPrice;
            pos.Quantity = newQty;
            pos.MarginBalance += realNotional;
            pos.CurrentPrice = fillPrice;
            pos.ExchangeOrderId = exchangeOrderId;
            // StopLoss/TakeProfit/TrailingStopPercent NON toccati: restano quelli della 1ª fetta.
        }
        _state.LastOrderUtc = ts;

        await SaveOrderAsync(order, isExisting, ct);
        if (mergeInto is null) await PersistNewPositionAsync(pos, ct); else await UpdatePositionRowAsync(pos, ct);
        await AuditAsync("PlaceOrder", new
        {
            order.ClientOrderId, strategyName, side = side.ToString(), qty = fillQty, price = fillPrice, merged = mergeInto is not null,
            autoStopLoss = pos.StopLoss, autoTakeProfit = pos.TakeProfit, autoTrailingStopPercent = pos.TrailingStopPercent,
        }, ts, ct);
        return true;
    }

    /// <summary>
    /// Apertura FUTURES: margine ISOLATO (solo il margine viene sottratto ad AvailableCapital,
    /// non l'intero nozionale leveraged), prezzo di liquidazione dalla fonte di verità
    /// dell'exchange (con fallback alla stima locale <see cref="MarginMath"/>).
    /// </summary>
    private async Task<bool> ExecuteFuturesOpenAsync(Order order, string strategyName, decimal currentPrice, DateTime ts, CancellationToken ct, bool isExisting, OpenPosition? mergeInto = null)
    {
        var side = order.Side;
        var qty = order.Quantity;
        var leverage = Math.Max(1, order.Leverage);

        var fillPrice = currentPrice;
        var fillQty = qty;
        string? exchangeOrderId = null;
        decimal? liquidationPrice = null;

        if (_state.Mode != TradingMode.Paper && _creds is TradingCredentials creds)
        {
            var futuresClient = exchangeFactory.CreateFutures(_state.ExchangeName);
            var res = await futuresClient.PlaceFuturesOrderAsync(new PlaceOrderRequest
            {
                Symbol = _state.Symbol,
                Side = side == OrderSide.Buy ? "BUY" : "SELL",
                Type = "MARKET",
                Quantity = qty,
                ClientOrderId = order.ClientOrderId,
                Credentials = creds,
            }, reduceOnly: false, ct);

            if (res.NetworkUncertain)
            {
                var outcome = await ReconcileUncertainOrderAsync(_state.Symbol, order.ClientOrderId, futures: true, creds, ct);
                switch (outcome.Status)
                {
                    case ReconcileStatus.Filled:
                        logger.LogWarning("Ordine futures {Cid} riconciliato come ESEGUITO dopo errore di rete (fill {Price} x {Qty}).",
                            order.ClientOrderId, outcome.FillPrice, outcome.FillQty);
                        await AuditAsync("OrderReconciledFilled",
                            new { order.ClientOrderId, fillPrice = outcome.FillPrice, fillQty = outcome.FillQty }, ts, ct);
                        res = new PlaceOrderResult
                        {
                            Success = true,
                            FilledPrice = outcome.FillPrice,
                            FilledQuantity = outcome.FillQty,
                            ExchangeOrderId = outcome.ExchangeOrderId,
                        };
                        break;

                    case ReconcileStatus.Uncertain:
                        order.Status = OrderStatus.Rejected;
                        order.ErrorMessage = "Errore di rete: stato dell'ordine futures NON verificabile dopo la riconciliazione. " + res.Error;
                        await SaveOrderAsync(order, isExisting, ct);
                        await AuditAsync("OrderReconcileUncertain", new { order.ClientOrderId, res.Error }, ts, ct);
                        logger.LogCritical(
                            "Ordine futures {Cid}: stato NON verificabile dopo la riconciliazione (cancellazione best-effort inviata). VERIFICARE MANUALMENTE sull'exchange.",
                            order.ClientOrderId);
                        return false;

                    default: // NotFound / TerminalUnfilled: sicuro ritentare alla prossima candela
                        order.Status = OrderStatus.Rejected;
                        order.ErrorMessage = "Errore di rete: ordine futures NON riscontrato sull'exchange. " + res.Error;
                        await SaveOrderAsync(order, isExisting, ct);
                        await AuditAsync("OrderRejected", new
                        {
                            order.ClientOrderId,
                            reason = outcome.Status == ReconcileStatus.NotFound ? "network-uncertain-not-found" : "network-uncertain-terminal",
                            res.Error,
                        }, ts, ct);
                        return false;
                }
            }
            else if (!res.Success)
            {
                order.Status = OrderStatus.Rejected;
                order.ErrorMessage = res.Error;
                await SaveOrderAsync(order, isExisting, ct);
                await AuditAsync("OrderRejected", new { order.ClientOrderId, res.Error }, ts, ct);
                return false;
            }

            fillPrice = res.FilledPrice ?? currentPrice;
            fillQty = res.FilledQuantity ?? qty;
            exchangeOrderId = res.ExchangeOrderId;

            // Prezzo di liquidazione: fonte di verità è l'exchange. Se la posizione non è
            // ancora visibile (race condition tra fill e query) si ricade sulla stima locale.
            try
            {
                var remotePos = await futuresClient.GetPositionAsync(_state.Symbol, creds, ct);
                liquidationPrice = remotePos?.LiquidationPrice;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Lettura prezzo di liquidazione dall'exchange fallita: uso la stima locale.");
            }
        }

        var margin = fillQty * fillPrice / leverage;
        var notional = fillQty * fillPrice;
        var fee = notional * FeeFrac;

        // Margine ISOLATO: sia long sia short bloccano lo STESSO margine (a differenza dello
        // Spot, qui non c'è "incasso della vendita allo scoperto").
        _state.AvailableCapital -= margin + fee;

        liquidationPrice ??= MarginMath.LiquidationPrice(
            fillPrice, fillQty, margin, notional, isLong: side == OrderSide.Buy,
            safety.CurrentValue.MaintenanceMarginPercent / 100m);

        order.Status = OrderStatus.Filled;
        order.FilledPrice = fillPrice;
        order.FilledQuantity = fillQty;
        order.FilledAtUtc = ts;
        order.ExchangeOrderId = exchangeOrderId;
        metrics?.RecordTradeExecuted(_state.Mode.ToString(), side.ToString(), "Open");

        OpenPosition pos;
        if (mergeInto is null)
        {
            pos = new OpenPosition
            {
                PositionId = order.PositionId,
                StrategyId = order.StrategyId,
                Symbol = _state.Symbol,
                Side = side,
                EntryPrice = fillPrice,
                Quantity = fillQty,
                OpenedAtUtc = ts,
                CurrentPrice = fillPrice,
                ExchangeOrderId = exchangeOrderId,
                OpenedInMode = _state.Mode,
                Leverage = leverage,
                LiquidationPrice = liquidationPrice,
                MarginBalance = margin,
            };
            ApplyAutoStops(pos, order);
            _positions.Add(pos);
        }
        else
        {
            pos = mergeInto;
            var newQty = pos.Quantity + fillQty;
            pos.EntryPrice = newQty > 0m ? (pos.Quantity * pos.EntryPrice + fillQty * fillPrice) / newQty : fillPrice;
            pos.Quantity = newQty;
            pos.MarginBalance += margin;
            pos.CurrentPrice = fillPrice;
            pos.ExchangeOrderId = exchangeOrderId;
            // Liquidazione ricalcolata sui valori FUSI (fonte exchange se disponibile, altrimenti stima locale).
            pos.LiquidationPrice = liquidationPrice ?? MarginMath.LiquidationPrice(
                pos.EntryPrice, pos.Quantity, pos.MarginBalance, pos.Quantity * pos.EntryPrice,
                isLong: pos.Side == OrderSide.Buy, safety.CurrentValue.MaintenanceMarginPercent / 100m);
        }
        _state.LastOrderUtc = ts;

        await SaveOrderAsync(order, isExisting, ct);
        if (mergeInto is null) await PersistNewPositionAsync(pos, ct); else await UpdatePositionRowAsync(pos, ct);
        await AuditAsync("PlaceOrder",
            new
            {
                order.ClientOrderId, strategyName, side = side.ToString(), qty = fillQty, price = fillPrice, leverage,
                liquidationPrice = pos.LiquidationPrice, merged = mergeInto is not null,
                autoStopLoss = pos.StopLoss, autoTakeProfit = pos.TakeProfit, autoTrailingStopPercent = pos.TrailingStopPercent,
            }, ts, ct);

        // [P0-5 follow-up] Protezione "resting" sull'exchange: solo se abilitata (default OFF), su nuova
        // posizione Testnet/Live. Non blocca mai l'apertura — con i client trigger ancora stub registra
        // un warning e restano gli stop software (fonte di verità). Vedi SafetyConfiguration.UseExchangeRestingStops.
        if (mergeInto is null && _state.Mode != TradingMode.Paper
            && safety.CurrentValue.UseExchangeRestingStops && _creds is TradingCredentials restingCreds)
        {
            await TryPlaceRestingBracketAsync(pos, restingCreds, ts, ct);
        }
        return true;
    }

    /// <summary>
    /// [P0-5] Piazza sull'exchange gli ordini TRIGGER reduce-only (stop-market e take-profit-market) che
    /// replicano <see cref="OpenPosition.StopLoss"/>/<see cref="OpenPosition.TakeProfit"/>. Invocato solo se
    /// <see cref="SafetyConfiguration.UseExchangeRestingStops"/> è attivo (default OFF). Mai bloccante: ogni
    /// fallimento è solo loggato e gli stop software restano la fonte di verità.
    /// </summary>
    private async Task TryPlaceRestingBracketAsync(OpenPosition pos, TradingCredentials creds, DateTime ts, CancellationToken ct)
    {
        var closeSide = pos.Side == OrderSide.Buy ? "SELL" : "BUY"; // ordine di protezione = lato opposto
        var futuresClient = exchangeFactory.CreateFutures(_state.ExchangeName);

        async Task PlaceAsync(decimal trigger, bool isStopLoss, Action<string> onPlaced)
        {
            var clientId = Guid.NewGuid().ToString("N");
            var res = await futuresClient.PlaceFuturesTriggerOrderAsync(new PlaceOrderRequest
            {
                Symbol = pos.Symbol,
                Side = closeSide,
                Type = isStopLoss ? "STOP_MARKET" : "TAKE_PROFIT_MARKET",
                Quantity = pos.Quantity,
                TriggerPrice = trigger,
                ClientOrderId = clientId,
                Credentials = creds,
            }, isStopLoss, ct);

            if (res.Success)
            {
                onPlaced(clientId);
                await AuditAsync("RestingStopPlaced", new { pos.PositionId, kind = isStopLoss ? "stop" : "target", trigger, clientId }, ts, ct);
            }
            else
            {
                logger.LogWarning("Ordine resting {Kind} non piazzato per {Pid}: {Err}. Resta lo stop software.",
                    isStopLoss ? "stop" : "target", pos.PositionId, res.Error);
            }
        }

        if (pos.StopLoss is decimal sl && sl > 0m) await PlaceAsync(sl, isStopLoss: true, id => pos.StopOrderId = id);
        if (pos.TakeProfit is decimal tp && tp > 0m) await PlaceAsync(tp, isStopLoss: false, id => pos.TakeProfitOrderId = id);

        // [M3] Persistenza immediata degli id: senza, un riavvio perdeva i clientOrderId dei
        // trigger REALI ancora armati sull'exchange e la chiusura non poteva più cancellarli.
        if (pos.StopOrderId is not null || pos.TakeProfitOrderId is not null)
        {
            await UpdatePositionRowAsync(pos, ct);
        }
    }

    /// <summary>
    /// [P0-5] Cancella gli ordini TRIGGER resting prima di chiudere a mercato, così non restano
    /// ordini orfani sull'exchange. INERTE se non ci sono id (feature off, default).
    /// </summary>
    private async Task TryCancelRestingBracketAsync(OpenPosition pos, TradingCredentials creds, CancellationToken ct)
    {
        var futuresClient = exchangeFactory.CreateFutures(_state.ExchangeName);
        foreach (var clientId in new[] { pos.StopOrderId, pos.TakeProfitOrderId })
        {
            if (string.IsNullOrEmpty(clientId)) continue;
            var res = await futuresClient.CancelFuturesOrderAsync(pos.Symbol, clientId, creds, ct);
            if (!res.Success)
            {
                logger.LogWarning("Cancellazione ordine resting {Cid} per {Pid} fallita: {Err}.", clientId, pos.PositionId, res.Error);
            }
        }
        pos.StopOrderId = null;
        pos.TakeProfitOrderId = null;
        await UpdatePositionRowAsync(pos, ct);   // [M3] azzeramento persistito come il piazzamento
    }

    // ---------------------------------------------------------------- esecuzione a fette (TWAP/VWAP/Iceberg)

    /// <summary>
    /// Decide fra apertura IMMEDIATA (comportamento odierno) ed esecuzione a fette. L'aggancio è QUI,
    /// dopo il gate di conferma manuale Live, così lo slicing non lo scavalca mai. Rif. ROADMAP-QLIB §1.2.
    /// </summary>
    private async Task TryBuildAndStartExecutionPlanAsync(Order order, EnsembleStrategy? strat, string strategyName, decimal price, DateTime ts, CancellationToken ct, bool isExisting)
    {
        var algoName = strat?.ExecutionAlgorithmName;
        var sliced = _state.Mode != TradingMode.Paper
                     && !string.IsNullOrEmpty(algoName) && algoName != "Immediate"
                     && liveExecution.CurrentValue.Enabled;

        if (!sliced)
        {
            await ExecuteOpenAsync(order, strategyName, price, ts, ct, isExisting);   // percorso INVARIATO
            return;
        }

        // Pre-check AGGREGATO sulla quantità PIENA: senza, ogni fetta vedrebbe solo 1/N del nozionale
        // e MaxPositionSizePercent sarebbe bypassabile. Order sintetico (mai piazzato, solo per il check).
        var fullOrder = new Order
        {
            Quantity = order.Quantity, Price = price, MarketType = _state.MarketType,
            Leverage = order.Leverage, Mode = _state.Mode, Side = order.Side,
        };
        var aggregate = SafetyChecker.Evaluate(fullOrder, BuildSafetyStatus(price), safety.CurrentValue, ts);
        if (!aggregate.IsAllowed)
        {
            order.Status = OrderStatus.Rejected;
            order.ErrorMessage = string.Join(" | ", aggregate.Violations);
            await SaveOrderAsync(order, isExisting, ct);
            await AuditAsync("ExecutionPlanRejected", new { strategyName, qty = order.Quantity, price, aggregate.Violations }, ts, ct);
            if (aggregate.RequiresEmergencyStop)
                await EmergencyInternalAsync("Safety critico: " + string.Join("; ", aggregate.Violations), ts, ct);
            return;
        }

        // Finestra e numero massimo di fette: lo spacing minimo deve rispettare MinOrderIntervalSeconds
        // (non si bypassa il check, ci si pianifica dentro).
        var windowMinutes = strat?.ExecutionWindowMinutes is int m and > 0 ? m : liveExecution.CurrentValue.DefaultWindowMinutes;
        var windowSeconds = Math.Max(60, windowMinutes * 60);
        var minInterval = Math.Max(1, safety.CurrentValue.MinOrderIntervalSeconds);
        var maxSlices = Math.Max(1, windowSeconds / minInterval);
        var cap = (int)Math.Min(maxSlices, 12);

        List<OhlcvData> profile;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            profile = await db.OhlcvData.AsNoTracking()
                .Where(c => c.Symbol == _state.Symbol && c.Timeframe == _state.Timeframe)
                .OrderByDescending(c => c.TimestampUtc)
                .Take((int)Math.Min(maxSlices, 60))
                .ToListAsync(ct);
        }
        profile.Reverse();

        var execParams = new ExecutionParameters
        {
            MaxSlices = cap,
            IcebergClipFraction = Math.Max(0.1m, 1m / cap),
        };
        var intent = new ExecutionIntent(_state.Symbol,
            order.Side == OrderSide.Buy ? ExecutionSide.Buy : ExecutionSide.Sell, order.Quantity, price);
        var plan = profile.Count >= 2
            ? executionAlgorithms.Create(algoName!).BuildPlan(intent, profile, execParams)
            : null;
        var n = plan?.SliceCount ?? 0;
        if (plan is null || n <= 1)
        {
            // Nessun profilo utile o piano a una sola fetta: apertura immediata (meglio eseguire subito).
            await ExecuteOpenAsync(order, strategyName, price, ts, ct, isExisting);
            return;
        }

        // Fetta #1 SUBITO: crea la posizione (mergeInto=null). Se rifiutata, nessun job.
        order.Quantity = plan.Slices[0].Quantity;
        var filled = await ExecuteOpenAsync(order, strategyName, price, ts, ct, isExisting, mergeInto: null);
        if (!filled) return;

        var slices = new List<ExecutionJobSlice>(n - 1);
        for (var i = 1; i < n; i++)
        {
            slices.Add(new ExecutionJobSlice
            {
                OffsetSeconds = (int)((long)i * windowSeconds / n),
                Quantity = plan.Slices[i].Quantity,
                Status = "Pending",
            });
        }
        var pos = _positions.First(p => p.PositionId == order.PositionId);
        var job = new ExecutionJob
        {
            Id = Guid.NewGuid(), LaneId = laneId, StrategyId = order.StrategyId, PositionId = order.PositionId,
            Symbol = _state.Symbol, MarketType = _state.MarketType, Side = order.Side,
            TotalQuantity = plan.PlannedQuantity, FilledQuantity = order.FilledQuantity ?? plan.Slices[0].Quantity,
            EntryPriceWeightedAvg = pos.EntryPrice, Algorithm = algoName!, WindowSeconds = windowSeconds,
            Status = "Running", CreatedAtUtc = ts, SlicesJson = ExecutionJobSlices.Serialize(slices),
            ArrivalPrice = price,   // t0 di decisione: base per l'implementation shortfall a fine job
        };
        _executionJobs.Add(job);
        await PersistExecutionJobAsync(job, ct);
        metrics?.RecordExecutionJob(algoName!, "Started");
        await AuditAsync("ExecutionPlanStarted", new { job.Id, algoName, slices = n, windowSeconds }, ts, ct);
    }

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

    private async Task<List<Order>> GetPendingInternalAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Orders.AsNoTracking()
            .Where(o => o.LaneId == laneId && o.Status == OrderStatus.Pending && o.Mode == TradingMode.Live)
            .OrderByDescending(o => o.CreatedAtUtc).ToListAsync(ct);
    }

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

    private async Task SaveOrderAsync(Order order, bool isExisting, CancellationToken ct)
    {
        order.LaneId = laneId;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (isExisting)
        {
            var existing = await db.Orders.FirstOrDefaultAsync(o => o.LaneId == laneId && o.OrderId == order.OrderId, ct);
            if (existing is not null)
            {
                existing.Status = order.Status;
                existing.FilledPrice = order.FilledPrice;
                existing.FilledQuantity = order.FilledQuantity;
                existing.FilledAtUtc = order.FilledAtUtc;
                existing.ExchangeOrderId = order.ExchangeOrderId;
                existing.ErrorMessage = order.ErrorMessage;
                existing.ManuallyConfirmed = order.ManuallyConfirmed;
                await db.SaveChangesAsync(ct);
                return;
            }
        }
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
    }

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

    /// <summary>Chiusura SPOT (comportamento INVARIATO rispetto a prima dell'introduzione dei Futures).</summary>
    private async Task CloseSpotPositionAsync(OpenPosition pos, decimal exitPrice, string reason, DateTime ts, CancellationToken ct)
    {
        var qty = pos.Quantity;
        var entry = pos.EntryPrice;
        var closeSide = pos.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var closeClientId = Guid.NewGuid().ToString("N");

        // Testnet/Live: piazza l'ordine di chiusura reale (market opposto).
        if (_state.Mode != TradingMode.Paper && _creds is TradingCredentials creds)
        {
            var client = exchangeFactory.Create(_state.ExchangeName);
            var res = await client.PlaceOrderAsync(new PlaceOrderRequest
            {
                Symbol = pos.Symbol,
                Side = closeSide == OrderSide.Buy ? "BUY" : "SELL",
                Type = "MARKET",
                Quantity = qty,
                ClientOrderId = closeClientId,
                Credentials = creds,
            }, ct);
            if (res.NetworkUncertain)
            {
                var outcome = await ReconcileUncertainOrderAsync(pos.Symbol, closeClientId, futures: false, creds, ct);
                if (outcome.Status == ReconcileStatus.Filled)
                {
                    // La chiusura È avvenuta durante il blip: si finalizza con il fill reale.
                    // Prima (check sui soli open orders) la posizione restava aperta localmente
                    // PER SEMPRE: ogni retry rivendeva un asset già venduto (oversell rifiutato).
                    logger.LogWarning("Chiusura {Pid} riconciliata come ESEGUITA dopo errore di rete (fill {Price}).",
                        pos.PositionId, outcome.FillPrice);
                    await AuditAsync("CloseReconciledFilled",
                        new { pos.PositionId, closeClientId, fillPrice = outcome.FillPrice }, ts, ct);
                    if (outcome.FillPrice is decimal rp && rp > 0m) exitPrice = rp;
                }
                else
                {
                    // NotFound/terminale: mai eseguita → retry alla prossima candela (nuovo ordine).
                    // Uncertain: una chiusura NON si finalizza MAI da uno stato ignoto (il rischio
                    // di oversell è peggiore del retry); la cancellazione best-effort è già partita.
                    logger.LogError("Chiusura {Pid} incerta e non confermata dall'exchange (esito {Outcome}): la posizione resta aperta.",
                        pos.PositionId, outcome.Status);
                    await AuditAsync("CloseUncertain", new { pos.PositionId, outcome = outcome.Status.ToString(), res.Error }, ts, ct);
                    return;
                }
            }
            else if (!res.Success)
            {
                logger.LogError("Chiusura {Pid} rifiutata dall'exchange: {Err}. Posizione mantenuta.", pos.PositionId, res.Error);
                await AuditAsync("CloseRejected", new { pos.PositionId, res.Error }, ts, ct);
                return;
            }
            else if (res.FilledPrice is decimal fp && fp > 0m)
            {
                exitPrice = fp;
            }
        }

        var entryFee = qty * entry * FeeFrac;
        var exitFee = qty * exitPrice * FeeFrac;

        decimal pnl;
        if (pos.Side == OrderSide.Buy)
        {
            _state.AvailableCapital += qty * exitPrice - exitFee;
            pnl = (exitPrice - entry) * qty - entryFee - exitFee;
        }
        else
        {
            _state.AvailableCapital -= qty * exitPrice + exitFee;
            pnl = (entry - exitPrice) * qty - entryFee - exitFee;
        }

        _state.RealizedPnl += pnl;
        if ((ts - _state.DailyAnchorUtc).TotalHours >= 24) { _state.DailyPnl = 0m; _state.DailyAnchorUtc = ts; }
        _state.DailyPnl += pnl;

        var closeOrder = new Order
        {
            ClientOrderId = closeClientId,
            PositionId = pos.PositionId,
            StrategyId = pos.StrategyId,
            Symbol = pos.Symbol,
            Side = closeSide,
            Type = OrderType.Market,
            Quantity = qty,
            Price = exitPrice,
            Status = OrderStatus.Filled,
            FilledPrice = exitPrice,
            FilledQuantity = qty,
            CreatedAtUtc = ts,
            FilledAtUtc = ts,
            Mode = _state.Mode,
        };

        var trade = new TradeRecord
        {
            PositionId = pos.PositionId,
            StrategyId = pos.StrategyId,
            Symbol = pos.Symbol,
            Side = pos.Side,
            EntryPrice = entry,
            ExitPrice = exitPrice,
            Quantity = qty,
            Pnl = pnl,
            PnlPercent = entry > 0m ? pnl / (qty * entry) * 100m : 0m,
            OpenedAtUtc = pos.OpenedAtUtc,
            ClosedAtUtc = ts,
            Duration = ts - pos.OpenedAtUtc,
            ExitReason = reason,
            Mode = _state.Mode,
        };

        _positions.Remove(pos);
        _state.LastOrderUtc = ts;

        await PersistOrderAsync(closeOrder, ct);
        await RemovePositionAsync(pos, ct);
        await PersistTradeAsync(trade, ct);
        await AuditAsync("ClosePosition", new { pos.PositionId, pnl, reason }, ts, ct);
    }

    /// <summary>
    /// Chiusura FUTURES: ordine reduceOnly opposto (salvo <paramref name="alreadyClosedOnExchange"/>,
    /// usato dalla riconciliazione quando l'exchange ha già liquidato/chiuso la posizione), rimborso
    /// del margine isolato (non del nozionale) + PnL, PnL% calcolata sul margine.
    /// </summary>
    private async Task CloseFuturesPositionAsync(OpenPosition pos, decimal exitPrice, string reason, DateTime ts, CancellationToken ct, bool alreadyClosedOnExchange)
    {
        var qty = pos.Quantity;
        var entry = pos.EntryPrice;
        var closeSide = pos.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var closeClientId = Guid.NewGuid().ToString("N");

        if (!alreadyClosedOnExchange && _state.Mode != TradingMode.Paper && _creds is TradingCredentials creds)
        {
            var futuresClient = exchangeFactory.CreateFutures(_state.ExchangeName);

            // [P0-5 follow-up] Cancella eventuali ordini TRIGGER resting prima del market close, per non
            // lasciarli orfani sull'exchange. Inerte se non ce ne sono (feature off/stub → id sempre null).
            if (pos.StopOrderId is not null || pos.TakeProfitOrderId is not null)
            {
                await TryCancelRestingBracketAsync(pos, creds, ct);
            }

            var res = await futuresClient.PlaceFuturesOrderAsync(new PlaceOrderRequest
            {
                Symbol = pos.Symbol,
                Side = closeSide == OrderSide.Buy ? "BUY" : "SELL",
                Type = "MARKET",
                Quantity = qty,
                ClientOrderId = closeClientId,
                Credentials = creds,
            }, reduceOnly: true, ct);
            if (res.NetworkUncertain)
            {
                var outcome = await ReconcileUncertainOrderAsync(pos.Symbol, closeClientId, futures: true, creds, ct);
                if (outcome.Status == ReconcileStatus.Filled)
                {
                    // La chiusura È avvenuta durante il blip: si finalizza con il fill reale.
                    // Prima la posizione restava aperta finché ReconcileFuturesPositionsAsync non
                    // la forzava a lastKnownPrice come "Liquidation/ExternalClose" — prezzo
                    // sbagliato e WasLiquidated fuorviante.
                    logger.LogWarning("Chiusura futures {Pid} riconciliata come ESEGUITA dopo errore di rete (fill {Price}).",
                        pos.PositionId, outcome.FillPrice);
                    await AuditAsync("CloseReconciledFilled",
                        new { pos.PositionId, closeClientId, fillPrice = outcome.FillPrice }, ts, ct);
                    if (outcome.FillPrice is decimal rp && rp > 0m) exitPrice = rp;
                }
                else
                {
                    // NotFound/terminale: mai eseguita → retry alla prossima candela (nuovo ordine).
                    // Uncertain: mai finalizzare da stato ignoto (cancellazione best-effort già partita).
                    logger.LogError("Chiusura futures {Pid} incerta e non confermata dall'exchange (esito {Outcome}): la posizione resta aperta.",
                        pos.PositionId, outcome.Status);
                    await AuditAsync("CloseUncertain", new { pos.PositionId, outcome = outcome.Status.ToString(), res.Error }, ts, ct);
                    return;
                }
            }
            else if (!res.Success)
            {
                logger.LogError("Chiusura futures {Pid} rifiutata dall'exchange: {Err}. Posizione mantenuta.", pos.PositionId, res.Error);
                await AuditAsync("CloseRejected", new { pos.PositionId, res.Error }, ts, ct);
                return;
            }
            else if (res.FilledPrice is decimal fp && fp > 0m)
            {
                exitPrice = fp;
            }
        }

        var entryFee = qty * entry * FeeFrac;
        var exitFee = qty * exitPrice * FeeFrac;

        var pnl = pos.Side == OrderSide.Buy
            ? (exitPrice - entry) * qty - entryFee - exitFee
            : (entry - exitPrice) * qty - entryFee - exitFee;

        // Margine ISOLATO: si restituisce il margine bloccato + PnL (guadagno o perdita),
        // MAI il nozionale intero (a differenza dello Spot).
        _state.AvailableCapital += pos.MarginBalance + pnl;

        _state.RealizedPnl += pnl;
        if ((ts - _state.DailyAnchorUtc).TotalHours >= 24) { _state.DailyPnl = 0m; _state.DailyAnchorUtc = ts; }
        _state.DailyPnl += pnl;

        var wasLiquidated = reason.StartsWith("Liquidation", StringComparison.Ordinal);

        var closeOrder = new Order
        {
            ClientOrderId = closeClientId,
            PositionId = pos.PositionId,
            StrategyId = pos.StrategyId,
            Symbol = pos.Symbol,
            Side = closeSide,
            Type = OrderType.Market,
            Quantity = qty,
            Price = exitPrice,
            Status = OrderStatus.Filled,
            FilledPrice = exitPrice,
            FilledQuantity = qty,
            CreatedAtUtc = ts,
            FilledAtUtc = ts,
            Mode = _state.Mode,
            MarketType = MarketType.Futures,
            Leverage = pos.Leverage,
        };

        var trade = new TradeRecord
        {
            PositionId = pos.PositionId,
            StrategyId = pos.StrategyId,
            Symbol = pos.Symbol,
            Side = pos.Side,
            EntryPrice = entry,
            ExitPrice = exitPrice,
            Quantity = qty,
            Pnl = pnl,
            PnlPercent = pos.MarginBalance > 0m ? pnl / pos.MarginBalance * 100m : 0m,
            OpenedAtUtc = pos.OpenedAtUtc,
            ClosedAtUtc = ts,
            Duration = ts - pos.OpenedAtUtc,
            ExitReason = reason,
            Mode = _state.Mode,
            MarketType = MarketType.Futures,
            Leverage = pos.Leverage,
            WasLiquidated = wasLiquidated,
        };

        _positions.Remove(pos);
        _state.LastOrderUtc = ts;

        await PersistOrderAsync(closeOrder, ct);
        await RemovePositionAsync(pos, ct);
        await PersistTradeAsync(trade, ct);
        await AuditAsync("ClosePosition", new { pos.PositionId, pnl, reason, wasLiquidated }, ts, ct);
    }

    /// <summary>
    /// Ogni candela (solo Futures, Testnet/Live), verifica sull'exchange che le posizioni locali
    /// siano ancora aperte. L'exchange può liquidare/chiudere una posizione indipendentemente dal
    /// ciclo del motore (es. liquidazione forzata più rapida del prossimo controllo locale): se
    /// risulta flat lato exchange ma aperta localmente, la chiudiamo qui con il miglior prezzo
    /// noto (stesso approccio "honest fallback" di EmergencyInternalAsync).
    /// </summary>
    private async Task ReconcileFuturesPositionsAsync(decimal lastKnownPrice, DateTime ts, CancellationToken ct)
    {
        // NB: si interroga l'exchange anche a posizioni locali ZERO, per la difesa inversa qui
        // sotto (posizione remota che il motore non conosce) — una chiamata firmata per candela.
        if (_state.MarketType != MarketType.Futures || _state.Mode == TradingMode.Paper || _creds is not TradingCredentials creds)
        {
            return;
        }

        var futuresClient = exchangeFactory.CreateFutures(_state.ExchangeName);
        FuturesPosition? remote;
        try
        {
            remote = await futuresClient.GetPositionAsync(_state.Symbol, creds, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Riconciliazione futures fallita (rete): salto questo ciclo.");
            return;
        }

        if (remote is not null)
        {
            // Difesa inversa: posizione APERTA sull'exchange ma sconosciuta al motore (es. esito
            // di un ordine dichiarato incerto, o apertura manuale fuori piattaforma). NESSUNA
            // auto-azione — chiuderla d'ufficio potrebbe distruggere un'operazione voluta
            // dall'operatore; si allerta una sola volta finché la condizione persiste.
            if (!_positions.Any(p => p.Symbol == _state.Symbol))
            {
                if (!_untrackedRemoteAlerted)
                {
                    _untrackedRemoteAlerted = true;
                    logger.LogCritical(
                        "Posizione {Side} {Qty} {Sym} APERTA sull'exchange ma SCONOSCIUTA al motore: VERIFICARE MANUALMENTE (nessuna azione automatica).",
                        remote.Side, remote.Quantity, _state.Symbol);
                    await AuditAsync("UntrackedRemotePosition",
                        new { _state.Symbol, remote.Side, remote.Quantity, remote.EntryPrice }, ts, ct);
                }
            }
            else
            {
                _untrackedRemoteAlerted = false;
            }
            return;
        }

        _untrackedRemoteAlerted = false;
        foreach (var pos in _positions.Where(p => p.Symbol == _state.Symbol).ToList())
        {
            logger.LogWarning("Posizione {Pid} risulta chiusa sull'exchange ma aperta localmente: riconciliazione (probabile liquidazione esterna).", pos.PositionId);
            await ClosePositionAsync(pos, lastKnownPrice, "Liquidation/ExternalClose", ts, ct, alreadyClosedOnExchange: true);
        }
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
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Orders.Where(o => o.LaneId == laneId);
        if (from is DateTime f) q = q.Where(o => o.CreatedAtUtc >= f);
        return await q.OrderByDescending(o => o.CreatedAtUtc).Take(500).ToListAsync(ct);
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
        var ppy = Statistics.PeriodsPerYear(_state.Timeframe);

        return new TradingPerformance
        {
            EquityCurve = _equity.ToList(),
            TotalReturn = _state.TotalCapital > 0m ? _state.RealizedPnl / _state.TotalCapital * 100m : 0m,
            // Sharpe calcolato sulla FINESTRA ritenuta della curva (bounded, vedi TrimEquity):
            // per una metrica di promozione la storia recente è quella che conta. Il MaxDrawdown
            // invece è il PEGGIORE tra ricalcolo locale e valore di sessione persistito — un
            // riavvio (curva vuota) o il trim non possono più "amnesiare" un drawdown già subito.
            SharpeRatio = Statistics.SharpeRatio(_equity, ppy),
            MaxDrawdown = Math.Max(_state.MaxDrawdownPercent, MaxDrawdown(_equity)),
            TotalTrades = trades.Count,
            WinRate = trades.Count > 0 ? (decimal)wins.Count / trades.Count * 100m : 0m,
            AverageWin = wins.Count > 0 ? wins.Average(t => t.Pnl) : 0m,
            AverageLoss = losses.Count > 0 ? losses.Average(t => t.Pnl) : 0m,
            ProfitFactor = grossLoss > 0m ? grossWin / grossLoss : 0m,
            Trades = trades,
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

    private async Task PersistOrderAsync(Order order, CancellationToken ct)
    {
        order.LaneId = laneId;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
    }

    private async Task PersistNewPositionAsync(OpenPosition pos, CancellationToken ct)
    {
        pos.LaneId = laneId;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.OpenPositions.Add(pos);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Aggiorna la riga di una posizione ESISTENTE dopo un fill fuso (media ponderata di una fetta).</summary>
    private async Task UpdatePositionRowAsync(OpenPosition pos, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.OpenPositions.FirstOrDefaultAsync(p => p.LaneId == laneId && p.PositionId == pos.PositionId, ct);
        if (row is null) return;
        row.Quantity = pos.Quantity;
        row.EntryPrice = pos.EntryPrice;
        row.MarginBalance = pos.MarginBalance;
        row.CurrentPrice = pos.CurrentPrice;
        row.LiquidationPrice = pos.LiquidationPrice;
        row.ExchangeOrderId = pos.ExchangeOrderId;
        row.StopOrderId = pos.StopOrderId;               // [M3] i trigger resting sopravvivono al riavvio
        row.TakeProfitOrderId = pos.TakeProfitOrderId;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Inserisce o aggiorna la riga di un ExecutionJob (idempotente per Id).</summary>
    private async Task PersistExecutionJobAsync(ExecutionJob job, CancellationToken ct)
    {
        job.LaneId = laneId;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ExecutionJobs.FirstOrDefaultAsync(j => j.Id == job.Id, ct);
        if (row is null) db.ExecutionJobs.Add(job);
        else db.Entry(row).CurrentValues.SetValues(job);
        await db.SaveChangesAsync(ct);
    }

    private async Task RemovePositionAsync(OpenPosition pos, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.OpenPositions.Where(p => p.LaneId == laneId && p.PositionId == pos.PositionId).ExecuteDeleteAsync(ct);
    }

    private async Task PersistTradeAsync(TradeRecord trade, CancellationToken ct)
    {
        trade.LaneId = laneId;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.TradeRecords.Add(trade);
        await db.SaveChangesAsync(ct);
    }

    private async Task AuditAsync(string action, object details, DateTime ts, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.TradingAuditLogs.Add(new TradingAuditLog
        {
            LaneId = laneId,
            TimestampUtc = ts,
            Action = action,
            Details = JsonSerializer.Serialize(details, Json),
            Mode = _state.Mode,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Carica le credenziali firmate dell'exchange (decifrate dal converter EF).</summary>
    private async Task<TradingCredentials?> LoadCredentialsAsync(string exchangeName, bool testnet, CancellationToken ct)
    {
        if (!Enum.TryParse<ExchangeName>(exchangeName, out var ex))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cred = await db.ExchangeCredentials.FirstOrDefaultAsync(c => c.ExchangeName == ex && c.IsTestnet == testnet, ct);
        return cred is null ? null : new TradingCredentials(cred.ApiKey, cred.ApiSecret, cred.Passphrase, testnet);
    }
}
