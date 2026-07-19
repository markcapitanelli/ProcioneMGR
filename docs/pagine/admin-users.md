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
| **User** | Accesso base: Dashboard, Backtest, le proprie strategie e credenziali |
| **Manager** | In più: Watchlist, Optimization, Discovery, Ensemble, ML Lab e tutte le pagine di analisi avanzata |
| **Admin** | In più: questa pagina, Autonomia, Backup e le impostazioni di sicurezza del trading live |

Regola di bootstrap: **il primo utente registrato diventa Admin automaticamente**; tutti
gli altri partono come User.

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
