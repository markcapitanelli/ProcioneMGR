using System.Globalization;
using System.Text.Json;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.MarketData;

/// <summary>
/// Dialetto degli stream pubblici Binance (combined streams).
///
/// Due canali per sottoscrizione:
///  - <c>{sym}@bookTicker</c>: best bid/ask ad ogni variazione — la fonte dei tick per le uscite protettive;
///  - <c>{sym}@kline_{tf}</c>: candele, di cui si usa SOLO quella con <c>k.x == true</c> (chiusa).
///
/// Le candele NON chiuse vengono scartate di proposito: una candela in formazione ha High/Low
/// provvisori, e darla in pasto alle strategie significherebbe valutare segnali su una barra che
/// può ancora cambiare — l'esatto contrario di ciò che il backtest valida.
/// </summary>
public sealed class BinanceStreamMapper : IExchangeStreamMapper
{
    private const string SpotBase = "wss://stream.binance.com:9443/stream?streams=";
    private const string FuturesBase = "wss://fstream.binance.com/stream?streams=";

    public ExchangeName Exchange => ExchangeName.Binance;

    /// <summary>Binance manda frame di ping di protocollo: <c>ClientWebSocket</c> risponde da solo.</summary>
    public string? HeartbeatFrame => null;

    public TimeSpan HeartbeatInterval => Timeout.InfiniteTimeSpan;

    /// <summary>"BTC/USDT" -> "btcusdt" (gli stream vogliono il simbolo minuscolo).</summary>
    public static string ToStreamSymbol(string canonical) =>
        canonical.Replace("/", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    public Uri BuildEndpoint(IReadOnlyCollection<StreamSubscription> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        if (subscriptions.Count == 0)
        {
            throw new ArgumentException("Nessuna sottoscrizione: non c'è endpoint da comporre.", nameof(subscriptions));
        }

        // Un endpoint per tipo di mercato: spot e futures vivono su domini completamente separati,
        // esattamente come nel client REST.
        var isFutures = subscriptions.First().MarketType == MarketType.Futures;
        if (subscriptions.Any(s => (s.MarketType == MarketType.Futures) != isFutures))
        {
            throw new ArgumentException(
                "Sottoscrizioni spot e futures non possono condividere una connessione Binance: domini diversi.",
                nameof(subscriptions));
        }

        var streams = subscriptions
            .SelectMany(s =>
            {
                var sym = ToStreamSymbol(s.Symbol);
                return new[] { $"{sym}@bookTicker", $"{sym}@kline_{s.Timeframe}" };
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal);

        return new Uri((isFutures ? FuturesBase : SpotBase) + string.Join('/', streams));
    }

    /// <summary>Binance codifica tutto nell'URL: nessun frame di sottoscrizione da mandare.</summary>
    public IReadOnlyList<string> BuildSubscribeFrames(IReadOnlyCollection<StreamSubscription> subscriptions) => [];

    public StreamEvent Parse(string raw, IReadOnlyDictionary<string, StreamSubscription> byExchangeSymbol)
    {
        ArgumentNullException.ThrowIfNull(byExchangeSymbol);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return StreamEvent.None;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // La radice DEVE essere un oggetto: su un array (es. una risposta a un comando di
            // sottoscrizione) TryGetProperty non ritorna false, LANCIA InvalidOperationException —
            // che non è una JsonException e quindi sfuggirebbe al catch sotto, abbattendo la
            // connessione per un frame irrilevante.
            if (root.ValueKind != JsonValueKind.Object)
            {
                return StreamEvent.None;
            }

            // Combined stream: il payload utile è sotto "data"; il formato singolo (usato da alcuni
            // test e dagli endpoint /ws/) arriva già srotolato.
            var data = root.TryGetProperty("data", out var wrapped) ? wrapped : root;
            if (data.ValueKind != JsonValueKind.Object)
            {
                return StreamEvent.None;
            }

            if (!data.TryGetProperty("s", out var symbolEl) || symbolEl.GetString() is not string exchangeSymbol)
            {
                return StreamEvent.None; // ack di sottoscrizione, risposta a un id, ecc.
            }

            // Il simbolo canonico non è ricostruibile dal simbolo dell'exchange ("BTCUSDT" non dice
            // dove sta la barra): si risale dalla mappa delle sottoscrizioni attive.
            if (!byExchangeSymbol.TryGetValue(exchangeSymbol.ToUpperInvariant(), out var sub))
            {
                return StreamEvent.None;
            }

            if (data.TryGetProperty("k", out var kline))
            {
                return ParseKline(kline, sub);
            }

            if (data.TryGetProperty("b", out var bidEl) && data.TryGetProperty("a", out var askEl))
            {
                return ParseBookTicker(bidEl, askEl, sub);
            }

            return StreamEvent.None;
        }
        catch (JsonException)
        {
            return StreamEvent.None; // frame malformato: si ignora, la connessione resta viva
        }
    }

    private StreamEvent ParseBookTicker(JsonElement bidEl, JsonElement askEl, StreamSubscription sub)
    {
        if (!TryDecimal(bidEl, out var bid) || !TryDecimal(askEl, out var ask))
        {
            return StreamEvent.None;
        }

        // bookTicker non porta un timestamp di evento: si usa l'ora di ricezione, che è comunque
        // ciò che conta per decidere "adesso".
        return StreamEvent.FromTick(new PriceTick(Exchange, sub.Symbol, bid, ask, DateTime.UtcNow));
    }

    private StreamEvent ParseKline(JsonElement k, StreamSubscription sub)
    {
        // Stessa cautela della radice: TryGetProperty su un non-oggetto LANCIA.
        if (k.ValueKind != JsonValueKind.Object)
        {
            return StreamEvent.None;
        }

        // SOLO candele chiuse.
        if (!k.TryGetProperty("x", out var closedEl) || closedEl.ValueKind != JsonValueKind.True)
        {
            return StreamEvent.None;
        }

        if (!k.TryGetProperty("t", out var openTimeEl) || !openTimeEl.TryGetInt64(out var openMs))
        {
            return StreamEvent.None;
        }

        if (!TryDecimal(k, "o", out var open) || !TryDecimal(k, "h", out var high)
            || !TryDecimal(k, "l", out var low) || !TryDecimal(k, "c", out var close)
            || !TryDecimal(k, "v", out var volume))
        {
            return StreamEvent.None;
        }

        // L'intervallo dichiarato dallo stream vince su quello della sottoscrizione: se per qualsiasi
        // motivo arrivasse una candela di un altro timeframe, va etichettata per quello che è, mai
        // scritta sotto il timeframe sbagliato.
        var timeframe = k.TryGetProperty("i", out var iEl) ? iEl.GetString() ?? sub.Timeframe : sub.Timeframe;

        return StreamEvent.FromBar(new BarClosed(
            Exchange, sub.Symbol, timeframe,
            DateTimeOffset.FromUnixTimeMilliseconds(openMs).UtcDateTime,
            open, high, low, close, volume));
    }

    private static bool TryDecimal(JsonElement parent, string name, out decimal value)
    {
        value = 0m;
        return parent.TryGetProperty(name, out var el) && TryDecimal(el, out value);
    }

    /// <summary>Binance serializza i numeri come stringhe ("0.00021"); si accetta comunque il numerico.</summary>
    private static bool TryDecimal(JsonElement el, out decimal value)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return decimal.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            case JsonValueKind.Number:
                return el.TryGetDecimal(out value);
            default:
                value = 0m;
                return false;
        }
    }
}
