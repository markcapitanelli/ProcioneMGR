using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Exchanges;

/// <summary>Restituisce l'implementazione <see cref="IExchangeClient"/> per un dato exchange.</summary>
public interface IExchangeClientFactory
{
    IExchangeClient Create(ExchangeName exchange);

    /// <summary>Variante che accetta il nome testuale (case-insensitive), utile dalla UI/servizi.</summary>
    IExchangeClient Create(string exchangeName);

    /// <summary>Variante futures: stessa istanza concreta di <see cref="Create(ExchangeName)"/>, vista come <see cref="IFuturesExchangeClient"/>.</summary>
    IFuturesExchangeClient CreateFutures(ExchangeName exchange);

    IFuturesExchangeClient CreateFutures(string exchangeName);
}
