namespace ProcioneMGR.Services.Portfolio;

/// <summary>Obiettivo dell'ottimizzazione Mean-Variance (Markowitz).</summary>
public enum MeanVarianceObjective
{
    /// <summary>Portafoglio tangente: massimizza lo Sharpe ratio.</summary>
    MaxSharpe,
    /// <summary>Portafoglio a varianza minima globale (non richiede stime di rendimento atteso, solo covarianza).</summary>
    MinVariance,
}

/// <summary>Stimatore della matrice di covarianza usato dagli allocatori.</summary>
public enum CovarianceEstimator
{
    /// <summary>Covarianza campionaria (con ridge minimo per stabilizzare l'inversa).</summary>
    Sample,
    /// <summary>Shrinkage di Ledoit-Wolf verso μI — ben condizionata anche con pochi dati o asset correlati.</summary>
    LedoitWolf,
}

/// <summary>Metodo di Risk Parity.</summary>
public enum RiskParityMethod
{
    /// <summary>Inverse-volatility w_i ∝ 1/σ_i (ERC esatto solo a correlazioni uniformi).</summary>
    InverseVolatility,
    /// <summary>Equal Risk Contribution ESATTO (tiene conto delle correlazioni).</summary>
    EqualRiskContribution,
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

    /// <summary>Stimatore di covarianza per Mean-Variance (default Ledoit-Wolf, meglio condizionato).</summary>
    public CovarianceEstimator CovarianceEstimator { get; set; } = CovarianceEstimator.LedoitWolf;

    /// <summary>Metodo di Risk Parity (default ERC esatto).</summary>
    public RiskParityMethod RiskParityMethod { get; set; } = RiskParityMethod.EqualRiskContribution;
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
