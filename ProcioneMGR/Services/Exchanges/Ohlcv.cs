namespace ProcioneMGR.Services.Exchanges;

/// <summary>
/// Candela OHLCV "di trasporto", restituita dai client exchange. Disaccoppiata
/// dall'entita' di persistenza <see cref="Data.OhlcvData"/>: il layer di ingestione
/// mappa da questo DTO all'entita'.
/// </summary>
public readonly record struct Ohlcv(
    DateTime TimestampUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume)
{
    /// <summary>Timestamp di apertura in millisecondi Unix (UTC).</summary>
    public long TimestampMs => new DateTimeOffset(TimestampUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
}
