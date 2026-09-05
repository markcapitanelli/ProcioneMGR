using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-05] <b>Le migrazioni si provano, non si sperano.</b> In questo repo le migrazioni si
/// scrivono a mano (<c>migrations add</c> scaffolda dalla baseline sbagliata) e lo snapshot del
/// modello si aggiorna a mano: fino a oggi l'unico controllo era la riga di log del primo avvio del
/// guscio («il MODELLO differisce dallo snapshot»), cioe' dopo il merge e dopo il rilascio. Qui le
/// due verita' si confrontano prima: lo snapshot dell'ultima migrazione deve descrivere lo stesso
/// modello di <see cref="ApplicationDbContext"/>, e la catena intera delle migrazioni deve costruire
/// su un database vergine uno schema che il modello sa usare.
/// </summary>
[Collection("Postgres")]
public sealed class MigrazioniAllineateTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public MigrazioniAllineateTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private IDbContextFactory<ApplicationDbContext> Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o =>
            o.UseNpgsql(_connString, npgsql => npgsql.MigrationsAssembly(DatabaseMigrator.MigrationsAssemblyName))
             // IL MODELLO VA COSTRUITO QUI, NON PRESO DALLA CACHE. EF condivide il provider interno
             // — e con lui la cache dei modelli, chiave = tipo del contesto — fra tutte le opzioni
             // «equivalenti» dello stesso processo: nella suite intera il primo test che costruisce
             // ApplicationDbContext lo fa SENZA Identity, e questo test riceverebbe quel modello
             // (niente AspNetUserPasskeys, chiavi a text) e gridarebbe su differenze che l'app non
             // ha. In locale, lanciato da solo, passava; in CI, dopo tremila test, no.
             .EnableServiceProviderCaching(false));
        // Identity va registrata COME NELL'APP (Program.cs): il modello delle tabelle AspNet*
        // (passkeys, lunghezze massime delle chiavi) nasce dalle sue opzioni, e senza di esse il
        // confronto col snapshot segnalerebbe un DropTable AspNetUserPasskeys che non esiste.
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;
                options.Stores.SchemaVersion = Microsoft.AspNetCore.Identity.IdentitySchemaVersions.Version3;
            })
            .AddRoles<Microsoft.AspNetCore.Identity.IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        _provider = services.BuildServiceProvider();
        return _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
    }

    /// <summary>Lo snapshot scritto a mano descrive esattamente il modello: nessuna migrazione «mancante».</summary>
    [Fact]
    public async Task LoSnapshot_DescriveIlModello()
    {
        await using var db = await Build().CreateDbContextAsync();
        Assert.True(db.Database.GetMigrations().Any(), "l'assembly delle migrazioni non espone migrazioni: riferimento mancante o versioni EF disallineate");
        var differenze = DifferenzeDalloSnapshot(db);
        Assert.True(differenze.Count == 0,
            "il modello differisce dallo snapshot dell'ultima migrazione (manca una migrazione, o lo snapshot non e' stato aggiornato a mano):\n"
            + string.Join("\n", differenze));
    }

    /// <summary>
    /// Le differenze fra lo snapshot dell'assembly delle migrazioni e il modello corrente, una per
    /// riga e leggibili: un guardiano che dice solo «differisce» manda a cercare alla cieca.
    /// </summary>
    private static List<string> DifferenzeDalloSnapshot(ApplicationDbContext db)
    {
        var differ = db.GetService<IMigrationsModelDiffer>();
        var assembly = db.GetService<IMigrationsAssembly>();
        var initializer = db.GetService<IModelRuntimeInitializer>();
        var snapshot = assembly.ModelSnapshot?.Model ?? throw new InvalidOperationException("snapshot assente nell'assembly delle migrazioni");
        var source = initializer.Initialize(snapshot, designTime: true, validationLogger: null);
        var target = db.GetService<IDesignTimeModel>().Model;
        return differ.GetDifferences(source.GetRelationalModel(), target.GetRelationalModel())
            .Select(d => d switch
            {
                AddColumnOperation a => $"AddColumn {a.Table}.{a.Name} {a.ColumnType} null={a.IsNullable}",
                DropColumnOperation dc => $"DropColumn {dc.Table}.{dc.Name}",
                AlterColumnOperation ac => $"AlterColumn {ac.Table}.{ac.Name}: {ac.OldColumn.ColumnType}->{ac.ColumnType}, null {ac.OldColumn.IsNullable}->{ac.IsNullable}, max {ac.OldColumn.MaxLength}->{ac.MaxLength}, default {ac.OldColumn.DefaultValue ?? ac.OldColumn.DefaultValueSql}->{ac.DefaultValue ?? ac.DefaultValueSql}",
                CreateTableOperation ct => $"CreateTable {ct.Name}",
                DropTableOperation dt => $"DropTable {dt.Name}",
                CreateIndexOperation ci => $"CreateIndex {ci.Table}.{ci.Name}",
                DropIndexOperation di => $"DropIndex {di.Table}.{di.Name}",
                _ => d.GetType().Name,
            })
            .ToList();
    }

    /// <summary>La catena intera delle migrazioni costruisce su un database vergine uno schema che il modello sa leggere e scrivere.</summary>
    [Fact]
    public async Task LaCatenaDelleMigrazioni_CostruisceUnoSchemaCheIlModelloSaUsare()
    {
        var factory = Build();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }

        // Una scrittura e una lettura sulla tabella nata oggi, attraverso il modello: se la
        // migrazione scritta a mano avesse una colonna in meno o di tipo diverso, e' qui che si vede.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.CarryLedger.Add(new CarryLedgerEntry
            {
                Symbol = "BTC/USDT", Mode = "Paper", OpenedUtc = new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc),
                NotionalQuote = 5_000m, EntryAnnualizedPercent = 6.6m, CostPercent = 0.42m,
            });
            await db.SaveChangesAsync();
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            var riga = await db.CarryLedger.AsNoTracking().SingleAsync();
            Assert.Null(riga.ClosedUtc);
            Assert.Equal(5_000m, riga.NotionalQuote);
            // E una tabella storica, per dire che la catena ha costruito anche il resto.
            Assert.Equal(0, await db.HostHeartbeats.AsNoTracking().CountAsync());
        }
    }
}
