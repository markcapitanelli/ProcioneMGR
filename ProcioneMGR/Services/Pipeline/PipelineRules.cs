using System.Text.Json;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Deterministic decision rules used by the RecommendationStage (NO LLM: every conclusion is
/// backed by verifiable numbers). Loaded from <c>Config/pipeline_rules.json</c> under the
/// content root so the user can tune thresholds without touching code; falls back to the
/// built-in defaults when the file is missing or malformed.
/// </summary>
public sealed class PipelineRuleSet
{
    // --- Volatility classification (forecast vol / long-run vol) ---
    public double VolatilityHighThresholdRatio { get; set; } = 1.3;
    public double VolatilityLowThresholdRatio { get; set; } = 0.8;

    /// <summary>Sizing reduction (in %) applied when volatility is classified "Alta".</summary>
    public decimal HighVolSizingReductionPercent { get; set; } = 30m;

    // --- Sentiment classification (average score in [-1, 1]) ---
    public double SentimentPositiveThreshold { get; set; } = 0.15;
    public double SentimentNegativeThreshold { get; set; } = -0.15;

    // --- Regime → strategy-family bias ---
    /// <summary>Strategy names favoured when the regime label contains "Sideways"/"Choppy" (mean-reversion).</summary>
    public List<string> MeanReversionStrategies { get; set; } = new() { "BollingerMeanReversion", "RsiOversold", "Stochastic", "VwapReversion" };

    /// <summary>Strategy names favoured when the regime label contains "Trend" (trend-following).</summary>
    public List<string> TrendStrategies { get; set; } = new() { "PriceSmaCross", "EmaCross", "DonchianBreakout", "Momentum", "MacdTrend", "Supertrend" };

    /// <summary>Weight multiplier applied to legs whose family matches the current regime.</summary>
    public decimal RegimeMatchWeightMultiplier { get; set; } = 1.5m;

    // --- Validation gates (mirror the strategy-hunt discipline) ---
    public decimal MinHoldoutSharpe { get; set; } = 0.5m;
    public int MinHoldoutTrades { get; set; } = 10;
    public decimal MaxMonteCarloRiskFactor95 { get; set; } = 2.5m;

    // --- Sizing ---
    /// <summary>Fraction of full Kelly to use (0.5 = half-Kelly, the standard prudent choice).</summary>
    public decimal KellyFraction { get; set; } = 0.5m;

    /// <summary>Hard cap on per-leg sizing regardless of Kelly (safety net for small samples).</summary>
    public decimal MaxSizingPercent { get; set; } = 10m;

    /// <summary>Maximum number of ensemble legs recommended.</summary>
    public int MaxLegs { get; set; } = 3;

    /// <summary>News categories whose recent presence generates an alert.</summary>
    public List<string> AlertNewsCategories { get; set; } = new() { "Regulatory", "Security", "CentralBanks" };
}

public interface IPipelineRulesProvider
{
    /// <summary>Current rule set (re-read from disk on every call — a run reads it once at RecommendationStage time).</summary>
    PipelineRuleSet GetRules();

    /// <summary>Absolute path of the rules file (for the UI to point the user at).</summary>
    string RulesFilePath { get; }
}

public sealed class PipelineRulesProvider(IWebHostEnvironment env, ILogger<PipelineRulesProvider> logger) : IPipelineRulesProvider
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public string RulesFilePath => Path.Combine(env.ContentRootPath, "Config", "pipeline_rules.json");

    public PipelineRuleSet GetRules()
    {
        try
        {
            if (File.Exists(RulesFilePath))
            {
                var loaded = JsonSerializer.Deserialize<PipelineRuleSet>(File.ReadAllText(RulesFilePath), Json);
                if (loaded is not null) return loaded;
            }
            else
            {
                // First use: materialize the defaults so the user has a file to edit.
                Directory.CreateDirectory(Path.GetDirectoryName(RulesFilePath)!);
                File.WriteAllText(RulesFilePath, JsonSerializer.Serialize(new PipelineRuleSet(), Json));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "pipeline_rules.json non leggibile: uso i default incorporati.");
        }
        return new PipelineRuleSet();
    }
}
