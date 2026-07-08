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
