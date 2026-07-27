using Microsoft.ML;
using Microsoft.ML.Data;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.ML.Shap;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D1] Verifica di TreeSHAP. La domanda non è "il codice gira" ma "i numeri sono quelli giusti",
/// e per rispondere servono riferimenti indipendenti dall'implementazione:
///
///  1. RICOSTRUZIONE — la struttura estratta da ML.NET riproduce le predizioni del modello vero.
///     Senza questo, ogni valore SHAP sarebbe spazzatura ben formattata.
///  2. EFFICIENZA — baseline + Σ contributi == predizione, la proprietà che rende sommabili le
///     attribuzioni.
///  3. SHAPLEY PER FORZA BRUTA — su pochi fattori si enumerano tutti i 2^n sottoinsiemi e si
///     applica la formula di Shapley: è il valore ESATTO, calcolato in modo completamente
///     diverso. Se TreeSHAP coincide, l'algoritmo veloce è corretto.
///  4. FEATURE INERTE — una feature che il modello non usa deve ricevere esattamente zero.
/// </summary>
public class TreeShapTests
{
    private sealed class Row
    {
        [VectorType(3)]
        public float[] Features { get; set; } = new float[3];
        public float Label { get; set; }
    }

    /// <summary>
    /// Dataset sintetico: la label dipende da f0 e f1 (con un'interazione), mentre f2 è rumore
    /// puro mai correlato al target — è la feature che deve risultare inerte.
    /// </summary>
    private static (List<float[]> Rows, List<float> Labels) BuildData(int n, int seed)
    {
        var rnd = new Random(seed);
        var rows = new List<float[]>(n);
        var labels = new List<float>(n);
        for (var i = 0; i < n; i++)
        {
            var f0 = (float)rnd.NextDouble();
            var f1 = (float)rnd.NextDouble();
            var f2 = (float)rnd.NextDouble();
            rows.Add([f0, f1, f2]);
            labels.Add(f0 * 3f + f1 * 2f + (f0 > 0.5f && f1 > 0.5f ? 1.5f : 0f));
        }
        return (rows, labels);
    }

    private static (RegressionPredictorBase Predictor, ShapTreeEnsemble Ensemble, List<float[]> Background)
        FitAndExtract(RegressionPredictorBase predictor, int n = 300, int seed = 11)
    {
        var (rows, labels) = BuildData(n, seed);
        var ml = new MLContext(seed: 1);
        var data = ml.Data.LoadFromEnumerable(
            rows.Select((f, i) => new Row { Features = f, Label = labels[i] }));

        predictor.Fit(ml, data);
        var ensemble = predictor.TryBuildShapEnsemble(rows);
        Assert.NotNull(ensemble);
        return (predictor, ensemble!, rows);
    }

    public static TheoryData<string> TreeModels() => new() { "RandomForest", "GradientBoosting" };

    // --- 1. Ricostruzione della struttura -------------------------------------------------------

    [Theory]
    [MemberData(nameof(TreeModels))]
    public void ExtractedEnsemble_ReproducesModelPredictions(string modelType)
    {
        var (predictor, ensemble, background) = FitAndExtract(
            (RegressionPredictorBase)ReturnPredictorCatalog.CreateBase(modelType));
        using var _ = predictor;

        foreach (var row in background.Take(50))
        {
            var fromModel = predictor.Predict(row);
            var fromTrees = ensemble.Predict(row);
            // Tolleranza sul float di ML.NET contro il double della ricostruzione.
            Assert.True(Math.Abs(fromModel - fromTrees) < 1e-3,
                $"{modelType}: modello={fromModel:F6} ricostruito={fromTrees:F6}");
        }
    }

