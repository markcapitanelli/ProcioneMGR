using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Analysis;

/// <summary>
/// Analisi delle escursioni di barra per il posizionamento probabilistico dello stop loss
/// (Trombetta, cap. 4, "Probabilita' e direzione") + effetto memoria (autocorrelazione
/// ritardata delle variazioni percentuali).
///
/// Idea: nelle giornate che chiudono positive, la massima ricorrezione open->low e' contenuta;
/// il 95esimo/99esimo percentile di quella distribuzione e' un livello di stop oltre il quale
/// la probabilita' che la barra si chiuda comunque positiva crolla. Simmetrico per lo short
/// con l'escursione open->high delle giornate negative.
/// </summary>
public sealed class ExcursionAnalyzer
{
    /// <summary>
    /// Percentili delle escursioni avverse, per stop loss di posizioni aperte sull'open di barra.
    /// Le distanze sono espresse in % dell'open (adimensionali, confrontabili tra strumenti).
    /// </summary>
    public StopLossSuggestion SuggestStopLoss(IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        // Long: ricorrezione open->low delle sole barre POSITIVE (close > open).
        var openLowPositive = new List<decimal>();
        // Short: ricorrezione open->high delle sole barre NEGATIVE (close < open).
        var highOpenNegative = new List<decimal>();

        foreach (var c in candles)
        {
            if (c.Open <= 0m) continue;
            if (c.Close > c.Open)
            {
                openLowPositive.Add((c.Open - c.Low) / c.Open * 100m);
            }
            else if (c.Close < c.Open)
            {
                highOpenNegative.Add((c.High - c.Open) / c.Open * 100m);
            }
        }

        openLowPositive.Sort();
        highOpenNegative.Sort();

        return new StopLossSuggestion
        {
            PositiveBars = openLowPositive.Count,
            NegativeBars = highOpenNegative.Count,
            LongStopPercentile95 = Optimization.TradeStatistics.Percentile(openLowPositive, 0.95m),
            LongStopPercentile99 = Optimization.TradeStatistics.Percentile(openLowPositive, 0.99m),
            ShortStopPercentile95 = Optimization.TradeStatistics.Percentile(highOpenNegative, 0.95m),
            ShortStopPercentile99 = Optimization.TradeStatistics.Percentile(highOpenNegative, 0.99m),
        };
    }

    /// <summary>
    /// Percentili delle escursioni FAVOREVOLI, per il take profit — speculare a
    /// <see cref="SuggestStopLoss"/>. Long: escursione open->high delle sole barre POSITIVE (quanto
    /// corrono i vincitori verso l'alto prima di chiudere); Short: open->low delle sole barre
    /// NEGATIVE. Il 95°/99° percentile è un target ampio, raggiunto solo dai movimenti eccezionali:
    /// blocca il profitto sugli outlier senza tagliare i vincitori normali (stessa filosofia dello
    /// stop a percentile). Distanze in % dell'open.
    /// </summary>
    public TakeProfitSuggestion SuggestTakeProfit(IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        var openHighPositive = new List<decimal>();  // long: quanto sale una barra vincente
        var openLowNegative = new List<decimal>();    // short: quanto scende una barra vincente (per lo short)

        foreach (var c in candles)
        {
            if (c.Open <= 0m) continue;
            if (c.Close > c.Open)
            {
                openHighPositive.Add((c.High - c.Open) / c.Open * 100m);
            }
            else if (c.Close < c.Open)
            {
                openLowNegative.Add((c.Open - c.Low) / c.Open * 100m);
            }
        }

        openHighPositive.Sort();
        openLowNegative.Sort();

        return new TakeProfitSuggestion
        {
            PositiveBars = openHighPositive.Count,
            NegativeBars = openLowNegative.Count,
            LongTakeProfitPercentile95 = Optimization.TradeStatistics.Percentile(openHighPositive, 0.95m),
            LongTakeProfitPercentile99 = Optimization.TradeStatistics.Percentile(openHighPositive, 0.99m),
            ShortTakeProfitPercentile95 = Optimization.TradeStatistics.Percentile(openLowNegative, 0.95m),
            ShortTakeProfitPercentile99 = Optimization.TradeStatistics.Percentile(openLowNegative, 0.99m),
        };
    }

