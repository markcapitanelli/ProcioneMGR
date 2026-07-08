using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.Agents;

/// <summary>
/// Qualitative AI supervisor of the continuous re-apply loop. Given a completed pipeline run and the
/// current vs candidate ensemble, it produces a readable judgment plus a VETO signal
/// (<see cref="SupervisorJudgment.ApproveReplacement"/>) that the scheduler ANDs with the objective
/// <see cref="IEnsembleComparator"/> verdict.
///
/// SAFETY (non-negotiable): the agent can only ever VETO a replacement the metrics already approved —
/// it can never FORCE one, never start trading, never switch to Live, never touch SafetyChecker. It
/// receives no execution/trading service in DI. When it fails or is not configured it returns
/// <c>ApproveReplacement = true</c> (defer to metrics), so an AI outage never blocks a good swap.
/// </summary>
public interface IPipelineSupervisorAgent
{
    /// <summary>Provider name for UI/telemetry ("Logging" | "Claude").</summary>
    string Provider { get; }

    Task<SupervisorJudgment> AnalyzeRunAsync(
        PipelineRun run,
        EnsembleSummary? currentEnsemble,
        EnsembleSummary? candidateEnsemble,
        CancellationToken ct = default);
}

/// <summary>The supervisor's verdict on a run + proposed ensemble swap. Advisory + a metrics-deferring veto flag only.</summary>
public sealed class SupervisorJudgment
{
    /// <summary>
    /// False = VETO the replacement even if the metrics approve it. True = no objection (the metrics
    /// decide). Default true so failures/absence never block a metrically-justified swap.
    /// </summary>
    public bool ApproveReplacement { get; set; } = true;

    /// <summary>Readable executive summary (Italian), shown in the UI.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Adjustment suggestions (proposals, never auto-applied).</summary>
    public IReadOnlyList<string> Suggestions { get; set; } = new List<string>();

    /// <summary>Concerns/risks the agent flags.</summary>
    public IReadOnlyList<string> Concerns { get; set; } = new List<string>();

    /// <summary>Internal reasoning (debug, expandable in UI).</summary>
    public string Reasoning { get; set; } = string.Empty;

    /// <summary>Provider that produced this judgment ("Logging" | "Claude").</summary>
    public string Provider { get; set; } = string.Empty;

    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Options for the supervisor agent (bound from the <c>PipelineSupervisor</c> config section).</summary>
public sealed class SupervisorAgentOptions
{
    /// <summary>"Logging" (default, no AI) or "Claude" (uses the existing ILlmClient / ANTHROPIC_API_KEY).</summary>
    public string Provider { get; set; } = "Logging";

    /// <summary>Hard timeout for a single Claude analysis; on timeout the agent falls back to "approve" (defer to metrics).</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
