using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [verifica browser 2026-08-16] Il conteggio candele si calcola UNA volta per finestra, per tutti.
///
/// <para>Nato da un difetto che solo la pagina vera poteva mostrare: la revisione aveva spostato i
/// conteggi fuori dal percorso critico facendoli per-serie sull'indice, e nel browser la colonna
/// restava «…» per sempre — 417 ms per serie × 234 serie ≈ 97 secondi a passata, con una passata
/// per ogni caricamento di pagina, tutte accavallate. La GROUP BY unica costa 15 s: il totale più
/// basso. Qui si prova che si paga una volta sola e che due chiamate insieme non ne fanno due.</para>
/// </summary>
[Collection("Postgres")]
public sealed class SeriesCandleCountCacheTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public SeriesCandleCountCacheTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    /// <summary>TimeProvider a orologio manuale (convenzione del repo: niente pacchetto per due metodi).</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return dbFactory;
    }

    private static async Task SeedAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        string symbol, string timeframe, int candles)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var step = Timeframes.Supported[timeframe];
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < candles; i++)
        {
            db.OhlcvData.Add(new OhlcvData
            {
                Symbol = symbol, Timeframe = timeframe, TimestampUtc = start + step * i,
                Open = 1m, High = 1m, Low = 1m, Close = 1m, Volume = 1m,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Conta_PerSerie_ETieneIlValoreDentroLaFinestra()
    {
        var db = await BuildAsync();
        await SeedAsync(db, "AAA/USDT", "1h", 7);
        await SeedAsync(db, "BBB/USDT", "1h", 3);

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var cache = new SeriesCandleCountCache(db, NullLogger<SeriesCandleCountCache>.Instance, clock);

        var counts = await cache.GetAsync();
        Assert.Equal(7, counts[("AAA/USDT", "1h")]);
        Assert.Equal(3, counts[("BBB/USDT", "1h")]);
        var computedAt = cache.ComputedAtUtc;

        // Arrivano candele nuove, ma dentro la finestra il valore mostrato resta quello: il numero
        // di candele non guida nessuna decisione, la passata costa secondi di database.
        await SeedAsync(db, "AAA/USDT", "4h", 5);
        clock.Advance(TimeSpan.FromMinutes(9));
        var again = await cache.GetAsync();
        Assert.False(again.ContainsKey(("AAA/USDT", "4h")));
        Assert.Equal(computedAt, cache.ComputedAtUtc); // nessun ricalcolo

        // Scaduta la finestra, si ricalcola.
        clock.Advance(TimeSpan.FromMinutes(2));
        var fresh = await cache.GetAsync();
        Assert.Equal(5, fresh[("AAA/USDT", "4h")]);
        Assert.NotEqual(computedAt, cache.ComputedAtUtc);
    }

    [Fact]
    public async Task DueChiamateInsieme_UnaSolaPassata()
    {
        // Il difetto misurato nel browser: ogni caricamento di pagina avviava la propria passata e
        // si accavallavano. Chi arriva secondo deve ASPETTARE la prima, non farne un'altra.
        var db = await BuildAsync();
        await SeedAsync(db, "AAA/USDT", "1h", 4);

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var cache = new SeriesCandleCountCache(db, NullLogger<SeriesCandleCountCache>.Instance, clock);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => cache.GetAsync()));

        Assert.All(results, r => Assert.Equal(4, r[("AAA/USDT", "1h")]));
        Assert.All(results, r => Assert.Same(results[0], r)); // stessa istanza: una sola passata
    }

    [Fact]
    public async Task SerieSenzaCandele_NonCompareNelDizionario()
    {
        // Chi legge deve trattare l'assenza come «zero», non come «non lo so»: il chiamante usa
        // GetValueOrDefault. Qui si fissa il contratto.
        var db = await BuildAsync();
        var cache = new SeriesCandleCountCache(db, NullLogger<SeriesCandleCountCache>.Instance);

        var counts = await cache.GetAsync();

        Assert.Empty(counts);
        Assert.Equal(0, counts.GetValueOrDefault(("MAI/VISTA", "1h")));
    }
}
