namespace ProcioneMGR.Services.Notifications;

/// <summary>
/// Provider di default: le notifiche finiscono nel log strutturato (nessuna dipendenza esterna).
/// Utile anche come "prova generale" del canale prima di configurare Telegram.
/// </summary>
public sealed class LoggingNotifier(ILogger<LoggingNotifier> logger) : INotificationProvider
{
    public string Name => "Logging";

    public Task SendAsync(NotificationSeverity severity, string title, string body, CancellationToken ct)
    {
        var level = severity switch
        {
            NotificationSeverity.Critical => LogLevel.Critical,
            NotificationSeverity.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        logger.Log(level, "[NOTIFICA] {Title} — {Body}", title, body);
        return Task.CompletedTask;
    }
}
