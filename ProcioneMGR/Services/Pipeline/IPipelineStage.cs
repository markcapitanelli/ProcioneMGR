namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// A composable phase of the autonomous pipeline. Stages are thin orchestrators over the
/// existing platform services (they never re-implement them): each one reads its inputs from
/// the <see cref="PipelineContext"/>, calls the underlying services, and writes its output
/// back into the context for the following stages.
///
/// Contract:
///  - stateless: any per-run state lives in the context, so stages can be transient;
///  - deterministic: same context + config + data → same output (seeded randomness only);
///  - no look-ahead: date ranges from <see cref="PipelineContext.Ranges"/> must be respected —
///    the holdout range is verdict-only and may be read exclusively by validation stages.
/// </summary>
public interface IPipelineStage
{
    /// <summary>Technical name, stable across versions (used in StageConfig.Type and dependencies).</summary>
    string Name { get; }

    string DisplayName { get; }

    /// <summary>One-line description shown in the UI editor.</summary>
    string Description { get; }

    /// <summary>Default position in a new configuration.</summary>
    int DefaultOrder { get; }

    /// <summary>
    /// Dependency groups: each group requires at least one of its stages to be enabled and
    /// ordered before this one. Checked by the engine before the run starts (DAG validation).
    /// </summary>
    IReadOnlyList<StageDependency> Dependencies { get; }

    /// <summary>Editable parameters (defaults + hints) for the UI gear editor.</summary>
    IReadOnlyList<StageParameterDefinition> ParameterDefinitions { get; }

    /// <summary>
    /// Runtime prerequisite check against the actual context (e.g. "no candidates to
    /// validate"). Returns null when OK, otherwise a human-readable error.
    /// </summary>
    string? ValidateInput(PipelineContext ctx);

    /// <summary>Executes the stage, writing its output into the context.</summary>
    Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct);

    /// <summary>
    /// Builds the textual + metric summary from the output this stage wrote into the context.
    /// Called by the engine right after <see cref="ExecuteAsync"/> completes.
    /// </summary>
    StageSummary Summarize(PipelineContext ctx);
}
