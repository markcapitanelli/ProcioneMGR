using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Monitoring.Drift;

/// <summary>
/// Valuta il drift di TUTTE le feature di un <see cref="SavedMlModel"/>: per ciascun fattore
/// usato dal modello confronta la distribuzione nella finestra di training (reference) con quella
/// nelle candele recenti (current), applicando ogni <see cref="IFeatureDriftDetector"/>.
/// Rif. <c>docs/ROADMAP-QLIB.md §1.5</c>.
/// </summary>
public interface IFeatureDriftMonitor
{
    Task<IReadOnlyList<FactorDriftReport>> EvaluateAsync(
        SavedMlModel model,
        IReadOnlyList<OhlcvData> recentCandles,
        DriftThresholds? thresholds = null,
        CancellationToken ct = default);
}
