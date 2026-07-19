using Microsoft.Extensions.Options;

namespace ProcioneMGR.Services.Notifications;

/// <summary>
/// L'<see cref="INotifier"/> registrato in DI: gate (<c>Notifications:Enabled</c>, default OFF,
/// hot-reload), rate-limit a finestra scorrevole con coalescing (i messaggi soppressi vengono
/// conteggiati e riportati nel primo messaggio successivo, mai persi in silenzio) e selezione
/// del provider per nome. NON propaga MAI eccezioni al producer: una notifica fallita non deve
/// far fallire un watchdog o un planner (si degrada a log d'errore).
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

    public async Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)
    {
        var opt = options.CurrentValue;
        if (!opt.Enabled) return;

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
                return;
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
            return;
        }

        try
        {
            await provider.SendAsync(severity, title, body, ct);
        }
        catch (Exception ex)
        {
            // Mai propagare al producer: il canale di ritorno è "best effort ma rumoroso nel log".
            logger.LogError(ex, "Recapito notifica fallito su {Provider}: [{Severity}] {Title} — {Body}",
                provider.Name, severity, title, body);
        }
    }
}
