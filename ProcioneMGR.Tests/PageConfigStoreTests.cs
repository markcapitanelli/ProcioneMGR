using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Preferences;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del PageConfigStore (preset di configurazione pagina + "ultima configurazione usata"):
/// round-trip salva/carica, upsert sullo stesso nome, isolamento per utente/pagina,
/// e lista dei soli preset con nome (l'ultima configurazione resta fuori dal dropdown).
/// </summary>
[Collection("Postgres")]
public sealed class PageConfigStoreTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public PageConfigStoreTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<PageConfigStore> BuildAsync(params string[] userIds)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            // La FK verso AspNetUsers richiede utenti reali.
            foreach (var id in userIds)
            {
                db.Users.Add(new ApplicationUser { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant() });
            }
            await db.SaveChangesAsync();
        }
        return new PageConfigStore(dbFactory);
    }

    [Fact]
    public async Task SaveLoad_RoundTripsJson()
    {
        var store = await BuildAsync("user-1");

        await store.SaveAsync("user-1", "backtest", "Realistico Binance", """{"Fee":0.1,"Leva":1}""");
        var loaded = await store.LoadAsync("user-1", "backtest", "Realistico Binance");

        Assert.Equal("""{"Fee":0.1,"Leva":1}""", loaded);
    }

    [Fact]
    public async Task Save_SameName_Upserts()
    {
        var store = await BuildAsync("user-1");

        await store.SaveAsync("user-1", "backtest", IPageConfigStore.LastUsedName, """{"v":1}""");
        await store.SaveAsync("user-1", "backtest", IPageConfigStore.LastUsedName, """{"v":2}""");

        Assert.Equal("""{"v":2}""", await store.LoadAsync("user-1", "backtest", IPageConfigStore.LastUsedName));
    }

    [Fact]
    public async Task Load_IsIsolated_PerUserAndPage()
    {
        var store = await BuildAsync("user-1", "user-2");

        await store.SaveAsync("user-1", "backtest", "mio", """{"a":1}""");

        Assert.Null(await store.LoadAsync("user-2", "backtest", "mio"));      // altro utente
        Assert.Null(await store.LoadAsync("user-1", "optimization", "mio")); // altra pagina
        Assert.Equal("""{"a":1}""", await store.LoadAsync("user-1", "backtest", "mio"));
    }

    [Fact]
    public async Task ListNames_ExcludesLastUsed_AndSorts()
    {
        var store = await BuildAsync("user-1");

        await store.SaveAsync("user-1", "backtest", IPageConfigStore.LastUsedName, "{}");
        await store.SaveAsync("user-1", "backtest", "zeta", "{}");
        await store.SaveAsync("user-1", "backtest", "alfa", "{}");

        Assert.Equal(new[] { "alfa", "zeta" }, await store.ListNamesAsync("user-1", "backtest"));
    }

    [Fact]
    public async Task Delete_RemovesOnlyThatPreset()
    {
        var store = await BuildAsync("user-1");

        await store.SaveAsync("user-1", "backtest", "uno", "{}");
        await store.SaveAsync("user-1", "backtest", "due", "{}");
        await store.DeleteAsync("user-1", "backtest", "uno");

        Assert.Equal(new[] { "due" }, await store.ListNamesAsync("user-1", "backtest"));
        Assert.Null(await store.LoadAsync("user-1", "backtest", "uno"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
