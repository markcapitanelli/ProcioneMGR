using Microsoft.ML;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Random Forest (ML.NET FastForest) per la previsione del rendimento forward — cap. 11.
/// Non lineare, cattura interazioni fra fattori che il modello lineare non vede. Gli alberi
/// sono invarianti alla scala delle feature: nessuna normalizzazione necessaria (a differenza
/// di <see cref="LinearReturnPredictor"/>).
/// </summary>
public sealed class RandomForestReturnPredictor : RegressionPredictorBase
{
    private readonly int _numberOfTrees;
    private readonly int _numberOfLeaves;

    public RandomForestReturnPredictor(int numberOfTrees = 100, int numberOfLeaves = 20)
    {
        if (numberOfTrees < 1) throw new ArgumentOutOfRangeException(nameof(numberOfTrees));
        if (numberOfLeaves < 2) throw new ArgumentOutOfRangeException(nameof(numberOfLeaves));
        _numberOfTrees = numberOfTrees;
        _numberOfLeaves = numberOfLeaves;
    }

    public override string Name => "RandomForest";

    protected override IEstimator<ITransformer> BuildPipeline(MLContext mlContext) =>
        mlContext.Regression.Trainers.FastForest(
            labelColumnName: "Label",
            featureColumnName: "Features",
            numberOfTrees: _numberOfTrees,
            numberOfLeaves: _numberOfLeaves);
}
