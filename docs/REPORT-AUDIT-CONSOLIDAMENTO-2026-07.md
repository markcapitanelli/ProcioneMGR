# Report di Audit Architetturale — Consolidamento Totale (2026-07-17)

Audit condotto file-per-file sull'intero repository (401 file C#, ~35.000 righe nel monolite + ~22.000 di test), con tracciamento dei caller per ogni modulo sospetto. Build verificata: **0 errori, 0 warning**. Metodo: sweep sistematici (stub/TODO/eccezioni/async/RNG) + lettura integrale dei moduli quant critici + analisi dei caller a livello di metodo (i soli nomi di tipo non bastano: `var` li nasconde).

---

## 1. Executive Summary

**Voto: 8/10.**

Il sospetto che ha motivato questa missione — "moduli integrati solo per figurare, logica stubata" — **non trova riscontro**. Ogni modulo quant dichiarato esiste, contiene matematica reale e verificata contro le fonti (Bailey–López de Prado, MacKinnon 2010, Ledoit-Wolf 2004, Maillard-Roncalli-Teiletche), ed è **cablato nel flusso decisionale reale** (pipeline o UI), non appeso nel vuoto. Nel codice di produzione non esiste un solo `NotImplementedException`, un solo valore quant hardcoded, un solo `return default` mascherato. Esiste **un solo TODO** in tutto il repository (gestione della master key, già presidiato da fail-fast). La disciplina di determinismo è rara da vedere: ogni RNG del percorso ricerca/training è seedato.

I problemi veri sono di **forma strutturale**, non di sostanza:

1. **`TradingEngine` è una God class conclamata** (2.211 righe, ~40 metodi, 16 dipendenze): il file più critico della piattaforma è anche il più difficile da testare in isolamento.
2. **Le 6 pagine Razor maggiori (782–998 righe)** mischiano markup, orchestrazione e stato: la UI è il punto dove il progetto "sfila" di più.
3. **Pattern fragile di polling** (`System.Threading.Timer` + lambda async) replicato in 5 pagine: un'eccezione non catturata in quel percorso è `async void` → può abbattere il processo.
4. **Rami secchi residui**: 1 classe morta registrata in DI, 1 membro d'interfaccia mai chiamato, 2 metodi senza caller, 1 commento ingannevole ("client trigger ancora stub" — non è più vero).
5. **Resilienza infrastrutturale**: manca `EnableRetryOnFailure` su Npgsql; HttpClient catturati a vita in un singleton; nessuna autorizzazione applicativa sul gRPC di trading (confine solo di rete, documentato).

Nessuno di questi punti mette a rischio fondi **oggi** (i failsafe anti-Live reggono su 5 livelli indipendenti, verificati nel codice e nei test). Sono debito da estinguere ora che lo sviluppo feature è fermo.

---

## 2. Caccia alle Stub e Codice Incompleto

Esito: **nessuna integrazione fake**. I `NotImplementedException` esistono solo nei fake dei test (corretto). Quello che segue è l'elenco completo di ciò che si avvicina di più a "incompleto":

| File/Percorso | Elemento | Problema |
|---|---|---|
| `ProcioneMGR/Services/Trading/TradingEngine.cs:1118` | Commento in `ExecuteFuturesOpenAsync` | **Commento obsoleto e ingannevole**: dice "con i client trigger ancora stub registra un warning". Falso: `PlaceFuturesTriggerOrderAsync` è implementato per davvero sia in `BinanceClient.cs:459` sia in `BitgetClient.cs:522` (firma, invio, gestione errori). Un commento che dichiara stub un percorso reale è più pericoloso di uno stub: chi legge disattiva mentalmente un ramo che invece opera. |
| `ProcioneMGR/Services/Security/AesGcmEncryptionService.cs:20` | `TODO(produzione)` | L'**unico TODO del repository**: la master key vive in appsettings/env invece che in un secret store. NON è uno stub — è presidiato da due guardie reali (fail-fast a startup Production in `Program.cs:395`, blocco Live in `TradingEngine.StartAsync:147`) — ma resta un item aperto dichiarato. |
| `ProcioneMGR/Services/Sentiment/KeywordSentimentScorer` (reg. `Program.cs:221`) | `ISentimentScorer` | **Semplificazione dichiarata, non fake**: scoring lessicale a keyword come fallback "in attesa del provider LLM" (commento in Program.cs). Funziona ed è testato, ma è il modulo con il rapporto più alto tra nome ambizioso e sofisticazione reale. Non alimenta direttamente decisioni di trading (analisi/UI), quindi accettabile — purché nessuno lo scambi per NLP vero. |
| `ProcioneMGR.Trading/TradingCommandServiceImpl.cs:13-17` | Servizio gRPC comandi trading | **Assenza dichiarata di autorizzazione applicativa**: `ConfirmOrder`/`StartLane` possono muovere denaro vero e l'unico confine è la NetworkPolicy K8s. Il commento è onesto ("Non esporre questo servizio altrove"), ma un confine di sicurezza fatto di sola topologia di rete è un guasto di configurazione via dal disastro. |

