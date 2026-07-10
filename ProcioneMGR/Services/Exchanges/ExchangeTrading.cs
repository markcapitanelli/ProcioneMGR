using System.Security.Cryptography;
using System.Text;

namespace ProcioneMGR.Services.Exchanges;

/// <summary>Credenziali per le chiamate firmate (private) all'exchange.</summary>
public readonly record struct TradingCredentials(string ApiKey, string ApiSecret, string? Passphrase, bool IsTestnet);

public class PlaceOrderRequest
{
    public string Symbol { get; set; } = string.Empty;   // canonico "BTC/USDT"
    public string Side { get; set; } = "BUY";            // "BUY" | "SELL"
    public string Type { get; set; } = "MARKET";         // "MARKET" | "LIMIT"
    public decimal Quantity { get; set; }
    public decimal? Price { get; set; }                  // per i LIMIT

    /// <summary>
    /// [P0-5] Prezzo di attivazione per gli ordini TRIGGER (stop-market / take-profit-market) piazzati
    /// via <see cref="IFuturesExchangeClient.PlaceFuturesTriggerOrderAsync"/>. Null per MARKET/LIMIT.
    /// </summary>
    public decimal? TriggerPrice { get; set; }

    /// <summary>Id idempotente lato client (newClientOrderId / clientOid).</summary>
    public string ClientOrderId { get; set; } = string.Empty;

    public TradingCredentials Credentials { get; set; }
}

public class PlaceOrderResult
{
    public bool Success { get; set; }
    public string? ExchangeOrderId { get; set; }
    public string Status { get; set; } = string.Empty;   // stato lato exchange
    public decimal? FilledPrice { get; set; }
    public decimal? FilledQuantity { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// True se la chiamata HTTP è fallita (timeout/5xx) e NON sappiamo se l'ordine sia stato
    /// piazzato: il chiamante DEVE riconciliare con GetOpenOrdersAsync prima di ritentare.
    /// </summary>
    public bool NetworkUncertain { get; set; }
}

public class CancelOrderResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class OpenOrder
{
    public string ExchangeOrderId { get; set; } = string.Empty;
    public string ClientOrderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal? Price { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class AccountBalance
{
    public Dictionary<string, decimal> Free { get; set; } = new();
    public Dictionary<string, decimal> Locked { get; set; } = new();
}

/// <summary>
/// Filtri di trading di un simbolo (da exchangeInfo): passo lotto/prezzo e minimi.
/// Servono a formattare la quantità in modo che l'exchange non rifiuti l'ordine.
/// </summary>
public class SymbolFilters
{
    public decimal StepSize { get; set; }      // LOT_SIZE: incremento valido della quantità
    public decimal MinQty { get; set; }
    public decimal TickSize { get; set; }      // PRICE_FILTER: incremento valido del prezzo
    public decimal MinNotional { get; set; }   // valore minimo dell'ordine

    /// <summary>Arrotonda la quantità per DIFETTO al multiplo di StepSize valido.</summary>
    public decimal RoundQuantity(decimal qty)
        => StepSize > 0m ? Math.Floor(qty / StepSize) * StepSize : qty;

    /// <summary>Arrotonda il prezzo al multiplo di TickSize valido.</summary>
    public decimal RoundPrice(decimal price)
        => TickSize > 0m ? Math.Floor(price / TickSize) * TickSize : price;

    /// <summary>True se l'ordine rispetta minQty e minNotional.</summary>
    public bool IsTradable(decimal qty, decimal price)
        => qty >= MinQty && qty * price >= MinNotional;
}

/// <summary>Firme HMAC per le richieste autenticate. Funzioni pure, testabili.</summary>
public static class ExchangeSigning
{
    /// <summary>HMAC-SHA256 esadecimale minuscolo (Binance).</summary>
    public static string HmacSha256Hex(string message, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>HMAC-SHA256 in base64 (Bitget).</summary>
    public static string HmacSha256Base64(string message, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
    }

    public static long UnixMillis(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
}
