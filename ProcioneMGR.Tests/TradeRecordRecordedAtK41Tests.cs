using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K41, PRD autonomia-piena — Fase 3, 2026-09-01] <b>L'ora di parete accanto all'ora di candela.</b>
///
/// <para>Tutte le date di <c>TradeRecords</c> sono <b>tempi di CANDELA</b>: vengono da
/// <c>candle.TimestampUtc</c>. Al riavvio del motore <c>TradingWorker</c> rigioca fino a trenta
/// giorni di storia, e le righe che ne nascono portano lo <c>StrategyId</c> e il simbolo attuali —
/// quindi superano ogni filtro esistente. Misurato il 2026-09-01: <b>35 righe precedevano la
/// creazione della gamba a cui appartenevano</b>, e i trade di forward test veri sulle cinque corsie
/// di flotta erano <b>0 · 0 · 1 · 0 · 0</b> contro 27 righe di replay sulla sola corsia 4.</para>
///
/// <para>Queste prove difendono tre proprietà, e la terza è quella che si perde più facilmente in un
/// refactoring: <b>il valore lo mette il DATABASE</b>, non il chiamante. Un booleano
/// <c>IsReplay</c> scritto da chi inserisce è un'<i>opinione</i> e può mentire; una data messa da
/// Postgres è un <i>fatto</i>, e vale anche per uno scrittore futuro che non sa che questa colonna
/// esiste — compreso dell'SQL a mano, che è il caso in cui i difetti nascono davvero.</para>
/// </summary>
[Collection("Postgres")]
public sealed class TradeRecordRecordedAtK41Tests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public TradeRecordRecordedAtK41Tests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();
        var db = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var c = await db.CreateDbContextAsync()) await c.Database.EnsureCreatedAsync();
        return db;
    }

    private static TradeRecord Trade(DateTime chiusura) => new()
    {
        LaneId = 4, PositionId = Guid.NewGuid().ToString("N"), StrategyId = "leg-a",
        Symbol = "DOGE/USDT", Side = OrderSide.Sell, EntryPrice = 0.083m, ExitPrice = 0.082m,
        Quantity = 100m, Pnl = 0.1m, PnlPercent = 0.12m,
        OpenedAtUtc = chiusura.AddHours(-1), ClosedAtUtc = chiusura,
        Duration = TimeSpan.FromHours(1), Mode = TradingMode.Paper,
    };

    [Fact]
    public async Task IlDATABASEtimbraLoraDIparete_ancheSeIlChiamanteNONlaMette()
    {
        var db = await BuildAsync();
        var prima = DateTime.UtcNow.AddSeconds(-5);

        await using (var c = await db.CreateDbContextAsync())
        {
            // Il chiamante NON tocca RecordedAtUtc: è il caso normale, ed è il caso che conta.
            c.TradeRecords.Add(Trade(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc)));
            await c.SaveChangesAsync();
        }

        await using var verifica = await db.CreateDbContextAsync();
        var riga = Assert.Single(await verifica.TradeRecords.AsNoTracking().ToListAsync());

        Assert.NotNull(riga.RecordedAtUtc);
        Assert.InRange(riga.RecordedAtUtc!.Value, prima, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task IlRITARDOdiSCRITTURAsiLEGGE_edEilNUMEROcheSEPARAviviEreplay()
    {
        // La proprietà per cui la colonna è una DATA e non un booleano: `RecordedAtUtc − ClosedAtUtc`
        // è il ritardo di scrittura, e chi legge sceglie la propria soglia invece di ereditare la
        // nostra. Misurato in esercizio: una barra a 15m viene scritta 8,97 secondi dopo la propria
        // chiusura; un recupero a trenta giorni dà ritardi di trenta giorni. Due ordini di
        // grandezza di margine — la soglia non è una scelta delicata.
        var db = await BuildAsync();
        var adesso = DateTime.UtcNow;

        await using (var c = await db.CreateDbContextAsync())
        {
            c.TradeRecords.Add(Trade(adesso.AddMinutes(-15)));   // vivo: chiuso un attimo fa
            c.TradeRecords.Add(Trade(adesso.AddDays(-25)));      // replay: candela di 25 giorni fa
            await c.SaveChangesAsync();
        }

        await using var verifica = await db.CreateDbContextAsync();
        var righe = await verifica.TradeRecords.AsNoTracking().OrderBy(t => t.ClosedAtUtc).ToListAsync();

        var ritardoReplay = righe[0].RecordedAtUtc!.Value - righe[0].ClosedAtUtc;
        var ritardoVivo = righe[1].RecordedAtUtc!.Value - righe[1].ClosedAtUtc;

        Assert.True(ritardoReplay > TimeSpan.FromDays(24), $"il replay deve mostrarsi come ritardo grande: {ritardoReplay}");
        Assert.True(ritardoVivo < TimeSpan.FromHours(1), $"il vivo deve mostrarsi come ritardo piccolo: {ritardoVivo}");
    }

    [Fact]
    public async Task UnValoreESPLICITOdelChiamante_nonVIENEsilenziosamenteACCETTATO()
    {
        // LA PROPRIETÀ CHE RENDE LA COLONNA UN FATTO, e che questo test ha già salvato una volta.
        //
        // La prima versione di K41 usava il solo `ValueGeneratedOnAdd`, e il commento prometteva
        // «il valore lo mette il database, non il chiamante». Era falso: EF manda comunque il valore
        // se il chiamante lo assegna, e questo test ha scritto 2020-01-01 su una riga nuova. Una
        // colonna che chiunque può scrivere non è un fatto, è un'opinione — cioè esattamente il
        // booleano `IsReplay` che questa forma esiste per evitare.
        //
        // Il rimedio è `BeforeSaveBehavior.Ignore`: la proprietà non entra MAI nell'INSERT.
        var db = await BuildAsync();
        var bugia = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var c = await db.CreateDbContextAsync())
        {
            var t = Trade(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc));
            t.RecordedAtUtc = bugia;
            c.TradeRecords.Add(t);
            await c.SaveChangesAsync();
        }

        await using var verifica = await db.CreateDbContextAsync();
        var riga = Assert.Single(await verifica.TradeRecords.AsNoTracking().ToListAsync());

        Assert.NotEqual(bugia, riga.RecordedAtUtc);
        Assert.True(riga.RecordedAtUtc > new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void LaMIGRAZIONEaggiungeLaColonnaNUDA_ePOIilDEFAULT()
    {
        // GUARDIANO DI SORGENTE, e non è pedanteria: `ADD COLUMN ... DEFAULT (now())` in un colpo
        // solo RIEMPIE anche le righe esistenti — `now()` è STABLE, quindi Postgres la tratta come
        // una costante. Le 369 righe storiche dichiarerebbero tutte di essere state scritte
        // nell'istante della migrazione, cioè la bugia peggiore delle due: ogni riga di replay già
        // in archivio passerebbe per viva.
        //
        // I due passi separati non si possono provare con un test d'integrazione (la fixture crea
        // lo schema con EnsureCreated, non applicando le migrazioni), quindi si difende la sorgente.
        var migrazione = File.ReadAllText(Path.Combine(
            Procione.Platform.RepoRoot, "ProcioneMGR.Migrations.Postgres", "Migrations",
            "20260901195814_AddTradeRecordRecordedAtUtc.cs"));

        var indiceColonna = migrazione.IndexOf("AddColumn<DateTime>", StringComparison.Ordinal);
        var indiceDefault = migrazione.IndexOf("SET DEFAULT", StringComparison.Ordinal);

        Assert.True(indiceColonna >= 0, "la migrazione deve aggiungere la colonna");
        Assert.True(indiceDefault > indiceColonna, "il default deve venire DOPO la colonna, o le righe storiche vengono riempite");
        // E la colonna non deve nascere già con il default addosso.
        Assert.DoesNotContain("defaultValueSql", migrazione);
    }
}
