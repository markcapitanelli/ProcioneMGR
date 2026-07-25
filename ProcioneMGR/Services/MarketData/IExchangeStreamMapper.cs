using ProcioneMGR.Data;

namespace ProcioneMGR.Services.MarketData;

/// <summary>
/// Traduce fra il modello interno del feed e il dialetto WebSocket di un exchange: come si compone
/// l'URL, quali frame di sottoscrizione mandare dopo la connessione, come si legge un messaggio e
/// come si tiene viva la connessione a livello applicativo.
///
/// Solo MARKET DATA PUBBLICO: nessun mapper firma richieste né tocca credenziali. Da qui discende
/// che il feed funziona anche dove il trading è precluso (es. Binance Futures per un utente UE
/// soggetto a MiCA), perché nessuno dei due exchange richiede una API key per gli stream pubblici.
/// </summary>
public interface IExchangeStreamMapper
{
    ExchangeName Exchange { get; }

    /// <summary>Endpoint a cui connettersi per queste sottoscrizioni.</summary>
    Uri BuildEndpoint(IReadOnlyCollection<StreamSubscription> subscriptions);

    /// <summary>
    /// Frame da inviare subito dopo la connessione. Vuoto per gli exchange che codificano le
    /// sottoscrizioni nell'URL (Binance); popolato per quelli che le negoziano (Bitget).
    /// </summary>
    IReadOnlyList<string> BuildSubscribeFrames(IReadOnlyCollection<StreamSubscription> subscriptions);

    /// <summary>
    /// Frame di keep-alive applicativo, oppure <c>null</c> se l'exchange si accontenta dei ping di
    /// protocollo (a cui <see cref="System.Net.WebSockets.ClientWebSocket"/> risponde da solo).
    /// </summary>
    string? HeartbeatFrame { get; }

    /// <summary>Intervallo del keep-alive applicativo. Ignorato se <see cref="HeartbeatFrame"/> è null.</summary>
    TimeSpan HeartbeatInterval { get; }

    /// <summary>
    /// Interpreta un messaggio grezzo. Non lancia MAI: un frame inatteso, malformato o semplicemente
    /// non interessante (ack di sottoscrizione, pong, evento di un canale che non usiamo) ritorna
    /// <see cref="StreamEvent.None"/>. Un parser che lancia farebbe cadere la connessione per un
    /// messaggio irrilevante.
    /// </summary>
    StreamEvent Parse(string raw, IReadOnlyDictionary<string, StreamSubscription> byExchangeSymbol);
}
