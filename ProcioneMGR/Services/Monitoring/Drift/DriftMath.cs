namespace ProcioneMGR.Services.Monitoring.Drift;

/// <summary>Utilità numeriche condivise dai detector di drift. Tutto in double (test statistici).</summary>
internal static class DriftMath
{
    public static double[] ToDoubles(IReadOnlyList<decimal> values)
    {
        var r = new double[values.Count];
        for (var i = 0; i < values.Count; i++) r[i] = (double)values[i];
        return r;
    }

    public static (double Mean, double Std) MeanStd(double[] values)
    {
        var n = values.Length;
        if (n == 0) return (0d, 0d);
        double mean = 0d;
        foreach (var v in values) mean += v;
        mean /= n;
        double sumSq = 0d;
        foreach (var v in values) { var d = v - mean; sumSq += d * d; }
        return (mean, Math.Sqrt(sumSq / n)); // deviazione di popolazione
    }

    /// <summary>Quantile (interpolazione lineare stile NumPy) su un array GIÀ ordinato crescente.</summary>
    public static double QuantileSorted(double[] sortedAsc, double q)
    {
        var m = sortedAsc.Length;
        if (m == 0) return 0d;
        if (m == 1) return sortedAsc[0];
        var pos = q * (m - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sortedAsc[lo];
        return sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * (pos - lo);
    }

    /// <summary>
    /// Q di Kolmogorov: coda della distribuzione KS. Q(λ)=2·Σ(-1)^(k-1)·e^(-2k²λ²). Restituisce
    /// il p-value asintotico del test KS a due campioni. Clampato in [0,1].
    /// </summary>
    public static double KolmogorovQ(double lambda)
    {
        if (lambda < 1e-6) return 1d;
        double sum = 0d, sign = 1d;
        var a2 = -2d * lambda * lambda;
        for (var k = 1; k <= 200; k++)
        {
            var term = sign * Math.Exp(a2 * k * k);
            sum += term;
            if (Math.Abs(term) < 1e-10) break;
            sign = -sign;
        }
        return Math.Clamp(2d * sum, 0d, 1d);
    }
}
