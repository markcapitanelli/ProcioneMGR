using Microsoft.Extensions.Options;

namespace ProcioneMGR.Services.Notifications;

/// <summary>
/// Provider Telegram (PRD Autonomia §7: pragmatico per un solo operatore — gratuito, push su
/// mobile). Il token del bot NON sta mai in config/repo: dalla variabile d'ambiente
/// <see cref="TokenEnvVar"/> (stesso patto di ANTHROPIC_API_KEY per il layer AI), con fallback su
/// <see cref="TokenFilePath"/> — lo STESSO file che usano la plancia e .claude/launch.json. La chat
/// di destinazione (<see cref="NotificationOptions.ChatId"/>) non è un segreto e sta in config.
///
/// <para>Il fallback sul file esiste dal 2026-08-28: il canale dipendeva dalla catena che AVVIA il
/// guscio (l'ambiente si eredita), e un guscio partito da una catena senza la variabile resta muto
/// per giorni con la piattaforma sana — il dispatcher assorbe l'errore per contratto e la spia
/// <c>ChannelStatus</c> vive solo in memoria. La plancia aveva già lo stesso fallback per lo stesso
/// motivo (Supervisor.Ambiente: «un dead-man switch che scopre il guasto e non riesce a dirlo è
/// mezzo dead-man switch»).</para>
/// </summary>
public sealed class TelegramNotifier(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<NotificationOptions> options,
    Func<string?>? tokenFileReader = null) : INotificationProvider
{
    public const string TokenEnvVar = "TELEGRAM_BOT_TOKEN";

    /// <summary>Nome del client HTTP nominato (i test lo intercettano con un handler scriptato).</summary>
    public const string HttpClientName = "telegram-notifier";

    /// <summary>Il file gemello di quello della plancia: <c>~/.procione/telegram.token</c>.</summary>
    public static string TokenFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".procione", "telegram.token");

    public string Name => "Telegram";

    /// <summary>
    /// Risoluzione PURA del token: l'ambiente vince (è il canale dichiarato), il file è la rete di
    /// sicurezza, il bianco è assenza. Separata per essere provabile senza toccare env né disco.
    /// </summary>
    internal static string? ResolveToken(string? fromEnv, string? fromFile)
    {
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
        if (!string.IsNullOrWhiteSpace(fromFile)) return fromFile.Trim();
        return null;
    }

    // Il lettore del file è iniettabile (default: il file vero) perché sulla macchina del
    // proprietario ~/.procione/telegram.token ESISTE: un test che provasse «token assente»
    // contro il disco vero passerebbe in CI e fallirebbe lì — o peggio, userebbe il token vero.
    private static string? ReadTokenFile()
    {
        try
        {
            var path = TokenFilePath;
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            // Un file illeggibile equivale a un file assente: la diagnosi vera sta nel messaggio
            // dell'eccezione a valle, che nomina entrambe le fonti.
            return null;
        }
    }

    public async Task SendAsync(NotificationSeverity severity, string title, string body, CancellationToken ct)
    {
        var leggiFile = tokenFileReader ?? ReadTokenFile;
        var token = ResolveToken(Environment.GetEnvironmentVariable(TokenEnvVar), leggiFile());
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"Provider Telegram selezionato ma il token non c'è: né env {TokenEnvVar}, né file {TokenFilePath} (mai in config).");
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
