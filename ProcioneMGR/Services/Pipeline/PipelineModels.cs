using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Discovery;
using ProcioneMGR.Services.Regime;

namespace ProcioneMGR.Services.Pipeline;

// ============================================================================
// Configuration DTOs (serialized as JSON inside the PipelineConfiguration entity)
// ============================================================================

/// <summary>One (symbol, timeframe) entry of the pipeline universe.</summary>
public sealed class SeriesSpec
{
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
}

/// <summary>
/// Date ranges of a pipeline run. Selection = where every decision is allowed to look;
/// Holdout = verdict-only, never used for any choice (same discipline as the strategy-hunt
/// campaigns: the holdout exists to catch overfitting, so nothing may peek at it).
/// </summary>
public sealed class PipelineDateRanges
{
    public DateTime SelectionFrom { get; set; }
    public DateTime SelectionTo { get; set; }
    public DateTime HoldoutFrom { get; set; }
    public DateTime HoldoutTo { get; set; }
}

/// <summary>Per-stage configuration inside a pipeline configuration (JSON column).</summary>
public sealed class StageConfig
{
    /// <summary>Technical stage name, matches <see cref="IPipelineStage.Name"/>.</summary>
    public string Type { get; set; } = string.Empty;

    public int Order { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Stage-specific parameters as strings (invariant culture); typed access via
    /// <see cref="StageConfigExtensions"/>. Kept as strings so the JSON round-trips
    /// losslessly and the UI can edit any parameter generically.
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public static class StageConfigExtensions
{
    public static decimal GetDecimal(this StageConfig cfg, string key, decimal fallback)
        => cfg.Parameters.TryGetValue(key, out var raw)
           && decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    public static int GetInt(this StageConfig cfg, string key, int fallback)
        => cfg.Parameters.TryGetValue(key, out var raw)
           && int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    public static bool GetBool(this StageConfig cfg, string key, bool fallback)
        => cfg.Parameters.TryGetValue(key, out var raw) && bool.TryParse(raw, out var v) ? v : fallback;

    public static string GetString(this StageConfig cfg, string key, string fallback)
        => cfg.Parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw : fallback;

    /// <summary>Comma-separated list parameter ("A,B,C"); empty string = empty list.</summary>
    public static List<string> GetList(this StageConfig cfg, string key)
        => cfg.Parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : new List<string>();
}

/// <summary>Definition of a stage parameter, for the generic gear-icon editor in the UI.</summary>
public sealed record StageParameterDefinition(string Key, string Label, string DefaultValue, string Hint);

/// <summary>
/// Costi di trading applicati ai backtest della pipeline, letti una volta dallo <see cref="StageConfig"/>
/// e replicati su OGNI <see cref="BacktestConfiguration"/> di valutazione dei candidati. I default
/// rispecchiano il venue reale (Bitget): fee taker (conservativa) + slippage realistico + funding dei
/// perpetual. Il <b>funding</b> in particolare era assente (default 0 in BacktestConfiguration): senza,
/// una strategia che tiene posizioni attraverso le finestre di funding appare più redditizia di quanto
/// sarà live. La validazione gira a leva 1, ma il rapporto funding/PnL è leva-invariante: valida quindi
/// correttamente l'edge al netto del funding.
/// </summary>
public readonly record struct PipelineCosts(decimal SlippagePercent, decimal FeePercent, decimal FundingRatePercentPer8h)
{
    public const decimal DefaultSlippagePercent = 0.05m;
    public const decimal DefaultFeePercent = 0.1m;                 // Bitget taker ~0.06%; 0.1 tenuto conservativo
    public const decimal DefaultFundingRatePercentPer8h = 0.01m;  // funding "neutro" storico dei perpetual

    public static PipelineCosts FromConfig(StageConfig config) => new(
        config.GetDecimal("slippagePercent", DefaultSlippagePercent),
        config.GetDecimal("feePercent", DefaultFeePercent),
        config.GetDecimal("fundingRatePercentPer8h", DefaultFundingRatePercentPer8h));

    /// <summary>Applica i costi a una configurazione di backtest (in-place) e la restituisce, per l'uso fluido.</summary>
    public BacktestConfiguration ApplyTo(BacktestConfiguration cfg)
    {
        cfg.SlippagePercent = SlippagePercent;
        cfg.FeePercent = FeePercent;
        cfg.FundingRatePercentPer8h = FundingRatePercentPer8h;
        return cfg;
    }

    /// <summary>Parametri UI condivisi dei costi, da innestare nelle ParameterDefinitions di ogni stage.</summary>
    public static IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("slippagePercent", "Slippage per fill (%)", "0.05", "attrito realistico su ogni eseguito"),
        new("feePercent", "Commissione per lato (%)", "0.1", "taker Bitget ~0.06%; 0.1 conservativo"),
        new("fundingRatePercentPer8h", "Funding perpetual (%/8h)", "0.01", "costo di mantenimento dei perp; 0.01 neutro storico"),
    ];
}

/// <summary>
/// A dependency group: the stage requires AT LEAST ONE of the listed stages to be enabled
/// and ordered before it (e.g. HoldoutValidation needs StrategyDiscovery OR MlModelTraining).
/// </summary>
public sealed record StageDependency(IReadOnlyList<string> AnyOf)
{
    public static StageDependency On(params string[] stages) => new(stages);
}

// ============================================================================
// PipelineContext — the in-memory state threaded through the stages
// ============================================================================

/// <summary>
/// Lazy candle loader shared by all stages of a run. Candles live in the DB and are NOT part
/// of the checkpoint snapshot (they would dwarf it); a resumed run reloads them on demand.
/// </summary>
public interface IPipelineCandleCache
{
    Task<IReadOnlyList<OhlcvData>> GetAsync(string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>
/// Transient state of a pipeline run. Everything except <see cref="Candles"/> and
/// <see cref="Log"/> is JSON-serializable: the engine snapshots it to the DB after every
/// completed stage (checkpoint), so a run can resume from the last completed stage.
/// </summary>
public sealed class PipelineContext
{
    public Guid RunId { get; set; }

    // ---- Input (from the configuration, frozen at run start) ----
    public string ExchangeName { get; set; } = "Binance";
    public List<SeriesSpec> Universe { get; set; } = new();
    public PipelineDateRanges Ranges { get; set; } = new();
    public decimal InitialCapital { get; set; } = 10_000m;

    /// <summary>Seed of the whole run: same seed + config + data = same output.</summary>
    public int Seed { get; set; } = 42;

    /// <summary>Owner of the run (used e.g. to persist SavedMlModel rows, which require a user FK).</summary>
    public string? UserId { get; set; }

    /// <summary>"Paper" | "Live" | "Disabled" — from the configuration; consumed by ExecutionPlanStage.</summary>
    public string ExecutionMode { get; set; } = "Paper";

    // ---- Stage outputs (all serializable) ----
    public DataIngestionOutput? DataStatus { get; set; }
    public AltDataOutput? AltData { get; set; }
    public FeatureSelectionOutput? Features { get; set; }
    public RegimeOutput? Regimes { get; set; }
    public VolatilityOutput? Volatility { get; set; }
    public PairsOutput? Pairs { get; set; }
    public MlTrainingOutput? MlTraining { get; set; }
    public List<DiscoveryCandidate> Candidates { get; set; } = new();
    public List<ValidatedCandidate> Validated { get; set; } = new();
    public EnsembleProposal? Ensemble { get; set; }
    public RiskAssessment? Risk { get; set; }
    public NewsImpactOutput? NewsImpact { get; set; }
    public PipelineRecommendation? Recommendation { get; set; }
    public ExecutionPlan? Plan { get; set; }

    public List<StageSummary> StageSummaries { get; set; } = new();

    // ---- Runtime-only services (NOT serialized) ----
    [System.Text.Json.Serialization.JsonIgnore]
    public IPipelineCandleCache Candles { get; set; } = null!;

    [System.Text.Json.Serialization.JsonIgnore]
    public Action<string>? Log { get; set; }

    public void LogLine(string message) => Log?.Invoke(message);

    /// <summary>First series of the universe: the "primary" one for single-series stages (regime, vol, news impact).</summary>
    public SeriesSpec PrimarySeries => Universe.Count > 0 ? Universe[0] : new SeriesSpec { Symbol = "BTC/USDT", Timeframe = "1h" };
}

// ============================================================================
// Stage output DTOs
// ============================================================================

public sealed class SeriesDataStatus
{
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public int CandleCount { get; set; }
    public DateTime? FirstUtc { get; set; }
    public DateTime? LastUtc { get; set; }
    public bool CoversSelection { get; set; }
    public bool CoversHoldout { get; set; }
}

public sealed class DataIngestionOutput
{
    public List<SeriesDataStatus> Series { get; set; } = new();
    public long CandlesIngested { get; set; }
}

public sealed class AltDataOutput
{
    public int InsertedCount { get; set; }
    public int NewsLast24h { get; set; }
    public double AvgSentimentLast24h { get; set; }

    /// <summary>
    /// Snapshot composite del market mood (Sentiment 2.0): per-mercato e per-simbolo, con z-score
    /// e flag contrarian. Nullable per compatibilità: i checkpoint dei run vecchi non ce l'hanno,
    /// e uno snapshot assente non deve mai far fallire lo stage.
    /// </summary>
    public ProcioneMGR.Services.Sentiment.SentimentSnapshot? Snapshot { get; set; }
}

public sealed class FactorIcSummary
{
    public string FactorName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public double InformationCoefficient { get; set; }
    public double RollingIcMean { get; set; }
    public double InformationRatio { get; set; }
    public int Observations { get; set; }

    /// <summary>t-statistic dell'IC con SE Newey-West (robusta all'overlap dei forward-return). |t| ≳ 2 ≈ significativo.</summary>
    public double IcTStatistic { get; set; }

    public bool Selected { get; set; }
}

public sealed class FeatureSelectionOutput
{
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public int ForwardHorizon { get; set; }
    public List<FactorIcSummary> Factors { get; set; } = new();

    /// <summary>Names of the top-K factors kept as ML features.</summary>
    public List<string> SelectedFactorNames { get; set; } = new();
}

public sealed class RegimeOutput
{
    public int CurrentRegimeId { get; set; } = -1;
    public string CurrentRegimeLabel { get; set; } = string.Empty;
    public double SilhouetteScore { get; set; }
    public bool TrainedNewModel { get; set; }
    public List<RegimeProfile> Profiles { get; set; } = new();
}

public sealed class VolatilityOutput
{
    public string Symbol { get; set; } = string.Empty;
    public double Omega { get; set; }
    public double Alpha { get; set; }
    public double Beta { get; set; }
    public double Persistence { get; set; }

    /// <summary>Current per-period conditional volatility (stddev, not variance).</summary>
    public double CurrentVolatility { get; set; }

    /// <summary>Long-run per-period volatility implied by the model.</summary>
    public double LongRunVolatility { get; set; }

    /// <summary>Forecast per-period volatility 24 steps ahead.</summary>
    public double ForecastVolatility24 { get; set; }

    /// <summary>"Bassa" / "Media" / "Alta" vs the long-run level (thresholds from pipeline rules).</summary>
    public string Level { get; set; } = "Media";

    /// <summary>
    /// Gradi di libertà ν stimati con innovazioni Student-t (null se il fit di coda non è disponibile).
    /// ν basso = code grasse. Rif. audit 2026-07 §4.
    /// </summary>
    public double? TailDegreesOfFreedom { get; set; }

    /// <summary>
    /// Mossa avversa all'1% (VaR di coda) prevista a orizzonte, consapevole delle code grasse
    /// (quantile Student-t su σ previsto). Come frazione di prezzo, sempre ≥ del corrispettivo gaussiano.
    /// Serve da distanza di stop prudente per l'operatore.
    /// </summary>
    public double ForecastTailMove99 { get; set; }
}

public sealed class PairScreenResult
{
    public string SymbolY { get; set; } = string.Empty;
    public string SymbolX { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public double AdfStatistic { get; set; }
    public bool IsCointegrated { get; set; }
    public double HedgeRatio { get; set; }
    public int AlignedCandles { get; set; }
}

public sealed class PairsOutput
{
    public List<PairScreenResult> Pairs { get; set; } = new();
    public int CointegratedCount { get; set; }
}

public sealed class MlTrainingOutput
{
    public string ModelType { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public int TrainRows { get; set; }
    public int TestRows { get; set; }
    public int CvFolds { get; set; }

    /// <summary>Pearson correlation between prediction and target on the temporal test split.</summary>
    public double TestCorrelation { get; set; }
    public List<FeatureImportanceDto> FeatureImportances { get; set; } = new();

    /// <summary>Id of the persisted SavedMlModel (null if persistence was disabled or training failed the quality bar).</summary>
    public int? SavedMlModelId { get; set; }
}

public sealed record FeatureImportanceDto(string FeatureName, double Importance);

/// <summary>A discovery candidate enriched with the holdout verdict and robustness metrics.</summary>
public sealed class ValidatedCandidate
{
    public string StrategyName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public Dictionary<string, decimal> Parameters { get; set; } = new();

    // Selection-phase metrics (walk-forward)
    public decimal WalkForwardOosSharpe { get; set; }
    public decimal SelectionSharpe { get; set; }
    public decimal SelectionReturn { get; set; }
    public decimal SelectionMaxDrawdown { get; set; }
    public int SelectionTrades { get; set; }

    // Holdout verdict (never used for selection)
    public decimal HoldoutSharpe { get; set; }
    public decimal HoldoutReturn { get; set; }
    public decimal HoldoutMaxDrawdown { get; set; }
    public int HoldoutTrades { get; set; }
    public decimal HoldoutProfitFactor { get; set; }

    public bool Survived { get; set; }
    public string? RejectReason { get; set; }

    // Gate anti-overfitting (López de Prado), calcolato in HoldoutValidationStage sull'intero batch.
    /// <summary>Deflated Sharpe del candidato (probabilità che l'edge holdout sia reale dopo N tentativi). null = non calcolabile.</summary>
    public double? DeflatedSharpe { get; set; }
    /// <summary>Probability of Backtest Overfitting del PANNELLO di candidati (comune a tutti). null = non calcolabile.</summary>
    public double? PanelPbo { get; set; }

    // Robustness (filled by RobustnessProbeStage on survivors)
    public decimal MonteCarloRiskFactor95 { get; set; }
    public decimal MonteCarloDrawdown95 { get; set; }
    public decimal KellyFraction { get; set; }

    /// <summary>
    /// Kelly EMPIRICO sui rendimenti dei trade (distribuzione osservata, senza ipotesi di normalità):
    /// cattura le code grasse e di norma è ≤ del Kelly binario. Vedi <see cref="Risk.KellyCalculator.EmpiricalKelly"/>.
    /// </summary>
    public decimal EmpiricalKelly { get; set; }

    /// <summary>Metà del MINIMO tra Kelly binario ed empirico: sizing prudente e robusto alle code grasse.</summary>
    public decimal HalfKelly { get; set; }
    public string BestStopVariant { get; set; } = "base";

    /// <summary>Identity key for dictionary lookups (EnsembleAssembly/RiskSizing) and log/UI display. See <see cref="PipelineCandidateKey"/>.</summary>
    public string Key => PipelineCandidateKey.Build(StrategyName, Symbol, Timeframe, Parameters);
}

/// <summary>
/// Shared identity-key builder for pipeline candidates/legs. Classic strategies from
/// StrategyDiscoveryStage produce at most ONE confirmed parameter set per
/// (strategy,symbol,timeframe), so the short form is already unique there — but
/// CreativeDiscoveryStage can confirm MULTIPLE distinct specs of the SAME meta-strategy (e.g.
/// two different "Composite" rules) on the SAME pair, which collided under the old short key
/// (a real bug caught live: `ToDictionary` throwing "same key already added", then a SECOND
/// bug where a call site rebuilt the short key inline instead of reusing this method, silently
/// failing every lookup). A short deterministic parameter fingerprint is appended whenever
/// parameters exist, so every distinct spec gets its own key — used by BOTH
/// <see cref="ValidatedCandidate.Key"/> and <see cref="ProposedLeg.Key"/> so the two always
/// agree, instead of each computing its own string.
/// </summary>
public static class PipelineCandidateKey
{
    public static string Build(string strategyName, string symbol, string timeframe, Dictionary<string, decimal> parameters)
        => parameters.Count == 0
            ? $"{strategyName} {symbol} {timeframe}"
            : $"{strategyName} {symbol} {timeframe} #{Fingerprint(parameters)}";

    private static string Fingerprint(Dictionary<string, decimal> parameters)
    {
        var canonical = string.Join(";", parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash)[..8];
    }
}

public sealed class ProposedLeg
{
    public string StrategyName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public Dictionary<string, decimal> Parameters { get; set; } = new();
    public decimal WeightPercent { get; set; }
    public decimal SizingPercent { get; set; }

    /// <summary>
    /// Stop variant validated in the walk-forward robustness probe ("base" | "SLx" | "TRAILx" —
    /// see <see cref="Stages.RobustnessProbeStage.ApplyVariant"/>). Carried here so the live
    /// wiring (Pipeline.razor's ApplyRecommendationAsync) can translate it into the
    /// EnsembleStrategy's StopLossPercent/TrailingStopPercent instead of leaving the validated
    /// stop stuck in the DisplayName string.
    /// </summary>
    public string BestStopVariant { get; set; } = "base";

    /// <summary>
    /// Holdout metrics of the originating <see cref="ValidatedCandidate"/> (verdict-only, never
    /// used for any selection decision — carried here purely so Pipeline.razor's
    /// ApplyRecommendationAsync can populate EnsembleStrategy.Expected* for the decay monitor).
    /// </summary>
    public decimal HoldoutSharpe { get; set; }
    public decimal HoldoutProfitFactor { get; set; }
    public decimal HoldoutMaxDrawdown { get; set; }

    /// <summary>
    /// Holdout trade count of the originating candidate — carried as the effective sample size behind
    /// the leg's Sharpe so the auto-reapply comparator can test a swap's statistical significance
    /// (<see cref="Ensemble.EnsembleSummary.Observations"/>). Verdict-only, never a selection input.
    /// </summary>
    public int HoldoutTrades { get; set; }

    /// <summary>Same identity key as the originating <see cref="ValidatedCandidate"/> — use this for lookups, never rebuild it inline.</summary>
    public string Key => PipelineCandidateKey.Build(StrategyName, Symbol, Timeframe, Parameters);
}

public sealed class EnsembleProposal
{
    public List<ProposedLeg> Legs { get; set; } = new();
    public string Method { get; set; } = "EqualWeight";
    public string? Note { get; set; }
}

public sealed class RiskAssessment
{
    public decimal AverageHalfKelly { get; set; }
    public decimal AverageRiskFactor95 { get; set; }

    /// <summary>System shutdown guard: stop everything if drawdown exceeds this % (MC-derived).</summary>
    public decimal ShutdownDrawdownPercent { get; set; }
    public decimal SuggestedStopLossPercent { get; set; }

    /// <summary>Multiplier applied to sizing because of the volatility level (1 = no adjustment).</summary>
    public decimal VolatilitySizingFactor { get; set; } = 1m;
    public List<string> Notes { get; set; } = new();
}

public sealed class CategoryImpactDto
{
    public string Category { get; set; } = string.Empty;
    public int Observations { get; set; }
    public double AvgReturn24hPercent { get; set; }
}

public sealed class NewsImpactOutput
{
    public string ReferenceSymbol { get; set; } = string.Empty;
    public List<CategoryImpactDto> ByCategory { get; set; } = new();
    public List<string> Alerts { get; set; } = new();
}

public sealed class RecommendationRiskLimits
{
    public decimal HalfKellyPercent { get; set; }
    public decimal RiskFactor95 { get; set; }
    public decimal ShutdownDrawdownPercent { get; set; }
    public decimal StopLossPercent { get; set; }
}

public sealed class PipelineRecommendation
{
    public string RegimeLabel { get; set; } = "sconosciuto";
    public string VolatilityLabel { get; set; } = "sconosciuta";
    public string SentimentLabel { get; set; } = "neutro";

    /// <summary>Composite di market mood [-1,+1] (Sentiment 2.0); null nei run senza snapshot (compat).</summary>
    public double? SentimentComposite { get; set; }

    /// <summary>Fear &amp; Greed Index 0-100 al momento del run; null senza snapshot.</summary>
    public double? FearGreedValue { get; set; }

    /// <summary>Flag contrarian del mood (estremi F&amp;G, funding/posizionamento a |z|≥soglia).</summary>
    public List<string> SentimentExtremes { get; set; } = new();
    public int CandidatesEvaluated { get; set; }
    public int Survivors { get; set; }
    public string BestCandidate { get; set; } = "nessuno";
    public List<ProposedLeg> EnsembleLegs { get; set; } = new();
    public RecommendationRiskLimits RiskLimits { get; set; } = new();
    public List<string> Alerts { get; set; } = new();
    public List<string> SuggestedActions { get; set; } = new();

    /// <summary>The rendered template (the "Conclusion" persisted on the run).</summary>
    public string FullText { get; set; } = string.Empty;
}

public sealed class PlannedAction
{
    public string Description { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public string StrategyName { get; set; } = string.Empty;
    public decimal SizingPercent { get; set; }
}

public sealed class ExecutionPlan
{
    /// <summary>"Paper" | "Live" | "Disabled" — mirrors the configuration's ExecutionMode.</summary>
    public string Mode { get; set; } = "Paper";
    public List<PlannedAction> Actions { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

// ============================================================================
// Stage summaries + live status
// ============================================================================

public enum StageStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
}

public sealed class StageSummary
{
    public string StageName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Order { get; set; }
    public StageStatus Status { get; set; }
    public DateTime StartedUtc { get; set; }
    public TimeSpan Duration { get; set; }
    public string Text { get; set; } = string.Empty;
    public Dictionary<string, decimal> Metrics { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>Live view of the run in progress, polled by the UI (same pattern as /trading).</summary>
public sealed class PipelineLiveStatus
{
    public Guid RunId { get; set; }
    public int ConfigurationId { get; set; }
    public string ConfigurationName { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public string? CurrentStage { get; set; }
    public List<StageSummary> Stages { get; set; } = new();
    public List<string> RecentLog { get; set; } = new();
    public bool PauseRequested { get; set; }
}
