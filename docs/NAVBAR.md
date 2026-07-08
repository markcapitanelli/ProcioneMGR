# NavBar — struttura ibrida a blocchi

La sidebar di navigazione è organizzata in **blocchi** invece di una lista piatta.
Fonte unica di verità: [`Components/Layout/NavModel.cs`](../ProcioneMGR/Components/Layout/NavModel.cs),
condivisa da `NavMenu.razor` (rendering della sidebar) e `Breadcrumb.razor` (percorso contestuale).

## Blocchi

| Blocco             | Pallino  | Comportamento                          | Razionale                                   |
|--------------------|----------|----------------------------------------|---------------------------------------------|
| 🟢 **Fondamenta**  | verde    | sempre aperto, non collassabile        | percorso minimo obbligato, sempre visibile  |
| 🟡 **Analisi**     | giallo   | collassabile, chiuso di default        | strumenti per migliorare strategie esistenti|
| 🔴 **Specialistico**| rosso   | collassabile, chiuso di default        | strumenti di nicchia + amministrazione      |
| ⚪ **Account**     | grigio   | sempre in fondo, separato              | profilo + logout                            |

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
> visibili ma non collassano.

## Ruoli

Ogni voce dichiara i ruoli abilitati nel campo `Roles` di `NavItem`:

- `Roles = null` → qualsiasi utente **autenticato**;
- `[Manager, Admin]` → solo Manager e Admin;
- `[Admin]` → solo Admin.

Un blocco senza voci visibili per il ruolo corrente viene **nascosto** interamente.
Il filtro avviene in `NavMenu.razor` via `context.User.IsInRole(...)`, rispecchiando 1:1
il vecchio gating `AuthorizeView`.

## Come aggiungere una nuova voce

Modifica **solo** `NavModel.cs`, aggiungendo un `NavItem` nella sezione desiderata:

```csharp
new NavItem("mia-route", "Etichetta", "bi-icona", ManagerAndAdmin),
```

- `Href`: URL relativo **senza** slash iniziale (es. `"admin/backup"`); `""` = Home.
- `Label`: testo in italiano.
- `Icon`: classe [Bootstrap Icons](https://icons.getbootstrap.com/) (es. `"bi-graph-up-arrow"`).
- `Roles`: `null`, `ManagerAndAdmin` o `AdminOnly`.
- `Match`: `true` solo per Home (match esatto della route).

La voce compare automaticamente nella sidebar **e** nel breadcrumb: nessun altro file
da toccare.

## Come personalizzare i colori dei blocchi

Il pallino di ogni blocco usa il campo `Accent` (colore CSS) definito in `NavModel.cs`:

```csharp
new NavSection("analisi", "Analisi", "#fbbf24", Collapsible: true, [ ... ]);
//                                    ^^^^^^^ colore del pallino
```

Gli stili di header, caret e animazione di collasso sono in
[`Components/Layout/NavMenu.razor.css`](../ProcioneMGR/Components/Layout/NavMenu.razor.css);
gli stili del breadcrumb in [`wwwroot/app.css`](../ProcioneMGR/wwwroot/app.css)
(sezione *Breadcrumb contestuale*).

## Breadcrumb

`Breadcrumb.razor` (in `Components/Shared`) mostra `Home › Sezione › Pagina` derivandolo
dalla route corrente tramite `NavModel.Resolve(...)`. Sulla Home e sulle pagine non mappate
(es. area Account/Identity) non viene renderizzato nulla. È inserito in `MainLayout.razor`
in cima all'area contenuti.
