using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.Optimization;

/// <summary>Statistiche per la valutazione delle strategie (Sharpe ratio annualizzato).</summary>
public static class Statistics
{
    /// <summary>Numero di periodi all'anno per timeframe (per annualizzare lo Sharpe).</summary>
    public static int PeriodsPerYear(string timeframe) => timeframe switch
    {
        "1m" => 365 * 24 * 60,
        "5m" => 365 * 24 * 12,
        "15m" => 365 * 24 * 4,
        "30m" => 365 * 24 * 2,
        "1h" => 365 * 24,
        "4h" => 365 * 6,
        "1d" => 365,
        _ => 365,
    };

    /// <summary>
    /// Sharpe ratio annualizzato calcolato sui rendimenti periodici dell'equity curve.
    ///   returns[i] = (equity[i] - equity[i-1]) / equity[i-1]
    ///   Sharpe = (mean - rfPerPeriod) / stdDev * sqrt(periodsPerYear)
    /// Ritorna 0 se i dati sono insufficienti o se stdDev == 0 (niente divisioni per zero).
    /// </summary>
    public static decimal SharpeRatio(IReadOnlyList<EquityPoint> equityCurve, int periodsPerYear, decimal riskFreeRateAnnual = 0.02m)
    {
        if (equityCurve is null || equityCurve.Count < 3 || periodsPerYear <= 0)
        {
            return 0m;
        }

        // Rendimenti periodici.
        var returns = new List<decimal>(equityCurve.Count - 1);
        for (var i = 1; i < equityCurve.Count; i++)
        {
            var prev = equityCurve[i - 1].Capital;
            if (prev <= 0m)
            {
                continue; // capitale azzerato/negativo: salta
            }
            returns.Add((equityCurve[i].Capital - prev) / prev);
        }

        if (returns.Count < 2)
        {
            return 0m;
        }

        // Media e deviazione standard (popolazione) dei rendimenti.
        decimal sum = 0m;
        foreach (var r in returns) sum += r;
        var mean = sum / returns.Count;

        decimal sumSq = 0m;
        foreach (var r in returns)
        {
            var d = r - mean;
            sumSq += d * d;
        }
        var variance = sumSq / returns.Count;
        if (variance <= 0m)
        {
            return 0m; // tutti i rendimenti uguali -> volatilita' nulla
        }
        var stdDev = Sqrt(variance);
        if (stdDev == 0m)
        {
            return 0m;
        }

        var rfPerPeriod = riskFreeRateAnnual / periodsPerYear;
        var sharpePerPeriod = (mean - rfPerPeriod) / stdDev;
        return sharpePerPeriod * Sqrt(periodsPerYear);
    }

    /// <summary>Radice quadrata in decimal (Newton-Raphson), come negli indicatori.</summary>
    internal static decimal Sqrt(decimal value)
    {
        if (value < 0m) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == 0m) return 0m;

