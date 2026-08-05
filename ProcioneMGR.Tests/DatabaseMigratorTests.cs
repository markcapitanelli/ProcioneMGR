using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-08-05] Migrate-on-startup.
///
/// <para>Il contratto: applicare lo schema all'avvio NON deve poter rompere l'avvio. Un host che
/// non ha l'assembly delle migrazioni (i satelliti non lo ricevono), un lock occupato da un altro
/// host, o l'interruttore spento devono produrre una RIGA DI LOG e la prosecuzione — mai
/// un'eccezione che risale, e mai il silenzio che lascia credere che lo schema sia allineato.</para>
/// </summary>
[Collection("Postgres")]
public sealed class DatabaseMigratorTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public DatabaseMigratorTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private IServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Services.Security.IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;
        return provider;
    }

    [Fact]
    public async Task Spento_NonTocaNienteELoDichiara()
    {
        var outcome = await DatabaseMigrator.MigrateAsync(
            Build(), new DatabaseMigrationOptions { AutoMigrate = false }, NullLogger.Instance);

        Assert.False(outcome.Ran);
        Assert.Equal("disattivata da configurazione", outcome.Skipped);
        Assert.Empty(outcome.Applied);
    }

    /// <summary>
    /// Nei test l'assembly delle migrazioni NON è caricabile (il progetto di test non lo
    /// referenzia): è esattamente la condizione degli host satelliti, e il comportamento atteso è
    /// dichiararlo senza lanciare. È la garanzia che un pod senza quella DLL parte comunque.
    /// </summary>
    [Fact]
    public async Task SenzaAssemblyDelleMigrazioni_DichiaraENonLancia()
    {
        var outcome = await DatabaseMigrator.MigrateAsync(
            Build(), new DatabaseMigrationOptions { AutoMigrate = true }, NullLogger.Instance);

        // O non trova l'assembly (satellite), o non ha nulla da fare: in nessun caso esplode.
        Assert.True(outcome.Skipped is "assembly delle migrazioni non disponibile" || outcome.Ran,
            $"Esito inatteso: Ran={outcome.Ran}, Skipped={outcome.Skipped}");
        Assert.Empty(outcome.Applied);
    }

    /// <summary>Le opzioni di default accendono la migrazione: un rimedio spento non rimedia niente.</summary>
    [Fact]
    public void Default_MigrazioneAccesa()
    {
        var o = new DatabaseMigrationOptions();

        Assert.True(o.AutoMigrate);
        Assert.True(o.LockTimeoutSeconds >= 5);
    }

    /// <summary>
    /// Due chiamate concorrenti sullo stesso database non devono ostacolarsi: l'advisory lock le
    /// serializza e la seconda trova il lavoro già fatto (o niente da fare).
    /// </summary>
    [Fact]
    public async Task DueChiamateConcorrenti_NessunaEsplode()
    {
        var services = Build();
        var options = new DatabaseMigrationOptions { AutoMigrate = true, LockTimeoutSeconds = 30 };

        var a = DatabaseMigrator.MigrateAsync(services, options, NullLogger.Instance);
        var b = DatabaseMigrator.MigrateAsync(services, options, NullLogger.Instance);

        var results = await Task.WhenAll(a, b);

        Assert.All(results, r => Assert.Empty(r.Applied));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
