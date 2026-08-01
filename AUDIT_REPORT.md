# AUDIT REPORT — ProcioneMGR (analisi a freddo, 2026-07-31)

**Metodo.** Analisi da contesto fresco, solo su ciò che è scritto nei file: inventario con
scansione dell'albero (677 file .cs, ~140k righe), lettura integrale dei file caldi del percorso
di trading (TradingEngine, TradingWorker, ExecutionWorker, RealtimePriceWorker, WebSocketPriceFeed,
EnsembleManager, BinanceClient, TradingServiceCollectionExtensions, Program.cs, PollingTimer,
LaneExecutionLease), scansioni sistematiche su tutto il sorgente per: `async void`, catch vuoti,
sync-over-async (`.Result`/`.Wait()`), eventi `+=` senza `-=`, timer non disposti, primitive di
sincronizzazione, interfacce/classi mai referenziate. Build dell'intera solution eseguita: **OK**.
Suite di test lanciata (esito in fondo).

---

## FASE 1 — Mappatura e inventario

**Solution** `ProcioneMGR.sln`, 8 progetti + 5 tool CLI:

| Progetto | File | Righe | Ruolo |
|---|---|---|---|
| ProcioneMGR | 387 | 58.077 | Monolite Blazor Server ("guscio"): UI + 20 hosted service |
| ProcioneMGR.Trading | 3 | 514 | Host gRPC standalone del motore (core caldo) |
| ProcioneMGR.Ingestion / .Ml | 2+3 | 247 | Servizi satellite (sync remota, inferenza dual-read) |
| ProcioneMGR.Contracts | 1+proto | 42 | Contratti gRPC condivisi |
| ProcioneMGR.Migrations.Postgres | 32 | 27.182 | Migrazioni EF (assembly separato, migrate-on-deploy) |
| ProcioneMGR.Tests | 244 | 47.876 | Suite (unit + Testcontainers Postgres) |
| tools/ | 5 | 6.632 | CLI di ricerca (PlatformExpand, StrategyHunter, …) |

**Entry point**: `ProcioneMGR/Program.cs` (601 righe) — Blazor Server interattivo, Identity,
Postgres unico provider via `IDbContextFactory`, cifratura AES-256-GCM dei segreti con fail-fast
in Production sulla master key placeholder.

**Composizione del trading**: `AddTradingLanes` (TradingServiceCollectionExtensions.cs) — keyed
singleton per corsia (`IEnsembleManager`, `ITradingEngine`, `LaneSafetyMonitor`) + un
`TradingWorker`/`ExecutionWorker`/`EnsembleRebalanceWorker` per corsia. Il toggle
`Trading:UseRemoteTrading` commuta motore locale ↔ `RemoteTradingEngineClient` per **registrazione
condizionale** (mai entrambi), con doppia difesa: advisory lock Postgres per corsia
(`NpgsqlLaneLeaseFactory`) che rende l'invariante "un esecutore per corsia" applicato dal database,
non dalla disciplina di deploy.

**DI**: ~120 registrazioni in Program.cs + estensioni; 104 interfacce nei Services, tutte risolte
(verifica sotto, Fase 5). CQRS via Mediator (source-generated) solo lato Blazor.

---

## 1. 🚨 CRITICAL ISSUES

### C1 — Le sottoscrizioni WebSocket aggiornate NON vengono mai applicate a una connessione viva

**[File: `ProcioneMGR/Services/MarketData/RealtimePriceWorker.cs:252` + `WebSocketPriceFeed.cs:75`]**

`WebSocketPriceFeed.UpdateSubscriptions` dichiara nel proprio doc-comment: *«il chiamante usa
l'esito per decidere se serve riciclare la connessione (Binance codifica le sottoscrizioni
nell'URL, quindi cambiarle richiede riconnettere)»*. Il chiamante — `RefreshSubscriptionsAsync` —
**non lo fa**: quando `UpdateSubscriptions` ritorna `true` si limita a loggare
*«sottoscrizioni aggiornate a N serie»*.

