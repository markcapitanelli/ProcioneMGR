namespace ProcioneMGR.Services.Regime;

public interface IRegimeDetector
{
    /// <summary>
    /// Addestra un nuovo modello K-means e lo profila. Se <paramref name="activate"/> è true
    /// lo salva come modello attivo; altrimenti lo restituisce senza persisterlo (preview).
    /// </summary>
    Task<RegimeModel> TrainAsync(TrainingConfiguration config, bool activate = true, CancellationToken ct = default);

    /// <summary>Salva e rende attivo un modello (es. dopo una preview o dal retraining worker).</summary>
    Task ActivateModelAsync(RegimeModel model, CancellationToken ct = default);

    /// <summary>Regime (nearest centroid) per una singola feature, usando il modello attivo.</summary>
    Task<int> PredictRegimeAsync(MarketFeatures features, CancellationToken ct = default);

    /// <summary>Etichetta una sequenza di feature col modello attivo, applicando lo smoothing.</summary>
    Task<List<MarketFeatures>> LabelFeaturesAsync(List<MarketFeatures> features, CancellationToken ct = default);

    /// <summary>Ultimo modello attivo (più recente).</summary>
    Task<RegimeModel?> LoadLatestModelAsync(CancellationToken ct = default);
}
