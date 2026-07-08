# Report — Supporto Multi-Coppia Concorrente nell'Ensemble/Trading (corsie isolate)

## Obiettivo originale (dal prompt)

Il prompt "PROMPT — Supporto Multi-Coppia Concorrente nell'Ensemble/Trading" chiedeva un
`MultiSymbolOrchestrator` che gestisse dinamicamente N entità `Ensemble` (con `Id` proprio,
attivabili/disattivabili singolarmente a runtime), col refactor di `EnsembleManager`/
`TradingEngine` da Singleton a Scoped, nuova UI in `/ensemble` e `/trading` per gestire
l'elenco dinamico, integrazione con `StrategyDecayMonitor` e `PipelineSchedulerWorker`.

## Perché l'implementazione è diversa dal prompt (deviazione deliberata, concordata)

Prima di scrivere codice ho verificato (non assunto) tre fatti sull'architettura reale:

1. `EnsembleConfiguration` è persistita come **un singolo blob JSON in una sola riga
   `EnsembleState`** (`db.EnsembleStates.OrderBy(e => e.Id).FirstOrDefault()`), non un'entità
   multi-riga con lookup per `Id` come assunto dal prompt.
2. `ITradingEngine` e `IEnsembleManager` sono registrati **`AddSingleton`** in `Program.cs`, e
   `TradingWorker`/`EnsembleRebalanceWorker` sono `BackgroundService` **globali, uno solo per
   tutta la piattaforma** — non esisteva alcun concetto di multi-tenancy.
3. Non esiste alcuna entità `Ensemble` con `Id` attivabile/disattivabile dinamicamente.

Il prompt assumeva quindi un'architettura che non esisteva, e la richiesta implicava un
refactor strutturale profondo (Singleton→Scoped, entità dinamiche) **mentre l'utente aveva una
sessione Paper reale attiva** (posizione aperta, 143 trade storici). Ho segnalato il
disallineamento e proposto due strade: seguire il prompt alla lettera (orchestratore dinamico,
rischio alto e cambiamento strutturale ampio) oppure **corsie fisse e in numero limitato**
(rischio basso, isolamento dati via colonna discriminante). L'utente ha scelto esplicitamente
la seconda ("Lane fisse e limitate — Recommended"), che governa tutte le decisioni seguenti.

## Architettura realizzata: corsie fisse (LaneId 0..2)

- **3 corsie fisse** (`LaneCount = 3` in `Program.cs`, `Ensemble.razor`, `Trading.razor`),
  invece di un numero dinamico di `Ensemble` con `Id`: un compromesso deliberato — copre lo
  scenario reale (2-3 coppie/timeframe in parallelo, come emerso dalle cacce multi-coppia) senza
  la complessità/il rischio di un registro dinamico a runtime.
- **Isolamento dati tramite colonna `LaneId`** su tutte le tabelle coinvolte (`TradingEngineStates`,
  `TradingAuditLogs`, `TradeRecords`, `Orders`, `OpenPositions`, `EnsembleStates`,
  `EnsembleRebalanceHistory`), non tramite `DbContext`/database separati: stesso database
  condiviso, stesso `ApplicationDbContext`, un discriminatore in più su ogni query. Scelto
  invece di un `DbContext` per corsia perché quest'ultimo avrebbe richiesto una history di
  migrazioni separata, la duplicazione dell'accesso a tabelle condivise (`ExchangeCredentials`)
  e un refactor molto più ampio — sproporzionato rispetto a "corsie fisse e limitate".