    /// <summary>
    /// Bracket SL+TP pronto da applicare per un dato lato, calcolato dai percentili di escursione
    /// (default 95°). È il "calcolo automatico" usato dalla pipeline e dal backtest: distanze in %
    /// dal prezzo d'ingresso. Ritorna 0 dove non ci sono abbastanza barre del segno richiesto.
    /// </summary>
    public RiskBracket SuggestBracket(IReadOnlyList<OhlcvData> candles, OrderSide side, bool use99thPercentile = false)
    {
        var sl = SuggestStopLoss(candles);
        var tp = SuggestTakeProfit(candles);
        return side == OrderSide.Buy
            ? new RiskBracket(
                use99thPercentile ? sl.LongStopPercentile99 : sl.LongStopPercentile95,
                use99thPercentile ? tp.LongTakeProfitPercentile99 : tp.LongTakeProfitPercentile95)
            : new RiskBracket(
                use99thPercentile ? sl.ShortStopPercentile99 : sl.ShortStopPercentile95,
                use99thPercentile ? tp.ShortTakeProfitPercentile99 : tp.ShortTakeProfitPercentile95);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // R1.5 — MAE/MFE sull'ORIZZONTE DI DETENZIONE, condizionato per regime di volatilità.
    //
    // Le escursioni a barra singola (open→low/high di UNA candela) sottostimano il rischio di uno
    // stop: un trade vive più barre e la sua massima escursione avversa (MAE) / favorevole (MFE) si
    // accumula sull'intero periodo di detenzione. Qui ogni barra è un ingresso ipotetico tenuto per
    // <c>horizon</c> barre; si misura MAE/MFE rispetto al prezzo d'ingresso e — stessa filosofia dei
    // percentili sui SOLI vincitori del metodo a barra singola — si prendono i percentili sui trade
    // che chiudono in profitto all'orizzonte. Inoltre si condiziona per regime di volatilità
    // (terziali dell'ATR% causale all'ingresso): lo stop giusto in mercato calmo è troppo stretto in
    // mercato agitato. Strumento di CALIBRAZIONE storica (l'escursione guarda avanti per costruire la
    // distribuzione), non un segnale live.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bracket SL+TP da MAE/MFE su <paramref name="horizon"/> barre, disaggregato per regime di
    /// volatilità (Low/Normal/High via terziali dell'ATR% all'ingresso) più il complessivo. SL =
    /// <paramref name="percentile"/>° percentile della MAE dei trade vincenti; TP = stesso percentile
    /// della MFE. Distanze in % dal prezzo d'ingresso (close di barra). <see cref="RegimeConditionedBracket.CurrentRegime"/>
    /// è il regime dell'ultima candela, per l'uso adattivo.
    /// </summary>
    internal RegimeConditionedBracket SuggestHorizonBracket(
        IReadOnlyList<OhlcvData> candles, OrderSide side, int horizon = 10, decimal percentile = 0.95m, int atrPeriod = 14)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (horizon < 1) throw new ArgumentOutOfRangeException(nameof(horizon));
        if (atrPeriod < 1) throw new ArgumentOutOfRangeException(nameof(atrPeriod));

        var atrPct = CausalAtrPercent(candles, atrPeriod);
        var samples = CollectHorizonSamples(candles, side, horizon, atrPeriod, atrPct);
        var (lo, hi) = VolTerciles(samples.Select(s => s.EntryAtrPercent));

        var byRegime = new Dictionary<VolatilityRegime, HorizonExcursion>();
        foreach (var reg in (ReadOnlySpan<VolatilityRegime>)[VolatilityRegime.Low, VolatilityRegime.Normal, VolatilityRegime.High])
        {
            byRegime[reg] = Aggregate(samples.Where(s => Classify(s.EntryAtrPercent, lo, hi) == reg), horizon, percentile);
        }

        var current = candles.Count > atrPeriod && atrPct[^1] > 0m ? Classify(atrPct[^1], lo, hi) : VolatilityRegime.Normal;
        return new RegimeConditionedBracket(side, horizon, percentile, byRegime, Aggregate(samples, horizon, percentile), current);
    }

