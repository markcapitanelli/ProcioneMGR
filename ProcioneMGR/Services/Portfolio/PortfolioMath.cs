using MathNet.Numerics.LinearAlgebra;

namespace ProcioneMGR.Services.Portfolio;

/// <summary>Helper comuni agli allocatori: validazione input, matrice dei rendimenti, media/covarianza/volatilità.</summary>
internal static class PortfolioMath
{
    public static (List<string> Symbols, Matrix<double> Returns) BuildMatrix(IReadOnlyDictionary<string, IReadOnlyList<decimal>> returnsBySymbol)
    {
        ArgumentNullException.ThrowIfNull(returnsBySymbol);
        if (returnsBySymbol.Count < 2)
        {
            throw new ArgumentException("Servono almeno 2 simboli.", nameof(returnsBySymbol));
        }

        var symbols = returnsBySymbol.Keys.ToList();
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
            throw new ArgumentException("Servono almeno 3 osservazioni per stimare una covarianza.", nameof(returnsBySymbol));
        }

        var matrix = Matrix<double>.Build.Dense(n, symbols.Count, (row, col) => (double)returnsBySymbol[symbols[col]][row]);
        return (symbols, matrix);
    }

    public static Vector<double> Mean(Matrix<double> returns) =>
        Vector<double>.Build.Dense(returns.ColumnCount, col => returns.Column(col).Average());

    public static Matrix<double> Covariance(Matrix<double> returns)
    {
        var n = returns.RowCount;
        var means = Mean(returns);
        var centered = Matrix<double>.Build.Dense(returns.RowCount, returns.ColumnCount,
            (row, col) => returns[row, col] - means[col]);
        return centered.TransposeThisAndMultiply(centered) / (n - 1);
    }

    /// <summary>
    /// Diagonal loading (ridge) per stabilizzare <c>Solve()</c> su una matrice di covarianza quasi
    /// singolare — caso realistico con asset crypto fortemente correlati (es. altcoin vs BTC) o
    /// quando il numero di osservazioni è vicino al numero di asset. Senza regolarizzazione, la
    /// LU-solve di MathNet può restituire pesi numericamente instabili (grandezza enorme, segno
    /// sbagliato) senza sollevare un errore. Il ridge è trascurabile su matrici ben condizionate.
    /// </summary>
    public static Matrix<double> Regularize(Matrix<double> covariance, double ridgeFactor = 1e-9)
    {
        var n = covariance.RowCount;
        var ridge = Math.Max(covariance.Diagonal().Average(), 1e-12) * ridgeFactor;
        return covariance + Matrix<double>.Build.DenseIdentity(n) * ridge;
    }

    public static Vector<double> StdDev(Matrix<double> returns)
    {
        var cov = Covariance(returns);
        return Vector<double>.Build.Dense(cov.RowCount, i => Math.Sqrt(Math.Max(0.0, cov[i, i])));
    }

    public static double[,] CorrelationFromCovariance(Matrix<double> covariance)
    {
        var p = covariance.RowCount;
        var std = Vector<double>.Build.Dense(p, i => Math.Sqrt(Math.Max(covariance[i, i], 1e-18)));
        var corr = new double[p, p];
        for (var i = 0; i < p; i++)
        {
            for (var j = 0; j < p; j++)
            {
                corr[i, j] = covariance[i, j] / (std[i] * std[j]);
            }
        }
        return corr;
    }

    /// <summary>Converte punteggi grezzi (possibilmente negativi/nulli) in pesi vincolati [min,max] che sommano a 1, riusando l'algoritmo di <c>EnsembleAllocator</c> (negativi -> 0, water-filling vincolato).</summary>
    public static Dictionary<string, decimal> ToConstrainedWeights(IReadOnlyList<string> symbols, IReadOnlyList<double> rawScores, decimal minWeight, decimal maxWeight)
    {
        var scores = rawScores.Select(s => (decimal)s).ToList();
        var weights = ProcioneMGR.Services.Ensemble.EnsembleAllocator.ComputeWeights(scores, minWeight, maxWeight);

        var result = new Dictionary<string, decimal>(symbols.Count);
        for (var i = 0; i < symbols.Count; i++)
        {
            result[symbols[i]] = weights[i];
        }
        return result;
    }
}
