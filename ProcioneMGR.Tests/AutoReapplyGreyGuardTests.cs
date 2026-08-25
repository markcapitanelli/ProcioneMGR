using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Agents;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Security;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [J12, PRD autonomia-operativa 2026-08-25] La guardia sulle gambe grigie dell'applica automatica.
///
/// Prima della guardia, la catena campagna → <see cref="RunApplyEvaluator"/> → avvio Paper poteva
/// schierare sulle corsie dell'impronta un ensemble di SOLE gambe grigie: l'evaluator guardava solo
/// <c>EnsembleLegs.Count &gt; 0</c>, mai la provenienza (misurato: run del 2026-08-21, config 19,
/// Survivors=0 ed EnsembleLegs=3, tutte grigie). Questi test pinnano: il blocco delle gambe grigie
/// e ignote, il fail-closed sulla provenienza null, il kind SEPARATO dell'artifact (un run bloccato
/// deve restare proponibile al click umano — F5), e che il percorso dei soli sopravvissuti resti
/// identico a prima.
/// </summary>
public class AutoReapplyGreyGuardStaticTests
{
    private static PipelineRecommendation Rec(params string?[] verdicts)
    {
        var rec = new PipelineRecommendation();
        foreach (var v in verdicts)
        {
            rec.EnsembleLegs.Add(new ProposedLeg
            {
                StrategyName = "S", Symbol = "BTC/USDT", Timeframe = "4h",
                WeightPercent = 100m / verdicts.Length, HoldoutSharpe = 1m,
                SourceVerdict = v,
            });
        }
        return rec;
    }

    [Fact]
    public void SoloSopravvissuti_ZeroNonSopravvissuti()
    {
        var (total, grey, unknown) = RunApplyEvaluator.CountNonSurvivorLegs(Rec("Survived", "Survived"));
        Assert.Equal(0, total);
        Assert.Equal(0, grey);
        Assert.Equal(0, unknown);
    }

    [Fact]
    public void GambeGrigie_Contate()
    {
        var (total, grey, unknown) = RunApplyEvaluator.CountNonSurvivorLegs(Rec("Survived", "Grey", "Grey"));
        Assert.Equal(2, total);
        Assert.Equal(2, grey);
        Assert.Equal(0, unknown);
    }

    [Fact]
    public void ProvenienzaNull_TrattataDaGrigia_FailClosed()
    {
        // I JSON precedenti a T1 (2026-08-14) non portano SourceVerdict: una guardia che si fida
        // di cio' che non conosce non e' una guardia.
        var (total, grey, unknown) = RunApplyEvaluator.CountNonSurvivorLegs(Rec("Survived", null));
        Assert.Equal(1, total);
        Assert.Equal(0, grey);
        Assert.Equal(1, unknown);
    }

    [Fact]
    public void EtichettaSconosciuta_TrattataDaIgnota()
    {
        // Un'etichetta che non e' ne' "Survived" ne' "Grey" (refuso, versione futura) non deve
        // passare per sopravvissuta.
        var (total, _, unknown) = RunApplyEvaluator.CountNonSurvivorLegs(Rec("survived", "SURVIVED"));
        Assert.Equal(2, total);
        Assert.Equal(2, unknown);
    }
}