    /// <summary>
    /// Auto SL/TP ADATTIVO: il bracket MAE/MFE del regime di volatilità CORRENTE (ultima candela). Se
    /// quel regime ha meno di <paramref name="minRegimeSamples"/> trade vincenti (stima instabile),
    /// ripiega sul complessivo. È il "calcolo automatico" data-driven consapevole del regime.
    /// </summary>
    public RiskBracket SuggestAdaptiveBracket(
        IReadOnlyList<OhlcvData> candles, OrderSide side, int horizon = 10, decimal percentile = 0.95m,
        int atrPeriod = 14, int minRegimeSamples = 30)
    {
        var b = SuggestHorizonBracket(candles, side, horizon, percentile, atrPeriod);
        var chosen = b.ByRegime.TryGetValue(b.CurrentRegime, out var r) && r.Samples >= minRegimeSamples ? r : b.Overall;
        if (chosen.Samples == 0) chosen = b.Overall;
        return new RiskBracket(chosen.StopPercentile, chosen.TakeProfitPercentile);
    }

    /// <summary>MAE/MFE (in % dall'ingresso) ed esito di ogni ingresso ipotetico tenuto per horizon barre.</summary>
    private readonly record struct HorizonSample(decimal Adverse, decimal Favorable, decimal EntryAtrPercent, bool FavorableOutcome);

    private static List<HorizonSample> CollectHorizonSamples(
        IReadOnlyList<OhlcvData> candles, OrderSide side, int horizon, int atrPeriod, decimal[] atrPct)
    {
        var samples = new List<HorizonSample>();
        // Ingresso al close della barra i (dopo il warm-up ATR); detenzione su (i, i+horizon].
        for (var i = atrPeriod; i + horizon < candles.Count; i++)
        {
            var entry = candles[i].Close;
            if (entry <= 0m) continue;

            decimal maxHigh = decimal.MinValue, minLow = decimal.MaxValue;
            for (var j = i + 1; j <= i + horizon; j++)
            {
                if (candles[j].High > maxHigh) maxHigh = candles[j].High;
                if (candles[j].Low < minLow) minLow = candles[j].Low;
            }
            var exit = candles[i + horizon].Close;

            decimal adverse, favorable;
            bool favorableOutcome;
            if (side == OrderSide.Buy)
            {
                adverse = (entry - minLow) / entry * 100m;    // massimo drawdown del long
                favorable = (maxHigh - entry) / entry * 100m; // massimo runup del long
                favorableOutcome = exit > entry;
            }
            else
            {
                adverse = (maxHigh - entry) / entry * 100m;    // per lo short il rischio è verso l'alto
                favorable = (entry - minLow) / entry * 100m;
                favorableOutcome = exit < entry;
            }

            samples.Add(new HorizonSample(Math.Max(0m, adverse), Math.Max(0m, favorable), atrPct[i], favorableOutcome));
        }
        return samples;
    }

    /// <summary>Percentili di MAE (→SL) e MFE (→TP) sui SOLI trade vincenti del sottoinsieme.</summary>
    private static HorizonExcursion Aggregate(IEnumerable<HorizonSample> samples, int horizon, decimal percentile)
    {
        var mae = new List<decimal>();
        var mfe = new List<decimal>();
        foreach (var s in samples)
        {
            if (!s.FavorableOutcome) continue;
            mae.Add(s.Adverse);
            mfe.Add(s.Favorable);
        }
        mae.Sort();
        mfe.Sort();
        return new HorizonExcursion(
            horizon, mae.Count,
            Optimization.TradeStatistics.Percentile(mae, percentile),
            Optimization.TradeStatistics.Percentile(mfe, percentile),
            percentile);
    }

