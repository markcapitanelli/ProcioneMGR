using System.Text.Json;
using Grpc.Core;
using ProcioneMGR.Services.Notifications;
using Proto = ProcioneMGR.Contracts.Trading.V1;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Da dove arrivano — e dove finiscono — le sezioni di configurazione OSPITATE DAL MOTORE.
///
/// <para>Stesso patto di <c>IMarketDataSyncService</c>: il pannello inietta l'interfaccia e ignora
/// quale implementazione sia attiva. Col motore in-process si scrive il file di questo processo,
/// che è anche quello che il motore legge; col motore remoto si parla con lui via gRPC, perché il
/// suo file non è il nostro — è la lezione del 2026-07-29, quando il PVC che avrebbe dovuto
/// condividerli era rimasto a <c>{}</c> e ogni soglia mostrata in UI era quella sbagliata.</para>
/// </summary>
public interface IEngineConfigStore
{
    /// <summary>Vero se il motore vive in un altro processo (cambia solo cosa dire all'operatore).</summary>
    bool IsRemote { get; }

    /// <summary>Sezioni richieste (vuoto = tutte quelle note), coi valori EFFETTIVI del motore.</summary>
    Task<EngineConfigSnapshot> ReadAsync(IEnumerable<string>? sections = null, CancellationToken ct = default);

    /// <summary>
    /// Sostituisce una sezione. Lancia <see cref="InvalidOperationException"/> con un messaggio
    /// leggibile se la sezione non è scrivibile, il valore non è valido, o il motore non risponde.
    /// </summary>
    Task<EngineConfigWriteResult> WriteAsync(string section, object options, CancellationToken ct = default);

    /// <summary>
    /// Prova il canale di notifica DEL MOTORE, non quello del guscio: sono due processi con
    /// variabili d'ambiente diverse, e il motore può essere muto mentre il guscio recapita. È il
    /// producer degli allarmi di quarantena, quindi il suo silenzio è il più costoso di tutti.
    /// </summary>
    Task<NotificationResult> SendTestNotificationAsync(CancellationToken ct = default);

    /// <summary>
    /// [E5] Spia di guasto del canale DEL MOTORE, letta senza inviare nulla: ultimo recapito,
    /// ultimo fallimento col motivo, fallimenti accumulati. La prova qui sopra è un gesto; questa è
    /// la memoria di ciò che è successo fra una prova e l'altra.
    /// </summary>
    Task<EngineNotificationChannelStatus> GetNotificationChannelStatusAsync(CancellationToken ct = default);
}

/// <summary>[E5] Spia del canale del motore come la vede il guscio.</summary>
/// <param name="Reachable">Falso se il motore remoto non ha risposto: <paramref name="Status"/> non significa nulla.</param>
/// <param name="ChannelComposed">Falso se l'host del motore non ha composto alcun canale di notifica.</param>
/// <param name="Status">Lo stato del canale, quando raggiungibile e composto.</param>
/// <param name="Error">Motivo dell'irraggiungibilità.</param>
public sealed record EngineNotificationChannelStatus(
    bool Reachable,
    bool ChannelComposed,
    NotificationChannelStatus? Status,
    string? Error = null);

