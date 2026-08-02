namespace ProcioneMGR.Data;

/// <summary>
/// [AF1] Consumo LLM aggregato per giorno/provider/modello/percorso. AGGREGATO e non a eventi di
/// proposito: alla scala reale (decine di chiamate l'ora nei giorni pieni) una riga per chiamata
/// sarebbe rumore da amministrare; una riga per combinazione al giorno resta leggibile per anni.
/// Scritto solo dal LlmUsageFlushWorker del guscio (l'unico host col layer AI).
/// </summary>
public class LlmUsageRecord
{
    public int Id { get; set; }

    /// <summary>Giorno UTC (mezzanotte) a cui il consumo appartiene.</summary>
    public DateTime DayUtc { get; set; }

    /// <summary>Provider che ha SERVITO le chiamate (minuscolo, una voce di AiProviders.Known).</summary>
    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>Percorso del guard ("advisory" | "veto" | "sentiment" | "committee" | "direct").</summary>
    public string Path { get; set; } = string.Empty;

    public int Calls { get; set; }

    public long PromptTokens { get; set; }

    public long CompletionTokens { get; set; }
}
