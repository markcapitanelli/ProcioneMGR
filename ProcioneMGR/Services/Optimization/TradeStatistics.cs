using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.Optimization;

/// <summary>
/// Performance report "alla Trombetta" (Strategie di trading con Python, cap. 6-7):
/// metriche calcolate sulla LISTA DEI TRADE e sull'equity monetaria, complementari al
/// <see cref="TearsheetMetrics"/> che lavora sui rendimenti percentuali dell'equity curve.
///
/// Include: Profit Factor, Average Trade, Gross Profit/Loss, media e massimo di guadagni
/// e perdite (con data), Reward/Risk Ratio, Average Draw Down (esclusi gli zeri),
/// rapporto AvgDD/MaxDD, ritardi tra massimi consecutivi (max e medio), Kestner Ratio
/// e aggregati annuali/mensili dei profitti.
/// </summary>
public static class TradeStatistics
{
    /// <summary>Report completo sulle operazioni chiuse + equity curve monetaria.</summary>
    public static TradeReport ComputeTradeReport(
        IReadOnlyList<BacktestTrade> trades,
        IReadOnlyList<EquityPoint> equityCurve)
    {
        trades ??= [];
        equityCurve ??= [];

        decimal grossProfit = 0m, grossLoss = 0m;
        decimal maxWin = 0m, maxLoss = 0m;
        DateTime? maxWinDate = null, maxLossDate = null;
        var wins = 0;
        var losses = 0;

        foreach (var t in trades)
        {
            if (t.Pnl > 0m)
            {
                wins++;
                grossProfit += t.Pnl;
                if (t.Pnl > maxWin) { maxWin = t.Pnl; maxWinDate = t.ExitTime ?? t.EntryTime; }
            }
            else if (t.Pnl < 0m)
            {
                losses++;
                grossLoss += t.Pnl; // negativo
                if (t.Pnl < maxLoss) { maxLoss = t.Pnl; maxLossDate = t.ExitTime ?? t.EntryTime; }
            }
        }

        var count = trades.Count;
        var netProfit = grossProfit + grossLoss;
        var avgTrade = count == 0 ? 0m : netProfit / count;
        var avgWin = wins == 0 ? 0m : grossProfit / wins;
        var avgLoss = losses == 0 ? 0m : grossLoss / losses; // negativo
        var percentWin = count == 0 ? 0m : (decimal)wins / count * 100m;
        var rewardRisk = avgLoss == 0m ? 0m : avgWin / Math.Abs(avgLoss);

        // Equazione di budget: PercentWin*AvgWin - PercentLoss*|AvgLoss| (> 0 -> capiente).
        var percentLoss = count == 0 ? 0m : (decimal)losses / count * 100m;
        var budget = percentWin * avgWin - percentLoss * Math.Abs(avgLoss);

        var (maxDdMoney, avgDdMoney) = DrawdownMoney(equityCurve);
        var (maxDelay, avgDelay) = DelayBetweenPeaks(equityCurve);

        return new TradeReport
        {
            NetProfit = netProfit,
            GrossProfit = grossProfit,
            GrossLoss = grossLoss,
            ProfitFactor = grossLoss == 0m ? 0m : grossProfit / Math.Abs(grossLoss),
            OperationCount = count,
            AverageTrade = avgTrade,
            PercentWin = percentWin,
            RewardRiskRatio = rewardRisk,
            AverageWin = avgWin,
            AverageLoss = avgLoss,
            MaxWin = maxWin,
            MaxWinDate = maxWinDate,
            MaxLoss = maxLoss,
            MaxLossDate = maxLossDate,
            BudgetEquation = budget,
            MaxDrawdownMoney = maxDdMoney,
            AverageDrawdownMoney = avgDdMoney,
            DrawdownRatio = maxDdMoney == 0m ? 0m : avgDdMoney / maxDdMoney,
            MaxDelayBetweenPeaks = maxDelay,
            AvgDelayBetweenPeaks = avgDelay,
            KestnerRatio = KestnerRatio(trades),
            AnnualProfits = AnnualProfits(trades),
            MonthlyAverageProfits = MonthlyAverageProfits(trades),
            MonthlyProfitMatrix = MonthlyProfitMatrix(trades),
        };
    }

    /// <summary>
    /// Draw down monetario sull'equity curve: massimo (picco-a-valle, valore positivo) e
    /// medio calcolato SOLO sui punti in draw down (esclusi gli zeri dei nuovi massimi),
    /// come la funzione avgdrawdown_nozero del libro.
    /// </summary>
    public static (decimal MaxDrawdown, decimal AverageDrawdown) DrawdownMoney(IReadOnlyList<EquityPoint> equityCurve)
    {
        if (equityCurve is null || equityCurve.Count == 0) return (0m, 0m);

        var peak = equityCurve[0].Capital;
        decimal maxDd = 0m, sumDd = 0m;
        var ddPoints = 0;
        foreach (var p in equityCurve)
        {
            if (p.Capital > peak) peak = p.Capital;
            var dd = peak - p.Capital;
            if (dd > 0m)
            {
                sumDd += dd;
                ddPoints++;
                if (dd > maxDd) maxDd = dd;
            }
        }
        return (maxDd, ddPoints == 0 ? 0m : sumDd / ddPoints);
    }