    // --- 2. Efficienza --------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(TreeModels))]
    public void Shap_SatisfiesEfficiency(string modelType)
    {
        var (predictor, ensemble, background) = FitAndExtract(
            (RegressionPredictorBase)ReturnPredictorCatalog.CreateBase(modelType));
        using var _ = predictor;

        var explainer = new TreeShapExplainer(ensemble);
        foreach (var row in background.Take(30))
        {
            var phi = explainer.Explain(row);
            var reconstructed = explainer.Baseline + phi.Sum();
            var prediction = ensemble.Predict(row);
            Assert.True(Math.Abs(reconstructed - prediction) < 1e-6,
                $"{modelType}: baseline+Σφ={reconstructed:F9} predizione={prediction:F9}");
        }
    }

    // --- 3. Confronto con Shapley esatto per forza bruta ----------------------------------------

    /// <summary>
    /// Valore atteso condizionato: le feature in <paramref name="subset"/> seguono il valore
    /// osservato, le altre si "diramano" secondo la copertura del background — la stessa
    /// definizione di assenza usata da TreeSHAP path-dependent.
    /// </summary>
    private static double ConditionalExpectation(ShapTree tree, float[] x, int subset)
    {
        return Walk(0, 1.0);

        double Walk(int node, double weight)
        {
            if (weight == 0) return 0;
            if (tree.IsLeaf(node)) return weight * tree.Value[node];

            var f = tree.SplitFeature[node];
            var l = tree.Left[node];
            var r = tree.Right[node];

            if ((subset & (1 << f)) != 0)
            {
                // Feature presente: si segue il ramo che il valore osservato impone.
                var hot = x[f] <= tree.Threshold[node] ? l : r;
                return Walk(hot, weight);
            }
            // Feature assente: entrambi i rami, pesati dalla copertura.
            return Walk(l, weight * tree.ChildFraction(node, l)) + Walk(r, weight * tree.ChildFraction(node, r));
        }
    }

    private static double[] BruteForceShapley(ShapTreeEnsemble ensemble, float[] x)
    {
        var n = ensemble.FeatureCount;
        var phi = new double[n];

        // Valore della "coalizione" S per l'intero ensemble (senza bias: è costante e si elide
        // nelle differenze marginali).
        double V(int subset)
        {
            var total = 0.0;
            for (var t = 0; t < ensemble.Trees.Count; t++)
            {
                total += ensemble.Weights[t] * ConditionalExpectation(ensemble.Trees[t], x, subset);
            }
            return total;
        }

        // Formula di Shapley: media pesata dei contributi marginali su tutti i sottoinsiemi.
        var factorial = new double[n + 1];
        factorial[0] = 1;
        for (var i = 1; i <= n; i++) factorial[i] = factorial[i - 1] * i;

        for (var f = 0; f < n; f++)
        {
            var bit = 1 << f;
            for (var subset = 0; subset < (1 << n); subset++)
            {
                if ((subset & bit) != 0) continue;
                var size = System.Numerics.BitOperations.PopCount((uint)subset);
                var weight = factorial[size] * factorial[n - size - 1] / factorial[n];
                phi[f] += weight * (V(subset | bit) - V(subset));
            }
        }
        return phi;
    }

    [Theory]
    [MemberData(nameof(TreeModels))]
    public void Shap_MatchesBruteForceShapleyValues(string modelType)
    {
        var (predictor, ensemble, background) = FitAndExtract(
            (RegressionPredictorBase)ReturnPredictorCatalog.CreateBase(modelType));
        using var _ = predictor;

        var explainer = new TreeShapExplainer(ensemble);
        foreach (var row in background.Take(12))
        {
            var fast = explainer.Explain(row);
            var exact = BruteForceShapley(ensemble, row);
            for (var f = 0; f < fast.Length; f++)
            {
                Assert.True(Math.Abs(fast[f] - exact[f]) < 1e-8,
                    $"{modelType} feature {f}: TreeSHAP={fast[f]:F10} Shapley esatto={exact[f]:F10}");
            }
        }
    }

    // --- 4. Feature inerte ----------------------------------------------------------------------

