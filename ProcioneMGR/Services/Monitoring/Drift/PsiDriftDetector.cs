namespace ProcioneMGR.Services.Monitoring.Drift;

/// <summary>
/// <b>Population Stability Index</b>: quanto la distribuzione corrente si è spostata fra i bin
/// definiti dai quantili della distribuzione di riferimento. PSI = Σ (a−e)·ln(a/e), con e/a =
/// frazione attesa (reference) / effettiva (current) per bin. Convenzione: &lt;0.1 stabile,
/// 0.1–0.25 spostamento moderato, &gt;0.25 significativo.
/// </summary>
public sealed class PsiDriftDetector : IFeatureDriftDetector
{
    public string Name => "Psi";

    public DriftResult Detect(IReadOnlyList<decimal> reference, IReadOnlyList<decimal> current, DriftThresholds thresholds)
    {
        var r = DriftMath.ToDoubles(reference);
        var c = DriftMath.ToDoubles(current);
        if (r.Length < thresholds.MinObservations || c.Length < thresholds.MinObservations)
            return new DriftResult(Name, 0d, null, DriftSeverity.None, $"Dati insufficienti (ref {r.Length}, cur {c.Length}).");

        Array.Sort(r);
        if (r[0] == r[^1])
            return new DriftResult(Name, 0d, null, DriftSeverity.None, "Riferimento costante: PSI non interpretabile.");

        var bins = Math.Max(2, thresholds.PsiBins);
        var edges = new double[bins - 1];
        for (var i = 1; i < bins; i++) edges[i - 1] = DriftMath.QuantileSorted(r, (double)i / bins);

        var refPct = BinPercents(r, edges);
        var curPct = BinPercents(c, edges);

        const double eps = 1e-6;
        double psi = 0d;
        for (var i = 0; i < refPct.Length; i++)
        {
            var e = Math.Max(refPct[i], eps);
            var a = Math.Max(curPct[i], eps);
            psi += (a - e) * Math.Log(a / e);
        }

        var severity = psi >= thresholds.PsiAlert ? DriftSeverity.Alert
                     : psi >= thresholds.PsiWarning ? DriftSeverity.Warning
                     : DriftSeverity.None;
        return new DriftResult(Name, psi, null, severity, $"PSI={psi:F3} su {bins} bin.");
    }

    /// <summary>Frazione di osservazioni per bucket definito da <paramref name="edges"/> (ascendenti).</summary>
    private static double[] BinPercents(double[] data, double[] edges)
    {
        var buckets = new double[edges.Length + 1];
        foreach (var x in data) buckets[BucketOf(x, edges)]++;
        var n = data.Length;
        for (var i = 0; i < buckets.Length; i++) buckets[i] /= n;
        return buckets;
    }

    private static int BucketOf(double x, double[] edges)
    {
        // Bucket = numero di edge < x (edges ascendenti); ricerca lineare (pochi bin).
        var b = 0;
        while (b < edges.Length && x > edges[b]) b++;
        return b;
    }
}