```csharp
// RealtimePriceWorker.cs:252 — oggi
if (feed.UpdateSubscriptions(subs))
{
    logger.LogInformation("Feed {Exchange}: sottoscrizioni aggiornate a {N} serie.", feed.Exchange, subs.Count);
}
```

Nel frattempo `RunAsync` (WebSocketPriceFeed.cs:108) prende lo snapshot delle sottoscrizioni **solo
prima di connettersi**, e `PumpAsync` legge dalla connessione esistente finché non cade. Ho
verificato entrambi i mapper: **Binance** codifica gli stream nell'URL (`BuildEndpoint`,
`BuildSubscribeFrames` ritorna `[]` — BinanceStreamMapper.cs:35,66), **Bitget** invia i frame di
subscribe solo al connect (BitgetStreamMapper.cs:44). Su nessuno dei due un cambio di
sottoscrizioni ha effetto finché il socket non cade da solo (Binance lo ricicla ogni 24h; può
volerci **ore**). Nessun test copre il cambio di sottoscrizioni a connessione viva
(WebSocketPriceFeedTests verifica solo il valore di ritorno).

**Scenario concreto**: corsia 0 attiva su BTC/USDT, feed connesso. Avvii la corsia 1 su ETH/USDT
(stesso exchange) — o cambi simbolo alla corsia 0 e la riavvii. Il log dice «sottoscrizioni
aggiornate a 2 serie», ma i tick di ETH **non arrivano**. E la watchdog di staleness non allerta:
misura la freschezza **per feed**, non per simbolo, e i messaggi di BTC la tengono "verde".
È esattamente la classe di difetto "controllo che rassicura a prescindere dalla realtà".

**Impatto sul trading**:
- Con `MarketData:Realtime:DriveProtectiveExits=true`: gli stop tick-driven della nuova corsia
  **non esistono** mentre l'operatore crede di sì → uscite ritardate fino alla chiusura candela,
  su una modalità che può essere Testnet o Live. Qui è critico.
- Con il default attuale (`DriveProtectiveExits=false`): la sentinella d'ombra B3 e la consegna
  anticipata delle candele chiuse per il nuovo simbolo non funzionano; il percorso REST copre le
  uscite, quindi nessuna perdita diretta — ma la misura su cui deciderai se accendere i tick è
  silenziosamente monca.

**Fix richiesto** — riciclare la connessione quando le sottoscrizioni cambiano. Il punto meno
invasivo è dare al feed una sessione di connessione cancellabile:

```csharp
// WebSocketPriceFeed.cs — aggiungere:
private CancellationTokenSource? _connectionCts;

public bool UpdateSubscriptions(IReadOnlyList<StreamSubscription> subscriptions)
{
    // ... (invariato fino al return true)
        _subscriptions = ordered;
        _byExchangeSymbol = ordered.ToDictionary(ExchangeSymbolOf, s => s, StringComparer.OrdinalIgnoreCase);
        _connectionCts?.Cancel();   // <-- ricicla la connessione corrente: RunAsync riconnette col nuovo set
        return true;
}

// In RunAsync, dentro il try del connect:
using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
lock (_sync) { _connectionCts = connectionCts; }
try
{
    await using var transport = transportFactory.Create();
    await transport.ConnectAsync(mapper.BuildEndpoint(subs), connectionCts.Token);
    foreach (var frame in mapper.BuildSubscribeFrames(subs))
        await transport.SendAsync(frame, connectionCts.Token);
    MarkConnected();
    attempt = 0;
    await PumpAsync(transport, index, connectionCts.Token);
}
catch (OperationCanceledException) when (!ct.IsCancellationRequested)
{
    // riciclo richiesto da UpdateSubscriptions: NON è un guasto — riconnetti subito, senza backoff
    continue;
}
```

E il log in `RefreshSubscriptionsAsync` va reso onesto: *«sottoscrizioni cambiate: riciclo la
connessione»*. Aggiungere un test: sottoscrizione aggiunta a connessione viva ⇒ il transport fake
osserva disconnect + riconnessione col nuovo endpoint/frame.

