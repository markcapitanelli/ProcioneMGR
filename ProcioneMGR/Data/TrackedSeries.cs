using System.ComponentModel.DataAnnotations;

namespace ProcioneMGR.Data;

/// <summary>
/// Una serie di mercato (Exchange + Symbol + Timeframe) che il sistema mantiene
/// aggiornata automaticamente in background. E' una watchlist GLOBALE: i dati OHLCV
/// non sono per-utente, quindi nemmeno la lista delle serie tracciate lo e'.
/// </summary>
public class TrackedSeries
{
    public int Id { get; set; }

    public ExchangeName Exchange { get; set; }

    /// <summary>Simbolo canonico "BASE/QUOTE", es. "BTC/USDT".</summary>
    [Required]
    [MaxLength(32)]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Timeframe canonico, es. "1h".</summary>
    [Required]
    [MaxLength(8)]
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>Se false, il worker la salta.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Ultima sincronizzazione riuscita (UTC), null se mai sincronizzata.</summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>Esito sintetico dell'ultima sincronizzazione (per la UI).</summary>
    [MaxLength(256)]
    public string? LastSyncStatus { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
