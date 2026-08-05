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

        // [2026-08-05] Lo schema lo applica ORA <see cref="DatabaseMigrator"/>, chiamato in
        // Program.cs subito prima di qui. Fino a quel giorno si applicava solo a mano
        // (`dotnet ef database update`): l'app non referenzia l'assembly delle migrazioni per non
        // creare un ciclo di progetti, e da lì la conclusione — sbagliata — che non si potesse
        // migrare all'avvio. Si può: EF risolve quell'assembly per NOME, basta che la DLL sia
        // accanto all'eseguibile (ci pensa un target di copia nel progetto delle migrazioni).
        // Il migrate-on-deploy resta possibile e supportato: Database:AutoMigrate=false.
        // Qui restano solo i ruoli applicativi.
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
