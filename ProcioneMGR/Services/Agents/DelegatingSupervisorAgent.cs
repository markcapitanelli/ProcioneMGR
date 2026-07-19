using Microsoft.Extensions.Options;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.Agents;

/// <summary>
/// L'<see cref="IPipelineSupervisorAgent"/> registrato in DI: sceglie Logging/Claude PER CHIAMATA
/// da <c>PipelineSupervisor:Provider</c> (hot-reload). Prima la scelta avveniva una volta sola al
/// boot in Program.cs: cambiare provider richiedeva un riavvio e la UI non poteva esporlo.
/// </summary>
public sealed class DelegatingSupervisorAgent(
    IOptionsMonitor<SupervisorAgentOptions> options,
    LoggingSupervisorAgent loggingAgent,
    ClaudeSupervisorAgent claudeAgent) : IPipelineSupervisorAgent
{
    private IPipelineSupervisorAgent Current =>
        string.Equals(options.CurrentValue.Provider, "Claude", StringComparison.OrdinalIgnoreCase)
            ? claudeAgent
            : loggingAgent;

    public string Provider => Current.Provider;

    public Task<SupervisorJudgment> AnalyzeRunAsync(
        PipelineRun run,
        EnsembleSummary? currentEnsemble,
        EnsembleSummary? candidateEnsemble,
        CancellationToken ct = default)
        => Current.AnalyzeRunAsync(run, currentEnsemble, candidateEnsemble, ct);
}