    /// <summary>
    /// Ritardi tra massimi consecutivi dell'equity (profilo "orizzontale" del rischio):
    /// numero massimo e medio di periodi trascorsi senza segnare un nuovo massimo.
    /// Il ritardo in corso a fine serie viene conteggiato.
    /// </summary>
    public static (int MaxDelay, decimal AvgDelay) DelayBetweenPeaks(IReadOnlyList<EquityPoint> equityCurve)
    {
        if (equityCurve is null || equityCurve.Count == 0) return (0, 0m);

        var peak = equityCurve[0].Capital;
        var current = 0;
        var maxDelay = 0;
        long totalDelayPoints = 0; // somma dei punti in ritardo (per la media "nozero")
        var delays = new List<int>();

        for (var i = 1; i < equityCurve.Count; i++)
        {
            if (equityCurve[i].Capital >= peak)
            {
                peak = equityCurve[i].Capital;
                if (current > 0)
                {
                    delays.Add(current);
                    if (current > maxDelay) maxDelay = current;
                    totalDelayPoints += current;
                    current = 0;
                }
            }
            else
            {
                current++;
            }
        }
        if (current > 0)
        {
            delays.Add(current);
            if (current > maxDelay) maxDelay = current;
            totalDelayPoints += current;
        }

        return (maxDelay, delays.Count == 0 ? 0m : (decimal)totalDelayPoints / delays.Count);
    }

    /// <summary>
    /// Kestner Ratio (versione del libro): regressione lineare sull'equity dei contributi
    /// MENSILI aggregati; rapporto tra pendenza della retta ed errore standard dei residui.
    /// Misura la regolarita' della curva dei profitti: piu' e' alto, piu' la crescita e' lineare.
    /// </summary>
    public static decimal KestnerRatio(IReadOnlyList<BacktestTrade> trades)
    {
        var monthly = MonthlyEquity(trades);
        var n = monthly.Count;
        if (n < 3) return 0m;

        // OLS y = a + b*x su x = 0..n-1 (double: qui la precisione decimal non serve).
        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
        for (var i = 0; i < n; i++)
        {
            var y = (double)monthly[i];
            sumX += i;
            sumY += y;
            sumXy += i * y;
            sumXx += (double)i * i;
        }
        var denom = n * sumXx - sumX * sumX;
        if (denom == 0) return 0m;
        var slope = (n * sumXy - sumX * sumY) / denom;
        var intercept = (sumY - slope * sumX) / n;

        double sumSqErr = 0;
        for (var i = 0; i < n; i++)
        {
            var err = (double)monthly[i] - (intercept + slope * i);
            sumSqErr += err * err;
        }
        var stdErr = Math.Sqrt(sumSqErr / n);
        if (double.IsNaN(stdErr)) return 0m;

        // Curva perfettamente lineare -> errore nullo: si usa un epsilon (come il divisore
        // "piccolissimo" del profit_factor del libro) e si limita il risultato per evitare
        // valori fuori scala nel cast a decimal.
        var ratio = slope / Math.Max(stdErr, 1e-9);
        if (double.IsNaN(ratio)) return 0m;
        ratio = Math.Clamp(ratio, -1e12, 1e12);
        return (decimal)ratio;
    }

