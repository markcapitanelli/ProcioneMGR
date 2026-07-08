using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Exchanges;

/// <summary>
/// Factory che risolve il client corretto dal DI. I client sono registrati come
/// typed HttpClient (vedi Program.cs), quindi otteniamo istanze gia' configurate
/// con base address e resilienza.
///
/// Per aggiungere un exchange: implementa <see cref="IExchangeClient"/> e
/// <see cref="IFuturesExchangeClient"/>, registralo in DI e aggiungi un case qui e in
/// <see cref="CreateFutures(ExchangeName)"/>. Nessun'altra parte del sistema cambia.
/// </summary>
public sealed class ExchangeClientFactory(IServiceProvider services) : IExchangeClientFactory
{
    public IExchangeClient Create(ExchangeName exchange) => exchange switch
    {
        ExchangeName.Binance => services.GetRequiredService<BinanceClient>(),
        ExchangeName.Bitget => services.GetRequiredService<BitgetClient>(),
        _ => throw new NotSupportedException($"Exchange non supportato: {exchange}."),
    };

    public IExchangeClient Create(string exchangeName) => Create(ParseOrThrow(exchangeName));

    public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => exchange switch
    {
        ExchangeName.Binance => services.GetRequiredService<BinanceClient>(),
        ExchangeName.Bitget => services.GetRequiredService<BitgetClient>(),
        _ => throw new NotSupportedException($"Exchange non supportato: {exchange}."),
    };

    public IFuturesExchangeClient CreateFutures(string exchangeName) => CreateFutures(ParseOrThrow(exchangeName));

    private static ExchangeName ParseOrThrow(string exchangeName)
    {
        if (!Enum.TryParse<ExchangeName>(exchangeName, ignoreCase: true, out var parsed))
        {
            throw new NotSupportedException($"Exchange non riconosciuto: '{exchangeName}'.");
        }
        return parsed;
    }
}
