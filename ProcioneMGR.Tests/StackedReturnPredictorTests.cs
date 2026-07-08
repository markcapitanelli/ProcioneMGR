using Microsoft.ML;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dello <see cref="StackedReturnPredictor"/> (rif. <c>docs/ROADMAP-QLIB.md §1.8</c>): ogni
/// modalità di stacking addestra e predice su un dataset apprendibile, e il round-trip Save/Load
/// riproduce le stesse predizioni. Essendo un <c>IReturnPredictor</c>, si comporta come gli altri
/// modelli (nessun consumatore va toccato).
/// </summary>
public class StackedReturnPredictorTests
{
    /// <summary>Dataset sintetico apprendibile: label = 2·f0 − f1 + 0.5·f2 + rumore.</summary>
    private static List<FeatureRow> MakeRows(int n, int seed = 7)
    {
        var rnd = new Random(seed);
        var rows = new List<FeatureRow>(n);
        for (var i = 0; i < n; i++)
        {
            var f0 = (float)(rnd.NextDouble() - 0.5);
            var f1 = (float)(rnd.NextDouble() - 0.5);
            var f2 = (float)(rnd.NextDouble() - 0.5);
            var noise = (float)((rnd.NextDouble() - 0.5) * 0.05);
            rows.Add(new FeatureRow { Features = [f0, f1, f2], Label = 2f * f0 - f1 + 0.5f * f2 + noise });
        }
        return rows;
    }

    private static double PredictionCorrelation(StackedReturnPredictor stack, List<FeatureRow> rows)
    {
        var preds = rows.Select(r => (double)stack.Predict(r.Features)).ToList();
        var actual = rows.Select(r => (double)r.Label).ToList();
        return Correlation.Pearson(preds, actual);
    }

    [Theory]
    [InlineData(StackingMode.Average)]
    [InlineData(StackingMode.InverseRmse)]
    [InlineData(StackingMode.StackedRidge)]
    public void EveryMode_FitsAndPredictsLearnableTarget(StackingMode mode)
    {
        var mlContext = new MLContext(seed: 1);
        var rows = MakeRows(300);
        var view = MlDatasetView.Create(mlContext, rows, 3);

        using var stack = new StackedReturnPredictor(["Linear", "RandomForest"], mode);
        stack.Fit(mlContext, view);

        Assert.True(stack.IsFitted);
        var corr = PredictionCorrelation(stack, rows);
        Assert.True(corr > 0.7, $"Correlazione attesa alta su target lineare, ottenuta {corr:F3} (mode {mode})");
    }

    [Fact]
    public void SaveLoad_RoundTrip_ReproducesPredictions()
    {
        var mlContext = new MLContext(seed: 1);
        var rows = MakeRows(250);
        var view = MlDatasetView.Create(mlContext, rows, 3);

        using var original = new StackedReturnPredictor(["Linear", "RandomForest"], StackingMode.StackedRidge);
        original.Fit(mlContext, view);

        var probe = rows.Take(15).Select(r => r.Features).ToList();
        var before = probe.Select(f => original.Predict(f)).ToList();

        var path = Path.Combine(Path.GetTempPath(), $"stack_roundtrip_{Guid.NewGuid():N}.zip");
        try
        {
            original.Save(mlContext, path);

            using var loaded = new StackedReturnPredictor();
            loaded.Load(mlContext, path);
            Assert.True(loaded.IsFitted);

            for (var i = 0; i < probe.Count; i++)
            {
                Assert.Equal(before[i], loaded.Predict(probe[i]), 3); // stesse predizioni dopo il round-trip
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void FeatureImportance_RanksTheStrongestFeatureFirst()
    {
        var mlContext = new MLContext(seed: 1);
        var rows = MakeRows(300);
        var view = MlDatasetView.Create(mlContext, rows, 3);

        using var stack = new StackedReturnPredictor(["Linear", "RandomForest"], StackingMode.Average);
        stack.Fit(mlContext, view);

        var importance = stack.ComputeFeatureImportance(mlContext, view, ["f0", "f1", "f2"]);
        Assert.Equal(3, importance.Count);
        // f0 ha il coefficiente più grande (2·f0): deve risultare la feature più importante.
        Assert.Equal("f0", importance[0].FeatureName);
    }

    [Fact]
    public void SingleBase_IsValid()
    {
        var mlContext = new MLContext(seed: 1);
        var rows = MakeRows(200);
        var view = MlDatasetView.Create(mlContext, rows, 3);

        using var stack = new StackedReturnPredictor(["Linear"], StackingMode.StackedRidge);
        stack.Fit(mlContext, view);
        Assert.True(PredictionCorrelation(stack, rows) > 0.7);
    }
}
