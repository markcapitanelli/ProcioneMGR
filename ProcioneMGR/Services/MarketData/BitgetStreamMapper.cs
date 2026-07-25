using System.Globalization;
using System.Text.Json;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.MarketData;

/// <summary>
/// Dialetto degli stream pubblici Bitget v2.
///
/// SOLO IL CANALE <c>ticker</c>, deliberatamente. Il canale delle candele di Bitget non espone un
/// flag "barra chiusa" come il <c>k.x</c> di Binance: pubblica ripetutamente la candela IN CORSO, e
/// per dedurne la chiusura bisognerebbe aspettare la comparsa di quella successiva e considerare
/// chiusa la precedente. È un'inferenza fragile — un buco di connessione o un riordino la fanno
/// sbagliare — e il premio è piccolo: anticipare di qualche minuto un INGRESSO. Il valore vero di
/// questo feed sono le USCITE protettive, che vivono sui tick e qui ci sono tutte.
/// Le candele Bitget continuano quindi ad arrivare dal ciclo REST già esistente, invariato.
///
/// NB sul Demo Trading: i dati di mercato Bitget sono PUBBLICI e condivisi fra ambiente reale e
/// demo (stessa lezione già appresa in <c>BitgetClient.PublicMarketProductType</c>), quindi il
/// productType demo "S..." non va mai usato qui — su questi canali non restituirebbe nulla.
/// </summary>
public sealed class BitgetStreamMapper : IExchangeStreamMapper
{
    private static readonly Uri PublicEndpoint = new("wss://ws.bitget.com/v2/ws/public");

    public ExchangeName Exchange => ExchangeName.Bitget;

    /// <summary>Bitget richiede un ping APPLICATIVO: senza, chiude la connessione dopo ~30s di silenzio.</summary>
    public string? HeartbeatFrame => "ping";

    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(25);

    /// <summary>"BTC/USDT" -> "BTCUSDT".</summary>
    public static string ToStreamSymbol(string canonical) =>
        canonical.Replace("/", string.Empty).Replace("-", string.Empty).ToUpperInvariant();

    /// <summary>Sempre il productType PUBBLICO, mai quello demo: vedi la nota di classe.</summary>
    private static string InstType(MarketType marketType) =>
        marketType == MarketType.Futures ? "USDT-FUTURES" : "SPOT";

    public Uri BuildEndpoint(IReadOnlyCollection<StreamSubscription> subscriptions) => PublicEndpoint;

    public IReadOnlyList<string> BuildSubscribeFrames(IReadOnlyCollection<StreamSubscription> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        if (subscriptions.Count == 0)
        {
            return [];
        }

        var args = subscriptions
            .Select(s => new { instType = InstType(s.MarketType), channel = "ticker", instId = ToStreamSymbol(s.Symbol) })
            .DistinctBy(a => (a.instType, a.instId))
            .ToList();

        return [JsonSerializer.Serialize(new { op = "subscribe", args })];
    }

    public StreamEvent Parse(string raw, IReadOnlyDictionary<string, StreamSubscription> byExchangeSymbol)
    {
        ArgumentNullException.ThrowIfNull(byExchangeSymbol);
        if (string.IsNullOrWhiteSpace(raw) || raw == "pong")
        {
            return StreamEvent.None;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // Ack di sottoscrizione ed errori non portano dati: si ignorano (l'errore, se c'è, si
            // manifesta come assenza di tick, che la watchdog di staleness rileva già).
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return StreamEvent.None;
            }

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("instId", out var instEl) || instEl.GetString() is not string instId) continue;
                if (!byExchangeSymbol.TryGetValue(instId.ToUpperInvariant(), out var sub)) continue;
                if (!TryDecimal(entry, "bidPr", out var bid) || !TryDecimal(entry, "askPr", out var ask)) continue;

                var ts = TryLong(entry, "ts", out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                    : DateTime.UtcNow;

                // Un solo tick per messaggio: il canale ticker pubblica un simbolo alla volta, e
                // restituire il primo utile tiene il contratto del parser semplice.
                return StreamEvent.FromTick(new PriceTick(Exchange, sub.Symbol, bid, ask, ts));
            }

            return StreamEvent.None;
        }
        catch (JsonException)
        {
            return StreamEvent.None;
        }
    }

    private static bool TryDecimal(JsonElement parent, string name, out decimal value)
    {
        value = 0m;
        if (!parent.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            JsonValueKind.Number => el.TryGetDecimal(out value),
            _ => false,
        };
    }

    private static bool TryLong(JsonElement parent, string name, out long value)
    {
        value = 0L;
        if (!parent.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.String => long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            JsonValueKind.Number => el.TryGetInt64(out value),
            _ => false,
        };
    }
}
