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

    /// <summary>
    /// [K51, PRD autonomia-piena — Fase 3, 2026-09-02] <b>Che fine ha fatto questa decisione.</b>
    ///
    /// <para><b>Perché non bastava <see cref="Applied"/>.</b> Quel booleano portava <b>tre</b>
    /// significati diversi, e chi leggeva il journal non poteva distinguerli: <i>esito</i>
    /// dell'azione (<c>Applied = error is null</c>), <i>intento</i> (le proposte grigie scrivono
    /// <c>true</c> perché «la proposta È l'azione») e <i>rifiuto</i> (i rami di gate scrivono
    /// <c>false</c> — e per giunta con <c>DryRun = true</c> cablato anche a dry-run spento, dove
    /// quel campo finiva per significare «non ho agito» invece di «ero in prova»).</para>
    ///
    /// <para><b>E ne mancava un quarto, che è quello che serviva.</b> Il 2026-08-31 due schieramenti
    /// su quattro e quattro arresti su quattro sono avvenuti <b>senza lasciare riga</b>: lo stato
    /// «è stato deciso di toccare la corsia N e non si sa come sia finita» non era esprimibile, e
    /// per questo era invisibile. <see cref="DecisionOutcome.Unknown"/> lo dice.</para>
    ///
    /// <para><c>Applied</c> resta, ed è ora una <b>vista derivata</b>: i consumatori esistenti —
    /// il badge del pannello e <c>SourceVerdictBackfill</c>, che sceglie il run di provenienza fra
    /// le <c>Assign</c> applicate — continuano a leggere ciò che leggevano.</para>
    /// </summary>
    public string Outcome { get; set; } = DecisionOutcome.Applied;

    /// <summary>True se la decisione è stata presa col dry-run acceso (solo journal, mai azione).</summary>
    public bool DryRun { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// [K51] I quattro stati in cui una decisione della flotta può trovarsi, più quello che mancava.
/// Stringhe e non enum a database: il journal è letto anche da SQL a mano, e un intero non si legge.
/// </summary>
public static class DecisionOutcome
{
    /// <summary>
    /// Scritta PRIMA di toccare la corsia. Se resta così, il processo è morto a metà: è
    /// l'informazione che il 2026-08-31 non esisteva, ed è il motivo per cui questo campo esiste.
    /// </summary>
    public const string Intended = "Intended";

    /// <summary>L'azione è avvenuta.</summary>
    public const string Applied = "Applied";

    /// <summary>L'azione è stata tentata ed è fallita: <c>Error</c> dice come.</summary>
    public const string Failed = "Failed";

    /// <summary>
    /// Non si è nemmeno tentato, per una regola: dry-run, corsia non autorizzata, budget del tick,
    /// guardia. È diverso da «fallita», e finora le due cose stavano sullo stesso <c>false</c>.
    /// </summary>
    public const string Refused = "Refused";

    /// <summary>
    /// Un intento rimasto aperto oltre il tempo in cui poteva chiudersi: <b>non si sa</b> se
    /// l'azione sia avvenuta. Non si promuove mai a <c>Applied</c> per somiglianza — sarebbe una
    /// deduzione presentata come misura, la trappola già pagata in questo progetto.
    /// </summary>
    public const string Unknown = "Unknown";
}
