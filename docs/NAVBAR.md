# NavBar — struttura per workflow utente

La sidebar di navigazione è organizzata in **blocchi per workflow** invece di una lista piatta.
Fonte unica di verità: [`Components/Layout/NavModel.cs`](../ProcioneMGR/Components/Layout/NavModel.cs),
condivisa da `NavMenu.razor` (rendering della sidebar), `Breadcrumb.razor` (percorso
contestuale) e `CommandPalette.razor` (ricerca globale Ctrl+K).

## Blocchi

| Blocco                    | Accent  | Comportamento                   | Razionale                                        |
|---------------------------|---------|---------------------------------|--------------------------------------------------|
| 🏠 **Overview**           | blu     | sempre aperto, non collassabile | punto di partenza: Home + Dashboard              |
| 📊 **Dati & Monitoraggio**| verde   | sempre aperto, non collassabile | la materia prima: watchlist, analisi, metriche   |
| 🔬 **Ricerca & Sviluppo** | giallo  | sempre aperto, non collassabile | workflow creazione strategie, in ordine naturale |
| 🚀 **Trading**            | arancio | sempre aperto, non collassabile | operatività: control center, strategie, execution|
| 🧠 **Strumenti Avanzati** | viola   | collassabile, chiuso di default | automazione e analisi di nicchia                 |
| ⚙️ **Configurazione**     | grigio  | collassabile, chiuso di default | impostazioni + amministrazione (role-based)      |
| ⚪ **Account**            | grigio  | sempre in fondo, separato       | profilo + logout                                 |

Un blocco chiuso viene **auto-aperto** quando contiene la pagina corrente, così la voce
attiva è sempre visibile. Lo stato aperto/chiuso dei blocchi collassabili è **persistito in
`localStorage`** (chiave `procione.nav.sections`) e ripristinato al reload.

> La lettura di `localStorage` avviene in `OnAfterRenderAsync(firstRender)`, non durante il
> prerender (dove non è disponibile): questo evita eccezioni e non blocca la UI.

> **Interattività:** il layout (`MainLayout`) è renderizzato in SSR statico, mentre solo le
> singole pagine sono `InteractiveServer`. Poiché `NavMenu` vive nel layout, per far
> funzionare il toggle dei blocchi (`@onclick`) e la lettura di `localStorage` il componente
> dichiara esso stesso `@rendermode InteractiveServer` in cima a `NavMenu.razor`: diventa
> un'isola interattiva dentro un layout statico. Senza questa direttiva i blocchi restano
> visibili ma non collassano. Lo stesso vale per `CommandPalette` (top-row del layout).

## Ruoli

Ogni voce dichiara i ruoli abilitati nel campo `Roles` di `NavItem`:

- `Roles = null` → qualsiasi utente **autenticato**;
- `[Manager, Admin]` → solo Manager e Admin;
- `[Admin]` → solo Admin.

Un blocco senza voci visibili per il ruolo corrente viene **nascosto** interamente.
Il filtro è centralizzato in `NavModel.IsVisible(item, user)`, usato sia dalla sidebar
sia dalla command palette, e rispecchia 1:1 il vecchio gating `AuthorizeView`.

## Come aggiungere una nuova voce

Modifica **solo** `NavModel.cs`, aggiungendo un `NavItem` nella sezione desiderata:

```csharp
new NavItem("mia-route", "Etichetta", "bi-icona", "Descrizione breve.", ManagerAndAdmin),
```

- `Href`: URL relativo **senza** slash iniziale (es. `"admin/backup"`); `""` = Home.
- `Label`: testo in italiano.
- `Icon`: classe [Bootstrap Icons](https://icons.getbootstrap.com/) (es. `"bi-graph-up-arrow"`).
- `Description`: frase breve, usata come **tooltip** in sidebar e come testo secondario
  nella ricerca globale Ctrl+K.
- `Roles`: `null`, `ManagerAndAdmin` o `AdminOnly`.
- `Match`: `true` solo per Home (match esatto della route).

La voce compare automaticamente nella sidebar, nel breadcrumb **e** nella command palette:
nessun altro file da toccare.

## Ricerca globale (Ctrl+K)

`CommandPalette.razor` (in `Components/Shared`, inclusa nella top-row di `MainLayout`)
apre un overlay di ricerca con **Ctrl+K / Cmd+K** o cliccando il campo "Cerca…" in alto.
Cerca su etichetta, descrizione, href e nome sezione; frecce ↑/↓ per scorrere, Invio per
aprire, Esc per chiudere. L'hotkey è registrata da
[`wwwroot/js/command-palette.js`](../ProcioneMGR/wwwroot/js/command-palette.js).
L'indice contiene solo le pagine visibili al ruolo dell'utente corrente.

## Come personalizzare i colori dei blocchi

Il pallino di ogni blocco usa il campo `Accent` (colore CSS) definito in `NavModel.cs`:

```csharp
new NavSection("ricerca", "Ricerca & Sviluppo", "#fbbf24", Collapsible: false, [ ... ]);
//                                              ^^^^^^^ colore del pallino
```

Gli stili di header, caret e animazione di collasso sono in
[`Components/Layout/NavMenu.razor.css`](../ProcioneMGR/Components/Layout/NavMenu.razor.css);
gli stili di breadcrumb, tema scuro GitHub-style e command palette in
[`wwwroot/app.css`](../ProcioneMGR/wwwroot/app.css).

## Tema scuro

L'app usa il tema dark di Bootstrap 5.3 (`data-bs-theme="dark"` su `<html>` in
`App.razor`) ricalibrato sulla palette GitHub: sfondo `#0d1117`, superfici `#161b22`,
bordi `#30363d`, accent blu `#58a6ff`, primario verde `#238636`. Le variabili sono in
`app.css` (sezione *Tema scuro GitHub-style*): le pagine che usano componenti Bootstrap
standard si adattano da sole, incluse le utility `bg-light`/`table-light` (rimappate
su superficie scura).

## Breadcrumb

`Breadcrumb.razor` (in `Components/Shared`) mostra `Home › Sezione › Pagina` derivandolo
dalla route corrente tramite `NavModel.Resolve(...)`. Sulla Home e sulle pagine non mappate
(es. area Account/Identity) non viene renderizzato nulla. È inserito in `MainLayout.razor`
in cima all'area contenuti.
