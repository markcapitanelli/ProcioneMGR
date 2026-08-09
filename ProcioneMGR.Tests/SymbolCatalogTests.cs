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
/// ingenua con la sola TrackedSeries.
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

    private static OhlcvData Candle(string symbol) => new()
    {
        Symbol = symbol, Timeframe = "1h", TimestampUtc = new DateTime(2026, 1, 1),
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

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
