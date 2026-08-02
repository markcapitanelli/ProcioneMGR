namespace ProcioneMGR.Services.Llm;

/// <summary>
/// Sceglie in automatico un modello di CHAT sensato dall'elenco che l'API restituisce per la
/// chiave (richiesta del proprietario 2026-08-02: «i modelli vengano scaricati in automatico e
/// uno venga scelto in automatico»). Funzione PURA: niente rete, niente stato — l'elenco arriva
/// da <see cref="IModelCatalogProvider"/> e la scelta è riproducibile e testabile.
///
/// <para>Strategia: (1) si scartano gli id palesemente non-chat (embedding, tts, immagini,
/// audio, video, robotica…) — un catalogo Gemini reale ne è pieno; (2) si prova una lista di
/// preferenze ORDINATE per provider (dal modello di lavoro consigliato in giù); (3) a parità di
/// preferenza vince l'id ordinalmente più alto, che nelle famiglie versionate coincide con la
/// versione più recente (gemini-3.6 &gt; gemini-2.0); (4) se niente combacia, il primo id
/// sopravvissuto al filtro — MAI null se l'elenco non è vuoto: un pilota automatico che si
/// arrende non è un pilota automatico.</para>
/// </summary>
public static class ModelAutoSelector
{
    /// <summary>Frammenti che marcano un modello NON adatto alla chat testuale del layer.</summary>
    private static readonly string[] NonChatFragments =
    [
        "embedding", "tts", "image", "audio", "live", "robotics", "imagen", "veo", "lyria",
        "aqa", "whisper", "guard", "moderation", "rerank", "ocr", "deep-research",
        "computer-use", "translate", "clip", "banana", "realtime", "-exp",
    ];

    /// <summary>Preferenze ordinate per provider: il primo predicato che trova candidati vince.</summary>
    private static IReadOnlyList<Func<string, bool>> PreferencesFor(string provider) => provider switch
    {
        var p when p.Equals(AiProviders.Gemini, StringComparison.OrdinalIgnoreCase) =>
        [
            m => m.Contains("gemini", StringComparison.OrdinalIgnoreCase) && m.Contains("flash", StringComparison.OrdinalIgnoreCase)
                 && !m.Contains("preview", StringComparison.OrdinalIgnoreCase) && !m.Contains("lite", StringComparison.OrdinalIgnoreCase)
                 && !m.Contains("latest", StringComparison.OrdinalIgnoreCase),
            m => m.Contains("gemini", StringComparison.OrdinalIgnoreCase) && m.Contains("flash", StringComparison.OrdinalIgnoreCase)
                 && !m.Contains("preview", StringComparison.OrdinalIgnoreCase),
            m => m.Contains("gemini", StringComparison.OrdinalIgnoreCase) && m.Contains("pro", StringComparison.OrdinalIgnoreCase),
        ],
        var p when p.Equals(AiProviders.Groq, StringComparison.OrdinalIgnoreCase) =>
        [
            m => m.Contains("llama", StringComparison.OrdinalIgnoreCase) && m.Contains("versatile", StringComparison.OrdinalIgnoreCase),
            m => m.Contains("llama", StringComparison.OrdinalIgnoreCase) && m.Contains("70b", StringComparison.OrdinalIgnoreCase),
            m => m.Contains("llama", StringComparison.OrdinalIgnoreCase),
        ],
        var p when p.Equals(AiProviders.Nvidia, StringComparison.OrdinalIgnoreCase) =>
        [
            m => m.Contains("llama", StringComparison.OrdinalIgnoreCase) && m.Contains("instruct", StringComparison.OrdinalIgnoreCase)
                 && m.Contains("70b", StringComparison.OrdinalIgnoreCase),
            m => m.Contains("llama", StringComparison.OrdinalIgnoreCase) && m.Contains("instruct", StringComparison.OrdinalIgnoreCase),
        ],
        var p when p.Equals(AiProviders.HuggingFace, StringComparison.OrdinalIgnoreCase) =>
        [
            m => m.Contains("meta-llama/", StringComparison.OrdinalIgnoreCase) && m.Contains("instruct", StringComparison.OrdinalIgnoreCase),
            m => m.Contains("instruct", StringComparison.OrdinalIgnoreCase),
        ],
        var p when p.Equals(AiProviders.Anthropic, StringComparison.OrdinalIgnoreCase) =>
        [
            m => m.Contains("opus", StringComparison.OrdinalIgnoreCase),
            m => m.Contains("sonnet", StringComparison.OrdinalIgnoreCase),
            m => m.Contains("claude", StringComparison.OrdinalIgnoreCase),
        ],
        _ => [],
    };

    /// <summary>Il modello scelto per il provider, o null solo se l'elenco è vuoto/tutto non-chat.</summary>
    public static string? Pick(string provider, IReadOnlyList<string> models)
    {
        var chat = models
            .Where(m => !NonChatFragments.Any(f => m.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (chat.Count == 0)
        {
            return null;
        }

        foreach (var preference in PreferencesFor(provider))
        {
            var candidates = chat.Where(preference).ToList();
            if (candidates.Count > 0)
            {
                // Ordinale più alto = versione più recente nelle famiglie versionate.
                return candidates.OrderByDescending(m => m, StringComparer.OrdinalIgnoreCase).First();
            }
        }
        return chat[0];
    }
}
