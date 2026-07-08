using System.ComponentModel.DataAnnotations;

namespace ProcioneMGR.Data;

/// <summary>
/// Un elemento di dato alternativo (cap. 3): oggi solo notizie via RSS, pensata per essere
/// generica (stesso spirito di <c>TrackedSeries</c> per l'OHLCV) così da poter accogliere in
/// futuro altre fonti (social, on-chain) senza cambiare schema.
/// </summary>
public class AltDataPoint
{
    public int Id { get; set; }

    /// <summary>Data di pubblicazione (dalla fonte), UTC.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>"CoinDesk" | "Cointelegraph" | "TheBlock" | "Decrypt" | ...</summary>
    [Required]
    [MaxLength(32)]
    public string Source { get; set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    public string? Summary { get; set; }

    [MaxLength(1024)]
    public string? Url { get; set; }

    /// <summary>"Regulatory" | "Security" | "Institutional" | "Other" — da <c>NewsImpactClassifier</c>.</summary>
    [MaxLength(32)]
    public string Category { get; set; } = "Other";

    /// <summary>Simboli rilevanti individuati nel testo (JSON array di stringhe, es. ["BTC","ETH"]).</summary>
    public string SymbolsJson { get; set; } = "[]";

    /// <summary>Punteggio di sentiment in [-1,+1], null finché non calcolato da un <c>ISentimentScorer</c>.</summary>
    public decimal? SentimentScore { get; set; }

    /// <summary>Chiave univoca per evitare duplicati fra sync successive dello stesso feed (Source+Url).</summary>
    [MaxLength(1024)]
    public string DedupeKey { get; set; } = string.Empty;
}
