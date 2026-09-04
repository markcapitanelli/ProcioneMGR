using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-05] La lettura in SOLA LETTURA del registro di osservazione: serve alla promozione per
/// giudicare con lo stesso orologio del ritiro senza essere lei a farlo scorrere. Il 2026-09-01 il
/// proprietario ha spento AutoPromoteToTestnet perché i due orologi divergevano del 42 % sulla
/// stessa corsia nello stesso istante (8,73 gg da StartedAtUtc contro 6,14 dal registro).
/// </summary>
[Collection("Postgres")]
public class RegistroOsservazioneLetturaTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public RegistroOsservazioneLetturaTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<LaneObservationLedger> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        return new LaneObservationLedger(dbFactory, NullLogger<LaneObservationLedger>.Instance);
    }

    private static readonly DateTime T0 = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private const string Id1 = "UNI/USDT|4h|s1";

    /// <summary>IL NULLO: una corsia mai vista non ha osservazione, e la lettura lo dice con null — non con zero.</summary>
    [Fact]
    public async Task CorsiaMAIvista_daNULL_nonZERO()
    {
        var ledger = await BuildAsync();
        Assert.Null(await ledger.ReadAsync(5, Id1));
    }

    /// <summary>La lettura restituisce ciò che l'accredito ha scritto, e NON fa scorrere l'orologio.</summary>
    [Fact]
    public async Task LaLETTURAnonACCREDITA()
    {
        var ledger = await BuildAsync();
        await ledger.AccumulateAsync(5, Id1, isRunning: true, T0);
        await ledger.AccumulateAsync(5, Id1, isRunning: true, T0.AddMinutes(15));

        var prima = await ledger.ReadAsync(5, Id1);
        Assert.NotNull(prima);
        Assert.Equal(TimeSpan.FromMinutes(15), prima.Value.Observed);
        Assert.Equal(T0, prima.Value.FirstSeenUtc);

        // Dieci letture dopo, l'osservazione è la stessa: leggere non è un tick.
        for (var i = 0; i < 10; i++) await ledger.ReadAsync(5, Id1);
        var dopo = await ledger.AccumulateAsync(5, Id1, isRunning: true, T0.AddMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(30), dopo.Observed);
    }

    /// <summary>
    /// Identità diversa da quella registrata = esperimento che il lettore della flotta non ha ancora
    /// visto: l'osservazione è ignota, e l'ignoranza non si trasforma nel numero della corsia precedente.
    /// </summary>
    [Fact]
    public async Task IdentitaDIVERSA_daNULL_nonIlNUMEROdellAltra()
    {
        var ledger = await BuildAsync();
        await ledger.AccumulateAsync(5, Id1, isRunning: true, T0);
        await ledger.AccumulateAsync(5, Id1, isRunning: true, T0.AddDays(6));

        Assert.NotNull(await ledger.ReadAsync(5, Id1));
        Assert.Null(await ledger.ReadAsync(5, "UNI/USDT|4h|s2"));
    }
}
