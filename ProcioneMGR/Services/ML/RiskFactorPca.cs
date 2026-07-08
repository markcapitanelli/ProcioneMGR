using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Factorization;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Implementazione di <see cref="IRiskFactorPca"/> via eigen-decomposizione (MathNet.Numerics).
///
/// DEVIAZIONE FLAGGATA rispetto al piano (che indicava ML.NET): usiamo MathNet.Numerics invece
/// di <c>mlContext.Transforms.ProjectToPrincipalComponents</c> perché quest'ultimo non espone
/// pubblicamente gli autovalori, necessari per calcolare la varianza spiegata per componente —
/// un dato imprescindibile in finanza per capire quanto rischio comune cattura ogni fattore.
/// MathNet dà accesso diretto e verificabile ad autovalori/autovettori.
/// </summary>
public sealed class RiskFactorPca : IRiskFactorPca
{
    public RiskFactorPcaResult Compute(IReadOnlyDictionary<string, IReadOnlyList<decimal>> returnsBySymbol, int componentCount)
    {
        ArgumentNullException.ThrowIfNull(returnsBySymbol);
        if (returnsBySymbol.Count < 2)
        {
            throw new ArgumentException("Servono almeno 2 serie di rendimenti.", nameof(returnsBySymbol));
        }

        var symbols = returnsBySymbol.Keys.ToList();
        var p = symbols.Count;
        var n = returnsBySymbol[symbols[0]].Count;
        foreach (var s in symbols)
        {
            if (returnsBySymbol[s].Count != n)
            {
                throw new ArgumentException("Le serie di rendimenti devono avere la stessa lunghezza (allineate per timestamp).", nameof(returnsBySymbol));
            }
        }
        if (n < 3)
        {
            throw new ArgumentException("Servono almeno 3 osservazioni per calcolare una correlazione.", nameof(returnsBySymbol));
        }
        if (componentCount < 1 || componentCount > p)
        {
            throw new ArgumentOutOfRangeException(nameof(componentCount));
        }

        var raw = Matrix<double>.Build.Dense(n, p, (row, col) => (double)returnsBySymbol[symbols[col]][row]);

        // Standardizzazione per colonna (z-score) -> PCA sulla matrice di CORRELAZIONE, non di
        // covarianza: evita che gli asset più volatili dominino le componenti solo per scala.
        var means = Vector<double>.Build.Dense(p, col => raw.Column(col).Average());
        var stds = Vector<double>.Build.Dense(p, col =>
        {
            var m = means[col];
            var variance = raw.Column(col).Sum(x => (x - m) * (x - m)) / (n - 1);
            return Math.Sqrt(variance);
        });
        var standardized = Matrix<double>.Build.Dense(n, p, (row, col) =>
            stds[col] > 1e-12 ? (raw[row, col] - means[col]) / stds[col] : 0.0);

        // Matrice di correlazione (p x p) ed eigen-decomposizione (simmetrica -> autovalori reali).
        var correlation = standardized.TransposeThisAndMultiply(standardized) / (n - 1);
        Evd<double> evd = correlation.Evd(Symmetricity.Symmetric);

        var eigenvalues = evd.EigenValues.Select(c => c.Real).ToArray();
        var eigenvectors = evd.EigenVectors;
        var totalVariance = eigenvalues.Sum();

        // MathNet non garantisce l'ordine: ordiniamo per autovalore (varianza spiegata) decrescente.
        var order = Enumerable.Range(0, p).OrderByDescending(i => eigenvalues[i]).Take(componentCount).ToList();

        var components = new List<PrincipalComponent>(order.Count);
        for (var k = 0; k < order.Count; k++)
        {
            var idx = order[k];
            var eigenvector = eigenvectors.Column(idx);

            var loadings = new Dictionary<string, double>(p);
            for (var col = 0; col < p; col++)
            {
                loadings[symbols[col]] = eigenvector[col];
            }

            var scores = new double[n];
            for (var row = 0; row < n; row++)
            {
                double score = 0;
                for (var col = 0; col < p; col++)
                {
                    score += standardized[row, col] * eigenvector[col];
                }
                scores[row] = score;
            }

            components.Add(new PrincipalComponent
            {
                Index = k + 1,
                ExplainedVarianceRatio = totalVariance > 0 ? eigenvalues[idx] / totalVariance : 0,
                Loadings = loadings,
                Scores = scores,
            });
        }

        return new RiskFactorPcaResult { Symbols = symbols, Components = components };
    }
}