/// <summary>Fotografia della configurazione del motore, con la diagnostica che la rende leggibile.</summary>
/// <param name="Sections">Le sezioni lette.</param>
/// <param name="ConfigPath">File su cui il motore scrive (vuoto se non lo sappiamo).</param>
/// <param name="Writable">Il motore può riscrivere la propria configurazione?</param>
/// <param name="Reachable">
/// Falso quando il motore remoto non risponde: il pannello mostra l'ultimo stato noto (i default)
/// DICENDO che non è stato possibile chiederglielo, invece di spacciarlo per la verità.
/// </param>
/// <param name="Error">Motivo dell'irraggiungibilità, se <paramref name="Reachable"/> è falso.</param>
public sealed record EngineConfigSnapshot(
    IReadOnlyList<EngineConfigSectionView> Sections,
    string ConfigPath,
    bool Writable,
    bool Reachable = true,
    string? Error = null)
{
    /// <summary>Deserializza una sezione nel suo POCO, o restituisce il default se assente/illeggibile.</summary>
    public T Bind<T>(string section) where T : new()
    {
        var view = Sections.FirstOrDefault(s => string.Equals(s.Path, section, StringComparison.OrdinalIgnoreCase));
        if (view is null || string.IsNullOrWhiteSpace(view.Json)) return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(view.Json, JsonOptions) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    /// <summary>Sorgente prevalente della sezione ("ConfigMap", "file", "default del codice"…).</summary>
    public string? SourceOf(string section) => Sections
        .FirstOrDefault(s => string.Equals(s.Path, section, StringComparison.OrdinalIgnoreCase))?.Source;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

/// <summary>
/// Motore in-process: il file di questo processo È quello che il motore legge, quindi si scrive
/// direttamente. Nessuna rete, nessun caso di irraggiungibilità.
/// </summary>
/// <param name="dispatcher">
/// OPZIONALE, come per ogni altro consumatore di notifiche in questa composizione: il
/// <c>LaneInvariantWatchdog</c> risolve <c>INotifier</c> con <c>GetService</c> e non con
/// <c>GetRequired</c>, perché una corsia deve poter girare in un host che non ha composto alcun
/// canale. Pretenderlo qui avrebbe rovesciato quell'invariante per un pulsante di diagnostica —
/// e fatto fallire l'avvio invece di dire che il canale non c'è.
/// </param>
public sealed class LocalEngineConfigStore(
    EngineConfigService service,
    NotificationDispatcher? dispatcher = null) : IEngineConfigStore
{
    public bool IsRemote => false;

    public Task<EngineConfigSnapshot> ReadAsync(IEnumerable<string>? sections = null, CancellationToken ct = default)
        => Task.FromResult(new EngineConfigSnapshot(service.Read(sections), service.ConfigPath, service.IsWritable()));

    public Task<EngineConfigWriteResult> WriteAsync(string section, object options, CancellationToken ct = default)
        => service.WriteAsync(section, JsonSerializer.Serialize(options, EngineConfigSnapshot.JsonOptions), ct);

    /// <summary>Motore in-process: il suo canale di notifica è questo, quindi si prova direttamente.</summary>
    public Task<NotificationResult> SendTestNotificationAsync(CancellationToken ct = default)
    {
        if (dispatcher is null)
        {
            return Task.FromResult(new NotificationResult(NotificationOutcome.Failed,
                "Nessun canale di notifica composto in questo host: non c'è nulla da provare."));
        }

        return dispatcher.SendDiagnosticAsync(NotificationSeverity.Info,
            "Notifica di prova (motore)",
            "Se leggi questo messaggio, gli allarmi del MOTORE — quarantena corsie compresa — ti raggiungono.",
            ct);
    }

    /// <summary>Motore in-process: la spia è quella del dispatcher di questo host.</summary>
    public Task<EngineNotificationChannelStatus> GetNotificationChannelStatusAsync(CancellationToken ct = default)
        => Task.FromResult(dispatcher is null
            ? new EngineNotificationChannelStatus(Reachable: true, ChannelComposed: false, Status: null)
            : new EngineNotificationChannelStatus(Reachable: true, ChannelComposed: true, dispatcher.ChannelStatus));
}

/// <summary>
/// Motore remoto: si chiede a lui. Gli errori di trasporto NON diventano eccezioni in faccia a chi
/// apre la pagina — in lettura si degrada a "non raggiungibile" con i default, perché un pannello
/// che esplode quando il core è giù è inutile proprio nel momento in cui serve guardarlo. In
/// SCRITTURA invece si propaga: lì il silenzio farebbe credere di aver cambiato qualcosa.
/// </summary>
public sealed class RemoteEngineConfigStore(
    Proto.TradingCommandService.TradingCommandServiceClient client,
    ILogger<RemoteEngineConfigStore> logger) : IEngineConfigStore
{
    public bool IsRemote => true;

    public async Task<EngineConfigSnapshot> ReadAsync(IEnumerable<string>? sections = null, CancellationToken ct = default)
    {
        var request = new Proto.GetEngineConfigRequest();
        if (sections is not null) request.Sections.AddRange(sections);

        try
        {
            var response = await client.GetEngineConfigAsync(request, cancellationToken: ct);
            var views = response.Sections
                .Select(s => new EngineConfigSectionView(s.Path, s.Json, s.Writable, s.Source))
                .ToList();
            return new EngineConfigSnapshot(views, response.ConfigPath, response.Writable);
        }
        catch (RpcException ex)
        {
            logger.LogWarning(ex, "Configurazione del motore non leggibile via gRPC.");
            return new EngineConfigSnapshot([], string.Empty, Writable: false, Reachable: false,
                Error: DescribeRpcFailure(ex));
        }
    }

    public async Task<EngineConfigWriteResult> WriteAsync(string section, object options, CancellationToken ct = default)
    {
        try
        {
            var response = await client.SetEngineConfigAsync(new Proto.SetEngineConfigRequest
            {
                Section = section,
                Json = JsonSerializer.Serialize(options, EngineConfigSnapshot.JsonOptions),
            }, cancellationToken: ct);

            return new EngineConfigWriteResult(
                response.AppliedJson,
                string.IsNullOrEmpty(response.Warning) ? null : response.Warning);
        }
        catch (RpcException ex)
        {
            // I rifiuti di dominio del motore (allow-list, validazione) arrivano già con un
            // messaggio scritto per un umano: si ripropone quello, non un codice di stato.
            throw new InvalidOperationException(DescribeRpcFailure(ex), ex);
        }
    }

    public async Task<NotificationResult> SendTestNotificationAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await client.SendTestNotificationAsync(
                new Proto.SendTestNotificationRequest(), cancellationToken: ct);

            // L'esito viaggia come stringa (il contratto non deve cambiare a ogni nuovo caso):
            // parsing ESPLICITO, e un valore sconosciuto diventa Failed invece di scivolare nel
            // vicino di enum — stessa regola degli altri enum di questo contratto.
            var outcome = Enum.TryParse<NotificationOutcome>(response.Outcome, out var parsed)
                ? parsed
                : NotificationOutcome.Failed;

            var detail = string.IsNullOrEmpty(response.Detail) ? null : response.Detail;
            return new NotificationResult(outcome, detail);
        }
        catch (RpcException ex)
        {
            return new NotificationResult(NotificationOutcome.Failed, DescribeRpcFailure(ex));
        }
    }

    public async Task<EngineNotificationChannelStatus> GetNotificationChannelStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetNotificationChannelStatusAsync(
                new Proto.GetNotificationChannelStatusRequest(), cancellationToken: ct);

            if (!response.ChannelComposed)
            {
                return new EngineNotificationChannelStatus(Reachable: true, ChannelComposed: false, Status: null);
            }

            return new EngineNotificationChannelStatus(Reachable: true, ChannelComposed: true,
                new NotificationChannelStatus(
                    response.LastDeliveredUtc?.ToDateTime(),
                    response.LastFailureUtc?.ToDateTime(),
                    string.IsNullOrEmpty(response.LastFailureDetail) ? null : response.LastFailureDetail,
                    response.FailuresSinceLastDelivery));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            return new EngineNotificationChannelStatus(Reachable: false, ChannelComposed: false, Status: null,
                "Il motore in esecuzione è una build precedente, che non espone ancora la spia del canale: va aggiornato.");
        }
        catch (RpcException ex)
        {
            return new EngineNotificationChannelStatus(Reachable: false, ChannelComposed: false, Status: null,
                DescribeRpcFailure(ex));
        }
    }

    /// <summary>
    /// Traduce il fallimento in una frase che dice cosa fare. "Unavailable" da solo manda a
    /// cercare nel posto sbagliato: quasi sempre è il port-forward chiuso, non il motore morto.
    /// </summary>
    private static string DescribeRpcFailure(RpcException ex) => ex.StatusCode switch
    {
        StatusCode.PermissionDenied or StatusCode.InvalidArgument or StatusCode.FailedPrecondition
            => ex.Status.Detail,
        StatusCode.Unimplemented
            => "Il motore in esecuzione è una build precedente, che non espone ancora la configurazione via gRPC: va aggiornato.",
        StatusCode.Unauthenticated
            => "Il motore ha rifiutato l'autorizzazione: Trading:GrpcSharedSecret deve combaciare da entrambe le parti.",
        StatusCode.Unavailable
            => "Motore non raggiungibile (di solito il port-forward 18092 è chiuso, non il motore fermo: vedi scripts/ensure-trading-portforward.ps1).",
        _ => $"Errore dal motore ({ex.StatusCode}): {ex.Status.Detail}",
    };
}
