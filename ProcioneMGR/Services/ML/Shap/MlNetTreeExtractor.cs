using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;
using Microsoft.ML.Trainers.LightGbm;

namespace ProcioneMGR.Services.ML.Shap;

/// <summary>
/// Estrae la struttura degli alberi da un <see cref="ITransformer"/> ML.NET addestrato
/// (FastForest o LightGBM) e la converte nella forma neutra <see cref="ShapTreeEnsemble"/>,
/// calcolando la copertura per nodo su un dataset di background.
///
/// Restituisce <c>null</c> — non solleva — quando il modello non è ad alberi (lineare, MLP,
/// attention, stacking): SHAP ad albero semplicemente non si applica, e il chiamante deve poter
/// ripiegare sulla permutation importance senza gestire eccezioni per un caso previsto.
/// </summary>
public static class MlNetTreeExtractor
{
    /// <summary>
    /// Converte un modello ML.NET in ensemble neutro. <paramref name="background"/> è il dataset
    /// da cui si misura la copertura dei nodi (tipicamente il train set): senza di esso non
    /// esisterebbe una distribuzione rispetto a cui definire "feature assente".
    /// </summary>
    public static ShapTreeEnsemble? TryExtract(ITransformer model, IReadOnlyList<float[]> background, int featureCount)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(background);

        if (!TryFindTreeModel(model, out var trees, out var weights, out var bias)) return null;

        var shapTrees = new List<ShapTree>(trees.Count);
        foreach (var t in trees)
        {
            shapTrees.Add(Convert(t, background));
        }

        return new ShapTreeEnsemble
        {
            Trees = shapTrees,
            Weights = weights,
            Bias = bias,
            FeatureCount = featureCount,
        };
    }

    // --- Navigazione del transformer -----------------------------------------------------------

    /// <summary>
    /// Un modello caricato da disco arriva spesso come <see cref="TransformerChain{T}"/> anche
    /// quando la pipeline di training era un singolo trainer: la ricerca scende ricorsivamente
    /// nelle catene invece di assumere una forma sola.
    /// </summary>
    private static bool TryFindTreeModel(
        ITransformer model,
        out IReadOnlyList<RegressionTreeBase> trees,
        out IReadOnlyList<double> weights,
        out double bias)
    {
        trees = [];
        weights = [];
        bias = 0;

        switch (model)
        {
            case RegressionPredictionTransformer<FastForestRegressionModelParameters> ff:
            {
                var ens = ff.Model.TrainedTreeEnsemble;
                trees = [.. ens.Trees];
                // Una foresta MEDIA i suoi alberi, non li somma: ML.NET tiene TreeWeights a 1 e
                // divide per il numero di alberi al momento della predizione. Senza questa
                // normalizzazione la ricostruzione sbaglia di un fattore pari al numero di alberi
                // — che è esattamente ciò che il test di ricostruzione ha misurato (96×) prima che
                // la riga esistesse. Nota: il numero EFFETTIVO di alberi può essere minore di
                // quello richiesto, quindi si divide per quelli davvero presenti.
                var count = Math.Max(1, ens.Trees.Count);
                weights = [.. ens.TreeWeights.Select(w => w / count)];
                bias = ens.Bias;
                return true;
            }
            case RegressionPredictionTransformer<LightGbmRegressionModelParameters> lgb:
            {
                var ens = lgb.Model.TrainedTreeEnsemble;
                trees = [.. ens.Trees];
                weights = [.. ens.TreeWeights];
                bias = ens.Bias;
                return true;
            }
            case IEnumerable<ITransformer> chain:
            {
                foreach (var inner in chain)
                {
                    if (TryFindTreeModel(inner, out trees, out weights, out bias)) return true;
                }
                return false;
            }
            default:
                return false;
        }
    }

    // --- Conversione di un singolo albero -------------------------------------------------------

    private static ShapTree Convert(RegressionTreeBase tree, IReadOnlyList<float[]> background)
    {
        // ML.NET: NumberOfNodes = nodi interni, NumberOfLeaves = foglie. Nell'indicizzazione
        // unificata le foglie seguono i nodi interni.
        var internalCount = tree.NumberOfNodes;
        var leafCount = tree.NumberOfLeaves;
        var total = internalCount + leafCount;

        var left = new int[total];
        var right = new int[total];
        var splitFeature = new int[total];
        var threshold = new double[total];
        var value = new double[total];

        for (var i = 0; i < internalCount; i++)
        {
            // Figlio < 0 in ML.NET significa foglia di indice ~figlio (−1 → foglia 0, −2 → foglia 1…).
            left[i] = tree.LeftChild[i] >= 0 ? tree.LeftChild[i] : internalCount + ~tree.LeftChild[i];
            right[i] = tree.RightChild[i] >= 0 ? tree.RightChild[i] : internalCount + ~tree.RightChild[i];
            splitFeature[i] = tree.NumericalSplitFeatureIndexes[i];
            threshold[i] = tree.NumericalSplitThresholds[i];
        }

        for (var k = 0; k < leafCount; k++)
        {
            var node = internalCount + k;
            left[node] = -1;
            right[node] = -1;
            splitFeature[node] = -1;
            value[node] = tree.LeafValues[k];
        }

        // La radice è sempre il nodo 0: con zero nodi interni (albero di sola foglia) l'indice 0
        // è già la foglia, perché le foglie partono da internalCount.
        var cover = ComputeCover(left, right, splitFeature, threshold, total, background);

        return new ShapTree
        {
            Left = left,
            Right = right,
            SplitFeature = splitFeature,
            Threshold = threshold,
            Value = value,
            Cover = cover,
            MaxDepth = ComputeMaxDepth(left, right, total),
        };
    }

    /// <summary>
    /// Copertura per nodo: quanti campioni di background attraversano ciascun nodo. È il dato che
    /// ML.NET non espone e che TreeSHAP path-dependent richiede.
    /// </summary>
    private static double[] ComputeCover(
        int[] left, int[] right, int[] splitFeature, double[] threshold, int total,
        IReadOnlyList<float[]> background)
    {
        var cover = new double[total];
        foreach (var row in background)
        {
            var node = 0;
            cover[node]++;
            while (left[node] >= 0)
            {
                var f = splitFeature[node];
                var v = f >= 0 && f < row.Length ? row[f] : 0f;
                node = v <= threshold[node] ? left[node] : right[node];
                cover[node]++;
            }
        }
        return cover;
    }

    private static int ComputeMaxDepth(int[] left, int[] right, int total)
    {
        if (total == 0) return 0;
        var depth = new int[total];
        var max = 0;
        // I figli hanno sempre indice maggiore del padre fra i nodi interni di ML.NET, ma non ci
        // facciamo affidamento: una pila esplicita è altrettanto economica e non assume nulla.
        var stack = new Stack<(int Node, int Depth)>();
        stack.Push((0, 0));
        while (stack.Count > 0)
        {
            var (node, d) = stack.Pop();
            depth[node] = d;
            if (d > max) max = d;
            if (left[node] >= 0)
            {
                stack.Push((left[node], d + 1));
                stack.Push((right[node], d + 1));
            }
        }
        return max;
    }
}
