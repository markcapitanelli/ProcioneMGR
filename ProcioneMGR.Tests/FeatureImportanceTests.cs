using Microsoft.ML;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test della permutation feature importance (<see cref="RegressionPredictorBase.ComputeFeatureImportance"/>):
/// una feature davvero predittiva deve pesare più di una puramente casuale, per qualunque
/// modello (lineare o ad alberi).
/// </summary>
public class FeatureImportanceTests
{
    /// <summary>f0 predice il label linearmente, f1 e' rumore puro scorrelato dal label.</summary>
    private static MlDataset MakeDatasetWithOneInformativeFeature(int n, int seed = 1)
    {
        var rnd = new Random(seed);
        var rows = new List<FeatureRow>(n);
        var timestamps = new List<DateTime>(n);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            var f0 = (float)(rnd.NextDouble() * 2 - 1);
            var f1 = (float)(rnd.NextDouble() * 2 - 1); // rumore, indipendente dal label
            var label = 3f * f0 + (float)(rnd.NextDouble() - 0.5) * 0.05f;
            rows.Add(new FeatureRow { Features = [f0, f1], Label = label });
            timestamps.Add(t0.AddHours(i));
        }
        return new MlDataset { Rows = rows, FeatureNames = ["Informative", "Noise"], Timestamps = timestamps };
    }

    [Fact]
    public void Linear_InformativeFeature_RanksAboveNoise()
    {
        var dataset = MakeDatasetWithOneInformativeFeature(1000);
        var mlContext = new MLContext(seed: 1);
        var predictor = new LinearReturnPredictor();
        var dataView = dataset.ToDataView(mlContext);
        predictor.Fit(mlContext, dataView);

        var importance = predictor.ComputeFeatureImportance(mlContext, dataView, dataset.FeatureNames);

        Assert.Equal(2, importance.Count);
        Assert.Equal("Informative", importance[0].FeatureName); // primo = più importante
        Assert.Equal("Noise", importance[1].FeatureName);
        Assert.True(importance[0].MeanDecreaseInRSquared > importance[1].MeanDecreaseInRSquared);
        // La feature informativa deve avere un impatto chiaramente positivo (permutarla peggiora R²).
        Assert.True(importance[0].MeanDecreaseInRSquared > 0.1, $"importanza={importance[0].MeanDecreaseInRSquared:F3}");
    }

    [Fact]
    public void RandomForest_InformativeFeature_RanksAboveNoise()
    {
        var dataset = MakeDatasetWithOneInformativeFeature(1000, seed: 7);
        var mlContext = new MLContext(seed: 1);
        var predictor = new RandomForestReturnPredictor(numberOfTrees: 50);
        var dataView = dataset.ToDataView(mlContext);
        predictor.Fit(mlContext, dataView);

        var importance = predictor.ComputeFeatureImportance(mlContext, dataView, dataset.FeatureNames);

        Assert.Equal("Informative", importance[0].FeatureName);
        Assert.True(importance[0].MeanDecreaseInRSquared > importance[1].MeanDecreaseInRSquared);
    }

    [Fact]
    public void ComputeFeatureImportance_BeforeFit_Throws()
    {
        var predictor = new LinearReturnPredictor();
        var mlContext = new MLContext();
        var dataset = MakeDatasetWithOneInformativeFeature(50);
        Assert.Throws<InvalidOperationException>(() =>
            predictor.ComputeFeatureImportance(mlContext, dataset.ToDataView(mlContext), dataset.FeatureNames));
    }
}
