# Trading — `/trading`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Trading.razor`](../../ProcioneMGR/Components/Pages/Trading.razor) (~1.790 righe) |
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

*Aggiornata il 2026-08-17 dalla revisione completa della sezione. Le righe sono un'indicazione:
il riferimento stabile è il `card-header` di ogni blocco.*

| Blocco | Riga ~ | Contenuto |
|---|---|---|
| Header + battito + corsia | 18–74 | Badge modalità/mercato/running, **battito di valutazione** (E6), avviso «candela non ancora chiusa», `<LaneSelector>` con tutte le corsie visibili |
| GuidaPanel | 76–128 | Spiegazione completa di modalità, emergency stop, conferme Live, SL/TP/trailing, safety |
| Banner critici | 130–183 | In ordine: **dati non aggiornati** (distingue «il motore remoto non risponde» da «l'ultima lettura è fallita» — vedi `StaleIsTransport`), **emergency stop attivo**, **master key incoerente**, **corsia in quarantena**, esito dell'ultimo comando |
| Controllo | 185–266 | Radio Paper/Testnet/Live, Avvia/Ferma, EMERGENCY STOP con doppia conferma (il prompt **nomina la corsia**), checkbox "Confermo" per Live, «Svuota corsia» con conferma |
| Carta d'identità della corsia | 275–324 | Cosa gira, con che attese di holdout, da dove viene (provenienza dal journal della flotta) |
| KPI del test corrente | 337–379 | Equity ora, capitale iniziale, cassa, impegnato/margine, leva, **PnL realizzato % (test)**, MaxDD %, operazioni chiuse, win |
| Promozioni | 381–535 | Valutazione per corsia con metriche **dall'avvio**; «→ Testnet» e «→ Paper» manuali. Verso Live **non esiste alcun pulsante**, per costruzione |
| Conferme in attesa (Live) | 537–592 | Ordini proposti dal worker, **conferma a due passi legata all'OrderId** («SÌ, INVIA» nomina lato, quantità e prezzo) |
| Protezioni toccate ma non eseguite | 594–623 | Sentinella su barre chiuse; se il controllo **non ha potuto girare** lo dichiara invece di tacere |
| Posizioni aperte | 624–684 | Entry/current/qty/PnL con **prezzi a precisione adattiva**; in Futures leva e Liq. Price colorato; editor SL/TP/Trailing con "Set SL/TP" e "Close", **disabilitati finché i dati non sono certamente di questa corsia** |
| Prezzo e operazioni | 686–692 | `OhlcvChart` con marker di entrata/uscita e linee entry/SL/TP |
| Equity curve | 694–700 | `OhlcvChart` solo indicatori |
| Ordini | 702–860 | Finestra del test o storico completo raggruppato per **episodio**; stato e motivo d'errore |
| Configurazione sicurezza | 862–1035 | Solo Admin: soglie lette e scritte **sul motore** via `IEngineConfigStore` (E1). Se il motore non risponde il pannello lo dice **e il Salva è bloccato**; se la corsia ha un **profilo di rischio**, un avviso dice quali otto campi il profilo sovrascrive |
| Posizioni orfane | 1037–1088 | Posizioni su corsie che non esistono più; chiusura manuale solo Admin |
| Ritardo delle uscite protettive | 1090–1247 | Misura B3 e confronti registrati dal vivo |

### Il battito di valutazione (header, E6 2026-07-31)

Accanto al badge RUNNING c'è il **battito**: l'apertura dell'ultima candela valutata dal motore in
questo avvio, giudicata contro adesso con la regola unica di `SeriesFreshness`. RUNNING è un flag
d'intento; il battito è la prova dell'attività — una corsia running con l'ultima candela di ieri ha
stop e trailing che non valuta nessuno. Rosso = stantio (se il numero non scende ai refresh, è un
digiuno, non la rincorsa del replay); giallo = nessuna candela ancora da questo avvio. Il
`LaneInvariantWatchdog` fa lo stesso controllo lato motore e allerta (una volta per transizione,
senza quarantena: la corsia non è corrotta, è a digiuno).

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
ordini, pending, equity. Il giro legge in variabili locali e **pubblica in blocco**: una risposta
sorpassata (cambio corsia, o semplicemente il tick successivo) viene scartata intera, mai a metà.

Se una qualunque lettura fallisce, il service espone `StaleSince`/`LastStaleReason` e la pagina
mostra il banner "dati non aggiornati" — i dati restano visibili ma dichiarati stantii.
`StaleIsTransport` distingue **il motore remoto che non risponde** (RpcException) da **la lettura
fallita qui** (database, errore locale): accusare il gRPC quando il gRPC non c'entra manderebbe a
cercare il guasto dalla parte sbagliata, e in topologia in-process *nessun* guasto è una
RpcException — prima di questa distinzione, lì il banner non poteva comparire mai.

`LoadedLaneId` è il **tag di provenienza** dei dati esposti. Al cambio corsia lo stato viene
azzerato: se la lettura della corsia nuova fallisce, «nessun dato per questa corsia» è l'unica cosa
vera da mostrare — tenere i numeri della precedente sotto l'etichetta della nuova non è degradare,
è mentire. A corsia invariata invece i dati restano (svuotare durante un riavvio di pochi secondi
sarebbe peggio).

Verso il motore remoto ogni rpc ha una **deadline**: `Trading:RemoteReadTimeoutSeconds` (10s) per le
letture, `Trading:RemoteCommandTimeoutSeconds` (60s) per i comandi, entrambe amministrabili da
[Autonomia](admin-autonomy.md). Senza, una chiamata appesa congelava il polling *senza* far comparire
il banner: non c'era nemmeno il fallimento da dichiarare.

### Avvio/arresto/emergenza
- `StartAsync` → `Service.StartAsync(lane, mode)`: in Live è gated dal checkbox; il motore
  applica i failsafe (SafetyChecker, master key, leva massima).
- `EmergencyAsync` → chiede la chiusura di **tutte** le posizioni e blocca nuovi ordini; la UI
  richiede doppia conferma, e il prompt **nomina la corsia**. Il messaggio di esito non promette
  «tutte le posizioni chiuse»: la chiusura di massa è best-effort per contratto, quindi dice cosa
  guardare invece di dichiarare un risultato che potrebbe essere falso.
- **Ogni comando** (Ferma, Emergency, Chiudi, Conferma, Rifiuta, Set SL/TP, rimuovi quarantena) è
  avvolto in try/catch e riporta il fallimento come messaggio. In Blazor Server un'eccezione non
  gestita in un handler `@onclick` abbatte il circuito, e nell'app non esiste alcun `ErrorBoundary`:
  senza questo, col motore remoto in riavvio la pagina moriva proprio mentre si premeva il pulsante
  rosso.
- **Nessuna conferma armata attraversa il cambio di corsia** (emergency, svuotamento, conferma
  ordine Live): resterebbe armata su una corsia diversa da quella per cui era stata data.
- Quarantena: quando il watchdog degli invarianti contabili (`LaneInvariantWatchdog`) rileva
  un'incoerenza, ferma la corsia **senza chiudere le posizioni** (preserva l'evidenza);
  il riavvio è bloccato finché un Admin non rimuove la quarantena dopo verifica.

