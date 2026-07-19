namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// Fotografia del "market mood" (Sentiment 2.0): composite per-mercato e per-simbolo con z-score
/// vs baseline rolling e flag contrarian agli estremi. POCO con setter pubblici DI PROPOSITO:
/// viaggia dentro AltDataOutput nel checkpoint serializzato del PipelineContext.
/// Semantica: <see cref="CompositeScore"/> è il MOOD DELLA FOLLA in [-1,+1] (positivo = folla
/// bullish); la lettura CONTRARIAN vive nei flag <see cref="Extremes"/> — un mood estremo è un
/// rischio di squeeze/svolta, non un invito a seguirlo.
/// </summary>
public sealed class SentimentSnapshot
{
    public DateTime ComputedAtUtc { get; set; }

    /// <summary>Media 24h del punteggio news di TUTTE le fonti testuali (null se nessuna notizia scorata).</summary>
    public double? NewsScore24h { get; set; }

    public double? FearGreedValue { get; set; }
    public string? FearGreedLabel { get; set; }

    /// <summary>Variazione del Fear &amp; Greed rispetto a ~7 giorni fa (null senza storico).</summary>
    public double? FearGreedDelta7d { get; set; }

    /// <summary>Mood della folla a livello mercato, [-1,+1].</summary>
    public double CompositeScore { get; set; }

    /// <summary>Flag contrarian (testo leggibile) a livello mercato + per simbolo.</summary>
    public List<string> Extremes { get; set; } = new();

    public List<SymbolSentiment> Symbols { get; set; } = new();
}

/// <summary>Mood per singolo simbolo (ticker base, es. "BTC").</summary>
public sealed class SymbolSentiment
{
    public string Symbol { get; set; } = string.Empty;

    public double? NewsScore24h { get; set; }

    /// <summary>Ultimo funding in percento (convenzione piattaforma) e relativo z-score.</summary>
    public double? FundingPercent { get; set; }
    public double? FundingZ { get; set; }

    public double? GlobalLongShortRatio { get; set; }
    public double? GlobalLongShortZ { get; set; }
    public double? TopTraderLongShortZ { get; set; }
    public double? TakerZ { get; set; }

    /// <summary>Variazione % dell'open interest (valore USDT) nelle ultime ~24h — contesto di ampiezza, MAI nel composite.</summary>
    public double? OiChange24hPercent { get; set; }

    /// <summary>Mood della folla sul simbolo, [-1,+1].</summary>
    public double Composite { get; set; }

    public List<string> Extremes { get; set; } = new();
}
