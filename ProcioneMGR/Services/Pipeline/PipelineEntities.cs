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

    /// <summary>
    /// [K56, 2026-09-02] <b>Ore minime fra due run DI QUESTA configurazione</b> nella rotazione
    /// automatica. <c>0</c> = nessun limite proprio, vale il solo backoff della campagna
    /// (comportamento storico).
    ///
    /// <para><b>Perché per-configurazione e non globale.</b> La rotazione ha un solo pomello —
    /// <c>VettingCampaign.BackoffHours</c>, più <c>Campaign:RearmHours</c> — per cacce che costano
    /// misure diversissime. Al 2026-09-02, mediana per run: <b>cfg 17 = 3,7 minuti</b>,
    /// <b>cfg 19 = 43,8 minuti</b>. Dodici volte tanto, stessa cadenza. Alzare il pomello globale
    /// per rallentare la 19 rallenterebbe anche la 17, che costa un dodicesimo.</para>
    ///
    /// <para><b>Perché rallentare è quasi gratis, misurato.</b> La finestra di holdout della cfg 19
    /// è di <b>112 giorni</b> e scorre di un giorno al giorno: fra un run e il successivo — 14,1 ore
    /// mediane — entrano <b>288 candele nuove su circa 32.000</b>, cioè lo <b>0,5%</b>. Si spendono
    /// 44 minuti di calcolo per mezzo punto percentuale di dati nuovi. A 48 ore la finestra si muove
    /// dell'1,8% e il costo scende da ~35,7 a ~11 ore al mese.</para>
    ///
    /// <para><b>Ciò che NON si perde:</b> la qualità non dipende dalla cadenza ma dalla finestra, che
    /// resta la stessa. Ciò che si perde è la <i>ridondanza</i> — e la dispersione fra rimisurazioni
    /// resta comunque abbondante per il gate di stabilità (13-16 misure per ipotesi).</para>
    /// </summary>
    public int MinHoursBetweenRuns { get; set; }
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

    /// <summary>"Manual" | "Scheduled" | "Campaign" (rotazione del Campaign Planner, Fase 1) | "Event" (trigger contestuale, Fase 2).</summary>
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

    /// <summary>
    /// [J4, PRD autonomia-operativa 2026-08-25] L'universo del run mescolava più timeframe.
    ///
    /// <para>Dal 2026-08-20 <c>HoldoutValidationStage.ValidateInput</c> RIFIUTA gli universi misti
    /// (il PBO di pannello confronta Sharpe per barra su partizioni per indice, e il DSR
    /// de-annualizzava con il ppy del singolo candidato su un pannello che ne mescolava due). Ma i
    /// run già archiviati — 29 della config 8, più quelli delle altre config marcate «timeframe
    /// misti» — restavano indistinguibili dai validi e alimentavano /research, le letture della
    /// fascia grigia e ogni statistica aggregata. Questo flag li marca: scritto all'avvio del run
    /// dall'universo del contesto, backfillato per i run storici dallo SNAPSHOT (la verità al
    /// momento del run, non la config di oggi che può essere cambiata).</para>
    /// </summary>
    public bool MixedTimeframeUniverse { get; set; }
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