    /// <summary>ATR% causale (SMA del true range su period barre, / close · 100). 0 durante il warm-up.</summary>
    private static decimal[] CausalAtrPercent(IReadOnlyList<OhlcvData> candles, int period)
    {
        var n = candles.Count;
        var atrPct = new decimal[n];
        if (n == 0) return atrPct;

        var tr = new decimal[n];
        tr[0] = candles[0].High - candles[0].Low;
        for (var i = 1; i < n; i++)
        {
            var h = candles[i].High;
            var l = candles[i].Low;
            var pc = candles[i - 1].Close;
            tr[i] = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
        }
        for (var i = period; i < n; i++)
        {
            decimal sum = 0m;
            for (var k = i - period + 1; k <= i; k++) sum += tr[k];
            var c = candles[i].Close;
            atrPct[i] = c > 0m ? sum / period / c * 100m : 0m;
        }
        return atrPct;
    }

    /// <summary>Soglie di terziale (33°/67° percentile) dell'ATR% d'ingresso, per i regimi di volatilità.</summary>
    private static (decimal Lo, decimal Hi) VolTerciles(IEnumerable<decimal> atrPercents)
    {
        var sorted = atrPercents.Where(v => v > 0m).OrderBy(v => v).ToList();
        if (sorted.Count < 3) return (0m, decimal.MaxValue); // dati insufficienti ⇒ tutto "Normal"
        return (Optimization.TradeStatistics.Percentile(sorted, 0.3333m),
                Optimization.TradeStatistics.Percentile(sorted, 0.6667m));
    }

    private static VolatilityRegime Classify(decimal atrPct, decimal lo, decimal hi)
        => atrPct <= lo ? VolatilityRegime.Low : atrPct >= hi ? VolatilityRegime.High : VolatilityRegime.Normal;

    /// <summary>
    /// Autocorrelazione ritardata ("effetto memoria", cap. 4): correlazione di Pearson tra la
    /// serie delle variazioni percentuali e le sue copie ritardate di 1..maxLag periodi.
    /// Correlazioni "deboli" (10-30%) su lag brevi sono gia' sfruttabili come filtro operativo.
    /// </summary>
    /// <param name="values">Serie di prezzi (es. close, high o avgprice = (O+H+L+C)/4).</param>
    public IReadOnlyList<LagCorrelation> LaggedAutocorrelation(IReadOnlyList<decimal> values, int maxLag = 10)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (maxLag < 1) throw new ArgumentOutOfRangeException(nameof(maxLag));

        // Variazioni percentuali.
        var returns = new List<double>(values.Count);
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i - 1] == 0m) continue;
            returns.Add((double)((values[i] - values[i - 1]) / values[i - 1]));
        }

        var result = new List<LagCorrelation>(maxLag);
        for (var lag = 1; lag <= maxLag; lag++)
        {
            result.Add(new LagCorrelation(lag, Pearson(returns, lag)));
        }
        return result;
    }

    /// <summary>
    /// Probabilita' di continuazione: quante volte, dopo una variazione positiva della serie
    /// (superiore a <paramref name="thresholdPercent"/>), la variazione successiva e' ancora
    /// positiva. E' il "test di consistenza" che il libro applica ai massimi di barra.
    /// </summary>
    public ContinuationStats ContinuationProbability(IReadOnlyList<decimal> values, decimal thresholdPercent = 0m)
    {
        ArgumentNullException.ThrowIfNull(values);

        int setups = 0, successes = 0;
        decimal? prevChange = null;
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i - 1] == 0m) { prevChange = null; continue; }
            var change = (values[i] - values[i - 1]) / values[i - 1] * 100m;
            if (prevChange.HasValue && prevChange.Value > thresholdPercent)
            {
                setups++;
                if (change > 0m) successes++;
            }
            prevChange = change;
        }

        return new ContinuationStats(setups, successes, setups == 0 ? 0m : (decimal)successes / setups * 100m);
    }

    private static decimal Pearson(IReadOnlyList<double> series, int lag)
    {
        var n = series.Count - lag;
        if (n < 3) return 0m;

        double sumX = 0, sumY = 0;
        for (var i = 0; i < n; i++)
        {
            sumX += series[i + lag]; // serie "attuale"
            sumY += series[i];       // copia ritardata
        }
        double meanX = sumX / n, meanY = sumY / n;

        double cov = 0, varX = 0, varY = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = series[i + lag] - meanX;
            var dy = series[i] - meanY;
            cov += dx * dy;
            varX += dx * dx;
            varY += dy * dy;
        }
        var denom = Math.Sqrt(varX * varY);
        if (denom == 0 || double.IsNaN(denom)) return 0m;
        var r = cov / denom;
        return double.IsNaN(r) ? 0m : (decimal)r;
    }
}

