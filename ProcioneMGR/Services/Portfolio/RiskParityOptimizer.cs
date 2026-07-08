namespace ProcioneMGR.Services.Portfolio;

/// <summary>
/// Risk Parity (cap. 5) — implementazione <b>naive</b> a volatilità inversa: w_i ∝ 1/σ_i.
///
/// DEVIAZIONE FLAGGATA: la vera "equal risk contribution" (ERC) — dove ogni asset contribuisce
/// IDENTICAMENTE alla varianza totale del portafoglio, tenendo conto delle correlazioni — non ha
/// soluzione analitica e richiede un solutore non lineare iterativo. L'inverse-volatility è
/// l'approssimazione standard ampiamente usata in pratica (equivale all'ERC esatto quando le
/// correlazioni fra asset sono uniformi); qui si è scelta la versione robusta e verificabile
/// piuttosto che un solutore numerico "fatto a mano" e potenzialmente instabile.
/// </summary>
public sealed class RiskParityOptimizer : IPortfolioOptimizer
{
    public string Name => "RiskParity";

    public PortfolioAllocation Optimize(IReadOnlyDictionary<string, IReadOnlyList<decimal>> returnsBySymbol, PortfolioOptimizationConfig? config = null)
    {
        config ??= new PortfolioOptimizationConfig();
        var (symbols, returns) = PortfolioMath.BuildMatrix(returnsBySymbol);
        var stdDev = PortfolioMath.StdDev(returns);

        var scores = stdDev.Select(sigma => sigma > 1e-12 ? 1.0 / sigma : 0.0).ToList();
        var weights = PortfolioMath.ToConstrainedWeights(symbols, scores, config.MinWeight, config.MaxWeight);
        return new PortfolioAllocation { Weights = weights };
    }
}
