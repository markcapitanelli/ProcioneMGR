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
/// [U4] Prima di questa persistenza gli esiti del drift vivevano SOLO nei log: la UI non poteva
/// mostrare né l'ultimo esito né lo storico, e "nessuna riga" non distingueva "tutto pulito" da
/// "il worker non gira". Contratti: una riga per modello per tick ANCHE se pulito, top-feature in
/// JSON, flag ChampionRetired coerente col registry, prune oltre la retention.
/// </summary>
[Collection("Postgres")]
public sealed class FeatureDriftWorkerPersistenceTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public FeatureDriftWorkerPersistenceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>Monitor scriptato: gravità fissa per ogni modello valutato.</summary>
    private sealed class ScriptedMonitor(DriftSeverity severity) : IFeatureDriftMonitor
    {
        public Task<IReadOnlyList<FactorDriftReport>> EvaluateAsync(
            SavedMlModel model, IReadOnlyList<OhlcvData> recentCandles, DriftThresholds? thresholds = null, CancellationToken ct = default)
        {
            IReadOnlyList<FactorDriftReport> reports =
            [
                new FactorDriftReport
                {
                    FeatureName = "Mom1",
                    Results = severity == DriftSeverity.None
                        ? [new DriftResult("Psi", 0.01, null, DriftSeverity.None, "stabile")]
                        : [new DriftResult("Psi", 0.51, null, severity, "shift")],
                },
                new FactorDriftReport
                {
                    FeatureName = "Vol5",
                    Results = [new DriftResult("Ks", 0.02, 0.9, DriftSeverity.None, "stabile")],
                },
            ];
            return Task.FromResult(reports);
        }
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

    private static async Task<int> AddModelAsync(IDbContextFactory<ApplicationDbContext> factory, ModelStage stage)
    {
        await using var db = await factory.CreateDbContextAsync();
        var user = new ApplicationUser { UserName = $"u_{Guid.NewGuid():N}", Email = "t@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var model = new SavedMlModel
        {
            UserId = user.Id, Name = $"m_{Guid.NewGuid():N}", ModelType = "Linear",
            Symbol = "BTCUSDT", Timeframe = "1h", FactorsJson = "[]", ModelBytes = [1],
            DeflatedSharpe = 0.9, Stage = stage,
            PromotedAtUtc = stage == ModelStage.Champion ? DateTime.UtcNow : null,
        };
        db.SavedMlModels.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    private FeatureDriftWorker Worker(IDbContextFactory<ApplicationDbContext> factory, DriftSeverity severity, DriftMonitorOptions? opt = null)
        => new(factory, new ScriptedMonitor(severity),
            new ModelRegistry(factory, new ModelRegistryOptions(), NullLogger<ModelRegistry>.Instance),
            (opt ?? new DriftMonitorOptions { RetireChampionOnAlert = true, MinAlertsToRetire = 1 }).AsMonitor(),
            NullLogger<FeatureDriftWorker>.Instance);

    [Fact]
    public async Task Tick_CleanModel_PersistsOneRowWithZeroDrift()
    {
        var factory = await BuildFactoryAsync();
        var modelId = await AddModelAsync(factory, ModelStage.Staging);

        await Worker(factory, DriftSeverity.None).TickAsync(CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var row = Assert.Single(await db.DriftCheckResults.ToListAsync());
        Assert.Equal(modelId, row.ModelId);
        Assert.Equal(2, row.TotalFeatures);
        Assert.Equal(0, row.DriftingFeatures);
        Assert.Equal(0, row.AlertFeatures);
        Assert.Equal(DriftSeverity.None, row.Overall);
        Assert.Null(row.TopFeaturesJson);
        Assert.False(row.ChampionRetired);
    }

    [Fact]
    public async Task Tick_ChampionInAlert_PersistsRowWithRetireFlagAndTopFeatures()
    {
        var factory = await BuildFactoryAsync();
        var modelId = await AddModelAsync(factory, ModelStage.Champion);

        await Worker(factory, DriftSeverity.Alert).TickAsync(CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var row = Assert.Single(await db.DriftCheckResults.ToListAsync());
        Assert.Equal(1, row.DriftingFeatures);
        Assert.Equal(1, row.AlertFeatures);
        Assert.Equal(DriftSeverity.Alert, row.Overall);
        Assert.True(row.ChampionRetired);
        Assert.Contains("Mom1", row.TopFeaturesJson);
        Assert.Contains("Alert", row.TopFeaturesJson);
        Assert.Contains("Psi", row.TopFeaturesJson);

        // Coerenza col registry: il flag sulla riga corrisponde allo stato reale del modello.
        var model = await db.SavedMlModels.FindAsync(modelId);
        Assert.Equal(ModelStage.Retired, model!.Stage);
    }

    [Fact]
    public async Task Tick_TwoTicks_TwoRowsPerModel_HistoryAccumulates()
    {
        var factory = await BuildFactoryAsync();
        await AddModelAsync(factory, ModelStage.Staging);
        var worker = Worker(factory, DriftSeverity.Warning);

        await worker.TickAsync(CancellationToken.None);
        await worker.TickAsync(CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(2, await db.DriftCheckResults.CountAsync());
    }

    [Fact]
    public async Task Tick_PrunesRowsOlderThanRetention()
    {
        var factory = await BuildFactoryAsync();
        await AddModelAsync(factory, ModelStage.Staging);

        await using (var db = await factory.CreateDbContextAsync())
        {
            db.DriftCheckResults.Add(new DriftCheckResult
            {
                CheckedAtUtc = DateTime.UtcNow.AddDays(-(FeatureDriftWorker.ResultRetentionDays + 10)),
                ModelId = 999, ModelName = "vecchio", Symbol = "ETHUSDT", Timeframe = "4h",
            });
            db.DriftCheckResults.Add(new DriftCheckResult
            {
                CheckedAtUtc = DateTime.UtcNow.AddDays(-1),
                ModelId = 998, ModelName = "recente", Symbol = "ETHUSDT", Timeframe = "4h",
            });
            await db.SaveChangesAsync();
        }

        await Worker(factory, DriftSeverity.None).TickAsync(CancellationToken.None);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var names = await db.DriftCheckResults.Select(r => r.ModelName).ToListAsync();
            Assert.DoesNotContain("vecchio", names);   // oltre retention: eliminata
            Assert.Contains("recente", names);         // dentro retention: conservata
            Assert.Equal(2, names.Count);              // recente + la riga del tick appena fatto
        }
    }

    [Fact]
    public void BuildTopFeaturesJson_OrdersBySeverityAndCapsAtFive()
    {
        var drifting = Enumerable.Range(0, 8).Select(i => new FactorDriftReport
        {
            FeatureName = $"f{i}",
            Results = [new DriftResult("Psi", i, null, i == 7 ? DriftSeverity.Alert : DriftSeverity.Warning, "d")],
        }).ToList();

        var json = FeatureDriftWorker.BuildTopFeaturesJson(drifting)!;

        var doc = System.Text.Json.JsonDocument.Parse(json);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(5, items.Count);                                        // cap a 5
        Assert.Equal("f7", items[0].GetProperty("name").GetString());        // l'Alert viene prima
        Assert.Equal("Alert", items[0].GetProperty("severity").GetString());
        Assert.Equal("f6", items[1].GetProperty("name").GetString());        // poi i Warning per score
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
