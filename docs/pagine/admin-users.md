# Gestione Utenti — `/admin/users`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/AdminUsers.razor`](../../ProcioneMGR/Components/Pages/AdminUsers.razor) (~140 righe) |
| **Route** | `/admin/users` |
| **Sezione navigazione** | Configurazione |
| **Accesso** | `[Authorize(Roles = Admin)]` — solo Admin |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Gestisce **utenti e ruoli**. Il modello a tre ruoli con permessi crescenti (dal `GuidaPanel`):

| Ruolo | Permessi |
|---|---|
| **User** | Accesso base: Home, Backtest, Analisi della serie, le proprie strategie |
| **Manager** | In più: Dashboard, Watchlist, Optimization, Discovery, Ensemble, ML Lab e tutte le pagine di analisi avanzata |
| **Admin** | In più: questa pagina, Autonomia, Protezioni, Backup, **Credenziali Exchange** e le impostazioni di sicurezza del trading live |

> **Cambiato il 2026-09-06 ([R21](../audit/09_RISKS_AND_TECH_DEBT.md#r21)).** Dashboard e Credenziali
> Exchange non sono più di livello `User`: la prima *scrive* sulle serie di mercato condivise, la
> seconda alimenta il pool di chiavi con cui il motore firma gli ordini — un pool solo, comune al
> progetto, non una cassetta privata per utente.

Regola di bootstrap: **il primo utente registrato diventa Admin automaticamente**; tutti
gli altri partono come User.

## Registrazione di nuovi account (R22, 2026-09-06)

Il pannello in cima alla pagina governa `Registration:AllowSelfRegistration`, **default `false`**:
la registrazione libera da `/Account/Register` è **chiusa**. Fino al 2026-09-06 era aperta a chiunque
raggiungesse l'app, e ogni nuovo account otteneva il ruolo `User` — innocuo su `localhost`, la porta
d'ingresso su un'istanza esposta.

- Con la manopola **chiusa**: il modulo di registrazione non si disegna, e un POST costruito a mano
  viene comunque rifiutato (il ricontrollo è lato server). Menù, Home e pagina di login non mostrano
  più l'invito a registrarsi.
- Con la manopola **aperta**: chiunque può registrarsi e ottiene il ruolo `User`. Aprila per il tempo
  necessario a far registrare la persona, poi richiudila — il badge nell'intestazione del pannello
  dice sempre quale dei due stati è in vigore *nel processo*, non solo nel file.
- **Eccezione di primo accesso**: su un database senza nessun utente la registrazione è consentita a
  prescindere dalla manopola. È l'unico modo di creare il primo Admin, e senza un Admin questo
  pannello non sarebbe raggiungibile.

## Struttura della pagina

Una sola tabella (righe 41–84): email, badge dei ruoli attuali, azione contestuale —
"Promuovi a Manager" per gli User, "Riporta a User" per i Manager, nessuna azione per gli
Admin ("account Admin" non modificabili da qui, per sicurezza).

## Come funziona (flusso del codice)

- **Caricamento** (righe 104–114): `UserManager.Users` ordinati per email +
  `GetRolesAsync` per riga (ASP.NET Identity).
- **Cambio ruolo** (righe 116–139): guardia in profondità — se l'utente target è Admin, il
  cambio è rifiutato anche se la UI non mostrava il bottone. Poi
  `RemoveFromRolesAsync(ruoli non-Admin)` + `AddToRoleAsync(nuovo)`: il ruolo non-Admin è
  sempre **singolo** (sostituzione, non accumulo).

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `UserManager<ApplicationUser>` | Identity: utenti e ruoli | (framework ASP.NET Identity) |
| `AppRoles` | Le costanti User/Manager/Admin usate in tutti gli `[Authorize]` | [`Data/`](../../ProcioneMGR/Data) |

## Dati letti / scritti

- **Legge/Scrive**: tabelle Identity (`AspNetUsers`, `AspNetUserRoles`).

## Collegamenti con le altre pagine

- Il gating per ruolo qui amministrato governa la visibilità di **tutte** le voci di
  navigazione (vedi `NavModel.IsVisible` in
  [`Components/Layout/NavModel.cs`](../../ProcioneMGR/Components/Layout/NavModel.cs)) e gli
  `[Authorize]` delle singole pagine.
- Le pagine [Account](account.md) (Identity) gestiscono il profilo del singolo utente.
