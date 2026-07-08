using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Security;

namespace ProcioneMGR.Migrations.Postgres;

/// <summary>
/// Factory di design-time usata SOLO per <b>applicare</b> le migrazioni PostgreSQL, es.:
/// <c>dotnet ef database update --project ProcioneMGR.Migrations.Postgres --startup-project ProcioneMGR.Migrations.Postgres</c>.
///
/// PUNTO CRITICO — SchemaVersion Identity: l'host dell'app imposta
/// <c>IdentityOptions.Stores.SchemaVersion = Version3</c> via DI, mentre questa factory costruisce un
/// <c>new ApplicationDbContext</c> "nudo" (schema passkey di default). Il modello nudo differisce
/// quindi dallo snapshot generato dall'host (Version3) SOLO sulle tabelle passkey, e ciò farebbe
/// scattare <c>PendingModelChangesWarning</c> come errore, bloccando <c>database update</c>. Qui la
/// warning viene declassata: la <b>fonte di verità in applicazione è il file di migrazione</b> (che
/// crea correttamente le tabelle passkey Version3 — <c>AspNetUserPasskeys.Data</c> come <c>jsonb</c>),
/// non il modello ricostruito a design-time. Le NUOVE migrazioni vanno comunque generate con
/// <c>--startup-project ProcioneMGR</c> (l'host reale = fonte di verità del modello).
///
/// L'encryption è passthrough: durante <c>database update</c> non si legge/scrive alcun dato cifrato.
/// Connection string: env <c>PostgresConnection</c> se presente, altrimenti il default locale.
/// </summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("PostgresConnection")
                   ?? "Host=localhost;Port=5432;Database=procionemgr;Username=procione;Password=Procione2026Pg_secure";

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(conn, npgsql =>
                npgsql.MigrationsAssembly(typeof(PostgresDesignTimeFactory).Assembly.GetName().Name))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return new ApplicationDbContext(options, new PassthroughEncryption());
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }
}
