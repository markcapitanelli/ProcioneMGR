using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Alpha;

/// <summary>
/// Implementazione del valutatore di fattori. Stateless -> registrato come Singleton.
///
/// Metriche prodotte:
///  - <b>IC (Information Coefficient)</b>: correlazione di Spearman tra il fattore alla candela i
///    e il rendimento forward su H candele. Misura quanto l'ORDINAMENTO indotto dal fattore
///    predice l'ordinamento dei rendimenti futuri. |IC| > ~0.03 su molte osservazioni è già
///    interessante nei mercati reali.
///  - <b>IR (Information Ratio)</b>: media/deviazione degli IC calcolati su finestre rolling.
///    Un fattore con IC modesto ma STABILE (IR alto) è preferibile a uno erratico.
///  - <b>Quantile returns</b>: rendimento forward medio per quantile del fattore; lo spread
///    top-bottom indica la monotonicità/forza del segnale.
///  - <b>IC decay</b>: IC a orizzonti crescenti; dice per quanto tempo il segnale resta utile.
/// </summary>
public sealed class FactorEvaluator : IFactorEvaluator
{
    public IReadOnlyList<decimal?> ForwardReturns(IReadOnlyList<OhlcvData> candles, int horizon)
    {
        var n = candles.Count;
        var result = new decimal?[n];
        if (horizon < 1) return result;

        for (var i = 0; i + horizon < n; i++)
        {
            var now = candles[i].Close;
            if (now <= 0m) continue;
            result[i] = (candles[i + horizon].Close - now) / now;
        }
        return result;
    }

    public FactorEvaluationResult Evaluate(
        IAlphaFactor factor,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        FactorEvaluationConfig config)
    {
        ArgumentNullException.ThrowIfNull(factor);
        ArgumentNullException.ThrowIfNull(candles);
        config ??= new FactorEvaluationConfig();

        var result = new FactorEvaluationResult
        {
            FactorName = factor.Name,
            DisplayName = factor.DisplayName,
        };

        var values = factor.Compute(candles, parameters ?? new Dictionary<string, decimal>());
        var fwd = ForwardReturns(candles, config.ForwardHorizon);

        // Coppie valide (fattore e forward-return entrambi presenti), in ordine temporale.
        var fx = new List<double>();
        var fy = new List<double>();
        for (var i = 0; i < candles.Count; i++)
        {
            if (values[i].HasValue && fwd[i].HasValue)
            {
                fx.Add((double)values[i]!.Value);
                fy.Add((double)fwd[i]!.Value);
            }
        }

        result.Observations = fx.Count;
        if (fx.Count < 3)
        {
            return result; // dati insufficienti: metriche restano a zero
        }

        result.InformationCoefficient = Correlation.Spearman(fx, fy);
        result.PearsonCorrelation = Correlation.Pearson(fx, fy);

        // Significatività dell'IC: t-stat Newey-West con lag = overlap dei forward-return (horizon-1).
        // Con horizon>1 le osservazioni sono autocorrelate → la t-stat ingenua gonfia la significatività.
        result.NeweyWestLags = Math.Max(0, config.ForwardHorizon - 1);
        result.IcTStatistic = Correlation.SpearmanTStatNeweyWest(fx, fy, result.NeweyWestLags);
        result.IcTStatisticNaive = Correlation.TStatIndependent(result.InformationCoefficient, fx.Count);

        ComputeRollingIc(fx, fy, config.RollingIcWindow, result);
        result.QuantileReturns = ComputeQuantileReturns(fx, fy, config.Quantiles);
        if (result.QuantileReturns.Count >= 2)
        {
            var top = result.QuantileReturns[^1].MeanForwardReturn;
            var bottom = result.QuantileReturns[0].MeanForwardReturn;
            result.TopMinusBottomSpread = top - bottom;
        }
        result.IcDecay = ComputeIcDecay(factor, candles, parameters ?? new Dictionary<string, decimal>(), config.DecayHorizons);

        return result;
    }

    private static void ComputeRollingIc(List<double> fx, List<double> fy, int window, FactorEvaluationResult result)
    {
        if (window < 3 || fx.Count < window)
        {
            // Non abbastanza dati per finestre: usa l'IC full-sample come unica stima.
            result.RollingIcMean = result.InformationCoefficient;
            result.RollingIcStd = 0d;
            result.InformationRatio = 0d;
            result.IcConsistency = 1d;
            return;
        }

        var ics = new List<double>();
        for (var start = 0; start + window <= fx.Count; start += window) // finestre non sovrapposte
        {
            var wx = fx.GetRange(start, window);
            var wy = fy.GetRange(start, window);
            ics.Add(Correlation.Spearman(wx, wy));
        }
        if (ics.Count == 0) { result.RollingIcMean = result.InformationCoefficient; return; }

        double mean = 0d;
        foreach (var v in ics) mean += v;
        mean /= ics.Count;

        double sumSq = 0d;
        foreach (var v in ics) { var d = v - mean; sumSq += d * d; }
        var std = ics.Count > 1 ? Math.Sqrt(sumSq / ics.Count) : 0d;

        var sign = Math.Sign(result.InformationCoefficient);
        var consistent = sign == 0 ? ics.Count : ics.Count(v => Math.Sign(v) == sign);

        result.RollingIcMean = mean;
        result.RollingIcStd = std;
        result.InformationRatio = std > 1e-12 ? mean / std : 0d;
        result.IcConsistency = (double)consistent / ics.Count;
    }

    private static List<QuantileReturn> ComputeQuantileReturns(List<double> fx, List<double> fy, int quantiles)
    {
        var q = Math.Max(2, quantiles);
        var n = fx.Count;
        if (n < q) return new List<QuantileReturn>();

        // Ordina gli indici per valore di fattore crescente, poi suddivide in q bucket ~uguali.
        var idx = Enumerable.Range(0, n).ToArray();
        Array.Sort(idx, (a, b) => fx[a].CompareTo(fx[b]));

        var buckets = new List<QuantileReturn>();
        for (var b = 0; b < q; b++)
        {
            var from = (int)((long)b * n / q);
            var to = (int)((long)(b + 1) * n / q); // esclusivo
            if (to <= from) continue;

            double sum = 0d;
            for (var k = from; k < to; k++) sum += fy[idx[k]];
            var count = to - from;
            buckets.Add(new QuantileReturn
            {
                Quantile = b + 1,
                Count = count,
                MeanForwardReturn = (decimal)(sum / count),
            });
        }
        return buckets;
    }

    private List<IcByHorizon> ComputeIcDecay(
        IAlphaFactor factor,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        int[] horizons)
    {
        var values = factor.Compute(candles, parameters);
        var decay = new List<IcByHorizon>();
        foreach (var h in horizons.Where(h => h >= 1).Distinct().OrderBy(h => h))
        {
            var fwd = ForwardReturns(candles, h);
            var fx = new List<double>();
            var fy = new List<double>();
            for (var i = 0; i < candles.Count; i++)
            {
                if (values[i].HasValue && fwd[i].HasValue)
                {
                    fx.Add((double)values[i]!.Value);
                    fy.Add((double)fwd[i]!.Value);
                }
            }
            decay.Add(new IcByHorizon
            {
                Horizon = h,
                InformationCoefficient = fx.Count >= 3 ? Correlation.Spearman(fx, fy) : 0d,
            });
        }
        return decay;
    }
}
