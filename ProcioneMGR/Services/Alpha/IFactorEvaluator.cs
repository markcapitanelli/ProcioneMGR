using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Alpha;

/// <summary>
/// Valuta la capacità predittiva di un fattore alpha rispetto ai rendimenti futuri, senza
/// look-ahead nella COSTRUZIONE del fattore (il rendimento forward è il <i>target</i>, e come
/// tale può guardare avanti: è ciò che vogliamo predire, non un input del fattore).
/// Controparte C# di Alphalens.
/// </summary>
public interface IFactorEvaluator
{
    /// <summary>
    /// Calcola i rendimenti forward su <paramref name="horizon"/> candele:
    /// <c>fwd[i] = (close[i+horizon] - close[i]) / close[i]</c>. Gli ultimi <c>horizon</c>
    /// elementi sono <c>null</c> (nessun futuro disponibile).
    /// </summary>
    IReadOnlyList<decimal?> ForwardReturns(IReadOnlyList<OhlcvData> candles, int horizon);

    /// <summary>Valuta il fattore dato lo storico di candele e la configurazione.</summary>
    FactorEvaluationResult Evaluate(
        IAlphaFactor factor,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        FactorEvaluationConfig config);
}
