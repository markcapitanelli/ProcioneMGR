using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// LA REGISTRAZIONE LIBERA È CHIUSA (R22, 2026-09-06).
///
/// <para><c>/Account/Register</c> non aveva né attributo né interruttore: chiunque raggiungesse
/// l'app poteva crearsi un account, e il nuovo account nasceva col ruolo <c>User</c> — abbastanza
/// per ogni pagina protetta dal solo <c>[Authorize]</c> nudo, fra cui (fino a R21)
/// <c>/settings/exchanges</c>, cioè il pool di chiavi con cui il motore firma gli ordini. Su
/// <c>localhost</c> era innocuo; con l'esposizione preparata il 2026-09-05 era la porta d'ingresso.</para>
///
/// <para>Qui si esercita <b>tutta</b> la tabella di verità della decisione — che è pura di
/// proposito: la pagina inietta <c>UserManager</c> e <c>SignInManager</c>, classi concrete che
/// bUnit non sostituisce, quindi se la logica stesse nel markup non sarebbe collaudabile affatto.
/// Il cancello vero (manopola + tabella utenti) si prova invece contro un Postgres reale.</para>
/// </summary>
public sealed class RegistrazioneChiusaTests
{
    // --- La decisione pura -----------------------------------------------------------------

    [Fact]
    public void DatabaseSenzaUtenti_ConsenteIlPrimoAccount_ancheAManopolaChiusa()
    {
        // È l'unico modo di creare il primo Admin: senza Admin nessuno potrebbe aprire la manopola.
        var d = RegistrationPolicy.Decide(allowSelfRegistration: false, anyUserExists: false);

        Assert.True(d.Allowed);
        Assert.Contains("Primo accesso", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ConUtentiEsistenti_ManopolaChiusa_Rifiuta_EDiceDoveSiApre()
    {
        var d = RegistrationPolicy.Decide(allowSelfRegistration: false, anyUserExists: true);

        Assert.False(d.Allowed);
        // Un rifiuto muto manda a indovinare: la ragione deve indicare il rimedio.
        Assert.Contains("/admin/users", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ConUtentiEsistenti_ManopolaAperta_Consente()
    {
        var d = RegistrationPolicy.Decide(allowSelfRegistration: true, anyUserExists: true);

        Assert.True(d.Allowed);
        Assert.Contains("User", d.Reason, StringComparison.Ordinal); // dice anche quale ruolo otterrà
    }

    /// <summary>
    /// Il default del POCO è ciò che vige quando la sezione <c>Registration</c> manca del tutto
    /// dall'<c>appsettings.json</c> vivo — che è il caso normale finché nessuno tocca il pannello.
    /// Se qualcuno lo cambiasse in <c>true</c> «per comodità», la porta si riaprirebbe in silenzio.
    /// </summary>
    [Fact]
    public void IlDefaultDelPoco_EChiusa()
    {
        Assert.False(new RegistrationOptions().AllowSelfRegistration);
        Assert.Equal("Registration", RegistrationOptions.SectionName);
    }

    // --- Il cancello: manopola aperta = nessuna query -----------------------------------------

    /// <summary>
    /// Con la manopola aperta la decisione non dipende dal conteggio, e il cancello NON deve
    /// interrogare il database: lo prova una factory che esplode se qualcuno la usa. Serve perché
    /// il cancello lo chiama anche il menù di navigazione, a ogni render per ogni anonimo.
    /// </summary>
    [Fact]
    public async Task ManopolaAperta_NonInterrogaIlDatabase()
    {
        var gate = new RegistrationGate(
            new EsplodeSeUsata(),
            new RegistrationOptions { AllowSelfRegistration = true }.AsMonitor(),
            NullLogger<RegistrationGate>.Instance);

        Assert.True((await gate.EvaluateAsync()).Allowed);
    }

    /// <summary>
    /// Fail-closed sulla sicurezza (quarta regola del progetto): se lo stato non è verificabile la
    /// registrazione resta chiusa — ma il messaggio dice che è un guasto, non una regola, perché
    /// un rifiuto travestito da politica manda a cercare il problema dalla parte sbagliata.
    /// </summary>
    [Fact]
    public async Task DatabaseIrraggiungibile_RestaChiusa_EDichiaraIlGuasto()
    {
        var gate = new RegistrationGate(
            new EsplodeSeUsata(),
            new RegistrationOptions { AllowSelfRegistration = false }.AsMonitor(),
            NullLogger<RegistrationGate>.Instance);

        var d = await gate.EvaluateAsync();

        Assert.False(d.Allowed);
        Assert.Contains("database", d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EsplodeSeUsata : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            throw new InvalidOperationException("Il cancello non doveva interrogare il database.");
    }
}

/// <summary>
/// Il cancello contro un Postgres vero: è l'unico modo di provare l'eccezione di primo accesso,
/// che dipende dal <b>contenuto</b> della tabella utenti e non da una configurazione.
/// </summary>
[Collection("Postgres")]
public sealed class RegistrazioneChiusaDbTests(PostgresFixture pg) : IAsyncDisposable
{
    private readonly string _connString = pg.CreateDatabase();
    private ServiceProvider? _provider;

    private async Task<IDbContextFactory<ApplicationDbContext>> BuildDbAsync(bool conUtente)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            if (conUtente)
            {
                db.Users.Add(new ApplicationUser { Id = "u1", UserName = "mark", Email = "mark@example.io" });
                await db.SaveChangesAsync();
            }
        }
        return dbFactory;
    }

    [Fact]
    public async Task TabellaUtentiVuota_LaRegistrazioneEAperta_ancheConLaManopolaChiusa()
    {
        var db = await BuildDbAsync(conUtente: false);
        var gate = new RegistrationGate(db, new RegistrationOptions().AsMonitor(), NullLogger<RegistrationGate>.Instance);

        var d = await gate.EvaluateAsync();

        Assert.True(d.Allowed);
        Assert.Contains("Primo accesso", d.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConAlmenoUnUtente_LaManopolaChiusaChiudeDavvero()
    {
        var db = await BuildDbAsync(conUtente: true);
        var gate = new RegistrationGate(db, new RegistrationOptions().AsMonitor(), NullLogger<RegistrationGate>.Instance);

        Assert.False((await gate.EvaluateAsync()).Allowed);
    }

    [Fact]
    public async Task ConAlmenoUnUtente_LaManopolaApertaRiapre()
    {
        var db = await BuildDbAsync(conUtente: true);
        var gate = new RegistrationGate(db,
            new RegistrationOptions { AllowSelfRegistration = true }.AsMonitor(),
            NullLogger<RegistrationGate>.Instance);

        Assert.True((await gate.EvaluateAsync()).Allowed);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
