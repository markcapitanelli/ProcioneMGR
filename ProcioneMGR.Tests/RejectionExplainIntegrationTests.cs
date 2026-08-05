using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Llm.Narration;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [G6] LIVELLO 3 dello standard di verifica: il giro completo su Postgres VERO — artifact dei
/// verdetti letto, digest costruito, narrazione persistita e riletta.
///
/// <para>Quello che i test di unità non possono dire: che il Kind nuovo non disturbi gli altri
/// artifact del run, che l'idempotenza regga su una tabella vera, e che il digest si formi anche
/// quando l'AI non ha mai risposto — che è la condizione normale con la prosa spenta.</para>
/// </summary>
[Collection("Postgres")]
public sealed class RejectionExplainIntegrationTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public RejectionExplainIntegrationTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    // ------------------------------------------------------------------ infrastruttura

    private sealed class StubNarrator(RejectionNarration? result) : IRejectionNarrator
    {
        public int Calls { get; private set; }
        public RunRejectionDigest? LastDigest { get; private set; }

        public Task<RejectionNarration?> NarrateAsync(RunRejectionDigest digest, CancellationToken ct = default)
        {
            Calls++;
            LastDigest = digest;
            return Task.FromResult(result);
        }
    }

    private async Task<(IDbContextFactory<ApplicationDbContext> Db, RejectionExplainService Service, StubNarrator Narrator)>
        BuildAsync(RejectionNarration? narration = null, int topN = 5)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var narrator = new StubNarrator(narration);
        var options = new TestOptionsMonitor<LlmOptions>(new LlmOptions { ExplainRejectionsTopN = topN });
        var service = new RejectionExplainService(dbFactory, narrator, options, NullLogger<RejectionExplainService>.Instance);
        return (dbFactory, service, narrator);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>Un run completato con i suoi verdetti, come li scrive il motore.</summary>
    private static async Task<Guid> SeedRunAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        List<ValidatedCandidate> candidates,
        string configName = "Caccia di prova")
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var config = new PipelineConfiguration
        {
            Name = configName,
            Description = "seed",
            CreatedBy = "test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.PipelineConfigurations.Add(config);
        await db.SaveChangesAsync();

        var run = new PipelineRun
        {
            Id = Guid.NewGuid(),
            ConfigurationId = config.Id,
            StartedAt = DateTime.UtcNow.AddMinutes(-30),
            CompletedAt = DateTime.UtcNow,
            Status = "Completed",
        };
        db.PipelineRuns.Add(run);
        db.PipelineArtifacts.Add(new PipelineArtifact
        {
            RunId = run.Id,
            StageName = "HoldoutValidation",
            Kind = "ValidatedCandidates",
            PayloadJson = JsonSerializer.Serialize(candidates),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static ValidatedCandidate Rejected(string symbol, string reason, decimal sharpe, int trades = 30) => new()
    {
        StrategyName = "Composite",
        Symbol = symbol,
        Timeframe = "1h",
        Survived = false,
        RejectReason = reason,
        HoldoutSharpe = sharpe,
        HoldoutTrades = trades,
    };

    // ------------------------------------------------------------------ digest

    /// <summary>
    /// LA PROPRIETÀ CHE GIUSTIFICA IL DESIGN: senza alcuna narrazione l'AI non viene nemmeno
    /// sfiorata, e il ritratto dei verdetti c'è lo stesso. È il caso normale con la prosa spenta.
    /// </summary>
    [Fact]
    public async Task Digest_SiFormaDalDbSenzaMaiChiamareLAi()
    {
        var (db, service, narrator) = await BuildAsync();
        var runId = await SeedRunAsync(db,
        [
            Rejected("BTC/USDT", "Solo 7 trade in holdout (< 20)", 1.93m, 7),
            Rejected("ETH/USDT", "DSR 0,812 ≤ 0,95 (probabile overfitting da selezione)", 0.44m),
        ]);

        var digest = await service.GetDigestAsync(runId);

        Assert.Equal(2, digest.Evaluated);
        Assert.Equal(2, digest.Rejected);
        Assert.Equal(2, digest.Groups.Sum(g => g.Count));
        Assert.Equal(0, narrator.Calls);
    }

    [Fact]
    public async Task Digest_RunSenzaArtifactDeiVerdetti_RestituisceVuotoSenzaEsplodere()
    {
        var (db, service, _) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            var config = new PipelineConfiguration { Name = "vuota", CreatedBy = "t", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            ctx.PipelineConfigurations.Add(config);
            await ctx.SaveChangesAsync();
            ctx.PipelineRuns.Add(new PipelineRun
            {
                Id = Guid.NewGuid(), ConfigurationId = config.Id,
                StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow, Status = "Completed",
            });
            await ctx.SaveChangesAsync();
        }

        var digest = await service.GetDigestAsync(Guid.NewGuid());

        Assert.Equal(RunRejectionDigest.Empty, digest);
    }

    [Fact]
    public async Task Digest_ArtifactCorrotto_DichiaraIlVuotoNonInventa()
    {
        var (db, service, _) = await BuildAsync();
        var runId = await SeedRunAsync(db, [Rejected("BTC/USDT", "Solo 7 trade in holdout (< 20)", 1.0m)]);
        await using (var ctx = await db.CreateDbContextAsync())
        {
            var artifact = await ctx.PipelineArtifacts.FirstAsync(a => a.RunId == runId);
            artifact.PayloadJson = "{ non è json valido";
            await ctx.SaveChangesAsync();
        }

        var digest = await service.GetDigestAsync(runId);

        Assert.Equal(RunRejectionDigest.Empty, digest);
    }

    // ------------------------------------------------------------------ narrazione persistita

    [Fact]
    public async Task Explain_ScrivePersisteERilegge()
    {
        var narration = new RejectionNarration
        {
            Summary = "Nessun sopravvissuto: quasi tutti fermati dal conteggio trade.",
            Notes = [new RejectionNote("Composite BTC/USDT 1h", "Solo 7 trade contro i 20 richiesti.")],
            ModelUsed = "modello-di-prova",
        };
        var (db, service, narrator) = await BuildAsync(narration);
        var runId = await SeedRunAsync(db, [Rejected("BTC/USDT", "Solo 7 trade in holdout (< 20)", 1.93m, 7)]);

        var written = await service.ExplainRunAsync(runId);

        Assert.NotNull(written);
        Assert.Equal(1, narrator.Calls);

        // Riletta dal DB da un'altra chiamata: è persistita davvero, non solo in memoria.
        var read = await service.GetNarrationAsync(runId);
        Assert.NotNull(read);
        Assert.Equal(narration.Summary, read.Summary);
        Assert.Equal("modello-di-prova", read.ModelUsed);
        Assert.Equal("Composite BTC/USDT 1h", Assert.Single(read.Notes).Key);
    }

    [Fact]
    public async Task Explain_Idempotente_NonRipagaLaStessaProsa()
    {
        var (db, service, narrator) = await BuildAsync(new RejectionNarration { Summary = "prima", ModelUsed = "m" });
        var runId = await SeedRunAsync(db, [Rejected("BTC/USDT", "Solo 7 trade in holdout (< 20)", 1.0m)]);

        await service.ExplainRunAsync(runId);
        await service.ExplainRunAsync(runId);
        await service.ExplainRunAsync(runId);

        Assert.Equal(1, narrator.Calls);
        await using var ctx = await db.CreateDbContextAsync();
        Assert.Equal(1, await ctx.PipelineArtifacts.CountAsync(a => a.Kind == LlmArtifactKinds.RejectionExplanation));
    }

    [Fact]
    public async Task Explain_Force_SostituisceInveceDiAccumulare()
    {
        var (db, service, narrator) = await BuildAsync(new RejectionNarration { Summary = "testo", ModelUsed = "m" });
        var runId = await SeedRunAsync(db, [Rejected("BTC/USDT", "Solo 7 trade in holdout (< 20)", 1.0m)]);

        await service.ExplainRunAsync(runId);
        await service.ExplainRunAsync(runId, force: true);

        Assert.Equal(2, narrator.Calls);
        await using var ctx = await db.CreateDbContextAsync();
        // Una sola riga: due narrazioni sullo stesso run non aiutano nessuno.
        Assert.Equal(1, await ctx.PipelineArtifacts.CountAsync(a => a.Kind == LlmArtifactKinds.RejectionExplanation));
    }

    /// <summary>Nessun bocciato ⇒ nessuna chiamata: la chiamata a vuoto la si paga comunque.</summary>
    [Fact]
    public async Task Explain_RunSenzaBocciati_NonChiamaLAiNeScrive()
    {
        var (db, service, narrator) = await BuildAsync(new RejectionNarration { Summary = "x", ModelUsed = "m" });
        var runId = await SeedRunAsync(db,
        [
            new ValidatedCandidate { StrategyName = "Composite", Symbol = "BTC/USDT", Timeframe = "1h", Survived = true },
        ]);

        var result = await service.ExplainRunAsync(runId);

        Assert.Null(result);
        Assert.Equal(0, narrator.Calls);
        await using var ctx = await db.CreateDbContextAsync();
        Assert.Equal(0, await ctx.PipelineArtifacts.CountAsync(a => a.Kind == LlmArtifactKinds.RejectionExplanation));
    }

    /// <summary>
    /// Il Kind nuovo non deve disturbare gli altri artifact del run — è la lezione della Fase C
    /// (un secondo artifact con lo stesso Kind faceva sbagliare worker, pannello e conteggi).
    /// </summary>
    [Fact]
    public async Task Explain_NonTocaGliAltriArtifactDelRun()
    {
        var (db, service, _) = await BuildAsync(new RejectionNarration { Summary = "x", ModelUsed = "m" });
        var runId = await SeedRunAsync(db, [Rejected("BTC/USDT", "Solo 7 trade in holdout (< 20)", 1.0m)]);
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.PipelineArtifacts.Add(new PipelineArtifact
            {
                RunId = runId, StageName = "LlmSupervisor", Kind = LlmArtifactKinds.Advisory,
                PayloadJson = """{"summary":"advisory esistente"}""", CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await service.ExplainRunAsync(runId, force: true);

        await using var check = await db.CreateDbContextAsync();
        Assert.Equal(1, await check.PipelineArtifacts.CountAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.Advisory));
        Assert.Equal(1, await check.PipelineArtifacts.CountAsync(a => a.RunId == runId && a.Kind == "ValidatedCandidates"));
        Assert.Equal(1, await check.PipelineArtifacts.CountAsync(a => a.RunId == runId && a.Kind == LlmArtifactKinds.RejectionExplanation));
    }

    [Fact]
    public async Task Explain_NarratoreCheNonProduce_NonScriveArtifact()
    {
        var (db, service, narrator) = await BuildAsync(narration: null); // AI non disponibile
        var runId = await SeedRunAsync(db, [Rejected("BTC/USDT", "Solo 7 trade in holdout (< 20)", 1.0m)]);

        var result = await service.ExplainRunAsync(runId);

        Assert.Null(result);
        Assert.Equal(1, narrator.Calls);
        await using var ctx = await db.CreateDbContextAsync();
        // Nessuna riga vuota a fare da lapide: il digest resta l'unica cosa da mostrare.
        Assert.Equal(0, await ctx.PipelineArtifacts.CountAsync(a => a.Kind == LlmArtifactKinds.RejectionExplanation));
    }

    // ------------------------------------------------------------------ elenco per la pagina

    [Fact]
    public async Task GetRecent_ElencaSoloRunConBocciatiPiuRecentiPrima()
    {
        var (db, service, _) = await BuildAsync();
        var conBocciati = await SeedRunAsync(db, [Rejected("BTC/USDT", "Solo 7 trade in holdout (< 20)", 1.0m)], "con bocciati");
        await SeedRunAsync(db,
        [
            new ValidatedCandidate { StrategyName = "C", Symbol = "ETH/USDT", Timeframe = "1h", Survived = true },
        ], "tutti passati");

        var recent = await service.GetRecentAsync(10);

        var only = Assert.Single(recent);
        Assert.Equal(conBocciati, only.RunId);
        Assert.Equal("con bocciati", only.ConfigurationName);
        Assert.Null(only.Narration);   // mai spiegato: la pagina mostrerà solo i numeri
        Assert.True(only.Digest.HasContent);
    }

    [Fact]
    public async Task GetRecent_PortaLaNarrazioneQuandoCE()
    {
        var (db, service, _) = await BuildAsync(new RejectionNarration { Summary = "spiegato", ModelUsed = "m" });
        var runId = await SeedRunAsync(db, [Rejected("BTC/USDT", "Solo 7 trade in holdout (< 20)", 1.0m)]);
        await service.ExplainRunAsync(runId);

        var recent = await service.GetRecentAsync(10);

        Assert.Equal("spiegato", Assert.Single(recent).Narration!.Summary);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
