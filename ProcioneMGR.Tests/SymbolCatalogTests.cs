using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [E-04, Fase 2 PRD-RISANAMENTO] Il catalogo simboli condiviso: la POLITICA (unione di
/// TrackedSeries e simboli storici in OhlcvData, ordinata) dichiarata e verificata in un punto
/// solo, al posto delle sette copie implicite nelle pagine. La sfumatura che contava: una serie
/// RIMOSSA dalla watchlist resta selezionabile (i suoi dati esistono), e una APPENA AGGIUNTA
/// compare anche senza candele — nessuna delle due sarebbe sopravvissuta a una sostituzione
/// ingenua con la sola TrackedSeries. Stessa politica per le COPPIE (simbolo, timeframe) di
/// GetKnownSeriesAsync, con in più il vincolo che NON siano il prodotto cartesiano
/// simboli × timeframe: quello mentirebbe sulle serie senza dati.
/// </summary>
[Collection("Postgres")]
public sealed class SymbolCatalogTests(PostgresFixture pg) : IAsyncDisposable
{
    private readonly string _connString = pg.CreateDatabase();
    private ServiceProvider? _provider;

    private async Task<IDbContextFactory<ApplicationDbContext>> BuildDbAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();
        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return dbFactory;
    }

    private static OhlcvData Candle(string symbol, string timeframe = "1h") => new()
    {
        Symbol = symbol, Timeframe = timeframe, TimestampUtc = new DateTime(2026, 1, 1),
        Open = 1m, High = 1m, Low = 1m, Close = 1m, Volume = 1m,
    };

    [Fact]
    public async Task Union_TrackedAndHistorical_Ordered()
    {
        var dbFactory = await BuildDbAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            // Storico SENZA tracking (serie rimossa) + tracciata SENZA candele (appena aggiunta)
            // + una presente in entrambi.
            db.OhlcvData.Add(Candle("ZEC/USDT"));
            db.OhlcvData.Add(Candle("BTC/USDT"));
            db.TrackedSeries.Add(new TrackedSeries { Exchange = ExchangeName.Binance, Symbol = "BTC/USDT", Timeframe = "1h" });
            db.TrackedSeries.Add(new TrackedSeries { Exchange = ExchangeName.Binance, Symbol = "AAA/USDT", Timeframe = "1h" });
            await db.SaveChangesAsync();
        }

        var catalog = new SymbolCatalog(dbFactory);
        var symbols = await catalog.GetKnownSymbolsAsync();

        Assert.Equal(["AAA/USDT", "BTC/USDT", "ZEC/USDT"], symbols);
    }

    [Fact]
    public async Task Cache_ServesWithoutRescan_UntilInvalidated()
    {
        var dbFactory = await BuildDbAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.OhlcvData.Add(Candle("BTC/USDT"));
            await db.SaveChangesAsync();
        }

        var catalog = new SymbolCatalog(dbFactory, ttl: TimeSpan.FromHours(1));
        var first = await catalog.GetKnownSymbolsAsync();
        Assert.Single(first);

        // Nuovo simbolo a DB: la cache (TTL lungo) NON lo vede...
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.OhlcvData.Add(Candle("ETH/USDT"));
            await db.SaveChangesAsync();
        }
        Assert.Single(await catalog.GetKnownSymbolsAsync());

        // ...finche' qualcuno non invalida (la watchlist lo fa a ogni salvataggio).
        catalog.Invalidate();
        Assert.Equal(2, (await catalog.GetKnownSymbolsAsync()).Count);
    }

    [Fact]
    public async Task Series_UnionTrackedAndHistorical_NoCartesianProduct()
    {
        var dbFactory = await BuildDbAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            // BTC ha dati su DUE timeframe, ZEC su uno solo (serie rimossa: nessun tracking);
            // AAA è tracciata senza candele; BTC/1h sta in entrambe le fonti (dedup).
            db.OhlcvData.Add(Candle("ZEC/USDT"));
            db.OhlcvData.Add(Candle("BTC/USDT"));
            db.OhlcvData.Add(Candle("BTC/USDT", "4h"));
            db.TrackedSeries.Add(new TrackedSeries { Exchange = ExchangeName.Binance, Symbol = "BTC/USDT", Timeframe = "1h" });
            db.TrackedSeries.Add(new TrackedSeries { Exchange = ExchangeName.Binance, Symbol = "AAA/USDT", Timeframe = "1h" });
            await db.SaveChangesAsync();
        }

        var catalog = new SymbolCatalog(dbFactory);
        var series = await catalog.GetKnownSeriesAsync();

        // Niente cartesiano: ZEC/USDT compare solo su 1h e AAA/USDT solo dov'è tracciata,
        // anche se il timeframe 4h esiste altrove nell'universo.
        Assert.Equal([
            new SeriesKey("AAA/USDT", "1h"),
            new SeriesKey("BTC/USDT", "1h"),
            new SeriesKey("BTC/USDT", "4h"),
            new SeriesKey("ZEC/USDT", "1h"),
        ], series);
    }

    [Fact]
    public async Task Series_ShareTheCacheAndTheInvalidate_WithSymbols()
    {
        var dbFactory = await BuildDbAsync();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.OhlcvData.Add(Candle("BTC/USDT"));
            await db.SaveChangesAsync();
        }

        var catalog = new SymbolCatalog(dbFactory, ttl: TimeSpan.FromHours(1));
        Assert.Single(await catalog.GetKnownSeriesAsync());

        // Serie nuova a DB: lo snapshot (TTL lungo) non la vede, né sulle coppie né sui simboli...
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.OhlcvData.Add(Candle("BTC/USDT", "4h"));
            db.OhlcvData.Add(Candle("ETH/USDT"));
            await db.SaveChangesAsync();
        }
        Assert.Single(await catalog.GetKnownSeriesAsync());
        Assert.Single(await catalog.GetKnownSymbolsAsync());

        // ...finche' la STESSA Invalidate non svuota entrambe le viste dello snapshot.
        catalog.Invalidate();
        Assert.Equal(3, (await catalog.GetKnownSeriesAsync()).Count);
        Assert.Equal(2, (await catalog.GetKnownSymbolsAsync()).Count);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
