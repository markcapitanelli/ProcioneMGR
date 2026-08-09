namespace ProcioneMGR.Services.Regime;

public class TrainingConfiguration
{
    public string ExchangeName { get; set; } = "Binance";
    public string Symbol { get; set; } = "BTC/USDT";
    public string Timeframe { get; set; } = "1h";
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int NumberOfRegimes { get; set; } = 4;
    public int MaxIterations { get; set; } = 100;

    /// <summary>
    /// Se true, K non è fisso: si addestra il K-means per ogni K in [<see cref="MinRegimes"/>..<see cref="MaxRegimes"/>]
    /// e si sceglie quello col Silhouette Score migliore (auto-selezione di K). <see cref="NumberOfRegimes"/>
    /// viene aggiornato al K scelto. Se false si usa <see cref="NumberOfRegimes"/> così com'è (comportamento storico).
    /// </summary>
    public bool AutoSelectK { get; set; }

    /// <summary>Estremo inferiore del range di K per l'auto-selezione (min 2). Usato solo se <see cref="AutoSelectK"/>.</summary>
    public int MinRegimes { get; set; } = 2;

    /// <summary>Estremo superiore del range di K per l'auto-selezione. Usato solo se <see cref="AutoSelectK"/>.</summary>
    public int MaxRegimes { get; set; } = 6;

    /// <summary>
    /// [3.8a] Quinta feature di clustering: VolumeRatio (volume / media 20 periodi). Default OFF =
    /// comportamento storico bit-identico. ATTENZIONE dichiarata: accenderla CAMBIA le etichette dei
    /// regimi del modello riaddestrato — l'impatto sull'allocazione regime-aware va misurato, non
    /// assunto. La scelta viaggia col modello (FeatureScaling.Names), l'inference si adegua da sola.
    /// </summary>
    public bool IncludeVolumeFeature { get; set; }

    /// <summary>
    /// [3.8a/4.9] Sesta feature di clustering: breadth interna (% dei simboli /USDT sopra la propria
    /// SMA50 — "quanti partecipano al movimento"). Default OFF; stessa avvertenza del volume.
    /// Richiede dati multi-simbolo sullo stesso timeframe (il calcolo è di IMarketBreadthCalculator).
    /// </summary>
    public bool IncludeBreadthFeature { get; set; }

    /// <summary>
    /// [2.7 PRD-RISANAMENTO, 2026-08-09] Algoritmo di stima dei centroidi:
    /// <see cref="RegimeModelKinds.KMeans"/> (default, comportamento storico bit-identico) oppure
    /// <see cref="RegimeModelKinds.Jump"/> (statistical jump model C1: la persistenza entra NELLA
    /// stima, non a valle). Rispetta il contratto scritto in <see cref="JumpModel"/>: il default
    /// resta K-means finché la misura non decide — questo flag rende la misura possibile
    /// dall'app (config <c>MarketRegime:Model</c>) invece che solo dalla fase CLI di PlatformExpand.
    /// Il formato persistito NON cambia (sempre centroidi + scaling): l'inference nearest-centroid
    /// + smoothing resta identica per entrambi.
    /// </summary>
    public string Model { get; set; } = RegimeModelKinds.KMeans;

    /// <summary>λ del jump model (penalità per salto di stato). Usato solo con Model=Jump.
    /// 0 = degenera in K-means; il valore va tarato con la misura, mai assunto.</summary>
    public double JumpLambda { get; set; } = 20.0;
}

/// <summary>Nomi degli algoritmi di regime selezionabili da <c>MarketRegime:Model</c>.</summary>
public static class RegimeModelKinds
{
    public const string KMeans = "KMeans";
    public const string Jump = "Jump";

    /// <summary>Parsing tollerante: sconosciuto o vuoto ⇒ KMeans (mai rompere il training per un typo in config).</summary>
    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Jump, StringComparison.OrdinalIgnoreCase) ? Jump : KMeans;
}

/// <summary>
/// Modello di regime addestrato. È anche l'entità EF (persistita nel DB).
/// I centroidi sono nello spazio NORMALIZZATO; per l'inference si standardizza la feature
/// con <see cref="FeatureScalingJson"/> e si assegna al centroide euclideo più vicino.
/// </summary>
public class RegimeModel
{
    public int Id { get; set; }
    public string ExchangeName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public DateTime TrainedAtUtc { get; set; }
    public DateTime TrainingDataFrom { get; set; }
    public DateTime TrainingDataTo { get; set; }
    public int NumberOfRegimes { get; set; }

    /// <summary>JSON: array K × 8 di centroidi normalizzati.</summary>
    public string CentroidsJson { get; set; } = "[]";

    /// <summary>JSON: <see cref="FeatureScaling"/> (mean/std per feature).</summary>
    public string FeatureScalingJson { get; set; } = "{}";

    /// <summary>JSON: List&lt;RegimeProfile&gt;.</summary>
    public string RegimeProfilesJson { get; set; } = "[]";

    public double SilhouetteScore { get; set; }
    public bool IsActive { get; set; } = true;
}

public class RegimeProfile
{
    public int RegimeId { get; set; }
    public string SuggestedLabel { get; set; } = string.Empty;
    public int SampleCount { get; set; }
    public double MeanVolatility { get; set; }
    public double MeanTrendStrength { get; set; }
    public double MeanTrendDirection { get; set; }
    public double MeanVolumeRatio { get; set; }
    public double MeanAtrNormalized { get; set; }
    public double MeanRsiLevel { get; set; }
    public double MeanDistanceFromMa { get; set; }
    public Dictionary<string, StrategyPerformanceInRegime> StrategyPerformances { get; set; } = new();
}

public class StrategyPerformanceInRegime
{
    public string StrategyName { get; set; } = string.Empty;
    public decimal AverageSharpe { get; set; }
    public decimal AverageReturn { get; set; }
    public decimal WinRate { get; set; }
    public int TotalTrades { get; set; }
}
