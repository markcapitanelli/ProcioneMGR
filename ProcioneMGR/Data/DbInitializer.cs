using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ProcioneMGR.Data;

/// <summary>
/// Inizializzazione del database all'avvio: applica le migrazioni pendenti e
/// garantisce l'esistenza dei ruoli applicativi (Admin / Manager / User).
/// La logica "primo utente = Admin" vive invece nel flusso di registrazione.
/// </summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<ApplicationDbContext>();

        // Auto-migrate solo su SQLite (dev): l'assembly delle migrazioni SQLite è ProcioneMGR
        // stesso. Su PostgreSQL l'app NON referenzia l'assembly ProcioneMGR.Migrations.Postgres
        // (per evitare un ciclo di progetti), quindi lo schema si applica come passo separato con
        // `dotnet ef database update` (vedi docs/POSTGRES_MIGRATION.md) — pattern migrate-on-deploy
        // invece di migrate-on-startup, del tutto ordinario in produzione.
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            await db.Database.MigrateAsync();
        }

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in AppRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }
    }
}
