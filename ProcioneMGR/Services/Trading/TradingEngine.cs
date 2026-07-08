using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Optimization;
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
    ILogger<TradingEngine> logger) : ITradingEngine
{
    public int LaneId => laneId;

    private const decimal PositionSizePercent = 8m;   // sotto MaxPositionSizePercent (10%)
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
        _positions.Clear();
        _positions.AddRange(positions);
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

    private async Task EmergencyInternalAsync(string reason, DateTime ts, CancellationToken ct)
    {
        _state.IsEmergencyStopped = true;
        _state.IsRunning = false;
        _state.EmergencyStopReason = reason;

        // Chiudi TUTTE le posizioni al prezzo corrente (market). ClosePositionAsync annulla anche
        // il piano di esecuzione eventualmente associato a ciascuna posizione (vedi §1 del design).
        foreach (var pos in _positions.ToList())
        {
            var exit = pos.CurrentPrice > 0m ? pos.CurrentPrice : pos.EntryPrice;
            await ClosePositionAsync(pos, exit, "EmergencyStop", ts, ct);
        }

        // Difesa: qualunque piano ancora Running (posizione non trovata, caso limite) va comunque annullato.
        foreach (var job in _executionJobs.ToList())
        {
            job.Status = "Cancelled";
            job.CompletedAtUtc = ts;
            _executionJobs.Remove(job);
            await PersistExecutionJobAsync(job, ct);
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

                // Trailing stop: livello calcolato sul best-since-entry PRIMA di considerare il
                // prezzo di QUESTA candela (evita di usare l'estremo di oggi per definire lo stop
                // di oggi) — stesso principio causale del motore di backtest. Se sia uno stop
                // statico che il trailing sono attivi, vince quello più protettivo (esito peggiore
                // per la posizione), come le varianti combinate SL+TRAIL del backtest.
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

                if (effectiveStop is decimal sl && ((pos.Side == OrderSide.Buy && price <= sl) || (pos.Side == OrderSide.Sell && price >= sl)))
                {
                    await ClosePositionAsync(pos, price, "StopLoss", ts, ct);
                }
                else if (pos.TakeProfit is decimal tp && ((pos.Side == OrderSide.Buy && price >= tp) || (pos.Side == OrderSide.Sell && price <= tp)))
                {
                    await ClosePositionAsync(pos, price, "TakeProfit", ts, ct);
                }
                else if (pos.TrailingStopPercent is > 0m)
                {
                    pos.BestPriceSinceEntry = pos.Side == OrderSide.Buy
                        ? Math.Max(pos.BestPriceSinceEntry ?? pos.EntryPrice, price)
                        : Math.Min(pos.BestPriceSinceEntry ?? pos.EntryPrice, price);
                }
            }

            // Valuta i segnali delle strategie.
            var closes = _buffer.Select(c => c.Close).ToList();
            if (closes.Count >= 5)
            {
                foreach (var strat in _active)
                {
                    if (_state.IsEmergencyStopped) break;
                    var s = strategyFactory.Create(strat.StrategyName);
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
            if (equity > _state.PeakEquity) _state.PeakEquity = equity;
            var dd = _state.PeakEquity > 0m ? (_state.PeakEquity - equity) / _state.PeakEquity * 100m : 0m;

            await SaveStateAsync(ct);

            if (dd >= safety.CurrentValue.MaxDrawdownPercent && !_state.IsEmergencyStopped)
            {
                await EmergencyInternalAsync($"Max drawdown {dd:F1}% superato", ts, ct);
            }
        }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------- open / close

    private async Task TryOpenAsync(EnsembleStrategy strat, OrderSide side, decimal price, DateTime ts, CancellationToken ct)
    {
        if (price <= 0m) return;

        // Spot: PositionSizePercent è il nozionale investito (leva implicita 1x).
        // Futures: PositionSizePercent è il MARGINE isolato; il nozionale (e quindi
        // l'esposizione reale) è margine × leva — stessa logica del motore di backtest.
        var margin = _state.TotalCapital * PositionSizePercent / 100m;
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
                var open = await client.GetOpenOrdersAsync(_state.Symbol, creds, ct);
                if (!open.Any(o => o.ClientOrderId == order.ClientOrderId))
                {
                    order.Status = OrderStatus.Rejected;
                    order.ErrorMessage = "Errore di rete: ordine NON riscontrato sull'exchange. " + res.Error;
                    await SaveOrderAsync(order, isExisting, ct);
                    await AuditAsync("OrderRejected", new { order.ClientOrderId, reason = "network-uncertain-not-found", res.Error }, ts, ct);
                    return false;
                }
                logger.LogWarning("Ordine {Cid} riconciliato sull'exchange dopo errore di rete.", order.ClientOrderId);
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
                var open = await futuresClient.GetOpenFuturesOrdersAsync(_state.Symbol, creds, ct);
                if (!open.Any(o => o.ClientOrderId == order.ClientOrderId))
                {
                    order.Status = OrderStatus.Rejected;
                    order.ErrorMessage = "Errore di rete: ordine futures NON riscontrato sull'exchange. " + res.Error;
                    await SaveOrderAsync(order, isExisting, ct);
                    await AuditAsync("OrderRejected", new { order.ClientOrderId, reason = "network-uncertain-not-found", res.Error }, ts, ct);
                    return false;
                }
                logger.LogWarning("Ordine futures {Cid} riconciliato sull'exchange dopo errore di rete.", order.ClientOrderId);
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
        return true;
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
        };
        _executionJobs.Add(job);
        await PersistExecutionJobAsync(job, ct);
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
                var open = await client.GetOpenOrdersAsync(pos.Symbol, creds, ct);
                if (!open.Any(o => o.ClientOrderId == closeClientId))
                {
                    // Chiusura non riscontrata: NON rimuovere la posizione, logga e riprova al prossimo ciclo.
                    logger.LogError("Chiusura {Pid} incerta e non riscontrata sull'exchange: la posizione resta aperta.", pos.PositionId);
                    await AuditAsync("CloseUncertain", new { pos.PositionId, res.Error }, ts, ct);
                    return;
                }
            }
            else if (!res.Success)
            {
                logger.LogError("Chiusura {Pid} rifiutata dall'exchange: {Err}. Posizione mantenuta.", pos.PositionId, res.Error);
                await AuditAsync("CloseRejected", new { pos.PositionId, res.Error }, ts, ct);
                return;
            }
            if (res.FilledPrice is decimal fp && fp > 0m) exitPrice = fp;
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
                var open = await futuresClient.GetOpenFuturesOrdersAsync(pos.Symbol, creds, ct);
                if (!open.Any(o => o.ClientOrderId == closeClientId))
                {
                    logger.LogError("Chiusura futures {Pid} incerta e non riscontrata sull'exchange: la posizione resta aperta.", pos.PositionId);
                    await AuditAsync("CloseUncertain", new { pos.PositionId, res.Error }, ts, ct);
                    return;
                }
            }
            else if (!res.Success)
            {
                logger.LogError("Chiusura futures {Pid} rifiutata dall'exchange: {Err}. Posizione mantenuta.", pos.PositionId, res.Error);
                await AuditAsync("CloseRejected", new { pos.PositionId, res.Error }, ts, ct);
                return;
            }
            if (res.FilledPrice is decimal fp && fp > 0m) exitPrice = fp;
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
        if (_state.MarketType != MarketType.Futures || _state.Mode == TradingMode.Paper || _creds is not TradingCredentials creds || _positions.Count == 0)
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

        if (remote is not null) return;

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
            SharpeRatio = Statistics.SharpeRatio(_equity, ppy),
            MaxDrawdown = MaxDrawdown(_equity),
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
            eq += pos.Side == OrderSide.Buy ? pos.Quantity * price : -pos.Quantity * price;
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
