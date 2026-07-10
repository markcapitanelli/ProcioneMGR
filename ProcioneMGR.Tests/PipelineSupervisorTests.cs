using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica il layer AI di supervisione (SOLO advisory): l'LLM è sostituito da un fake, così nessun
/// test tocca la rete. Copre parsing, persistenza come PipelineArtifact, idempotenza per-run, e il
/// percorso d'errore (che deve comunque persistere un advisory di errore, un tentativo per run).
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
        public bool IsConfigured => true;
        public string Model => "fake-model";
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
            => Task.FromResult(respond());
    }

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

    private static async Task<Guid> SeedCompletedRunAsync(IDbContextFactory<ApplicationDbContext> factory)
    {
        var runId = Guid.NewGuid();
        await using var db = await factory.CreateDbContextAsync();
        db.PipelineRuns.Add(new PipelineRun
        {
            Id = runId,
            ConfigurationId = 1,
            StartedAt = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Unspecified),
            CompletedAt = new DateTime(2026, 7, 5, 0, 30, 0, DateTimeKind.Unspecified),
            Status = "Completed",
            Trigger = "Scheduled",
            Conclusion = "Ensemble proposto su 2 leg.",
            RecommendationJson = """{"RegimeLabel":"trend","VolatilityLabel":"alta","Survivors":2,"CandidatesEvaluated":40}""",
        });
        await db.SaveChangesAsync();
        return runId;
    }

    private static PipelineSupervisor MakeSupervisor(IDbContextFactory<ApplicationDbContext> factory, ILlmClient llm)
        => new(factory, llm, LoggerFactory.Create(b => { }).CreateLogger<PipelineSupervisor>());

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
}
