using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Research;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R2+R5, PRD memoria-caccia 2026-08-14] L'indice a righe dei candidati è una tabella DERIVATA
/// dagli artifact "ValidatedCandidates": qui si fissa che (1) la mappatura è fedele campo per
/// campo al blob d'origine (riferimento indipendente: l'oggetto sorgente), (2) l'indicizzazione è
/// incrementale e idempotente (l'indice unico è il contratto), (3) un payload illeggibile esclude
/// QUEL run e non l'intero giro, (4) il flag IsGrey concorda col giudice unico, (5) la pagina
/// filtra e aggrega quello che c'è davvero, e la scelta grigia in Ensemble deduplica per chiave.
/// </summary>
[Collection("Postgres")]
public sealed class ResearchCandidateIndexerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public ResearchCandidateIndexerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private async Task<(IDbContextFactory<ApplicationDbContext> DbFactory, ResearchCandidateIndexer Indexer)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }
        return (dbFactory, new ResearchCandidateIndexer(dbFactory, NullLogger<ResearchCandidateIndexer>.Instance));
    }

    private static ValidatedCandidate Candidate(string strategy, string symbol, bool survived, decimal holdoutSharpe,
        string? reject = null, double? dsr = null) => new()
    {
        StrategyName = strategy,
        Symbol = symbol,
        Timeframe = "1h",
        Parameters = new() { ["period"] = 14m, ["threshold"] = 0.5m },
        WalkForwardOosSharpe = 1.1m,
        SelectionSharpe = 1.4m,
        SelectionReturn = 12.5m,
        SelectionMaxDrawdown = 8.3m,
        SelectionTrades = 42,
        HoldoutSharpe = holdoutSharpe,
        HoldoutReturn = 3.2m,
        HoldoutMaxDrawdown = 5.1m,
        HoldoutProfitFactor = 1.3m,
        HoldoutTrades = 8,
        Survived = survived,
        RejectReason = reject,
        DeflatedSharpe = dsr,
        PanelPbo = 0.4,
        PermutationPValue = 0.12,
        NullTwinPercentile = 97.5,
        BestStopVariant = "SL3",
    };

    private static async Task<Guid> SeedRunAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        List<ValidatedCandidate> candidates, DateTime completedAt, string payloadOverride = "")
    {
        var runId = Guid.NewGuid();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.PipelineRuns.Add(new PipelineRun
        {
            Id = runId,
            ConfigurationId = 1,
            StartedAt = completedAt.AddMinutes(-30),
            CompletedAt = completedAt,
            Status = "Completed",
            Trigger = "Manual",
        });
        db.PipelineArtifacts.Add(new PipelineArtifact
        {
            RunId = runId,
            StageName = "HoldoutValidation",
            Kind = "ValidatedCandidates",
            PayloadJson = payloadOverride.Length > 0 ? payloadOverride : JsonSerializer.Serialize(candidates),
            CreatedAt = completedAt,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    // ------------------------------------------------------------------ 1. mappatura fedele

    [Fact]
    public async Task IndexNewRuns_MapsEveryField_FromTheSourceBlob()
    {
        var (dbFactory, indexer) = await BuildAsync();
        var source = Candidate("RsiOversold", "XLM/USDT", survived: false, holdoutSharpe: 0.9m,
            reject: "Solo 8 trade in holdout (< 10)");
        var completed = new DateTime(2026, 8, 10, 14, 0, 0, DateTimeKind.Utc);
        var runId = await SeedRunAsync(dbFactory, [source], completed);

        var result = await indexer.IndexNewRunsAsync();

        Assert.Equal(new ResearchIndexResult(1, 1, 0), result);
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ResearchCandidates.SingleAsync();
        Assert.Equal(runId, row.RunId);
        Assert.Equal(completed, row.RunCompletedUtc);
        Assert.Equal(source.StrategyName, row.StrategyName);
        Assert.Equal(source.Symbol, row.Symbol);
        Assert.Equal(source.Timeframe, row.Timeframe);
        Assert.Equal(source.Key, row.CandidateKey);
        Assert.Equal(source.Parameters, JsonSerializer.Deserialize<Dictionary<string, decimal>>(row.ParametersJson));
        Assert.Equal(source.WalkForwardOosSharpe, row.WalkForwardOosSharpe);
        Assert.Equal(source.SelectionSharpe, row.SelectionSharpe);
        Assert.Equal(source.SelectionReturn, row.SelectionReturn);
        Assert.Equal(source.SelectionMaxDrawdown, row.SelectionMaxDrawdown);
        Assert.Equal(source.SelectionTrades, row.SelectionTrades);
        Assert.Equal(source.HoldoutSharpe, row.HoldoutSharpe);
        Assert.Equal(source.HoldoutReturn, row.HoldoutReturn);
        Assert.Equal(source.HoldoutMaxDrawdown, row.HoldoutMaxDrawdown);
        Assert.Equal(source.HoldoutProfitFactor, row.HoldoutProfitFactor);
        Assert.Equal(source.HoldoutTrades, row.HoldoutTrades);
        Assert.Equal(source.Survived, row.Survived);
        Assert.Equal(source.RejectReason, row.RejectReason);
        Assert.Equal(source.DeflatedSharpe, row.DeflatedSharpe);
        Assert.Equal(source.PanelPbo, row.PanelPbo);
        Assert.Equal(source.PermutationPValue, row.PermutationPValue);
        Assert.Equal(source.NullTwinPercentile, row.NullTwinPercentile);
        Assert.Equal(source.BestStopVariant, row.BestStopVariant);
        // Il flag concorda col giudice unico — MAI una seconda definizione di grigio.
        Assert.Equal(GreyZone.IsGrey(source), row.IsGrey);
        Assert.True(row.IsGrey);
    }

    // ------------------------------------------------------------------ 2. incrementale e idempotente

    [Fact]
    public async Task Incremental_IndexesOnlyNewRuns_AndRerunIsNoOp()
    {
        var (dbFactory, indexer) = await BuildAsync();
        await SeedRunAsync(dbFactory, [Candidate("A", "BTC/USDT", true, 1.5m)], DateTime.UtcNow.AddDays(-2));
        var first = await indexer.IndexNewRunsAsync();
        Assert.Equal(1, first.RunsIndexed);

        // Rilancio senza run nuovi: no-op, nessun duplicato (l'indice unico e' il contratto).
        var rerun = await indexer.IndexNewRunsAsync();
        Assert.Equal(new ResearchIndexResult(0, 0, 0), rerun);

        // Un secondo run: entra SOLO lui.
        await SeedRunAsync(dbFactory, [Candidate("B", "ETH/USDT", false, -1.2m, reject: "Sharpe holdout -1,20 < 0,30")], DateTime.UtcNow.AddDays(-1));
        var second = await indexer.IndexNewRunsAsync();
        Assert.Equal(1, second.RunsIndexed);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(2, await db.ResearchCandidates.CountAsync());
    }

    [Fact]
    public async Task Rebuild_RealignsFromScratch()
    {
        var (dbFactory, indexer) = await BuildAsync();
        await SeedRunAsync(dbFactory, [Candidate("A", "BTC/USDT", true, 1.5m), Candidate("B", "BTC/USDT", false, 0.9m, dsr: 0.88)], DateTime.UtcNow.AddDays(-1));
        await indexer.IndexNewRunsAsync();

        var rebuilt = await indexer.RebuildAsync();

        Assert.Equal(1, rebuilt.RunsIndexed);
        Assert.Equal(2, rebuilt.CandidatesIndexed);
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(2, await db.ResearchCandidates.CountAsync());
    }

    // ------------------------------------------------------------------ 3. difensivo per run

    [Fact]
    public async Task MalformedPayload_SkipsThatRun_IndexesTheOthers()
    {
        var (dbFactory, indexer) = await BuildAsync();
        await SeedRunAsync(dbFactory, [], DateTime.UtcNow.AddDays(-2), payloadOverride: "{ not json [");
        await SeedRunAsync(dbFactory, [Candidate("A", "BTC/USDT", true, 1.5m)], DateTime.UtcNow.AddDays(-1));

        var result = await indexer.IndexNewRunsAsync();

        Assert.Equal(1, result.RunsIndexed);
        Assert.Equal(1, result.RunsSkipped);
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.ResearchCandidates.CountAsync());
    }

    // ------------------------------------------------------------------ 4. la pagina legge il vero

    [Fact]
    public async Task PageService_FiltersAndAggregates_MatchSeededTruth()
    {
        var (dbFactory, indexer) = await BuildAsync();
        await SeedRunAsync(dbFactory,
        [
            Candidate("RsiOversold", "XLM/USDT", survived: true, holdoutSharpe: 1.5m),
            Candidate("RsiOversold", "DOT/USDT", survived: false, holdoutSharpe: 0.9m, reject: "Solo 8 trade in holdout (< 10)"),
            Candidate("Composite", "XLM/USDT", survived: false, holdoutSharpe: -1.8m, reject: "Sharpe holdout -1,80 < 0,30"),
        ], DateTime.UtcNow.AddDays(-1));

        var page = new ResearchPageService(indexer, dbFactory);
        await page.InitializeAsync(new ResearchFilter());

        Assert.NotNull(page.Summary);
        Assert.Equal(3, page.Summary!.TotalCandidates);
        Assert.Equal(1, page.Summary.Survivors);
        Assert.Equal(1, page.Summary.Grey);
        Assert.Equal(1, page.Summary.RejectedOnMerit);

        // Filtro per verdetto: grigio.
        await page.LoadAsync(new ResearchFilter(Verdict: "grey"));
        var grey = Assert.Single(page.Candidates);
        Assert.Equal("DOT/USDT", grey.Symbol);

        // Filtro per coppia.
        await page.LoadAsync(new ResearchFilter(Symbol: "XLM/USDT"));
        Assert.Equal(2, page.Candidates.Count);
        Assert.All(page.Candidates, c => Assert.Equal("XLM/USDT", c.Symbol));

        // Resa per famiglia: RsiOversold 2 provati / 1 promosso / 1 grigio.
        var rsi = page.FamilyStats.Single(f => f.Family == "RsiOversold");
        Assert.Equal((2, 1, 1), (rsi.Tested, rsi.Survived, rsi.Grey));

        // Motivi di scarto classificati.
        Assert.Contains(page.RejectReasons, r => r.Category == "Finestra corta (pochi trade)" && r.Count == 1);
        Assert.Contains(page.RejectReasons, r => r.Category == "Sharpe holdout sotto soglia" && r.Count == 1);
    }

    [Fact]
    public void RejectCategory_ClassifiesKnownPrefixes_AndKeepsUnknownVerbatim()
    {
        Assert.Equal("Finestra corta (pochi trade)", ResearchPageService.RejectCategory("Solo 8 trade in holdout (< 10)"));
        Assert.Equal("DSR sotto soglia", ResearchPageService.RejectCategory("DSR 0,62 ≤ 0,95 dopo 128 tentativi"));
        Assert.Equal("Sharpe holdout sotto soglia", ResearchPageService.RejectCategory("Sharpe holdout -1,50 < 0,30"));
        Assert.Equal("(nessun motivo registrato)", ResearchPageService.RejectCategory(null));
        // Un motivo mai visto resta com'e': onesto, mai un "altro" muto.
        Assert.Equal("Motivo inedito", ResearchPageService.RejectCategory("Motivo inedito"));
    }

    // ------------------------------------------------------------------ 5. la scelta grigia deduplica

    [Fact]
    public void DedupGreyChoices_KeepsMostRecentPerKey_OrdersByHoldoutSharpe()
    {
        var old = new ResearchCandidate { CandidateKey = "K1", HoldoutSharpe = 0.5m, RunCompletedUtc = DateTime.UtcNow.AddDays(-5) };
        var fresh = new ResearchCandidate { CandidateKey = "K1", HoldoutSharpe = 0.9m, RunCompletedUtc = DateTime.UtcNow.AddDays(-1) };
        var other = new ResearchCandidate { CandidateKey = "K2", HoldoutSharpe = 0.7m, RunCompletedUtc = DateTime.UtcNow.AddDays(-3) };

        // L'input arriva ordinato dal run piu' recente (come la query della pagina).
        var deduped = EnsemblePageService.DedupGreyChoices([fresh, other, old], max: 10);

        Assert.Equal(2, deduped.Count);
        Assert.Equal(0.9m, deduped[0].HoldoutSharpe); // K1: vince la misura piu' fresca, e guida per Sharpe
        Assert.Equal("K2", deduped[1].CandidateKey);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
