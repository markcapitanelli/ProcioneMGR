using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.Agents;

/// <summary>
/// Default supervisor: no AI. It logs the run and always approves the replacement, delegating the
/// entire decision to the objective <see cref="IEnsembleComparator"/>. This is the fallback when the
/// user has not configured a Claude API key — the platform is fully operational without any AI layer.
/// </summary>
public sealed class LoggingSupervisorAgent(ILogger<LoggingSupervisorAgent> logger) : IPipelineSupervisorAgent
{
    public string Provider => "Logging";

    public Task<SupervisorJudgment> AnalyzeRunAsync(
        PipelineRun run,
        EnsembleSummary? currentEnsemble,
        EnsembleSummary? candidateEnsemble,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Supervisore (Logging): run {RunId} — corrente Sharpe {Cur:F2} ({CurLegs} gambe) vs candidato Sharpe {Cand:F2} ({CandLegs} gambe). Decisione delegata alle metriche.",
            run.Id,
            currentEnsemble?.WeightedAverageSharpe ?? 0m, currentEnsemble?.SurvivingLegs ?? 0,
            candidateEnsemble?.WeightedAverageSharpe ?? 0m, candidateEnsemble?.SurvivingLegs ?? 0);

        return Task.FromResult(new SupervisorJudgment
        {
            ApproveReplacement = true,
            Provider = Provider,
            Summary = "Nessun supervisore AI configurato. La decisione di ri-applica è basata solo su metriche oggettive.",
            AnalyzedAt = DateTime.UtcNow,
        });
    }
}
