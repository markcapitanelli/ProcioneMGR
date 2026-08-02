namespace ProcioneMGR.Data;

/// <summary>
/// [AF2] Il journal della Queen Bee: UNA riga per decisione che porta informazione (assegnazione,
/// ritiro, proposta di fascia grigia, blocco motivato). Persistito perché l'autonomia senza
/// tracciabilità è un racconto: il pannello in /admin/autonomy mostra ESATTAMENTE ciò che
/// l'orchestratore ha deciso, quando, con che motivo e con quale esito — dry-run compreso.
/// </summary>
public class OrchestratorDecision
{
    public int Id { get; set; }

    public DateTime AtUtc { get; set; }

    /// <summary>"Assign" | "Retire" | "ProposeGrey" | "Blocked".</summary>
    public string Kind { get; set; } = string.Empty;

    public int? LaneId { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>Chi ha scelto: "rules" (il default deterministico) | "committee" (AF3) | "default" (comitato fallito → default).</summary>
    public string Source { get; set; } = "rules";

    public string Reason { get; set; } = string.Empty;

    /// <summary>[AF3] I voti del comitato, uno per provider (JSON). "[]" quando il comitato non è stato interpellato.</summary>
    public string VotesJson { get; set; } = "[]";

    /// <summary>True se l'azione è stata ESEGUITA (false in dry-run o su errore).</summary>
    public bool Applied { get; set; }

    /// <summary>True se la decisione è stata presa col dry-run acceso (solo journal, mai azione).</summary>
    public bool DryRun { get; set; }

    public string? Error { get; set; }
}
