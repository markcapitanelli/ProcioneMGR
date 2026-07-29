# Protezioni di trading — `/admin/protections`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Admin/Protections.razor`](../../ProcioneMGR/Components/Pages/Admin/Protections.razor) |
| **Route** | `/admin/protections` |
| **Sezione navigazione** | Configurazione |
| **Accesso** | `[Authorize(Roles = Admin)]` — solo Admin |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Le soglie del pannello sicurezza di [`/trading`](trading.md) agiscono sul **singolo ordine di
una singola corsia**. Qui vivono i controlli **trasversali**, quelli che nessuna soglia
scalare può esprimere: il feed che accorcia i tempi di reazione delle uscite, il limite
sull'esposizione *correlata* fra corsie diverse, il router che decide in quale regime una
strategia può operare, il watchdog che manda in quarantena una corsia con la contabilità
impossibile.

La pagina nasce dall'audit backend↔frontend del **2026-07-29**: queste quattro protezioni
esistevano nel codice, giravano in produzione e si configuravano **solo** editando
`appsettings.json` a mano — in violazione della regola secondo cui ogni funzione backend
dev'essere controllabile dall'interfaccia.

## Le card

| # | Card | Sezione config | Cosa controlla |
|---|---|---|---|
| 1 | Feed di prezzo real-time | `MarketData:Realtime` | Interruttore del feed WebSocket ⟳, **`DriveProtectiveExits`** (i tick possono chiudere posizioni), refresh sottoscrizioni, soglia di staleness, spread massimo accettato, backoff di riconnessione |
| 2 | Sentinella d'ombra | `Trading:ProtectiveExitShadow` | Registrazione dei confronti "feed vs candela" sulle uscite protettive, soglia di allerta sul singolo evento (bps) |
| 3 | Esposizione correlata | `Trading:CorrelatedExposure` | Tetto sull'esposizione correlata netta fra corsie, |ρ| minima per contare, timeframe/finestra/sovrapposizione della stima, TTL della cache |
| 4 | Router di regime | `Trading:RegimeRouting` | Interruttore, **`DriveDecisions`**, politica per i regimi senza regola, candele minime, TTL controllo modello, **editor delle regole** (una riga per regime, con le strategie ammesse) |
| 5 | Watchdog invarianti | `Trading:LaneInvariants` | Interruttore, cadenza del check ⟳, tolleranza sul capitale, multipli di PnL ed esposizione oltre cui la corsia va in quarantena |

## Il disegno: osservare prima di decidere

Tre di queste protezioni hanno **due interruttori separati** — uno che accende la misura, uno
che le dà potere:

| Funzione | «Osserva» | «Decide» |
|---|---|---|
| Feed real-time | `Enabled` | `DriveProtectiveExits` |
| Router di regime | `Enabled` | `DriveDecisions` |

Non è ridondanza. Le regole di instradamento si scrivono guardando come rende una strategia in
ciascun regime, e quel dato è oggi sottile (sul modello BTC 1h la strategia in uso ha da 5 a 37
trade per regime): scriverci regole sopra sarebbe il curve-fitting che il resto della
piattaforma rifiuta. In osservazione si accumula il dato mancante senza rischiare nulla.

Nella UI l'interruttore «decide» è **disabilitato finché quello «osserva» è spento** — una
configurazione che dichiara un potere che nessuno esercita si attiverebbe di sorpresa il
giorno in cui qualcun altro accende l'interruttore principale.

Sul feed la pagina riporta anche il verdetto già misurato
([REPORT-B3-EXITLAG](../REPORT-B3-EXITLAG-2026-07-28.md)): uscire al tocco è risultato
**peggio** che a barra chiusa in 24 configurazioni su 24. Accendere `DriveProtectiveExits` è
una scelta da rimisurare sui propri dati, non un miglioramento gratuito.

## Come funziona (flusso del codice)

Stesso patto di [`/admin/autonomy`](admin-autonomy.md): copie **locali** delle opzioni (clone
via JSON round-trip da `IOptionsMonitor<T>.CurrentValue`), scrittura solo al Salva via
`IAppConfigWriter.SaveSectionAsync`, badge ✅/⟳ per campo.

Due specificità:

- **TimeSpan in unità intere.** `CorrelatedExposureOptions.CacheTtl` e
  `RegimeRoutingOptions.ModelCheckTtl` si editano in ore/minuti e vengono ricomposti al
  salvataggio: un campo `"00:06:00"` da digitare a mano è un invito all'errore di battitura.
