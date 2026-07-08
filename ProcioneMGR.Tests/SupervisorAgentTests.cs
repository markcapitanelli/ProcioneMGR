using Microsoft.Extensions.Logging;
using ProcioneMGR.Services.Agents;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica gli agenti supervisori del ciclo di ri-applica. Punti chiave: il Logging approva sempre
/// (delega alle metriche); il Claude usa un fake ILlmClient (nessuna rete); su assenza di API key o
/// errore ricade su "approva" (un problema AI non blocca mai una sostituzione giustificata dai
/// numeri — l'AI può solo porre un veto, mai forzare).
/// </summary>
public class SupervisorAgentTests
{
    private sealed class FakeLlm(bool configured, Func<string> respond) : ILlmClient
    {
        public bool IsConfigured => configured;
        public string Model => "fake-model";
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => Task.FromResult(respond());
    }

    private static PipelineRun Run() => new() { Id = Guid.NewGuid(), Status = "Completed", Trigger = "Scheduled" };
    private static EnsembleSummary Sum(decimal s) => new() { WeightedAverageSharpe = s, SurvivingLegs = 2, DistinctSymbols = 2, Legs = new List<LegSummary>() };
    private static ILogger<T> Log<T>() => LoggerFactory.Create(b => { }).CreateLogger<T>();

    [Fact]
    public async Task Logging_AlwaysApproves()
    {
        var agent = new LoggingSupervisorAgent(Log<LoggingSupervisorAgent>());
        var j = await agent.AnalyzeRunAsync(Run(), Sum(1.0m), Sum(1.5m));
        Assert.True(j.ApproveReplacement);
        Assert.Equal("Logging", j.Provider);
    }

    [Fact]
    public async Task Claude_NotConfigured_FallsBackToApprove()
    {
        var agent = new ClaudeSupervisorAgent(new FakeLlm(false, () => ""), new SupervisorAgentOptions { Provider = "Claude" }, Log<ClaudeSupervisorAgent>());
        var j = await agent.AnalyzeRunAsync(Run(), Sum(1.0m), Sum(1.5m));
        Assert.True(j.ApproveReplacement); // nessuna key → decidono le metriche
    }

    [Fact]
    public async Task Claude_OnError_FallsBackToApprove()
    {
        var agent = new ClaudeSupervisorAgent(new FakeLlm(true, () => throw new InvalidOperationException("boom")), new SupervisorAgentOptions(), Log<ClaudeSupervisorAgent>());
        var j = await agent.AnalyzeRunAsync(Run(), Sum(1.0m), Sum(1.5m));
        Assert.True(j.ApproveReplacement); // errore AI → non blocca la sostituzione
        Assert.Contains("boom", j.Summary);
    }

    [Fact]
    public async Task Claude_VetoIsHonored()
    {
        var agent = new ClaudeSupervisorAgent(
            new FakeLlm(true, () => """{"approveReplacement":false,"summary":"Rischio regime","concerns":["overfit"]}"""),
            new SupervisorAgentOptions(), Log<ClaudeSupervisorAgent>());
        var j = await agent.AnalyzeRunAsync(Run(), Sum(1.0m), Sum(1.5m));
        Assert.False(j.ApproveReplacement);
        Assert.Contains("Rischio regime", j.Summary);
        Assert.Single(j.Concerns);
    }

    [Fact]
    public void Parse_ToleratesSurroundingText_DefaultsApproveTrueWhenOmitted()
    {
        var j = ClaudeSupervisorAgent.Parse("blah {\"summary\":\"x\"} trailing");
        Assert.Equal("x", j.Summary);
        Assert.True(j.ApproveReplacement); // campo assente → nessun veto
    }
}
