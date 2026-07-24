using System.Net.WebSockets;
using System.Text;

namespace ProcioneMGR.Services.MarketData;

/// <summary>
/// Canale WebSocket ridotto all'osso. Esiste come interfaccia per una ragione sola: rendere
/// TESTABILE <see cref="WebSocketPriceFeed"/> senza rete né server finto — i test iniettano un
/// transport che consegna messaggi da una coda e simula disconnessioni. La logica difficile
/// (riconnessione, backoff, staleness, resubscribe) sta nel feed, non qui.
/// </summary>
public interface IWebSocketTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);

    Task SendAsync(string message, CancellationToken ct);

    /// <summary>Prossimo messaggio testuale completo, oppure <c>null</c> se il canale si è chiuso.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
}

/// <summary>Crea un transport nuovo per ogni tentativo di connessione (un ClientWebSocket non si riusa dopo la chiusura).</summary>
public interface IWebSocketTransportFactory
{
    IWebSocketTransport Create();
}

/// <summary>
/// Implementazione su <see cref="ClientWebSocket"/> della BCL. Nessuna dipendenza esterna: la
/// libreria SuperSocket non serve, .NET ha già tutto — e i frame di ping del server ricevono
/// risposta pong automaticamente dallo stack, quindi il keep-alive di protocollo è gratis.
/// (Il ping APPLICATIVO richiesto da alcuni exchange, es. Bitget, resta compito del mapper.)
/// </summary>
public sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private const int ReceiveBufferSize = 16 * 1024;

    /// <summary>
    /// Tetto sul messaggio riassemblato. Un frame patologicamente grande (o un server che non
    /// chiude mai il messaggio) non deve poter far crescere il buffer senza limite.
    /// </summary>
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    private readonly ClientWebSocket _socket = new();

    public ClientWebSocketTransport() =>
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

    public Task ConnectAsync(Uri uri, CancellationToken ct) => _socket.ConnectAsync(uri, ct);

    public Task SendAsync(string message, CancellationToken ct) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, ct);

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        using var message = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, ct);
            }
            catch (WebSocketException)
            {
                return null; // canale caduto: il feed lo tratta come disconnessione e riconnette
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                return null; // messaggio abnorme: si tratta il canale come corrotto
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
        }
        catch
        {
            // La chiusura pulita è best-effort: se il peer è già sparito non c'è nulla da salvare.
        }
        _socket.Dispose();
    }
}

/// <summary>Factory di produzione.</summary>
public sealed class ClientWebSocketTransportFactory : IWebSocketTransportFactory
{
    public IWebSocketTransport Create() => new ClientWebSocketTransport();
}
