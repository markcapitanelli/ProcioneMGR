using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D2, completamento] Verifica della fotografia in memoria e del job che la aggiorna — i due
/// pezzi che il PRD §5e chiedeva ("alert accanto al widget in Home" + "job di calcolo periodico")
/// e che mancavano: il pannello in <c>/feature-selection</c> risponde solo a chi va a cercarlo.
/// </summary>
public class FactorDriftSnapshotTests
{
    private static FactorDriftReport Report(string name, FactorDriftStatus status, double reference, double recent) =>
        new(name, name, 0, reference, recent, 0.04, status, "…", []);

    [Fact]
    public void AnEmptySnapshot_HasNoAlertsAndNoRun()
    {
        var snapshot = new FactorDriftSnapshot();

        Assert.Empty(snapshot.Alerts);
        Assert.Empty(snapshot.All);
        Assert.Null(snapshot.LastRunUtc);
    }

    [Fact]
    public void Alerts_PutTheMostSevereFirst()
    {
        var snapshot = new FactorDriftSnapshot();
        var now = DateTime.UtcNow;
        snapshot.Replace(
        [
            new FactorDriftSeriesSnapshot("BTC/USDT", "1h", now,
            [
                Report("spento", FactorDriftStatus.Weakening, 0.06, 0.01),
                Report("stabile", FactorDriftStatus.Stable, 0.06, 0.06),
                Report("invertito", FactorDriftStatus.SignFlip, 0.06, -0.06),
            ]),
        ], now);

        var alerts = snapshot.Alerts;

        // SignFlip è più grave di Weakening; lo Stable non è un alert e non compare.
        Assert.Equal(2, alerts.Count);
        Assert.Equal("invertito", alerts[0].Report.FeatureName);
        Assert.Equal("spento", alerts[1].Report.FeatureName);
        Assert.Equal(now, snapshot.LastRunUtc);
    }

    [Fact]
    public void Replace_SwapsTheWholePictureInsteadOfMerging()
    {
        // Una fotografia parziale sarebbe peggio di nessuna: se una serie sparisce dalla watchlist,
        // i suoi vecchi allarmi non devono restare appesi.
        var snapshot = new FactorDriftSnapshot();
        snapshot.Replace([new FactorDriftSeriesSnapshot("BTC/USDT", "1h", DateTime.UtcNow,
            [Report("vecchio", FactorDriftStatus.Weakening, 0.06, 0.01)])], DateTime.UtcNow);

        snapshot.Replace([new FactorDriftSeriesSnapshot("ETH/USDT", "4h", DateTime.UtcNow,
            [Report("nuovo", FactorDriftStatus.SignFlip, 0.06, -0.06)])], DateTime.UtcNow);

        Assert.Single(snapshot.All);
        Assert.Equal("nuovo", Assert.Single(snapshot.Alerts).Report.FeatureName);
    }

    [Fact]
    public void SeriesSnapshot_ExposesOnlyItsAlerts()
    {
        var s = new FactorDriftSeriesSnapshot("BTC/USDT", "1h", DateTime.UtcNow,
        [
            Report("a", FactorDriftStatus.Stable, 0.06, 0.06),
            Report("b", FactorDriftStatus.Insufficient, 0, 0),
            Report("c", FactorDriftStatus.Weakening, 0.06, 0.01),
        ]);

        Assert.Equal("c", Assert.Single(s.Alerts).FeatureName);
    }
}