- **Editor delle regole di regime.** Una riga per regime con checkbox per strategia (dai
  `Prototypes` di `IStrategyFactory`). Una lista vuota è etichettata *«corsia ferma in questo
  regime»*: è una decisione, non una riga incompleta da sistemare.

### Validazione lato server

Ogni Salva passa da
[`AdminConfigRules.Validate`](../../ProcioneMGR/Services/Config/AdminConfigRules.cs) prima di
toccare il file. L'attributo `min=` di un `<input type="number">` vincola la validazione di un
*form*, non il binding di Blazor: con `@bind` il valore digitato arriva al modello comunque.
Le regole più utili sono quelle **relazionali**, che un `min=` non potrebbe nemmeno esprimere
— per esempio una finestra di stima più corta delle barre sovrapposte richieste, con cui
nessuna correlazione sarebbe mai stimabile e il guard, che fallisce verso il permesso,
lascerebbe passare tutto sembrando acceso.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IAppConfigWriter` | Scrittura sezioni di appsettings.json | [`Services/Config/AppConfigWriter.cs`](../../ProcioneMGR/Services/Config/AppConfigWriter.cs) |
| `AdminConfigRules` | Validazione lato server | [`Services/Config/AdminConfigRules.cs`](../../ProcioneMGR/Services/Config/AdminConfigRules.cs) |
| `RealtimeFeedOptions` → `RealtimePriceWorker` / `WebSocketPriceFeed` | Il feed governato dalla card 1 | [`Services/MarketData/`](../../ProcioneMGR/Services/MarketData) |
| `ProtectiveExitShadowOptions` → `ProtectiveExitShadowRecorder` | La sentinella della card 2 | [`Services/Trading/ProtectiveExitShadow.cs`](../../ProcioneMGR/Services/Trading/ProtectiveExitShadow.cs) |
| `CorrelatedExposureOptions` → `CorrelatedExposureGuard` | Il limite della card 3 | [`Services/Risk/CorrelatedExposureGuard.cs`](../../ProcioneMGR/Services/Risk/CorrelatedExposureGuard.cs) |
| `RegimeRoutingOptions` → `LaneRegimeRouter` | Il router della card 4 | [`Services/Regime/LaneRegimeRouter.cs`](../../ProcioneMGR/Services/Regime/LaneRegimeRouter.cs) |
| `LaneInvariantOptions` → `LaneInvariantWatchdog` | Il watchdog della card 5 | [`Services/Trading/LaneInvariantWatchdog.cs`](../../ProcioneMGR/Services/Trading/LaneInvariantWatchdog.cs) |
| `IStrategyFactory` | I nomi di strategia per l'editor delle regole | [`Services/Backtesting/StrategyFactory.cs`](../../ProcioneMGR/Services/Backtesting/StrategyFactory.cs) |

Il **binding** di `RealtimeFeedOptions` e `ProtectiveExitShadowOptions` è incondizionato in
`AddTradingLanes`, la loro **esecuzione** no: col trading remoto il monolite non ospita il feed
ma ne ospita ancora il pannello, e un pannello che legge i default invece del file mostrerebbe
uno stato che non è quello vero.

## Dati letti / scritti

- **Legge**: `appsettings.json` (via `IOptionsMonitor`), i prototipi di strategia.
- **Scrive**: `appsettings.json` — sezioni `MarketData:Realtime`,
  `Trading:ProtectiveExitShadow`, `Trading:CorrelatedExposure`, `Trading:RegimeRouting`,
  `Trading:LaneInvariants`.

## Collegamenti con le altre pagine

- [Trading](trading.md) — dove le protezioni agiscono, dove si legge la diagnostica delle
  uscite protettive e dove si rimuove una quarantena.
- [Autonomia](admin-autonomy.md) — gli automatismi che *decidono cosa provare*; qui stanno i
  limiti che decidono *cosa può passare*.
- [Regimes](regimes.md) — gli id di regime che l'editor delle regole referenzia.

## Note di design

- I default sono deliberati e asimmetrici: il watchdog nasce **acceso** (è un freno di
  sicurezza, spegnerlo è la scelta da motivare), feed e router nascono **spenti** (prima di
  dare a un modello il potere di chiudere una posizione, quel potere va guadagnato in
  validazione).
- Il guard delle correlazioni **fallisce verso il permesso**: se la correlazione non è
  stimabile lascia passare e lo registra. Bloccare al buio fermerebbe l'operatività per un
  buco di dati, che è un guasto peggiore del rischio che si evita.
