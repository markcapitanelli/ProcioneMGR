using MathNet.Numerics.LinearAlgebra;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Services.Portfolio;

/// <summary>
/// Hierarchical Risk Parity (López de Prado, cap. 16 di "Advances in Financial Machine
/// Learning" — citato al cap. 5/13 del libro di Jansen). A differenza di Mean-Variance, non
/// richiede l'inversione della matrice di covarianza (instabile quando gli asset sono molto
/// correlati): raggruppa gli asset per similarità (clustering gerarchico sulla distanza di
/// correlazione, riusando <see cref="IHierarchicalClustering"/> del cap. 13), poi alloca il
/// peso ricorsivamente per bisezione, dando più peso ai (sotto-)cluster meno rischiosi.
///
/// Pipeline: correlazione -> distanza di Mantegna -> dendrogramma (linkage configurabile, default
/// Average/UPGMA per evitare il chaining del single-linkage) -> ordine quasi-diagonale (l'ordine
/// delle foglie nel dendrogramma, che riflette naturalmente la struttura di correlazione) ->
/// bisezione ricorsiva.
/// </summary>
public sealed class HierarchicalRiskParityOptimizer(IHierarchicalClustering clustering) : IPortfolioOptimizer
{
    public string Name => "HRP";

    public PortfolioAllocation Optimize(IReadOnlyDictionary<string, IReadOnlyList<decimal>> returnsBySymbol, PortfolioOptimizationConfig? config = null)
    {
        config ??= new PortfolioOptimizationConfig();
        var (symbols, returns) = PortfolioMath.BuildMatrix(returnsBySymbol);
        var covariance = PortfolioMath.Covariance(returns);
        var correlation = PortfolioMath.CorrelationFromCovariance(covariance);

        var distance = CorrelationDistance.FromCorrelationMatrix(correlation);
        var root = clustering.BuildDendrogram(distance, symbols, config.HrpLinkage);

        // L'ordine delle foglie nel dendrogramma (quasi-diagonale): asset simili sono vicini.
        var sortedIndices = root.LeafIndices.ToList();

        var weights = new double[symbols.Count];
        Array.Fill(weights, 1.0);
        RecursiveBisection(sortedIndices, weights, covariance);

        var scores = weights.ToList();
        var result = PortfolioMath.ToConstrainedWeights(symbols, scores, config.MinWeight, config.MaxWeight);
        return new PortfolioAllocation { Weights = result };
    }

    private static void RecursiveBisection(List<int> indices, double[] weights, Matrix<double> covariance)
    {
        if (indices.Count <= 1) return;

        var mid = indices.Count / 2;
        var left = indices.Take(mid).ToList();
        var right = indices.Skip(mid).ToList();

        var varLeft = ClusterVariance(left, covariance);
        var varRight = ClusterVariance(right, covariance);
        // Chi rischia meno riceve una quota maggiore (risk parity fra i due sotto-cluster).
        var alpha = varLeft + varRight > 0 ? varRight / (varLeft + varRight) : 0.5;

        foreach (var i in left) weights[i] *= alpha;
        foreach (var i in right) weights[i] *= 1 - alpha;

        RecursiveBisection(left, weights, covariance);
        RecursiveBisection(right, weights, covariance);
    }

    /// <summary>Varianza del sotto-portafoglio con pesi a inverse-varianza (solo per stimare il rischio relativo dei due rami, non l'allocazione finale).</summary>
    private static double ClusterVariance(List<int> indices, Matrix<double> covariance)
    {
        var invVar = indices.Select(i => 1.0 / Math.Max(covariance[i, i], 1e-12)).ToArray();
        var sum = invVar.Sum();
        var w = invVar.Select(v => v / sum).ToArray();

        double variance = 0;
        for (var a = 0; a < indices.Count; a++)
        {
            for (var b = 0; b < indices.Count; b++)
            {
                variance += w[a] * w[b] * covariance[indices[a], indices[b]];
            }
        }
        return variance;
    }
}
