namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Orchestrates pipeline runs: validates the stage DAG, executes the enabled stages in order
/// in the background, checkpoints the context to the DB after every stage, and exposes a live
/// status for the UI. One run at a time (the underlying engines are heavy).
/// </summary>
public interface IPipelineEngine
{
    /// <summary>Starts a new run of a saved configuration. Throws if a run is already in progress.</summary>
    Task<Guid> StartRunAsync(int configurationId, string trigger = "Manual", string? userId = null, CancellationToken ct = default);

    /// <summary>
    /// Resumes a Paused/Failed/Cancelled run from its last checkpoint: already-completed
    /// stages are skipped, the rest re-execute against the restored context.
    /// </summary>
    Task<Guid> ResumeRunAsync(Guid runId, string? userId = null, CancellationToken ct = default);

    /// <summary>Requests a graceful pause: the run stops at the NEXT stage boundary (checkpoint intact).</summary>
    void RequestPause(Guid runId);

    /// <summary>Cancels the in-progress run (checkpoint of completed stages is preserved).</summary>
    void Cancel(Guid runId);

    /// <summary>Live status of the in-progress run, or null when idle.</summary>
    PipelineLiveStatus? GetLiveStatus();

    /// <summary>
    /// Validates a configuration's stage list against the DAG rules (dependencies satisfied
    /// by enabled stages ordered earlier). Returns the list of problems (empty = valid).
    /// </summary>
    List<string> ValidateConfiguration(IReadOnlyList<StageConfig> stages);
}

/// <summary>Catalog of all available stages (prototypes for the UI, factory for the engine).</summary>
public interface IPipelineStageCatalog
{
    /// <summary>Prototype instances (read Name/DisplayName/ParameterDefinitions — do not execute).</summary>
    IReadOnlyList<IPipelineStage> Prototypes { get; }

    /// <summary>Resolves a fresh stage instance by technical name from the given scope.</summary>
    IPipelineStage Create(IServiceProvider scopedProvider, string name);

    /// <summary>Default stage list for a brand-new configuration (all stages, default order/params).</summary>
    List<StageConfig> DefaultStages();
}
