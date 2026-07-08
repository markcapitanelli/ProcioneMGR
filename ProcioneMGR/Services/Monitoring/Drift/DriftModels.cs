namespace ProcioneMGR.Services.Monitoring.Drift;

/// <summary>Gravità del drift rilevato su una feature. None &lt; Warning &lt; Alert.</summary>
public enum DriftSeverity
{
    None = 0,
    Warning = 1,
    Alert = 2,
}

/// <summary>
/// Esito di UN test di drift su una feature (una distribuzione di riferimento vs una corrente).
/// <see cref="Score"/> è la statistica del test (PSI, D di KS, statistica di Page-Hinkley);
/// <see cref="PValue"/> è valorizzato solo dove il test ne produce uno (KS).
/// </summary>
public sealed record DriftResult(
    string Detector,
    double Score,
    double? PValue,
    DriftSeverity Severity,
    string Detail);

/// <summary>
/// Soglie dei test di drift. Default coerenti con la prassi (PSI &gt;0.2 warning, &gt;0.25 alert;
/// KS p&lt;0.05 warning, p&lt;0.01 alert). Page-Hinkley lavora su z-score (standardizzati sulla
/// distribuzione di riferimento) così le sue soglie sono indipendenti dalla scala della feature.
/// </summary>
public sealed class DriftThresholds
{
    public int PsiBins { get; set; } = 10;
    public double PsiWarning { get; set; } = 0.2;
    public double PsiAlert { get; set; } = 0.25;

    public double KsPValueWarning { get; set; } = 0.05;
    public double KsPValueAlert { get; set; } = 0.01;

    /// <summary>
    /// Tolleranza (in deviazioni standard di riferimento) prima che Page-Hinkley accumuli: assorbe
    /// il rumore di uno stream stazionario così solo uno spostamento PERSISTENTE della media supera
    /// le soglie. Le soglie sono tarate per stream nell'ordine di 100-500 osservazioni: la
    /// statistica cresce ~(|z̄|−delta)·N su un vero shift, restando piccola sul rumore.
    /// </summary>
    public double PageHinkleyDelta { get; set; } = 1.0;
    public double PageHinkleyWarning { get; set; } = 25.0;
    public double PageHinkleyAlert { get; set; } = 50.0;

    /// <summary>Numero minimo di osservazioni valide (per lato) sotto cui il test non è affidabile.</summary>
    public int MinObservations { get; set; } = 20;
}

/// <summary>
/// Report di drift per UNA feature di un modello: distribuzione di training (reference) vs finestra
/// recente (current), con l'esito di ciascun detector. <see cref="Overall"/> è la gravità massima.
/// </summary>
public sealed class FactorDriftReport
{
    public string FeatureName { get; init; } = string.Empty;
    public int ReferenceCount { get; init; }
    public int CurrentCount { get; init; }
    public IReadOnlyList<DriftResult> Results { get; init; } = [];

    public DriftSeverity Overall => Results.Count == 0 ? DriftSeverity.None : Results.Max(r => r.Severity);
}
