namespace ProcioneMGR.Services.Monitoring.Drift;

/// <summary>
/// Test di <b>Page-Hinkley</b>: change-point online su uno STREAM (non due campioni statici). A
/// differenza di PSI/KS, che confrontano due distribuzioni globali, qui si scorre la serie corrente
/// nell'ordine temporale e si accumula la deviazione persistente della media rispetto al
/// riferimento — utile per cogliere uno spostamento GRADUALE del regime.
///
/// I valori correnti sono standardizzati (z-score) sulla media/deviazione di riferimento, così la
/// statistica e le soglie sono indipendenti dalla scala della feature. Si valutano entrambe le
/// direzioni (aumento/diminuzione della media) e si tiene la più forte.
/// </summary>
public sealed class PageHinkleyDetector : IFeatureDriftDetector
{
    public string Name => "PageHinkley";

    public DriftResult Detect(IReadOnlyList<decimal> reference, IReadOnlyList<decimal> current, DriftThresholds thresholds)
    {
        var r = DriftMath.ToDoubles(reference);
        var c = DriftMath.ToDoubles(current);
        if (r.Length < thresholds.MinObservations || c.Length < thresholds.MinObservations)
            return new DriftResult(Name, 0d, null, DriftSeverity.None, $"Dati insufficienti (ref {r.Length}, cur {c.Length}).");

        var (mu, sigma) = DriftMath.MeanStd(r);
        if (sigma <= 0d)
            return new DriftResult(Name, 0d, null, DriftSeverity.None, "Riferimento costante: Page-Hinkley non applicabile.");

        var delta = thresholds.PageHinkleyDelta;
        double mUp = 0d, minUp = 0d, phUp = 0d;   // rilevamento aumento della media
        double mDown = 0d, minDown = 0d, phDown = 0d; // rilevamento diminuzione
        foreach (var x in c)
        {
            var z = (x - mu) / sigma;
            mUp += z - delta; minUp = Math.Min(minUp, mUp); phUp = Math.Max(phUp, mUp - minUp);
            mDown += -z - delta; minDown = Math.Min(minDown, mDown); phDown = Math.Max(phDown, mDown - minDown);
        }

        var ph = Math.Max(phUp, phDown);
        var severity = ph >= thresholds.PageHinkleyAlert ? DriftSeverity.Alert
                     : ph >= thresholds.PageHinkleyWarning ? DriftSeverity.Warning
                     : DriftSeverity.None;
        var dir = phUp >= phDown ? "media ↑" : "media ↓";
        return new DriftResult(Name, ph, null, severity, $"PH={ph:F2} ({dir}).");
    }
}
