namespace ProcioneMGR.Services.Security;

/// <summary>
/// Chi può creare un account su questa istanza.
///
/// <para><b>PERCHÉ ESISTE (R22, 2026-09-06).</b> <c>/Account/Register</c> era aperta a chiunque
/// raggiungesse l'app, e il nuovo account riceveva il ruolo <c>User</c>
/// (<c>Register.razor</c>, «il primo utente diventa Admin, tutti gli altri User»). Finché la
/// piattaforma girava su <c>localhost</c> la cosa non aveva conseguenze; con l'esposizione
/// preparata il 2026-09-05 diventa la porta d'ingresso a ogni pagina protetta dal solo
/// <c>[Authorize]</c> nudo — fra cui, fino a R21, <c>/settings/exchanges</c>, cioè il pool di
/// credenziali con cui il motore firma gli ordini.</para>
///
/// <para><b>CHIUSA È IL DEFAULT.</b> La sezione <c>Registration</c> può mancare del tutto
/// dall'<c>appsettings.json</c> vivo: il binder lascia allora il default del POCO, che è
/// <c>false</c>. Fail-closed sulla sicurezza, come vuole la quarta regola del progetto — una
/// configurazione dimenticata non deve poter riaprire la porta.</para>
///
/// <para><b>L'ECCEZIONE DI PRIMO ACCESSO</b> non sta qui ma in
/// <see cref="RegistrationPolicy"/>: su un database senza nessun utente la registrazione è
/// comunque consentita, perché è l'unico modo di creare il primo Admin — e senza un Admin non
/// esiste nessuno che possa aprire questa manopola. Vederla come chiave sarebbe stato peggio:
/// una seconda verità da tenere allineata al contenuto della tabella utenti.</para>
///
/// <para>Nessuna proprietà calcolata: <c>SaveSectionAsync</c> serializza il POCO intero e una
/// get-only diventerebbe una chiave inventata nel file (guardiano
/// <c>ConfigPocoComputedPropertyTests</c>).</para>
/// </summary>
public sealed class RegistrationOptions
{
    public const string SectionName = "Registration";

    /// <summary>
    /// <c>true</c> = chiunque raggiunga l'app può crearsi un account (ruolo <c>User</c>).
    /// <c>false</c> (default) = solo il primo accesso su database vuoto. Si apre e si richiude da
    /// <c>/admin/users</c>: il pannello mostra lo stato in vigore, non solo quello salvato.
    /// </summary>
    public bool AllowSelfRegistration { get; set; }
}
