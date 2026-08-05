namespace ProcioneMGR.Data;

/// <summary>
/// [G4] L'analisi a posteriori di UN'operazione chiusa in perdita (o del ritiro di una corsia).
///
/// <para>Tabella propria e non <c>PipelineArtifact</c>: quelli sono agganciati a un <c>RunId</c>
/// che un trade non ha. E non il journal della flotta: un post-mortem non è una decisione
/// dell'orchestratore, e piegare quello schema si sarebbe pagato dopo.</para>
///
/// <para><b>Confine</b>: questa riga è testo e una classificazione. Non entra in nessun percorso
/// di esecuzione; l'unico consumatore oltre la pagina è il <c>Context</c> della domanda al
/// comitato AF3 — che resta a menù chiuso, con quorum e default deterministico.</para>
/// </summary>
public class TradePostMortem
{
    public int Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int LaneId { get; set; }

    /// <summary>Il trade analizzato (<c>TradeRecords.Id</c>). Indice unico: un trade, un post-mortem.</summary>
    public int TradeRecordId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public string StrategyId { get; set; } = string.Empty;

    /// <summary>Perdita percentuale del trade (negativa), copiata qui per poter interrogare senza join.</summary>
    public decimal PnlPercent { get; set; }

    /// <summary>
    /// Una voce di <see cref="ProcioneMGR.Services.Llm.Narration.PostMortemCauses"/> — MAI testo
    /// libero. È il solo campo che viaggia verso il comitato.
    /// </summary>
    public string Cause { get; set; } = string.Empty;

    /// <summary>
    /// Chi ha scelto la causa: "rules" = calcolata dal codice (aritmetica, nessuna AI interpellata)
    /// | "ai" = scelta dall'AI dentro il menù | "default" = AI non disponibile o fuori menù.
    /// </summary>
    public string Source { get; set; } = "rules";

    /// <summary>I fatti oggettivi su cui si è ragionato (JSON), per poter rileggere il verdetto fra un mese.</summary>
    public string FactsJson { get; set; } = "{}";

    /// <summary>La prosa dell'AI. Vuota quando l'AI non ha risposto: la causa deterministica resta comunque.</summary>
    public string Narrative { get; set; } = string.Empty;

    /// <summary>Il modello che ha davvero risposto, vuoto se non è stata interpellata nessuna AI.</summary>
    public string ModelUsed { get; set; } = string.Empty;
}