Menzione d'onore (il contrario di uno stub): i valori "placeholder" trovati dal grep sono tutti **controlli di sicurezza sul placeholder della master key**, non logica fittizia.

---

## 3. Codice Morto e Rami Secchi

Verificato caller-per-caller (inclusi i `.razor`, che l'analisi testuale sui soli `.cs` manca). I moduli che sembravano orfani a livello di tipo (OverfittingGate, CPCV, GARCH, MonteCarlo, LeverageAdvisor, Kelly, gli analyzer Trombetta/McAllen) **sono tutti vivi** — consumati via `var` dalla pipeline o dalle pagine. Il morto vero è questo:

1. **`ProcioneMGR/Services/TimeSeries/PairsSpreadAnalyzer.cs` — CLASSE MORTA.** Registrata come singleton in `Program.cs:193` ma **mai risolta da nessuno**. Era lo screening full-sample di Fase C; superata da `RollingPairsSpreadAnalyzer` (che non ne dipende — la cita solo in un commento) e dall'uso diretto di `ICointegrationTest` in `AnalysisStages`. Rimuovere classe + registrazione. (`OlsRegression`, che condivide, resta vivo: lo usano Engle-Granger e il rolling.)
2. **`IRegimeDetector.PredictRegimeAsync`** (`IRegimeDetector.cs:15`, impl. `RegimeDetector.cs:134`) — **membro d'interfaccia mai invocato** nel flusso reale. `RegimeAugmentation.cs:18` documenta esplicitamente che NON va usato ("nearest-centroid grezzo, senza smoothing"). Effetto collaterale concreto: ogni fake nei test è costretto a implementarlo con `throw new NotImplementedException()` (es. `EnsembleManagerDecayTests.cs:38`). Da potare dall'interfaccia.
3. **`ExcursionAnalyzer.ComputeBarAnatomy`** (`ExcursionAnalyzer.cs:279`) **+ record `BarAnatomy`** (`:437`) — zero caller, nemmeno nei test.
4. **`BarBuilder.ToOhlcv`** (`BarBuilder.cs:69`) — zero caller (Volume/Dollar bars sono vivi via `InformationBars.razor`; la ri-conversione a OHLCV non la usa nessuno).
5. **API surface più larga del necessario**: `ExcursionAnalyzer.SuggestHorizonBracket` (`:139`) è `public` ma ha un solo caller, interno (`SuggestAdaptiveBracket:169`). Candidato a `private`/`internal` — non morto, ma superficie da restringere.

Nota storica positiva: il codice porta le cicatrici giuste — `SafetyChecker.cs:15` documenta che l'interfaccia istanza `ISafetyChecker` è già stata rimossa in passato per lo stesso motivo. La bonifica è una pratica, non un evento.

---

## 4. I "Punti di Dolore" (Pain Points)

### 4.1 `TradingEngine` — God class sul percorso del denaro
- **Il Problema:** 2.211 righe, ~40 metodi, 16 parametri di costruttore. Un solo tipo gestisce: lifecycle, processamento candele, valutazione segnali, apertura/chiusura Spot, apertura/chiusura Futures, bracket resting, esecuzione a fette (TWAP/VWAP), riconciliazione ordini incerti e posizioni remote, conferme manuali, persistenza (stato/ordini/posizioni/trade/audit), reporting performance, caricamento credenziali.
- **Perché è pericoloso:** è il file col blast radius più alto della piattaforma e insieme quello meno testabile in isolamento: ogni test del percorso Futures paga il costo dell'intero motore. Ogni modifica futura (nuovo exchange, nuovo tipo ordine) tocca il file che non deve rompersi mai. La qualità interna è alta (commenti eccellenti, gate coerente) — è la *forma* a essere sbagliata, non il contenuto.
- **Soluzione Proposta:** decomposizione conservativa in 3 mosse, **senza cambiare comportamento né schema** (vedi Roadmap P1-1): (a) estrarre la persistenza in un `TradingStateStore` per-lane (i 7 metodi `Persist*/Save*/Update*/Remove*/Audit*`); (b) estrarre `SpotExecutionPath` e `FuturesExecutionPath` (classi interne che ricevono il contesto della lane); (c) il motore resta orchestratore: gate, safety, segnali, equity. Da fare a suite verde prima/dopo, un estratto per PR.

### 4.2 Pagine Razor monolitiche
- **Il Problema:** `MlLab.razor` 998 righe, `Ensemble.razor` 917, `Pipeline.razor` 905, `Backtest.razor` 905, `Optimization.razor` 834, `Trading.razor` 782. Markup + stato + orchestrazione + mapping grafici in un file unico.
- **Perché è pericoloso:** il `@code` di queste pagine è l'unico strato del progetto quasi privo di test unitari mirati (i bUnit `Audit*` coprono i casi chiave, non la logica di orchestrazione); ogni feature UI nuova cresce lì dentro per inerzia. È anche il punto dove un refactor del service layer produce più rotture silenziose.
- **Soluzione Proposta:** estrazione meccanica del `@code` in code-behind (`.razor.cs` partial class) per le 6 pagine maggiori, poi estrazione dei blocchi ripetuti in componenti figli (pannello risultati backtest, tabella posizioni, KPI header — `<Stat>` esiste già e dimostra il pattern). Nessuna logica nuova: solo spostamento testabile.

### 4.3 Polling UI: `System.Threading.Timer` + lambda async (`async void` di fatto)
- **Il Problema:** pattern replicato con varianti in `Ensemble.razor:650`, `Metrics.razor:117`, `Pipeline.razor:530`, `Trading.razor:520`: `new Timer(async _ => await InvokeAsync(RefreshAsync), ...)`. La lambda è `async void`: un'eccezione che sfugge termina il processo (non il circuito — il **processo**).
- **Perché è pericoloso:** oggi regge perché i corpi sono quasi tutti in try/catch (verificato: `Ensemble.RefreshAsync:715`, `Trading.RefreshAsync:559`), ma `Pipeline.TickAsync:533` ha un tratto scoperto (`GetLiveStatus`/`StateHasChanged` fuori dal try) e la protezione dipende dalla disciplina di ogni singola pagina. Su una piattaforma che fa trading 24/7, "il processo muore se un tick di refresh lancia" è un single point of failure gratuito.
- **Soluzione Proposta:** un unico helper condiviso (`PollingTimer` basato su `PeriodicTimer` + loop `try/catch` + `IAsyncDisposable`) in `Components/Shared`, e sostituzione meccanica dei 4-5 usi. Il try/catch smette di essere una convenzione e diventa struttura.

### 4.4 Nessuna strategia di retry sui transitori Postgres
- **Il Problema:** `DatabaseServiceCollectionExtensions.AddProcioneDatabase` configura Npgsql senza `EnableRetryOnFailure`. Nessuna transazione esplicita nel codice (verificato: zero `BeginTransaction`/`TransactionScope`), quindi l'adozione è a costo zero.
- **Perché è pericoloso:** i worker assorbono i transitori (catch + retry al tick dopo), ma `ProcessCandleAsync` persiste ordini e posizioni **dopo** il fill sull'exchange: un blip di rete verso Postgres in quel punto lascia un ordine reale eseguito e non persistito. La riconciliazione (`ReconcileUncertainOrderAsync`, purge M2) è la rete di sicurezza — meglio non doverla usare per un errore che un retry da 3 tentativi avrebbe assorbito. In K8s (Fase 3) i micro-blip di rete sono fisiologici.
- **Soluzione Proposta:** `npgsql.EnableRetryOnFailure(maxRetryCount: 3)` nell'unico punto di registrazione condiviso da tutti gli host. Un test Testcontainers che uccide la connessione a metà operazione documenta il comportamento.

### 4.5 HttpClient catturati a vita nel singleton Alt-Data
- **Il Problema:** `Program.cs:210-220` — il singleton `IEnumerable<IAltDataSource>` chiama `CreateClient(...)` **una volta** e cattura gli `HttpClient` per l'intera vita del processo.
- **Perché è pericoloso:** vanifica la rotazione degli handler di `IHttpClientFactory` (default 2 minuti): DNS stale su un processo che gira per settimane. Le fonti RSS/ForexFactory che cambiano IP smettono di rispondere finché non si riavvia.
- **Soluzione Proposta:** iniettare `IHttpClientFactory` nei source e chiamare `CreateClient` per richiesta (o registrare i source come typed clients).

### 4.6 Fee live hardcoded, fee backtest configurabile
- **Il Problema:** `TradingEngine.cs:73` — `private const decimal FeePercent = 0.1m`, mentre il backtest usa `BacktestConfiguration.FeePercent` (parametrico).
- **Perché è pericoloso:** è un'asimmetria backtest/live silenziosa, la stessa classe di problema dell'intrabar-stop già corretto. Se il fee tier reale (o l'exchange) cambia, il paper/live engine mente di una costante e nessun confronto decay lo attribuisce alla causa giusta.
- **Soluzione Proposta:** spostare in `SafetyConfiguration` (o `LiveExecutionOptions`) con default 0.1 — hot-reload gratuito via `IOptionsMonitor` già iniettato.

### 4.7 Minori (ma da chiudere)
- **`BitgetClient.cs:508-511`**: catch best-effort **senza nemmeno un LogDebug** — l'unico catch veramente muto del repository. Aggiungere il log.
- **`OptimizationEngine.cs:363`**: `GetAwaiter().GetResult()` nel ramo bayesiano — contenuto e documentato (girato su thread-pool via `Task.Run`, mai sul circuito), ma sparirebbe rendendo l'obiettivo `Func<..., Task<double>>`.
- **`TradingEngine._championCache`**: il predictor ML resta in memoria dopo `StopAsync` (dispose solo al cambio Champion). Un modello per lane: leak limitato, ma il dispose a stop è una riga.
- **gRPC `GetPerformance` a 64MB** (`TradingServiceCollectionExtensions.cs:82`): il contratto trasporta l'intero storico trade; il tetto alzato è un cerotto onesto — la cura è la paginazione o un aggregato server-side.

---

## 5. Review di Solidità e Thread-Safety

### Moduli quant: verdetti dopo lettura integrale

| Modulo | Verdetto | Evidenza |
|---|---|---|
| `SafetyChecker` | ✅ **Rigoroso** | Funzione statica **pura** (testabile senza I/O), **fail-closed** su capitale ≤ 0, `>=` alla soglia di perdita giornaliera (chiude AL limite, non oltre), raccoglie TUTTE le violazioni, flag emergency-stop separato. 10 check, zero dipendenze. |
| `GarchModel` / `GarchFit` | ✅ **Rigoroso** | MLE vera via Nelder-Mead con **riparametrizzazione** che garantisce ω>0, α≥0, β≥0, α+β<1 in ogni punto esplorato; Student-t **standardizzata** (scala (ν-2)/ν, ν>2 per costruzione); forecast mean-reverting corretto; quantile di coda coerente. Deterministico (guess fisso). |
| `EngleGrangerCointegrationTest` | ✅ **Rigoroso** (post-audit P0-1) | Valori critici **MacKinnon 2010 specifici per cointegrazione** (−3.34 al 5%, non i −2.86 ADF standard), lag ADF per **AIC su campione comune**, bound di Schwert. Il punto 🔴 "cointegrazione troppo liberale" dell'audit di luglio è chiuso davvero. |
| `PurgedTimeSeriesCv` / `CombinatorialPurgedCv` | ✅ **Corretti** | Purge prima del test, embargo dopo, unione delle bande sui gruppi combinatori, combinazioni in ordine lessicografico ⇒ deterministici, stateless. |
| `DeflatedSharpeRatio` / `EffectiveTrials` / `OverfittingGate` | ✅ **Corretti e CABLATI** | Formule PSR/DSR conformi a Bailey-LdP 2014 (momenti population, kurtosis non-excess, convenzione per-periodo documentata). Non decorazione: il gate gira dentro `ModelStages.cs:383` con soglie DSR≥0.95, PBO panel, N effettivo via clustering di correlazione. |
| `OptimizationEngine` (walk-forward) | ✅ **Solido** | Selezione per-finestra **in-sample di default** con nota metodologica esplicita anti-peeking; tie-break deterministico (`ThenBy(ComboKey)`); parallelismo con strutture concurrent + `Interlocked`; OOS concatenato compounded. |
| `KellyCalculator` | ✅ **Completo** | Binario, continuo chiuso, continuo numerico (Simpson+golden-section), **empirico anti-code-grasse** (usato dalla pipeline — il punto 🔴 "sizing gaussiano" di luglio è chiuso), multi-asset Chan con ridge. Half-Kelly esposto ovunque. |
| Portfolio (`PortfolioMath`, MV/RP/HRP) | ✅ **Rigoroso** | Ledoit-Wolf fedele al paper (norma /p, clamp b²≤d² ⇒ δ∈[0,1]); **ERC esatto** (fixed-point Gauss-Seidel, non solo inverse-vol); HRP LdP cap. 16; MV analitica con onestà dichiarata sul long-only. Riuso coerente del water-filling di `EnsembleAllocator` (a sua volta corretto: bound geometrici, guard, normalizzazione). |
| `BacktestEngine` | ✅ **Onesto** | `decimal` puro, zero RNG, stop-prima-del-target (esito peggiore), fill a livello stop/open-se-gap, slippage sfavorevole, liquidazione intrabar, funding pro-rata. **Parità col live**: l'asimmetria SL intrabar-vs-close di luglio è corretta in `ProcessCandleAsync:416-459` (stesso worst-case, stesso trailing causale). |

### Thread-safety e stato

- **Modello per-corsia coerente**: `TradingEngine`, `EnsembleManager`, `RegimeDetector` serializzano con `SemaphoreSlim(1,1)`; **tutte** le entry pubbliche del motore acquisiscono il gate (verificate una per una, 13 coppie Wait/Release in try/finally). `PipelineEngine` protegge lo stato live con `lock` e un solo run alla volta.
- **Dual-read ML**: fire-and-forget corretto — flag `Interlocked` (mai una coda che cresce), input immutabile, eccezioni confinate, mai sul percorso decisionale (`TradingEngine.cs:566-602`).
- **Single-writer per costruzione**: `AddTradingLanes` rende mutuamente esclusivi motore locale e client remoto per registrazione condizionale (non per convenzione), e `EnsembleRebalanceWorker` appartiene a un solo host. È la risposta giusta al problema "due processi che scrivono la stessa corsia".
- **`ExperimentTracker`**: merge **JSONB atomico lato server** (`||`), il lost-update di luglio è chiuso alla radice.
- **Determinismo**: `MLContext(seed:1)`, ogni `new Random` del percorso ricerca è seedato (GeneticAlphaMiner, StrategyComposer, Bayesian, predictors, RegimeAssignment); gli unici fallback non seedati sono i tool interattivi (MonteCarlo/LeverageAdvisor) quando l'utente non passa un seed — la pipeline passa sempre `ctx.Seed`.
- **Failsafe anti-Live, 5 livelli verificati nel codice**: (1) Champion → throw se lane Live (`ResolveChampionStrategyAsync:533`); (2) `PromotionWorker` promuove solo Paper↔Testnet + guardia esplicita sulle decisioni incoerenti (log error, mai azione); (3) `RequireManualConfirmationForLive` nel SafetyChecker; (4) master key placeholder blocca Live a `StartAsync`; (5) fail-fast a startup Production. Più il replay-guard del `TradingWorker` (Testnet/Live partono da `UtcNow`: mai ordini reali in massa sullo storico).
- **Punti d'attenzione (non bug)**: `ProcessCandleAsync` tiene il gate della corsia durante I/O verso exchange e DB — serializzazione voluta, adeguata a candele 5m+, non a HFT (coerente con la filosofia dichiarata); la riconciliazione Futures fa una chiamata exchange per candela in Testnet/Live (costo accettato e commentato).

---

## 6. Roadmap di Refactoring Prioritaria (Action Plan)

Nessun difetto funzionale che metta a rischio fondi oggi ⇒ i P0 sono i consolidamenti a rapporto rischio/beneficio più favorevole, non emergenze.

### P0 — subito (basso rischio, alto valore igienico)

**P0-1 · Bonifica rami secchi** — *Come:* rimozione chirurgica di (a) `PairsSpreadAnalyzer` + registrazione `Program.cs:193`; (b) `PredictRegimeAsync` da `IRegimeDetector`, implementazione e fake nei test; (c) `ComputeBarAnatomy` + `BarAnatomy`; (d) `BarBuilder.ToOhlcv`; (e) correzione del commento ingannevole `TradingEngine.cs:1118` ("client trigger ancora stub" → i trigger sono implementati); (f) `SuggestHorizonBracket` → `internal`. Una PR sola, suite completa prima/dopo. Zero comportamento cambiato.

**P0-2 · `PollingTimer` condiviso per il refresh UI** — *Come:* helper unico (`PeriodicTimer` + loop con `try/catch` che logga e continua + `IAsyncDisposable`), sostituzione dei 4 usi di `System.Threading.Timer` (`Ensemble:650`, `Metrics:117`, `Pipeline:530`, `Trading:520`). Il rischio "eccezione in tick di polling = processo giù" sparisce per costruzione. Test bUnit sul componente helper.

**P0-3 · `EnableRetryOnFailure` su Npgsql** — *Come:* una riga in `AddProcioneDatabase` (`DatabaseServiceCollectionExtensions.cs`), ereditata da tutti e 4 gli host. Verificato: zero transazioni esplicite nel codice ⇒ nessuna controindicazione con la retrying execution strategy. Protegge il punto più delicato: persistenza post-fill in `ProcessCandleAsync`.

### P1 — prossimo ciclo (refactor strutturali, richiedono la tua approvazione puntuale)

**P1-4 · Decomposizione `TradingEngine`** in 3 PR successive: ① estrazione `TradingStateStore` (persistenza+audit, ~300 righe); ② estrazione `FuturesExecutionPath`/`SpotExecutionPath`; ③ motore = orchestratore (gate, safety, segnali, equity). Vincoli: nessun cambio di schema/JSON, suite verde a ogni passo, i test `Audit*` esistenti come golden tests.

**P1-5 · Code-behind per le 6 pagine >780 righe** (`.razor.cs` partial), poi componenti figli per i blocchi ripetuti. Meccanico, una pagina per PR, si parte da `MlLab`.

**P1-6 · Autorizzazione applicativa sul gRPC di trading** (interceptor a shared-secret o mTLS anche dentro il cluster): il confine "solo NetworkPolicy" per `ConfirmOrder`/`StartLane` è troppo sottile per soldi veri.

**P1-7 · Lifetimes HttpClient Alt-Data**: `IHttpClientFactory` per chiamata nei source RSS/ForexFactory/FXSSI.

### P2 — coerenza e pulizia

**P2-8 · Fee/slippage live configurabili** (via `SafetyConfiguration`/`LiveExecutionOptions`) per chiudere l'asimmetria col backtest parametrico.
**P2-9 · Obiettivo bayesiano async** (`Func<..., Task<double>>`) per eliminare l'ultimo sync-over-async (`OptimizationEngine.cs:363`).
**P2-10 · Micro-fix**: LogDebug nel catch muto `BitgetClient.cs:508`; dispose di `_championCache` in `StopAsync`.

### P3 — hardening di lungo periodo

**P3-11 · Master key fuori da appsettings** (DPAPI/secret store — è il TODO esistente).
**P3-12 · Paginazione/aggregazione `GetPerformance`** sul contratto gRPC (rimuovere la necessità del tetto a 64MB).

---

*Audit eseguito il 2026-07-17 su branch `claude/procionemgr-audit-consolidation-b489ce`. Nessuna modifica al codice applicativo: come da regole di ingaggio, questo documento è l'unico artefatto prodotto.*