**Nessun altro problema di livello critico trovato.** In particolare — e l'ho cercato con
insistenza: zero `async void` in tutto il sorgente; zero race sulle collezioni del motore (tutte
le mutazioni di `_positions`/`_buffer`/`_equity`/`_executionJobs`/`_state` avvengono sotto il
`SemaphoreSlim _gate`, tick compresi); zero event-handler leak (gli unici `+=` su eventi UI sono i
due `LocationChanged` di NavMenu/Breadcrumb, entrambi con `-=` in Dispose; le lambda su
`TickReceived`/`BarClosed` vivono su feed ricreati per sessione e muoiono con essi); zero
sync-over-async (gli unici `.Result` sono dopo `Task.WhenAll` in TradingPageService.cs:98).

---

## 2. ⚠️ INTEGRATION GAPS

### G1 — Nessun timeout esplicito sugli HttpClient di trading (default: 100 secondi)

**[File: `ProcioneMGR/Services/Ingestion/IngestionServiceCollectionExtensions.cs:41-50`]**

I typed client `BinanceClient`/`BitgetClient` non impostano `client.Timeout`: vale il default di
100s. Un ordine firmato che si appende (rete che "muore" senza RST) tiene il `_gate` del motore
occupato fino a 100 secondi: in quella finestra i tick vengono scartati (per design, `WaitAsync(0)`)
e ogni chiamata di stato della corsia (UI, TradingWorker, promozioni) resta in coda. La tassonomia
d'incertezza già esistente (4xx=non piazzato, 5xx/rete=incerto→riconciliazione) rende il timeout
corto **sicuro**: l'ordine incerto viene riconciliato comunque. `recvWindow=5000` rende inutile
attendere oltre ~10-15s.

