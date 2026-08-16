# Watchlist — `/market/watchlist`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Watchlist.razor`](../../ProcioneMGR/Components/Pages/Watchlist.razor) + [`Services/Ingestion/WatchlistPageService.cs`](../../ProcioneMGR/Services/Ingestion/WatchlistPageService.cs) |
| **Route** | `/market/watchlist` |
| **Sezione navigazione** | Dati & Monitoraggio |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` · **polling 60 s** (solo freschezza/corsie/timbro, mai i conteggi) |

## A cosa serve

È il punto dove si dichiara **quali serie di mercato la piattaforma deve scaricare e tenere
aggiornate da sola**. Ogni riga della watchlist è una tripla *(Exchange, Symbol, Timeframe)*:
una volta aggiunta, un worker in background la sincronizza periodicamente senza intervento
manuale. Tutti i dati usati da backtest, ottimizzazioni, ML e analisi provengono da qui (o
dai fetch una tantum della [Dashboard](dashboard.md)).

Dal 2026-08-15 la pagina risponde anche alla domanda che nell'incidente delle 122 serie ferme
non aveva risposta: **«il sync sta girando ADESSO?»** (report:
[REPORT-SYNC-WORKER-STALL](../REPORT-SYNC-WORKER-STALL-2026-08-15.md)).

## Struttura della pagina

| Blocco | Contenuto |
|---|---|
| GuidaPanel | Spiegazione di sync automatico/exchange/symbol/timeframe/stato/verifica exchange/azioni |
| **Riquadro «Sync automatico»** | [2026-08-15] Il TIMBRO del ciclo: 🟢 `vivo` (ultimo giro HH:mm:ss, intervallo), 🔴 `FERMO` (ultimo giro + età, «le serie invecchiano tutte insieme»), 🟡 `MAI VISTO UN GIRO`. `(stimato)` se il timbro manca e l'ora viene dal MAX di `LastSyncUtc` |
| Form "Aggiungi serie" | Select exchange (enum `ExchangeName`), input symbol, select timeframe (`Timeframes.Supported`: 1m/5m/15m/30m/1h/4h/1d) |
| Alert di stato | Esito dell'ultima azione (verde/rosso) |
| **Banner divergenza corsie** | [2026-08-13] Rosso, sopra la tabella: corsie in esecuzione che operano una serie disabilitata qui |
| **Banner ferme CON diagnosi** | [2026-08-15] Se il sync è FERMO: «l'imputato è il sync, NON disabilitare le serie, si drena da solo». Se il sync è vivo: «probabile sospensione» + bottone **Verifica su exchange** |
| Tabella "Serie tracciate" | Filtro «solo ferme», badge stato exchange accanto al symbol (es. `BREAK`), conteggio candele (arriva DOPO il primo paint, «…» nel frattempo), ultima candela / ritardo con badge `aggiornata` / `FERMA` / **`in recupero`** / `nessuna candela`, ultima sync, azioni per riga |

### Questo interruttore NON ferma il trading (2026-08-13)

`Enabled` governa **l'aggiornamento delle candele**, non le corsie. Il feed real-time si instrada
dalle **corsie in esecuzione** (`TradingEngineStates`) e ignora del tutto questa pagina.
L'equivoco è costato una mattinata di notifiche su STX/USDT (report:
[REPORT-FEED-STALENESS-STX](../REPORT-FEED-STALENESS-STX-2026-08-13.md)). La pagina lo dice in due
punti: un **banner rosso** quando la divergenza esiste già, e un **avviso al momento del toggle**.
La decisione resta umana.

### Il timbro del ciclo di sync (2026-08-15)

`MarketDataSyncWorker` scrive a fine di ogni ciclo una riga in `HostHeartbeats` con ruolo
`ingestion-sync` (LastUtc + esito + **cadenza**: l'intervallo viaggia col timbro perché il pod e
il guscio hanno `appsettings` indipendenti). La regola unica che la giudica è
[`SyncPulse`](../../ProcioneMGR/Services/Ingestion/SyncPulse.cs): fermo = timbro assente o più
vecchio di 3× intervallo + 2 min; **spento** (`MarketData:Enabled=false`) è uno stato a sé, non un
guasto. La stessa regola compone la riga di diagnosi della notifica della guardia di freschezza
(«è il SYNC fermo» vs «probabile BREAK» vs «è spento»). Separatamente, `/health/live` del pod
ingestion giudica il **battito del loop** in-process
([`IngestionSyncHeartbeat`](../../ProcioneMGR/Services/Ingestion/IngestionSyncHeartbeat.cs)): loop
muto oltre max(30 min, 6× intervallo) ⇒ 503 ⇒ la liveness K8s riavvia il pod da sola. La readiness
resta su `/health` (sempre 200): un worker parcheggiato non deve togliere il traffico al
`POST /sync` manuale, che è la via di rimedio.

### La colonna di freschezza (E7, 2026-07-31 · rivista 2026-08-15)

La freschezza si giudica **contro adesso** con la regola unica di
[`SeriesFreshness`](../../ProcioneMGR/Services/Ingestion/SeriesFreshness.cs) (B2.a): badge rosso
`FERMA · N barre indietro` oltre la tolleranza (3 barre), serie vuota = «nessuna candela» rosso se
abilitata. Novità 2026-08-15: il badge giallo **`in recupero`** quando la serie è oltre tolleranza
ma l'ultima candela È avanzata dall'osservazione precedente e il sync è vivo — il drenaggio
post-blocco non è un guasto in corso, e leggerlo come tale ha quasi fatto disabilitare serie sane.

In parallelo il `SeriesFreshnessWatchWorker` (guscio, ogni 15 minuti) **notifica la transizione** a
ferma — una volta per serie, aggregata in un messaggio solo, ora **con la riga di diagnosi** di
`SyncPulse.DescribeCause`. Nessuna azione automatica: disabilitare resta una scelta umana (un
BREAK può essere temporaneo, decisione B2.a).

### Verifica su exchange (2026-08-15)

Il bottone nel banner delle ferme chiama `IExchangeClient.GetSymbolStatusesAsync` (una chiamata
pubblica per exchange copre l'intero listino: Binance `exchangeInfo`, Bitget `public/symbols`) e
mette il badge dello stato accanto al symbol (es. `BREAK`). Per una serie sospesa, **Disabilita**
scrive da solo l'annotazione `disabilitata YYYY-MM-DD — Exchange riporta stato X` in
`LastSyncStatus` — lo stesso formato che per MKR/TON era stato scritto a mano il 2026-07-28.

## Come funziona (flusso del codice)

Tutta l'orchestrazione sta in `WatchlistPageService` (Scoped, come TradingPageService — la pagina
tiene solo rendering, PollingTimer e stato di UI):

- **`LoadAsync`** — serie ordinate, ultima candela per serie con **MAX per-serie sull'indice**
  (mai la `GROUP BY` sull'intera `OhlcvData`: era un seq scan da 15 s misurati su 12,6M righe,
  pagato a ogni apertura), corsie in esecuzione, timbro del sync. **Niente conteggi qui.**
- **`LoadCountsAsync`** — i conteggi per serie, fuori dal percorso critico (la colonna mostra «…»
  finché non arrivano; l'auto-refresh non riconta mai).
- **`RefreshFreshnessAsync`** — il tick leggero del polling (60 s): MAX per serie, corsie, timbro,
  flag di recupero (confronto con l'osservazione precedente tenuta nel service).
- **`CheckExchangeStatusesAsync`** — stati simboli via `IExchangeClientFactory`, ritorna quante
  ferme risultano sospese.
- **`AddAsync` / `ToggleAsync` / `SyncNowAsync` / `DeleteAsync`** — le azioni di sempre
  (`SymbolCatalog.Invalidate()` dopo ogni mutazione [E-04]; Toggle con avviso corsie e annotazione
  BREAK).

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `WatchlistPageService` | Orchestrazione della pagina | [`Services/Ingestion/WatchlistPageService.cs`](../../ProcioneMGR/Services/Ingestion/WatchlistPageService.cs) |
| `SyncPulse` | Regola pura «il sync sta girando?» + diagnosi | [`Services/Ingestion/SyncPulse.cs`](../../ProcioneMGR/Services/Ingestion/SyncPulse.cs) |
| `SeriesFreshness` | Regola pura di freschezza per serie | [`Services/Ingestion/SeriesFreshness.cs`](../../ProcioneMGR/Services/Ingestion/SeriesFreshness.cs) |
| `IMarketDataSyncService` | Sync on-demand di una serie | [`Services/Ingestion/MarketDataSyncService.cs`](../../ProcioneMGR/Services/Ingestion/MarketDataSyncService.cs) |
| `MarketDataSyncWorker` (indiretto) | Ciclo periodico + timbro `ingestion-sync` + battito `/health` | [`Services/Ingestion/MarketDataSyncWorker.cs`](../../ProcioneMGR/Services/Ingestion/MarketDataSyncWorker.cs) |
| `IExchangeClientFactory` → `GetSymbolStatusesAsync` | Stato simboli (TRADING/BREAK/…) | [`Services/Exchanges/IExchangeClient.cs`](../../ProcioneMGR/Services/Exchanges/IExchangeClient.cs) |
| `PollingTimer` | Auto-refresh 60 s a prova di eccezione | [`Components/Shared/PollingTimer.cs`](../../ProcioneMGR/Components/Shared/PollingTimer.cs) |

## Dati letti / scritti

- **Legge**: `TrackedSeries`, `OhlcvData` (MAX e Count per serie sull'indice),
  `TradingEngineStates` (corsie), `HostHeartbeats` (timbro `ingestion-sync`).
- **Scrive**: `TrackedSeries` (insert/update/delete, incl. annotazione BREAK); indirettamente
  `OhlcvData` via sync.

## Collegamenti con le altre pagine

- [Dashboard](dashboard.md) — fetch esplorativo una tantum; la Watchlist è il tracking stabile.
- [Trading](trading.md) — le corsie che il banner di divergenza confronta con questa pagina.
- Tutte le pagine di analisi (Backtest, Optimization, ML, …) dipendono dai dati scaricati da qui.

## Test

`WatchlistPageServiceTests` (integrazione Postgres): verdetti, timbro vivo/fermo/stimato,
recupero, verifica BREAK + annotazione, avviso corsia al toggle. `SyncPulseTests` (puri):
soglie e diagnosi. `SeriesFreshnessWatchWorkerTests`: notifiche con diagnosi.
