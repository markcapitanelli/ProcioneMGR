using System.ComponentModel.DataAnnotations;

namespace ProcioneMGR.Data;

/// <summary>
/// Configurazione completa di una pagina (form di Backtest, Optimization, ...) salvata per utente:
/// preset con nome oppure "ultima configurazione usata" (Name vuoto, aggiornata a ogni Run).
/// Il contenuto è un JSON opaco definito dalla pagina stessa (ogni pagina ha il suo DTO).
/// </summary>
public class UserPageConfig
{
    public int Id { get; set; }

    /// <summary>FK verso AspNetUsers.</summary>
    [Required]
    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    /// <summary>Chiave stabile della pagina, es. "backtest", "optimization".</summary>
    [Required]
    [MaxLength(32)]
    public string PageKey { get; set; } = string.Empty;

    /// <summary>Nome del preset scelto dall'utente; stringa vuota = ultima configurazione usata.</summary>
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Configurazione serializzata (JSON opaco, schema a carico della pagina).</summary>
    [Required]
    public string ConfigJson { get; set; } = "{}";

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
