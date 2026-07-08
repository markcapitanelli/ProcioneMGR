namespace ProcioneMGR.Services.Monitoring.Drift;

/// <summary>
/// Test di <b>Kolmogorov-Smirnov a due campioni</b>: la statistica D è la massima distanza fra le
/// due funzioni di ripartizione empiriche (reference vs current). Il p-value asintotico dice quanto
/// è improbabile osservare quella distanza se le due distribuzioni fossero identiche: p piccolo ⇒
/// drift. Nessuna assunzione di normalità (non parametrico).
/// </summary>
public sealed class KsDriftDetector : IFeatureDriftDetector
{
    public string Name => "Ks";

    public DriftResult Detect(IReadOnlyList<decimal> reference, IReadOnlyList<decimal> current, DriftThresholds thresholds)
    {
        var a = DriftMath.ToDoubles(reference);
        var b = DriftMath.ToDoubles(current);
        if (a.Length < thresholds.MinObservations || b.Length < thresholds.MinObservations)
            return new DriftResult(Name, 0d, null, DriftSeverity.None, $"Dati insufficienti (ref {a.Length}, cur {b.Length}).");

        Array.Sort(a);
        Array.Sort(b);
        var d = KsStatistic(a, b);

        double n1 = a.Length, n2 = b.Length;
        var en = Math.Sqrt(n1 * n2 / (n1 + n2));
        var p = DriftMath.KolmogorovQ((en + 0.12 + 0.11 / en) * d);

        var severity = p < thresholds.KsPValueAlert ? DriftSeverity.Alert
                     : p < thresholds.KsPValueWarning ? DriftSeverity.Warning
                     : DriftSeverity.None;
        return new DriftResult(Name, d, p, severity, $"D={d:F3}, p={p:F4}.");
    }

    /// <summary>Massima distanza fra le CDF empiriche di due campioni ordinati (merge-walk).</summary>
    private static double KsStatistic(double[] a, double[] b)
    {
        int i = 0, j = 0;
        double n1 = a.Length, n2 = b.Length;
        double fa = 0d, fb = 0d, d = 0d;
        while (i < a.Length && j < b.Length)
        {
            var x = Math.Min(a[i], b[j]);
            while (i < a.Length && a[i] <= x) { i++; fa = i / n1; }
            while (j < b.Length && b[j] <= x) { j++; fb = j / n2; }
            d = Math.Max(d, Math.Abs(fa - fb));
        }
        return d;
    }
}
