namespace ProcioneMGR.Services.Notifications;

/// <summary>Gravità di una notifica (mappa su livello di log e icona del messaggio).</summary>
public enum NotificationSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// Canale di notifica verso l'operatore (Fase 4, PRD Autonomia Operativa §7): il contrario
/// dell'autonomia cieca — un modo affidabile di CHIAMARE l'umano quando serve. Un solo metodo,
/// nessun bus: progetto solo-operatore. L'implementazione registrata è
/// <see cref="NotificationDispatcher"/> (gate + rate-limit + scelta provider); i producer NON
/// devono mai fallire per colpa di una notifica — il dispatcher non propaga eccezioni.
/// </summary>
public interface INotifier
{
    Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default);
}

/// <summary>Provider concreto di recapito (Logging, Telegram, …), selezionato da <see cref="NotificationOptions.Provider"/>.</summary>
public interface INotificationProvider
{
    /// <summary>Nome con cui il provider si seleziona in config (case-insensitive).</summary>
    string Name { get; }

    /// <summary>Recapita il messaggio. Può lanciare: è il dispatcher a contenere l'errore.</summary>
    Task SendAsync(NotificationSeverity severity, string title, string body, CancellationToken ct);
}

/// <summary>Opzioni del canale di notifica, sezione <c>Notifications</c>. Default OFF.</summary>
public sealed class NotificationOptions
{
    /// <summary>Default false: nessuna notifica finché l'operatore non abilita esplicitamente.</summary>
    public bool Enabled { get; set; }

    /// <summary>"Logging" (default) | "Telegram".</summary>
    public string Provider { get; set; } = "Logging";

    /// <summary>Chat id Telegram di destinazione (il token del bot NON va in config: env TELEGRAM_BOT_TOKEN).</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Rate-limit: massimo di messaggi recapitati per ora (finestra scorrevole); l'eccesso viene coalizzato.</summary>
    public int MaxPerHour { get; set; } = 20;
}
