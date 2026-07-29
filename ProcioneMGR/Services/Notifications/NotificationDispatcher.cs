using Microsoft.Extensions.Options;

namespace ProcioneMGR.Services.Notifications;

/// <summary>Esito di un tentativo di recapito. Serve alla diagnostica, non ai producer.</summary>
public enum NotificationOutcome
{
    /// <summary>Il provider ha accettato il messaggio.</summary>
    Delivered,

    /// <summary>Gate globale spento (<c>Notifications:Enabled=false</c>): scartata prima di tutto.</summary>
    Disabled,

    /// <summary>Soppressa dal rate-limit; verrà conteggiata nel primo messaggio successivo.</summary>
    RateLimited,

    /// <summary>Nome di provider non riconosciuto in configurazione.</summary>
    UnknownProvider,

    /// <summary>Il provider ha rifiutato o è esploso (token assente, rete, API).</summary>
    Failed,
}

/// <param name="Detail">Motivo leggibile quando l'esito non è <see cref="NotificationOutcome.Delivered"/>.</param>
public sealed record NotificationResult(NotificationOutcome Outcome, string? Detail = null)
{
    public bool IsDelivered => Outcome == NotificationOutcome.Delivered;
}

/// <summary>
/// L'<see cref="INotifier"/> registrato in DI: gate (<c>Notifications:Enabled</c>, default OFF,
/// hot-reload), rate-limit a finestra scorrevole con coalescing (i messaggi soppressi vengono
/// conteggiati e riportati nel primo messaggio successivo, mai persi in silenzio) e selezione
/// del provider per nome. NON propaga MAI eccezioni al producer: una notifica fallita non deve
/// far fallire un watchdog o un planner (si degrada a log d'errore).
///
/// <para>Quel «non propaga mai» è giusto per i producer e sbagliato per chi vuole SAPERE se il
/// canale funziona: il 2026-07-29 il pulsante «Invia notifica di prova» di /admin/autonomy
/// dichiarava successo mentre il recapito falliva per <c>TELEGRAM_BOT_TOKEN</c> assente — una
/// verifica che dice la cosa rassicurante indipendentemente dalla realtà è peggio di nessuna
/// verifica. Da qui <see cref="SendDiagnosticAsync"/>: stesso identico percorso (gate, rate-limit,
/// provider), ma l'esito torna al chiamante invece di finire solo nel log.</para>
/// </summary>
public sealed class NotificationDispatcher(
    IOptionsMonitor<NotificationOptions> options,
    IEnumerable<INotificationProvider> providers,
    ILogger<NotificationDispatcher> logger,
    TimeProvider? timeProvider = null) : INotifier
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _sentInWindow = new();
    private int _suppressed;

    /// <summary>
    /// Contratto invariato verso i producer: nessuna eccezione, nessun esito da controllare.
    /// Il risultato lo assorbe il log, esattamente come prima.
    /// </summary>
    public async Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        => await SendAsync(severity, title, body, ct);

    /// <summary>
    /// Percorso di VERIFICA: stessa catena di <see cref="NotifyAsync"/> — quindi una prova che passa
    /// dimostra davvero che il canale configurato recapita, gate e rate-limit compresi — ma con
    /// l'esito restituito, così la UI distingue «consegnato» da «gate spento», «rate-limit»,
    /// «provider sconosciuto» e «provider fallito».
    /// </summary>
    public Task<NotificationResult> SendDiagnosticAsync(
        NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        => SendAsync(severity, title, body, ct);

    private async Task<NotificationResult> SendAsync(
        NotificationSeverity severity, string title, string body, CancellationToken ct)
    {
        var opt = options.CurrentValue;
        if (!opt.Enabled)
        {
            return new NotificationResult(NotificationOutcome.Disabled,
                "Notifications:Enabled = false: il messaggio è stato scartato prima di scegliere un provider.");
        }

        int suppressedToReport;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            while (_sentInWindow.Count > 0 && now - _sentInWindow.Peek() > TimeSpan.FromHours(1))
            {
                _sentInWindow.Dequeue();
            }
            if (_sentInWindow.Count >= Math.Max(1, opt.MaxPerHour))
            {
                _suppressed++;
                logger.LogWarning("Notifica SOPPRESSA dal rate-limit ({Max}/h): [{Severity}] {Title}", opt.MaxPerHour, severity, title);
                return new NotificationResult(NotificationOutcome.RateLimited,
                    $"Raggiunto il tetto di {opt.MaxPerHour} messaggi/ora: questa verrà conteggiata nel primo messaggio successivo.");
            }
            _sentInWindow.Enqueue(now);
            suppressedToReport = _suppressed;
            _suppressed = 0;
        }

        if (suppressedToReport > 0)
        {
            body += $"\n(+{suppressedToReport} notifiche soppresse dal rate-limit nell'ultima ora)";
        }

        var provider = providers.FirstOrDefault(p => string.Equals(p.Name, opt.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            logger.LogError("Provider di notifica '{Provider}' sconosciuto: notifica [{Severity}] {Title} solo nel log. {Body}",
                opt.Provider, severity, title, body);
            return new NotificationResult(NotificationOutcome.UnknownProvider,
                $"Provider '{opt.Provider}' sconosciuto. Disponibili: {string.Join(", ", providers.Select(p => p.Name))}.");
        }

        try
        {
            await provider.SendAsync(severity, title, body, ct);
            return new NotificationResult(NotificationOutcome.Delivered);
        }
        catch (Exception ex)
        {
            // Mai propagare al producer: il canale di ritorno è "best effort ma rumoroso nel log".
            logger.LogError(ex, "Recapito notifica fallito su {Provider}: [{Severity}] {Title} — {Body}",
                provider.Name, severity, title, body);
            return new NotificationResult(NotificationOutcome.Failed, $"{provider.Name}: {ex.Message}");
        }
    }
}
