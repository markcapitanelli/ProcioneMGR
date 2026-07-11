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

    /// <summary>
    /// Stimatore di covarianza <b>Ledoit-Wolf</b> (2004, "A well-conditioned estimator for
    /// large-dimensional covariance matrices"): riduce (shrink) la covarianza campionaria S verso il
    /// target strutturato F = μI (μ = varianza media) con intensità ottimale δ* stimata dai dati.
    /// Σ* = δ*·μI + (1−δ*)·S. Con poche osservazioni o asset molto correlati (crypto), S è
    /// mal condizionata e la sua inversa (usata da Mean-Variance) produce pesi instabili; lo shrinkage
    /// è la correzione standard, molto più mirata del semplice ridge. δ* ∈ [0,1]: 0 = nessuno shrink
    /// (tanti dati), 1 = target puro (pochissimi dati). Norma di Frobenius normalizzata per p, come nel paper.
    /// </summary>
    public static (Matrix<double> Covariance, double Shrinkage) LedoitWolf(Matrix<double> returns)
    {
        var n = returns.RowCount;
        var p = returns.ColumnCount;
        var means = Mean(returns);
        var x = Matrix<double>.Build.Dense(n, p, (row, col) => returns[row, col] - means[col]); // centrato

        var s = x.TransposeThisAndMultiply(x) / n; // covarianza campionaria con 1/n (convenzione LW)
        var mu = s.Diagonal().Sum() / p;           // <S,I> = trace(S)/p

        // d² = ||S − μI||²  (norma di Frobenius / p)
        double d2 = 0.0;
        for (var i = 0; i < p; i++)
        {
            for (var j = 0; j < p; j++)
            {
                var t = s[i, j] - (i == j ? mu : 0.0);
                d2 += t * t;
            }
        }
        d2 /= p;

        // b̄² = (1/n²) Σ_k || x_k x_kᵀ − S ||²  (stessa norma normalizzata per p)
        double bbar2 = 0.0;
        for (var k = 0; k < n; k++)
        {
            double acc = 0.0;
            for (var i = 0; i < p; i++)
            {
                var xki = x[k, i];
                for (var j = 0; j < p; j++)
                {
                    var t = xki * x[k, j] - s[i, j];
                    acc += t * t;
                }
            }
            bbar2 += acc / p;
        }
        bbar2 /= (double)n * n;

        var b2 = Math.Min(bbar2, d2);          // b² ≤ d² ⇒ δ ∈ [0,1]
        var delta = d2 > 1e-18 ? b2 / d2 : 0.0;

        var shrunk = Matrix<double>.Build.Dense(p, p, (i, j) => (1.0 - delta) * s[i, j] + (i == j ? delta * mu : 0.0));
        return (shrunk, delta);
    }

    /// <summary>
    /// Portafoglio <b>Equal Risk Contribution</b> ESATTO (Maillard-Roncalli-Teiletche; algoritmo di
    /// coordinate cyclical di Griveau-Billion et al. 2013): trova w &gt; 0 tale che ogni asset
    /// contribuisca IDENTICAMENTE alla varianza del portafoglio, RC_i = w_i·(Σw)_i uguale per tutti —
    /// a differenza dell'inverse-volatility (che è l'ERC esatto SOLO a correlazioni uniformi), qui si
    /// tiene conto della struttura di correlazione. Risolve il fixed-point
    /// w_i = (−β_i + √(β_i² + 4·Σ_ii·b)) / (2·Σ_ii), con β_i = Σ_{j≠i} Σ_ij w_j e budget b = 1/p,
    /// aggiornando ciclicamente (Gauss-Seidel) fino a convergenza; pesi grezzi poi normalizzati a somma 1.
    /// </summary>
    public static double[] EqualRiskContribution(Matrix<double> covariance, int maxIterations = 1000, double tolerance = 1e-10)
    {
        var p = covariance.RowCount;
        var b = 1.0 / p;

        // Inizializzazione a inverse-volatility (buon punto di partenza, convergenza in pochi sweep).
        var w = new double[p];
        for (var i = 0; i < p; i++) w[i] = 1.0 / Math.Sqrt(Math.Max(covariance[i, i], 1e-12));

        for (var iter = 0; iter < maxIterations; iter++)
        {
            var maxDelta = 0.0;
            for (var i = 0; i < p; i++)
            {
                double beta = 0.0;
                for (var j = 0; j < p; j++)
                {
                    if (j != i) beta += covariance[i, j] * w[j];
                }
                var alpha = Math.Max(covariance[i, i], 1e-18);
                var wi = (-beta + Math.Sqrt(beta * beta + 4.0 * alpha * b)) / (2.0 * alpha);
                maxDelta = Math.Max(maxDelta, Math.Abs(wi - w[i]));
                w[i] = wi;
            }
            if (maxDelta < tolerance) break;
        }

        var sum = w.Sum();
        if (sum > 0.0)
        {
            for (var i = 0; i < p; i++) w[i] /= sum;
        }
        return w;
    }

    /// <summary>
    /// Contributi di rischio percentuali di un portafoglio: RC_i = w_i·(Σw)_i / (wᵀΣw), somma 1.
    /// È la quantità che l'ERC pareggia — mostrarla è il modo onesto di verificare quanto una
    /// allocazione concentra il rischio (i pesi da soli non bastano: un asset più volatile
    /// contribuisce più del suo peso).
    /// </summary>
    public static double[] RiskContributions(Matrix<double> covariance, IReadOnlyList<double> weights)
    {
        var p = covariance.RowCount;
        var w = Vector<double>.Build.Dense(p, i => weights[i]);
        var sigmaW = covariance * w;
        var total = w * sigmaW; // varianza del portafoglio
        var rc = new double[p];
        if (total <= 1e-18)
        {
            return rc;
        }
        for (var i = 0; i < p; i++)
        {
            rc[i] = w[i] * sigmaW[i] / total;
        }
        return rc;
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
