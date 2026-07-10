namespace ProcioneMGR.Services.Portfolio;

/// <summary>
/// Risk Parity (cap. 5). Due modalità (<see cref="PortfolioOptimizationConfig.RiskParityMethod"/>):
///
///  - <b>EqualRiskContribution</b> (default): ERC ESATTO — ogni asset contribuisce IDENTICAMENTE alla
///    varianza del portafoglio, tenendo conto delle correlazioni. Risolto con l'algoritmo di coordinate
///    cyclical (Griveau-Billion et al. 2013) in <see cref="PortfolioMath.EqualRiskContribution"/>, che è
///    robusto e convergente; la covarianza è stimata con Ledoit-Wolf per correlazioni affidabili.
///  - <b>InverseVolatility</b>: w_i ∝ 1/σ_i, l'approssimazione classica (= ERC esatto solo quando le
///    correlazioni fra asset sono uniformi). Mantenuta per confronto/retro-compatibilità.
/// </summary>
public sealed class RiskParityOptimizer : IPortfolioOptimizer
{
    public string Name => "RiskParity";

    public PortfolioAllocation Optimize(IReadOnlyDictionary<string, IReadOnlyList<decimal>> returnsBySymbol, PortfolioOptimizationConfig? config = null)
    {
        config ??= new PortfolioOptimizationConfig();
        var (symbols, returns) = PortfolioMath.BuildMatrix(returnsBySymbol);

        List<double> scores;
        if (config.RiskParityMethod == RiskParityMethod.EqualRiskContribution)
        {
            var cov = PortfolioMath.LedoitWolf(returns).Covariance;
            scores = PortfolioMath.EqualRiskContribution(cov).ToList();
        }
        else
        {
            var stdDev = PortfolioMath.StdDev(returns);
            scores = stdDev.Select(sigma => sigma > 1e-12 ? 1.0 / sigma : 0.0).ToList();
        }

        var weights = PortfolioMath.ToConstrainedWeights(symbols, scores, config.MinWeight, config.MaxWeight);
        return new PortfolioAllocation { Weights = weights };
    }
}
