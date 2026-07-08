using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Backtesting;

public interface IBacktestEngine
{
    /// <summary>Carica le candele dal DB per il range in <paramref name="config"/> ed esegue il backtest.</summary>
    Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, CancellationToken ct);

    /// <summary>
    /// Esegue il backtest su candele gia' caricate (caching per l'ottimizzatore).
    /// Le candele devono coprire il range desiderato ed essere ordinate cronologicamente.
    /// </summary>
    Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, CancellationToken ct);

    /// <summary>
    /// Esegue il backtest con un'istanza di <see cref="IStrategy"/> già pronta invece di crearla
    /// per nome dalla factory. Punto di aggancio per strategie che richiedono uno stato
    /// costruito esternamente (es. <c>MlStrategy</c> con un <c>IReturnPredictor</c> già
    /// addestrato) — stesso motore, stessa pipeline, nessuna duplicazione.
    /// </summary>
    Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, IStrategy strategy, CancellationToken ct);
}
