using System.ComponentModel.DataAnnotations;

namespace ProcioneMGR.Data;

/// <summary>
/// Un punto di una serie numerica di "market mood" (Sentiment 2.0): Fear &amp; Greed, long/short
/// ratio, taker buy/sell, open interest, funding. Tabella slim separata da <see cref="AltDataPoint"/>
/// (che è event-shaped: Title/Url/DedupeKey) perché queste sono serie DENSE per-metrica/per-simbolo
/// su cui si calcolano baseline rolling e z-score. La dedupe è l'indice unico composito
/// (Source, Metric, Symbol, TimestampUtc) + un pre-filtro applicativo nel sync service.
/// </summary>
public class SentimentMetricPoint
{
    public long Id { get; set; }

    /// <summary>Timestamp del punto (dalla fonte), UTC.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>"FearGreed" | "BinanceFutures" | ... (vedi <see cref="SentimentMetricSources"/>).</summary>
    [Required]
    [MaxLength(32)]
    public string Source { get; set; } = string.Empty;

    /// <summary>Nome della metrica (vedi <see cref="SentimentMetrics"/>).</summary>
    [Required]
    [MaxLength(48)]
    public string Metric { get; set; } = string.Empty;

    /// <summary>
    /// Ticker base ("BTC", "ETH"); stringa VUOTA = mercato intero (es. Fear &amp; Greed).
    /// Non-nullable di proposito: in Postgres i NULL sono distinti negli indici unici e la
    /// dedupe sui punti market-wide smetterebbe di funzionare.
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Valore della metrica. Convenzioni: Fear &amp; Greed 0-100; ratio così come arrivano dalla
    /// fonte; funding in PERCENTO (×100, convenzione della piattaforma).
    /// </summary>
    public decimal Value { get; set; }
}

/// <summary>Nomi delle fonti di metriche sentiment (colonna Source).</summary>
public static class SentimentMetricSources
{
    public const string FearGreed = "FearGreed";
    public const string BinanceFutures = "BinanceFutures";
}

/// <summary>Nomi delle metriche sentiment (colonna Metric).</summary>
public static class SentimentMetrics
{
    /// <summary>Fear &amp; Greed Index di alternative.me, 0 (extreme fear) - 100 (extreme greed).</summary>
    public const string FearGreedIndex = "FearGreedIndex";

    /// <summary>Rapporto account long/short di tutti gli account (Binance globalLongShortAccountRatio).</summary>
    public const string GlobalLongShortRatio = "GlobalLongShortRatio";

    /// <summary>Rapporto POSIZIONI long/short dei top trader (Binance topLongShortPositionRatio).</summary>
    public const string TopTraderLongShortRatio = "TopTraderLongShortRatio";

    /// <summary>Rapporto volume taker buy/sell (Binance takerlongshortRatio).</summary>
    public const string TakerBuySellRatio = "TakerBuySellRatio";

    /// <summary>Open interest in contratti (Binance openInterestHist.sumOpenInterest).</summary>
    public const string OpenInterest = "OpenInterest";

    /// <summary>Open interest in USDT (Binance openInterestHist.sumOpenInterestValue).</summary>
    public const string OpenInterestValue = "OpenInterestValue";

    /// <summary>Funding rate in percento (×100), come il resto della piattaforma.</summary>
    public const string FundingRate = "FundingRate";
}
