using System.Text.Json;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Exchanges;

/// <summary>
/// Orologio usato per il campo <c>timestamp</c> delle richieste FIRMATE.
///
/// Perché non basta <see cref="DateTime.UtcNow"/>: gli exchange rifiutano una richiesta firmata se
/// il suo timestamp si discosta dall'ora del LORO server oltre <c>recvWindow</c> (5s qui). Un
/// orologio locale che deriva di pochi secondi — cosa normale su una macchina desktop che non
/// sincronizza NTP con regolarità, o dopo una sospensione — fa fallire ordini validi con l'errore
/// Binance <c>-1021 Timestamp for this request is outside of the recvWindow</c>. È un guasto
/// particolarmente sgradevole perché intermittente e perché colpisce anche le CHIUSURE: uno stop
/// che non riesce a partire per un problema d'orologio è una perdita reale.
///
/// Rimedio: si misura una volta l'offset rispetto al server dell'exchange e lo si applica a ogni
/// timestamp firmato, riallineandolo periodicamente.
/// </summary>
public interface IExchangeClock
{
    /// <summary>Millisecondi Unix da usare nel campo <c>timestamp</c>, corretti per l'offset noto.</summary>
    long TimestampMillis(ExchangeName exchange);

    /// <summary>Offset corrente (ora server − ora locale). Zero finché non è stato misurato.</summary>
    TimeSpan Offset(ExchangeName exchange);

    /// <summary>Registra un offset appena misurato.</summary>
    void SetOffset(ExchangeName exchange, TimeSpan offset);
}

/// <summary>
/// Implementazione condivisa (singleton). Parte con offset ZERO per ogni exchange, quindi finché
/// <see cref="ExchangeClockSyncWorker"/> non ha misurato nulla il comportamento è identico a quello
/// storico: nessuna regressione se la sonda fallisce o non gira.
/// </summary>
public sealed class ExchangeClock(ILogger<ExchangeClock> logger, TimeProvider? timeProvider = null) : IExchangeClock
{
    /// <summary>
    /// Oltre questa deriva l'offset è più probabilmente un errore di misura (una risposta lentissima,
    /// un proxy che ha bufferizzato) che un orologio davvero sballato: applicarlo peggiorerebbe le
    /// cose. Si rifiuta e si lascia l'offset precedente, gridandolo nei log.
    /// </summary>
    private static readonly TimeSpan MaxPlausibleOffset = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ExchangeName, TimeSpan> _offsets = new();

    public TimeSpan Offset(ExchangeName exchange) =>
        _offsets.TryGetValue(exchange, out var offset) ? offset : TimeSpan.Zero;

    public long TimestampMillis(ExchangeName exchange) =>
        (_time.GetUtcNow() + Offset(exchange)).ToUnixTimeMilliseconds();

    public void SetOffset(ExchangeName exchange, TimeSpan offset)
    {
        if (offset.Duration() > MaxPlausibleOffset)
        {
            logger.LogError(
                "Offset d'orologio implausibile per {Exchange} ({Offset}): RIFIUTATO, resta {Current}. " +
                "Verificare la sincronizzazione NTP della macchina.",
                exchange, offset, Offset(exchange));
            return;
        }

        var previous = Offset(exchange);
        _offsets[exchange] = offset;

        // Si logga solo quando la correzione è significativa: sotto il secondo è rumore di rete.
        if ((offset - previous).Duration() > TimeSpan.FromSeconds(1))
        {
            logger.LogInformation("Offset d'orologio {Exchange} aggiornato: {Previous} -> {Offset}.",
                exchange, previous, offset);
        }
    }
}

/// <summary>
/// Misura periodicamente l'offset d'orologio verso ogni exchange interrogandone l'endpoint di ora
/// del server (pubblico, nessuna credenziale).
///
/// La misura sottrae metà del round-trip: il timestamp che il server riporta è di quando LUI ha
/// risposto, quindi confrontarlo con l'ora locale di ricezione conterebbe come deriva anche la
/// latenza di rete, che deriva non è.
/// </summary>
public sealed class ExchangeClockSyncWorker(
    IExchangeClock clock,
    IHttpClientFactory httpFactory,
    ILogger<ExchangeClockSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(1);

    /// <summary>Endpoint pubblici di ora del server, per exchange.</summary>
    private static readonly (ExchangeName Exchange, string Url, string JsonField)[] Sources =
    [
        (ExchangeName.Binance, "https://api.binance.com/api/v3/time", "serverTime"),
        (ExchangeName.Bitget, "https://api.bitget.com/api/v2/public/time", "serverTime"),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subito una prima misura: le richieste firmate possono partire pochi secondi dopo l'avvio.
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var source in Sources)
            {
                try
                {
                    await SyncAsync(source.Exchange, source.Url, source.JsonField, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // Non fatale: si resta sull'offset precedente (zero al primo giro), che è
                    // esattamente il comportamento storico.
                    logger.LogWarning(ex, "Sincronizzazione orologio {Exchange} fallita; riprovo al prossimo giro.", source.Exchange);
                }
            }

            try { await Task.Delay(SyncInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SyncAsync(ExchangeName exchange, string url, string field, CancellationToken ct)
    {
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);

        var before = DateTimeOffset.UtcNow;
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var after = DateTimeOffset.UtcNow;

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var root = doc.RootElement;
        // Bitget annida sotto "data": { "serverTime": "..." }; Binance lo espone in radice.
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            root = data;
        }
        if (!root.TryGetProperty(field, out var timeEl) || !TryReadMillis(timeEl, out var serverMs))
        {
            logger.LogWarning("Risposta di ora del server {Exchange} non interpretabile.", exchange);
            return;
        }

        var roundTrip = after - before;
        var localAtServerResponse = before + roundTrip / 2;
        var serverTime = DateTimeOffset.FromUnixTimeMilliseconds(serverMs);

        clock.SetOffset(exchange, serverTime - localAtServerResponse);
    }

    private static bool TryReadMillis(JsonElement el, out long millis)
    {
        millis = 0L;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out millis),
            JsonValueKind.String => long.TryParse(el.GetString(), out millis),
            _ => false,
        };
    }
}
