namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// A saved, reusable pipeline configuration ("recipe"): universe, date ranges, and the ordered
/// list of stages with their parameters. JSON columns keep the schema stable while stages and
/// parameters evolve (same pattern as EnsembleState / SavedStrategy.ParametersJson).
/// </summary>
public class PipelineConfiguration
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Id of the IdentityUser that owns the configuration.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Exchange the whole pipeline reads data from.</summary>
    public string ExchangeName { get; set; } = "Binance";

    /// <summary>JSON: List&lt;SeriesSpec&gt;.</summary>
    public string UniverseJson { get; set; } = "[]";

    /// <summary>JSON: PipelineDateRanges.</summary>
    public string DateRangesJson { get; set; } = "{}";

    /// <summary>JSON: List&lt;StageConfig&gt;.</summary>
    public string StagesJson { get; set; } = "[]";

    public decimal InitialCapital { get; set; } = 10_000m;

    /// <summary>Seed for deterministic runs.</summary>
    public int Seed { get; set; } = 42;

    /// <summary>"Paper" | "Live" | "Disabled". Live never bypasses SafetyChecker / manual confirms.</summary>
    public string ExecutionMode { get; set; } = "Paper";

    /// <summary>Standard 5-field cron expression (e.g. "0 3 * * *" = every day at 03:00 UTC), evaluated by <see cref="PipelineSchedulerWorker"/>. Null/empty = no automatic schedule.</summary>
    public string? Schedule { get; set; }

    /// <summary>Master on/off switch for automatic scheduling, independent of whether <see cref="Schedule"/> is set — lets the user pause automation without losing the expression.</summary>
    public bool ScheduleEnabled { get; set; }

    /// <summary>
    /// Next due UTC timestamp per <see cref="Schedule"/>, maintained by <see cref="PipelineSchedulerWorker"/>.
    /// Null means "due now" (never scheduled yet, or schedule just changed) — the worker computes
    /// a real value on the next tick. Deliberately NOT paired with LastRunAt/LastRunStatus fields:
    /// those would duplicate what <see cref="PipelineRun"/> already records (StartedAt/Status/ErrorLog,
    /// queryable by ConfigurationId) and could drift out of sync with it; the UI reads the most
    /// recent PipelineRun for "last run" info instead of a denormalized copy.
    /// </summary>
    public DateTime? NextRunAt { get; set; }
}

/// <summary>
/// One execution of a configuration. The context snapshot is the checkpoint: it is rewritten
/// after every completed stage, so a Failed/Cancelled/Paused run can resume from the last
/// completed stage instead of starting over.
/// </summary>
public class PipelineRun
{
    public Guid Id { get; set; }
    public int ConfigurationId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>"Running" | "Completed" | "Failed" | "Cancelled" | "Paused".</summary>
    public string Status { get; set; } = "Running";

    /// <summary>"Manual" | "Scheduled" | "Event".</summary>
    public string Trigger { get; set; } = "Manual";

    /// <summary>JSON: the serializable part of PipelineContext (checkpoint, updated per stage).</summary>
    public string ContextSnapshotJson { get; set; } = "{}";

    /// <summary>JSON: List&lt;StageSummary&gt; (denormalized copy for fast history queries).</summary>
    public string StageSummariesJson { get; set; } = "[]";

    /// <summary>Executive conclusion produced by the RecommendationStage.</summary>
    public string Conclusion { get; set; } = string.Empty;

    /// <summary>JSON: PipelineRecommendation.</summary>
    public string RecommendationJson { get; set; } = "{}";

    public string? ErrorLog { get; set; }
}

/// <summary>
/// Large per-stage artifacts (equity curves, trade lists, importances) kept OUT of the run
/// row so the history table stays fast to query.
/// </summary>
public class PipelineArtifact
{
    public int Id { get; set; }
    public Guid RunId { get; set; }
    public string StageName { get; set; } = string.Empty;

    /// <summary>"EquityCurve" | "TradeList" | "FeatureImportance" | "RegimeProfile" | ...</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>JSON payload (shape depends on Kind).</summary>
    public string PayloadJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }
}
