namespace ProcioneMGR.Services.Portfolio;

/// <summary>Obiettivo dell'ottimizzazione Mean-Variance (Markowitz).</summary>
public enum MeanVarianceObjective
{
    /// <summary>Portafoglio tangente: massimizza lo Sharpe ratio.</summary>
    MaxSharpe,
    /// <summary>Portafoglio a varianza minima globale (non richiede stime di rendimento atteso, solo covarianza).</summary>
    MinVariance,
}

public sealed class PortfolioOptimizationConfig
{
    public decimal RiskFreeRateAnnual { get; set; } = 0.02m;
    public int PeriodsPerYear { get; set; } = 365;

    /// <summary>Peso minimo per asset (frazione, es. 0.05 = 5%). Long-only: 0 di default.</summary>
    public decimal MinWeight { get; set; } = 0m;

    /// <summary>Peso massimo per asset (frazione, es. 0.40 = 40%).</summary>
    public decimal MaxWeight { get; set; } = 1m;

    public MeanVarianceObjective Objective { get; set; } = MeanVarianceObjective.MaxSharpe;
}

public sealed class PortfolioAllocation
{
    /// <summary>Pesi per simbolo, frazioni che sommano a 1 (100%).</summary>
    public required IReadOnlyDictionary<string, decimal> Weights { get; init; }
}

/// <summary>
/// Allocatore di portafoglio (cap. 5): dati i rendimenti storici di un paniere di simboli,
/// calcola i pesi. Le implementazioni si affiancano a <c>EnsembleAllocator</c> (che pesa
/// STRATEGIE in base allo Sharpe rolling) come strategie di pesatura alternative per un
/// paniere di ASSET.
/// </summary>
public interface IPortfolioOptimizer
{
    string Name { get; }

    /// <param name="returnsBySymbol">Rendimenti periodici per simbolo, tutti della STESSA lunghezza e allineati per indice temporale.</param>
    PortfolioAllocation Optimize(IReadOnlyDictionary<string, IReadOnlyList<decimal>> returnsBySymbol, PortfolioOptimizationConfig? config = null);
}
