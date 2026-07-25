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

    /// <summary>
    /// Etichetta una sequenza di feature col modello attivo <b>più recente, di qualunque serie</b>,
    /// applicando lo smoothing. Da preferire l'overload che dichiara la serie: questo è corretto
    /// solo se chi chiama ha già verificato che il modello più recente sia quello giusto.
    /// </summary>
    Task<List<MarketFeatures>> LabelFeaturesAsync(List<MarketFeatures> features, CancellationToken ct = default);

    /// <summary>
    /// Etichetta le feature col modello attivo <b>di quella serie</b>. È la forma corretta quando si
    /// sa a quale coppia/timeframe appartengono le candele: etichettarle con i centroidi di un'altra
    /// serie produce un numero ben formato e privo di significato. Se la serie non ha un modello
    /// attivo, le feature tornano senza etichetta invece di riceverne una sbagliata.
    /// </summary>
    Task<List<MarketFeatures>> LabelFeaturesAsync(
        List<MarketFeatures> features, string symbol, string timeframe, CancellationToken ct = default);

    /// <summary>
    /// Ultimo modello attivo (più recente), <b>di qualunque serie</b>. Con più serie seguite
    /// contemporaneamente questo non è quasi mai ciò che serve: vedi <see cref="LoadActiveModelAsync"/>.
    /// </summary>
    Task<RegimeModel?> LoadLatestModelAsync(CancellationToken ct = default);

    /// <summary>Modello attivo della serie indicata; null se quella serie non ne ha uno.</summary>
    Task<RegimeModel?> LoadActiveModelAsync(string symbol, string timeframe, CancellationToken ct = default);
}
