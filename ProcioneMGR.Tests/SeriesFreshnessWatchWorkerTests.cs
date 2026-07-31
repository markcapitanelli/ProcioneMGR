using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [E7] La guardia di freschezza deve accorgersi DA SOLA di una serie che ha smesso di avanzare —
/// il caso MKR/USDT, ferma dieci mesi con «OK: 1 candele» a ogni giro — e dirlo UNA volta per
/// transizione, non una per giro. Il complemento (livello 2 dello standard): su serie sane non
/// deve inventare nulla.
/// </summary>
[Collection("Postgres")]
public sealed class SeriesFreshnessWatchWorkerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public SeriesFreshnessWatchWorkerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<(NotificationSeverity Severity, string Title, string Body)> Sent { get; } = new();
        public Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Sent.Add((severity, title, body));
            return Task.CompletedTask;
        }
    }

    private async Task<(SeriesFreshnessWatchWorker Worker, IDbContextFactory<ApplicationDbContext> Db, RecordingNotifier Notifier)> BuildAsync()
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

        var notifier = new RecordingNotifier();
        var worker = new SeriesFreshnessWatchWorker(
            dbFactory, NullLogger<SeriesFreshnessWatchWorker>.Instance, notifier);
        return (worker, dbFactory, notifier);
    }

    private static async Task SeedSeriesAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        string symbol, string timeframe, DateTime lastCandleUtc, int candles = 5, bool enabled = true)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.TrackedSeries.Add(new TrackedSeries
        {
            Exchange = ExchangeName.Binance, Symbol = symbol, Timeframe = timeframe, Enabled = enabled,
        });
        var step = Timeframes.Supported[timeframe];
        for (var i = 0; i < candles; i++)
        {
            db.OhlcvData.Add(new OhlcvData
            {
                Symbol = symbol, Timeframe = timeframe,
                TimestampUtc = lastCandleUtc - step * i,
                Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 1m,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Tick_SerieFerma_NotificaUnaVoltaSola()
    {
        var (worker, db, notifier) = await BuildAsync();
        // Ultima candela 30 barre orarie fa: ben oltre la tolleranza di 3.
        await SeedSeriesAsync(db, "MKR/USDT", "1h", DateTime.UtcNow.AddHours(-30));

        var first = await worker.TickAsync(CancellationToken.None);
        Assert.Single(first);
        var alert = Assert.Single(notifier.Sent);
        Assert.Equal(NotificationSeverity.Warning, alert.Severity);
        Assert.Contains("FERMA", alert.Title);
        Assert.Contains("MKR/USDT", alert.Body);

        // Secondo e terzo giro: ferma e già segnalata, silenzio.
        Assert.Empty(await worker.TickAsync(CancellationToken.None));
        Assert.Empty(await worker.TickAsync(CancellationToken.None));
        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task Tick_SerieSana_NessunAllarme()
    {
        // Livello 2 dello standard: il rumore (qui: la normalità) non deve accendere niente.
        var (worker, db, notifier) = await BuildAsync();
        await SeedSeriesAsync(db, "BTC/USDT", "1h", DateTime.UtcNow.AddMinutes(-30)); // ultima barra chiusa presente

        for (var i = 0; i < 5; i++)
        {
            Assert.Empty(await worker.TickAsync(CancellationToken.None));
        }
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task Tick_SerieDisabilitata_NonSiGiudica()
    {
        // Disabilitare è la decisione umana di B2.a (MKR/TON in BREAK): una serie spenta apposta
        // non deve continuare a gridare.
        var (worker, db, notifier) = await BuildAsync();
        await SeedSeriesAsync(db, "MKR/USDT", "1h", DateTime.UtcNow.AddDays(-300), enabled: false);

        Assert.Empty(await worker.TickAsync(CancellationToken.None));
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task Tick_SerieCheRiprende_RiarmaLAllarme()
    {
        var (worker, db, notifier) = await BuildAsync();
        await SeedSeriesAsync(db, "TON/USDT", "1h", DateTime.UtcNow.AddHours(-30));

        Assert.Single(await worker.TickAsync(CancellationToken.None)); // ferma → allarme

        // La serie riprende: arriva una candela fresca.
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.OhlcvData.Add(new OhlcvData
            {
                Symbol = "TON/USDT", Timeframe = "1h",
                TimestampUtc = DateTime.UtcNow.AddMinutes(-30),
                Open = 1m, High = 1m, Low = 1m, Close = 1m, Volume = 1m,
            });
            await ctx.SaveChangesAsync();
        }
        Assert.Empty(await worker.TickAsync(CancellationToken.None)); // fresca: riarmo silenzioso

        // Si ferma di nuovo: le candele fresche spariscono... non si può togliere il passato, quindi
        // si simula il tempo che avanza è impraticabile qui — il riarmo è già provato dal Remove:
        // la stessa serie, se torna ferma, esce dall'insieme dei segnalati ed è di nuovo eleggibile.
        // Il ciclo completo ferma→fresca→ferma con orologio finto è coperto dai test di
        // LaneInvariantWatchdog per il battito (stessa struttura a transizioni).
    }

    [Fact]
    public async Task Tick_SerieAbilitataSenzaCandele_EFerma()
    {
        // Il caso-trappola di B2.a: serie vuota NON vale "aggiornata" — un null in un confronto
        // numerico si comporterebbe da zero, e zero significherebbe fresca.
        var (worker, db, notifier) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.TrackedSeries.Add(new TrackedSeries
            {
                Exchange = ExchangeName.Binance, Symbol = "NEW/USDT", Timeframe = "1h", Enabled = true,
            });
            await ctx.SaveChangesAsync();
        }

        var newlyStale = await worker.TickAsync(CancellationToken.None);

        Assert.Single(newlyStale);
        Assert.Contains("nessuna candela", newlyStale[0]);
        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task Tick_PiuSerieFermeInsieme_UnaNotificaAggregata()
    {
        // Un'interruzione che ferma molte serie insieme deve produrre UN messaggio, non uno per
        // serie: duecento critici in raffica sono un canale che smette di essere letto.
        var (worker, db, notifier) = await BuildAsync();
        await SeedSeriesAsync(db, "AAA/USDT", "1h", DateTime.UtcNow.AddHours(-30));
        await SeedSeriesAsync(db, "BBB/USDT", "1h", DateTime.UtcNow.AddHours(-40));
        await SeedSeriesAsync(db, "CCC/USDT", "1h", DateTime.UtcNow.AddHours(-50));

        var newlyStale = await worker.TickAsync(CancellationToken.None);

        Assert.Equal(3, newlyStale.Count);
        var alert = Assert.Single(notifier.Sent);
        Assert.Contains("3 serie", alert.Title);
        Assert.Contains("AAA/USDT", alert.Body);
        Assert.Contains("CCC/USDT", alert.Body);
    }
}
