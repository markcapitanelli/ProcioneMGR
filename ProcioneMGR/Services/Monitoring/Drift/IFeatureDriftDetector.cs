namespace ProcioneMGR.Services.Monitoring.Drift;

/// <summary>
/// Rileva il <b>drift statistico</b> di una feature: quanto la distribuzione dei valori CORRENTI
/// si è spostata rispetto a quella di RIFERIMENTO (tipicamente: finestra di training del modello).
///
/// È un segnale <i>anticipatore</i> che AFFIANCA — non sostituisce — lo
/// <see cref="StrategyDecayMonitor"/>: quest'ultimo misura il PnL realizzato (il giudice finale),
/// il drift misura se gli INPUT del modello sono cambiati prima ancora che il PnL ne risenta
/// (rif. <c>docs/ROADMAP-QLIB.md §1.5</c>). Puro/stateless → registrabile come Singleton.
/// </summary>
public interface IFeatureDriftDetector
{
    /// <summary>Nome tecnico del test: "Psi" | "Ks" | "PageHinkley".</summary>
    string Name { get; }

    /// <summary>
    /// Confronta i valori di riferimento con quelli correnti. Restituisce sempre un
    /// <see cref="DriftResult"/> (con <see cref="DriftSeverity.None"/> e un dettaglio esplicativo
    /// quando i dati sono insufficienti), mai un'eccezione.
    /// </summary>
    DriftResult Detect(IReadOnlyList<decimal> reference, IReadOnlyList<decimal> current, DriftThresholds thresholds);
}
