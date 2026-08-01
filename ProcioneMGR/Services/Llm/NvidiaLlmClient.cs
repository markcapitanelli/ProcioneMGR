using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ProcioneMGR.Services.Llm;

/// <summary>
/// <see cref="ILlmClient"/> sull'API OpenAI-compatible della piattaforma NVIDIA
/// (build.nvidia.com → <c>https://integrate.api.nvidia.com/v1/chat/completions</c>, Bearer
/// <c>nvapi-…</c>). Nessun SDK: il contratto è tre campi JSON, e un HttpClient nudo è meno
/// fragile di una dipendenza in più.
///
/// <para>Il base URL viene dalle opzioni: qualunque endpoint che parli lo stesso dialetto
/// (OpenRouter, un vLLM self-hosted, …) può subentrare senza un client nuovo — è il mattone
/// del multi-provider, non un adattatore una tantum.</para>
///
/// <para>La chiave viene da <see cref="IAiKeyStore"/> (DB cifrato → env <c>NVIDIA_API_KEY</c>).
/// Timeout e retry NON vivono qui: la disciplina è del <see cref="LlmCallGuard"/>, identica per
/// ogni provider — un breaker per il layer, non uno per client.</para>
/// </summary>
public sealed class NvidiaLlmClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<LlmOptions> options,
    IAiKeyStore keyStore,
    ILogger<NvidiaLlmClient> logger) : ILlmClient
{
    /// <summary>Nome del client registrato in Program.cs (timeout largo: modelli con reasoning sono lenti).</summary>
    public const string HttpClientName = "NvidiaLlm";

    public bool IsConfigured => keyStore.GetCachedKey(AiProviders.Nvidia) is not null;

    public string Model => options.CurrentValue.NvidiaModel;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var apiKey = await keyStore.GetKeyAsync(AiProviders.Nvidia, ct)
            ?? throw new InvalidOperationException(
                "Nessuna chiave NVIDIA: inseriscila in /admin/ai-supervisor (o imposta NVIDIA_API_KEY).");

        var opt = options.CurrentValue;
        var payload = JsonSerializer.Serialize(new
        {
            model = opt.NvidiaModel,
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
            opt.NvidiaBaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // Il body va nell'errore (troncato): un 401/402/429 di NVIDIA spiega la causa nel
            // JSON, e il breaker/pannello devono poterla mostrare — mai un "HTTP 4xx" muto.
            throw new InvalidOperationException(
                $"NVIDIA HTTP {(int)response.StatusCode}: {(body.Length > 400 ? body[..400] : body)}");
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
            logger.LogWarning("NVIDIA: risposta senza contenuto (finish_reason={Reason}).",
                doc.RootElement.GetProperty("choices")[0].TryGetProperty("finish_reason", out var fr) ? fr.GetString() : "?");
            throw new InvalidOperationException(
                "NVIDIA ha risposto senza testo (probabile budget token esaurito dal reasoning: alza Llm:MaxTokens o scegli un modello non-reasoning).");
        }

        return content;
    }
}

/// <summary>
/// L'<see cref="ILlmClient"/> registrato: instrada OGNI chiamata al provider scelto in
/// <see cref="LlmOptions.Provider"/> (hot-reload: cambiare provider dal pannello ha effetto alla
/// chiamata successiva, senza riavvio). Tutto ciò che consuma ILlmClient — supervisore, guard,
/// worker, pannello — resta ignaro di quale provider stia parlando: è il punto dell'astrazione.
/// </summary>
public sealed class DelegatingLlmClient(
    AnthropicLlmClient anthropic,
    NvidiaLlmClient nvidia,
    IOptionsMonitor<LlmOptions> options) : ILlmClient
{
    private ILlmClient Active => options.CurrentValue.Provider.Equals(AiProviders.Nvidia, StringComparison.OrdinalIgnoreCase)
        ? nvidia
        : anthropic; // default e fallback: il comportamento storico

    public bool IsConfigured => Active.IsConfigured;
    public string Model => Active.Model;

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct) =>
        Active.CompleteAsync(systemPrompt, userPrompt, ct);
}
