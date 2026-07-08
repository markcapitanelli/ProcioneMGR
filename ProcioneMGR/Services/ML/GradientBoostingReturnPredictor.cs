using Microsoft.ML;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Gradient Boosting (ML.NET LightGBM) per la previsione del rendimento forward — cap. 12.
/// Nel libro è il modello con il miglior rapporto performance/sforzo sui dati tabellari di
/// fattori. Come <see cref="RandomForestReturnPredictor"/>, basato su alberi: nessuna
/// normalizzazione delle feature necessaria.
/// </summary>
public sealed class GradientBoostingReturnPredictor : RegressionPredictorBase
{
    private readonly int _numberOfLeaves;
    private readonly int _numberOfIterations;
    private readonly double _learningRate;

    public GradientBoostingReturnPredictor(int numberOfLeaves = 20, int numberOfIterations = 100, double learningRate = 0.1)
    {
        if (numberOfLeaves < 2) throw new ArgumentOutOfRangeException(nameof(numberOfLeaves));
        if (numberOfIterations < 1) throw new ArgumentOutOfRangeException(nameof(numberOfIterations));
        if (learningRate <= 0) throw new ArgumentOutOfRangeException(nameof(learningRate));
        _numberOfLeaves = numberOfLeaves;
        _numberOfIterations = numberOfIterations;
        _learningRate = learningRate;
    }

    public override string Name => "GradientBoosting";

    protected override IEstimator<ITransformer> BuildPipeline(MLContext mlContext) =>
        mlContext.Regression.Trainers.LightGbm(
            labelColumnName: "Label",
            featureColumnName: "Features",
            numberOfLeaves: _numberOfLeaves,
            numberOfIterations: _numberOfIterations,
            learningRate: _learningRate);
}
