using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Monitoring.Drift;
using ProcioneMGR.Services.Security;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del <see cref="FeatureDriftMonitor"/>: dato un modello i cui fattori sono calcolati su una
/// finestra di training a BASSA volatilità (reference, letta dal DB) e su candele recenti ad ALTA
/// volatilità (current), il fattore di volatilità realizzata deve risultare in drift. Verifica
/// l'integrazione reale (ricostruzione fattori dal FactorsJson + detector).
/// </summary>
[Collection("Postgres")]
public sealed class FeatureDriftMonitorTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public FeatureDriftMonitorTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> BuildDbAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return dbFactory;
    }

    private static List<OhlcvData> BuildWalk(string symbol, string tf, DateTime start, int n, double stepVol, int seed)
    {
        var rnd = new Random(seed);
        var list = new List<OhlcvData>(n);
        var price = 100m;
        for (var i = 0; i < n; i++)
        {
            var open = price;
            var shock = (decimal)((rnd.NextDouble() - 0.5) * stepVol);
            price = Math.Max(1m, open + shock);
            list.Add(new OhlcvData
            {
                Symbol = symbol,
                Timeframe = tf,
                TimestampUtc = start.AddHours(i),
                Open = open,
                High = Math.Max(open, price) + 0.1m,
                Low = Math.Min(open, price) - 0.1m,
                Close = price,
                Volume = 100m + i % 10,
            });
        }
        return list;
    }

    [Fact]
    public async Task Evaluate_DetectsVolatilityDrift_BetweenTrainingAndRecent()
    {
        var dbFactory = await BuildDbAsync();
        var trainFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Reference (in DB): 400 candele a bassa volatilità nella finestra di training.
        var reference = BuildWalk("BTCUSDT", "1h", trainFrom, 400, stepVol: 0.4, seed: 11);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.OhlcvData.AddRange(reference);
            await db.SaveChangesAsync();
        }

        // Current (passate a mano): 250 candele ad alta volatilità (regime cambiato).
        var recent = BuildWalk("BTCUSDT", "1h", trainFrom.AddHours(500), 250, stepVol: 6.0, seed: 22);

        var factors = new List<SavedFactorSpecDto>
        {
            new("RealizedVol", "RealizedVol", new Dictionary<string, decimal> { ["Lookback"] = 20m }),
            new("Momentum", "Momentum", new Dictionary<string, decimal> { ["Lookback"] = 20m, ["Skip"] = 0m }),
        };
        var model = new SavedMlModel
        {
            Name = "test-model",
            ModelType = "Linear",
            Symbol = "BTCUSDT",
            Timeframe = "1h",
            TrainingDataFrom = trainFrom,
            TrainingDataTo = trainFrom.AddHours(399),
            ForwardHorizon = 1,
            FactorsJson = System.Text.Json.JsonSerializer.Serialize(factors),
        };

        var detectors = new IFeatureDriftDetector[] { new PsiDriftDetector(), new KsDriftDetector(), new PageHinkleyDetector() };
        var monitor = new FeatureDriftMonitor(dbFactory, new AlphaFactorFactory(), detectors);

        var reports = await monitor.EvaluateAsync(model, recent);

        Assert.Equal(2, reports.Count);
        var vol = reports.Single(r => r.FeatureName == "RealizedVol");
        Assert.True(vol.ReferenceCount > 100 && vol.CurrentCount > 100);
        Assert.Equal(3, vol.Results.Count); // un esito per detector
        Assert.NotEqual(DriftSeverity.None, vol.Overall); // il regime di volatilità è cambiato: drift atteso
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
