using Microsoft.ML;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="RandomForestReturnPredictor"/> e <see cref="GradientBoostingReturnPredictor"/>:
/// apprendono una relazione non lineare che il modello lineare non può catturare, e la
/// persistenza (Save/Load) funziona tramite la stessa base condivisa.
/// </summary>
public class TreeReturnPredictorTests
{
    /// <summary>
    /// Relazione XOR-like non lineare: label alta solo quando ESATTAMENTE una delle due feature
    /// è positiva. Un modello lineare non può separarla (correlazione lineare ~0 con ciascuna
    /// feature presa singolarmente), un albero sì.
    /// </summary>
    private static MlDataset MakeXorDataset(int n, int seed = 1)
    {
        var rnd = new Random(seed);
        var rows = new List<FeatureRow>(n);
        var timestamps = new List<DateTime>(n);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            var f0 = (float)(rnd.NextDouble() * 2 - 1);
            var f1 = (float)(rnd.NextDouble() * 2 - 1);
            var label = (f0 > 0) != (f1 > 0) ? 1f : -1f;
            rows.Add(new FeatureRow { Features = [f0, f1], Label = label });
            timestamps.Add(t0.AddHours(i));
        }
        return new MlDataset { Rows = rows, FeatureNames = ["f0", "f1"], Timestamps = timestamps };
    }

    [Fact]
    public void RandomForest_LearnsNonLinearRelationship_BetterThanChance()
    {
        var dataset = MakeXorDataset(2000);
        var mlContext = new MLContext(seed: 1);
        var predictor = new RandomForestReturnPredictor(numberOfTrees: 50, numberOfLeaves: 20);

        predictor.Fit(mlContext, dataset.ToDataView(mlContext));

        Assert.True(predictor.IsFitted);
        Assert.Equal("RandomForest", predictor.Name);

        // Sul quadrante (+,-): label vera = +1. Un modello che ha imparato l'XOR deve prevedere positivo.
        Assert.True(predictor.Predict([0.8f, -0.8f]) > 0f);
        Assert.True(predictor.Predict([-0.8f, 0.8f]) > 0f);
        // Sul quadrante (+,+): label vera = -1.
        Assert.True(predictor.Predict([0.8f, 0.8f]) < 0f);
        Assert.True(predictor.Predict([-0.8f, -0.8f]) < 0f);
    }

    [Fact]
    public void GradientBoosting_LearnsNonLinearRelationship_BetterThanChance()
    {
        var dataset = MakeXorDataset(2000, seed: 2);
        var mlContext = new MLContext(seed: 1);
        var predictor = new GradientBoostingReturnPredictor(numberOfLeaves: 15, numberOfIterations: 50);

        predictor.Fit(mlContext, dataset.ToDataView(mlContext));

        Assert.True(predictor.IsFitted);
        Assert.Equal("GradientBoosting", predictor.Name);

        Assert.True(predictor.Predict([0.8f, -0.8f]) > 0f);
        Assert.True(predictor.Predict([-0.8f, 0.8f]) > 0f);
        Assert.True(predictor.Predict([0.8f, 0.8f]) < 0f);
        Assert.True(predictor.Predict([-0.8f, -0.8f]) < 0f);
    }

    [Fact]
    public void RandomForest_InvalidHyperparameters_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RandomForestReturnPredictor(numberOfTrees: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RandomForestReturnPredictor(numberOfLeaves: 1));
    }

    [Fact]
    public void GradientBoosting_InvalidHyperparameters_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GradientBoostingReturnPredictor(numberOfLeaves: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GradientBoostingReturnPredictor(numberOfIterations: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GradientBoostingReturnPredictor(learningRate: 0));
    }

    [Fact]
    public void RandomForest_SaveAndLoad_RoundTrip_ProducesSamePredictions()
    {
        var dataset = MakeXorDataset(500, seed: 3);
        var mlContext = new MLContext(seed: 1);
        var predictor = new RandomForestReturnPredictor(numberOfTrees: 20);
        predictor.Fit(mlContext, dataset.ToDataView(mlContext));

        var path = Path.Combine(Path.GetTempPath(), $"rf_{Guid.NewGuid():N}.zip");
        try
        {
            predictor.Save(mlContext, path);
            var loaded = new RandomForestReturnPredictor();
            var loadContext = new MLContext(seed: 1);
            loaded.Load(loadContext, path);

            var probe = new float[] { 0.4f, -0.3f };
            Assert.Equal(predictor.Predict(probe), loaded.Predict(probe), 4);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void GradientBoosting_SaveAndLoad_RoundTrip_ProducesSamePredictions()
    {
        var dataset = MakeXorDataset(500, seed: 4);
        var mlContext = new MLContext(seed: 1);
        var predictor = new GradientBoostingReturnPredictor(numberOfIterations: 30);
        predictor.Fit(mlContext, dataset.ToDataView(mlContext));

        var path = Path.Combine(Path.GetTempPath(), $"gbm_{Guid.NewGuid():N}.zip");
        try
        {
            predictor.Save(mlContext, path);
            var loaded = new GradientBoostingReturnPredictor();
            var loadContext = new MLContext(seed: 1);
            loaded.Load(loadContext, path);

            var probe = new float[] { -0.2f, 0.6f };
            Assert.Equal(predictor.Predict(probe), loaded.Predict(probe), 4);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