    [Fact]
    public void UnusedFeature_GetsExactlyZeroAttribution()
    {
        // Un albero costruito a mano che splitta SOLO su f0: f1 non compare mai.
        var tree = new ShapTree
        {
            Left = [1, -1, -1],           // nodo 0 → nodo 1 (sx) / foglia 1 (dx)
            Right = [2, -1, -1],
            SplitFeature = [0, -1, -1],
            Threshold = [0.5, 0, 0],
            Value = [0, 10, 20],
            Cover = [100, 50, 50],
            MaxDepth = 1,
        };
        var ensemble = new ShapTreeEnsemble
        {
            Trees = [tree],
            Weights = [1.0],
            Bias = 0,
            FeatureCount = 2,
        };

        var explainer = new TreeShapExplainer(ensemble);
        var phi = explainer.Explain([0.2f, 0.9f]);

        Assert.Equal(0.0, phi[1], 12);              // f1 è inerte
        Assert.Equal(10 - 15.0, phi[0], 12);        // f0 porta dalla media (15) al valore (10)
        Assert.Equal(15.0, explainer.Baseline, 12);
    }

    // --- Modelli non ad alberi ------------------------------------------------------------------

    [Fact]
    public void NonTreeModel_ReturnsNullInsteadOfThrowing()
    {
        var (rows, labels) = BuildData(200, 5);
        var ml = new MLContext(seed: 1);
        var data = ml.Data.LoadFromEnumerable(rows.Select((f, i) => new Row { Features = f, Label = labels[i] }));

        using var linear = new LinearReturnPredictor();
        linear.Fit(ml, data);

        // SHAP ad albero non si applica a un modello lineare: il contratto è "null", non eccezione.
        Assert.Null(linear.TryBuildShapEnsemble(rows));
    }

    [Fact]
    public void UntrainedModel_ReturnsNull()
    {
        using var forest = new RandomForestReturnPredictor();
        Assert.Null(forest.TryBuildShapEnsemble([[0f, 0f, 0f]]));
    }

    // --- Sintesi globale ------------------------------------------------------------------------

    [Fact]
    public void Summary_RanksTheDrivingFeaturesAboveTheNoiseFeature()
    {
        var (predictor, ensemble, background) = FitAndExtract(new GradientBoostingReturnPredictor());
        using var _ = predictor;

        var explainer = new TreeShapExplainer(ensemble);
        var summary = explainer.Summarize(background.Take(150).ToList(), ["f0", "f1", "rumore"]);

        // La label è costruita da f0 (peso 3) e f1 (peso 2); "rumore" non entra mai.
        Assert.Equal("f0", summary[0].FeatureName);
        Assert.Equal("f1", summary[1].FeatureName);
        Assert.Equal("rumore", summary[2].FeatureName);
        Assert.True(summary[2].MeanAbsShap < summary[1].MeanAbsShap * 0.5,
            $"il fattore rumore dovrebbe pesare molto meno: {summary[2].MeanAbsShap:F6} vs {summary[1].MeanAbsShap:F6}");
    }

    [Fact]
    public void ExplainRow_OrdersContributionsByAbsoluteImpact()
    {
        var (predictor, ensemble, background) = FitAndExtract(new RandomForestReturnPredictor());
        using var _ = predictor;

        var explainer = new TreeShapExplainer(ensemble);
        var explanation = explainer.ExplainRow(background[0], ["f0", "f1", "rumore"]);

        Assert.Equal(3, explanation.Contributions.Count);
        for (var i = 1; i < explanation.Contributions.Count; i++)
        {
            Assert.True(Math.Abs(explanation.Contributions[i - 1].Value) >= Math.Abs(explanation.Contributions[i].Value));
        }
        Assert.Equal(explanation.Prediction, explanation.Baseline + explanation.Contributions.Sum(c => c.Value), 6);
    }
}
