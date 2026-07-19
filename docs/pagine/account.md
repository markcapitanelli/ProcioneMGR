# Pagine Account (Identity) — `/Account/*`

| | |
|---|---|
| **Cartella sorgente** | [`ProcioneMGR/Components/Account/`](../../ProcioneMGR/Components/Account) |
| **Route** | `/Account/...` e `/Account/Manage/...` |
| **Accesso** | Pubbliche (login/registrazione) o utente autenticato (gestione profilo) |
| **Origine** | Scaffold standard **ASP.NET Core Identity** per Blazor, con personalizzazioni puntuali |

## A cosa serve

È il blocco di autenticazione e gestione del profilo utente, basato sullo scaffold Identity
di ASP.NET Core. Non fa parte del dominio trading: gestisce chi entra e come.

## Le pagine

### Autenticazione (`Pages/`)

| Pagina | Route | Funzione |
|---|---|---|
| `Login.razor` | `/Account/Login` | Accesso con email/password (+ passkey) |
| `Register.razor` | `/Account/Register` | Registrazione nuovo utente |
| `LoginWith2fa.razor` / `LoginWithRecoveryCode.razor` | `/Account/LoginWith2fa`, `…RecoveryCode` | Secondo fattore TOTP / codici di recupero |
| `ForgotPassword.razor` / `ResetPassword.razor` (+ conferme) | `/Account/ForgotPassword`, … | Recupero password |
| `ConfirmEmail.razor` / `ResendEmailConfirmation.razor` / `RegisterConfirmation.razor` | `/Account/Confirm…` | Conferma email |
| `ExternalLogin.razor` | `/Account/ExternalLogin` | Provider esterni (se configurati) |
| `AccessDenied.razor` / `Lockout.razor` / `InvalidUser.razor` / `InvalidPasswordReset.razor` | — | Pagine di stato/errore |

### Gestione profilo (`Pages/Manage/`, layout `ManageLayout` con menu laterale)

| Pagina | Funzione |
|---|---|
| `Index.razor` | Profilo (username/telefono) |
| `Email.razor` | Cambio email con conferma |
| `ChangePassword.razor` / `SetPassword.razor` | Cambio/impostazione password |
| `TwoFactorAuthentication.razor` + `EnableAuthenticator` / `Disable2fa` / `ResetAuthenticator` / `GenerateRecoveryCodes` | Ciclo di vita completo del 2FA (TOTP) |
| `Passkeys.razor` / `RenamePasskey.razor` | Gestione **passkey** (WebAuthn, con JS dedicato `PasskeySubmit.razor.js`) |
| `ExternalLogins.razor` | Collegamento provider esterni |
| `PersonalData.razor` / `DeletePersonalData.razor` | Download / cancellazione dati personali (GDPR) |

### Infrastruttura (`Shared/` e file `.cs`)

- `IdentityComponentsEndpointRouteBuilderExtensions.cs` — endpoint aggiuntivi Identity
  (logout, download dati, ecc.).
- `IdentityRevalidatingAuthenticationStateProvider.cs` — rivalida periodicamente lo stato
  di autenticazione del circuito Blazor Server (revoche/lockout hanno effetto senza logout).
- `IdentityRedirectManager.cs` — redirect con status message.
- `IdentityNoOpEmailSender.cs` — sender email **no-op**: i link di conferma vengono resi
  disponibili senza un vero server SMTP (setup single-tenant/self-hosted).

## Personalizzazioni rilevanti rispetto allo scaffold

1. **Bootstrap del ruolo Admin** — in [`Register.razor:90`](../../ProcioneMGR/Components/Account/Pages/Register.razor):
   *il PRIMO utente registrato (DB vuoto) diventa `Admin`*, tutti i successivi partono
   `User`. È la regola che rende utilizzabile la piattaforma appena installata, ed è
   coerente con quanto documentato in [Gestione Utenti](admin-users.md).
2. **Passkey/WebAuthn** abilitate (template Identity recente), con componenti dedicati.
3. Le viste sono in italiano dove toccate dalla piattaforma; la logica resta quella dello
   scaffold, così gli aggiornamenti di template restano applicabili.

## Collegamenti con le altre pagine

- La [Home](home.md) mostra la vista anonima con i bottoni Login/Registrati.
- I ruoli assegnati qui governano la visibilità dell'intera navigazione
  ([`NavModel`](../../ProcioneMGR/Components/Layout/NavModel.cs)) e gli `[Authorize]` di
  ogni pagina.

## Nota

Le pagine `Error.razor` (`/Error`) e `NotFound.razor` (`/not-found`) in
[`Components/Pages/`](../../ProcioneMGR/Components/Pages) completano il quadro delle pagine
"di servizio": pagina d'errore con RequestId e pagina 404 minimale.
