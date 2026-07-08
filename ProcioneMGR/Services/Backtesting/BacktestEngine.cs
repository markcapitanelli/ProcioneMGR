using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Risk;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// Motore di backtest event-driven, long/short, una posizione alla volta.
///
/// Pipeline:
///  1. carica le candele OHLCV dal DB per il range;
///  2. la strategia pre-calcola i suoi indicatori UNA volta (InitializeAsync);
///  3. itera candela per candela (hot loop su array <c>decimal[]</c>, niente LINQ):
///     - chiede il <see cref="Signal"/> alla strategia;
///     - Long/Short: se serve chiude la posizione opposta (flip) e apre;
///     - Close: chiude la posizione corrente;
///     - aggiorna l'equity curve a ogni candela;
///  4. chiude l'eventuale posizione aperta sull'ultima candela e calcola le metriche.
///
/// Tutto in <c>decimal</c> e deterministico (nessuna casualita'/parallelismo).
/// Commissione <c>FeePercent</c> applicata su entry ed exit. Cancellabile; <c>Task.Yield()</c>
/// ogni 1000 candele per non saturare il thread pool.
/// </summary>
public sealed class BacktestEngine(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IStrategyFactory strategyFactory,
    ITechnicalIndicatorsService indicators,
    IAlphaFactorFactory alphaFactorFactory,
    ILogger<BacktestEngine> logger,
    IFactorCache? factorCache = null) : IBacktestEngine
{
    /// <summary>Nome speciale (non nello switch di <see cref="IStrategyFactory"/>): risolto caricando un <c>SavedMlModel</c>.</summary>
    private const string MlStrategyName = "Ml";
    public async Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Carica le candele del range richiesto (ordine cronologico) e delega al core.
        var fromUtc = DateTime.SpecifyKind(config.From, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(config.To, DateTimeKind.Utc);

        List<OhlcvData> candles;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            candles = await db.OhlcvData
                .Where(c => c.Symbol == config.Symbol
                            && c.Timeframe == config.Timeframe
                            && c.TimestampUtc >= fromUtc
                            && c.TimestampUtc <= toUtc)
                .OrderBy(c => c.TimestampUtc)
                .ToListAsync(ct);
        }

        if (candles.Count == 0)
        {
            logger.LogWarning("Backtest senza dati per {Symbol} {Timeframe} nel range richiesto.", config.Symbol, config.Timeframe);
        }

        return await RunBacktestAsync(config, candles, ct);
    }

    /// <summary>
    /// Overload con candele gia' caricate: usato dall'ottimizzatore per non ricaricare
    /// l'OHLCV dal DB a ogni backtest (caching). Le candele devono essere gia' filtrate
    /// per il range desiderato e ordinate cronologicamente. Stateless e thread-safe.
    /// </summary>
    public async Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, CancellationToken ct)
    {
        var (strategy, owned) = await ResolveStrategyAsync(config, ct);
        try
        {
            return await RunBacktestAsync(config, candles, strategy, ct);
        }
        finally
        {
            // Solo le risorse create QUI (es. il predittore ML caricato per questo singolo
            // backtest, un'istanza per combo/finestra in uno sweep di Optimization) vengono
            // rilasciate: una strategia passata dal chiamante tramite l'overload con "strategy"
            // esplicita resta di sua proprietà (es. MlLab.razor riusa lo stesso predittore fra
            // Train/Backtest/Save nella stessa sessione UI).
            owned?.Dispose();
        }
    }

    /// <summary>
    /// Risolve una strategia per nome. "Ml" è un caso speciale: non è nello switch di
    /// <see cref="IStrategyFactory"/> (richiede un predittore addestrato + fattori, non
    /// rappresentabili come parametri decimali di default) — si carica invece un
    /// <c>SavedMlModel</c> referenziato dal parametro "SavedModelId". Questo è il PUNTO DI
    /// AGGANCIO che rende i modelli ML utilizzabili da Optimization/Discovery/Ensemble senza
    /// modificarli: bastano <c>StrategyName="Ml"</c> e <c>StrategyParameters["SavedModelId"]</c>
    /// nella stessa <see cref="BacktestConfiguration"/> che già usano per le strategie normali.
    /// </summary>
    private async Task<(IStrategy Strategy, IDisposable? Owned)> ResolveStrategyAsync(BacktestConfiguration config, CancellationToken ct)
    {
        if (config.StrategyName == MlStrategyName)
        {
            var (strategy, predictor) = await LoadMlStrategyAsync(config.StrategyParameters, ct);
            return (strategy, predictor);
        }
        return (strategyFactory.Create(config.StrategyName), null);
    }

    private async Task<(MlStrategy Strategy, IReturnPredictor Predictor)> LoadMlStrategyAsync(IReadOnlyDictionary<string, decimal> parameters, CancellationToken ct)
    {
        if (parameters is null || !parameters.TryGetValue("SavedModelId", out var idValue))
        {
            throw new ArgumentException("La strategia 'Ml' richiede il parametro 'SavedModelId' (Id di un modello addestrato e salvato in /ml).");
        }
        var id = (int)idValue;

        SavedMlModel? saved;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            saved = await db.SavedMlModels.FirstOrDefaultAsync(m => m.Id == id, ct);
        }
        if (saved is null)
        {
            throw new InvalidOperationException($"Modello ML salvato con Id={id} non trovato.");
        }

        // Nota di performance: ogni chiamata deserializza di nuovo il modello dal blob salvato
        // (nessuna cache per SavedModelId). Accettabile per lo scope attuale (poche soglie da
        // sweepare in Optimization, poche strategie in Ensemble); da rivedere se in futuro
        // servissero sweep massivi su molti modelli ML. Caricamento condiviso col TradingEngine
        // (Champion) via MlModelLoader → parità batch/stream garantita.
        return await MlModelLoader.LoadAsync(saved, alphaFactorFactory, factorCache, ct);
    }

    /// <summary>
    /// Overload che usa un'istanza di strategia già pronta (vedi <see cref="IBacktestEngine"/>)
    /// invece di crearla per nome. Contiene il core del motore: entrambi gli overload pubblici
    /// convergono qui, così la pipeline di esecuzione è unica.
    /// </summary>
    public async Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, IStrategy strategy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(strategy);

        var n = candles.Count;
        if (n == 0)
        {
            return new BacktestResult { FinalCapital = config.InitialCapital, TotalReturnPercent = 0m };
        }

        // Inizializza la strategia (pre-calcolo indicatori).
        var closes = new List<decimal>(n);
        var closeArr = new decimal[n];
        var timeArr = new DateTime[n];
        for (var i = 0; i < n; i++)
        {
            var px = candles[i].Close;
            closes.Add(px);
            closeArr[i] = px;
            timeArr[i] = DateTime.SpecifyKind(candles[i].TimestampUtc, DateTimeKind.Utc);
        }

        await strategy.InitializeAsync(closes, candles, config.StrategyParameters ?? new(), indicators, ct);

        // 3) Loop event-driven.
        // Difesa: una fee negativa verrebbe trattata come un rebate che paga a ogni fill,
        // gonfiando artificialmente i rendimenti. La commissione non puo' essere < 0.
        var feePercent = Math.Max(0m, config.FeePercent);
        var book = new Portfolio(config.InitialCapital, feePercent, config.PositionSizePercent, config.Leverage);
        var equity = new List<EquityPoint>(n);

        // Overlay stop/target a livello di motore (0 = disattivi, comportamento invariato).
        var slFrac = config.StopLossPercent > 0m ? config.StopLossPercent / 100m : 0m;
        var tpFrac = config.TakeProfitPercent > 0m ? config.TakeProfitPercent / 100m : 0m;
        var trailFrac = config.TrailingStopPercent > 0m ? config.TrailingStopPercent / 100m : 0m;
        var stopsActive = slFrac > 0m || tpFrac > 0m || trailFrac > 0m;
        var entryIndex = -1;
        decimal entryPrice = 0m, bestSinceEntry = 0m;

        // Leva/derivati: slippage sfavorevole sui fill, liquidazione intrabar, funding pro-rata.
        var slipFrac = config.SlippagePercent > 0m ? config.SlippagePercent / 100m : 0m;
        var maintFrac = config.MaintenanceMarginPercent > 0m ? config.MaintenanceMarginPercent / 100m : 0m;
        var leveraged = config.Leverage > 1m;
        var fundingFrac = config.FundingRatePercentPer8h > 0m ? config.FundingRatePercentPer8h / 100m : 0m;
        var candleHours = TimeframeHours(config.Timeframe);

        // Fill con slippage: chi compra paga di piu', chi vende incassa di meno.
        decimal Buy(decimal level) => level * (1m + slipFrac);
        decimal Sell(decimal level) => level * (1m - slipFrac);

        for (var i = 0; i < n; i++)
        {
            if (i % 1000 == 0)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            var price = closeArr[i];
            var ts = timeArr[i];

            // Controllo stop/target PRIMA del segnale, su high/low della candela. La candela
            // di ingresso e' esclusa: il fill avviene alla sua close, l'escursione precedente
            // non riguarda la posizione. Se stop e target cadono nella stessa candela si
            // assume l'esito PEGGIORE (stop) per non essere ottimisti.
            if (stopsActive && !book.IsFlat && i > entryIndex)
            {
                var high = candles[i].High;
                var low = candles[i].Low;

                // Il livello di trailing attivo su questa candela e' calcolato sugli estremi
                // delle candele PRECEDENTI (lo stop "in macchina" e' stato piazzato prima
                // dell'apertura); solo dopo il controllo si aggiorna il best con la candela
                // corrente. Evita di assumere che l'estremo favorevole intrabar preceda quello
                // sfavorevole.
                if (book.IsLong)
                {
                    var stopLevel = slFrac > 0m ? entryPrice * (1m - slFrac) : decimal.MinValue;
                    if (trailFrac > 0m)
                    {
                        var trail = bestSinceEntry * (1m - trailFrac);
                        if (trail > stopLevel) stopLevel = trail;
                    }
                    if (stopLevel > decimal.MinValue && low <= stopLevel)
                    {
                        book.Close(Sell(Math.Min(stopLevel, candles[i].Open)), ts);
                    }
                    else if (tpFrac > 0m && high >= entryPrice * (1m + tpFrac))
                    {
                        book.Close(Sell(Math.Max(entryPrice * (1m + tpFrac), candles[i].Open)), ts);
                    }
                    else if (high > bestSinceEntry)
                    {
                        bestSinceEntry = high;
                    }
                }
                else if (book.IsShort)
                {
                    var stopLevel = slFrac > 0m ? entryPrice * (1m + slFrac) : decimal.MaxValue;
                    if (trailFrac > 0m)
                    {
                        var trail = bestSinceEntry * (1m + trailFrac);
                        if (trail < stopLevel) stopLevel = trail;
                    }
                    if (stopLevel < decimal.MaxValue && high >= stopLevel)
                    {
                        book.Close(Buy(Math.Max(stopLevel, candles[i].Open)), ts);
                    }
                    else if (tpFrac > 0m && low <= entryPrice * (1m - tpFrac))
                    {
                        book.Close(Buy(Math.Min(entryPrice * (1m - tpFrac), candles[i].Open)), ts);
                    }
                    else if (low < bestSinceEntry)
                    {
                        bestSinceEntry = low;
                    }
                }
            }

            // Liquidazione (solo con leva > 1): se dopo gli eventuali stop la posizione e'
            // ancora aperta e l'escursione intrabar raggiunge il prezzo di liquidazione, la
            // chiusura e' FORZATA a quel livello (o all'open se la candela apre gia' oltre).
            // Gli stop vengono controllati prima perche' un ordine stop in macchina scatta
            // a un livello meno profondo della liquidazione.
            if (leveraged && !book.IsFlat && i > entryIndex)
            {
                var liq = book.LiquidationPrice(maintFrac);
                if (book.IsLong && candles[i].Low <= liq)
                {
                    book.Close(Sell(Math.Min(liq, candles[i].Open)), ts, liquidated: true);
                }
                else if (book.IsShort && candles[i].High >= liq)
                {
                    book.Close(Buy(Math.Max(liq, candles[i].Open)), ts, liquidated: true);
                }
            }

            // Funding dei perpetual: addebito pro-rata per candela con posizione aperta.
            if (fundingFrac > 0m && !book.IsFlat)
            {
                book.ChargeFunding(book.OpenNotional * fundingFrac * candleHours / 8m);
            }

            var signal = strategy.EvaluateSignal(i, price, ts);

            switch (signal)
            {
                case Signal.Long:
                    if (book.IsShort) book.Close(Buy(price), ts);
                    if (book.IsFlat)
                    {
                        var fill = Buy(price);
                        book.OpenLong(fill, ts);
                        entryIndex = i;
                        entryPrice = fill;
                        bestSinceEntry = fill;
                    }
                    break;
                case Signal.Short:
                    if (book.IsLong) book.Close(Sell(price), ts);
                    if (book.IsFlat)
                    {
                        var fill = Sell(price);
                        book.OpenShort(fill, ts);
                        entryIndex = i;
                        entryPrice = fill;
                        bestSinceEntry = fill;
                    }
                    break;
                case Signal.Close:
                    if (book.IsLong) book.Close(Sell(price), ts);
                    else if (book.IsShort) book.Close(Buy(price), ts);
                    break;
                case Signal.Hold:
                default:
                    break;
            }

            equity.Add(new EquityPoint { Timestamp = ts, Capital = book.Equity(price) });
        }

        // 4) Chiudi l'eventuale posizione residua sull'ultima candela.
        if (!book.IsFlat)
        {
            var lastFill = book.IsLong ? Sell(closeArr[n - 1]) : Buy(closeArr[n - 1]);
            book.Close(lastFill, timeArr[n - 1]);
            if (equity.Count > 0)
            {
                equity[^1].Capital = book.Cash;
            }
        }

        return BuildResult(config, book, equity, n);
    }

    /// <summary>Durata di una candela in ore (per il funding pro-rata).</summary>
    private static decimal TimeframeHours(string timeframe) => timeframe switch
    {
        "1m" => 1m / 60m,
        "5m" => 5m / 60m,
        "15m" => 0.25m,
        "30m" => 0.5m,
        "1h" => 1m,
        "4h" => 4m,
        "1d" => 24m,
        _ => 1m,
    };

    private static BacktestResult BuildResult(BacktestConfiguration config, Portfolio book, List<EquityPoint> equity, int candlesEvaluated)
    {
        var trades = book.Trades;
        var winning = 0;
        var losing = 0;
        foreach (var t in trades)
        {
            if (t.Pnl > 0m) winning++;
            else if (t.Pnl < 0m) losing++;
        }

        var finalCapital = book.Cash;
        return new BacktestResult
        {
            FinalCapital = finalCapital,
            TotalReturnPercent = config.InitialCapital == 0m ? 0m : (finalCapital - config.InitialCapital) / config.InitialCapital * 100m,
            TotalTrades = trades.Count,
            WinningTrades = winning,
            LosingTrades = losing,
            WinRate = trades.Count == 0 ? 0m : (decimal)winning / trades.Count * 100m,
            MaxDrawdownPercent = MaxDrawdown(equity),
            CandlesEvaluated = candlesEvaluated,
            LiquidationCount = book.LiquidationCount,
            Trades = trades,
            EquityCurve = equity,
        };
    }

    private static decimal MaxDrawdown(List<EquityPoint> equity)
    {
        var peak = decimal.MinValue;
        var maxDd = 0m;
        foreach (var p in equity)
        {
            if (p.Capital > peak) peak = p.Capital;
            if (peak > 0m)
            {
                var dd = (peak - p.Capital) / peak * 100m;
                if (dd > maxDd) maxDd = dd;
            }
        }
        return maxDd;
    }

    /// <summary>
    /// Conto/posizione a MARGINE: all'apertura si riserva margine = equity * sizeFrac e si
    /// apre un nozionale = margine * leva; equity = cash + margine + PnL non realizzato.
    /// A leva 1 questa contabilita' coincide ESATTAMENTE (formula per formula) con la vecchia
    /// contabilita' spot, per long e short: nessun cambiamento nei risultati esistenti.
    /// Con leva &gt; 1 espone il prezzo di liquidazione (margine eroso fino al mantenimento)
    /// e accumula gli eventuali costi di funding nel PnL del trade.
    /// </summary>
    private sealed class Portfolio(decimal initialCapital, decimal feePercent, decimal sizePercent, decimal leverage)
    {
        private readonly decimal _feeFrac = feePercent / 100m;
        private readonly decimal _marginFrac = sizePercent / 100m;
        private readonly decimal _leverage = Math.Max(1m, leverage);

        private int _side; // 0 flat, +1 long, -1 short
        private decimal _qty;
        private decimal _entryPrice;
        private decimal _entryFee;
        private decimal _margin;
        private decimal _notionalEntry;
        private decimal _fundingAccrued;
        private DateTime _entryTime;

        public decimal Cash { get; private set; } = initialCapital;
        public List<BacktestTrade> Trades { get; } = new();
        public int LiquidationCount { get; private set; }

        public bool IsFlat => _side == 0;
        public bool IsLong => _side == 1;
        public bool IsShort => _side == -1;

        /// <summary>Nozionale di apertura della posizione corrente (0 se flat).</summary>
        public decimal OpenNotional => _side == 0 ? 0m : _notionalEntry;

        public decimal Equity(decimal price)
            => _side == 0 ? Cash : Cash + _margin + UnrealizedPnl(price);

        private decimal UnrealizedPnl(decimal price)
            => _side == 1 ? _qty * (price - _entryPrice) : _qty * (_entryPrice - price);

        /// <summary>
        /// Prezzo al quale margine + PnL scende al margine di mantenimento: liquidazione.
        /// Long: sotto l'entry; short: sopra. Con leva 1 e' cosi' lontano da non scattare mai
        /// in pratica (equivale a perdere quasi il 100% del nozionale).
        /// </summary>
        public decimal LiquidationPrice(decimal maintenanceFrac)
        {
            if (_side == 0 || _qty == 0m) return 0m;
            return MarginMath.LiquidationPrice(_entryPrice, _qty, _margin, _notionalEntry, isLong: _side == 1, maintenanceFrac);
        }

        /// <summary>Addebita il funding pro-rata sul nozionale aperto (entra nel PnL del trade).</summary>
        public void ChargeFunding(decimal amount)
        {
            if (_side == 0 || amount <= 0m) return;
            Cash -= amount;
            _fundingAccrued += amount;
        }

        public void OpenLong(decimal price, DateTime ts) => Open(1, price, ts);
        public void OpenShort(decimal price, DateTime ts) => Open(-1, price, ts);

        private void Open(int side, decimal price, DateTime ts)
        {
            var margin = Equity(price) * _marginFrac;
            if (margin <= 0m || price <= 0m) return;
            var notional = margin * _leverage;
            _qty = notional / price;
            _entryFee = notional * _feeFrac;
            Cash -= margin + _entryFee;
            _margin = margin;
            _notionalEntry = notional;
            _fundingAccrued = 0m;
            _side = side;
            _entryPrice = price;
            _entryTime = ts;
        }

        public void Close(decimal price, DateTime ts, bool liquidated = false)
        {
            if (_side == 0) return;

            var pnlRaw = UnrealizedPnl(price);
            var exitFee = _qty * price * _feeFrac;
            Cash += _margin + pnlRaw - exitFee;

            var pnl = pnlRaw - _entryFee - exitFee - _fundingAccrued;
            Trades.Add(new BacktestTrade
            {
                EntryTime = _entryTime,
                EntryPrice = _entryPrice,
                ExitTime = ts,
                ExitPrice = price,
                Quantity = _qty,
                Pnl = pnl,
                PnlPercent = _notionalEntry == 0m ? 0m : pnl / _notionalEntry * 100m,
                Direction = _side == 1 ? "Long" : "Short",
                WasLiquidated = liquidated,
            });
            if (liquidated) LiquidationCount++;

            _side = 0; _qty = 0; _entryPrice = 0; _entryFee = 0;
            _margin = 0; _notionalEntry = 0; _fundingAccrued = 0;
        }
    }
}
