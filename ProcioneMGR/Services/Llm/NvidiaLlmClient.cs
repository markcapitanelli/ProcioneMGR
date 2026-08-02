using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ProcioneMGR.Services.Llm;

/// <summary>
/// Base di OGNI provider che parla il dialetto OpenAI-compatible (<c>POST
/// {base}/chat/completions</c>, Bearer, tre campi JSON). Nata come <c>NvidiaLlmClient</c> ed
/// elevata a base quando il principio §1.2 del PRD («un provider nuovo = URL+chiave, zero client
/// nuovi») è passato dalla promessa alla prova: NVIDIA, Google Gemini (layer compat), Groq e il
/// router HuggingFace differiscono SOLO per nome, base URL e modello — una sottoclasse a testa,
/// cinque righe l'una. Nessun SDK: un HttpClient nudo è meno fragile di quattro dipendenze.
///
/// <para>La chiave viene da <see cref="IAiKeyStore"/> (DB cifrato → env del provider). Timeout e
/// retry NON vivono qui: la disciplina è del <see cref="LlmCallGuard"/>, identica per ogni
/// provider — un breaker per il layer, non uno per client.</para>
/// </summary>
public abstract class OpenAiCompatibleLlmClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<LlmOptions> options,
    IAiKeyStore keyStore,
    ILogger logger) : ILlmClient
{
    /// <summary>Nome del client HTTP registrato in Program.cs (timeout largo: i modelli con reasoning sono lenti). Condiviso da tutti i provider compat.</summary>
    public const string HttpClientName = "OpenAiCompatLlm";

    /// <summary>Nome canonico del provider (una voce di <see cref="AiProviders.Known"/>).</summary>
    protected abstract string ProviderName { get; }

    /// <summary>Base URL e modello del provider, letti a OGNI chiamata (hot-reload).</summary>
    protected abstract (string BaseUrl, string Model) Endpoint(LlmOptions options);

    public bool IsConfigured => keyStore.GetCachedKey(ProviderName) is not null;

    public string Model => Endpoint(options.CurrentValue).Model;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var apiKey = await keyStore.GetKeyAsync(ProviderName, ct)
            ?? throw new InvalidOperationException(
                $"Nessuna chiave {ProviderName}: inseriscila in /admin/ai-supervisor (o imposta {AiProviders.EnvVarFor(ProviderName)}).");

        var opt = options.CurrentValue;
        var (baseUrl, model) = Endpoint(opt);
        var payload = JsonSerializer.Serialize(new
        {
            model,
            max_tokens = opt.MaxTokens,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        });

        var http = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            baseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // Il body va nell'errore (troncato): un 401/402/429 spiega la causa nel JSON, e il
            // breaker/pannello devono poterla mostrare — mai un "HTTP 4xx" muto. Il prefisso
            // "<PROVIDER> HTTP <code>:" è il contratto che LlmCallGuard.Classify sa leggere.
            throw new InvalidOperationException(
                $"{ProviderName.ToUpperInvariant()} HTTP {(int)response.StatusCode}: {(body.Length > 400 ? body[..400] : body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
        {
            // I modelli con reasoning possono esaurire i max_tokens PRIMA di scrivere la risposta
            // (il pensiero conta nel budget): un contenuto vuoto è un errore da spiegare, non da
            // restituire come advisory vuota.
            logger.LogWarning("{Provider}: risposta senza contenuto (finish_reason={Reason}).", ProviderName,
                doc.RootElement.GetProperty("choices")[0].TryGetProperty("finish_reason", out var fr) ? fr.GetString() : "?");
            throw new InvalidOperationException(
                $"{ProviderName} ha risposto senza testo (probabile budget token esaurito dal reasoning: alza Llm:MaxTokens o scegli un modello non-reasoning).");
        }

        return content;
    }
}

/// <summary>NVIDIA build.nvidia.com (<c>integrate.api.nvidia.com/v1</c>, Bearer <c>nvapi-…</c>).</summary>
public sealed class NvidiaLlmClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<LlmOptions> options,
    IAiKeyStore keyStore,
    ILogger<NvidiaLlmClient> logger) : OpenAiCompatibleLlmClient(httpClientFactory, options, keyStore, logger)
{
    protected override string ProviderName => AiProviders.Nvidia;
    protected override (string BaseUrl, string Model) Endpoint(LlmOptions options) => (options.NvidiaBaseUrl, options.NvidiaModel);
}

/// <summary>Google Gemini via layer OpenAI-compatible (<c>generativelanguage.googleapis.com/v1beta/openai</c>).</summary>
public sealed class GeminiLlmClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<LlmOptions> options,
    IAiKeyStore keyStore,
    ILogger<GeminiLlmClient> logger) : OpenAiCompatibleLlmClient(httpClientFactory, options, keyStore, logger)
{
    protected override string ProviderName => AiProviders.Gemini;
    protected override (string BaseUrl, string Model) Endpoint(LlmOptions options) => (options.GeminiBaseUrl, options.GeminiModel);
}

/// <summary>Groq (<c>api.groq.com/openai/v1</c>): inferenza a bassissima latenza su modelli aperti.</summary>
public sealed class GroqLlmClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<LlmOptions> options,
    IAiKeyStore keyStore,
    ILogger<GroqLlmClient> logger) : OpenAiCompatibleLlmClient(httpClientFactory, options, keyStore, logger)
{
    protected override string ProviderName => AiProviders.Groq;
    protected override (string BaseUrl, string Model) Endpoint(LlmOptions options) => (options.GroqBaseUrl, options.GroqModel);
}

/// <summary>Router di inferenza HuggingFace (<c>router.huggingface.co/v1</c>): molti modelli aperti dietro un endpoint solo.</summary>
public sealed class HuggingFaceLlmClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<LlmOptions> options,
    IAiKeyStore keyStore,
    ILogger<HuggingFaceLlmClient> logger) : OpenAiCompatibleLlmClient(httpClientFactory, options, keyStore, logger)
{
    protected override string ProviderName => AiProviders.HuggingFace;
    protected override (string BaseUrl, string Model) Endpoint(LlmOptions options) => (options.HuggingFaceBaseUrl, options.HuggingFaceModel);
}

/// <summary>
/// L'<see cref="ILlmClient"/> registrato: instrada OGNI chiamata al provider scelto in
/// <see cref="LlmOptions.Provider"/> (hot-reload: cambiare provider dal pannello ha effetto alla
/// chiamata successiva, senza riavvio). Tutto ciò che consuma ILlmClient — supervisore, guard,
/// worker, pannello — resta ignaro di quale provider stia parlando: è il punto dell'astrazione.
/// Con l'<see cref="ILlmClientResolver"/> (opzionale per compatibilità coi vecchi harness) i
/// provider instradabili sono TUTTI quelli noti; senza, il comportamento storico a due.
/// </summary>
public sealed class DelegatingLlmClient(
    AnthropicLlmClient anthropic,
    NvidiaLlmClient nvidia,
    IOptionsMonitor<LlmOptions> options,
    ILlmClientResolver? resolver = null) : ILlmClient
{
    private ILlmClient Active
    {
        get
        {
            var provider = options.CurrentValue.Provider;
            if (resolver?.Resolve(provider) is { } resolved)
            {
                return resolved;
            }
            return provider.Equals(AiProviders.Nvidia, StringComparison.OrdinalIgnoreCase)
                ? nvidia
                : anthropic; // default e fallback: il comportamento storico
        }
    }

    public bool IsConfigured => Active.IsConfigured;
    public string Model => Active.Model;

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct) =>
        Active.CompleteAsync(systemPrompt, userPrompt, ct);
}
