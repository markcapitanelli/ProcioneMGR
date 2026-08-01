using Anthropic;
using Anthropic.Models.Messages;

namespace ProcioneMGR.Services.Llm;

/// <summary>
/// Opzioni del layer AI. Le API key NON sono qui: vivono cifrate a database (AiCredentials,
/// gestite da /admin/ai-supervisor) con fallback alle variabili d'ambiente
/// (<c>ANTHROPIC_API_KEY</c>, <c>NVIDIA_API_KEY</c>) — vedi <see cref="IAiKeyStore"/>.
/// </summary>
public sealed class LlmOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Provider attivo del layer AI: "Anthropic" (default, comportamento storico) o "Nvidia".
    /// Hot-reload: l'instradamento avviene A OGNI chiamata (DelegatingLlmClient), cambiare
    /// provider dal pannello non richiede riavvio.
    /// </summary>
    public string Provider { get; set; } = AiProviders.Anthropic;

    public string Model { get; set; } = "claude-opus-4-8";

    /// <summary>Modello per il provider Nvidia (namespace/modello del catalogo build.nvidia.com).</summary>
    public string NvidiaModel { get; set; } = "meta/llama-3.3-70b-instruct";

    /// <summary>
    /// Endpoint OpenAI-compatible del provider Nvidia. Parametrico DI PROPOSITO: qualunque
    /// piattaforma esponga lo stesso contratto (OpenRouter, endpoint self-hosted, …) potrà
    /// entrare cambiando URL e chiave, senza un client nuovo.
    /// </summary>
    public string NvidiaBaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";

    public int MaxTokens { get; set; } = 4096;
    public int PollIntervalMinutes { get; set; } = 5;

    /// <summary>Timeout della singola chiamata Claude (il SDK da solo aspetterebbe fino a 10 minuti).</summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>Errori transitori consecutivi dopo i quali il breaker sospende le chiamate.</summary>
    public int BreakerFailureThreshold { get; set; } = 3;

    /// <summary>Minuti tra i probe automatici a breaker aperto (il ripristino è autonomo).</summary>
    public int BreakerCooldownMinutes { get; set; } = 30;

    /// <summary>Notifica (Info) quando un'advisory riuscita contiene decisioni per l'utente. Default off.</summary>
    public bool NotifyDecisions { get; set; }
}

/// <summary>
/// Implementazione di <see cref="ILlmClient"/> sull'SDK ufficiale Anthropic (pacchetto <c>Anthropic</c>).
/// Usa il modello configurato (default <c>claude-opus-4-8</c>) con adaptive thinking. La API key è
/// letta esclusivamente dalla variabile d'ambiente <c>ANTHROPIC_API_KEY</c> — mai da appsettings —
/// e se manca il client è semplicemente "non configurato" (l'app parte lo stesso).
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    // IOptionsMonitor (non POCO): modello/token modificabili a caldo da /admin/autonomy.
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<LlmOptions> _options;
    private readonly ILogger<AnthropicLlmClient> _logger;
    private readonly IAiKeyStore? _keyStore;

    public AnthropicLlmClient(
        Microsoft.Extensions.Options.IOptionsMonitor<LlmOptions> options,
        ILogger<AnthropicLlmClient> logger,
        IAiKeyStore? keyStore = null)   // opzionale: i vecchi harness di test costruiscono senza store
    {
        _options = options;
        _logger = logger;
        _keyStore = keyStore;
    }

    // Riletta a OGNI accesso, mai cachata nel ctor: DB cifrato (pannello) prima, env poi — così
    // una chiave inserita a processo vivo prende effetto senza riavvio. (NB Windows consegna le
    // variabili UTENTE nuove solo ai processi nuovi: l'hot-read serve al worker che non muore più.)
    public bool IsConfigured => _keyStore is not null
        ? _keyStore.GetCachedKey(AiProviders.Anthropic) is not null
        : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    public string Model => _options.CurrentValue.Model;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var apiKey = _keyStore is not null
            ? await _keyStore.GetKeyAsync(AiProviders.Anthropic, ct)
            : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Nessuna chiave Anthropic: inseriscila in /admin/ai-supervisor (o imposta ANTHROPIC_API_KEY).");

        var client = new AnthropicClient { ApiKey = apiKey };

        var options = _options.CurrentValue;
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            System = systemPrompt,
            Thinking = new ThinkingConfigAdaptive(),
            Messages = [new() { Role = Role.User, Content = userPrompt }],
        }, cancellationToken: ct);

        if (response.StopReason == "refusal")
        {
            _logger.LogWarning("Il modello ha rifiutato la richiesta di supervisione (safety refusal).");
            throw new InvalidOperationException("Il modello ha rifiutato la richiesta (stop_reason=refusal).");
        }

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return text;
    }
}
