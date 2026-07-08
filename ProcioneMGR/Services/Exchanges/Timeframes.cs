namespace ProcioneMGR.Services.Exchanges;

/// <summary>
/// Timeframe canonici dell'applicazione e relativa durata. Ogni client exchange
/// traduce questi valori nel proprio dialetto (es. Bitget "1day" per "1d").
/// </summary>
public static class Timeframes
{
    /// <summary>Mappa timeframe canonico -> durata.</summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> Supported = new Dictionary<string, TimeSpan>
    {
        ["1m"] = TimeSpan.FromMinutes(1),
        ["5m"] = TimeSpan.FromMinutes(5),
        ["15m"] = TimeSpan.FromMinutes(15),
        ["30m"] = TimeSpan.FromMinutes(30),
        ["1h"] = TimeSpan.FromHours(1),
        ["4h"] = TimeSpan.FromHours(4),
        ["1d"] = TimeSpan.FromDays(1),
    };

    public static bool IsSupported(string timeframe) => Supported.ContainsKey(timeframe);

    /// <summary>Durata del timeframe in millisecondi. Lancia se non supportato.</summary>
    public static long ToMilliseconds(string timeframe)
    {
        if (!Supported.TryGetValue(timeframe, out var span))
        {
            throw new ArgumentException($"Timeframe non supportato: '{timeframe}'.", nameof(timeframe));
        }
        return (long)span.TotalMilliseconds;
    }
}