### Conferme Live
In Live ogni apertura proposta dal `TradingWorker` resta `Pending` finché l'utente non la
conferma (`ConfirmOrderCommand`) o rifiuta (`RejectOrderCommand`) — pattern CQRS via
Mediator. L'identità dell'utente confermante è tracciata (`_userId`).

La conferma è a **due passi legati all'OrderId**: la coda si riordina da sola ogni 2 secondi (i più
recenti in cima), quindi un ordine nuovo proposto dal worker faceva scorrere le righe sotto il
cursore e il clic partiva su un ordine mai letto. Il secondo passo nomina lato, quantità e prezzo.

Alla conferma il motore **riallinea il prezzo di riferimento dell'ordine** al prezzo corrente prima
di rivalidarlo: una proposta invecchiata in coda veniva altrimenti pesata dal `SafetyChecker` sul
prezzo di quando il segnale era nato, e su un rally i tetti che dipendono dal nozionale
(`MaxPositionSizePercent`, `MaxTotalExposurePercent`) risultavano rispettati mentre l'esecuzione al
mercato li superava. Il prezzo della proposta resta nell'audit.

### SL/TP/Trailing per posizione
I valori vengono precompilati automaticamente all'apertura se la gamba ha stop validati in
Ensemble (`AutoStopApplier`); qui si possono modificare a mano (`SetStopLossTakeProfitCommand`)
— l'ultima modifica manuale ha la precedenza. Il trailing segue il prezzo a favore e si
blocca a quella distanza, e il suo **cricchetto è persistito**: prima un riavvio del processo
riportava lo stop effettivo al livello di apertura, buttando via in silenzio tutto il profitto già
bloccato.

