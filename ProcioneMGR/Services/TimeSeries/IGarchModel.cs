namespace ProcioneMGR.Services.TimeSeries;

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
    GarchFit Fit(IReadOnlyList<decimal> returns);
}