- **`EnsembleManager`/`TradingEngine` restano Singleton** (nessun refactor a Scoped): ogni
  corsia ha invece la propria **istanza dedicata**, registrata come **keyed singleton**
  (`AddKeyedSingleton<IEnsembleManager>(laneId, ...)` / `AddKeyedSingleton<ITradingEngine>(laneId, ...)`
  in `Program.cs`), con `laneId` passato come primo parametro del costruttore (entrambe le
  classi ora lo espongono anche come proprietà `LaneId` sull'interfaccia). `TradingWorker` ed
  `EnsembleRebalanceWorker` **non hanno richiesto alcuna modifica interna**: già dipendevano da
  `ITradingEngine`/`IEnsembleManager` iniettati, quindi bastava registrarne N istanze (una per
  corsia) tramite `AddSingleton<IHostedService>(sp => new TradingWorker(sp.GetRequiredKeyedService<ITradingEngine>(laneId), ...))`,
  ciascuna con le dipendenze keyed della propria corsia.
- **Corsia 0 = comportamento preesistente**: le righe esistenti ricevono `LaneId=0` via
  default di migrazione, diventando trasparentemente "Corsia 0" — zero discontinuità per la
  sessione Paper reale già in corso.
- **Fallback non-keyed su Corsia 0**: oltre alle registrazioni keyed, `Program.cs` registra
  anche `IEnsembleManager`/`ITradingEngine` "semplici" che risolvono sempre la corsia 0
  (`sp.GetRequiredKeyedService<IEnsembleManager>(0)`), così i consumer non ancora aggiornati con
  un selettore esplicito (`RegimeRetrainingWorker`, dashboard `Home.razor`, `Regimes.razor`,
  applicazione raccomandazioni in `Pipeline.razor`) continuano a funzionare **senza alcuna
  modifica**, con lo stesso comportamento di prima dell'introduzione delle corsie.

## File modificati/creati

- `Services/Trading/TradingEntities.cs`, `Services/Trading/TradingModels.cs`,
  `Data/EnsembleState.cs` — campo `LaneId` (int) aggiunto a `TradingEngineState`,
  `TradingAuditLog`, `OpenPosition`, `Order`, `TradeRecord`, `EnsembleState`,
  `EnsembleRebalanceHistory`.
- `Data/Migrations/20260704110229_AddTradingLaneSupport.cs` (nuova, applicata contro il DB
  reale) — `LaneId INTEGER NOT NULL DEFAULT 0` su tutte le tabelle sopra.
- `Data/ApplicationDbContext.cs` — indice composito `(StrategyId, ClosedAtUtc)` su
  `TradeRecord` (dal punto 2 di questa sessione, riusato dalla query per-lane del decay monitor).
- `Services/Ensemble/EnsembleManager.cs`, `Services/Ensemble/IEnsembleManager.cs` — `laneId`
  come primo parametro del costruttore, proprietà `LaneId`, ogni query/scrittura filtrata per
  `LaneId` (config, rebalance history, decay reports).
- `Services/Trading/TradingEngine.cs`, `Services/Trading/ITradingEngine.cs` — stesso schema:
  `laneId` nel costruttore, proprietà `LaneId`, ogni query/scrittura su
  `TradingEngineStates`/`OpenPositions`/`Orders`/`TradeRecords`/`TradingAuditLogs` filtrata per
  `LaneId`. **Fix di sicurezza incluso in questo refactor**: `StartAsync` in Paper ora cancella
  con `ExecuteDeleteAsync` **solo** le posizioni della propria corsia
  (`db.OpenPositions.Where(p => p.LaneId == laneId)`) — prima del refactor, un riavvio Paper su
  una corsia avrebbe azzerato le posizioni di TUTTE le corsie.
- `Program.cs` — rimosse le registrazioni Singleton semplici di `IEnsembleManager`/
  `ITradingEngine`/`TradingWorker`/`EnsembleRebalanceWorker`; aggiunto un loop
  `for (var lane = 0; lane < LaneCount; lane++)` con `AddKeyedSingleton` + hosted service per
  corsia, più il fallback non-keyed su corsia 0 (vedi sopra).
- `Components/Pages/Ensemble.razor`, `Components/Pages/Trading.razor` — selettore "Corsia"
  (dropdown 0/1/2) in cima alla pagina; `IEnsembleManager`/`ITradingEngine` non più iniettati
  direttamente ma risolti a runtime da `IServiceProvider.GetRequiredKeyedService<T>(_laneId)`
  dietro una proprietà calcolata (stesso nome identificatore usato in tutto il resto del file,
  quindi **nessuna altra riga cambiata** oltre all'iniezione e all'aggiunta del selettore).
- `ProcioneMGR.Tests/MultiLaneIsolationTests.cs` (nuovo) — 4 test di isolamento cross-lane.
- `ProcioneMGR.Tests/EnsembleManagerDecayTests.cs`, `ProcioneMGR.Tests/TradingEngineStopTests.cs`
  — adattati al nuovo parametro `laneId` (valore 0, nessun cambiamento di comportamento atteso).

## Decisioni architetturali

### 1. Corsie fisse invece di entità dinamiche

Vedi sezione "Perché l'implementazione è diversa dal prompt" sopra. Deviazione esplicitamente
autorizzata dall'utente dopo `AskUserQuestion`, non una scelta unilaterale.

### 2. Colonna discriminante `LaneId` invece di `DbContext`/DB separati

Isolamento "buono ma non assoluto": una query che dimentica il filtro `LaneId` potrebbe
leggere/scrivere dati di un'altra corsia (con `DbContext` separati questo sarebbe strutturalmente
impossibile). Mitigato con una revisione esaustiva a mano di ogni touchpoint DB in
`EnsembleManager`/`TradingEngine` (enumerato esplicitamente prima di modificare) e con 4 test di
isolamento cross-lane dedicati (vedi §Test) che avrebbero fallito su qualunque filtro dimenticato
— in particolare il test `StartAsync_Paper_OnlyWipesOwnLanePositions`, scritto apposta per
catturare la classe di bug più pericolosa (cancellazione cross-lane).