L'input è **fail-closed**: «campo svuotato» (rimozione voluta), «valore valido» e «valore non
valido» sono tre casi distinti. Prima collassavano tutti in `null`, che il motore interpreta come
AZZERAMENTO: un `-59800` digitato per errore *rimuoveva* lo stop loss, con un messaggio verde
«aggiornati» a confermarlo. Ora un input non valido non invia nulla e lo dice; una rimozione voluta
viene dichiarata come tale; e uno stop dalla parte sbagliata rispetto al prezzo d'ingresso viene
rifiutato.

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
| `IEngineConfigStore` | [E1] Lettura/scrittura delle soglie **sul processo che le applica** (in-process o gRPC), con rilettura dopo la scrittura e validazione `AdminConfigRules` lato server | [`Services/Trading/EngineConfigStore.cs`](../../ProcioneMGR/Services/Trading/EngineConfigStore.cs) |
| `PromotionEvaluator` / `LanePromoter` / `PromotionWorker` | Valutazione e promozione corsie (mai auto-Live) | [`Services/Trading/PromotionEvaluator.cs`](../../ProcioneMGR/Services/Trading/PromotionEvaluator.cs) |
| `LaneInvariantWatchdog` / `LaneQuarantineStore` | Invarianti contabili e quarantena | [`Services/Trading/LaneInvariantWatchdog.cs`](../../ProcioneMGR/Services/Trading/LaneInvariantWatchdog.cs) |
| `IMasterKeyProbe` | Diagnosi credenziali non decifrabili (banner master key) | [`Services/Security/MasterKeyProbe.cs`](../../ProcioneMGR/Services/Security/MasterKeyProbe.cs) |
| `TradingWorker` (indiretto) | Il loop che valuta le candele e propone ordini | [`Services/Trading/TradingWorker.cs`](../../ProcioneMGR/Services/Trading/TradingWorker.cs) |
| `RemoteTradingEngineClient` (indiretto) | Variante remota dietro `Trading:UseRemoteTrading` (microservizio gRPC) | [`Services/Trading/RemoteTradingEngineClient.cs`](../../ProcioneMGR/Services/Trading/RemoteTradingEngineClient.cs) |

## Dati letti / scritti

- **Legge**: stato motore per corsia (incluso il battito di valutazione), posizioni, ordini,
  pending, equity, quarantena, safety config **dal motore**.
- **Scrive**: comandi al motore (avvio/stop/emergenza/conferme/SL-TP/chiusure), soglie di
  sicurezza **sul motore** via `IEngineConfigStore` (solo Admin, con rilettura), audit di
  conferma/rifiuto con utente.

## [2026-07-25] Corsie configurabili e selettore a schede

Il numero di corsie non è più fisso a tre: si configura con `Trading:LaneCount` (default 3, massimo
12) e richiede un riavvio, perché le corsie sono registrate nel contenitore DI all'avvio. Le corsie
in più nascono vuote e ferme, e si configurano da [Ensemble](ensemble.md) come tutte le altre.

Al posto della tendina c'è ora `<LaneSelector>`: ogni corsia è una **scheda cliccabile** con id,
simbolo, modalità e un puntino verde quando sta operando — così si sa cosa gira dove senza entrare
in ogni corsia. Oltre sei corsie le altre si raccolgono sotto `+N`, ma chi resta a vista lo decide
l'utilità e non l'id: prima chi opera, poi chi è configurato, infine le vuote; e la corsia
selezionata è sempre visibile.

## Collegamenti con le altre pagine

