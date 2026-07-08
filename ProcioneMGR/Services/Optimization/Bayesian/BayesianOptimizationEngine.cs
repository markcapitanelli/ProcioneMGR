using MathNet.Numerics.Distributions;
using MathNet.Numerics.LinearAlgebra;

namespace ProcioneMGR.Services.Optimization.Bayesian;

/// <summary>Iperparametri del surrogato Gaussian Process e dell'acquisizione.</summary>
public sealed class BayesianOptions
{
    /// <summary>Lengthscale del kernel RBF sullo spazio normalizzato [0,1]^d.</summary>
    public double LengthScale { get; set; } = 0.2;

    /// <summary>Varianza del segnale (ampiezza a priori delle funzioni).</summary>
    public double SignalVariance { get; set; } = 1.0;

    /// <summary>Rumore/regolarizzazione sulla diagonale (stabilità numerica + osservazioni rumorose).</summary>
    public double NoiseVariance { get; set; } = 1e-6;

    /// <summary>Parametro di esplorazione ξ dell'Expected Improvement (più alto ⇒ più esplorativo).</summary>
    public double ExplorationXi { get; set; } = 0.01;

    /// <summary>Quanti candidati casuali campionare per massimizzare l'acquisizione a ogni passo.</summary>
    public int AcquisitionSamples { get; set; } = 512;

    /// <summary>Seme di base: la ricerca è deterministica a parità di seme e di storia.</summary>
    public int Seed { get; set; } = 42;
}

/// <summary>
/// Ottimizzatore di iperparametri: dato lo storico dei punti valutati, propone il prossimo punto da
/// provare. Alternativa al grid search esaustivo quando lo spazio dei parametri è grande e ogni
/// valutazione è costosa (un walk-forward completo). Rif. docs/ROADMAP-QLIB §1.6.
///
/// METODOLOGIA (decisione presa nell'aggancio a OptimizationEngine): l'obiettivo che GUIDA la
/// ricerca è lo <b>Sharpe</b> della finestra (la stessa <c>Statistics.SharpeRatio</c> usata dal
/// grid, in-sample o out-of-sample secondo <c>SelectionMetric</c>) — un surrogato economico e
/// STAZIONARIO. Il <b>Deflated Sharpe</b> NON è ricalcolato a ogni iterazione (sarebbe
/// non-stazionario: la correzione da test multipli cambia con ogni nuovo trial) bensì applicato UNA
/// VOLTA a fine ricerca come VERDETTO sul migliore, sulla distribuzione di TUTTI i punti visitati —
/// esattamente il ruolo che il DSR ha per il grid. Il contratto resta agnostico rispetto
/// all'obiettivo (riceve solo punteggi).
/// </summary>
public interface IHyperparameterOptimizer
{
    /// <summary>Prossimo vettore di parametri (spazio reale) che massimizza l'acquisizione dato lo storico.</summary>
    double[] SuggestNext(IReadOnlyList<EvaluatedPoint> history, ParameterSpace space);
}

/// <summary>
/// <see cref="IHyperparameterOptimizer"/> via <b>Gaussian Process</b> (kernel RBF, media/varianza
/// posteriore in forma chiusa con MathNet) e acquisizione <b>Expected Improvement</b>. Nessuna
/// libreria GP dedicata: kernel + Cholesky + solve sono poche decine di righe di algebra lineare.
/// Deterministico a parità di <see cref="BayesianOptions.Seed"/> e di storia.
/// </summary>
public sealed class BayesianOptimizationEngine(BayesianOptions? options = null) : IHyperparameterOptimizer
{
    private readonly BayesianOptions _opt = options ?? new BayesianOptions();

    public double[] SuggestNext(IReadOnlyList<EvaluatedPoint> history, ParameterSpace space)
    {
        ArgumentNullException.ThrowIfNull(space);
        var d = space.Dimensions.Count;

        // Deterministico ma diverso a ogni passo: il seme dipende dalla lunghezza della storia.
        var rng = new Random(_opt.Seed + (history?.Count ?? 0));

        // Nessuna osservazione: primo punto casuale (l'inizializzazione la fa di solito il driver).
        if (history is null || history.Count == 0) return space.Denormalize(SampleUnit(rng, d));

        var n = history.Count;
        var x = new double[n][];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = space.Normalize(history[i].Parameters);
            y[i] = history[i].Score;
        }
        var yMean = y.Average();

        // K = kernel + rumore·I; Cholesky per un solve stabile. α = K⁻¹(y − ȳ).
        var kMatrix = Matrix<double>.Build.Dense(n, n, (i, j) => Rbf(x[i], x[j]) + (i == j ? _opt.NoiseVariance : 0.0));
        var chol = kMatrix.Cholesky();
        var alpha = chol.Solve(Vector<double>.Build.Dense(n, i => y[i] - yMean));

        var bestObserved = y.Max();

        // Massimizza l'Expected Improvement su candidati casuali (semplice, robusto, deterministico).
        double[] bestCandidate = SampleUnit(rng, d);
        var bestEi = double.NegativeInfinity;
        for (var s = 0; s < Math.Max(1, _opt.AcquisitionSamples); s++)
        {
            var cand = SampleUnit(rng, d);
            var kStar = Vector<double>.Build.Dense(n, i => Rbf(cand, x[i]));

            var mean = yMean + kStar.DotProduct(alpha);
            var variance = _opt.SignalVariance - kStar.DotProduct(chol.Solve(kStar));
            var sigma = Math.Sqrt(Math.Max(variance, 1e-12));

            var ei = ExpectedImprovement(mean, sigma, bestObserved, _opt.ExplorationXi);
            if (ei > bestEi) { bestEi = ei; bestCandidate = cand; }
        }
        return space.Denormalize(bestCandidate);
    }

    private double Rbf(double[] a, double[] b)
    {
        var sq = 0.0;
        for (var i = 0; i < a.Length; i++) { var dd = a[i] - b[i]; sq += dd * dd; }
        return _opt.SignalVariance * Math.Exp(-sq / (2.0 * _opt.LengthScale * _opt.LengthScale));
    }

    /// <summary>EI per la MASSIMIZZAZIONE: (μ−f⁺−ξ)Φ(z) + σφ(z), con z = (μ−f⁺−ξ)/σ.</summary>
    private static double ExpectedImprovement(double mean, double sigma, double bestObserved, double xi)
    {
        if (sigma <= 0.0) return 0.0;
        var improvement = mean - bestObserved - xi;
        var z = improvement / sigma;
        return improvement * Normal.CDF(0.0, 1.0, z) + sigma * Normal.PDF(0.0, 1.0, z);
    }

    private static double[] SampleUnit(Random rng, int d)
    {
        var x = new double[d];
        for (var i = 0; i < d; i++) x[i] = rng.NextDouble();
        return x;
    }
}