    /// <summary>Profitti aggregati per anno (istogramma annuale del libro). Data = chiusura trade.</summary>
    public static IReadOnlyList<(int Year, decimal Profit)> AnnualProfits(IReadOnlyList<BacktestTrade> trades)
    {
        var byYear = new SortedDictionary<int, decimal>();
        foreach (var t in trades ?? [])
        {
            var year = (t.ExitTime ?? t.EntryTime).Year;
            byYear[year] = byYear.GetValueOrDefault(year) + t.Pnl;
        }
        return byYear.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// Profitto MEDIO per mese di calendario (1-12), aggregando i contributi mensili di tutti
    /// gli anni (istogramma del bias mensile del libro). Utile per capire in quali mesi il
    /// sistema fatica. Data di riferimento = chiusura del trade.
    /// </summary>
    public static IReadOnlyList<(int Month, decimal AverageProfit)> MonthlyAverageProfits(IReadOnlyList<BacktestTrade> trades)
    {
        // sum per (anno, mese), poi media per mese sui vari anni.
        var byYearMonth = new Dictionary<(int Year, int Month), decimal>();
        foreach (var t in trades ?? [])
        {
            var d = t.ExitTime ?? t.EntryTime;
            var key = (d.Year, d.Month);
            byYearMonth[key] = byYearMonth.GetValueOrDefault(key) + t.Pnl;
        }

        var result = new List<(int, decimal)>(12);
        for (var m = 1; m <= 12; m++)
        {
            var samples = byYearMonth.Where(kv => kv.Key.Month == m).Select(kv => kv.Value).ToList();
            result.Add((m, samples.Count == 0 ? 0m : samples.Sum() / samples.Count));
        }
        return result;
    }

    /// <summary>Matrice anno x mese dei profitti cumulati mensili (base della heatmap del libro).</summary>
    public static IReadOnlyList<MonthlyProfitCell> MonthlyProfitMatrix(IReadOnlyList<BacktestTrade> trades)
    {
        var byYearMonth = new SortedDictionary<(int Year, int Month), decimal>();
        foreach (var t in trades ?? [])
        {
            var d = t.ExitTime ?? t.EntryTime;
            var key = (d.Year, d.Month);
            byYearMonth[key] = byYearMonth.GetValueOrDefault(key) + t.Pnl;
        }
        return byYearMonth.Select(kv => new MonthlyProfitCell(kv.Key.Year, kv.Key.Month, kv.Value)).ToList();
    }

    /// <summary>Equity dei contributi mensili cumulati (per il Kestner Ratio).</summary>
    private static List<decimal> MonthlyEquity(IReadOnlyList<BacktestTrade> trades)
    {
        var cells = MonthlyProfitMatrix(trades);
        var equity = new List<decimal>(cells.Count);
        decimal cum = 0m;
        foreach (var c in cells)
        {
            cum += c.Profit;
            equity.Add(cum);
        }
        return equity;
    }

    /// <summary>
    /// Gandalf Persistence Distribution Index (GPDI, cap. 7): confronto percentile-per-percentile
    /// tra due distribuzioni di trade (tipicamente In Sample e Out of Sample). Restituisce la
    /// percentuale di livelli percentili in cui la SECONDA serie (OOS) batte la prima (IS).
    /// Valori vicini al 50% = distribuzioni equivalenti; molto bassi = OOS in degrado.
    /// </summary>
    public static decimal Gpdi(IReadOnlyList<decimal> inSamplePnls, IReadOnlyList<decimal> outOfSamplePnls, int step = 5)
    {
        if (inSamplePnls is null || outOfSamplePnls is null
            || inSamplePnls.Count == 0 || outOfSamplePnls.Count == 0 || step <= 0)
        {
            return 0m;
        }

        var isSorted = inSamplePnls.OrderBy(x => x).ToList();
        var oosSorted = outOfSamplePnls.OrderBy(x => x).ToList();

        int oosWins = 0, levels = 0;
        for (var p = step; p < 100; p += step)
        {
            var frac = p / 100m;
            var isValue = Percentile(isSorted, frac);
            var oosValue = Percentile(oosSorted, frac);
            if (oosValue > isValue) oosWins++;
            levels++;
        }
        return levels == 0 ? 0m : (decimal)oosWins / levels * 100m;
    }

    /// <summary>Percentile con interpolazione lineare su lista ORDINATA, p in [0,1].</summary>
    internal static decimal Percentile(IReadOnlyList<decimal> sorted, decimal p)
    {
        if (sorted.Count == 0) return 0m;
        if (sorted.Count == 1) return sorted[0];

        var rank = p * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];

        var weight = rank - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
    }
}

/// <summary>Cella della matrice anno x mese dei profitti.</summary>
public sealed record MonthlyProfitCell(int Year, int Month, decimal Profit);

/// <summary>Performance report basato sui trade (controparte del report del libro, cap. 6).</summary>
public sealed record TradeReport
{
    public decimal NetProfit { get; init; }
    public decimal GrossProfit { get; init; }
    public decimal GrossLoss { get; init; }
    public decimal ProfitFactor { get; init; }
    public int OperationCount { get; init; }
    public decimal AverageTrade { get; init; }
    public decimal PercentWin { get; init; }
    public decimal RewardRiskRatio { get; init; }
    public decimal AverageWin { get; init; }
    public decimal AverageLoss { get; init; }
    public decimal MaxWin { get; init; }
    public DateTime? MaxWinDate { get; init; }
    public decimal MaxLoss { get; init; }
    public DateTime? MaxLossDate { get; init; }

    /// <summary>PercentWin*AvgWin - PercentLoss*|AvgLoss|: se &gt; 0 il sistema produce utili al lordo dei costi.</summary>
    public decimal BudgetEquation { get; init; }

    public decimal MaxDrawdownMoney { get; init; }
    public decimal AverageDrawdownMoney { get; init; }

    /// <summary>AvgDD/MaxDD: piu' e' piccolo, piu' il MaxDD e' stato un'anomalia isolata.</summary>
    public decimal DrawdownRatio { get; init; }

    public int MaxDelayBetweenPeaks { get; init; }
    public decimal AvgDelayBetweenPeaks { get; init; }
    public decimal KestnerRatio { get; init; }

    public IReadOnlyList<(int Year, decimal Profit)> AnnualProfits { get; init; } = [];
    public IReadOnlyList<(int Month, decimal AverageProfit)> MonthlyAverageProfits { get; init; } = [];
    public IReadOnlyList<MonthlyProfitCell> MonthlyProfitMatrix { get; init; } = [];
}
