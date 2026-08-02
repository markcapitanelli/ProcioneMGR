using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test della Fase C (secondo parere multi-provider) di <see cref="PipelineSupervisor"/>:
/// artifact SEPARATO con Kind proprio (i filtri di worker/pannello/test sull'advisory primaria
/// non devono vederlo), best-effort dichiarato (un fallimento del provider di confronto non tocca
/// mai l'advisory primaria), skip su provider coincidente/ignoto/senza chiave, default spento.
/// </summary>
[Collection("Postgres")]
public class PipelineSupervisorComparisonTests
{
    private const string ValidAdvisoryJson = """{"summary":"parere primario","confidence":"media"}""";
    private const string SecondAdvisoryJson = """{"summary":"secondo parere","confidence":"alta"}""";

    private readonly PostgresFixture _pg;

    public PipelineSupervisorComparisonTests(PostgresFixture pg) => _pg = pg;

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class FakeLlmClient(Func<string> respond, bool configured = true, string model = "fake-model") : ILlmClient
    {
        public int Calls { get; private set; }
        public bool IsConfigured => configured;
        public string Model => model;
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private sealed class FakeResolver(ILlmClient? client) : ILlmClientResolver
    {
        public List<string> Requested { get; } = new();
        public ILlmClient? Resolve(string provider)
        {
            Requested.Add(provider);
            return client;
        }
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
            StartedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Unspecified),
            CompletedAt = new DateTime(2026, 8, 1, 0, 30, 0, DateTimeKind.Unspecified),
            Status = "Completed",
            Trigger = "Scheduled",
            Conclusion = "Nessun sopravvissuto.",
            RecommendationJson = """{"Survivors":0,"CandidatesEvaluated":10}""",
        });
        await db.SaveChangesAsync();
        return runId;
    }

    private static PipelineSupervisor MakeSupervisor(
        IDbContextFactory<ApplicationDbContext> factory, ILlmClient primary,
        LlmOptions options, ILlmClientResolver? resolver)
    {
        var monitor = options.AsMonitor();
        var guard = new LlmCallGuard(primary, monitor, NullLogger<LlmCallGuard>.Instance);
        return new(factory, primary, guard, monitor,
            NullLogger<PipelineSupervisor>.Instance, metrics: null, notifier: null,
            sentimentCache: null, clientResolver: resolver);
    }

    private static LlmOptions ComparisonOptions() => new()
    {
        Provider = AiProviders.Anthropic,
        ComparisonEnabled = true,
        ComparisonProvider = AiProviders.Nvidia,
    };

    [Fact]
    public async Task ComparisonEnabled_WritesSecondArtifact_WithOwnKindAndProviderInStageName()
    {
        var (factory, _) = MakeDb();
        var runId = await SeedCompletedRunAsync(factory);
        var second = new FakeLlmClient(() => SecondAdvisoryJson, model: "fake-second");
        var supervisor = MakeSupervisor(factory, new FakeLlmClient(() => ValidAdvisoryJson),
            ComparisonOptions(), new FakeResolver(second));

        Assert.True(await supervisor.SuperviseRunAsync(runId, CancellationToken.None));

        await using var db = await factory.CreateDbContextAsync();
        var primary = await db.PipelineArtifacts.SingleAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.Advisory);
        var comparison = await db.PipelineArtifacts.SingleAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.AdvisoryComparison);

        Assert.Equal("LlmSupervisor:Comparison:Nvidia", comparison.StageName);
        var advisory = JsonSerializer.Deserialize<SupervisorAdvisory>(comparison.PayloadJson)!;
        Assert.Equal("secondo parere", advisory.Summary);
        Assert.Equal("fake-second", advisory.ModelUsed);
        Assert.Equal(1, second.Calls);

        // L'advisory primaria resta quella del provider attivo, intoccata.
        var primaryAdvisory = JsonSerializer.Deserialize<SupervisorAdvisory>(primary.PayloadJson)!;
        Assert.Equal("parere primario", primaryAdvisory.Summary);
    }

    [Fact]
    public async Task ComparisonFailure_PrimaryAdvisorySurvives_NoComparisonArtifact()
    {
        var (factory, _) = MakeDb();
        var runId = await SeedCompletedRunAsync(factory);
        var second = new FakeLlmClient(() => throw new HttpRequestException("provider di confronto giù"));
        var supervisor = MakeSupervisor(factory, new FakeLlmClient(() => ValidAdvisoryJson),
            ComparisonOptions(), new FakeResolver(second));

        Assert.True(await supervisor.SuperviseRunAsync(runId, CancellationToken.None));

        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(1, await db.PipelineArtifacts.CountAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.Advisory));
        Assert.Equal(0, await db.PipelineArtifacts.CountAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.AdvisoryComparison));
    }

    [Fact]
    public async Task SameProviderAsActive_ComparisonSkipped()
    {
        var (factory, _) = MakeDb();
        var runId = await SeedCompletedRunAsync(factory);
        var second = new FakeLlmClient(() => SecondAdvisoryJson);
        var options = ComparisonOptions();
        options.ComparisonProvider = options.Provider; // coincide: due pareri identici non confrontano niente
        var supervisor = MakeSupervisor(factory, new FakeLlmClient(() => ValidAdvisoryJson),
            options, new FakeResolver(second));

        Assert.True(await supervisor.SuperviseRunAsync(runId, CancellationToken.None));

        Assert.Equal(0, second.Calls);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(0, await db.PipelineArtifacts.CountAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.AdvisoryComparison));
    }

    [Fact]
    public async Task ComparisonDisabledByDefault_NoSecondCall()
    {
        var (factory, _) = MakeDb();
        var runId = await SeedCompletedRunAsync(factory);
        var second = new FakeLlmClient(() => SecondAdvisoryJson);
        var resolver = new FakeResolver(second);
        var supervisor = MakeSupervisor(factory, new FakeLlmClient(() => ValidAdvisoryJson),
            new LlmOptions(), resolver); // default: ComparisonEnabled=false

        Assert.True(await supervisor.SuperviseRunAsync(runId, CancellationToken.None));

        Assert.Equal(0, second.Calls);
        Assert.Empty(resolver.Requested);
    }

    [Fact]
    public async Task UnconfiguredComparisonProvider_Skipped()
    {
        var (factory, _) = MakeDb();
        var runId = await SeedCompletedRunAsync(factory);
        var second = new FakeLlmClient(() => SecondAdvisoryJson, configured: false);
        var supervisor = MakeSupervisor(factory, new FakeLlmClient(() => ValidAdvisoryJson),
            ComparisonOptions(), new FakeResolver(second));

        Assert.True(await supervisor.SuperviseRunAsync(runId, CancellationToken.None));

        Assert.Equal(0, second.Calls);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(0, await db.PipelineArtifacts.CountAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.AdvisoryComparison));
    }

    [Fact]
    public async Task PrimaryErrorAdvisory_NoComparisonAttempted()
    {
        var (factory, _) = MakeDb();
        var runId = await SeedCompletedRunAsync(factory);
        var second = new FakeLlmClient(() => SecondAdvisoryJson);
        // Risposta primaria non interpretabile → advisory di errore persistita: il secondo parere
        // non deve nemmeno partire (confrontare un errore non informa nessuno).
        var supervisor = MakeSupervisor(factory, new FakeLlmClient(() => "niente json qui"),
            ComparisonOptions(), new FakeResolver(second));

        Assert.True(await supervisor.SuperviseRunAsync(runId, CancellationToken.None));

        Assert.Equal(0, second.Calls);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(0, await db.PipelineArtifacts.CountAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.AdvisoryComparison));
    }

    [Fact]
    public async Task WorkerAntiJoin_IgnoresComparisonArtifacts()
    {
        // Un run con SOLO il secondo parere (caso limite: advisory primaria cancellata a mano)
        // deve restare "pendente" per il worker: l'anti-join guarda il Kind primario.
        var (factory, _) = MakeDb();
        var runId = await SeedCompletedRunAsync(factory);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.PipelineArtifacts.Add(new PipelineArtifact
            {
                RunId = runId,
                StageName = "LlmSupervisor:Comparison:Nvidia",
                Kind = LlmArtifactKinds.AdvisoryComparison,
                PayloadJson = """{"summary":"orfano"}""",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            var pending = await db.PipelineRuns
                .Where(r => r.Status == "Completed")
                .Where(r => !db.PipelineArtifacts.Any(a => a.RunId == r.Id && a.Kind == LlmArtifactKinds.Advisory))
                .CountAsync();
            Assert.Equal(1, pending);
        }
    }
}
