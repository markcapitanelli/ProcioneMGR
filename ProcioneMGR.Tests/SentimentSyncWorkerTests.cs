using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.AltData;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Sentiment;
using ProcioneMGR.Services.Sentiment.Metrics;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del tick di <see cref="SentimentSyncWorker"/>: metriche + news + snapshot in cache, la
/// cadenza delle news (secondo tick entro l'intervallo NON risincronizza), e la retention con
/// l'esenzione della fonte FearGreed.
/// </summary>
[Collection("Postgres")]
public class SentimentSyncWorkerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public SentimentSyncWorkerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class FakeMetricSource : ISentimentMetricSource
    {
        public string Name => "BinanceFutures";
        public Task<IReadOnlyList<SentimentMetricSample>> FetchLatestAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SentimentMetricSample>>(
                [new SentimentMetricSample(DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour), SentimentMetrics.FundingRate, "BTC", 0.01m)]);
    }

    private sealed class FakeNewsSource : IAltDataSource
    {
        public int Calls { get; private set; }
        public string Name => "TestNews";
        public Task<IReadOnlyList<RawNewsItem>> FetchLatestAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<RawNewsItem>>(
                [new RawNewsItem(DateTime.UtcNow, $"Bitcoin news {Calls}", "rally", $"https://example.com/{Calls}")]);
        }
    }

    private async Task<(SentimentSyncWorker Worker, FakeNewsSource News, IDbContextFactory<ApplicationDbContext> DbFactory, SentimentSnapshotCache Cache)> BuildAsync(
        SentimentOptions? options = null)
    {
        var news = new FakeNewsSource();
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<SentimentOptions>>((options ?? new SentimentOptions()).AsMonitor());
        services.AddSingleton<IEnumerable<ISentimentMetricSource>>([new FakeMetricSource()]);
        services.AddSingleton<IEnumerable<IAltDataSource>>([news]);
        services.AddSingleton<ISentimentScorer, KeywordSentimentScorer>();
        services.AddSingleton<SentimentSnapshotCache>();
        services.AddScoped<ISentimentMetricSyncService, SentimentMetricSyncService>();
        services.AddScoped<IAltDataSyncService, AltDataSyncService>();
        services.AddScoped<ISentimentSnapshotService, SentimentSnapshotService>();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var worker = new SentimentSyncWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<SentimentOptions>>(),
            NullLogger<SentimentSyncWorker>.Instance);
        return (worker, news, dbFactory, provider.GetRequiredService<SentimentSnapshotCache>());
    }

    [Fact]
    public async Task Tick_SyncsMetricsAndNews_AndComputesSnapshotInCache()
    {
        var (worker, news, dbFactory, cache) = await BuildAsync();

        var (metrics, newsCount) = await worker.TickAsync(CancellationToken.None);

        Assert.Equal(1, metrics);
        Assert.Equal(1, newsCount);
        Assert.Equal(1, news.Calls);
        Assert.NotNull(cache.Current);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.SentimentMetricPoints.CountAsync());
        Assert.Equal(1, await db.AltDataPoints.CountAsync());
    }

    [Fact]
    public async Task Tick_NewsCadence_SecondTickWithinIntervalSkipsNews_ForceNewsOverrides()
    {
        var (worker, news, _, _) = await BuildAsync(new SentimentOptions { NewsIntervalMinutes = 60 });

        await worker.TickAsync(CancellationToken.None);
        Assert.Equal(1, news.Calls);

        // Secondo tick subito dopo: le news NON sono dovute (intervallo 60 min non passato).
        var (_, newsCount) = await worker.TickAsync(CancellationToken.None);
        Assert.Equal(0, newsCount);
        Assert.Equal(1, news.Calls);

        // "Esegui ora" dalla UI: forza anche le news.
        await worker.TickAsync(CancellationToken.None, forceNews: true);
        Assert.Equal(2, news.Calls);
    }

    [Fact]
    public async Task Tick_Purge_RespectsCutoffs_AndExemptsHistoricalSeries()
    {
        // Esenzioni della purge: FearGreed (baseline lungo), FundingRate (backfill 2019 che alimenta
        // il backtest — il vecchio test sanciva la sua CANCELLAZIONE: era il bug, non il contratto)
        // e BinanceLiquidations (F4: non ricostruibile a posteriori). Le metriche-cache (OI ecc.)
        // continuano a essere potate.
        //
        // [I15, 2026-08-19] STESSO ROVESCIAMENTO sulle notizie, e per la stessa ragione: una notizia
        // già valutata da uno scorer ha un costo di produzione e non si riscarica. Anche qui il
        // vecchio assert sanciva la cancellazione — «vecchia» moriva e basta — ed era il bug.
        // L'esenzione è MIRATA: le notizie SENZA punteggio restano potabili, e il test lo verifica
        // in ENTRAMBI i versi, perché un'esenzione totale farebbe crescere la tabella senza limite
        // per conservare righe che nessun consumatore guarda.
        var (worker, _, dbFactory, _) = await BuildAsync(new SentimentOptions { NewsRetentionDays = 30, MetricRetentionDays = 60 });

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.AltDataPoints.Add(new AltDataPoint { TimestampUtc = DateTime.UtcNow.AddDays(-40), Source = "X", Title = "vecchia-grezza", DedupeKey = "X:vecchia-grezza" });
            db.AltDataPoints.Add(new AltDataPoint { TimestampUtc = DateTime.UtcNow.AddDays(-40), Source = "X", Title = "vecchia-scorata", DedupeKey = "X:vecchia-scorata", SentimentScore = 0.42m });
            // Il caso insidioso: punteggio ZERO non è punteggio ASSENTE. Una lettura sbagliata del
            // predicato (falsy invece di null) cancellerebbe proprio le notizie neutre.
            db.AltDataPoints.Add(new AltDataPoint { TimestampUtc = DateTime.UtcNow.AddDays(-40), Source = "X", Title = "vecchia-neutra", DedupeKey = "X:vecchia-neutra", SentimentScore = 0m });
            db.AltDataPoints.Add(new AltDataPoint { TimestampUtc = DateTime.UtcNow.AddDays(-5), Source = "X", Title = "recente", DedupeKey = "X:recente" });
            db.SentimentMetricPoints.Add(new SentimentMetricPoint { TimestampUtc = DateTime.UtcNow.AddDays(-90), Source = SentimentMetricSources.BinanceFutures, Metric = SentimentMetrics.OpenInterest, Symbol = "BTC", Value = 1m });
            db.SentimentMetricPoints.Add(new SentimentMetricPoint { TimestampUtc = DateTime.UtcNow.AddDays(-90), Source = SentimentMetricSources.BinanceFutures, Metric = SentimentMetrics.FundingRate, Symbol = "BTC", Value = 0.01m });
            db.SentimentMetricPoints.Add(new SentimentMetricPoint { TimestampUtc = DateTime.UtcNow.AddDays(-90), Source = SentimentMetricSources.BinanceLiquidations, Metric = SentimentMetrics.LongLiquidationNotional, Symbol = "BTC", Value = 1_000_000m });
            db.SentimentMetricPoints.Add(new SentimentMetricPoint { TimestampUtc = DateTime.UtcNow.AddDays(-90), Source = SentimentMetricSources.FearGreed, Metric = SentimentMetrics.FearGreedIndex, Symbol = "", Value = 50m });
            await db.SaveChangesAsync();
        }

        await worker.TickAsync(CancellationToken.None);

        await using var check = await dbFactory.CreateDbContextAsync();
        // [I15] La notizia vecchia SENZA punteggio muore: l'esenzione è mirata, non totale.
        Assert.False(await check.AltDataPoints.AnyAsync(a => a.Title == "vecchia-grezza"));
        // ...quella SCORATA sopravvive, per quanto vecchia: è patrimonio.
        Assert.True(await check.AltDataPoints.AnyAsync(a => a.Title == "vecchia-scorata"));
        // ...e il punteggio 0 conta come punteggio.
        Assert.True(await check.AltDataPoints.AnyAsync(a => a.Title == "vecchia-neutra"));
        Assert.True(await check.AltDataPoints.AnyAsync(a => a.Title == "recente"));
        // La metrica-cache (OI) muore oltre la retention...
        Assert.False(await check.SentimentMetricPoints.AnyAsync(p => p.Metric == SentimentMetrics.OpenInterest && p.TimestampUtc < DateTime.UtcNow.AddDays(-80)));
        // ...le serie storiche sopravvivono.
        Assert.True(await check.SentimentMetricPoints.AnyAsync(p => p.Metric == SentimentMetrics.FundingRate));
        Assert.True(await check.SentimentMetricPoints.AnyAsync(p => p.Source == SentimentMetricSources.BinanceLiquidations));
        Assert.True(await check.SentimentMetricPoints.AnyAsync(p => p.Source == SentimentMetricSources.FearGreed));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
