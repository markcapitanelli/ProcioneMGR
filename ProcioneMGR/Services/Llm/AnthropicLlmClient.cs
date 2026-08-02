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
    /// Provider attivo del layer AI (una voce di <see cref="AiProviders.Known"/>). Default Nvidia
    /// dal 2026-08-02 (Anthropic retrocessa: credito esaurito). Hot-reload: l'instradamento
    /// avviene A OGNI chiamata (DelegatingLlmClient), cambiare provider dal pannello non
    /// richiede riavvio.
    /// </summary>
    public string Provider { get; set; } = AiProviders.Nvidia;

    /// <summary>
    /// [Failover 2026-08-02] Se la chiamata al provider attivo fallisce (qualunque errore che non
    /// sia una cancellazione), il DelegatingLlmClient prova DA SOLO i provider di questa lista,
    /// nell'ordine, saltando quelli senza chiave e il provider già tentato. Default on: con più
    /// AI configurate, un 503 del free tier non deve fermare advisory o sentiment.
    /// </summary>
    public bool FailoverEnabled { get; set; } = true;

    /// <summary>
    /// Catena di failover, nell'ordine di tentativo; VUOTA = catena di default
    /// (<see cref="DefaultFailoverChain"/>). Il default sta in una costante e NON qui: il binder
    /// di configurazione APPENDE gli elementi dell'array alla lista già inizializzata invece di
    /// sostituirla — con un default popolato la lista raddoppiava a ogni salvataggio dal pannello
    /// (successo davvero, 2026-08-02). Anthropic esclusa dal default (credito esaurito) ma
    /// aggiungibile a mano.
    /// </summary>
    public List<string> FailoverProviders { get; set; } = [];

    /// <summary>La catena di default quando <see cref="FailoverProviders"/> è vuota.</summary>
    public static readonly IReadOnlyList<string> DefaultFailoverChain =
        [AiProviders.Nvidia, AiProviders.Groq, AiProviders.Gemini, AiProviders.HuggingFace];

    /// <summary>La catena EFFETTIVA (configurata, o default se vuota), deduplicata preservando l'ordine.</summary>
    public IReadOnlyList<string> EffectiveFailoverChain()
    {
        var source = FailoverProviders.Count > 0 ? (IReadOnlyList<string>)FailoverProviders : DefaultFailoverChain;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return source.Where(p => seen.Add(p)).ToList();
    }

    public string Model { get; set; } = "claude-opus-4-8";

    /// <summary>Modello per il provider Nvidia (namespace/modello del catalogo build.nvidia.com).</summary>
    public string NvidiaModel { get; set; } = "meta/llama-3.3-70b-instruct";

    /// <summary>
    /// Endpoint OpenAI-compatible del provider Nvidia. Parametrico DI PROPOSITO: qualunque
    /// piattaforma esponga lo stesso contratto (OpenRouter, endpoint self-hosted, …) potrà
    /// entrare cambiando URL e chiave, senza un client nuovo.
    /// </summary>
    public string NvidiaBaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";

    // [Fase D 2026-08-02] Tre provider in un colpo, stessa forma di Nvidia: la prova del
    // principio §1.2 del PRD. Ogni coppia Model/BaseUrl è hot-reload dal pannello.

    /// <summary>Modello per Google Gemini (layer OpenAI-compatible di Generative Language API). Id CANONICO col prefisso "models/" come lo restituisce l'elenco dell'API (verificato dal vivo 2026-08-02); il 2.5 è ritirato per le chiavi nuove — usare «Scarica modelli» nel pannello per l'elenco vero della PROPRIA chiave.</summary>
    public string GeminiModel { get; set; } = "models/gemini-3.6-flash";

    public string GeminiBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai";

    /// <summary>Modello per Groq (inferenza a bassa latenza su modelli aperti).</summary>
    public string GroqModel { get; set; } = "llama-3.3-70b-versatile";

    public string GroqBaseUrl { get; set; } = "https://api.groq.com/openai/v1";

    /// <summary>Modello per il router HuggingFace (org/nome del catalogo; il router sceglie il backend).</summary>
    public string HuggingFaceModel { get; set; } = "meta-llama/Llama-3.3-70B-Instruct";

    public string HuggingFaceBaseUrl { get; set; } = "https://router.huggingface.co/v1";

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

    /// <summary>
    /// [Fase C] Secondo parere: dopo ogni advisory riuscita, chiede la STESSA analisi anche al
    /// provider di confronto e la salva accanto (artifact separato, mai al posto). Default off:
    /// raddoppia il costo per run, e va scelto apposta.
    /// </summary>
    public bool ComparisonEnabled { get; set; }

    /// <summary>Provider del secondo parere (una voce di <see cref="AiProviders.Known"/>). Default Groq (attivo default = Nvidia; due pareri dallo stesso provider non confrontano niente e si saltano da soli).</summary>
    public string ComparisonProvider { get; set; } = AiProviders.Groq;
}

/// <summary>
/// Implementazione di <see cref="ILlmClient"/> sull'SDK ufficiale Anthropic (pacchetto <c>Anthropic</c>).
/// Usa il modello configurato (default <c>claude-opus-4-8</c>) con adaptive thinking. La API key è
/// letta esclusivamente dalla variabile d'ambiente <c>ANTHROPIC_API_KEY</c> — mai da appsettings —
/// e se manca il client è semplicemente "non configurato" (l'app parte lo stesso).
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient, IModelCatalogProvider
{
    // IOptionsMonitor (non POCO): modello/token modificabili a caldo da /admin/autonomy.
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<LlmOptions> _options;
    private readonly ILogger<AnthropicLlmClient> _logger;
    private readonly IAiKeyStore? _keyStore;
    private readonly IHttpClientFactory? _httpClientFactory;

    public AnthropicLlmClient(
        Microsoft.Extensions.Options.IOptionsMonitor<LlmOptions> options,
        ILogger<AnthropicLlmClient> logger,
        IAiKeyStore? keyStore = null,   // opzionale: i vecchi harness di test costruiscono senza store
        IHttpClientFactory? httpClientFactory = null)   // opzionale: serve solo a ListModelsAsync
    {
        _options = options;
        _logger = logger;
        _keyStore = keyStore;
        _httpClientFactory = httpClientFactory;
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

    /// <summary>
    /// <c>GET api.anthropic.com/v1/models</c> (dialetto proprio: header <c>x-api-key</c> +
    /// <c>anthropic-version</c>, non Bearer). HTTP nudo invece dell'SDK: è una GET con due header,
    /// e il contratto d'errore resta quello leggibile dal pannello.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        if (_httpClientFactory is null)
        {
            throw new InvalidOperationException("Elenco modelli Anthropic non disponibile in questo assetto (nessun HttpClientFactory).");
        }
        var apiKey = (_keyStore is not null
                ? await _keyStore.GetKeyAsync(AiProviders.Anthropic, ct)
                : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
            ?? throw new InvalidOperationException(
                "Nessuna chiave Anthropic: inseriscila in /admin/ai-supervisor (o imposta ANTHROPIC_API_KEY).");

        var http = _httpClientFactory.CreateClient(OpenAiCompatibleLlmClient.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=100");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ANTHROPIC HTTP {(int)response.StatusCode}: {(body.Length > 400 ? body[..400] : body)}");
        }

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var ids = new List<string>();
        foreach (var el in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (el.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
            {
                ids.Add(value);
            }
        }
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }
}
