using Microsoft.ML;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="LinearReturnPredictor"/>: apprendimento di una relazione lineare nota,
/// persistenza (Save/Load) e comportamento prima dell'addestramento.
/// </summary>
public class LinearReturnPredictorTests
{
    private static MlDataset MakeLinearDataset(int n, float w0, float w1, int seed = 1, float noise = 0.01f)
    {
        var rnd = new Random(seed);
        var rows = new List<FeatureRow>(n);
        var timestamps = new List<DateTime>(n);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            var f0 = (float)(rnd.NextDouble() * 2 - 1);
            var f1 = (float)(rnd.NextDouble() * 2 - 1);
            var label = w0 * f0 + w1 * f1 + (float)(rnd.NextDouble() * 2 - 1) * noise;
            rows.Add(new FeatureRow { Features = [f0, f1], Label = label });
            timestamps.Add(t0.AddHours(i));
        }
        return new MlDataset { Rows = rows, FeatureNames = ["f0", "f1"], Timestamps = timestamps };
    }

    [Fact]
    public void Fit_LearnsKnownLinearRelationship()
    {
        var dataset = MakeLinearDataset(1000, w0: 2f, w1: -1f);
        var mlContext = new MLContext(seed: 1);
        var predictor = new LinearReturnPredictor();

        predictor.Fit(mlContext, dataset.ToDataView(mlContext));

        Assert.True(predictor.IsFitted);
        Assert.Equal("Linear", predictor.Name);

        // f0=1, f1=0 -> atteso ~2; f0=0, f1=1 -> atteso ~-1.
        Assert.True(Math.Abs(predictor.Predict([1f, 0f]) - 2f) < 0.3f, $"pred={predictor.Predict([1f, 0f])}");
        Assert.True(Math.Abs(predictor.Predict([0f, 1f]) - -1f) < 0.3f, $"pred={predictor.Predict([0f, 1f])}");
    }

    [Fact]
    public void Predict_BeforeFit_Throws()
    {
        var predictor = new LinearReturnPredictor();
        Assert.Throws<InvalidOperationException>(() => predictor.Predict([1f, 2f]));
    }

    [Fact]
    public void Save_BeforeFit_Throws()
    {
        var predictor = new LinearReturnPredictor();
        var path = Path.Combine(Path.GetTempPath(), $"never_{Guid.NewGuid():N}.zip");
        Assert.Throws<InvalidOperationException>(() => predictor.Save(new MLContext(), path));
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_ProducesSamePredictions()
    {
        var dataset = MakeLinearDataset(500, w0: 1.5f, w1: 0.7f, seed: 2);
        var mlContext = new MLContext(seed: 1);
        var predictor = new LinearReturnPredictor();
        predictor.Fit(mlContext, dataset.ToDataView(mlContext));

        var path = Path.Combine(Path.GetTempPath(), $"linear_{Guid.NewGuid():N}.zip");
        try
        {
            predictor.Save(mlContext, path);
            Assert.True(File.Exists(path));

            var loaded = new LinearReturnPredictor();
            var loadContext = new MLContext(seed: 1);
            loaded.Load(loadContext, path);

            Assert.True(loaded.IsFitted);
            var probe = new float[] { 0.3f, -0.6f };
            Assert.Equal(predictor.Predict(probe), loaded.Predict(probe), 4);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
