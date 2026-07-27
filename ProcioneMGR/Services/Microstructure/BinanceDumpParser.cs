using System.Globalization;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Microstructure;

// =============================================================================================
//  [D3] Lettura dei dump pubblici di Binance (data.binance.vision).
//
//  I tre formati che servono, con le loro trappole — tutte trovate guardando i file veri, non la
//  documentazione:
//
//  1. aggTrades SPOT: NESSUN header, timestamp in MICROsecondi, booleani "False"/"True".
//  2. aggTrades FUTURES: header presente, timestamp in MILLIsecondi, booleani "false"/"true".
//  3. bookDepth FUTURES: header, timestamp come TESTO "yyyy-MM-dd HH:mm:ss", 12 righe per
//     snapshot (bande ±0,20% e ±1…5%), uno snapshot ogni 30 secondi.
//
//  Le differenze fra 1 e 2 sono il motivo per cui il parser NON assume nulla: header rilevato
//  provandolo a parsare, unità del timestamp dedotta dall'ordine di grandezza. Un parser tarato
//  su un solo formato avrebbe letto lo spot con timestamp mille volte troppo grandi, cioè barre
//  tutte vuote tranne una — e un IC calcolato su barre vuote è zero, che si sarebbe confuso con
//  il verdetto "nessuna informazione".
//
//  RIGHE MALFORMATE: si contano e si espongono (<see cref="MalformedLines"/>), non si ignorano in
//  silenzio. Un file troncato a metà del download deve potersi accorgere di esserlo.
// =============================================================================================

/// <summary>Legge i CSV dei dump Binance. Istanza per file (tiene i contatori di quel file).</summary>
public sealed class BinanceDumpParser
{
    /// <summary>Righe che non è stato possibile interpretare (colonne mancanti, numeri non validi).</summary>
    public int MalformedLines { get; private set; }

    /// <summary>Righe interpretate correttamente.</summary>
    public int ParsedLines { get; private set; }

    /// <summary>
    /// Soglia fra millisecondi e microsecondi. Un epoch in ms dei nostri anni vale ~1,8·10¹², in µs
    /// ~1,8·10¹⁵: qualunque valore sopra 10¹⁴ è microsecondi (in ms sarebbe l'anno 5138).
    /// </summary>
    private const long MicrosecondThreshold = 100_000_000_000_000L;

    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Converte un epoch in ms o µs in UTC, decidendo l'unità dall'ordine di grandezza.</summary>
    internal static DateTime FromEpoch(long value) =>
        value >= MicrosecondThreshold
            ? Epoch.AddTicks(value * 10)          // µs → tick (1 tick = 100 ns)
            : Epoch.AddMilliseconds(value);

    /// <summary>
    /// Trade aggregati, in streaming. L'ordine è quello del file (cronologico crescente): non si
    /// riordina, così un file fuori ordine si vede a valle invece di essere mascherato qui.
    /// </summary>
    public IEnumerable<AggTrade> ReadAggTrades(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string? line;
        var first = true;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;

            var parts = line.Split(',');
            if (parts.Length < 7)
            {
                MalformedLines++;
                continue;
            }

            // Header: si riconosce dal fatto che la prima colonna non è un numero.
            if (first && !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                first = false;
                continue;
            }
            first = false;

            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var price)
                || !decimal.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var qty)
                || !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)
                || !bool.TryParse(parts[6], out var isBuyerMaker))
            {
                MalformedLines++;
                continue;
            }

            ParsedLines++;
            yield return new AggTrade(id, price, qty, FromEpoch(ts), isBuyerMaker);
        }
    }

    /// <summary>
    /// Snapshot di profondità. Le righe dello stesso istante vanno raggruppate: il file ha una riga
    /// per banda, quindi uno snapshot completo sono 12 righe consecutive con lo stesso timestamp.
    /// </summary>
    public IEnumerable<BookDepthSnapshot> ReadBookDepth(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? line;
        DateTime? currentTs = null;
        var levels = new Dictionary<decimal, decimal>();

        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;

            var parts = line.Split(',');
            if (parts.Length < 4) { MalformedLines++; continue; }

            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts)
                || !decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
                || !decimal.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var notional))
            {
                // La riga di header finisce qui, e va contata come tale (non come malformata).
                if (parts[0].Equals("timestamp", StringComparison.OrdinalIgnoreCase)) continue;
                MalformedLines++;
                continue;
            }

            if (currentTs is { } open && open != ts)
            {
                ParsedLines++;
                yield return new BookDepthSnapshot(open, new Dictionary<decimal, decimal>(levels));
                levels.Clear();
            }

            currentTs = ts;
            levels[pct] = notional;
        }

        if (currentTs is { } last && levels.Count > 0)
        {
            ParsedLines++;
            yield return new BookDepthSnapshot(last, levels);
        }
    }

    /// <summary>
    /// Klines dei dump, lette direttamente in <see cref="OhlcvData"/> — la stessa entità delle
    /// candele della piattaforma. Non è un dettaglio di comodità: significa che il proxy si calcola
    /// col <c>TakerImbalanceFactor</c> VERO, quello che gira in produzione, e non con una
    /// riscrittura per l'occasione che potrebbe differire proprio nel punto in discussione.
    ///
    /// Colonne: open_time, open, high, low, close, volume, close_time, quote_volume, count,
    /// taker_buy_volume, taker_buy_quote_volume, ignore.
    /// </summary>
    public IEnumerable<OhlcvData> ReadKlines(TextReader reader, string symbol, string timeframe)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? line;
        var first = true;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;

            var parts = line.Split(',');
            if (parts.Length < 11) { MalformedLines++; continue; }

            if (first && !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                first = false;
                continue;
            }
            first = false;

            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var openTime)
                || !decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var open)
                || !decimal.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var high)
                || !decimal.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var low)
                || !decimal.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var close)
                || !decimal.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var volume)
                || !decimal.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var quoteVolume)
                || !long.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                || !decimal.TryParse(parts[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var takerBuy))
            {
                MalformedLines++;
                continue;
            }

            ParsedLines++;
            yield return new OhlcvData
            {
                Symbol = symbol,
                Timeframe = timeframe,
                TimestampUtc = FromEpoch(openTime),
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                QuoteVolume = quoteVolume,
                TradeCount = count,
                TakerBuyVolume = takerBuy,
            };
        }
    }
}
