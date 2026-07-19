using System.Net;
using System.Net.Http;
using System.Text.Json;
using Anthropic.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica il layer AI di supervisione (SOLO advisory): l'LLM è sostituito da un fake, così nessun
/// test tocca la rete. Copre parsing, persistenza come PipelineArtifact, idempotenza per-run, il
/// percorso d'errore PERMANENTE (che persiste un advisory di errore) e quello TRANSITORIO (che NON
/// persiste nulla: il run resta pendente e viene ritentato quando il problema — es. credito API —
/// rientra), più la pulizia manuale del backlog di advisory in errore.
/// </summary>
[Collection("Postgres")]
public class PipelineSupervisorTests
{
    private readonly PostgresFixture _pg;

    public PipelineSupervisorTests(PostgresFixture pg) => _pg = pg;

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class FakeLlmClient(Func<string> respond) : ILlmClient
    {
        public Func<string> Respond { get; set; } = respond;
        public string? LastUserPrompt { get; private set; }
        public int Calls { get; private set; }
        public bool IsConfigured => true;
        public string Model => "fake-model";
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            Calls++;
            LastUserPrompt = userPrompt;
            return Task.FromResult(Respond());
        }
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<(NotificationSeverity Severity, string Title)> Sent { get; } = new();
        public Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Sent.Add((severity, title));
            return Task.CompletedTask;
        }
    }

    private static AnthropicBadRequestException BillingException() =>
        new(new HttpRequestException("Status Code: BadRequest"))
        {
            StatusCode = HttpStatusCode.BadRequest,
            ResponseBody = """{"error":{"message":"Your credit balance is too low to access the Anthropic API."}}""",
        };

    private (IDbContextFactory<ApplicationDbContext> factory, ServiceProvider sp) MakeDb()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_pg.CreateDatabase()));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        return (factory, sp);
    }

    private static async Task<Guid> SeedCompletedRunAsync(IDbContextFactory<ApplicationDbContext> factory, DateTime? completedAt = null)
    {
        var runId = Guid.NewGuid();
        await using var db = await factory.CreateDbContextAsync();
        db.PipelineRuns.Add(new PipelineRun
        {
            Id = runId,
            ConfigurationId = 1,
            StartedAt = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Unspecified),
            CompletedAt = completedAt ?? new DateTime(2026, 7, 5, 0, 30, 0, DateTimeKind.Unspecified),
            Status = "Completed",
            Trigger = "Scheduled",
            Conclusion = "Ensemble proposto su 2 leg.",
            RecommendationJson = """{"RegimeLabel":"trend","VolatilityLabel":"alta","Survivors":2,"CandidatesEvaluated":40}""",
        });
        await db.SaveChangesAsync();
        return runId;
    }

    private static PipelineSupervisor MakeSupervisor(
        IDbContextFactory<ApplicationDbContext> factory, ILlmClient llm,
        LlmOptions? options = null, INotifier? notifier = null)
    {
        var monitor = (options ?? new LlmOptions()).AsMonitor();
        var guard = new LlmCallGuard(llm, monitor, NullLogger<LlmCallGuard>.Instance);
        return new(factory, llm, guard, monitor,
            LoggerFactory.Create(b => { }).CreateLogger<PipelineSupervisor>(), metrics: null, notifier: notifier);
    }

    [Fact]
    public async Task SuperviseRunAsync_ParsesJson_AndPersistsAdvisoryArtifact()
    {
        var (factory, sp) = MakeDb();
        try
        {
            var runId = await SeedCompletedRunAsync(factory);
            var llm = new FakeLlmClient(() => """
                Ecco la mia analisi:
                {"summary":"Regime trend, pochi sopravvissuti.","confidence":"alta",
                 "parameterSuggestions":[{"parameter":"SurvivorThreshold","currentOrObserved":"2/40","suggested":"alzare a 3","rationale":"tasso di sopravvivenza basso"}],
                 "decisionsForUser":["Confermare l'aumento del capitale sul leg migliore"]}
                Fine.
                """);
            var supervisor = MakeSupervisor(factory, llm);

            var did = await supervisor.SuperviseRunAsync(runId, CancellationToken.None);
            Assert.True(did);

            await using var db = await factory.CreateDbContextAsync();
            var artifact = await db.PipelineArtifacts.SingleAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.Advisory);
            var advisory = JsonSerializer.Deserialize<SupervisorAdvisory>(artifact.PayloadJson)!;

            Assert.False(advisory.IsError);
            Assert.Equal("alta", advisory.Confidence);
            Assert.Equal("fake-model", advisory.ModelUsed);
            Assert.Single(advisory.ParameterSuggestions);
            Assert.Equal("SurvivorThreshold", advisory.ParameterSuggestions[0].Parameter);
            Assert.Single(advisory.DecisionsForUser);
            Assert.Contains("trend", advisory.Summary);
        }
        finally { sp.Dispose(); }
    }

    [Fact]
    public async Task SuperviseRunAsync_IsIdempotentPerRun()
    {
        var (factory, sp) = MakeDb();
        try
        {
            var runId = await SeedCompletedRunAsync(factory);
            var calls = 0;
            var llm = new FakeLlmClient(() => { calls++; return """{"summary":"ok","confidence":"media"}"""; });
            var supervisor = MakeSupervisor(factory, llm);

            Assert.True(await supervisor.SuperviseRunAsync(runId, CancellationToken.None));
            Assert.False(await supervisor.SuperviseRunAsync(runId, CancellationToken.None)); // già presente

            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(1, await db.PipelineArtifacts.CountAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.Advisory));
            Assert.Equal(1, calls); // l'LLM è stato chiamato una sola volta
        }
        finally { sp.Dispose(); }
    }

    [Fact]
    public async Task SuperviseRunAsync_OnLlmFailure_PersistsErrorAdvisory()
    {
        var (factory, sp) = MakeDb();
        try
        {
            var runId = await SeedCompletedRunAsync(factory);
            var llm = new FakeLlmClient(() => throw new InvalidOperationException("boom"));
            var supervisor = MakeSupervisor(factory, llm);

            Assert.True(await supervisor.SuperviseRunAsync(runId, CancellationToken.None));

            await using var db = await factory.CreateDbContextAsync();
            var artifact = await db.PipelineArtifacts.SingleAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.Advisory);
            var advisory = JsonSerializer.Deserialize<SupervisorAdvisory>(artifact.PayloadJson)!;
            Assert.True(advisory.IsError);
            Assert.Contains("boom", advisory.Summary);
        }
        finally { sp.Dispose(); }
    }

    [Fact]
    public void ParseAdvisory_ToleratesSurroundingText_AndNormalizesConfidence()
    {
        var advisory = PipelineSupervisor.ParseAdvisory("blah {\"summary\":\"x\",\"confidence\":\"FANTASIA\"} trailing");
        Assert.Equal("x", advisory.Summary);
        Assert.Equal("media", advisory.Confidence); // valore non valido → default
    }

    [Fact]
    public async Task SuperviseRunAsync_OnRetryableFailure_PersistsNothing_SoTheRunIsRetried()
    {
        var (factory, sp) = MakeDb();
        try
        {
            var runId = await SeedCompletedRunAsync(factory);
            var llm = new FakeLlmClient(() => throw BillingException());
            var supervisor = MakeSupervisor(factory, llm, new LlmOptions { BreakerFailureThreshold = 3 });

            Assert.False(await supervisor.SuperviseRunAsync(runId, CancellationToken.None));
            Assert.False(await supervisor.SuperviseRunAsync(runId, CancellationToken.None)); // ritentato davvero

            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(0, await db.PipelineArtifacts.CountAsync(a => a.RunId == runId));
            Assert.Equal(2, llm.Calls);
        }
        finally { sp.Dispose(); }
    }

    [Fact]
    public async Task SuperviseRunAsync_WithBreakerOpen_SkipsWithoutCallingTheLlm()
    {
        var (factory, sp) = MakeDb();
        try
        {
            var runId = await SeedCompletedRunAsync(factory);
            var llm = new FakeLlmClient(() => throw BillingException());
            var supervisor = MakeSupervisor(factory, llm, new LlmOptions { BreakerFailureThreshold = 1, BreakerCooldownMinutes = 60 });

            Assert.False(await supervisor.SuperviseRunAsync(runId, CancellationToken.None)); // apre il breaker
            Assert.False(await supervisor.SuperviseRunAsync(runId, CancellationToken.None)); // skip: breaker aperto

            Assert.Equal(1, llm.Calls);
            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(0, await db.PipelineArtifacts.CountAsync(a => a.RunId == runId));
        }
        finally { sp.Dispose(); }
    }

    [Fact]
    public async Task SuperviseRunAsync_ForceProbe_RecoversAndWritesRealAdvisory()
    {
        var (factory, sp) = MakeDb();
        try
        {
            var runId = await SeedCompletedRunAsync(factory);
            var llm = new FakeLlmClient(() => throw BillingException());
            var supervisor = MakeSupervisor(factory, llm, new LlmOptions { BreakerFailureThreshold = 1, BreakerCooldownMinutes = 60 });

            Assert.False(await supervisor.SuperviseRunAsync(runId, CancellationToken.None)); // apre il breaker

            // "Il credito è stato ricaricato": il probe forzato riesce e l'advisory VERA viene scritta.
            llm.Respond = () => """{"summary":"tutto ok","confidence":"alta"}""";
            Assert.True(await supervisor.SuperviseRunAsync(runId, CancellationToken.None, forceProbe: true));

            await using var db = await factory.CreateDbContextAsync();
            var artifact = await db.PipelineArtifacts.SingleAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.Advisory);
            var advisory = JsonSerializer.Deserialize<SupervisorAdvisory>(artifact.PayloadJson)!;
            Assert.False(advisory.IsError);
            Assert.Equal("tutto ok", advisory.Summary);
        }
        finally { sp.Dispose(); }
    }

    [Fact]
    public async Task DeleteErrorAdvisories_RemovesOnlyErrors_InsideTheWindow()
    {
        var (factory, sp) = MakeDb();
        try
        {
            var recentError = await SeedCompletedRunAsync(factory);
            var recentOk = await SeedCompletedRunAsync(factory);
            var oldError = await SeedCompletedRunAsync(factory, completedAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Unspecified));

            var failing = MakeSupervisor(factory, new FakeLlmClient(() => throw new InvalidOperationException("boom")));
            Assert.True(await failing.SuperviseRunAsync(recentError, CancellationToken.None));
            Assert.True(await failing.SuperviseRunAsync(oldError, CancellationToken.None));
            var ok = MakeSupervisor(factory, new FakeLlmClient(() => """{"summary":"ok","confidence":"media"}"""));
            Assert.True(await ok.SuperviseRunAsync(recentOk, CancellationToken.None));

            var deleted = await ok.DeleteErrorAdvisoriesAsync(new DateTime(2026, 7, 1), CancellationToken.None);

            Assert.Equal(1, deleted); // solo l'errore recente: l'ok resta, l'errore vecchio è storia
            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(0, await db.PipelineArtifacts.CountAsync(a => a.RunId == recentError));
            Assert.Equal(1, await db.PipelineArtifacts.CountAsync(a => a.RunId == recentOk));
            Assert.Equal(1, await db.PipelineArtifacts.CountAsync(a => a.RunId == oldError));
        }
        finally { sp.Dispose(); }
    }

    [Fact]
    public async Task SuperviseRunAsync_NotifiesDecisions_OnlyWhenOptedIn()
    {
        var (factory, sp) = MakeDb();
        try
        {
            const string withDecisions = """{"summary":"analisi","confidence":"alta","decisionsForUser":["Confermare X"]}""";

            // Default (NotifyDecisions=false): nessuna notifica.
            var silent = new RecordingNotifier();
            var run1 = await SeedCompletedRunAsync(factory);
            Assert.True(await MakeSupervisor(factory, new FakeLlmClient(() => withDecisions), notifier: silent)
                .SuperviseRunAsync(run1, CancellationToken.None));
            Assert.Empty(silent.Sent);

            // Opt-in: una Info con le decisioni in attesa.
            var loud = new RecordingNotifier();
            var run2 = await SeedCompletedRunAsync(factory);
            Assert.True(await MakeSupervisor(factory, new FakeLlmClient(() => withDecisions),
                    new LlmOptions { NotifyDecisions = true }, notifier: loud)
                .SuperviseRunAsync(run2, CancellationToken.None));
            Assert.Single(loud.Sent);
            Assert.Equal(NotificationSeverity.Info, loud.Sent[0].Severity);
        }
        finally { sp.Dispose(); }
    }
}