        var guess = (decimal)Math.Sqrt((double)value);
        for (var i = 0; i < 12; i++)
        {
            if (guess == 0m) break;
            var next = (guess + value / guess) / 2m;
            if (next == guess) break;
            guess = next;
        }
        return guess;
    }

    /// <summary>Rendimenti periodici dell'equity curve: returns[i] = (equity[i]-equity[i-1])/equity[i-1].</summary>
    private static List<decimal> PeriodicReturns(IReadOnlyList<EquityPoint> equityCurve)
    {
        var returns = new List<decimal>();
        if (equityCurve is null) return returns;
        for (var i = 1; i < equityCurve.Count; i++)
        {
            var prev = equityCurve[i - 1].Capital;
            if (prev <= 0m) continue;
            returns.Add((equityCurve[i].Capital - prev) / prev);
        }
        return returns;
    }

    /// <summary>
    /// Sortino ratio annualizzato: come lo Sharpe ma il denominatore è la downside deviation
    /// (solo i rendimenti sotto il MAR contribuiscono, gli altri contano 0), non la deviazione
    /// standard totale. Penalizza solo il rischio "cattivo".
    /// </summary>
    public static decimal SortinoRatio(IReadOnlyList<EquityPoint> equityCurve, int periodsPerYear,
        decimal riskFreeRateAnnual = 0.02m, decimal minimumAcceptableReturnPerPeriod = 0m)
    {
        var returns = PeriodicReturns(equityCurve);
        if (returns.Count < 2 || periodsPerYear <= 0) return 0m;

        decimal sum = 0m;
        foreach (var r in returns) sum += r;
        var mean = sum / returns.Count;

        decimal downsideSumSq = 0m;
        foreach (var r in returns)
        {
            var shortfall = minimumAcceptableReturnPerPeriod - r;
            if (shortfall > 0m) downsideSumSq += shortfall * shortfall;
        }
        var downsideDeviation = Sqrt(downsideSumSq / returns.Count);
        if (downsideDeviation == 0m) return 0m;

        var rfPerPeriod = riskFreeRateAnnual / periodsPerYear;
        return (mean - rfPerPeriod) / downsideDeviation * Sqrt(periodsPerYear);
    }

    /// <summary>Rendimento annualizzato composto (CAGR) dai rendimenti periodici dell'equity curve.</summary>
    public static decimal AnnualizedReturn(IReadOnlyList<EquityPoint> equityCurve, int periodsPerYear)
    {
        if (equityCurve is null || equityCurve.Count < 2 || periodsPerYear <= 0) return 0m;
        var first = equityCurve[0].Capital;
        var last = equityCurve[^1].Capital;
        if (first <= 0m || last <= 0m) return 0m;

        var totalGrowth = (double)(last / first);
        var periods = equityCurve.Count - 1;
        var exponent = (double)periodsPerYear / periods;
        var annualizedGrowth = Math.Pow(totalGrowth, exponent);

        // Curva troppo corta rispetto a periodsPerYear -> estrapolazione fuori scala: il dato
        // non è significativo (non un vero CAGR). Evita l'overflow nel cast a decimal.
        if (double.IsNaN(annualizedGrowth) || double.IsInfinity(annualizedGrowth) || annualizedGrowth > 7e28)
        {
            return 0m;
        }
        return (decimal)annualizedGrowth - 1m;
    }

    /// <summary>Massimo drawdown (%) dell'equity curve, picco-a-valle.</summary>
    public static decimal MaxDrawdownPercent(IReadOnlyList<EquityPoint> equityCurve)
    {
        if (equityCurve is null || equityCurve.Count == 0) return 0m;
        var peak = decimal.MinValue;
        var maxDd = 0m;
        foreach (var p in equityCurve)
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
    /// Calmar ratio: rendimento annualizzato diviso il massimo drawdown (in valore assoluto).
    /// Misura il rendimento "per unità" del peggior scenario di perdita subito.
    /// </summary>
    public static decimal CalmarRatio(IReadOnlyList<EquityPoint> equityCurve, int periodsPerYear)
    {
        var maxDd = MaxDrawdownPercent(equityCurve) / 100m;
        if (maxDd == 0m) return 0m;
        return AnnualizedReturn(equityCurve, periodsPerYear) / maxDd;
    }

    /// <summary>
    /// Omega ratio rispetto a una soglia di rendimento periodico (default 0): rapporto fra la
    /// somma dei guadagni sopra soglia e la somma delle perdite sotto soglia. Un fattore &gt; 1
    /// indica che la distribuzione dei rendimenti pesa più a favore che contro la soglia.
    /// Ritorna 0 se non ci sono perdite sotto soglia (evita una divisione per zero/infinito).
    /// </summary>
    public static decimal OmegaRatio(IReadOnlyList<EquityPoint> equityCurve, decimal thresholdPerPeriod = 0m)
    {
        var returns = PeriodicReturns(equityCurve);
        if (returns.Count == 0) return 0m;

        decimal gains = 0m, losses = 0m;
        foreach (var r in returns)
        {
            var diff = r - thresholdPerPeriod;
            if (diff > 0m) gains += diff; else losses += -diff;
        }
        return losses == 0m ? 0m : gains / losses;
    }

    /// <summary>Percentile (interpolazione lineare) di una lista di rendimenti, p in [0,1].</summary>
    private static decimal Percentile(List<decimal> sortedReturns, decimal p)
    {
        if (sortedReturns.Count == 0) return 0m;
        if (sortedReturns.Count == 1) return sortedReturns[0];

        var rank = p * (sortedReturns.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sortedReturns[lower];

        var weight = rank - lower;
        return sortedReturns[lower] + (sortedReturns[upper] - sortedReturns[lower]) * weight;
    }

    /// <summary>
    /// Tail ratio: rapporto fra il 95° e il 5° percentile (in valore assoluto) dei rendimenti
    /// periodici. Alto -> le code positive sono più ampie di quelle negative.
    /// </summary>
    public static decimal TailRatio(IReadOnlyList<EquityPoint> equityCurve)
    {
        var returns = PeriodicReturns(equityCurve);
        if (returns.Count < 3) return 0m;
        returns.Sort();

        var p95 = Math.Abs(Percentile(returns, 0.95m));
        var p5 = Math.Abs(Percentile(returns, 0.05m));
        return p5 == 0m ? 0m : p95 / p5;
    }

    /// <summary>
    /// Value at Risk storico: perdita (valore positivo, frazione di capitale) attesa nel
    /// worst-case al livello di confidenza dato, stimata dal percentile empirico dei rendimenti.
    /// Es. confidence=0.95 -> VaR = -5° percentile dei rendimenti.
    /// </summary>
    public static decimal HistoricalVaR(IReadOnlyList<EquityPoint> equityCurve, decimal confidence = 0.95m)
    {
        var returns = PeriodicReturns(equityCurve);
        if (returns.Count < 3) return 0m;
        returns.Sort();
        var tail = 1m - confidence;
        return -Percentile(returns, tail);
    }

    /// <summary>
    /// Conditional VaR (Expected Shortfall): media dei rendimenti nella coda oltre il VaR,
    /// espressa come perdita positiva. Più informativo del VaR perché misura QUANTO si perde
    /// in media nello scenario peggiore, non solo la soglia.
    /// </summary>
    public static decimal HistoricalCVaR(IReadOnlyList<EquityPoint> equityCurve, decimal confidence = 0.95m)
    {
        var returns = PeriodicReturns(equityCurve);
        if (returns.Count < 3) return 0m;
        returns.Sort();
        var tail = 1m - confidence;
        var cutoffIndex = Math.Max(1, (int)Math.Ceiling(tail * returns.Count));
        var worst = returns.Take(cutoffIndex).ToList();
        if (worst.Count == 0) return 0m;

        decimal sum = 0m;
        foreach (var r in worst) sum += r;
        return -(sum / worst.Count);
    }

    /// <summary>
    /// Durata (in numero di periodi) del più lungo drawdown: dal picco fino al momento in cui
    /// l'equity torna a un nuovo massimo storico. Se il drawdown corrente non è ancora recuperato
    /// alla fine della serie, conta fino all'ultimo punto disponibile.
    /// </summary>
    public static int MaxDrawdownDurationPeriods(IReadOnlyList<EquityPoint> equityCurve)
    {
        if (equityCurve is null || equityCurve.Count == 0) return 0;

        var peak = equityCurve[0].Capital;
        var peakIndex = 0;
        var maxDuration = 0;
        var inDrawdown = false;
        for (var i = 1; i < equityCurve.Count; i++)
        {
            if (equityCurve[i].Capital >= peak)
            {
                if (inDrawdown)
                {
                    var duration = i - peakIndex;
                    if (duration > maxDuration) maxDuration = duration;
                    inDrawdown = false;
                }
                peak = equityCurve[i].Capital;
                peakIndex = i;
            }
            else
            {
                inDrawdown = true;
            }
        }
        if (inDrawdown)
        {
            var duration = equityCurve.Count - 1 - peakIndex;
            if (duration > maxDuration) maxDuration = duration;
        }
        return maxDuration;
    }

    /// <summary>
    /// Esposizione (%): frazione del tempo totale della curva in cui una posizione era aperta.
    /// I trade ancora aperti a fine periodo (ExitTime nullo) contano fino all'ultimo punto della
    /// curva.
    /// </summary>
    public static decimal ExposurePercent(IReadOnlyList<BacktestTrade> trades, IReadOnlyList<EquityPoint> equityCurve)
    {
        if (trades is null || trades.Count == 0 || equityCurve is null || equityCurve.Count < 2) return 0m;

        var curveStart = equityCurve[0].Timestamp;
        var curveEnd = equityCurve[^1].Timestamp;
        var totalTicks = (curveEnd - curveStart).Ticks;
        if (totalTicks <= 0) return 0m;

        long inMarketTicks = 0;
        foreach (var t in trades)
        {
            var entry = t.EntryTime < curveStart ? curveStart : t.EntryTime;
            var exit = t.ExitTime ?? curveEnd;
            if (exit > curveEnd) exit = curveEnd;
            if (exit > entry) inMarketTicks += (exit - entry).Ticks;
        }
        return (decimal)inMarketTicks / totalTicks * 100m;
    }

    /// <summary>Hit-rate (%): percentuale di trade chiusi in profitto.</summary>
    public static decimal HitRate(IReadOnlyList<BacktestTrade> trades)
    {
        if (trades is null || trades.Count == 0) return 0m;
        var winning = trades.Count(t => t.Pnl > 0m);
        return (decimal)winning / trades.Count * 100m;
    }

    /// <summary>Tearsheet completo: tutte le metriche di performance/rischio in un'unica chiamata.</summary>
    public static TearsheetMetrics ComputeTearsheet(
        IReadOnlyList<EquityPoint> equityCurve,
        IReadOnlyList<BacktestTrade> trades,
        int periodsPerYear,
        decimal riskFreeRateAnnual = 0.02m) => new()
    {
        Sharpe = SharpeRatio(equityCurve, periodsPerYear, riskFreeRateAnnual),
        Sortino = SortinoRatio(equityCurve, periodsPerYear, riskFreeRateAnnual),
        Calmar = CalmarRatio(equityCurve, periodsPerYear),
        Omega = OmegaRatio(equityCurve),
        TailRatio = TailRatio(equityCurve),
        ValueAtRisk95 = HistoricalVaR(equityCurve),
        ConditionalValueAtRisk95 = HistoricalCVaR(equityCurve),
        MaxDrawdownPercent = MaxDrawdownPercent(equityCurve),
        MaxDrawdownDurationPeriods = MaxDrawdownDurationPeriods(equityCurve),
        ExposurePercent = ExposurePercent(trades, equityCurve),
        HitRatePercent = HitRate(trades),
        AnnualizedReturnPercent = AnnualizedReturn(equityCurve, periodsPerYear) * 100m,
    };
}

/// <summary>Insieme completo di metriche di performance/rischio per un backtest (controparte di pyfolio).</summary>
public sealed record TearsheetMetrics
{
    public decimal Sharpe { get; init; }
    public decimal Sortino { get; init; }
    public decimal Calmar { get; init; }
    public decimal Omega { get; init; }
    public decimal TailRatio { get; init; }
    public decimal ValueAtRisk95 { get; init; }
    public decimal ConditionalValueAtRisk95 { get; init; }
    public decimal MaxDrawdownPercent { get; init; }
    public int MaxDrawdownDurationPeriods { get; init; }
    public decimal ExposurePercent { get; init; }
    public decimal HitRatePercent { get; init; }
    public decimal AnnualizedReturnPercent { get; init; }
}
