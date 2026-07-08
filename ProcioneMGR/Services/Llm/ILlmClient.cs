namespace ProcioneMGR.Services.Llm;

/// <summary>
/// Astrazione minimale su un LLM testuale. Esiste per un solo motivo: isolare l'SDK Anthropic
/// dietro un'interfaccia, così <see cref="PipelineSupervisor"/> è testabile con un fake e nessun
/// test tocca la rete. Nessuna capacità oltre "prompt → testo": il layer AI è advisory puro.
/// </summary>
public interface ILlmClient
{
    /// <summary>True se il client ha le credenziali per operare (env <c>ANTHROPIC_API_KEY</c> presente).</summary>
    bool IsConfigured { get; }

    /// <summary>Modello configurato (per tracciabilità nell'advisory).</summary>
    string Model { get; }

    /// <summary>Esegue una singola completion e restituisce il testo concatenato dei blocchi di risposta.</summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);
}
