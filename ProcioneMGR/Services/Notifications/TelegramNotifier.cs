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

        // [2026-09-05] Telegram rifiuta con HTTP 400 un testo oltre 4.096 caratteri, e il digest
        // giornaliero li supera: il 5/09 alle 08:26 è fallito, ha ritentato dopo 15 minuti ed è
        // fallito di nuovo, e per due giorni nessun digest è arrivato. Il messaggio si spezza in
        // parti numerate; un errore porta ANCHE la descrizione di Telegram, che finora veniva
        // scartata — «HTTP 400» da solo non dice se è il testo, il chat_id o il token.
        var parti = Spezza($"{icon} {title}\n{body}");
        for (var i = 0; i < parti.Count; i++)
        {
            var testo = parti.Count == 1 ? parti[i] : $"{parti[i]}\n[{i + 1}/{parti.Count}]";
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = testo,
            });
            using var response = await client.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                string dettaglio;
                try { dettaglio = (await response.Content.ReadAsStringAsync(ct)).Trim(); }
                catch (Exception) { dettaglio = string.Empty; }
                if (dettaglio.Length > 300) dettaglio = dettaglio[..300] + "…";
                throw new InvalidOperationException(
                    $"Telegram sendMessage fallita: HTTP {(int)response.StatusCode}"
                    + (dettaglio.Length > 0 ? $" — {dettaglio}" : ".")
                    + (parti.Count > 1 ? $" (parte {i + 1}/{parti.Count})" : ""));
            }
        }
    }

    /// <summary>Limite di Telegram per il campo <c>text</c> di sendMessage.</summary>
    public const int MaxCaratteri = 4096;

    /// <summary>
    /// Margine per il suffisso «[n/N]» e per i caratteri contati doppi (UTF-16): si spezza prima
    /// del limite, non sul limite.
    /// </summary>
    internal const int TagliaParte = 3900;

    /// <summary>
    /// Puro: spezza un testo in parti sotto <see cref="TagliaParte"/>, preferendo un a-capo come
    /// punto di taglio così che una riga del digest non finisca a metà fra due messaggi.
    /// </summary>
    internal static IReadOnlyList<string> Spezza(string testo)
    {
        if (testo.Length <= MaxCaratteri) return [testo];

        var parti = new List<string>();
        var resto = testo;
        while (resto.Length > TagliaParte)
        {
            var taglio = resto.LastIndexOf('\n', TagliaParte);
            if (taglio < TagliaParte / 2) taglio = TagliaParte; // niente a-capo utile: si taglia secco
            parti.Add(resto[..taglio].TrimEnd());
            resto = resto[taglio..].TrimStart('\n');
        }
        if (resto.Length > 0) parti.Add(resto);
        return parti;
    }
}
