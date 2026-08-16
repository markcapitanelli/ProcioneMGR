using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Exchanges;

/// <summary>
/// Astrazione di un exchange (Strategy Pattern). Aggiungere un nuovo exchange
/// significa implementare questa interfaccia e registrarla nella
/// <see cref="ExchangeClientFactory"/>, senza toccare il codice esistente.
///
/// I simboli usano la forma canonica "BASE/QUOTE" (es. "BTC/USDT"); ogni client
/// converte internamente nel formato dell'exchange.
/// </summary>
public interface IExchangeClient
{
    /// <summary>Exchange gestito da questa implementazione.</summary>
    ExchangeName Exchange { get; }

    /// <summary>Numero massimo di candele restituibili in una singola richiesta (rate-limit).</summary>
    int MaxCandlesPerRequest { get; }

    /// <summary>
    /// Scarica candele OHLCV pubbliche.
    /// </summary>
    /// <param name="symbol">Simbolo canonico, es. "BTC/USDT".</param>
    /// <param name="timeframe">Timeframe canonico, es. "1h".</param>
    /// <param name="since">Timestamp di partenza in millisecondi Unix (UTC).</param>
    /// <param name="limit">Numero massimo di candele desiderate (verra' limitato a <see cref="MaxCandlesPerRequest"/>).</param>
    Task<List<Ohlcv>> FetchOhlcvAsync(string symbol, string timeframe, long since, int limit, CancellationToken ct = default);

    /// <summary>Elenco dei simboli negoziabili in forma canonica "BASE/QUOTE".</summary>
    Task<List<string>> GetSymbolsAsync(CancellationToken ct = default);

    /// <summary>
    /// Stato di quotazione di TUTTI i simboli spot: canonico "BASE/QUOTE" → stato grezzo
    /// dell'exchange (Binance: "TRADING"/"BREAK"/"HALT"/…; Bitget: "online"/"offline"/"gray"/"halt").
    /// Una sola chiamata pubblica copre l'intero listino: la pagina watchlist la usa per dire se
    /// una serie ferma è ferma perché il simbolo è SOSPESO — prima lo status veniva letto e
    /// buttato via come filtro in <see cref="GetSymbolsAsync"/>, e la verifica restava un compito
    /// manuale dell'utente. Implementazione di default vuota: è una capacità diagnostica opzionale
    /// e gli otto client finti dei test non devono pagarla.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetSymbolStatusesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    /// <summary>Verifica la raggiungibilita' dell'exchange (endpoint pubblico).</summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    // --- Trading (firmato, privato) ---

    /// <summary>Piazza un ordine. Su errore di rete imposta <c>NetworkUncertain</c>.</summary>
    Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default);

    /// <summary>Cancella un ordine per client order id.</summary>
    Task<CancelOrderResult> CancelOrderAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default);

    /// <summary>Ordini aperti (per riconciliazione dopo un errore di rete).</summary>
    Task<List<OpenOrder>> GetOpenOrdersAsync(string symbol, TradingCredentials creds, CancellationToken ct = default);

    /// <summary>
    /// Stato di un ordine per client order id, INCLUSI gli ordini già eseguiti/terminati (a
    /// differenza di <see cref="GetOpenOrdersAsync"/>). È il lookup autorevole per la
    /// riconciliazione dopo un <see cref="PlaceOrderResult.NetworkUncertain"/>.
    /// </summary>
    Task<OrderStatusResult> GetOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default);

    /// <summary>Saldi del conto.</summary>
    Task<AccountBalance> GetBalanceAsync(TradingCredentials creds, CancellationToken ct = default);

    /// <summary>Filtri di trading del simbolo (LOT_SIZE / PRICE_FILTER / minNotional). Endpoint pubblico.</summary>
    Task<SymbolFilters> GetSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default);
}
