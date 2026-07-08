using System.ComponentModel.DataAnnotations;

namespace ProcioneMGR.Data;

/// <summary>
/// Configurazione di strategia salvata da un utente, riutilizzabile in /backtest.
/// I parametri sono serializzati in JSON (Dictionary&lt;string, decimal&gt;).
/// </summary>
public class SavedStrategy
{
    public int Id { get; set; }

    /// <summary>FK verso AspNetUsers.</summary>
    [Required]
    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    /// <summary>Nome scelto dall'utente, es. "Il mio EMA veloce".</summary>
    [Required]
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Nome tecnico della strategia, es. "EmaCross".</summary>
    [Required]
    [MaxLength(32)]
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>Parametri serializzati: JSON di Dictionary&lt;string, decimal&gt;.</summary>
    [Required]
    public string ParametersJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True se la configurazione proviene da un'ottimizzazione walk-forward (Fase 5).</summary>
    public bool IsOptimized { get; set; }

    public DateTime? OptimizationDate { get; set; }

    /// <summary>Sharpe out-of-sample medio dell'ottimizzazione che ha prodotto questi parametri.</summary>
    public decimal? OptimizationSharpe { get; set; }
}