/// <summary>Livelli di stop loss suggeriti dalle distribuzioni delle escursioni avverse (% dell'open).</summary>
public sealed record StopLossSuggestion
{
    public int PositiveBars { get; init; }
    public int NegativeBars { get; init; }
    /// <summary>Distanza % sotto l'open che contiene il 95% delle ricorrezioni delle barre positive.</summary>
    public decimal LongStopPercentile95 { get; init; }
    public decimal LongStopPercentile99 { get; init; }
    /// <summary>Distanza % sopra l'open che contiene il 95% delle escursioni delle barre negative.</summary>
    public decimal ShortStopPercentile95 { get; init; }
    public decimal ShortStopPercentile99 { get; init; }
}

/// <summary>Livelli di take profit suggeriti dalle distribuzioni delle escursioni favorevoli (% dell'open).</summary>
public sealed record TakeProfitSuggestion
{
    public int PositiveBars { get; init; }
    public int NegativeBars { get; init; }
    /// <summary>Distanza % sopra l'entry che cattura il 95% delle escursioni favorevoli delle barre positive (long).</summary>
    public decimal LongTakeProfitPercentile95 { get; init; }
    public decimal LongTakeProfitPercentile99 { get; init; }
    /// <summary>Distanza % sotto l'entry che cattura il 95% delle escursioni favorevoli delle barre negative (short).</summary>
    public decimal ShortTakeProfitPercentile95 { get; init; }
    public decimal ShortTakeProfitPercentile99 { get; init; }
}

/// <summary>Bracket protettivo pronto da applicare: distanze % dall'entry per stop loss e take profit (0 = non disponibile).</summary>
public sealed record RiskBracket(decimal StopLossPercent, decimal TakeProfitPercent);

/// <summary>Regime di volatilità all'ingresso, per terziali dell'ATR% causale (R1.5).</summary>
public enum VolatilityRegime { Low, Normal, High }

/// <summary>
/// Escursioni MAE/MFE su un orizzonte di detenzione (R1.5): SL/TP come percentile della massima
/// escursione avversa/favorevole dei trade vincenti. Distanze in % dal prezzo d'ingresso.
/// </summary>
public sealed record HorizonExcursion(int Horizon, int Samples, decimal StopPercentile, decimal TakeProfitPercentile, decimal Percentile);

/// <summary>
/// Bracket MAE/MFE disaggregato per regime di volatilità più il complessivo, con il regime corrente
/// (ultima candela) per l'uso adattivo. <see cref="ByRegime"/> contiene sempre le tre chiavi
/// Low/Normal/High (Samples=0 dove non ci sono abbastanza trade).
/// </summary>
public sealed record RegimeConditionedBracket(
    OrderSide Side,
    int Horizon,
    decimal Percentile,
    IReadOnlyDictionary<VolatilityRegime, HorizonExcursion> ByRegime,
    HorizonExcursion Overall,
    VolatilityRegime CurrentRegime);

/// <summary>Correlazione di Pearson tra la serie delle variazioni e la sua copia ritardata di Lag periodi.</summary>
public sealed record LagCorrelation(int Lag, decimal Correlation);

/// <summary>Esito del test di continuazione (occorrenze e probabilita' di successo).</summary>
public sealed record ContinuationStats(int Setups, int Successes, decimal SuccessPercent);
