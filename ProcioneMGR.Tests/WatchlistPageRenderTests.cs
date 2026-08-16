using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Rendering di <c>/market/watchlist</c> dopo la revisione post-incidente 2026-08-15 (122 serie
/// ferme per 6 ore, worker di sync morto in silenzio).
///
/// <para>Questi test guardano ciò che l'utente LEGGE, non ciò che il servizio calcola: la
/// differenza è esattamente il difetto dell'incidente, dove la pagina sapeva elencare le vittime
/// ma non sapeva nominare l'imputato. Sono il livello 4 dello standard di verifica reso
/// permanente — la pagina vera, con i suoi banner, resa in un test invece che guardata una volta
/// in uno screenshot.</para>
/// </summary>
[Collection("Postgres")]
public sealed class WatchlistPageRenderTests : BunitContext
{
    private readonly string _connString;

    public WatchlistPageRenderTests(PostgresFixture pg)
    {
        _connString = pg.CreateDatabase();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class NoopSync : IMarketDataSyncService
    {
        public Task<int> SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default) => Task.FromResult(0);
        public Task SyncAllEnabledAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopCatalog : ProcioneMGR.Services.MarketData.ISymbolCatalog
    {
        public ValueTask<IReadOnlyList<string>> GetKnownSymbolsAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);
        public ValueTask<IReadOnlyList<ProcioneMGR.Services.MarketData.SeriesKey>> GetKnownSeriesAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<ProcioneMGR.Services.MarketData.SeriesKey>>([]);
        public void Invalidate() { }
    }

    private sealed class NoopExchangeFactory : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => throw new NotSupportedException();
        public IExchangeClient Create(string exchangeName) => throw new NotSupportedException();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotSupportedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotSupportedException();
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> RegisterAsync()
    {
        Services.AddLogging();
        Services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
        Services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        Services.AddSingleton<IMarketDataSyncService, NoopSync>();
        Services.AddSingleton<IExchangeClientFactory, NoopExchangeFactory>();
        Services.AddSingleton<ProcioneMGR.Services.MarketData.ISymbolCatalog, NoopCatalog>();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<SeriesCandleCountCache>();
        Services.AddScoped<WatchlistPageService>();

        var auth = AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles(AppRoles.Admin);

        var dbFactory = Services.BuildServiceProvider().GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return dbFactory;
    }

    private static async Task SeedAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        string symbol, string timeframe, DateTime lastCandleUtc)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.TrackedSeries.Add(new TrackedSeries
        {
            Exchange = ExchangeName.Binance, Symbol = symbol, Timeframe = timeframe, Enabled = true,
        });
        db.OhlcvData.Add(new OhlcvData
        {
            Symbol = symbol, Timeframe = timeframe, TimestampUtc = lastCandleUtc,
            Open = 1m, High = 1m, Low = 1m, Close = 1m, Volume = 1m,
        });
        await db.SaveChangesAsync();
    }

    private static async Task StampAsync(IDbContextFactory<ApplicationDbContext> dbFactory, DateTime lastUtc, string esito)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.HostHeartbeats.Add(new HostHeartbeat
        {
            Host = HostHeartbeat.IngestionSyncRole,
            LastUtc = lastUtc,
            Version = SyncPulse.ComposeOutcome(esito, TimeSpan.FromMinutes(5)),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SyncFermo_LaPaginaNominaLImputatoEDiceDiNonDisabilitare()
    {
        // Il difetto dell'incidente reso impossibile: con 122 serie ferme la pagina consigliava di
        // «verificare BREAK sui simboli», mentre l'imputato era il sync morto alle 22:44.
        var db = await RegisterAsync();
        await SeedAsync(db, "AAA/USDT", "1h", DateTime.UtcNow.AddHours(-30));
        await StampAsync(db, DateTime.UtcNow.AddHours(-6), "ciclo ok");

        var cut = Render<ProcioneMGR.Components.Pages.Watchlist>();
        cut.WaitForAssertion(() => Assert.Contains("FERMO", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.Contains("l'imputato è il sync, non i simboli", cut.Markup);
        Assert.Contains("Non disabilitare le serie", cut.Markup);
        Assert.DoesNotContain("Verifica su exchange", cut.Markup); // niente consiglio sbagliato
    }

    [Fact]
    public async Task SyncVivoESerieFerma_LaPaginaOffreLaVerificaSuExchange()
    {
        var db = await RegisterAsync();
        await SeedAsync(db, "MKR/USDT", "1h", DateTime.UtcNow.AddHours(-30));
        await StampAsync(db, DateTime.UtcNow.AddMinutes(-2), "ciclo ok");

        var cut = Render<ProcioneMGR.Components.Pages.Watchlist>();
        cut.WaitForAssertion(() => Assert.Contains("serie abilitata FERMA", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.Contains("vivo", cut.Markup);                       // il riquadro del sync
        Assert.Contains("Verifica su exchange", cut.Markup);       // l'ipotesi giusta, a portata di click
        Assert.Contains("probabile sospensione del simbolo", cut.Markup);
    }

    [Fact]
    public async Task WorkerSpento_LaPaginaLoDiceInveceDiAccusareUnPodMorto()
    {
        var db = await RegisterAsync();
        await SeedAsync(db, "AAA/USDT", "1h", DateTime.UtcNow.AddHours(-30));
        await StampAsync(db, DateTime.UtcNow.AddHours(-6), "spento");

        var cut = Render<ProcioneMGR.Components.Pages.Watchlist>();
        cut.WaitForAssertion(() => Assert.Contains("SPENTO", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.Contains("disattivato da configurazione", cut.Markup);
        Assert.DoesNotContain("l'imputato è il sync, non i simboli", cut.Markup);
    }

    [Fact]
    public async Task SenzaTimbro_LaPaginaDichiaraCheNonHaMaiVistoUnGiro()
    {
        // Nessun timbro e nessuna serie mai sincronizzata: la pagina non deve inventare un'ora.
        var db = await RegisterAsync();
        await SeedAsync(db, "AAA/USDT", "1h", DateTime.UtcNow.AddMinutes(-30));

        var cut = Render<ProcioneMGR.Components.Pages.Watchlist>();
        cut.WaitForAssertion(() => Assert.Contains("MAI VISTO UN GIRO", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.Contains("procionemgr-ingestion", cut.Markup); // dove guardare, in assetto kind
    }

    [Fact]
    public async Task SerieSane_NessunBannerRosso()
    {
        // Livello 2 dello standard: sulla normalità la pagina non deve accendere nulla.
        var db = await RegisterAsync();
        await SeedAsync(db, "BTC/USDT", "1h", DateTime.UtcNow.AddMinutes(-30));
        await StampAsync(db, DateTime.UtcNow.AddMinutes(-2), "ciclo ok");

        var cut = Render<ProcioneMGR.Components.Pages.Watchlist>();
        cut.WaitForAssertion(() => Assert.Contains("aggiornata", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("FERM", cut.Markup);
        Assert.DoesNotContain("alert-danger", cut.Markup);
    }
}
