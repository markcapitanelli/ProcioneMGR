using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Monitoring.Drift;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Security;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del Model Registry (Fase 2): gate del Deflated Sharpe sulla promozione a Champion, invariante
/// "un solo Champion per (Symbol, Timeframe)", e ciclo chiuso col drift (Champion in Alert → Retired +
/// retrain accodato, mai Live). DB Postgres effimero (Testcontainers) via EnsureCreated.
/// </summary>
[Collection("Postgres")]
public class ModelRegistryTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public ModelRegistryTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> BuildFactoryAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private static async Task<string> SeedUserAsync(IDbContextFactory<ApplicationDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var user = new ApplicationUser { UserName = "tester", Email = "tester@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<int> AddModelAsync(
        IDbContextFactory<ApplicationDbContext> factory, string userId,
        string symbol, string timeframe, double? deflatedSharpe, ModelStage stage = ModelStage.Staging)
    {
        await using var db = await factory.CreateDbContextAsync();
        var model = new SavedMlModel
        {
            UserId = userId, Name = $"m_{Guid.NewGuid():N}", ModelType = "Linear",
            Symbol = symbol, Timeframe = timeframe, FactorsJson = "[]", ModelBytes = new byte[] { 1 },
            DeflatedSharpe = deflatedSharpe, Stage = stage,
            PromotedAtUtc = stage == ModelStage.Champion ? DateTime.UtcNow : null,
        };
        db.SavedMlModels.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    private static ModelRegistry NewRegistry(IDbContextFactory<ApplicationDbContext> factory, double minDsr = 0.0)
        => new(factory, new ModelRegistryOptions { MinChampionDeflatedSharpe = minDsr }, NullLogger<ModelRegistry>.Instance);

    // --- Gate DSR + unicità del Champion -----------------------------------------------------

    [Fact]
    public async Task Promote_FirstChampion_SucceedsWhenNoIncumbent()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", deflatedSharpe: 0.90);
        var registry = NewRegistry(f);

        var outcome = await registry.TryPromoteToChampionAsync(id);

        Assert.True(outcome.Promoted, outcome.Reason);
        var champ = await registry.GetChampionAsync("BTCUSDT", "1h");
        Assert.NotNull(champ);
        Assert.Equal(id, champ!.Id);
    }

    [Fact]
    public async Task Promote_LowerDsr_IsRejected_AndIncumbentStays()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var champ = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.90, ModelStage.Champion);
        var weak = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.80);
        var registry = NewRegistry(f);

        var outcome = await registry.TryPromoteToChampionAsync(weak);

        Assert.False(outcome.Promoted);
        var current = await registry.GetChampionAsync("BTCUSDT", "1h");
        Assert.Equal(champ, current!.Id); // l'incumbent resta
    }

    [Fact]
    public async Task Promote_HigherDsr_ReplacesIncumbent_AndKeepsSingleChampion()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var oldChamp = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.80, ModelStage.Champion);
        var better = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.95);
        var registry = NewRegistry(f);

        var outcome = await registry.TryPromoteToChampionAsync(better);

        Assert.True(outcome.Promoted, outcome.Reason);
        Assert.Equal(oldChamp, outcome.DemotedChampionId);

        await using var db = await f.CreateDbContextAsync();
        var champions = await db.SavedMlModels.Where(m => m.Symbol == "BTCUSDT" && m.Timeframe == "1h" && m.Stage == ModelStage.Champion).ToListAsync();
        Assert.Single(champions);                        // invariante: un solo Champion
        Assert.Equal(better, champions[0].Id);
        var demoted = await db.SavedMlModels.FindAsync(oldChamp);
        Assert.Equal(ModelStage.Retired, demoted!.Stage); // il vecchio è Retired
    }

    [Fact]
    public async Task Promote_WithoutDsr_IsRejected()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", deflatedSharpe: null);
        var registry = NewRegistry(f);

        var outcome = await registry.TryPromoteToChampionAsync(id);

        Assert.False(outcome.Promoted);
        Assert.Null(await registry.GetChampionAsync("BTCUSDT", "1h"));
    }

    [Fact]
    public async Task Champion_IsScopedPerSymbolTimeframe()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var btc = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9);
        var eth = await AddModelAsync(f, user, "ETHUSDT", "1h", 0.5);
        var registry = NewRegistry(f);

        Assert.True((await registry.TryPromoteToChampionAsync(btc)).Promoted);
        Assert.True((await registry.TryPromoteToChampionAsync(eth)).Promoted); // gruppo diverso: non compete col BTC

        Assert.Equal(btc, (await registry.GetChampionAsync("BTCUSDT", "1h"))!.Id);
        Assert.Equal(eth, (await registry.GetChampionAsync("ETHUSDT", "1h"))!.Id);
    }

    [Fact]
    public async Task Retire_WithRetrain_SetsReasonAndRetrainMarker()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var id = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, ModelStage.Champion);
        var registry = NewRegistry(f);

        await registry.RetireAsync(id, "test reason", requestRetrain: true);

        await using var db = await f.CreateDbContextAsync();
        var m = await db.SavedMlModels.FindAsync(id);
        Assert.Equal(ModelStage.Retired, m!.Stage);
        Assert.Equal("test reason", m.RetiredReason);
        Assert.NotNull(m.RetiredAtUtc);
        Assert.NotNull(m.RetrainRequestedAtUtc);
    }

    // --- Ciclo chiuso col drift (worker + monitor fittizio + registry reale) -----------------

    private sealed class AlertMonitor : IFeatureDriftMonitor
    {
        public Task<IReadOnlyList<FactorDriftReport>> EvaluateAsync(
            SavedMlModel model, IReadOnlyList<OhlcvData> recentCandles, DriftThresholds? thresholds = null, CancellationToken ct = default)
        {
            IReadOnlyList<FactorDriftReport> reports = new[]
            {
                new FactorDriftReport
                {
                    FeatureName = "Mom1",
                    Results = new[] { new DriftResult("Psi", 0.5, null, DriftSeverity.Alert, "shift") },
                },
            };
            return Task.FromResult(reports);
        }
    }

    [Fact]
    public async Task DriftWorker_ChampionInAlert_IsRetiredAndRetrainRequested()
    {
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var champ = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, ModelStage.Champion);
        var registry = NewRegistry(f);

        var worker = new FeatureDriftWorker(
            f, new AlertMonitor(), registry,
            new DriftMonitorOptions { Enabled = true, RetireChampionOnAlert = true, MinAlertsToRetire = 1 }.AsMonitor(),
            NullLogger<FeatureDriftWorker>.Instance);

        await worker.TickAsync(CancellationToken.None);

        await using var db = await f.CreateDbContextAsync();
        var m = await db.SavedMlModels.FindAsync(champ);
        Assert.Equal(ModelStage.Retired, m!.Stage);
        Assert.NotNull(m.RetrainRequestedAtUtc);
        Assert.Contains("drift", m.RetiredReason);
        Assert.Null(await registry.GetChampionAsync("BTCUSDT", "1h")); // niente Champion drifted attivo
    }

    [Fact]
    public async Task DriftWorker_StagingModelInAlert_IsNotRetired()
    {
        // Solo i Champion vengono ritirati dal ciclo chiuso: uno Staging in drift resta (lo si valuta a mano).
        var f = await BuildFactoryAsync();
        var user = await SeedUserAsync(f);
        var staging = await AddModelAsync(f, user, "BTCUSDT", "1h", 0.9, ModelStage.Staging);
        var registry = NewRegistry(f);

        var worker = new FeatureDriftWorker(
            f, new AlertMonitor(), registry, new DriftMonitorOptions { RetireChampionOnAlert = true }.AsMonitor(),
            NullLogger<FeatureDriftWorker>.Instance);
        await worker.TickAsync(CancellationToken.None);

        await using var db = await f.CreateDbContextAsync();
        var m = await db.SavedMlModels.FindAsync(staging);
        Assert.Equal(ModelStage.Staging, m!.Stage);
        Assert.Null(m.RetrainRequestedAtUtc);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