### 3. `EnsembleManager`/`TradingEngine` restano Singleton (keyed, non Scoped)

Il prompt suggeriva Scoped; ho scelto keyed Singleton perché più vicino al modello di vita
attuale (un'istanza logica per "cosa" invece che per richiesta HTTP/circuito Blazor) e perché
`TradingWorker`/`EnsembleRebalanceWorker` sono `BackgroundService` a vita di processo — un
`ITradingEngine` Scoped non avrebbe un "chi" chiaro a cui appartenere per un worker sempre
attivo. Keyed Singleton dà lo stesso risultato (N istanze indipendenti, isolate) senza toccare
il modello di lifetime né introdurre uno scope artificiale attorno ai worker.

### 4. Fallback non-keyed su Lane 0 per i consumer non aggiornati

`RegimeRetrainingWorker`, `Home.razor`, `Regimes.razor` e la sezione "applica raccomandazione"
di `Pipeline.razor` iniettano ancora `IEnsembleManager`/`ITradingEngine` "semplici". Il prompt
limitava esplicitamente la UI da aggiornare a `/ensemble` e `/trading`; estendere anche questi
altri 4 punti con un selettore di corsia avrebbe ampliato lo scope oltre quanto richiesto.
Ho invece registrato un fallback che risolve sempre la corsia 0 — identico al comportamento
preesistente, quindi zero rischio di regressione per questi consumer, ma **limite noto**:
oggi retraining del regime, dashboard e applicazione raccomandazioni pipeline "vedono" sempre e
solo la corsia 0, anche se l'utente opera attivamente su corsia 1/2. Segnalato in
§Prossimi passi.

### 5. Nessuna modifica a `TradingWorker`/`EnsembleRebalanceWorker`

Entrambi già dipendevano da `ITradingEngine`/`IEnsembleManager` iniettati e non hanno stato
statico condiviso (`_sessionStart`/`_cursor` sono campi di istanza) — verificato leggendo per
intero entrambi i file prima di escluderli dal refactor. Registrarne N istanze via factory
lambda in `Program.cs` è stato sufficiente; toccare la loro logica avrebbe violato il vincolo
esplicito del prompt di non alterare backtest/discovery/execution logic già funzionante.

## Test

- **4 nuovi test di isolamento cross-lane** (`MultiLaneIsolationTests`, DB SQLite reale su file
  temp condiviso tra due `TradingEngine`/`EnsembleManager` con `laneId` diversi):
  - `OpenPosition_OnOneLane_NotVisibleOnAnotherLane` — una posizione aperta su corsia 0 non
    compare in `GetOpenPositionsAsync()` di corsia 1.
  - `StartAsync_Paper_OnlyWipesOwnLanePositions` — riavvio Paper su corsia 0 non cancella le
    posizioni aperte di corsia 1 (regressione mirata sul fix di sicurezza del punto §Decisioni.2).
  - `TradeHistoryAndPerformance_AreIsolatedPerLane` — trade chiusi, `GetPerformanceAsync`,
    `GetOrderHistoryAsync` isolati per corsia.
  - `EnsembleManager_Configuration_IsIsolatedPerLane` — configurazioni (symbol, strategie)
    salvate/ricaricate in modo indipendente per corsia, senza mescolamenti.
- **Suite completa**: 464/464 verdi (460 preesistenti + 4 nuovi), 0 regressioni.
- **Build**: 0 errori, 0 warning nuovi.

## Verifica browser (contro il DB reale dell'utente, sessione Paper attiva)

- Build in Release-equivalente (`dotnet build`, Debug) e riavvio dell'app reale (non un DB di
  test): la migrazione `AddTradingLaneSupport` si applica da sola all'avvio
  (`DbInitializer.MigrateAsync`) senza errori.
