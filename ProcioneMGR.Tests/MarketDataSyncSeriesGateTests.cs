using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Ingestion;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [review 2026-08-15] Il gate per-serie non deve poter affamare il resto del ciclo.
///
/// <para>Il difetto trovato dalla review del fix all'incidente delle 122 serie: un ciclo
/// ABBANDONATO dal backstop lascia il <c>SemaphoreSlim</c> della serie in volo preso da un task
/// zombie che non lo rilascerà mai. Con l'attesa illimitata di prima, ogni ciclo successivo si
/// bloccava su quel gate fino a consumare il budget, e TUTTE le serie successive nell'elenco non
/// venivano più sincronizzate — mentre battito e timbro restavano freschi e la liveness non
/// riavviava nulla. L'incidente in versione parziale e non auto-riparabile.</para>
///
/// <para>La cura: nel CICLO il gate si prende con attesa breve e, se occupato, la serie si SALTA.
/// Una serie bloccata costa una serie, mai il resto del ciclo.</para>
/// </summary>
[Collection("Postgres")]
public sealed class MarketDataSyncSeriesGateTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public MarketDataSyncSeriesGateTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    /// <summary>Ingestione che si appende SOLO sulla serie designata (il caso del task zombie).</summary>
    private sealed class HangingOnSymbolIngestion(string hangSymbol, List<string> touched) : IOhlcvIngestionService
    {
        public async Task<IngestionResult> IngestHistoricalDataAsync(
            string exchange, string symbol, string timeframe, DateTime from, DateTime to,
            IProgress<IngestionProgress>? progress = null, CancellationToken ct = default)
        {
            lock (touched) touched.Add(symbol);
            if (symbol == hangSymbol)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None); // ignora il token, come lo zombie
            }
            return new IngestionResult(0, false);
        }
    }

    private async Task<(MarketDataSyncService Sync, IDbContextFactory<ApplicationDbContext> Db, List<string> Touched)>
        BuildAsync(string hangSymbol)
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

        var touched = new List<string>();
        var sync = new MarketDataSyncService(
            dbFactory,
            new HangingOnSymbolIngestion(hangSymbol, touched),
            new ConfigurationBuilder().Build(),
            NullLogger<MarketDataSyncService>.Instance);
        return (sync, dbFactory, touched);
    }

    /// <summary>
    /// Semina a partire da un Id DICHIARATO. Serve perché <c>SeriesLocks</c> è statico e di
    /// processo, chiavato sull'Id della serie: ogni test ha il suo database con gli Id che
    /// ripartono da 1, quindi senza blocchi di Id distinti lo zombie lasciato da un test
    /// bloccherebbe la serie omonima del test successivo. In produzione il DB è uno solo e gli Id
    /// sono unici davvero.
    /// </summary>
    private static async Task SeedAsync(IDbContextFactory<ApplicationDbContext> dbFactory, int startId, params string[] symbols)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // DDL con valore letterale: RESTART WITH non accetta parametri, e startId è un intero
        // costante del test — nessun input esterno in gioco.
#pragma warning disable EF1002, EF1003
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"TrackedSeries\" ALTER COLUMN \"Id\" RESTART WITH "
            + startId.ToString(System.Globalization.CultureInfo.InvariantCulture));
#pragma warning restore EF1002, EF1003
        foreach (var s in symbols)
        {
            db.TrackedSeries.Add(new TrackedSeries
            {
                Exchange = ExchangeName.Binance, Symbol = s, Timeframe = "1h", Enabled = true,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CicloSuccessivo_SerieConGateOccupato_VieneSaltataEIlCicloProsegue()
    {
        // AAA si appende ignorando il token: il primo ciclo viene interrotto dal budget e lascia
        // il gate di AAA preso per sempre.
        var (sync, db, touched) = await BuildAsync(hangSymbol: "AAA/USDT");
        await SeedAsync(db, startId: 91_000, "AAA/USDT", "BBB/USDT", "CCC/USDT");

        var firstCycle = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        // Il primo ciclo NON ritorna: lo zombie ignora il token. È lo scenario per cui esiste il
        // backstop del worker — qui lo si riproduce abbandonando l'attesa, come fa RunCycleAsync.
        var abandoned = sync.SyncAllEnabledAsync(firstCycle.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => abandoned.WaitAsync(TimeSpan.FromSeconds(3)));
        lock (touched) touched.Clear();

        // Secondo ciclo: AAA è ancora tenuta dallo zombie. Prima della cura questo ciclo si
        // bloccava sul gate fino al budget e BBB/CCC non venivano MAI sincronizzate.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sync.SyncAllEnabledAsync(CancellationToken.None);
        sw.Stop();

        List<string> visited;
        lock (touched) visited = [.. touched];
        Assert.DoesNotContain("AAA/USDT", visited);       // saltata: il gate era occupato
        Assert.Contains("BBB/USDT", visited);             // le successive NON sono affamate
        Assert.Contains("CCC/USDT", visited);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"il ciclo non doveva attendere il gate: {sw.Elapsed}");
    }

    [Fact]
    public async Task SyncManuale_SerieConGateOccupato_DiceCheEInCorsoInveceDiAppendersi()
    {
        // Il percorso manuale (pulsante «Sync now», POST /sync) non deve restare appeso per l'intero
        // timeout HTTP su un gate che non si libererà: meglio un messaggio subito.
        var (sync, db, _) = await BuildAsync(hangSymbol: "AAA/USDT");
        await SeedAsync(db, startId: 92_000, "AAA/USDT");
        int id;
        await using (var ctx = await db.CreateDbContextAsync())
        {
            id = await ctx.TrackedSeries.Select(s => s.Id).FirstAsync();
        }

        var firstCall = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var zombie = sync.SyncSeriesAsync(id, firstCall.Token); // si appende ignorando il token
        await Assert.ThrowsAsync<TimeoutException>(() => zombie.WaitAsync(TimeSpan.FromSeconds(3)));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncSeriesAsync(id, CancellationToken.None));
        sw.Stop();

        Assert.Contains("già in corso", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"doveva rinunciare presto: {sw.Elapsed}");
    }
}
