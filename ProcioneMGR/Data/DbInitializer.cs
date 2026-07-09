using Microsoft.AspNetCore.Identity;

namespace ProcioneMGR.Data;

/// <summary>
/// Inizializzazione all'avvio: garantisce l'esistenza dei ruoli applicativi (Admin / Manager / User).
/// Lo schema del database si applica come passo separato (migrate-on-deploy, vedi InitializeAsync).
/// La logica "primo utente = Admin" vive invece nel flusso di registrazione.
/// </summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        // Lo schema PostgreSQL si applica come passo separato (`dotnet ef database update`, vedi
        // docs/POSTGRES_MIGRATION.md): l'app NON referenzia l'assembly ProcioneMGR.Migrations.Postgres
        // (per evitare un ciclo di progetti), quindi niente migrate-on-startup — pattern
        // migrate-on-deploy del tutto ordinario in produzione. Qui garantiamo solo i ruoli applicativi.
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
