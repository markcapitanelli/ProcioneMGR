using Microsoft.Extensions.Options;

namespace ProcioneMGR.Services.Notifications;

/// <summary>
/// Provider Telegram (PRD Autonomia §7: pragmatico per un solo operatore — gratuito, push su
/// mobile). Il token del bot NON sta mai in config/repo: SOLO dalla variabile d'ambiente
/// <see cref="TokenEnvVar"/> (stesso patto di ANTHROPIC_API_KEY per il layer AI). La chat di
/// destinazione (<see cref="NotificationOptions.ChatId"/>) non è un segreto e sta in config.
/// </summary>
public sealed class TelegramNotifier(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<NotificationOptions> options) : INotificationProvider
{
    public const string TokenEnvVar = "TELEGRAM_BOT_TOKEN";

    /// <summary>Nome del client HTTP nominato (i test lo intercettano con un handler scriptato).</summary>
    public const string HttpClientName = "telegram-notifier";

    public string Name => "Telegram";

    public async Task SendAsync(NotificationSeverity severity, string title, string body, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable(TokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"Provider Telegram selezionato ma env {TokenEnvVar} assente: impostala col token del bot (mai in config).");
        }
        var chatId = options.CurrentValue.ChatId;
        if (string.IsNullOrWhiteSpace(chatId))
        {
            throw new InvalidOperationException("Notifications:ChatId mancante: serve la chat Telegram di destinazione.");
        }

        var icon = severity switch
        {
            NotificationSeverity.Critical => "🔴",
            NotificationSeverity.Warning => "🟡",
            _ => "ℹ️",
        };

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["text"] = $"{icon} {title}\n{body}",
        });
        using var response = await client.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Telegram sendMessage fallita: HTTP {(int)response.StatusCode}.");
        }
    }
}
