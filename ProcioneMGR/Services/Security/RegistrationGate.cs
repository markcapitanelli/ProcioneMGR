using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Security;

/// <summary>
/// Esito del cancello: se un account si può creare, e la ragione — che è testo da mostrare, non
/// un codice. Un rifiuto muto manderebbe la persona a indovinare.
/// </summary>
public sealed record RegistrationDecision(bool Allowed, string Reason);

/// <summary>
/// La decisione, PURA (R22, 2026-09-06): due fatti in ingresso, nessun I/O, nessuna dipendenza.
/// Sta separata dal servizio perché la tabella di verità si possa esercitare per intero senza
/// database — <c>Register.razor</c> inietta <c>SignInManager</c> e <c>UserManager</c>, classi
/// concrete che in bUnit non si sostituiscono, quindi la pagina non è collaudabile a rendering: la
/// logica va tenuta dove i test possono arrivarci.
/// </summary>
public static class RegistrationPolicy
{
    /// <param name="allowSelfRegistration">La manopola <c>Registration:AllowSelfRegistration</c>.</param>
    /// <param name="anyUserExists">Se la tabella utenti ha già almeno una riga.</param>
    public static RegistrationDecision Decide(bool allowSelfRegistration, bool anyUserExists)
    {
        // Primo accesso: senza utenti non esiste nessun Admin che possa aprire la manopola, quindi
        // il database vuoto È il permesso. Questa registrazione crea l'amministratore (la regola
        // «il primo utente diventa Admin» vive in Register.razor).
        if (!anyUserExists)
        {
            return new RegistrationDecision(true,
                "Primo accesso: il database non ha ancora nessun utente, quindi questa registrazione "
                + "crea l'account amministratore della piattaforma.");
        }

        return allowSelfRegistration
            ? new RegistrationDecision(true,
                "Registrazione aperta dal pannello in /admin/users: chi si registra ora riceve il ruolo User.")
            : new RegistrationDecision(false,
                "La registrazione libera è chiusa su questa istanza. Chiedi a un amministratore di "
                + "aprirla dal pannello «Registrazione di nuovi account» in /admin/users.");
    }
}

/// <summary>
/// Il cancello vero: mette insieme la manopola e lo stato della tabella utenti.
/// Lo usano <c>Register.razor</c> ed <c>ExternalLogin.razor</c> prima di creare l'account, e le tre
/// superfici che offrono il collegamento (menù, Home, pagina di login) per non invitare a una porta
/// chiusa.
/// </summary>
public interface IRegistrationGate
{
    Task<RegistrationDecision> EvaluateAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IRegistrationGate"/>
public sealed class RegistrationGate(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptionsMonitor<RegistrationOptions> options,
    ILogger<RegistrationGate> logger) : IRegistrationGate
{
    // Una volta visto un utente non se ne torna a zero se non svuotando la tabella: la memoria
    // evita una query per ogni render del menù agli anonimi. Un ripristino che azzeri davvero gli
    // utenti si accompagna a un riavvio, che la azzera con sé.
    private volatile bool _seenAUser;

    public async Task<RegistrationDecision> EvaluateAsync(CancellationToken ct = default)
    {
        // Con la manopola aperta la decisione non dipende dal conteggio: si evita anche la query.
        if (options.CurrentValue.AllowSelfRegistration)
        {
            return RegistrationPolicy.Decide(allowSelfRegistration: true, anyUserExists: true);
        }

        try
        {
            return RegistrationPolicy.Decide(allowSelfRegistration: false, await AnyUserAsync(ct));
        }
        catch (Exception ex)
        {
            // Fail-closed: se lo stato non è verificabile NON si apre. Dirlo, però — un rifiuto che
            // sembra una regola quando è un guasto manda a cercare il problema dalla parte sbagliata.
            logger.LogWarning(ex, "Cancello della registrazione: stato degli utenti non verificabile, resta chiusa.");
            return new RegistrationDecision(false,
                "Registrazione non disponibile: la piattaforma non riesce a verificare lo stato degli account "
                + "(database non raggiungibile). Riprova più tardi.");
        }
    }

    private async Task<bool> AnyUserAsync(CancellationToken ct)
    {
        if (_seenAUser) return true;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var any = await db.Users.AnyAsync(ct);
        if (any) _seenAUser = true;
        return any;
    }
}