[Collection("Postgres")]
public class AutoReapplyGreyGuardIntegrationTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public AutoReapplyGreyGuardIntegrationTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class CountingApplier : IPipelineApplier
    {
        public int ApplyCallCount { get; private set; }
        public int LaneCount => 3;
        public Task<ApplyResult> ApplyRecommendationAsync(PipelineRecommendation recommendation, CancellationToken ct = default)
        {
            ApplyCallCount++;
            return Task.FromResult(new ApplyResult { LanesUsed = 1, Message = "applicato (fake)" });
        }
        public Task<ApplyResult> ApplyRunAsync(Guid runId, CancellationToken ct = default) => ApplyRecommendationAsync(new PipelineRecommendation(), ct);
        public Task<EnsembleSummary> GetCurrentEnsembleSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(new EnsembleSummary()); // ensemble corrente vuoto: il comparatore direbbe si'
        public EnsembleSummary SummarizeRecommendation(PipelineRecommendation recommendation)
            => new()
            {
                WeightedAverageSharpe = recommendation.EnsembleLegs.Count > 0 ? recommendation.EnsembleLegs.Average(l => l.HoldoutSharpe) : 0m,
                SurvivingLegs = recommendation.EnsembleLegs.Count,
                DistinctSymbols = recommendation.EnsembleLegs.Select(l => l.Symbol).Distinct().Count(),
                Legs = [],
            };
    }

    /// <summary>Il supervisore non deve nemmeno essere INTERPELLATO su un run bloccato dalla guardia (costa una chiamata LLM).</summary>
    private sealed class CountingSupervisor : IPipelineSupervisorAgent
    {
        public string Provider => "Counting";
        public int Calls { get; private set; }
        public Task<SupervisorJudgment> AnalyzeRunAsync(PipelineRun run, EnsembleSummary current, EnsembleSummary candidate, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new SupervisorJudgment { ApproveReplacement = true, Summary = "ok (fake)" });
        }
    }

    private async Task<(RunApplyEvaluator Evaluator, CountingApplier Applier, CountingSupervisor Supervisor, IDbContextFactory<ApplicationDbContext> DbFactory)> BuildAsync(int maxGreyLegs = 0)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var applier = new CountingApplier();
        var supervisor = new CountingSupervisor();
        var evaluator = new RunApplyEvaluator(
            dbFactory, applier,
            new EnsembleComparator(new EnsembleComparatorOptions()),
            supervisor,
            NullLogger<RunApplyEvaluator>.Instance,
            autoReapply: new AutoReapplyOptions { Enabled = true, MaxGreyLegs = maxGreyLegs }.AsMonitor());
        return (evaluator, applier, supervisor, dbFactory);
    }

    private async Task<Guid> SeedRunAsync(IDbContextFactory<ApplicationDbContext> dbFactory, params string?[] verdicts)
    {
        var rec = new PipelineRecommendation { Survivors = verdicts.Count(v => v == "Survived") };
        // Simboli distinti per gamba: il comparatore pretende MinDistinctSymbols (default 2) e
        // questo test non vuole misurare QUELLA soglia — vuole isolare la guardia grigia.
        string[] symbols = ["BTC/USDT", "ETH/USDT", "SOL/USDT", "BNB/USDT", "XRP/USDT"];
        foreach (var v in verdicts)
        {
            rec.EnsembleLegs.Add(new ProposedLeg
            {
                StrategyName = "S" + rec.EnsembleLegs.Count,
                Symbol = symbols[rec.EnsembleLegs.Count % symbols.Length], Timeframe = "4h",
                WeightPercent = 100m / verdicts.Length, HoldoutSharpe = 1.5m, SourceVerdict = v,
            });
        }
        var runId = Guid.NewGuid();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.PipelineRuns.Add(new PipelineRun
        {
            Id = runId,
            ConfigurationId = 1,
            StartedAt = DateTime.UtcNow.AddMinutes(-30),
            CompletedAt = DateTime.UtcNow.AddMinutes(-5),
            Status = "Completed",
            Trigger = "Campaign",
            RecommendationJson = System.Text.Json.JsonSerializer.Serialize(rec),
        });
        await db.SaveChangesAsync();
        return runId;
    }

    [Fact]
    public async Task SoleGambeGrigie_NonApplica_E_RegistraGreyBlocked_NonDecision()
    {
        var (evaluator, applier, supervisor, dbFactory) = await BuildAsync();
        var runId = await SeedRunAsync(dbFactory, "Grey", "Grey", "Grey");

        var outcome = await evaluator.EvaluateAndMaybeApplyAsync(runId);

        Assert.True(outcome.HadCandidate);
        Assert.False(outcome.Applied);
        Assert.False(outcome.Vetoed);
        Assert.Contains("fascia grigia", outcome.Message);
        Assert.Equal(0, applier.ApplyCallCount);
        // La guardia e' deterministica e viene PRIMA del supervisore: zero chiamate LLM sprecate.
        Assert.Equal(0, supervisor.Calls);

        await using var db = await dbFactory.CreateDbContextAsync();
        // Il kind e' SEPARATO: una Decision marcherebbe il run "gia' gestito" per il lettore di
        // flotta (FleetStateReader.ReadCandidatesAsync) e la proposta grigia al click umano — il
        // percorso F5 che questa guardia esiste per preservare — sparirebbe in silenzio.
        Assert.True(await db.PipelineArtifacts.AnyAsync(a => a.RunId == runId && a.Kind == AutoReapplyArtifactKinds.GreyBlocked));
        Assert.False(await db.PipelineArtifacts.AnyAsync(a => a.RunId == runId && a.Kind == AutoReapplyArtifactKinds.Decision));
    }

    [Fact]
    public async Task SoliSopravvissuti_ApplicaComeOggi()
    {
        var (evaluator, applier, _, dbFactory) = await BuildAsync();
        var runId = await SeedRunAsync(dbFactory, "Survived", "Survived");

        var outcome = await evaluator.EvaluateAndMaybeApplyAsync(runId);

        Assert.True(outcome.Applied);
        Assert.Equal(1, applier.ApplyCallCount);
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.True(await db.PipelineArtifacts.AnyAsync(a => a.RunId == runId && a.Kind == AutoReapplyArtifactKinds.Decision));
    }

    [Fact]
    public async Task MisteSottoIlTetto_Passano_SopraIlTetto_No()
    {
        // Tetto 1: una gamba grigia su tre e' tollerata...
        var (evaluator, applier, _, dbFactory) = await BuildAsync(maxGreyLegs: 1);
        var sotto = await SeedRunAsync(dbFactory, "Survived", "Survived", "Grey");
        var outcomeSotto = await evaluator.EvaluateAndMaybeApplyAsync(sotto);
        Assert.True(outcomeSotto.Applied);

        // ...due no.
        var sopra = await SeedRunAsync(dbFactory, "Survived", "Grey", "Grey");
        var outcomeSopra = await evaluator.EvaluateAndMaybeApplyAsync(sopra);
        Assert.False(outcomeSopra.Applied);
        Assert.Equal(1, applier.ApplyCallCount);
    }

    [Fact]
    public async Task ProvenienzaNull_Blocca_FailClosed()
    {
        var (evaluator, applier, _, dbFactory) = await BuildAsync();
        var runId = await SeedRunAsync(dbFactory, "Survived", null);

        var outcome = await evaluator.EvaluateAndMaybeApplyAsync(runId);

        Assert.False(outcome.Applied);
        Assert.Contains("provenienza ignota", outcome.Message);
        Assert.Equal(0, applier.ApplyCallCount);
    }

    [Fact]
    public async Task GreyBlocked_Idempotente()
    {
        var (evaluator, _, _, dbFactory) = await BuildAsync();
        var runId = await SeedRunAsync(dbFactory, "Grey");

        await evaluator.EvaluateAndMaybeApplyAsync(runId);
        await evaluator.EvaluateAndMaybeApplyAsync(runId);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.PipelineArtifacts.CountAsync(a => a.RunId == runId && a.Kind == AutoReapplyArtifactKinds.GreyBlocked));
    }

    [Fact]
    public async Task EvaluatorSenzaOpzioni_UsaIlDefaultPiuSevero()
    {
        // Il parametro opzionale assente (wiring mancante) NON deve mai essere piu' permissivo
        // del default: fail-closed anche sulla composizione.
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();

        var applier = new CountingApplier();
        var evaluator = new RunApplyEvaluator(
            dbFactory, applier,
            new EnsembleComparator(new EnsembleComparatorOptions()),
            new CountingSupervisor(),
            NullLogger<RunApplyEvaluator>.Instance);
        var runId = await SeedRunAsync(dbFactory, "Grey");

        var outcome = await evaluator.EvaluateAndMaybeApplyAsync(runId);

        Assert.False(outcome.Applied);
        Assert.Equal(0, applier.ApplyCallCount);
    }
}
