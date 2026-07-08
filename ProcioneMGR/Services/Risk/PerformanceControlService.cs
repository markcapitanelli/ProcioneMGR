using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Services.Risk;

/// <summary>
/// Controllo dinamico del rischio (Trombetta, cap. 8): inibisce e riattiva una strategia
/// in base allo stato di salute della sua equity line, pagando un "premio di assicurazione"
/// in profitto in cambio di draw down piu' contenuti.
///
/// Due modalita' implementate:
///  - Performance Control (metrico): profitto a finestra scorrevole degli ultimi N trade;
///    se scende sotto la soglia, i trade successivi vengono saltati finche' la metrica
///    (sempre calcolata sui trade ORIGINALI) non risale sopra la soglia.
///  - Equity Control (grafico): media mobile semplice sull'equity dei trade originali;
///    si opera solo quando l'equity e' sopra la propria media.
///
/// In entrambi i casi il segnale e' valutato sul trade PRECEDENTE (nessun look-ahead:
/// la decisione di eseguire il trade i usa solo informazioni fino a i-1).
/// </summary>
public sealed class PerformanceControlService
{
    /// <summary>
    /// Performance Control su profitto a finestra scorrevole.
    /// </summary>
    /// <param name="trades">Trade originali in ordine cronologico di chiusura.</param>
    /// <param name="windowPeriod">Numero di trade della finestra di controllo.</param>
    /// <param name="threshold">Soglia minima di profitto cumulato della finestra.</param>
    public EquityControlResult ApplyWindowProfitControl(
        IReadOnlyList<BacktestTrade> trades, int windowPeriod = 10, decimal threshold = 0m)
    {
        ArgumentNullException.ThrowIfNull(trades);
        if (windowPeriod < 1) throw new ArgumentOutOfRangeException(nameof(windowPeriod));

        var executed = new bool[trades.Count];
        decimal windowSum = 0m;
        for (var i = 0; i < trades.Count; i++)
        {
            // Il trade i viene eseguito solo se la metrica calcolata fino a i-1 e' sana.
            // Finche' la finestra non e' piena si opera normalmente (buffer in riempimento).
            executed[i] = i < windowPeriod || windowSum > threshold;

            // A fine iterazione windowSum = somma dei PnL originali [i-window+1 .. i],
            // cosi' alla prossima iterazione copre gli ultimi `windowPeriod` trade fino a i.
            windowSum += trades[i].Pnl;
            if (i >= windowPeriod)
            {
                windowSum -= trades[i - windowPeriod].Pnl;
            }
        }

        return BuildResult(trades, executed);
    }

    /// <summary>
    /// Equity Control con media mobile semplice sull'equity a operazioni chiuse dei trade
    /// originali: il trade i viene eseguito solo se, al trade i-1, l'equity era sopra la
    /// propria SMA. Finche' la SMA non e' calcolabile si opera normalmente.
    /// </summary>
    public EquityControlResult ApplyEquityMovingAverageControl(
        IReadOnlyList<BacktestTrade> trades, int smaPeriod = 20)
    {
        ArgumentNullException.ThrowIfNull(trades);
        if (smaPeriod < 1) throw new ArgumentOutOfRangeException(nameof(smaPeriod));

        var n = trades.Count;
        var equity = new decimal[n];
        decimal cum = 0m;
        for (var i = 0; i < n; i++)
        {
            cum += trades[i].Pnl;
            equity[i] = cum;
        }

        var executed = new bool[n];
        decimal windowSum = 0m;
        for (var i = 0; i < n; i++)
        {
            if (i < smaPeriod)
            {
                executed[i] = true; // SMA non ancora disponibile al trade precedente
            }
            else
            {
                var sma = windowSum / smaPeriod;
                executed[i] = equity[i - 1] >= sma;
            }

            // A fine iterazione windowSum = somma di equity[i-period+1 .. i].
            windowSum += equity[i];
            if (i >= smaPeriod)
            {
                windowSum -= equity[i - smaPeriod];
            }
        }

        return BuildResult(trades, executed);
    }

