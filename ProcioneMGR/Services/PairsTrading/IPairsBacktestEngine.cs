using ProcioneMGR.Data;

namespace ProcioneMGR.Services.PairsTrading;

/// <summary>
/// Motore di backtest DEDICATO al pairs trading (cap. 9): a differenza di
/// <c>Services.Backtesting.IBacktestEngine</c> (single-symbol per progettazione), opera su DUE
/// serie di candele contemporaneamente. Scelta architetturale deliberata: estendere il motore
/// single-symbol esistente per gestire un numero variabile di simboli avrebbe richiesto toccare
/// <c>IStrategy</c>, tutte le strategie esistenti e i chiamanti (Optimization/Discovery/
/// Ensemble) — qui invece è un sotto-sistema parallelo e indipendente, zero rischio di
/// regressione sul motore esistente.
/// </summary>
public interface IPairsBacktestEngine
{
    /// <param name="candlesY">Candele del primo simbolo (non serve siano pre-allineate a X: l'engine allinea per timestamp).</param>
    /// <param name="candlesX">Candele del secondo simbolo.</param>
    PairsBacktestResult RunBacktest(IReadOnlyList<OhlcvData> candlesY, IReadOnlyList<OhlcvData> candlesX, PairsBacktestConfiguration config);
}
