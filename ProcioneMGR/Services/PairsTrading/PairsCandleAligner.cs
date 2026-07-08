using ProcioneMGR.Data;

namespace ProcioneMGR.Services.PairsTrading;

/// <summary>
/// Allinea due serie di candele per timestamp (intersezione): due simboli possono avere gap
/// diversi (manutenzione exchange, listing in date diverse, ecc.), quindi non si può assumere
/// che siano già sincronizzate indice-per-indice come nel motore single-symbol.
/// </summary>
public static class PairsCandleAligner
{
    public static (IReadOnlyList<OhlcvData> Y, IReadOnlyList<OhlcvData> X) Align(
        IReadOnlyList<OhlcvData> candlesY, IReadOnlyList<OhlcvData> candlesX)
    {
        ArgumentNullException.ThrowIfNull(candlesY);
        ArgumentNullException.ThrowIfNull(candlesX);

        var mapY = BuildTimestampMap(candlesY, nameof(candlesY));
        var mapX = BuildTimestampMap(candlesX, nameof(candlesX));
        var common = mapY.Keys.Intersect(mapX.Keys).OrderBy(t => t).ToList();

        var alignedY = new List<OhlcvData>(common.Count);
        var alignedX = new List<OhlcvData>(common.Count);
        foreach (var t in common)
        {
            alignedY.Add(mapY[t]);
            alignedX.Add(mapX[t]);
        }
        return (alignedY, alignedX);
    }

    /// <summary>
    /// Costruisce la mappa timestamp->candela con un errore chiaro in caso di timestamp duplicati,
    /// invece del generico <see cref="ArgumentException"/> di <c>ToDictionary</c>: l'unico
    /// percorso reale (OHLCV da DB, vincolato da un indice univoco Symbol+Timeframe+TimestampUtc)
    /// non può produrne, ma il metodo è pubblico e può ricevere dati costruiti a mano nei test.
    /// </summary>
    private static Dictionary<DateTime, OhlcvData> BuildTimestampMap(IReadOnlyList<OhlcvData> candles, string paramName)
    {
        var map = new Dictionary<DateTime, OhlcvData>(candles.Count);
        foreach (var c in candles)
        {
            if (!map.TryAdd(c.TimestampUtc, c))
            {
                throw new ArgumentException($"Timestamp duplicato {c.TimestampUtc:O} nella serie di candele.", paramName);
            }
        }
        return map;
    }
}
