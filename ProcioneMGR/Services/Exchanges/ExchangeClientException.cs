using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Exchanges;

/// <summary>Errore restituito da un exchange (HTTP non-2xx o payload d'errore).</summary>
public sealed class ExchangeClientException : Exception
{
    public ExchangeName Exchange { get; }
    public int StatusCode { get; }

    public ExchangeClientException(ExchangeName exchange, int statusCode, string body)
        : base($"{exchange} ha risposto {statusCode}: {Truncate(body)}")
    {
        Exchange = exchange;
        StatusCode = statusCode;
    }

    private static string Truncate(string body) =>
        string.IsNullOrEmpty(body) ? "(corpo vuoto)" : body.Length <= 300 ? body : body[..300] + "…";
}
