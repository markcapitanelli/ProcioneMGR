using System.ComponentModel.DataAnnotations;

namespace ProcioneMGR.Data;

/// <summary>
/// Una candela OHLCV (Open/High/Low/Close/Volume) di mercato.
///
/// Questa tabella e' progettata per ospitare ENORMI volumi time-series (storico
/// di mercato), in netto contrasto con le poche righe delle tabelle Identity.
/// Per questo motivo:
///  - prezzi in <see cref="decimal"/> (precisione esatta, niente errori float);
///  - volume in <see cref="decimal"/> (gestisce sia asset interi che frazionari/crypto);
///  - timestamp in UTC (<see cref="DateTime"/>) per coerenza globale;
///  - indice composto Univoco (Symbol, Timeframe, TimestampUtc) configurato via
///    Fluent API nel <see cref="ApplicationDbContext"/> per query time-series veloci
///    e per impedire candele duplicate.
/// </summary>
public class OhlcvData
{
    /// <summary>Chiave surrogata. long perche' la tabella crescera' oltre i limiti di int.</summary>
    public long Id { get; set; }

    /// <summary>Strumento di mercato, es. "BTCUSDT", "AAPL".</summary>
    [Required]
    [MaxLength(32)]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Intervallo della candela, es. "1m", "5m", "1h", "1d".</summary>
    [Required]
    [MaxLength(8)]
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>Apertura della candela in UTC (Unix epoch normalizzato a DateTime UTC).</summary>
    public DateTime TimestampUtc { get; set; }

    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }

    /// <summary>Volume scambiato nel periodo.</summary>
    public decimal Volume { get; set; }

    // [T0.3 roadmap macchina-ricerca] I campi che le klines Binance consegnano GRATIS in ogni
    // download e che il parsing scartava. Nullable di proposito: le candele storiche non ancora
    // ri-scaricate restano valide, e null distingue "non disponibile" da "zero" — un imbalance
    // calcolato su uno zero finto sarebbe un segnale inventato.

    /// <summary>Controvalore scambiato (quote asset, es. USDT). Binance k[7], Bitget k[6]. Null = non raccolto.</summary>
    public decimal? QuoteVolume { get; set; }

    /// <summary>Numero di trade nella candela (Binance k[8]). Abilita dimensione media del trade e trade-bars. Null = non raccolto.</summary>
    public long? TradeCount { get; set; }

    /// <summary>
    /// Volume base comprato da TAKER (Binance k[9]): l'order flow aggressivo — chi attraversa lo
    /// spread. L'imbalance TakerBuyVolume/Volume è la feature order-flow di T3.8b. Null = non raccolto.
    /// </summary>
    public decimal? TakerBuyVolume { get; set; }

    /// <summary>Controvalore comprato da taker (Binance k[10]). Null = non raccolto.</summary>
    public decimal? TakerBuyQuoteVolume { get; set; }
}
