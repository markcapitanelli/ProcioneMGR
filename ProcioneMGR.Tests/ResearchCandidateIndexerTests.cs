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
        List<ValidatedCandidate> candidates, DateTime completedAt, string payloadOverride = "",
        bool mixedUniverse = false)
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
            MixedTimeframeUniverse = mixedUniverse, // [J4]
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
    public async Task PageService_ExcludesMixedUniverseRuns_AndDeclaresTheCount()
    {
        // [J4] I candidati dei run a universo misto escono da OGNI lettura (PBO/DSR calcolati su
        // un pannello che mescola due ppy: verdetti non confrontabili), e l'esclusione è
        // DICHIARATA col conteggio — uno scarto silenzioso si leggerebbe come «non c'era nulla».
        var (dbFactory, indexer) = await BuildAsync();
        await SeedRunAsync(dbFactory,
            [Candidate("RsiOversold", "XLM/USDT", survived: true, holdoutSharpe: 1.5m)],
            DateTime.UtcNow.AddDays(-2));
        await SeedRunAsync(dbFactory,
            [
                Candidate("Momentum", "BTC/USDT", survived: true, holdoutSharpe: 2.0m),
                Candidate("Momentum", "ETH/USDT", survived: false, holdoutSharpe: 0.8m, reject: "Solo 8 trade in holdout (< 10)"),
            ],
            DateTime.UtcNow.AddDays(-1), mixedUniverse: true);

        var page = new ResearchPageService(indexer, dbFactory);
        await page.InitializeAsync(new ResearchFilter());

        // Gli aggregati contano SOLO il run pulito; l'esclusione porta i suoi due numeri.
        Assert.NotNull(page.Summary);
        Assert.Equal(1, page.Summary!.TotalCandidates);
        Assert.Equal(1, page.Summary.Survivors);
        Assert.Equal(0, page.Summary.Grey);
        Assert.Equal(2, page.MixedExcludedRows);
        Assert.Equal(2, page.MixedExcludedKeys);

        // E la tabella non mostra i candidati del run misto.
        await page.LoadAsync(new ResearchFilter());
        Assert.DoesNotContain(page.Candidates, c => c.Symbol == "BTC/USDT");
    }

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
        // Il testo VERO del gate T1.5 (ModelStages): inglese "permutation" e con "Sharpe" dentro
        // — la review 2026-08-14 lo ha trovato classificato come Sharpe. Deve vincere il ramo giusto.
        Assert.Equal("Permutation test", ResearchPageService.RejectCategory("permutation p 0,123 ≥ 0,10 (Sharpe holdout compatibile col rumore)"));
        Assert.Equal("(nessun motivo registrato)", ResearchPageService.RejectCategory(null));
        // Un motivo mai visto resta com'e': onesto, mai un "altro" muto.
        Assert.Equal("Motivo inedito", ResearchPageService.RejectCategory("Motivo inedito"));
    }

    // ------------------------------------------------------------------ 6. robustezza (review 2026-08-14)

    [Fact]
    public async Task OverlongRejectReason_IsTruncated_RunStillIndexed()
    {
        // Il RejectReason può portare un messaggio d'eccezione ILLIMITATO ("Backtest fallito:
        // {ex.Message}") contro varchar(256): senza troncamento UN candidato storico fuori misura
        // faceva fallire l'intero giro e svuotava la pagina.
        var (dbFactory, indexer) = await BuildAsync();
        var longReason = "Backtest fallito: " + new string('x', 600);
        await SeedRunAsync(dbFactory, [Candidate("A", "BTC/USDT", false, -0.5m, reject: longReason)], DateTime.UtcNow.AddDays(-1));

        var result = await indexer.IndexNewRunsAsync();

        Assert.Equal(1, result.RunsIndexed);
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ResearchCandidates.SingleAsync();
        Assert.Equal(256, row.RejectReason!.Length);
        Assert.StartsWith("Backtest fallito:", row.RejectReason);
    }

    [Fact]
    public async Task ConcurrentlyIndexedRun_IsSkipped_OthersStillIndexed()
    {
        // Fra processi diversi (guscio + pod ui) la gara sul read-then-insert resta possibile: il
        // perdente riceve la violazione dell'indice unico e deve trattare QUEL run come "già
        // indicizzato da un altro", non abortire il giro. Si riproduce ESATTAMENTE la finestra:
        // la lista dei run già indicizzati viene letta (vuota), POI "l'altro processo" scrive la
        // sua riga, e il nostro giro parte con la lista ormai stantia — via IndexAsync internal.
        var (dbFactory, indexer) = await BuildAsync();
        var winner = Candidate("A", "BTC/USDT", true, 1.5m);
        var runId = await SeedRunAsync(dbFactory, [winner], DateTime.UtcNow.AddDays(-2));
        await SeedRunAsync(dbFactory, [Candidate("B", "ETH/USDT", true, 1.1m)], DateTime.UtcNow.AddDays(-1));
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            // La riga "dell'altro processo": stesso (RunId, CandidateKey) che il nostro giro proverà a scrivere.
            db.ResearchCandidates.Add(new ResearchCandidate
            {
                RunId = runId, RunCompletedUtc = DateTime.UtcNow, StrategyName = winner.StrategyName,
                Symbol = winner.Symbol, Timeframe = winner.Timeframe, CandidateKey = winner.Key,
            });
            await db.SaveChangesAsync();
        }

        ResearchIndexResult result;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            result = await indexer.IndexAsync(db, [], CancellationToken.None); // lista stantia: la finestra della gara
        }

        Assert.Equal(1, result.RunsIndexed);   // il secondo run passa
        Assert.Equal(1, result.RunsSkipped);   // il run "vinto dall'altro" è saltato, non fatale
        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.Equal(1, await check.ResearchCandidates.CountAsync(c => c.RunId != runId));
        Assert.Equal(1, await check.ResearchCandidates.CountAsync(c => c.RunId == runId)); // nessun duplicato
    }

    [Fact]
    public async Task NullCompletedAt_FallsBackToArtifactCreatedAt_StableAcrossRebuilds()
    {
        // DateTime.UtcNow come fallback fabbricherebbe una recency diversa a ogni rebuild: il
        // riferimento stabile è il CreatedAt dell'artifact (stessa transazione di fine run).
        var (dbFactory, indexer) = await BuildAsync();
        var artifactCreated = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var runId = Guid.NewGuid();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.PipelineRuns.Add(new PipelineRun
            {
                Id = runId, ConfigurationId = 1, StartedAt = artifactCreated.AddMinutes(-30),
                CompletedAt = null, Status = "Completed", Trigger = "Manual",
            });
            db.PipelineArtifacts.Add(new PipelineArtifact
            {
                RunId = runId, StageName = "HoldoutValidation", Kind = "ValidatedCandidates",
                PayloadJson = JsonSerializer.Serialize(new List<ValidatedCandidate> { Candidate("A", "BTC/USDT", true, 1.5m) }),
                CreatedAt = artifactCreated,
            });
            await db.SaveChangesAsync();
        }

        await indexer.IndexNewRunsAsync();
        var first = await ReadCompletedUtcAsync(dbFactory);
        await indexer.RebuildAsync();
        var second = await ReadCompletedUtcAsync(dbFactory);

        Assert.Equal(artifactCreated, first);
        Assert.Equal(first, second); // stabile: due rebuild, stessa data
    }

    private static async Task<DateTime> ReadCompletedUtcAsync(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.ResearchCandidates.SingleAsync()).RunCompletedUtc;
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