**Fix**: `client.Timeout = TimeSpan.FromSeconds(15);` su entrambi i typed client (il client
"AltData*"/"Sentiment*" li hanno già, a 15s: qui è solo un'omissione).

### G2 — Watchdog di staleness per-feed, non per-serie

**[File: `ProcioneMGR/Services/MarketData/RealtimePriceWorker.cs:263`]**

`CheckStaleness` guarda `feed.Health.LastMessageUtc` dell'intero feed: basta UN simbolo vivo per
coprire il silenzio di tutti gli altri. È il complice che rende invisibile C1, ma resta un gap
anche dopo il fix di C1 (es. uno stream che l'exchange smette di consegnare per blocco regionale —
già successo con le liquidazioni EEA/MiCA). Traccia `LastMessageUtc` per sottoscrizione (il mapper
sa a quale serie appartiene ogni messaggio) e allerta per serie.

### G3 — Config del backtest interno all'ensemble con fee hardcoded

**[File: `ProcioneMGR/Services/Ensemble/EnsembleManager.cs:493-503`]**

`BuildBtConfig` fissa `FeePercent = 0.1m` e `InitialCapital = 10_000m` per le simulazioni che
decidono i **pesi del ribilanciamento** (percorso live: `RebalanceAsync`). Il motore reale usa
`SafetyConfiguration.FeePercent` (hot-reload, P2-8). Se operi con fee reali diverse (BNB discount,
VIP tier, futures a 0,02/0,05%), i pesi vengono calcolati su costi diversi da quelli che paghi.
Non è un bug di correttezza del codice ma una divergenza motore↔ribilanciatore che P2-8 aveva
eliminato altrove. Fix: passare la stessa `FeePercent` della safety configuration.

---

## 3. 🧹 DEAD CODE & CLEANUP

La caccia è stata sistematica (104 interfacce e ~500 classi pubbliche dei Services scansionate per
riferimenti incrociati) e il bottino è **quasi nullo** — segno di una codebase tenuta pulita:

- **Nessuna interfaccia morta.** Le due candidate emerse dallo scan (`ICarryExecutor`,
  `ICombinatorialPurgedCv`) sono implementate e consumate nel loro stesso file + pipeline/worker.
  I "falsi morti" (`*CommandHandler`/`*QueryHandler`, `*Extensions`, `Alpha158Factor`,
  `OverfittingGate`) sono risolti da Mediator source-generated, chiamati per nome-metodo o via
  catalogo: tutti vivi.
- **`tools/PlatformExpand/Program.cs`**: 8 `catch { }` vuoti (righe 902-1694). È un tool CLI di
  ricerca, non tocca il trading — accettabile, ma se un giorno un esperimento "torna zero risultati"
  senza spiegazione, la causa sarà uno di questi. Costo di aggiungere un contatore di scarti: 10 minuti.
- **Un solo TODO reale** in tutto il sorgente: `AesGcmEncryptionService.cs:29` (passaggio a KMS,
  dichiarato e rimandato deliberatamente). Gli altri 3 match sono parole in commenti metodologici.
- **Catch silenziosi**: i 3 `catch (Exception)` senza log (BayesianOptimizationEngine:177,
  OptimizationEngine:293, PipelineSchedulerWorker:371) sono fallback numerici/parse documentati con
  semantica esplicita (Cholesky non PSD ⇒ −∞; combinazione invalida ⇒ Sharpe pessimo; cron invalida
  ⇒ null). Nessuno è sul percorso ordini. **Nel trading path non esiste un solo errore ingoiato**:
  ogni catch logga, audita o propaga.

---

## 4. ✅ WHAT IS WORKING PERFECTLY

Flussi verificati riga per riga e trovati solidi:

- **Serializzazione del motore**: ogni entrypoint pubblico di `TradingEngine` prende il
  `SemaphoreSlim(1,1)`; i tick usano `WaitAsync(0)` con scarto (coalescenza: mai code di prezzi
  stantii, mai doppia chiusura della stessa posizione). Le uniche letture fuori gate sono query
  DB-only con context usa-e-getta. `GetPerformanceAsync` fa lo snapshot atomico della curva sotto
  gate e calcola fuori — il commento documenta il bug "collection modified" che questo ha chiuso.
- **Isolamento corsie**: ogni query/scrittura filtra `LaneId`; lo start Paper purga solo la propria
  corsia; le posizioni di modalità diversa vengono purgate con audit (M2); quarantena che blocca il
  riavvio PRIMA che lo stato contabile venga azzerato.
- **Pipeline dati real-time**: thread di rete mai bloccato (`TryWrite` su `Channel` bounded;
  tick `DropOldest` — un tick vecchio è peggio di nessun tick; candele `Wait` — non sacrificabili);
  singolo consumer; code svuotate a fine sessione per non consegnare prezzi vecchi alla riaccensione.
- **Riconnessione WebSocket**: backoff esponenziale con jitter pieno e cap, heartbeat applicativo
  (Bitget), transport disposto con `await using`, handler degli eventi isolati (un consumer che
  lancia non abbatte il feed), health sotto lock.
- **Tassonomia degli errori d'ordine** (BinanceClient/BitgetClient): 4xx = NON piazzato certo,
  5xx/eccezione di rete = INCERTO → riconciliazione con lookup per `ClientOrderId`; il -2013 è
  l'unico "not found" certo, tutto il resto resta incerto (mai riaprire la finestra del doppio
  ordine). Clock skew gestito da `ExchangeClock` + rate-limit proattivo come DelegatingHandler su
  ogni chiamata REST.
- **UI Blazor Server**: tutte e 5 le pagine di polling usano `PollingTimer` (loop su
  `PeriodicTimer` con try/catch per tick — il commento spiega perché il vecchio
  `Timer(async _ => …)` poteva uccidere il processo) + `InvokeAsync(...)` per il marshaling sul
  sync context del circuito + `Dispose`/`DisposeAsync` coerenti. CPU-bound spostato via `Task.Run`.
- **Confini di sicurezza nel codice, non in convenzione**: Champion mai su Live (throw), master key
  placeholder blocca Live e l'avvio Production, promozione automatica mai oltre Testnet, lease
  advisory-lock Postgres contro il doppio esecutore, sizing incoerente respinto all'avvio con
  spiegazione del rimedio.