- **Log di avvio confermano la sopravvivenza della sessione reale**: `TradingEngine: stato
  ripristinato dal DB (running=True, emergency=False, posizioni=1)` per la corsia 0 — la
  sessione Paper con posizione aperta e 143 trade storici dell'utente è intatta dopo il
  refactor. Corsia 1/2 correttamente `running=False, posizioni=0` (mai usate finora).
- `/trading`: selettore "Corsia" presente, default "Corsia 0 (default)"; con Corsia 0 selezionata
  mostra correttamente lo stato reale (RUNNING, Total PnL 56,27, 143 Trades, 1 posizione aperta
  NEAR/USDT Long); passando a "Corsia 1" la pagina mostra correttamente STOPPED, Capitale 0,00,
  0 Trades, "Nessuna posizione aperta" — isolamento confermato dal vivo, non solo nei test.
- `/ensemble`: selettore "Corsia" presente; con Corsia 0 selezionata mostra la configurazione
  reale dell'utente (NEAR/USDT, 4h, strategia MacdTrend con allocazione 21,6%) — nessuna
  regressione nella configurazione esistente dopo il refactor da Singleton a keyed Singleton.

## Prossimi passi consigliati

1. **Selettore di corsia anche in `Pipeline.razor`** (applicazione raccomandazioni): oggi
   applica sempre alla corsia 0 (fallback). Se l'utente inizia a usare corsia 1/2 attivamente,
   estendere anche questo punto con lo stesso pattern già usato in `/ensemble`/`/trading`.
   Fuori scope in questa fase perché il prompt limitava la UI a `/ensemble` e `/trading`.
2. **`RegimeRetrainingWorker` è single-corsia per costruzione** (il modello di regime è un
   singolo modello globale, non uno per corsia): se in futuro corsie diverse opereranno su
   coppie/timeframe molto diversi, un solo modello di regime addestrato sulla serie della
   corsia 0 potrebbe non essere rappresentativo per le altre. Richiederebbe un
   `IRegimeDetector` per corsia — investimento più grande, non necessario finché le corsie 1/2
   restano inattive.
3. **`LaneCount` è una costante fissa (3)** duplicata in tre punti (`Program.cs`,
   `Ensemble.razor`, `Trading.razor`). Se il numero di corsie dovesse cambiare, andrebbe
   sincronizzato manualmente nei tre punti — accettabile per un numero che cambia raramente,
   ma da tenere a mente.
