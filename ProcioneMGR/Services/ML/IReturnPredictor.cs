using Microsoft.ML;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Astrazione comune a tutti i modelli di previsione dei rendimenti (lineari, Random Forest,
/// boosting, deep learning nelle fasi successive). Ogni implementazione incapsula un
/// <c>ITransformer</c> di ML.NET addestrato su un <see cref="MlDataset"/> con colonne
/// Features/Label; <see cref="Predict"/> è la via rapida (no IDataView) usata in hot-loop dal
/// backtest (<c>MlStrategy</c>).
/// </summary>
public interface IReturnPredictor : IDisposable
{
    /// <summary>Nome tecnico del modello (per persistenza/versionamento, come <c>RegimeModel</c>).</summary>
    string Name { get; }

    /// <summary>True dopo <see cref="Fit"/> o <see cref="Load"/> riusciti.</summary>
    bool IsFitted { get; }

    /// <summary>Addestra il modello su un IDataView con colonne "Features" (vettore) e "Label" (float).</summary>
    void Fit(MLContext mlContext, IDataView trainingData);

    /// <summary>Predizione puntuale (rendimento forward atteso) dato un vettore di feature.</summary>
    float Predict(float[] features);

    /// <summary>Persiste il modello addestrato su file (riuso del pattern di versionamento di <c>RegimeModel</c>).</summary>
    void Save(MLContext mlContext, string path);

    /// <summary>Carica un modello precedentemente salvato con <see cref="Save"/>.</summary>
    void Load(MLContext mlContext, string path);

    /// <summary>
    /// Permutation feature importance: per ogni feature (nell'ordine di <paramref name="featureNames"/>),
    /// quanto peggiora la qualità delle predizioni se quella feature viene mescolata casualmente
    /// nel dataset di valutazione. Richiede un modello già addestrato/caricato.
    /// </summary>
    IReadOnlyList<FeatureImportance> ComputeFeatureImportance(MLContext mlContext, IDataView evaluationData, IReadOnlyList<string> featureNames);
}
