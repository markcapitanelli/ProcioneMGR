using Microsoft.ML;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="MlpReturnPredictor"/> (cap. 17 ML4T in C# puro): apprendimento di
/// relazioni lineari e NON lineari, determinismo a parità di seed, persistenza JSON e
/// feature importance.
/// </summary>
public class MlpReturnPredictorTests
{
    private static MlDataset MakeDataset(int n, Func<float, float, float> target, int seed = 1, float noise = 0.01f)
    {
        var rnd = new Random(seed);
        var rows = new List<FeatureRow>(n);
        var timestamps = new List<DateTime>(n);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            var f0 = (float)(rnd.NextDouble() * 2 - 1);
            var f1 = (float)(rnd.NextDouble() * 2 - 1);
            var label = target(f0, f1) + (float)(rnd.NextDouble() * 2 - 1) * noise;
            rows.Add(new FeatureRow { Features = [f0, f1], Label = label });
            timestamps.Add(t0.AddHours(i));
        }
        return new MlDataset { Rows = rows, FeatureNames = ["f0", "f1"], Timestamps = timestamps };
    }

    [Fact]
    public void Fit_LearnsLinearRelationship()
    {
        var dataset = MakeDataset(1000, (f0, f1) => 2f * f0 - 1f * f1);
        var mlContext = new MLContext(seed: 1);
        var predictor = new MlpReturnPredictor();

        predictor.Fit(mlContext, dataset.ToDataView(mlContext));

        Assert.True(predictor.IsFitted);
        Assert.Equal("Mlp", predictor.Name);
        Assert.True(Math.Abs(predictor.Predict([1f, 0f]) - 2f) < 0.3f, $"pred={predictor.Predict([1f, 0f])}");
        Assert.True(Math.Abs(predictor.Predict([0f, 1f]) - -1f) < 0.3f, $"pred={predictor.Predict([0f, 1f])}");
    }

    [Fact]
    public void Fit_LearnsNonlinearRelationship_BetterThanMean()
    {
        // Target = f0^2 - |f1|: un modello lineare qui ha R² ~ 0, l'MLP deve fare nettamente meglio.
        var dataset = MakeDataset(2000, (f0, f1) => f0 * f0 - Math.Abs(f1), noise: 0.02f);
        var mlContext = new MLContext(seed: 1);
        var predictor = new MlpReturnPredictor(hiddenUnits: 24, epochs: 400);

        predictor.Fit(mlContext, dataset.ToDataView(mlContext));

        // R² in-sample sulle predizioni dirette.
        double ssRes = 0, ssTot = 0;
        var meanLabel = dataset.Rows.Average(r => (double)r.Label);
        foreach (var row in dataset.Rows)
        {
            var pred = predictor.Predict(row.Features);
            ssRes += (row.Label - pred) * (row.Label - pred);
            ssTot += (row.Label - meanLabel) * (row.Label - meanLabel);
        }
        var r2 = 1 - ssRes / ssTot;
        Assert.True(r2 > 0.7, $"R² atteso > 0.7 su un target non lineare imparabile, trovato {r2:F3}");
    }

    [Fact]
    public void Fit_SameSeed_IsDeterministic()
    {
        var dataset = MakeDataset(500, (f0, f1) => f0 - f1, seed: 3);
        var mlContext = new MLContext(seed: 1);

        var p1 = new MlpReturnPredictor(seed: 7);
        var p2 = new MlpReturnPredictor(seed: 7);
        p1.Fit(mlContext, dataset.ToDataView(mlContext));
        p2.Fit(mlContext, dataset.ToDataView(mlContext));

        float[] probe = [0.3f, -0.6f];
        Assert.Equal(p1.Predict(probe), p2.Predict(probe));
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_ProducesSamePredictions()
    {
        var dataset = MakeDataset(500, (f0, f1) => 1.5f * f0 + 0.7f * f1, seed: 2);
        var mlContext = new MLContext(seed: 1);
        var predictor = new MlpReturnPredictor();
        predictor.Fit(mlContext, dataset.ToDataView(mlContext));

        var path = Path.Combine(Path.GetTempPath(), $"mlp_{Guid.NewGuid():N}.json");
        try
        {
            predictor.Save(mlContext, path);
            var reloaded = new MlpReturnPredictor();
            reloaded.Load(mlContext, path);

            float[] probe = [0.4f, -0.2f];
            Assert.Equal(predictor.Predict(probe), reloaded.Predict(probe));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FeatureImportance_RanksInformativeFeatureHigher()
    {
        // f0 determina il target, f1 e' rumore puro.
        var dataset = MakeDataset(1000, (f0, _) => 2f * f0, noise: 0.02f);
        var mlContext = new MLContext(seed: 1);
        var predictor = new MlpReturnPredictor();
        predictor.Fit(mlContext, dataset.ToDataView(mlContext));

        var importance = predictor.ComputeFeatureImportance(
            mlContext, dataset.ToDataView(mlContext), dataset.FeatureNames);

        Assert.Equal(2, importance.Count);
        Assert.Equal("f0", importance[0].FeatureName); // la feature informativa domina
        Assert.True(importance[0].MeanDecreaseInRSquared > importance[1].MeanDecreaseInRSquared);
    }

    [Fact]
    public void Predict_BeforeFit_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new MlpReturnPredictor().Predict([1f, 2f]));
    }

    [Fact]
    public void Predict_WrongFeatureCount_Throws()
    {
        var dataset = MakeDataset(100, (f0, f1) => f0 + f1);
        var mlContext = new MLContext(seed: 1);
        var predictor = new MlpReturnPredictor(epochs: 10);
        predictor.Fit(mlContext, dataset.ToDataView(mlContext));

        Assert.Throws<ArgumentException>(() => predictor.Predict([1f]));
    }
}
