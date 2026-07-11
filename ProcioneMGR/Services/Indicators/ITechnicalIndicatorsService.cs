namespace ProcioneMGR.Services.Indicators;

/// <summary>
/// Calcolo di indicatori tecnici lato server. Stateless -> registrato come Singleton.
///
/// NOTA SULLA FIRMA: lo spec chiedeva <c>List&lt;decimal&gt;</c> con null/NaN per i valori
/// non calcolabili, ma <c>decimal</c> e' un value type non-nullable e non ha NaN. Si usa
/// quindi <c>List&lt;decimal?&gt;</c>: i primi elementi (warm-up dell'indicatore) sono
/// <c>null</c> e la lista risultante ha SEMPRE la stessa lunghezza dell'input, cosi' resta
/// allineata per indice alla serie dei prezzi (necessario per sovrapporre gli indicatori).
/// </summary>
public interface ITechnicalIndicatorsService
{
    /// <summary>EMA (Exponential Moving Average) con seed = SMA dei primi <paramref name="period"/> valori.</summary>
    Task<List<decimal?>> CalculateEmaAsync(List<decimal> values, int period, CancellationToken ct = default);

    /// <summary>RSI (Relative Strength Index) con smoothing di Wilder.</summary>
    Task<List<decimal?>> CalculateRsiAsync(List<decimal> closes, int period = 14, CancellationToken ct = default);

    /// <summary>
    /// Variante SINCRONA di <see cref="CalculateRsiAsync"/>: il calcolo è CPU-bound e l'async è
    /// solo un Task.FromResult — i chiamanti sincroni prima ripiegavano su
    /// .GetAwaiter().GetResult() (sync-over-async inutile).
    /// </summary>
    List<decimal?> CalculateRsi(List<decimal> closes, int period = 14, CancellationToken ct = default);

    /// <summary>MACD: linea = EMA(fast) - EMA(slow); signal = EMA(MACD, signalPeriod); histogram = MACD - signal.</summary>
    Task<(List<decimal?> Macd, List<decimal?> Signal, List<decimal?> Histogram)> CalculateMacdAsync(
        List<decimal> closes, int fastPeriod = 12, int slowPeriod = 26, int signalPeriod = 9, CancellationToken ct = default);

    /// <summary>Bollinger Bands: middle = SMA(period); upper/lower = middle +/- mult * deviazione standard (popolazione).</summary>
    Task<(List<decimal?> Upper, List<decimal?> Middle, List<decimal?> Lower)> CalculateBollingerAsync(
        List<decimal> closes, int period = 20, decimal stdDevMultiplier = 2.0m, CancellationToken ct = default);

    /// <summary>SMA (Simple Moving Average) a finestra scorrevole.</summary>
    Task<List<decimal?>> CalculateSmaAsync(List<decimal> values, int period, CancellationToken ct = default);

    /// <summary>
    /// Donchian Channel: Upper = massimo degli ultimi <paramref name="period"/> massimi (HHV),
    /// Lower = minimo degli ultimi <paramref name="period"/> minimi (LLV), finestra inclusiva
    /// della barra corrente (le strategie di breakout confrontano con il canale della barra
    /// PRECEDENTE per evitare retroazione).
    /// </summary>
    Task<(List<decimal?> Upper, List<decimal?> Lower)> CalculateDonchianAsync(
        List<decimal> highs, List<decimal> lows, int period = 20, CancellationToken ct = default);

    /// <summary>
    /// ATR (Average True Range) con smoothing di Wilder. True Range =
    /// max(high-low, |high-closePrec|, |low-closePrec|); il primo valore utile e' all'indice
    /// <paramref name="period"/> (media dei primi <c>period</c> TR), poi smoothing di Wilder.
    /// Misura la volatilita' assoluta, usata dagli stop adattivi e da Supertrend.
    /// </summary>
    Task<List<decimal?>> CalculateAtrAsync(
        List<decimal> highs, List<decimal> lows, List<decimal> closes, int period = 14, CancellationToken ct = default);
}
