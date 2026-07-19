# Trading — `/trading`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Trading.razor`](../../ProcioneMGR/Components/Pages/Trading.razor) (~650 righe) |
| **Route** | `/trading` |
| **Sezione navigazione** | Trading |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]`; configurazione di sicurezza e rimozione quarantena solo Admin |
| **Render mode** | `InteractiveServer`, implementa `IDisposable` (polling 2s) |

## A cosa serve

È il **control center dell'operatività reale**: qui le strategie girano davvero (non è una
simulazione) — il motore valuta ogni nuova candela e apre/chiude posizioni. Tre modalità a
rischio crescente:

| Modalità | Cosa fa | Rete di sicurezza |
|---|---|---|
| **Paper** | Denaro finto, nessun ordine reale | Riparte da zero a ogni avvio |
| **Testnet** | Ordini reali su ambiente di prova (firma HMAC) con fondi finti dell'exchange | Serve credenziale testnet in [Credenziali Exchange](settings-exchanges.md); stato persistente tra riavvii |
| **Live** | Soldi veri | Checkbox di conferma per l'avvio + **ogni ordine automatico resta in coda finché non lo approvi manualmente** (`RequireManualConfirmationForLive`) |

Come in [Ensemble](ensemble.md), tutto è **per corsia** (3 corsie isolate). Spot/Futures e
leva **si impostano in Ensemble**, non qui: questa pagina opera con la configurazione già
salvata (badge FUTURES Nx / SPOT nell'header).

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| Header + corsia | 15–42 | Badge modalità/mercato/running, selettore corsia |
| Promozioni | 44–127 | Valutazione per corsia: metriche **dall'avvio della corsia** (Sharpe, trade, MaxDD, win rate, giorni di osservazione), stato (Pronta per Testnet / retrocessione consigliata), bottone "Promuovi a Testnet". Il bottone "Promuovi a Live" è **sempre disabilitato**: Live solo dai controlli manuali |
| GuidaPanel | 129–181 | Spiegazione completa di modalità, emergency stop, conferme Live, SL/TP/trailing, safety |
| Banner critici | 183–225 | In ordine: **dati stantii** (servizio trading non risponde: ciò che vedi è l'ultimo stato noto), **emergency stop attivo**, **master key incoerente** (credenziali non decifrabili → avvii Testnet/Live falliranno), **corsia in quarantena** (watchdog invarianti: trading fermato senza chiudere posizioni; rimozione solo Admin dopo verifica) |
| Controllo | 227–284 | Radio Paper/Testnet/Live, Avvia/Ferma, EMERGENCY STOP con doppia conferma, checkbox "Confermo" per Live |
| KPI | 288–301 | Capitale totale/disponibile/usato (o margine), leva, PnL, MaxDD, trades, win rate |
| Conferme in attesa (Live) | 303–328 | Ordini proposti dal worker in attesa di Conferma/Rifiuto uno per uno |
| Posizioni aperte | 330–375 | Entry/current/qty/PnL; in Futures leva e **Liq. Price** colorato per vicinanza (<5% rosso, <15% giallo); editor SL/TP/Trailing per posizione con "Set SL/TP" e "Close" |
| Equity curve | 377–383 | `OhlcvChart` solo indicatori |
| Ordini recenti | 385–406 | Storico con stato (Filled/Rejected/Pending) e motivo d'errore |
| Configurazione sicurezza | 408–481 | Solo Admin: soglie salvate in appsettings.json e applicate a caldo |

### Le soglie di sicurezza (righe 421–474)
Max size per posizione, size per apertura (sui Futures il nozionale è size × leva), max
esposizione totale, max perdita giornaliera, **max drawdown → emergency stop automatico**,
max posizioni aperte, intervallo minimo tra ordini, **leva massima consentita** (l'avvio è
rifiutato se l'Ensemble chiede di più), margine di mantenimento stimato (per il prezzo di
liquidazione quando l'exchange non lo riporta), fee, e il flag di conferma manuale Live.

## Come funziona (flusso del codice)

### Architettura (commento righe 512–516)
I metodi della pagina sono **wrapper sottili**: l'orchestrazione (chiamate al motore,
gestione stato, validazione) vive in `TradingPageService`, testabile senza Blazor. In pagina
resta solo ciò che è intrinsecamente di UI: corsia/utente correnti e `StateHasChanged`.

### Polling e stato
`PollingTimer` da **2 secondi** su `Service.RefreshAsync(_laneId)`: status, posizioni,
ordini, pending, equity. Se il servizio di trading non risponde, il service espone
`StaleSince`/`LastStaleReason` e la pagina mostra il banner "dati non aggiornati" — i dati
restano visibili ma dichiarati stantii.

### Avvio/arresto/emergenza
- `StartAsync` → `Service.StartAsync(lane, mode)`: in Live è gated dal checkbox; il motore
  applica i failsafe (SafetyChecker, master key, leva massima).
- `EmergencyAsync` → chiude **tutte** le posizioni e blocca nuovi ordini; la UI richiede
  doppia conferma (bottone pulsante → "SÌ, FERMA TUTTO").
- Quarantena: quando il watchdog degli invarianti contabili (`LaneInvariantWatchdog`) rileva
  un'incoerenza, ferma la corsia **senza chiudere le posizioni** (preserva l'evidenza);
  il riavvio è bloccato finché un Admin non rimuove la quarantena dopo verifica.

### Conferme Live
In Live ogni apertura proposta dal `TradingWorker` resta `Pending` finché l'utente non la
conferma (`ConfirmOrderCommand`) o rifiuta (`RejectOrderCommand`) — pattern CQRS via
Mediator. L'identità dell'utente confermante è tracciata (`_userId`).

### SL/TP/Trailing per posizione
I valori vengono precompilati automaticamente all'apertura se la gamba ha stop validati in
Ensemble (`AutoStopApplier`); qui si possono modificare a mano (`SetStopLossTakeProfitCommand`)
— l'ultima modifica manuale ha la precedenza. Il trailing segue il prezzo a favore e si
blocca a quella distanza.

### Promozioni (righe 44–127)
`PromotionEvaluator` valuta ogni corsia sulle metriche **dall'avvio** (il tooltip avverte
che possono differire dai KPI a finestra 90gg). La promozione Paper→Testnet può avvenire
qui manualmente o dal `PromotionWorker` in automatico; **Testnet→Live mai in automatico**,
per costruzione.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `TradingPageService` | Orchestrazione completa della pagina | [`Services/Trading/TradingPageService.cs`](../../ProcioneMGR/Services/Trading/TradingPageService.cs) |
| `ITradingEngine` / `TradingEngine` | Il motore: posizioni, ordini, safety, corsie | [`Services/Trading/TradingEngine.cs`](../../ProcioneMGR/Services/Trading/TradingEngine.cs) |
| Commands/Queries (Mediator) | StartLane, StopLane, EmergencyStop, ConfirmOrder, RejectOrder, SetStopLossTakeProfit, ClosePosition; GetLaneStatus, GetOpenPositions, GetOrderHistory, GetPendingOrders, GetPerformance | [`Services/Trading/Commands/`](../../ProcioneMGR/Services/Trading/Commands) · [`Queries/`](../../ProcioneMGR/Services/Trading/Queries) |
| `SafetyChecker` + `SafetyConfiguration` | Le soglie applicate a ogni ordine (fail-safe anche con capitale ≤ 0) | [`Services/Trading/SafetyChecker.cs`](../../ProcioneMGR/Services/Trading/SafetyChecker.cs) |
| `SafetyConfigWriter` | Persistenza a caldo delle soglie in appsettings.json | [`Services/Trading/SafetyConfigWriter.cs`](../../ProcioneMGR/Services/Trading/SafetyConfigWriter.cs) |
| `PromotionEvaluator` / `LanePromoter` / `PromotionWorker` | Valutazione e promozione corsie (mai auto-Live) | [`Services/Trading/PromotionEvaluator.cs`](../../ProcioneMGR/Services/Trading/PromotionEvaluator.cs) |
| `LaneInvariantWatchdog` / `LaneQuarantineStore` | Invarianti contabili e quarantena | [`Services/Trading/LaneInvariantWatchdog.cs`](../../ProcioneMGR/Services/Trading/LaneInvariantWatchdog.cs) |
| `IMasterKeyProbe` | Diagnosi credenziali non decifrabili (banner master key) | [`Services/Security/MasterKeyProbe.cs`](../../ProcioneMGR/Services/Security/MasterKeyProbe.cs) |
| `TradingWorker` (indiretto) | Il loop che valuta le candele e propone ordini | [`Services/Trading/TradingWorker.cs`](../../ProcioneMGR/Services/Trading/TradingWorker.cs) |
| `RemoteTradingEngineClient` (indiretto) | Variante remota dietro `Trading:UseRemoteTrading` (microservizio gRPC) | [`Services/Trading/RemoteTradingEngineClient.cs`](../../ProcioneMGR/Services/Trading/RemoteTradingEngineClient.cs) |

## Dati letti / scritti

- **Legge**: stato motore per corsia, posizioni, ordini, pending, equity, quarantena, safety config.
- **Scrive**: comandi al motore (avvio/stop/emergenza/conferme/SL-TP/chiusure),
  `appsettings.json` (safety, solo Admin), audit di conferma/rifiuto con utente.

## Collegamenti con le altre pagine

- [Ensemble](ensemble.md) — definisce COSA gira (strategie, Spot/Futures, leva, stop per gamba).
- [Credenziali Exchange](settings-exchanges.md) — necessarie per Testnet/Live.
- [Execution Lab](execution.md) — l'esecuzione a fette usata dalle gambe configurate.
- [Dashboard](dashboard.md) / [Home](home.md) — mostrano promozioni e stato del motore.

## Note di design

- La gerarchia dei freni è a 5 livelli, tutti visibili in pagina: safety per-ordine →
  conferma manuale Live → emergency stop (manuale o da MaxDD) → quarantena watchdog →
  divieto strutturale di auto-Live.
- Il polling a 2s con banner di stantietà è la risposta al problema "UI che sembra viva ma
  mostra dati vecchi": lo stato è sempre dichiarato.
- I fill anomali dal testnet vengono scartati dal `FillSanityCheck` (bug B1 del 2026-07,
  audit `FillSanityRejected`): la pagina mostra gli ordini rifiutati con il motivo.