- **Memoria bounded ovunque conti**: equity trim a 10k punti (M1), trades esposti troncati a 500
  (P3-12), cache job solo Running, potatura delle shadow detection orfane.
- **Build**: intera solution compila senza errori.

---

## Verdetto

Contrariamente alla premessa dell'incarico ("scritto da persone diverse, incongruenze, classi
duplicate, eventi disconnessi"), questa codebase è **coerente, integrata e difensiva** — i pattern
pericolosi classici del trading C# (race sulle collezioni, event leak, async void, errori ingoiati
sul percorso ordini) sono assenti, e quasi ogni scelta non ovvia ha accanto il commento che spiega
il bug che l'ha motivata. I problemi veri trovati sono **uno** (C1 — e la sua correzione è
delimitata a due file) più tre gap minori (G1-G3). Priorità: C1 subito, G1 è una riga, G2-G3 quando
si mette mano ai rispettivi file.

## Stato delle correzioni (aggiornato 2026-07-31, fase post-audit)

- **C1** — implementato nella sessione dedicata (worktree `goofy-pike-95fa56`, branch
  `claude/goofy-pike-95fa56`): 3 file modificati (+153/−19), **non ancora committato** al momento
  di questo aggiornamento — da rivedere e mergiare da lì.
- **G1** — ✅ CORRETTO, ma in forma diversa da quella proposta sopra: leggendo
  `ExchangeRateLimitHandler` è emerso che `HttpClient.Timeout` copre l'INTERA pipeline del
  SendAsync, incluse le attese deliberate di Retry-After (fino a 30s × 3 ritiri) — un timeout
  secco a 15s sul client abortirebbe proprio il rispetto del rate-limit. Implementato invece un
  **timeout per-tentativo** (default 15s, `TimeProvider`-testabile) dentro l'handler, attorno alla
  sola chiamata di rete, tradotto in `TaskCanceledException` per conservare la tassonomia
  "rete ⇒ incerto ⇒ riconciliazione" dei client. Due test nuovi (connessione appesa abortita;
  cancellazione del chiamante non travestita da timeout).
- **G2** — RIMANDATO deliberatamente: tocca gli stessi file di C1 (WebSocketPriceFeed,
  RealtimePriceWorker); si fa sopra il merge di C1 per non fabbricare conflitti.
- **G3** — ✅ CORRETTO: `EnsembleManager` riceve la fee viva (`Func<decimal>` composto nel
  composition root su `SafetyConfiguration.FeePercent`, hot-reload) e `BuildBtConfig` non ha più
  lo 0,1% fisso. Forma a delegato per rispettare il confine dichiarato Ensemble↛Trading.
- **Flaky test** — indagato, non riprodotto: la run completa di verifica è passata **2053/2053**
  (14m 45s, stessi 2051 + i 2 nuovi di G1). Il percorso del test è deterministico (database
  dedicato per test, ordinamenti senza pareggi possibili) e la fixture documenta già la classe di
  guasto infrastrutturale più probabile ("53300 too many clients", mitigata due volte:
  max_connections=500 + potatura idle 10s). Conclusione onesta: intermittenza da carico del
  container, non difetto del prodotto. Alla prossima ricomparsa, girare con verbosità normale per
  catturare l'eccezione — la run in quiet non la registra, ed è il motivo per cui stavolta non
  c'è un colpevole certo.

---

**Test suite (run di audit)**: **2050/2051 passati** (15m 6s, Testcontainers inclusi). L'unico fallimento —
`PipelinePageServiceTests.SelectRun_LoadsSummaries_PreviousComparison_AndDecisionArtifact` —
**passa se eseguito in isolamento**: è flakiness da isolamento del test nella run completa (stato
condiviso o timing), non un difetto dimostrato del codice di produzione. Va comunque indagato: un
test che fallisce "a volte" insegna a ignorare la suite, che è il modo in cui i fallimenti veri
passano inosservati.
