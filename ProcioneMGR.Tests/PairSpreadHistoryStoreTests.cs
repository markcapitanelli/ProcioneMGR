using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.PairsTrading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I14c] La persistenza della storia dello spread: <b>idempotenza</b> (il worker ricalcola le
/// stesse finestre a ogni giro, e senza il vincolo unico la tabella crescerebbe di un duplicato per
/// giro, per sempre) e <b>deduplica in lettura</b> delle finestre sovrapposte.
/// </summary>
[Collection("Postgres")]
public sealed class PairSpreadHistoryStoreTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public PairSpreadHistoryStoreTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private async Task<(IDbContextFactory<ApplicationDbContext> Db, PairSpreadHistoryStore Store)> BuildAsync()
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
        return (dbFactory, new PairSpreadHistoryStore(dbFactory));
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static PairSpreadWindow Finestra(
        int indice, int ampiezza = 250, bool stazionaria = true,
        string chiave = "BTC/USDT|ETH/USDT 1h", string estimatore = "Kalman", double adf = -4.2)
        => new()
        {
            PairKeyValue = chiave, SymbolY = "ETH/USDT", SymbolX = "BTC/USDT", Timeframe = "1h",
            Estimator = estimatore, WindowSize = ampiezza,
            WindowStartUtc = T0.AddHours(indice * ampiezza),
            WindowEndUtc = T0.AddHours((indice + 1) * ampiezza - 1),
            AdfStatistic = adf, CriticalValue = -3.34, IsStationaryWindow = stazionaria,
            HedgeRatio = 1.2, SpreadMean = 0.01, SpreadStdDev = 0.05, LastZScore = 0.4,
            ComputedAtUtc = T0.AddDays(1),
        };

    /// <summary>
    /// <b>L2 idempotenza</b>: lo stesso giro scritto due volte non duplica nulla. È il contratto su
    /// cui poggia l'affermazione «dal secondo giro in poi scrive una riga sola» — senza, il carico
    /// dichiarato nel pannello sarebbe una bugia che cresce ogni dodici ore.
    /// </summary>
    [Fact]
    public async Task L2_LoStessoGiroScrittoDueVolte_NonDuplicaNulla()
    {
        var (dbFactory, store) = await BuildAsync();
        var giro = Enumerable.Range(0, 6).Select(i => Finestra(i)).ToList();

        Assert.Equal(6, await store.SaveAsync(giro));
        // Secondo giro identico: zero righe NUOVE.
        Assert.Equal(0, await store.SaveAsync(Enumerable.Range(0, 6).Select(i => Finestra(i)).ToList()));

        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(6, await db.PairSpreadWindows.CountAsync());
    }

    /// <summary>
    /// Il giro successivo porta UNA finestra nuova e ritocca le vecchie: è il regime stazionario del
    /// worker, e il numero su cui il pannello dichiara il carico.
    /// </summary>
    [Fact]
    public async Task IlGiroSuccessivo_ScriveUnaSolaRigaNuova()
    {
        var (dbFactory, store) = await BuildAsync();
        await store.SaveAsync(Enumerable.Range(0, 6).Select(i => Finestra(i)).ToList());

        var nuove = await store.SaveAsync(Enumerable.Range(0, 7).Select(i => Finestra(i)).ToList());

        Assert.Equal(1, nuove);
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(7, await db.PairSpreadWindows.CountAsync());
    }

    /// <summary>
    /// Una finestra ricalcolata si AGGIORNA, non si duplica: se le candele sono state corrette a
    /// posteriori l'ultimo calcolo è quello buono.
    /// </summary>
    [Fact]
    public async Task FinestraRicalcolata_SiAggiorna()
    {
        var (dbFactory, store) = await BuildAsync();
        await store.SaveAsync([Finestra(0, adf: -2.0, stazionaria: false)]);

        await store.SaveAsync([Finestra(0, adf: -5.5, stazionaria: true)]);

        await using var db = await dbFactory.CreateDbContextAsync();
        var riga = await db.PairSpreadWindows.SingleAsync();
        Assert.Equal(-5.5, riga.AdfStatistic, 6);
        Assert.True(riga.IsStationaryWindow);
    }

    /// <summary>
    /// <b>Due estimatori sono due serie.</b> Stanno in chiave perché danno due spread diversi per
    /// costruzione: mescolarli confronterebbe misure che non sono la stessa misura, e la frazione su
    /// cui si esprime il verdetto sarebbe la media di due cose.
    /// </summary>
    [Fact]
    public async Task DueEstimatori_SonoDueSerieDistinte()
    {
        var (_, store) = await BuildAsync();
        await store.SaveAsync([Finestra(0, estimatore: "Kalman"), Finestra(0, estimatore: "RollingOls")]);

        Assert.Single(await store.LoadSeriesAsync("BTC/USDT|ETH/USDT 1h", "Kalman"));
        Assert.Single(await store.LoadSeriesAsync("BTC/USDT|ETH/USDT 1h", "RollingOls"));
    }

    /// <summary>
    /// <b>Le finestre sovrapposte si tolgono in lettura.</b> Il worker taglia la griglia dalla
    /// candela più recente all'indietro; al giro dopo la griglia scivola, e in tabella restano
    /// finestre che condividono dati. Punti correlati per costruzione farebbero sembrare la
    /// relazione più stabile di quanto è — cioè gonfierebbero proprio la frazione su cui il verdetto
    /// si esprime.
    ///
    /// <para>Qui si semina una griglia allineata più una traslata di mezza finestra: la lettura deve
    /// restituire una catena senza sovrapposizioni.</para>
    /// </summary>
    [Fact]
    public async Task FinestreSovrapposte_TolteInLettura()
    {
        var (_, store) = await BuildAsync();

        var allineate = Enumerable.Range(0, 6).Select(i => Finestra(i)).ToList();
        var traslate = Enumerable.Range(0, 5).Select(i =>
        {
            var f = Finestra(i);
            f.WindowStartUtc = f.WindowStartUtc.AddHours(125);   // mezza finestra
            f.WindowEndUtc = f.WindowEndUtc.AddHours(125);
            return f;
        }).ToList();
        await store.SaveAsync([.. allineate, .. traslate]);

        var serie = await store.LoadSeriesAsync("BTC/USDT|ETH/USDT 1h", "Kalman");

        Assert.True(serie.Count < 11, $"nessuna deduplica: {serie.Count} punti su 11 righe seminate");
        for (var i = 1; i < serie.Count; i++)
        {
            Assert.True(serie[i].WindowStartUtc >= serie[i - 1].WindowEndUtc,
                $"punti {i - 1} e {i} si sovrappongono: [{serie[i - 1].WindowStartUtc:o}, {serie[i - 1].WindowEndUtc:o}] "
                + $"e [{serie[i].WindowStartUtc:o}, {serie[i].WindowEndUtc:o}]");
        }
    }

    /// <summary>
    /// <b>Il verdetto sulla serie DEDUPLICATA, non su quella grezza.</b> È la saldatura fra le due
    /// contromisure: senza, le finestre sovrapposte gonfierebbero la frazione e una coppia mediocre
    /// passerebbe per persistente. Qui la stessa storia letta grezza avrebbe più punti di quella che
    /// il giudice riceve.
    /// </summary>
    [Fact]
    public async Task IlGiudizioPoggiaSullaSerieDeduplicata()
    {
        var (dbFactory, store) = await BuildAsync();
        var allineate = Enumerable.Range(0, 8).Select(i => Finestra(i)).ToList();
        var traslate = Enumerable.Range(0, 8).Select(i =>
        {
            var f = Finestra(i);
            f.WindowStartUtc = f.WindowStartUtc.AddHours(125);
            f.WindowEndUtc = f.WindowEndUtc.AddHours(125);
            return f;
        }).ToList();
        await store.SaveAsync([.. allineate, .. traslate]);

        await using var db = await dbFactory.CreateDbContextAsync();
        var grezze = await db.PairSpreadWindows.CountAsync();
        var serie = await store.LoadSeriesAsync("BTC/USDT|ETH/USDT 1h", "Kalman");

        Assert.Equal(16, grezze);
        Assert.True(serie.Count < grezze, "la serie letta deve essere piu' corta delle righe grezze");
        Assert.True(PairSpreadJudge.Judge(serie).Windows == serie.Count,
            "il giudice deve ricevere la serie deduplicata, non il conteggio delle righe");
    }

    /// <summary>Coppia mai sorvegliata: serie vuota, non un errore — e il giudice lo dichiara.</summary>
    [Fact]
    public async Task CoppiaMaiSorvegliata_SerieVuota()
    {
        var (_, store) = await BuildAsync();

        var serie = await store.LoadSeriesAsync("SOL/USDT|BTC/USDT 4h", "Kalman");

        Assert.Empty(serie);
        Assert.Contains("mai stata sorvegliata", PairSpreadJudge.Judge(serie).Text, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
