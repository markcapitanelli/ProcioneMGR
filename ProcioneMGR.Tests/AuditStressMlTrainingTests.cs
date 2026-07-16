using System.Diagnostics;
using Microsoft.ML;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Monitoring.Drift;
using Xunit.Abstractions;

namespace ProcioneMGR.Tests;

/// <summary>
/// Audit FASE 2.2 — training pesante: LightGBM su 100k righe × 30 feature e MLP (C# puro) su
/// 20k righe, con concept-drift detection attiva sulle stesse feature (PSI/KS/Page-Hinkley) e
/// misure di tempo/allocazioni. Verifica: il training completa, le predizioni sono finite, i
/// detector scattano sulle distribuzioni spostate e NON scattano su quelle identiche, e la
/// memoria viene rilasciata dopo Dispose (nessuna ritenzione da training ripetuti).
/// </summary>
[Trait("Category", "Stress")]
public sealed class AuditStressMlTrainingTests
{
    private readonly ITestOutputHelper _output;

    public AuditStressMlTrainingTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Dataset sintetico con segnale reale: label = combinazione lineare sparsa + interazione
    /// non lineare + rumore. 30 feature, ~la dimensione di un run Alpha158 ridotto.
    /// </summary>
    private static MlDataset MakeDataset(int rows, int features = 30, int seed = 7, double shift = 0.0)
    {
        var rnd = new Random(seed);
        var list = new List<FeatureRow>(rows);
        var timestamps = new List<DateTime>(rows);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < rows; i++)
        {
            var f = new float[features];
            for (var j = 0; j < features; j++) f[j] = (float)(rnd.NextDouble() * 2 - 1 + shift);
            var label = 0.3f * f[0] - 0.2f * f[3] + 0.4f * f[7] * f[11] + (float)(rnd.NextDouble() * 0.1 - 0.05);
            list.Add(new FeatureRow { Features = f, Label = label });
            timestamps.Add(t0.AddMinutes(i));
        }
        return new MlDataset
        {
            Rows = list,
            FeatureNames = Enumerable.Range(0, features).Select(j => $"f{j}").ToList(),
            Timestamps = timestamps,
        };
    }

    [Fact]
    public void HeavyLightGbm_100kRows_TrainsPredictsAndReleasesMemory()
    {
        var dataset = MakeDataset(rows: 100_000);
        var mlContext = new MLContext(seed: 1);

        var allocBefore = GC.GetTotalAllocatedBytes();
        var sw = Stopwatch.StartNew();

        float pAfterTrain;
        using (var predictor = new GradientBoostingReturnPredictor(numberOfLeaves: 31, numberOfIterations: 100))
        {
            predictor.Fit(mlContext, dataset.ToDataView(mlContext));
            sw.Stop();
            Assert.True(predictor.IsFitted);

            // Predizioni finite e non tutte identiche (il modello ha imparato qualcosa).
            var probe1 = predictor.Predict(dataset.Rows[0].Features);
            var probe2 = predictor.Predict(dataset.Rows[1].Features);
            Assert.True(float.IsFinite(probe1) && float.IsFinite(probe2));
            pAfterTrain = probe1;

            var distinct = dataset.Rows.Take(200).Select(r => predictor.Predict(r.Features)).Distinct().Count();
            Assert.True(distinct > 10, $"predizioni degeneri: {distinct} valori distinti su 200");
        }

        var allocatedMb = (GC.GetTotalAllocatedBytes() - allocBefore) / 1024.0 / 1024.0;
        _output.WriteLine($"LightGBM 100k×30: fit in {sw.Elapsed.TotalSeconds:F1}s, allocati {allocatedMb:N0} MB, probe={pAfterTrain:F5}");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var retainedMb = GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0;
        _output.WriteLine($"Heap dopo Dispose+GC: {retainedMb:N0} MB");
        Assert.True(retainedMb < 400, $"Ritenzione sospetta dopo training: {retainedMb:N0} MB");
    }

    [Fact]
    public void HeavyMlp_20kRows_TrainsDeterministicallyWithFiniteOutputs()
    {
        var dataset = MakeDataset(rows: 20_000, seed: 11);
        var mlContext = new MLContext(seed: 1);
        var sw = Stopwatch.StartNew();

        using var predictor = new MlpReturnPredictor(hiddenUnits: 16, epochs: 60, learningRate: 0.01, seed: 42);
        predictor.Fit(mlContext, dataset.ToDataView(mlContext));
        sw.Stop();

        Assert.True(predictor.IsFitted);
        foreach (var row in dataset.Rows.Take(500))
        {
            var p = predictor.Predict(row.Features);
            Assert.True(float.IsFinite(p), $"predizione non finita: {p}");
        }

        // Stesso seed => stesso modello (determinismo dichiarato dal predictor).
        using var predictor2 = new MlpReturnPredictor(hiddenUnits: 16, epochs: 60, learningRate: 0.01, seed: 42);
        predictor2.Fit(mlContext, dataset.ToDataView(mlContext));
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(predictor.Predict(dataset.Rows[i].Features), predictor2.Predict(dataset.Rows[i].Features));
        }
        _output.WriteLine($"MLP 20k×30×60epoche: fit in {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void ConceptDriftDetectors_DuringTrainingLoop_AlertOnShift_SilentOnSame()
    {
        // Simula il loop reale: il modello è addestrato su 'reference'; la produzione vede
        // 'current'. Caso A: stessa distribuzione -> nessun alert. Caso B: shift +1.5 -> alert.
        var reference = MakeDataset(rows: 2_000, seed: 3).Rows.Select(r => (decimal)r.Features[0]).ToList();
        var same = MakeDataset(rows: 500, seed: 99).Rows.Select(r => (decimal)r.Features[0]).ToList();
        var shifted = MakeDataset(rows: 500, seed: 99, shift: 1.5).Rows.Select(r => (decimal)r.Features[0]).ToList();

        var thresholds = new DriftThresholds();
        var detectors = new IFeatureDriftDetector[] { new PsiDriftDetector(), new KsDriftDetector(), new PageHinkleyDetector() };

        foreach (var d in detectors)
        {
            var calm = d.Detect(reference, same, thresholds);
            Assert.True(calm.Severity == DriftSeverity.None,
                $"{d.Name}: falso positivo su distribuzione identica ({calm.Detail})");

            var alarm = d.Detect(reference, shifted, thresholds);
            Assert.True(alarm.Severity >= DriftSeverity.Warning,
                $"{d.Name}: shift di 1.5 non rilevato ({alarm.Detail})");
        }
    }

    [Fact]
    public void RepeatedTrainings_WithDriftChecks_DoNotAccumulateMemory()
    {
        // 8 cicli train->drift->dispose consecutivi: la memoria trattenuta a fine giro deve
        // restare piatta (il GC deve poter recuperare ogni ciclo).
        var mlContext = new MLContext(seed: 1);
        var thresholds = new DriftThresholds();
        var detectors = new IFeatureDriftDetector[] { new PsiDriftDetector(), new KsDriftDetector() };

        var retained = new List<double>();
        for (var cycle = 0; cycle < 8; cycle++)
        {
            var dataset = MakeDataset(rows: 20_000, seed: 100 + cycle);
            using (var predictor = new GradientBoostingReturnPredictor(numberOfLeaves: 20, numberOfIterations: 40))
            {
                predictor.Fit(mlContext, dataset.ToDataView(mlContext));
                var reference = dataset.Rows.Take(2000).Select(r => (decimal)r.Features[0]).ToList();
                var current = dataset.Rows.Skip(2000).Take(500).Select(r => (decimal)r.Features[0]).ToList();
                foreach (var d in detectors) _ = d.Detect(reference, current, thresholds);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            retained.Add(GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0);
        }

        _output.WriteLine("Heap trattenuto per ciclo (MB): " + string.Join(", ", retained.Select(m => m.ToString("N0"))));
        // Il confronto onesto è fra i cicli a regime (il primo paga JIT/caches una tantum).
        var steadyGrowth = retained[^1] - retained[2];
        Assert.True(steadyGrowth < 150, $"Crescita sospetta a regime: +{steadyGrowth:N0} MB fra ciclo 3 e 8");
    }
}
