namespace ProcioneMGR.Services.Llm;

/// <summary>
/// Esito della supervisione AI di un run del pipeline: un parere LEGGIBILE per l'utente, più
/// suggerimenti sui parametri di caccia e le decisioni che richiedono conferma umana. È solo
/// advisory: non contiene azioni eseguibili, non avvia trading, non tocca SafetyChecker.
/// Persistito come <c>PipelineArtifact</c> (Kind="LlmAdvisory") — nessuna nuova tabella.
/// </summary>
public sealed class SupervisorAdvisory
{
    /// <summary>Riepilogo esecutivo in italiano, pronto da mostrare all'utente.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Aggiustamenti proposti ai parametri di caccia (proposte, non modifiche applicate).</summary>
    public List<ParameterSuggestion> ParameterSuggestions { get; set; } = new();

    /// <summary>Decisioni che l'AI segnala come da confermare esplicitamente dall'utente.</summary>
    public List<string> DecisionsForUser { get; set; } = new();

    /// <summary>"bassa" | "media" | "alta".</summary>
    public string Confidence { get; set; } = "media";

    /// <summary>Modello usato (tracciabilità).</summary>
    public string ModelUsed { get; set; } = string.Empty;

    /// <summary>True se l'advisory è il risultato di un errore (LLM non raggiungibile, parsing fallito…).</summary>
    public bool IsError { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Un singolo suggerimento di aggiustamento parametro (proposta, mai applicata in automatico).</summary>
public sealed class ParameterSuggestion
{
    public string Parameter { get; set; } = string.Empty;
    public string CurrentOrObserved { get; set; } = string.Empty;
    public string Suggested { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
}
