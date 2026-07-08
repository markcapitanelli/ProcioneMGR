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

    /// <summary>
    /// Anatomia della singola barra (i "mattoni elementari" del cap. 4). Liste allineate
    /// per indice alle candele. Percentuali in [0,100]; ClosePerc = dove chiude la barra
    /// rispetto al proprio range (0 = sul low, 100 = sul high).
    /// </summary>
    public IReadOnlyList<BarAnatomy> ComputeBarAnatomy(IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var result = new List<BarAnatomy>(candles.Count);
        foreach (var c in candles)
        {
            var range = c.High - c.Low;
            var body = c.Close - c.Open;
            result.Add(new BarAnatomy(
                Timestamp: c.TimestampUtc,
                Body: body,
                Range: range,
                CloseOpen: c.Close - c.Open,
                OpenLow: c.Open - c.Low,
                HighOpen: c.High - c.Open,
                CloseLow: c.Close - c.Low,
                HighClose: c.High - c.Close,
                BodyRangePercent: range == 0m ? 0m : Math.Abs(body) / range * 100m,
                ClosePercent: range == 0m ? 0m : (c.Close - c.Low) / range * 100m,
                IsWhite: c.Close > c.Open));
        }
        return result;
    }

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

/// <summary>Attributi elementari di una barra (cap. 4 del libro).</summary>
public sealed record BarAnatomy(
    DateTime Timestamp,
    decimal Body,
    decimal Range,
    decimal CloseOpen,
    decimal OpenLow,
    decimal HighOpen,
    decimal CloseLow,
    decimal HighClose,
    decimal BodyRangePercent,
    decimal ClosePercent,
    bool IsWhite);

/// <summary>Correlazione di Pearson tra la serie delle variazioni e la sua copia ritardata di Lag periodi.</summary>
public sealed record LagCorrelation(int Lag, decimal Correlation);

/// <summary>Esito del test di continuazione (occorrenze e probabilita' di successo).</summary>
public sealed record ContinuationStats(int Setups, int Successes, decimal SuccessPercent);
