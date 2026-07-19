using System.Net;
using System.Net.Http;
using Anthropic.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Agents;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica gli agenti supervisori del ciclo di ri-applica. Punti chiave: il Logging approva sempre
/// (delega alle metriche); il Claude usa un fake ILlmClient (nessuna rete) attraverso il guard
/// condiviso — su assenza di API key, breaker aperto o errore ricade SUBITO su "approva" (un
/// problema AI non blocca mai una sostituzione giustificata dai numeri — l'AI può solo porre un
/// veto, mai forzare); il DelegatingSupervisorAgent instrada il provider per-chiamata (hot-swap).
/// </summary>
public class SupervisorAgentTests
{
    private sealed class FakeLlm(bool configured, Func<string> respond) : ILlmClient
    {
        public int Calls { get; private set; }
        public bool IsConfigured => configured;
        public string Model => "fake-model";
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private static PipelineRun Run() => new() { Id = Guid.NewGuid(), Status = "Completed", Trigger = "Scheduled" };
    private static EnsembleSummary Sum(decimal s) => new() { WeightedAverageSharpe = s, SurvivingLegs = 2, DistinctSymbols = 2, Legs = new List<LegSummary>() };
    private static ILogger<T> Log<T>() => LoggerFactory.Create(b => { }).CreateLogger<T>();

    private static LlmCallGuard MakeGuard(ILlmClient llm, LlmOptions? options = null)
        => new(llm, (options ?? new LlmOptions()).AsMonitor(), NullLogger<LlmCallGuard>.Instance);

    private static ClaudeSupervisorAgent MakeClaude(FakeLlm llm, SupervisorAgentOptions? options = null, LlmCallGuard? guard = null)
        => new(llm, guard ?? MakeGuard(llm), (options ?? new SupervisorAgentOptions()).AsMonitor(), Log<ClaudeSupervisorAgent>());

    private static AnthropicBadRequestException BillingException() =>
        new(new HttpRequestException("Status Code: BadRequest"))
        {
            StatusCode = HttpStatusCode.BadRequest,
            ResponseBody = "Your credit balance is too low to access the Anthropic API.",
        };

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
        var agent = MakeClaude(new FakeLlm(false, () => ""), new SupervisorAgentOptions { Provider = "Claude" });
        var j = await agent.AnalyzeRunAsync(Run(), Sum(1.0m), Sum(1.5m));
        Assert.True(j.ApproveReplacement); // nessuna key → decidono le metriche
    }

    [Fact]
    public async Task Claude_OnError_FallsBackToApprove()
    {
        var agent = MakeClaude(new FakeLlm(true, () => throw new InvalidOperationException("boom")));
        var j = await agent.AnalyzeRunAsync(Run(), Sum(1.0m), Sum(1.5m));
        Assert.True(j.ApproveReplacement); // errore AI → non blocca la sostituzione
        Assert.Contains("boom", j.Summary);
    }

    [Fact]
    public async Task Claude_VetoIsHonored()
    {
        var agent = MakeClaude(new FakeLlm(true, () => """{"approveReplacement":false,"summary":"Rischio regime","concerns":["overfit"]}"""));
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

    [Fact]
    public async Task Claude_WithOpenBreaker_ApprovesImmediately_WithoutCallingTheLlm()
    {
        var llm = new FakeLlm(true, () => throw BillingException());
        var guard = MakeGuard(llm, new LlmOptions { BreakerFailureThreshold = 1, BreakerCooldownMinutes = 60 });
        var agent = MakeClaude(llm, guard: guard);

        // Primo giudizio: la chiamata fallisce (billing) e apre il breaker; fallback approva.
        var first = await agent.AnalyzeRunAsync(Run(), Sum(1.0m), Sum(1.5m));
        Assert.True(first.ApproveReplacement);
        Assert.True(guard.GetStatus().BreakerOpen);
        Assert.Equal(1, llm.Calls);

        // Secondo giudizio: breaker aperto → approva SUBITO, zero chiamate API.
        var second = await agent.AnalyzeRunAsync(Run(), Sum(1.0m), Sum(1.5m));
        Assert.True(second.ApproveReplacement);
        Assert.Contains("sospeso", second.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, llm.Calls);
    }

    [Fact]
    public async Task Delegating_SwitchesProviderPerCall_WithoutRestart()
    {
        var llm = new FakeLlm(true, () => """{"approveReplacement":true,"summary":"ok da claude"}""");
        var options = new MutableOptionsMonitor<SupervisorAgentOptions>(new SupervisorAgentOptions { Provider = "Logging" });
        var agent = new DelegatingSupervisorAgent(options,
            new LoggingSupervisorAgent(Log<LoggingSupervisorAgent>()),
            new ClaudeSupervisorAgent(llm, MakeGuard(llm), options, Log<ClaudeSupervisorAgent>()));

        Assert.Equal("Logging", agent.Provider);
        var j1 = await agent.AnalyzeRunAsync(Run(), Sum(1.0m), Sum(1.5m));
        Assert.Equal("Logging", j1.Provider);
        Assert.Equal(0, llm.Calls);

        options.CurrentValue = new SupervisorAgentOptions { Provider = "Claude" };
        Assert.Equal("Claude", agent.Provider);
        var j2 = await agent.AnalyzeRunAsync(Run(), Sum(1.0m), Sum(1.5m));
        Assert.Equal("Claude", j2.Provider);
        Assert.Equal(1, llm.Calls);
    }
}
