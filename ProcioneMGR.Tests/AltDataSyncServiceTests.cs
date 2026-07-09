using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.AltData;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Sentiment;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="AltDataSyncService"/>: inserimento con classificazione/scoring automatici,
/// deduplica fra sync successive, e resilienza a una fonte che lancia un'eccezione (non deve far
/// fallire l'intera sync — stesso principio di <c>MarketDataSyncService</c>).
/// </summary>
[Collection("Postgres")]
public class AltDataSyncServiceTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public AltDataSyncServiceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class FakeAltDataSource(string name, IReadOnlyList<RawNewsItem> items, Exception? throwOnFetch = null) : IAltDataSource
    {
        public string Name { get; } = name;
        public Task<IReadOnlyList<RawNewsItem>> FetchLatestAsync(CancellationToken ct) =>
            throwOnFetch is not null ? throw throwOnFetch : Task.FromResult(items);
    }

    private async Task<(IDbContextFactory<ApplicationDbContext> DbFactory, AltDataSyncService Service)> BuildAsync(params IAltDataSource[] sources)
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

        var syncService = new AltDataSyncService(sources, new KeywordSentimentScorer(), dbFactory, NullLogger<AltDataSyncService>.Instance);
        return (dbFactory, syncService);
    }

    [Fact]
    public async Task SyncAllAsync_InsertsNewItems_WithClassificationAndSentiment()
    {
        var items = new List<RawNewsItem>
        {
            new(DateTime.UtcNow, "SEC approves new Bitcoin ETF", "Market surges on approval news", "https://example.com/1"),
        };
        var (dbFactory, service) = await BuildAsync(new FakeAltDataSource("TestSource", items));

        var inserted = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, inserted);
        await using var db = await dbFactory.CreateDbContextAsync();
        var saved = await db.AltDataPoints.SingleAsync();
        Assert.Equal("Regulatory", saved.Category);
        Assert.Contains("BTC", saved.SymbolsJson);
        Assert.True(saved.SentimentScore > 0m);
    }

    [Fact]
    public async Task SyncAllAsync_SecondRun_DoesNotDuplicate()
    {
        var items = new List<RawNewsItem> { new(DateTime.UtcNow, "Some headline", null, "https://example.com/dup") };
        var (dbFactory, service) = await BuildAsync(new FakeAltDataSource("TestSource", items));

        var first = await service.SyncAllAsync(CancellationToken.None);
        var second = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(1, await db.AltDataPoints.CountAsync());
    }

    [Fact]
    public async Task SyncAllAsync_OneSourceThrows_OthersStillSynced()
    {
        var goodItems = new List<RawNewsItem> { new(DateTime.UtcNow, "Working source headline", null, "https://example.com/ok") };
        var (dbFactory, service) = await BuildAsync(
            new FakeAltDataSource("Broken", [], throwOnFetch: new HttpRequestException("simulated network failure")),
            new FakeAltDataSource("Working", goodItems));

        var inserted = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(1, inserted);
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal("Working", (await db.AltDataPoints.SingleAsync()).Source);
    }

    [Fact]
    public async Task SyncAllAsync_ItemsWithoutUrl_DedupeByTitle()
    {
        var items = new List<RawNewsItem> { new(DateTime.UtcNow, "No-link headline", null, null) };
        var (dbFactory, service) = await BuildAsync(new FakeAltDataSource("TestSource", items));

        await service.SyncAllAsync(CancellationToken.None);
        var second = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(0, second);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
