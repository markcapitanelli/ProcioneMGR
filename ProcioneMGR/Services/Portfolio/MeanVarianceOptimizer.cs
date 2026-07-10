using MathNet.Numerics.LinearAlgebra;

namespace ProcioneMGR.Services.Portfolio;

/// <summary>
/// Ottimizzazione Mean-Variance di Markowitz (cap. 5): soluzione analitica (non QP iterativo)
/// per due obiettivi classici, poi vincoli long-only/Min/Max applicati riusando l'algoritmo di
/// <c>EnsembleAllocator</c> (coerenza con l'allocatore già esistente per le strategie):
///
///  - <b>MaxSharpe</b> (portafoglio tangente): w ∝ Σ⁻¹(μ - r_f), la combinazione che massimizza
///    lo Sharpe ratio senza vincoli di segno.
///  - <b>MinVariance</b>: w ∝ Σ⁻¹·1, il portafoglio a varianza minima globale (non usa μ, solo
///    la struttura di covarianza — più robusto quando le stime di rendimento atteso sono
///    rumorose, che è quasi sempre il caso).
///
/// La soluzione grezza può contenere pesi negativi (posizioni short); qui il portafoglio è
/// long-only per costruzione della piattaforma, quindi i negativi vengono trattati come "punteggio
/// zero" ed esclusi, poi si applicano i vincoli Min/Max con lo stesso water-filling vincolato di
/// <c>EnsembleAllocator</c>.
/// </summary>
public sealed class MeanVarianceOptimizer : IPortfolioOptimizer
{
    public string Name => "MeanVariance";

    public PortfolioAllocation Optimize(IReadOnlyDictionary<string, IReadOnlyList<decimal>> returnsBySymbol, PortfolioOptimizationConfig? config = null)
    {
        config ??= new PortfolioOptimizationConfig();
        var (symbols, returns) = PortfolioMath.BuildMatrix(returnsBySymbol);
        var rawCov = config.CovarianceEstimator == CovarianceEstimator.LedoitWolf
            ? PortfolioMath.LedoitWolf(returns).Covariance
            : PortfolioMath.Covariance(returns);
        var covariance = PortfolioMath.Regularize(rawCov);

        Vector<double> raw;
        if (config.Objective == MeanVarianceObjective.MinVariance)
        {
            var ones = Vector<double>.Build.Dense(symbols.Count, 1.0);
            raw = covariance.Solve(ones);
        }
        else
        {
            var mean = PortfolioMath.Mean(returns);
            var rfPerPeriod = (double)config.RiskFreeRateAnnual / config.PeriodsPerYear;
            var excess = mean - Vector<double>.Build.Dense(symbols.Count, rfPerPeriod);
            raw = covariance.Solve(excess);
        }

        // Negativi (posizioni short) -> punteggio zero: il portafoglio è long-only.
        var scores = raw.Select(v => Math.Max(0.0, v)).ToList();
        var weights = PortfolioMath.ToConstrainedWeights(symbols, scores, config.MinWeight, config.MaxWeight);
        return new PortfolioAllocation { Weights = weights };
    }
}