    private static EquityControlResult BuildResult(IReadOnlyList<BacktestTrade> trades, bool[] executed)
    {
        var n = trades.Count;
        var originalEquity = new decimal[n];
        var controlledEquity = new decimal[n];
        var controlledTrades = new List<BacktestTrade>();

        decimal cumOriginal = 0m, cumControlled = 0m;
        for (var i = 0; i < n; i++)
        {
            cumOriginal += trades[i].Pnl;
            if (executed[i])
            {
                cumControlled += trades[i].Pnl;
                controlledTrades.Add(trades[i]);
            }
            originalEquity[i] = cumOriginal;
            controlledEquity[i] = cumControlled;
        }

        var (origMaxDd, origAvgDd) = DrawdownFromEquity(originalEquity);
        var (ctrlMaxDd, ctrlAvgDd) = DrawdownFromEquity(controlledEquity);

        return new EquityControlResult
        {
            ExecutedFlags = executed,
            OriginalEquity = originalEquity,
            ControlledEquity = controlledEquity,
            OriginalTradeCount = n,
            ControlledTradeCount = controlledTrades.Count,
            OriginalProfit = cumOriginal,
            ControlledProfit = cumControlled,
            OriginalMaxDrawdown = origMaxDd,
            ControlledMaxDrawdown = ctrlMaxDd,
            OriginalAvgDrawdown = origAvgDd,
            ControlledAvgDrawdown = ctrlAvgDd,
            OriginalReport = TradeStatistics.ComputeTradeReport(trades, ToEquityPoints(trades, originalEquity)),
            ControlledReport = TradeStatistics.ComputeTradeReport(controlledTrades, ToEquityPoints(controlledTrades, null)),
        };
    }

    private static (decimal MaxDd, decimal AvgDd) DrawdownFromEquity(IReadOnlyList<decimal> equity)
    {
        decimal peak = 0m, maxDd = 0m, sumDd = 0m;
        var points = 0;
        foreach (var e in equity)
        {
            if (e > peak) peak = e;
            var dd = peak - e;
            if (dd > 0m)
            {
                sumDd += dd;
                points++;
                if (dd > maxDd) maxDd = dd;
            }
        }
        return (maxDd, points == 0 ? 0m : sumDd / points);
    }

    private static List<EquityPoint> ToEquityPoints(IReadOnlyList<BacktestTrade> trades, IReadOnlyList<decimal>? equity)
    {
        var result = new List<EquityPoint>(trades.Count + 1);

        // Punto iniziale a 0: la curva dei PnL parte da zero PRIMA del primo trade. Senza
        // questo ancoraggio, un primo trade in perdita non conterebbe come draw down nei
        // TradeReport annidati (picco iniziale = primo valore), in disaccordo con i campi
        // Original/ControlledMaxDrawdown calcolati in DrawdownFromEquity (picco iniziale = 0).
        if (trades.Count > 0)
        {
            result.Add(new EquityPoint { Timestamp = trades[0].EntryTime, Capital = 0m });
        }

        decimal cum = 0m;
        for (var i = 0; i < trades.Count; i++)
        {
            cum = equity is not null ? equity[i] : cum + trades[i].Pnl;
            result.Add(new EquityPoint
            {
                Timestamp = trades[i].ExitTime ?? trades[i].EntryTime,
                Capital = cum,
            });
        }
        return result;
    }
}

/// <summary>Confronto tra la curva originale e quella controllata (cap. 8 del libro).</summary>
public sealed record EquityControlResult
{
    /// <summary>Per ogni trade originale: true se la curva controllata lo avrebbe eseguito.</summary>
    public IReadOnlyList<bool> ExecutedFlags { get; init; } = [];
    public IReadOnlyList<decimal> OriginalEquity { get; init; } = [];
    public IReadOnlyList<decimal> ControlledEquity { get; init; } = [];

    public int OriginalTradeCount { get; init; }
    public int ControlledTradeCount { get; init; }
    public decimal OriginalProfit { get; init; }
    public decimal ControlledProfit { get; init; }
    public decimal OriginalMaxDrawdown { get; init; }
    public decimal ControlledMaxDrawdown { get; init; }
    public decimal OriginalAvgDrawdown { get; init; }
    public decimal ControlledAvgDrawdown { get; init; }

    /// <summary>Report completi per il confronto metrico puntuale (stile Figura 8.24).</summary>
    public TradeReport OriginalReport { get; init; } = new();
    public TradeReport ControlledReport { get; init; } = new();

    /// <summary>Quota di profitto conservata dalla curva controllata (1 = nessuna perdita).</summary>
    public decimal ProfitRetention => OriginalProfit == 0m ? 0m : ControlledProfit / OriginalProfit;

    /// <summary>Quota di max draw down rispetto all'originale (&lt; 1 = rischio ridotto).</summary>
    public decimal MaxDrawdownRatio => OriginalMaxDrawdown == 0m ? 0m : ControlledMaxDrawdown / OriginalMaxDrawdown;
}
