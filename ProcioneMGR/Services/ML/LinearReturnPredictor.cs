using Microsoft.ML;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Baseline lineare regolarizzata (ML.NET SDCA) per la previsione del rendimento forward.
/// Prima implementazione di <see cref="IReturnPredictor"/> (cap. 7 del libro): interpretabile,
/// veloce da addestrare, punto di riferimento prima dei modelli non lineari (Random Forest,
/// boosting) delle fasi successive.
/// </summary>
public sealed class LinearReturnPredictor : RegressionPredictorBase
{
    public override string Name => "Linear";

    protected override IEstimator<ITransformer> BuildPipeline(MLContext mlContext) =>
        // Normalizzazione necessaria: SDCA è numericamente instabile (pesi non finiti) su feature
        // a scala molto piccola/non centrata come i fattori alpha grezzi (es. rendimenti ~1e-3).
        mlContext.Transforms.NormalizeMeanVariance("Features")
            .Append(mlContext.Regression.Trainers.Sdca(labelColumnName: "Label", featureColumnName: "Features"));
}
