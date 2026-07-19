using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Sentiment;
using ProcioneMGR.Services.Sentiment.Metrics;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="SentimentMetricSyncService"/>: inserimento, dedupe fra sync sovrapposte,
/// isolamento per fonte (una che lancia non fa fallire le altre), backfill una tantum delle fonti
/// backfillable, e popolamento del registro di salute.
/// </summary>
[Collection("Postgres")]
public class SentimentMetricSyncServiceTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public SentimentMetricSyncServiceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class FakeMetricSource(string name, IReadOnlyList<SentimentMetricSample> samples, Exception? throwOnFetch = null) : ISentimentMetricSource
    {
        public int FetchCalls { get; private set; }
        public string Name { get; } = name;
        public Task<IReadOnlyList<SentimentMetricSample>> FetchLatestAsync(CancellationToken ct)
        {
            FetchCalls++;
            return throwOnFetch is not null ? throw throwOnFetch : Task.FromResult(samples);
        }
    }

    private sealed class FakeBackfillableSource(string name, IReadOnlyList<SentimentMetricSample> latest, IReadOnlyList<SentimentMetricSample> history) : IBackfillableMetricSource
    {
        public int LatestCalls { get; private set; }
        public int HistoryCalls { get; private set; }
        public string Name { get; } = name;
        public Task<IReadOnlyList<SentimentMetricSample>> FetchLatestAsync(CancellationToken ct)
        {
            LatestCalls++;
            return Task.FromResult(latest);
        }
        public Task<IReadOnlyList<SentimentMetricSample>> FetchFullHistoryAsync(CancellationToken ct)
        {
            HistoryCalls++;
            return Task.FromResult(history);
        }
    }

    private static SentimentMetricSample Sample(int hoursAgo, string metric = SentimentMetrics.GlobalLongShortRatio, string symbol = "BTC", decimal value = 1.5m)
        => new(DateTime.UtcNow.Date.AddHours(-hoursAgo), metric, symbol, value);

    private async Task<(IDbContextFactory<ApplicationDbContext> DbFactory, SentimentMetricSyncService Service, SentimentSourceHealthRegistry Health)> BuildAsync(
        params ISentimentMetricSource[] sources)
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

        var health = new SentimentSourceHealthRegistry();
        var service = new SentimentMetricSyncService(sources, dbFactory, NullLogger<SentimentMetricSyncService>.Instance, health);
        return (dbFactory, service, health);
    }

    [Fact]
    public async Task SyncAllAsync_InsertsSamples_AndDeduplicatesOverlappingRuns()
    {
        var samples = new[] { Sample(3), Sample(2), Sample(1) };
        var source = new FakeMetricSource("BinanceFutures", samples);
        var (dbFactory, service, _) = await BuildAsync(source);

        Assert.Equal(3, await service.SyncAllAsync(CancellationToken.None));
        // Seconda sync con gli stessi punti (le finestre si sovrappongono di proposito): zero nuovi.
        Assert.Equal(0, await service.SyncAllAsync(CancellationToken.None));

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(3, await db.SentimentMetricPoints.CountAsync());
    }

    [Fact]
    public async Task SyncAllAsync_OneSourceThrows_OthersStillSync_AndHealthTracksBoth()
    {
        var ok = new FakeMetricSource("BinanceFutures", [Sample(1)]);
        var broken = new FakeMetricSource("FonteRotta", [], new HttpRequestException("451"));
        var (dbFactory, service, health) = await BuildAsync(broken, ok);

        var inserted = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, inserted);
        var snapshot = health.Snapshot();
        var okHealth = Assert.Single(snapshot, h => h.Name == "BinanceFutures");
        Assert.NotNull(okHealth.LastSuccessUtc);
        var brokenHealth = Assert.Single(snapshot, h => h.Name == "FonteRotta");
        Assert.NotNull(brokenHealth.LastErrorUtc);
        Assert.Contains("451", brokenHealth.LastError);
    }

    [Fact]
    public async Task SyncAllAsync_BackfillableSource_FetchesFullHistoryOnlyWhenTableIsEmpty()
    {
        var history = Enumerable.Range(1, 30)
            .Select(d => new SentimentMetricSample(DateTime.UtcNow.Date.AddDays(-d), SentimentMetrics.FearGreedIndex, "", 50m))
            .ToList();
        var latest = new[] { new SentimentMetricSample(DateTime.UtcNow.Date, SentimentMetrics.FearGreedIndex, "", 28m) };
        var source = new FakeBackfillableSource("FearGreed", latest, history);
        var (dbFactory, service, _) = await BuildAsync(source);

        // Prima sync: tabella vuota → backfill dell'intero storico.
        Assert.Equal(30, await service.SyncAllAsync(CancellationToken.None));
        Assert.Equal(1, source.HistoryCalls);
        Assert.Equal(0, source.LatestCalls);

        // Seconda sync: righe presenti → solo gli ultimi punti.
        Assert.Equal(1, await service.SyncAllAsync(CancellationToken.None));
        Assert.Equal(1, source.HistoryCalls);
        Assert.Equal(1, source.LatestCalls);

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(31, await db.SentimentMetricPoints.CountAsync(p => p.Source == "FearGreed"));
    }

    [Fact]
    public async Task SyncAllAsync_SameTimestampDifferentMetricOrSymbol_AreDistinctPoints()
    {
        var ts = DateTime.UtcNow.Date;
        var source = new FakeMetricSource("BinanceFutures",
        [
            new SentimentMetricSample(ts, SentimentMetrics.GlobalLongShortRatio, "BTC", 1.2m),
            new SentimentMetricSample(ts, SentimentMetrics.GlobalLongShortRatio, "ETH", 1.1m),
            new SentimentMetricSample(ts, SentimentMetrics.FundingRate, "BTC", 0.01m),
            new SentimentMetricSample(ts, SentimentMetrics.GlobalLongShortRatio, "BTC", 9.9m), // duplicato nel batch
        ]);
        var (dbFactory, service, _) = await BuildAsync(source);

        Assert.Equal(3, await service.SyncAllAsync(CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