- [Ensemble](ensemble.md) — definisce COSA gira (strategie, Spot/Futures, leva, stop per gamba).
- [Credenziali Exchange](settings-exchanges.md) — necessarie per Testnet/Live.
- [Execution Lab](execution.md) — l'esecuzione a fette usata dalle gambe configurate.
- [Dashboard](dashboard.md) / [Home](home.md) — mostrano promozioni e stato del motore.

## Le soglie di sicurezza sono GLOBALI, il profilo di corsia le sovrascrive

Il pannello legge e scrive la sezione unica `Trading:Safety`. Ma da R3 le soglie **effettive** di
una corsia con un profilo di rischio sono `profilo.Apply(globale)`, e il profilo sovrascrive otto
degli undici campi mostrati (size per apertura, max size, esposizione totale, perdita giornaliera,
drawdown, posizioni aperte, intervallo minimo, leva massima). La pagina è per-corsia dappertutto
tranne qui: dal 2026-08-17 un avviso lo dichiara quando la corsia visualizzata ha un profilo.

Restano globali — e quindi validi ovunque — commissione, margine di mantenimento, bande di
plausibilità dei fill, stop resting, conferma manuale in Live e il **dosaggio sulla volatilità**
(che `RiskProfile.Apply` dimenticava di ricopiare: la manopola risultava accesa nel pannello e
inerte in corsia).

Il **Salva è bloccato** finché non c'è stata una lettura riuscita dal motore: con la lettura fallita
il form contiene i default del codice, la scrittura sostituisce l'intera sezione, e bastava
ritoccare la commissione per allargare in silenzio tutti gli altri limiti.

## Note di design

- La gerarchia dei freni è a 5 livelli, tutti visibili in pagina: safety per-ordine →
  conferma manuale Live → emergency stop (manuale o da MaxDD) → quarantena watchdog →
  divieto strutturale di auto-Live.
- Il polling a 2s con banner di stantietà è la risposta al problema "UI che sembra viva ma
  mostra dati vecchi": lo stato è sempre dichiarato.
- I fill anomali dal testnet vengono scartati dal `FillSanityCheck` (bug B1 del 2026-07,
  audit `FillSanityRejected`): la pagina mostra gli ordini rifiutati con il motivo.

## [2026-08-17] Revisione completa della sezione

Otto revisori indipendenti sul codice e sulla UI, ogni scoperta passata da un revisore
avversariale incaricato di refutarla. Le correzioni sono descritte sopra, ai rispettivi paragrafi.
Le tre di maggior peso, tutte fuori dalla pagina:

- **Contabilità Futures**: alla chiusura la fee d'ingresso veniva sottratta due volte (era già
  uscita dalla cassa all'apertura). L'errore era cumulativo — una fee per ogni giro — e contaminava
  la curva equity, quindi Sharpe e MaxDrawdown, cioè i numeri su cui il `PromotionEvaluator`
  decide. Il ramo Spot era corretto; il riferimento è `BacktestEngine.Close`.
- **Riconciliazione Futures**: i client rispondevano `null` sia per «sei flat» sia per «non sono
  riuscito a leggere» (4xx, 5xx, rate limit, timeout — le eccezioni di rete sono tradotte in
  risultati, non rilanciate). Un 429 faceva quindi chiudere la posizione *localmente* senza inviare
  alcun ordine reduce-only: esposizione reale abbandonata sull'exchange. Ora la lettura restituisce
  un `FuturesPositionRead` che distingue i due casi, e il difetto ha il test che il fake precedente
  non poteva esprimere (simulava il guasto con un'eccezione, cioè l'unico comportamento che i client
  veri non hanno).
- **Replay dopo un riavvio**: la guardia anti-replay guardava solo il buffer in memoria, vuoto dopo
  un riavvio, mentre lo stato veniva restaurato. Il feed ripartiva da trenta giorni prima e
  rigiocava candele vecchie contro posizioni vive. Il segnalibro di sessione è ora persistito
  (`TradingEngineState.LastCandleUtc`), e il worker alimenta la serie **della sessione** invece di
  quella della configurazione viva — che poteva essere riscritta sotto una corsia in esecuzione,
  consegnando le candele di un altro strumento a un motore con posizioni aperte.