/// <summary>Il job che alimenta la fotografia, con Postgres effimero e candele vere.</summary>
[Collection("Postgres")]
public sealed class FactorDriftWorkerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public FactorDriftWorkerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<(FactorDriftWorker Worker, FactorDriftSnapshot Snapshot, IDbContextFactory<ApplicationDbContext> Db)>
        BuildAsync(Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        var snapshot = new FactorDriftSnapshot();
        var worker = new FactorDriftWorker(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new FactorDriftAnalyzer(),
            new AlphaFactorFactory(),
            snapshot,
            config,
            _provider.GetRequiredService<ILogger<FactorDriftWorker>>());

        return (worker, snapshot, dbFactory);
    }

    /// <summary>Serie con una relazione fattore→rendimento che si spegne a metà: alert atteso.</summary>
    private static List<OhlcvData> DecayingSeries(int n, string symbol, string timeframe, int seed = 4)
    {
        var rnd = new Random(seed);
        var candles = new List<OhlcvData>(n);
        var price = 100m;
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var prevReturn = 0d;

        for (var i = 0; i < n; i++)
        {
            // Prima metà: forte momentum (il rendimento continua quello precedente).
            // Seconda metà: rumore puro. Il fattore Momentum deve accorgersene.
            var drift = i < n / 2
                ? prevReturn * 0.7 + (rnd.NextDouble() - 0.5) * 0.004
                : (rnd.NextDouble() - 0.5) * 0.02;
            prevReturn = drift;
            var next = Math.Max(1m, price * (1m + (decimal)drift));
            candles.Add(new OhlcvData
            {
                Symbol = symbol,
                Timeframe = timeframe,
                TimestampUtc = start.AddHours(i),
                Open = price,
                High = Math.Max(price, next) * 1.001m,
                Low = Math.Min(price, next) * 0.999m,
                Close = next,
                Volume = 1000m + (decimal)(rnd.NextDouble() * 100),
            });
            price = next;
        }
        return candles;
    }

    private static async Task SeedAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        string symbol, string timeframe, int candleCount, bool enabled = true)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.TrackedSeries.Add(new TrackedSeries
        {
            Exchange = ExchangeName.Binance,
            Symbol = symbol,
            Timeframe = timeframe,
            Enabled = enabled,
        });
        db.OhlcvData.AddRange(DecayingSeries(candleCount, symbol, timeframe));
        await db.SaveChangesAsync();
    }

    // --- Il giro del job -------------------------------------------------------------------------

    [Fact]
    public async Task RunOnce_PopulatesTheSnapshotForEachTrackedSeries()
    {
        var (worker, snapshot, db) = await BuildAsync();
        await SeedAsync(db, "BTC/USDT", "1h", 6000);
        await SeedAsync(db, "ETH/USDT", "1h", 6000);

        await worker.RunOnceAsync();

        Assert.Equal(2, snapshot.All.Count);
        Assert.NotNull(snapshot.LastRunUtc);
        Assert.All(snapshot.All, s => Assert.NotEmpty(s.Reports));
        // Otto fattori scritti a mano monitorati per serie.
        Assert.All(snapshot.All, s => Assert.Equal(8, s.Reports.Count));
    }

    [Fact]
    public async Task RunOnce_OnASeriesWhereAFactorDies_RaisesAnAlert()
    {
        // La serie ha momentum forte nella prima metà e rumore nella seconda: il monitor deve
        // accorgersene. È il controllo che il job misuri davvero, invece di girare a vuoto.
        var (worker, snapshot, db) = await BuildAsync();
        await SeedAsync(db, "BTC/USDT", "1h", 8000);

        await worker.RunOnceAsync();

        var alerts = snapshot.Alerts;
        Assert.NotEmpty(alerts);
        Assert.Contains(alerts, a => a.Report.FeatureName == "Momentum");
    }

    [Fact]
    public async Task RunOnce_SkipsSeriesWithTooFewCandles()
    {
        var (worker, snapshot, db) = await BuildAsync();
        await SeedAsync(db, "TINY/USDT", "1h", 200); // sotto il minimo di 500

        await worker.RunOnceAsync();

        Assert.Empty(snapshot.All);
        Assert.NotNull(snapshot.LastRunUtc); // il giro è comunque avvenuto
    }

    [Fact]
    public async Task RunOnce_IgnoresDisabledSeries()
    {
        var (worker, snapshot, db) = await BuildAsync();
        await SeedAsync(db, "OFF/USDT", "1h", 6000, enabled: false);

        await worker.RunOnceAsync();

        Assert.Empty(snapshot.All);
    }

    [Fact]
    public async Task RunOnce_RespectsTheMaxSeriesCap()
    {
        // Il tetto esiste perché il monitor non diventi un consumo di CPU proporzionale alla
        // watchlist: chi vuole guardare tutto lo fa su richiesta da /feature-selection.
        var (worker, snapshot, db) = await BuildAsync(new Dictionary<string, string?>
        {
            ["FactorDrift:MaxSeries"] = "1",
        });
        await SeedAsync(db, "AAA/USDT", "1h", 6000);
        await SeedAsync(db, "BBB/USDT", "1h", 6000);

        await worker.RunOnceAsync();

        Assert.Single(snapshot.All);
    }

    [Fact]
    public async Task RunOnce_WithNoTrackedSeries_LeavesAnEmptyButValidSnapshot()
    {
        var (worker, snapshot, _) = await BuildAsync();

        await worker.RunOnceAsync();

        Assert.Empty(snapshot.All);
        Assert.Empty(snapshot.Alerts);
        Assert.NotNull(snapshot.LastRunUtc);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
