namespace ProcioneMGR.Services.TimeSeries;

/// <summary>
/// Distribuzione delle innovazioni standardizzate zₜ = εₜ/σₜ nel GARCH.
/// </summary>
public enum GarchInnovation
{
    /// <summary>zₜ ~ Normal(0,1). Semplice, ma sottostima la probabilità delle mosse estreme.</summary>
    Gaussian,

    /// <summary>
    /// zₜ ~ Student-t standardizzata (varianza 1), con gradi di libertà ν stimati per MLE. Cattura
    /// le CODE GRASSE tipiche delle cripto: a parità di σ prevista, i quantili estremi (VaR, distanza
    /// di stop) sono più ampi che sotto la normale → sizing e stop più prudenti. Rif. audit 2026-07 §4.
    /// </summary>
    StudentT,
}

/// <summary>
/// GARCH(1,1) (cap. 9): modella la volatilità come processo essa stessa autoregressivo —
/// σ²ₜ = ω + α·ε²ₜ₋₁ + β·σ²ₜ₋₁ — catturando il "volatility clustering" tipico dei mercati
/// finanziari (periodi di calma e periodi turbolenti si susseguono a grappoli). Usato per il
/// position sizing dinamico e gli stop adattivi: quando la volatilità prevista sale, si riduce
/// l'esposizione, e viceversa.
/// </summary>
public interface IGarchModel
{
    /// <param name="returns">Rendimenti periodici (non i prezzi). Servono almeno ~30 osservazioni.</param>
    /// <param name="innovation">
    /// Distribuzione delle innovazioni: <see cref="GarchInnovation.Gaussian"/> (default, retro-compatibile)
    /// o <see cref="GarchInnovation.StudentT"/> per stimare anche i gradi di libertà ν e ottenere quantili
    /// di coda realistici (sizing/stop consapevoli delle code grasse).
    /// </param>
    GarchFit Fit(IReadOnlyList<decimal> returns, GarchInnovation innovation = GarchInnovation.Gaussian);
}
