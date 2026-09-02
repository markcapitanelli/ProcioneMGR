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
        // [K53, 2026-09-02] «embedding» → «embed». Il frammento lungo non prendeva NESSUNO dei nomi
        // veri del catalogo NVIDIA: `nvidia/embed-qa-4`, `nvidia/nv-embedqa-mistral-7b-v2`,
        // `nvidia/llama-3.2-nv-embedqa-1b-v1`, `nvidia/nemotron-3-embed-1b`,
        // `snowflake/arctic-embed-l`. Un filtro scritto sulla parola del dominio invece che sui
        // nomi che esistono davvero: sembrava coprire una categoria e non ne copriva un solo caso.
        "embed", "retriever", "reward", "nemotron-parse", "detector",
        "tts", "image", "audio", "live", "robotics", "imagen", "veo", "lyria",
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

    /// <summary>
    /// Il modello scelto per il provider, o null solo se l'elenco è vuoto/tutto non-chat.
    /// </summary>
    /// <param name="giaFunzionanti">
    /// [K53, 2026-09-02] I modelli di questo provider che <b>hanno davvero risposto</b>, dal
    /// consumo persistito (<c>LlmUsageRecords</c>, che registra solo le chiamate riuscite), dal più
    /// recente al meno recente. Se uno di essi è ancora in catalogo, vince su qualunque euristica.
    ///
    /// <para><b>Perché serve, con il caso che l'ha imposto.</b> Le preferenze qui sotto indovinano
    /// dal NOME, e il nome non dice se l'account può invocare quel modello. Per NVIDIA la regola è
    /// «llama + instruct + 70b», e nel catalogo del 2026-09-02 esiste <b>un solo</b> candidato che
    /// la soddisfa: <c>nvidia/llama-3.1-nemotron-70b-instruct</c> — misurato, risponde
    /// <c>404 Function not found for account</c>. Cioè il pilota automatico, lasciato fare,
    /// riportava la configurazione esattamente sul modello morto, ogni volta che qualcuno apriva
    /// il pannello. Un aiuto che aiuta a rompersi.</para>
    ///
    /// <para>Il consumo persistito è l'unica prova non congetturale in mano alla piattaforma: se un
    /// modello ha prodotto token, quell'account può invocarlo. Vuoto o null = nessuna prova, e si
    /// torna alle euristiche — che restano il ripiego, non la regola.</para>
    /// </param>
    public static string? Pick(
        string provider, IReadOnlyList<string> models, IReadOnlyList<string>? giaFunzionanti = null)
    {
        var chat = models
            .Where(m => !NonChatFragments.Any(f => m.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (chat.Count == 0)
        {
            return null;
        }

        // La prova batte l'indovinello: il primo che ha già risposto ED è ancora in catalogo.
        // Si confronta contro `chat` e non contro `models`, perché un modello che ha risposto una
        // volta ma è di forma non-chat (un vision, per dire) resta comunque la scelta sbagliata.
        if (giaFunzionanti is { Count: > 0 })
        {
            var provato = giaFunzionanti.FirstOrDefault(
                g => chat.Any(c => c.Equals(g, StringComparison.OrdinalIgnoreCase)));
            if (provato is not null)
            {
                return chat.First(c => c.Equals(provato, StringComparison.OrdinalIgnoreCase));
            }
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
