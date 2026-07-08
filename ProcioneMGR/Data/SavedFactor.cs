using System.ComponentModel.DataAnnotations;

namespace ProcioneMGR.Data;

/// <summary>
/// Un fattore alpha "minato" (formulaic alpha mining, rif. <c>docs/ROADMAP-QLIB.md §1.7</c>) salvato
/// per riuso: l'espressione serializzata + la diagnostica IC su selezione e holdout. L'espressione si
/// ricostruisce in un <c>IAlphaFactor</c> (via <c>AlphaExpressionFactor</c>/<c>IAlphaFactorFactory.Create</c>
/// con nome "expr:…"), quindi è riusabile ovunque come qualunque altro fattore.
/// </summary>
public class SavedFactor
{
    public int Id { get; set; }

    /// <summary>FK verso AspNetUsers.</summary>
    [Required]
    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    /// <summary>Etichetta scelta dall'utente.</summary>
    [Required]
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Espressione alpha serializzata (S-expression), es. <c>Div(Sub($Close,Mean($Close,5)),Std($Close,20))</c>.</summary>
    [Required]
    [MaxLength(1024)]
    public string Expression { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string Symbol { get; set; } = string.Empty;

    [Required]
    [MaxLength(8)]
    public string Timeframe { get; set; } = string.Empty;

    public int ForwardHorizon { get; set; }

    /// <summary>IC (Spearman) sul periodo di selezione dove il fattore è stato scelto.</summary>
    public double SelectionIc { get; set; }

    /// <summary>IC sull'holdout mai visto: il verdetto onesto (null se non verificato).</summary>
    public double? HoldoutIc { get; set; }

    public int Observations { get; set; }

    /// <summary>Numero di nodi dell'albero (complessità).</summary>
    public int Size { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
