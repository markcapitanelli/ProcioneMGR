using Microsoft.ML;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'<see cref="AttentionReturnPredictor"/> (§1.4, attention in C# puro). Il test chiave è di
/// CORRETTEZZA del backprop manuale: la feature predittiva sta sul timestep più VECCHIO della
/// finestra, mentre il readout è sull'ultimo timestep — solo un'attention che instrada
/// correttamente l'informazione (e i cui gradienti sono giusti) può impararlo. Più: round-trip
/// Save/Load e determinismo.
/// </summary>
public class AttentionReturnPredictorTests
{
    private const int T = 6;
    private const int F = 2;

    /// <summary>
    /// Finestre casuali dove la label dipende SOLO dalla feature 0 del timestep 0 (il più vecchio).
    /// Gli altri valori sono rumore: un modello che guardasse solo l'ultimo passo non predirebbe nulla.
    /// </summary>
    private static List<FeatureRow> MakeTemporalRows(int n, int seed = 3)
    {
        var rnd = new Random(seed);
        var rows = new List<FeatureRow>(n);
        for (var i = 0; i < n; i++)
        {
            var flat = new float[T * F];
            for (var k = 0; k < flat.Length; k++) flat[k] = (float)(rnd.NextDouble() * 2 - 1);
            var predictive = flat[0]; // timestep 0, feature 0
            var noise = (float)((rnd.NextDouble() - 0.5) * 0.1);
            rows.Add(new FeatureRow { Features = flat, Label = 2f * predictive + noise });
        }
        return rows;
    }

    private static double Correlation_(IReturnPredictor model, List<FeatureRow> rows)
    {
        var preds = rows.Select(r => (double)model.Predict(r.Features)).ToList();
        var actual = rows.Select(r => (double)r.Label).ToList();
        return Correlation.Pearson(preds, actual);
    }

    [Fact]
    public void Learns_TemporalPattern_FromOldestTimestep()
    {
        var mlContext = new MLContext(seed: 1);
        var rows = MakeTemporalRows(400);
        var view = MlDatasetView.Create(mlContext, rows, T * F);

        using var model = new AttentionReturnPredictor(windowLength: T, embedDim: 16, hiddenUnits: 16, epochs: 250, seed: 42);
        model.Fit(mlContext, view);

        Assert.True(model.IsFitted);
        Assert.Equal(T, model.WindowLength);
        Assert.Equal(F, model.FeaturesPerStep);

        var corr = Correlation_(model, rows);
        Assert.True(corr > 0.5,
            $"L'attention dovrebbe imparare il pattern temporale (backprop corretto), correlazione ottenuta {corr:F3}");
    }

    [Fact]
    public void SaveLoad_RoundTrip_ReproducesPredictions()
    {
        var mlContext = new MLContext(seed: 1);
        var rows = MakeTemporalRows(200);
        var view = MlDatasetView.Create(mlContext, rows, T * F);

        using var original = new AttentionReturnPredictor(windowLength: T, epochs: 80, seed: 7);
        original.Fit(mlContext, view);

        var probe = rows.Take(12).Select(r => r.Features).ToList();
        var before = probe.Select(f => original.Predict(f)).ToList();

        var path = Path.Combine(Path.GetTempPath(), $"attn_roundtrip_{Guid.NewGuid():N}.json");
        try
        {
            original.Save(mlContext, path);
            using var loaded = new AttentionReturnPredictor(windowLength: T); // stessi D/Hff di default
            loaded.Load(mlContext, path);

            for (var i = 0; i < probe.Count; i++)
                Assert.Equal(before[i], loaded.Predict(probe[i]), 4);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IsDeterministic_ForSameSeed()
    {
        var mlContext = new MLContext(seed: 1);
        var rows = MakeTemporalRows(150);
        var view = MlDatasetView.Create(mlContext, rows, T * F);

        using var a = new AttentionReturnPredictor(windowLength: T, epochs: 60, seed: 99);
        using var b = new AttentionReturnPredictor(windowLength: T, epochs: 60, seed: 99);
        a.Fit(mlContext, view);
        b.Fit(mlContext, view);

        foreach (var r in rows.Take(20))
            Assert.Equal(a.Predict(r.Features), b.Predict(r.Features), 6);
    }

    [Fact]
    public void SequenceWindowing_BuildsCorrectLayout()
    {
        // Dataset puntuale: 5 righe, 2 feature; verifichiamo la finestra T=3.
        var pointwise = new MlDataset
        {
            Rows =
            [
                new FeatureRow { Features = [1, 10], Label = 0 },
                new FeatureRow { Features = [2, 20], Label = 0 },
                new FeatureRow { Features = [3, 30], Label = 7 },
                new FeatureRow { Features = [4, 40], Label = 0 },
                new FeatureRow { Features = [5, 50], Label = 0 },
            ],
            FeatureNames = ["a", "b"],
            Timestamps = Enumerable.Range(0, 5).Select(i => new DateTime(2024, 1, 1).AddHours(i)).ToList(),
        };

        var windowed = SequenceWindowing.Build(pointwise, windowLength: 3);
        Assert.Equal(3, windowed.RowCount);          // 5 − 3 + 1
        Assert.Equal(6, windowed.FeatureCount);      // 3 × 2

        // La prima finestra copre righe 0..2, layout timestep-major dal più vecchio.
        Assert.Equal(new float[] { 1, 10, 2, 20, 3, 30 }, windowed.Rows[0].Features);
        Assert.Equal(7f, windowed.Rows[0].Label);    // label = riga corrente (indice 2)
        Assert.Equal("a@t-2", windowed.FeatureNames[0]);
        Assert.Equal("b@t-0", windowed.FeatureNames[5]);
    }

    [Fact]
    public void SequenceWindowing_SkipsWindowsAcrossTimeGap()
    {
        // Timestamp con una LACUNA fra l'indice 2 (2h) e 3 (5h): passo = 1h.
        var hours = new[] { 0, 1, 2, 5, 6, 7 };
        var pointwise = new MlDataset
        {
            Rows = hours.Select((_, i) => new FeatureRow { Features = [i], Label = i }).ToList(),
            FeatureNames = ["a"],
            Timestamps = hours.Select(h => new DateTime(2024, 1, 1).AddHours(h)).ToList(),
        };

        var windowed = SequenceWindowing.Build(pointwise, windowLength: 3);

        // Solo le finestre interamente contigue: [0,1,2] e [5,6,7]. Quelle a cavallo del salto no.
        Assert.Equal(2, windowed.RowCount);
        Assert.Equal(new float[] { 0, 1, 2 }, windowed.Rows[0].Features);
        Assert.Equal(new float[] { 3, 4, 5 }, windowed.Rows[1].Features);
    }
}
