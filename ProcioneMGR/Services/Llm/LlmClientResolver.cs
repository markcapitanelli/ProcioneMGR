namespace ProcioneMGR.Services.Llm;

/// <summary>
/// Risolve un <see cref="ILlmClient"/> per NOME di provider — serve al secondo parere (Fase C),
/// che deve parlare con un provider SPECIFICO e non con quello attivo del
/// <see cref="DelegatingLlmClient"/>. Interfaccia minima al posto della DI keyed: un fake nei
/// test è una lambda, e un provider nuovo è una riga qui accanto a quella in
/// <see cref="AiProviders.Known"/>.
/// </summary>
public interface ILlmClientResolver
{
    /// <summary>Il client del provider richiesto, o null se il nome non è noto.</summary>
    ILlmClient? Resolve(string provider);
}

/// <inheritdoc cref="ILlmClientResolver"/>
public sealed class LlmClientResolver(
    AnthropicLlmClient anthropic,
    NvidiaLlmClient nvidia,
    GeminiLlmClient gemini,
    GroqLlmClient groq,
    HuggingFaceLlmClient huggingFace) : ILlmClientResolver
{
    public ILlmClient? Resolve(string provider) => provider switch
    {
        var p when string.Equals(p, AiProviders.Anthropic, StringComparison.OrdinalIgnoreCase) => anthropic,
        var p when string.Equals(p, AiProviders.Nvidia, StringComparison.OrdinalIgnoreCase) => nvidia,
        var p when string.Equals(p, AiProviders.Gemini, StringComparison.OrdinalIgnoreCase) => gemini,
        var p when string.Equals(p, AiProviders.Groq, StringComparison.OrdinalIgnoreCase) => groq,
        var p when string.Equals(p, AiProviders.HuggingFace, StringComparison.OrdinalIgnoreCase) => huggingFace,
        _ => null,
    };
}
