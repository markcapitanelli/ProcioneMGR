using System.Text.Json;

namespace ProcioneMGR.Services.MarketData;

/// <summary>Una liquidazione forzata: quando, che ticker, quale LATO è stato liquidato, quanto nozionale.</summary>
/// <param name="LongLiquidated">true = liquidato un LONG (l'exchange VENDE, side "SELL"); false = liquidato uno short.</param>
public sealed record LiquidationEvent(DateTime TimestampUtc, string BaseTicker, bool LongLiquidated, decimal Notional);

/// <summary>
/// [F4 roadmap frontiere-profitto] Parsing dello stream pubblico Binance futures
/// <c>!forceOrder@arr</c>: OGNI ordine di liquidazione del mercato, keyless. Il dato non è storico
/// — su questo stream esiste solo il presente — quindi il suo intero valore è l'ACCUMULO: fra sei
/// mesi le serie per-simbolo datano le cascate e alimentano feature di fragilità.
/// </summary>
public static class BinanceLiquidationMapper
{
    /// <summary>Stream market-wide (un solo socket per tutto il listino futures USDT-M).</summary>
    public const string StreamUri = "wss://fstream.binance.com/ws/!forceOrder@arr";

    /// <summary>
    /// Payload → evento. Null per messaggi non-forceOrder, simboli non /USDT o payload malformati
    /// (uno stream pubblico non merita mai un'eccezione che ammazza il worker).
    /// Nozionale = quantità × prezzo medio di esecuzione (fallback: prezzo ordine).
    /// </summary>
    public static LiquidationEvent? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("e", out var kind) || kind.GetString() != "forceOrder") return null;
            if (!root.TryGetProperty("o", out var order)) return null;

            var symbol = order.TryGetProperty("s", out var s) ? s.GetString() : null;
            if (symbol is null || !symbol.EndsWith("USDT", StringComparison.Ordinal) || symbol.Length <= 4) return null;

            var side = order.TryGetProperty("S", out var sd) ? sd.GetString() : null;
            if (side is not ("SELL" or "BUY")) return null;

            var qty = ParseDecimal(order, "q");
            var avgPrice = ParseDecimal(order, "ap");
            if (avgPrice <= 0m) avgPrice = ParseDecimal(order, "p");
            if (qty <= 0m || avgPrice <= 0m) return null;

            var ts = order.TryGetProperty("T", out var t) && t.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                : DateTime.UtcNow;

            return new LiquidationEvent(ts, symbol[..^4], side == "SELL", qty * avgPrice);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal ParseDecimal(JsonElement order, string prop) =>
        order.TryGetProperty(prop, out var el)
        && decimal.TryParse(el.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : 0m;
}

/// <summary>Totali di un'ora per un ticker (il "secchio" che il worker scrive a ogni flush).</summary>
public sealed record LiquidationBucket(string BaseTicker, DateTime HourUtc)
{
    public decimal LongNotional { get; set; }
    public decimal ShortNotional { get; set; }
    public int LongCount { get; set; }
    public int ShortCount { get; set; }
}

/// <summary>
/// Aggregazione per (ticker, ora UTC). Thread-safe per il pattern del worker (loop di lettura e
/// flush sullo stesso task, ma il lock costa nulla e toglie ogni dubbio). I secchi restano in
/// memoria finché <see cref="PruneBefore"/> non li ritira: il flush è idempotente perché scrive
/// il TOTALE corrente del secchio (upsert), non i delta.
/// </summary>
public sealed class LiquidationAggregator
{
    private readonly Dictionary<(string Ticker, DateTime Hour), LiquidationBucket> _buckets = new();
    private readonly object _gate = new();

    public void Add(LiquidationEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var hour = new DateTime(e.TimestampUtc.Year, e.TimestampUtc.Month, e.TimestampUtc.Day, e.TimestampUtc.Hour, 0, 0, DateTimeKind.Utc);
        lock (_gate)
        {
            if (!_buckets.TryGetValue((e.BaseTicker, hour), out var b))
            {
                b = new LiquidationBucket(e.BaseTicker, hour);
                _buckets[(e.BaseTicker, hour)] = b;
            }
            if (e.LongLiquidated) { b.LongNotional += e.Notional; b.LongCount++; }
            else { b.ShortNotional += e.Notional; b.ShortCount++; }
        }
    }

    /// <summary>Fotografia dei secchi correnti (copie: il chiamante può scriverle senza lock).</summary>
    public IReadOnlyList<LiquidationBucket> Snapshot()
    {
        lock (_gate)
        {
            return _buckets.Values
                .Select(b => new LiquidationBucket(b.BaseTicker, b.HourUtc)
                {
                    LongNotional = b.LongNotional, ShortNotional = b.ShortNotional,
                    LongCount = b.LongCount, ShortCount = b.ShortCount,
                })
                .OrderBy(b => b.HourUtc).ThenBy(b => b.BaseTicker)
                .ToList();
        }
    }

    /// <summary>Ritira i secchi delle ore ormai chiuse e già scritte (il flush li ha resi definitivi).</summary>
    public void PruneBefore(DateTime cutoffUtc)
    {
        lock (_gate)
        {
            foreach (var key in _buckets.Keys.Where(k => k.Hour < cutoffUtc).ToList())
            {
                _buckets.Remove(key);
            }
        }
    }
}
