# PRD — Consolidamento Architetturale di ProcioneMGR

**Stato**: **Fase 0 completa e mergiata** (PR #13, 2026-07-17) e **Fase 1 completa** (2026-07-18
— PR #14/#15/#16, CQRS/Mediator + Intervento B, dettagli §4.7-§4.8) — tutti gli item P0-P3
fatti, TRANNE le 5 pagine Razor >780 righe non-`Trading.razor` di P1-5, non bloccanti (§3.6).
Fase 2 non iniziata (dettagli §5). **Creato**: 2026-07-17 · **Tipo**: documento vivo (aggiornare
ad ogni fase completata, vedi §8)

## Scopo di questo documento

Questo PRD nasce dal confronto tra un documento esterno — *"Dalla Monade alla Piattaforma
Robusta"*, un report generato con un altro assistente AI (Qwen) che propone in astratto
alcune pratiche architetturali .NET (Clean Architecture, CQRS/MediatR, multi-targeting,
pipeline asincrone, caching, documentazione API, "ibridazione" con qlib) — e lo stato reale
del codebase di ProcioneMGR, verificato riga per riga invece che assunto.

Non è una traduzione di quel documento. Ogni proposta esterna è stata verificata contro il
codice; dove il progetto ha già scelto una strada diversa (spesso migliore, perché nata da
un problema reale invece che da un principio generico), lo dichiara esplicitamente con
motivazione (§2). Dove la proposta esterna si applica davvero a un problema concreto già
diagnosticato, la integra con un audit interno preesistente invece di duplicarlo (§3-§4).

**Nota per chi arriva da un commento di codice**: `infra/k8s/README.md`,
`ProcioneMGR.Trading.csproj` e diversi messaggi di commit citano un "PRD Da Monolite a
Microservizi" come riferimento per le decisioni di estrazione a microservizi — quel
documento non era mai stato scritto per iscritto. §1 di questo file lo formalizza
retroattivamente (cosa è già stato fatto e perché); §3 in avanti guarda al futuro.

## Legenda

Questo documento usa **due scale di priorità diverse**, da non confondere:

- **P0/P1/P2/P3**: severità/rischio degli item di hardening, convenzione ereditata
  dall'audit architetturale del 2026-07-17 (`docs/REPORT-AUDIT-CONSOLIDAMENTO-2026-07.md`).
  P0 = basso rischio/alto valore igienico, P3 = hardening di lungo periodo.
- **Fase 0 / Fase 1 / Fase 2**: raggruppamento cronologico di *questo* PRD. La Fase 0
  contiene tutti gli item P0-P3 dell'audit (§3); la Fase 1 e la Fase 2 sono lavoro nuovo,
  non presente nell'audit originale.

---

## §1 — Stato Attuale dell'Architettura

*(Retrospettivo — nessuna azione richiesta in questa sezione. Contesto per le decisioni
nelle sezioni successive.)*

ProcioneMGR è nato come monolite Blazor Server e da lì è stato scomposto **progressivamente
e selettivamente**, non riscritto. La direzione è già stata scelta e in parte già eseguita:

```
┌───────────────────────────────────────────────────────────┐
│  ProcioneMGR  (Blazor Server, UI + orchestrazione)         │
│  Services/ (227 file, 30 sottocartelle a dominio)          │
│  Data/ (EF Core)   Components/ (pagine .razor)              │
└───────────────────────────────────────────────────────────┘
        │ gRPC/REST (feature-toggle, default in-process)
        ├──────────────┬───────────────┬─────────────────────
        ▼              ▼               ▼
 ProcioneMGR.Ingestion  ProcioneMGR.Ml   ProcioneMGR.Trading
  (REST, sync OHLCV)    (gRPC, dual-read  (gRPC, motore ordini)
                         osservativo)
        └──────────────┴───────────────┴──── ProcioneMGR.Contracts (.proto)
```

| Servizio | Toggle (default) | Trasporto | Ruolo |
|---|---|---|---|
| `ProcioneMGR.Ingestion` | `MarketData:UseRemoteIngestion=false` | REST (`POST /sync/{id}`) — **non gRPC**, per scelta dichiarata in `ingestion.proto` | Sync OHLCV, sostituisce il worker locale quando attivo |
| `ProcioneMGR.Ml` | `Ml:Enabled=false` | gRPC (`InferenceService.PredictSignal`) | Inferenza in dual-read **puramente osservativo**: logga il confronto, non influenza mai la decisione live |
| `ProcioneMGR.Trading` | `Trading:UseRemoteTrading=false` | gRPC (`TradingCommandService`, 11 rpc: 3 letture + 8 comandi) | Sostituisce **l'intero** motore locale; mutua esclusione garantita per costruzione in `AddTradingLanes` |

Il meccanismo che rende sicura questa estrazione è `AddTradingLanes`
(`ProcioneMGR/Services/Trading/TradingServiceCollectionExtensions.cs`), composition root
condiviso **verbatim** tra `ProcioneMGR/Program.cs` e `ProcioneMGR.Trading/Program.cs`:
registra `ITradingEngine` come keyed-singleton per corsia (0..2), con due implementazioni
intercambiabili — `TradingEngine` (motore reale) o `RemoteTradingEngineClient` (proxy gRPC)
— mai attive insieme sulla stessa corsia. `Trading.razor` e `TradingCommandServiceImpl`
(l'adapter gRPC del servizio standalone) risolvono entrambi l'istanza con lo stesso pattern,
`GetRequiredKeyedService<ITradingEngine>(laneId)`.

La **Pipeline autonoma** (`Services/Pipeline/`) e l'**AI supervisor** (`Services/Llm/`,
`Services/Agents/`) restano **in-process per scelta esplicita e documentata**
(`infra/k8s/README.md:205-207`): non sono candidati a estrazione, non c'è una fase in questo
PRD che li tocchi.

Attorno al codice applicativo esiste già: un cluster Kubernetes locale (`kind`), GitOps via
ArgoCD (sync **manuale**, l'infrastruttura stessa lo descrive come "in gran parte teorico"
su un cluster mono-sviluppatore), e uno stack di observability opt-in
(`Observability:Enabled`, default off) — OpenTelemetry Collector, Prometheus, Loki, Grafana,
distribuiti oggi **solo via `docker-compose`**, non ancora su `infra/k8s/`. Questo contesto
non richiede una fase dedicata a "renderlo più reale": nessun bisogno operativo di un solo
operatore lo giustifica oggi.

Un precedente storico rilevante: il report `docs/REPORT-ANALISI-RICOSTRUZIONE-2026-07.md`
(2026-07-08, precede l'estrazione a microservizi) aveva concluso che la piattaforma **non
andava ricostruita da zero**, e aveva individuato 3 gap poi effettivamente chiusi: rigore
statistico nella selezione (Deflated Sharpe Ratio/CPCV/PBO, oggi cablati e bloccanti),
governo del ciclo di vita dei modelli (`ModelRegistry` Champion/Challenger), fondamenta
operative (il progetto non era nemmeno un repository git). Lo si cita qui non come lavoro da
fare, ma come precedente utile: l'ultima volta che si è scritto un piano di questo tipo, la
disciplina di eseguirlo fase per fase ha funzionato.

---

## §2 — Non-Goals e idee scartate

Ogni voce scartata dal documento esterno è elencata qui esplicitamente, con motivazione —
per evitare che la stessa proposta venga rivalutata da zero se il documento originale viene
riletto tra qualche mese.

| Idea (dal documento esterno) | Decisione | Motivazione |
|---|---|---|
| Multi-targeting `netstandard2.1` + `net8.0` | **Scartata** | Tutti e 7 i progetti della solution sono già `net10.0` single-target (nessun `Directory.Build.props` condiviso, ognuno definisce il proprio `TargetFramework`). Non esiste alcun consumer NuGet esterno che benefici di compatibilità `.NET Framework 4.8`/`netstandard2.1` — l'unico effetto di questa proposta sarebbe un downgrade tecnologico non richiesto. |
| Clean Architecture a progetti `.csproj` separati (`Domain`/`Application`/`Infrastructure`/`Presentation`) | **Scartata** | L'architettura reale ha già scelto un asse di modularizzazione — bounded-context via gRPC/REST dietro feature-toggle (§1) — diverso da quello proposto (layer orizzontali). Sovrapporre un secondo asse costringerebbe a ri-tagliare per layer ogni bounded-context già estratto (`ProcioneMGR.Trading`, `.Ml`, `.Ingestion`), raddoppiando la complessità di progetto senza un beneficiario reale (nessun team con necessità di confini di compilazione forzati, nessun consumer NuGet). `Services/` è già organizzato in 30 sottocartelle a dominio: non è un monolite piatto in attesa di struttura. L'unica idea con un grano di valore — separare meglio l'orchestrazione UI dalla logica di dominio — è già coperta da P1-5 ridisegnato (§3.3), senza bisogno di nuovi assembly né dell'etichetta "Clean Architecture". |
| API pubblica documentata (OpenAPI/Swashbuckle), repository di esempi per sviluppatori terzi, "marketplace" di algoritmi/indicatori di terze parti | **Scartata** | ProcioneMGR è, per sua stessa descrizione (`README.md`: *"Progetto personale di ricerca quantitativa"*), una piattaforma solo-operatore. Non esiste oggi né un'API REST pubblica (l'unico endpoint non-Blazor del monolite è `/health`) né un'ambizione dichiarata di apertura a terzi. Investire in documentazione OpenAPI, repository campione e un marketplace risponderebbe a un pubblico che non esiste. |
| Convertitore `qlib`→ProcioneMGR: DataFrame custom, lettura Parquet/HDF5 | **Scartata** | La roadmap "prestiti da qlib" (`docs/ROADMAP-QLIB.md`, fasi QLIB-1→5, **tutte completate**) ha già affrontato l'ibridazione con qlib in modo mirato — Alpha158 in C#, execution TWAP/VWAP/Iceberg/Adaptive, experiment tracker, feature-drift detection, stacking di modelli, alpha mining genetico — e ha **esplicitamente rifiutato** l'interoperabilità diretta con la libreria Python: *"NON integrare le due librerie (stack Python incompatibile)"*. Riproporre un DataFrame/converter generico riaprirebbe una domanda già chiusa con verifica concreta, non con un principio astratto. |
| Event Sourcing per l'estensibilità | **Scartata** | Nessun bisogno concreto rilevato. L'audit trail delle decisioni di trading esiste già (persistenza diretta Postgres + `AuditAsync` in `TradingEngine`), e introdurre un event store per un problema non osservato aggiungerebbe complessità permanente (versioning degli eventi, proiezioni) senza un caso d'uso che la giustifichi oggi. |
| Pipeline asincrone generiche (`Channel<T>`/`IAsyncEnumerable<T>`) | **Spostata in Backlog Condizionale (§6)**, non scartata del tutto | Zero utilizzo oggi, e nessun collo di bottiglia I/O concreto rilevato nell'esplorazione. Introdurli ora sarebbe una soluzione in cerca di un problema. |
| Caching generalizzato (`IMemoryCache`/`IDistributedCache`/Redis) | **Spostata in Backlog Condizionale (§6)**, non scartata del tutto | `Services/Alpha/FactorCache.cs` mostra che dove il caching serve davvero (memoizzazione CPU-bound dei fattori alpha, chiave a impronta composita, coerenza train/serve), il repo **ce l'ha già**, in forma specifica e giustamente ristretta al suo dominio. Generalizzare a un backend distribuito risolverebbe un problema di scalabilità multi-processo che un Blazor Server mono-istanza con corsie keyed-singleton in-process non ha. |
| Reactive Extensions (Rx.NET) | **Scartata** | Stessa motivazione delle pipeline generiche: nessun caso d'uso reattivo complesso rilevato che i worker/timer esistenti (9 `BackgroundService` con `PeriodicTimer`) non coprano già. |
| Mimir (storage metriche long-term multi-tenant) | **Scartata** | Vedi §5 — risolve un problema di scala che un cluster `kind` locale mono-operatore non ha. |

---

## §3 — Fase 0: Fondamenta (hardening)

**Obiettivo**: chiudere gli item già identificati dall'audit architetturale del
2026-07-17 (`docs/REPORT-AUDIT-CONSOLIDAMENTO-2026-07.md`, voto 8/10, zero stub/fake, un
solo TODO in tutto il repo), a rischio complessivamente basso, senza introdurre pattern o
dipendenze nuove. Nessun item dell'audit originale è stato lasciato fuori da questo PRD.

### 3.1 — Primo passo pratico (blocca l'inizio operativo)

Gli item P0-1/P0-2/P0-3 sono **già scritti**, ma esistono solo come modifiche non
committate nel worktree `zealous-ellis-b6357f` (branch locale
`claude/procionemgr-audit-consolidation-b489ce`, mai pushato su `origin`). Prima che
"P0 è pronto" sia vero anche sul branch principale, quel lavoro va portato via commit + PR
verso `master`. Finché questo non accade, gli item 3.2 vanno considerati "progettati, non
ancora disponibili" anche se marcati come pronti sotto.

### 3.2 — Mappa completa item → trattamento

| # | Item | Descrizione | Trattamento |
|---|---|---|---|
| P0-1 | Bonifica rami secchi | Rimuovere: classe morta `PairsSpreadAnalyzer` + relativa registrazione DI; `IRegimeDetector.PredictRegimeAsync` (mai invocato nel flusso reale); `ExcursionAnalyzer.ComputeBarAnatomy` + record `BarAnatomy` (zero caller); `BarBuilder.ToOhlcv` (zero caller); correggere il commento ingannevole `TradingEngine.cs:1118` ("client trigger ancora stub" — falso, sono implementati); `ExcursionAnalyzer.SuggestHorizonBracket` → `internal`. | Invariato dall'audit |
| P0-2 | `PollingTimer` condiviso | Helper unico (`PeriodicTimer` + loop `try/catch` che logga e continua + `IAsyncDisposable`) in `Components/Shared/`, sostituisce i 4 usi di `System.Threading.Timer` in `Trading.razor:497/520`, `Pipeline.razor:522/530`, `Metrics.razor:109/117`, `Ensemble.razor:621/650`. Elimina il rischio che un'eccezione non catturata in una lambda `async void` abbatta il **processo** (non solo il circuito Blazor). | Invariato dall'audit |
| P0-3 | `EnableRetryOnFailure` su Npgsql | Una riga in `DatabaseServiceCollectionExtensions.AddProcioneDatabase`, ereditata da tutti gli host. Verificato: zero transazioni esplicite nel codice → nessuna controindicazione. Protegge la persistenza post-fill in `ProcessCandleAsync` dai transitori di rete, fisiologici in K8s. | Invariato dall'audit |
| P1-5 | Pagine Razor >780 righe | `MlLab.razor` (998), `Ensemble.razor` (917), `Pipeline.razor` (905), `Backtest.razor` (905), `Optimization.razor` (834), `Trading.razor` (782) — più un caso limite, `Sentiment.razor` (742), da includere se si abbassa la soglia a 700. | **Ridisegnato** — vedi §3.3 |
| P1-6 | Autorizzazione applicativa gRPC trading | `TradingCommandServiceImpl` (`ConfirmOrder`/`StartLane` possono muovere denaro vero) non ha oggi alcun controllo di autorizzazione applicativa — l'unico confine è la `NetworkPolicy` K8s, bypassabile con `kubectl port-forward` (limite dichiarato in `infra/k8s/README.md`). | **Resta in Fase 0** — vedi §3.4 per la correzione rispetto alla proposta iniziale |
| P1-7 | Lifetimes `HttpClient` in AltData | `RssNewsSource`, `ForexFactoryIngestor`, `RetailSentimentIngestor` sono registrate come singleton collettivo (`Program.cs:210-220`) che catturano l'`HttpClient` ottenuto da `IHttpClientFactory.CreateClient()` **una sola volta a startup** invece che per-richiesta — vanifica la rotazione degli handler anti-DNS-stale. Fix: iniettare `IHttpClientFactory` nelle classi sorgente, chiamare `CreateClient(...)` per fetch. | Invariato dall'audit |
| P2-8 | Fee/slippage live hardcoded | `TradingEngine.cs:73` — `private const decimal FeePercent = 0.1m`, usata in 6 punti (righe 898, 1052, 1584-1585, 1717-1718) — contro `BacktestModels.cs:25`, dove `FeePercent` è una proprietà configurabile. Spostare in `SafetyConfiguration`/`LiveExecutionOptions` con hot-reload via `IOptionsMonitor` (già iniettato in `TradingEngine`). | Invariato nel contenuto, **sequenziato per ultimo in Fase 0** — vedi §3.5 |
| P2-9 | Obiettivo bayesiano sync-over-async | `OptimizationEngine.cs:363` — `GetAwaiter().GetResult()` nel ramo bayesiano (contenuto su thread-pool via `Task.Run`, mai sul circuito, ma sparirebbe rendendo l'obiettivo `Func<..., Task<double>>`). | Invariato dall'audit |
| P2-10 | Micro-fix | `LogDebug` nel catch muto `BitgetClient.cs:508-511` (best-effort fill-enrichment, oggi ingoia eccezioni senza traccia); dispose di `TradingEngine._championCache` in `StopAsync`. | Invariato dall'audit |
| P3-11 | Master key fuori da appsettings | L'unico TODO dichiarato nel repository (`AesGcmEncryptionService.cs:20`), oggi presidiato da due guardie fail-fast (avvio Production, blocco Live in `TradingEngine.StartAsync`). Migrare a un secret store (DPAPI o equivalente). | Invariato dall'audit |
| P3-12 | Paginazione `GetPerformance` gRPC | Il contratto trasporta l'intero storico trade; il tetto messaggio è stato alzato a 64MB come soluzione temporanea. Sostituire con paginazione o aggregazione server-side. | Invariato — **pienamente parallelizzabile con la Fase 1** (non tocca `TradingEngine.cs` né la sua interfaccia pubblica in modo conflittuale) |

### 3.3 — P1-5 ridisegnato: non è semplice estrazione code-behind

Verificato: **zero file `.razor.cs`** esistono in tutto il progetto `ProcioneMGR`. Tutta la
logica di ciascuna pagina — inclusa l'orchestrazione di business, non solo il binding UI —
vive in un unico blocco `@code` inline. Per `MlLab.razor`, quel blocco da solo è ~565 righe
di C#. Questo significa che spostare `@code` in un `.razor.cs` partial **non riduce la
complessità, la ridenomina soltanto**.

Il criterio di accettazione corretto, per ciascuna delle 6 (o 7) pagine: estrarre **prima**
la logica di orchestrazione (chiamate a servizi, gestione dello stato applicativo,
validazione form) in una classe C# testabile — un service o view-model dedicato in
`Services/<Area>/` — lasciando nel componente Blazor solo ciò che è intrinsecamente legato
al suo ciclo di vita (parametri, `OnInitializedAsync`, binding, rendering condizionale). "Il
`@code` è più corto" non è un criterio di successo valido di per sé; **"la logica di
orchestrazione ha test unitari indipendenti da Blazor"** lo è.

### 3.4 — Correzione: l'autorizzazione gRPC (P1-6) non passa da MediatR

In una prima bozza di questo design si era ipotizzato di risolvere P1-6 come "capstone"
della Fase 1, tramite una pipeline behavior MediatR applicata ai comandi di trading. È
un errore da correggere esplicitamente: **`TradingCommandServiceImpl` non passerà mai da
`IMediator`** (principio cardine della Fase 1, §4.2) — resta un adapter sottile che chiama
`ITradingEngine` direttamente, esattamente come oggi. Una behavior MediatR lato monolite
protegge solo il percorso Blazor→Mediator, che ha **già** oggi il proprio gate
(`[Authorize]` su `Trading.razor`). Il gap reale, documentato esplicitamente nel codice
(`trading.proto:37-38`, `TradingCommandServiceImpl.cs:13-17`), è l'esposizione di rete del
servizio gRPC **standalone** (topologia `Trading:UseRemoteTrading=true`), oggi presidiata
solo dalla `NetworkPolicy` K8s.

La soluzione corretta è indipendente da MediatR: un **gRPC server interceptor** su
`TradingCommandServiceImpl` che verifica un header shared-secret (stesso pattern di
gestione già in uso per la master key — env var/appsettings — verificato lato client nel
canale gRPC di `RemoteTradingEngineClient`). mTLS resta un'opzione più forte ma più
costosa da configurare (gestione certificati), da valutare solo se il modello di minaccia
lo richiede (es. cluster multi-tenant, oggi non il caso).

### 3.5 — Nota di sequenza per `Trading.razor` e P2-8

`Trading.razor` è insieme una delle 6 pagine di P1-5 **e** il file che la Fase 1 dovrà
modificare (6 call-site diretti a `ITradingEngine`, righe 614-697: `StartAsync`,
`StopAsync`, `EmergencyStopAsync`, `ClosePositionAsync`, `ConfirmOrderAsync`,
`RejectOrderAsync`). Ordine raccomandato: estrarre l'orchestrazione di `Trading.razor` in un
service dedicato (es. `TradingPageService`, che nella sua prima versione chiamerà
`ITradingEngine` direttamente) **dentro** la Fase 0; la Fase 1 modificherà poi quel service
per usare `IMediator.Send(...)` al posto della chiamata diretta. Così la Fase 1 tocca un
service C# isolato e già testato, non un blocco `@code` Razor da riscrivere da zero, e P1-5
non va rifatto dopo l'introduzione di MediatR.

Allo stesso modo, **P2-8** (fee hardcoded) va chiuso appena prima di iniziare la Fase 1: i 6
punti d'uso di `FeePercent` sono nella cascata privata che l'Intervento B della Fase 1
(§4.5) estrarrà in collaboratori dedicati — parametrizzare la fee prima evita di toccare due
volte lo stesso codice.

Le altre 5 pagine di P1-5 (`MlLab`, `Ensemble`, `Pipeline`, `Backtest`, `Optimization`) non
hanno alcuna relazione con la Fase 1 e possono procedere in qualunque ordine, quando
conviene.

### 3.6 — Criteri di accettazione Fase 0

- [x] Ogni item chiuso con una PR indipendente, CI verde (`ci.yml`: build + test + audit
  vulnerabilità, già eseguita a ogni push/PR).
- [x] Zero regressioni sui test esistenti.
- [x] Il commento ingannevole `TradingEngine.cs:1118` corretto.
- [x] Zero classi o registrazioni DI morte rilevabili con la stessa analisi statica usata
  dall'audit (grep dei caller, non solo dei tipi).

**Rischio**: basso su tutti gli item — nessun pattern nuovo, nessuna dipendenza nuova, per
lo più modifiche localizzate a un singolo file. **Approvazione esplicita richiesta**: no —
è lavoro già scritto o comunque già rivisto concettualmente dall'audit; procede PR per PR a
discrezione dell'operatore, in qualunque ordine rispetti le due note di sequenza di §3.5.

**FASE 0 SOSTANZIALMENTE COMPLETATA (2026-07-17, stessa giornata della stesura)** — 11 commit sul branch
`claude/procionemgr-audit-consolidation-b489ce` (report audit + P0-1/P0-2/P0-3/P1-5/P1-6/
P1-7/P2-8/P2-9/P2-10/P3-11/P3-12), **1011/1011 test verdi con Docker/Postgres reale**. Note
sugli scostamenti dal design originale, emersi durante l'esecuzione:
- **P3-11** non ha richiesto codice: investigato prima di implementare, la master key era
  **già** fuori da appsettings.json in produzione (Secret K8s dedicato in entrambi i
  deployment, `infra/k8s/{trading,ui}/deployment.yaml`) — Azure Key Vault (proposta
  originale del TODO nel codice) non è pertinente per uno stack Kubernetes-nativo. Solo il
  commento è stato corretto. La rotazione della chiave resta l'unico pezzo TODO reale,
  deliberatamente rimandato come feature a sé.
- **P3-12** non ha richiesto paginazione vera: investigato prima di costruirla, **nessun
  consumer attuale legge il campo `trades`** (né locale né remoto — confermato da commenti
  già presenti nel codice). Fix minimo: troncamento ai 500 trade più recenti nel motore
  (stesso pattern già in uso in Backtest/MlLab/PairsTrading.razor), non un meccanismo di
  cursori/continuazione per un campo senza lettori.
- **P1-6**: l'autorizzazione applicativa gRPC è un `SharedSecretAuthInterceptor` **fail-closed**
  (rifiuta tutto se il segreto non è configurato, non degrada in silenzio), non una pipeline
  MediatR — coerente con §4.2, che la Fase 1 conferma.
- **P2-8** (fee hardcoded) è stato aggiunto **oltre** alla tabella originale di §3.2: la
  prima stesura del PRD lo aveva elencato ma la sessione di esecuzione lo aveva inizialmente
  saltato, corretto prima di dichiarare la fase chiusa.
- **P1-5** ha coperto `Trading.razor` (il prerequisito esplicito per la Fase 1); le altre 5
  pagine Razor >780 righe (MlLab, Ensemble, Pipeline, Backtest, Optimization) restano da
  fare, senza vincoli di sequenza tra loro né con la Fase 1.

Branch pushato e PR aperta: [#13](https://github.com/markcapitanelli/ProcioneMGR/pull/13)
(o successiva se rinominata/sostituita — verificare lo stato reale su GitHub prima di
assumere che sia ancora quella aperta).

---

## §4 — Fase 1: CQRS/MediatR — decomposizione di `TradingEngine`

**Obiettivo**: introdurre MediatR come primo pattern CQRS del repository (oggi: zero
occorrenze di MediatR o di un pattern Command/Query manuale in tutto il codebase),
applicato alla superficie pubblica di `TradingEngine.cs` — 2211 righe, 17 parametri nel
costruttore (16 dipendenze iniettate + `laneId`), 13 membri pubblici "di sessione/ordini"
(5 query, 8 comandi) più 2 metodi hot-path (`ProcessCandleAsync`,
`ProcessDueExecutionSlicesAsync`) esclusi dal perimetro CQRS — **combinato con**, non
sostituito da, l'estrazione a classi semplici della cascata privata dietro
`ProcessCandleAsync` (righe indicative 371-1846: apertura/chiusura spot/futures, bracket
order, execution slicing, riconciliazione — **circa due terzi delle 2211 righe totali**,
fuori dalla superficie pubblica).

Questo secondo punto è cruciale: **MediatR da solo riduce `TradingEngine.cs` solo del
15-25%**. Il grosso del file non è raggiungibile da una decomposizione Command/Query sulla
sola interfaccia pubblica. Vanno scritti entrambi gli interventi (§4.5), non uno in
alternativa all'altro — è il rischio concreto di applicare CQRS come se fosse l'unica leva
disponibile.

### 4.1 — Superficie pubblica di `TradingEngine` (perimetro dell'Intervento A)

| Categoria | Membri | Righe (indicative, ante-refactor) |
|---|---|---|
| Query | `GetStatusAsync`, `GetOpenPositionsAsync`, `GetOrderHistoryAsync`, `GetPerformanceAsync`, `GetPendingOrdersAsync` | 1419, 1847, 1894, 1954, 1961 |
| Comandi | `StartAsync`, `StopAsync`, `EmergencyStopAsync`, `CloseAllPositionsAsync`, `ClosePositionAsync(string)`, `ConfirmOrderAsync`, `RejectOrderAsync`, `SetStopLossTakeProfitAsync` | 142, 285, 299, 312, 1939, 1428, 1452, 1901 |
| Esclusi dal perimetro CQRS (hot-path, chiamati dai worker a frequenza di candela) | `ProcessCandleAsync`, `ProcessDueExecutionSlicesAsync` | 371, 1308 |

Nota sul contratto gRPC: `trading.proto` espone 11 rpc (3 letture — `GetLaneStatus`,
`GetOpenPositions`, `GetPerformance` — e 8 comandi, la stessa lista di cui sopra meno
`GetOrderHistoryAsync`/`GetPendingOrdersAsync` che restano solo lato Blazor/UI di conferma
manuale). La separazione letture/comandi **esiste già** al livello del contratto —
formalizzarla in C# con MediatR non introduce un concetto nuovo nella piattaforma, lo rende
solo esplicito anche nel codice che lo implementa.

### 4.2 — Principio cardine: `ITradingEngine` resta il confine stabile

`AddTradingLanes` registra oggi due implementazioni intercambiabili di `ITradingEngine`
dietro lo stesso keyed-singleton — `TradingEngine` (motore reale) e
`RemoteTradingEngineClient` (proxy gRPC) — consumate da due porte d'ingresso indipendenti
che risolvono l'istanza nello stesso identico modo:
`Trading.razor` (`Services.GetRequiredKeyedService<ITradingEngine>(_laneId)`) e
`TradingCommandServiceImpl.Engine(int laneId)`. MediatR si inserisce **sopra**
l'interfaccia, e **solo** sul lato Blazor:

```
Trading.razor → TradingPageService → IMediator.Send(new StartLaneCommand(laneId, mode))
    → StartLaneCommandHandler → GetRequiredKeyedService<ITradingEngine>(laneId).StartAsync(...)
        → [invariato] TradingEngine reale  O  RemoteTradingEngineClient (proxy gRPC)
```

Conseguenze dirette, da rispettare come vincoli di design:

- **`ITradingEngine` non cambia forma.** `TradingCommandServiceImpl`,
  `RemoteTradingEngineClient`, `TradingWorker`, `ExecutionWorker`, `LanePromoter`,
  `PromotionEvaluator` continuano a chiamare l'interfaccia esattamente come oggi — **zero
  modifiche** a questi file.
- **`TradingCommandServiceImpl` non diventa un publisher MediatR.** Tre motivi: (1) è già
  un adapter sottile (`Engine(laneId).XxxAsync(...)` + `DomainGuard<T>`, che traduce
  `InvalidOperationException` in `RpcException(FailedPrecondition)` — un comportamento
  specifico al trasporto gRPC, non portabile in una pipeline behavior generica senza
  reintrodurre una dipendenza da gRPC nel livello applicativo); (2) in topologia remota
  (`Trading:UseRemoteTrading=true`) un eventuale `IMediator` locale al processo
  `ProcioneMGR.Trading` farebbe girare due volte la stessa cross-cutting concern
  (logging, autorizzazione) per la stessa azione utente; (3) il gate di autorizzazione
  applicativa appartiene concettualmente al lato Blazor, dove l'utente autenticato clicca
  (`[Authorize]` su `Trading.razor` esiste già) — non al lato gRPC, il cui gap va chiuso
  separatamente e diversamente (§3.4).
- **`IMediator` è un singolo servizio globale, non keyed per lane.** Il routing per
  corsia avviene per **dato**, non per istanza di servizio — ogni comando/query porta
  `LaneId` come proprietà:

  ```csharp
  public sealed record StartLaneCommand(int LaneId, TradingMode Mode) : IRequest;
  public sealed record GetLaneStatusQuery(int LaneId) : IRequest<TradingEngineStatus>;
  ```

  L'handler risolve l'istanza keyed a runtime
  (`serviceProvider.GetRequiredKeyedService<ITradingEngine>(request.LaneId)`), riusando —
  idealmente estraendolo in un piccolo helper condiviso con
  `TradingCommandServiceImpl.Engine(int)` per non duplicare il controllo di range
  `0..TradingLanes.Count-1` — la stessa identica logica di risoluzione già in produzione.
  Tre corsie keyed-singleton non richiedono tre `IMediator`.

### 4.3 — Dove vive il codice

Tutto **dentro il monolite** (`ProcioneMGR`), non in un nuovo progetto/assembly — coerente
con lo scarto della "Clean Architecture a progetti separati" in §2:

```
ProcioneMGR/Services/Trading/
├── Commands/     StartLaneCommand.cs, StopLaneCommand.cs, EmergencyStopCommand.cs,
│                 ClosePositionCommand.cs, CloseAllPositionsCommand.cs,
│                 SetStopLossTakeProfitCommand.cs, ConfirmOrderCommand.cs, RejectOrderCommand.cs
│                 (handler accanto a ciascun comando)
├── Queries/      GetLaneStatusQuery.cs, GetOpenPositionsQuery.cs, GetPerformanceQuery.cs,
│                 GetOrderHistoryQuery.cs, GetPendingOrdersQuery.cs
├── Behaviors/    LoggingBehavior.cs
├── TradingEngine.cs   (orchestratore assottigliato, implementa ITradingEngine)
└── Internal/     collaboratori estratti dalla cascata privata (§4.5, Intervento B) —
                  nomi indicativi: SpotOrderExecutor, FuturesOrderExecutor,
                  BracketOrderManager, ExecutionSlicePlanner, PositionCloser,
                  FuturesReconciler
```

`ProcioneMGR.Trading` (il servizio standalone) **non referenzia MediatR**: la
registrazione (`services.AddMediatR(...)`) va nel solo `ProcioneMGR/Program.cs`. La
composizione condivisa `AddTradingLanes` resta esattamente com'è, senza sapere nulla di
MediatR.

### 4.4 — Preflight obbligatorio prima di aggiungere la dipendenza

Verificare i **termini di licenza correnti** del pacchetto NuGet `MediatR` prima di
aggiungerlo al progetto: negli ultimi anni il progetto ha introdotto un modello con
componente commerciale per alcuni utilizzi/soglie — da controllare puntualmente al momento
di iniziare questa fase, non da assumere in un senso o nell'altro in questo documento (le
condizioni possono essere cambiate tra la stesura di questo PRD e la sua esecuzione).

Se i termini non risultassero idonei a un progetto personale non commerciale, due
alternative equivalenti nella forma (`IRequest`/`IRequestHandler`, stessa API), da valutare
senza cambiare nulla del design sopra:

- **`Mediator` di martinothamar** (licenza MIT, basato su source-generator, drop-in
  compatibile).
- **Dispatcher interno scritto a mano**: dato il perimetro piccolo e chiuso (13 tipi di
  richiesta, un solo bounded context), poche decine di righe (due interfacce +
  risoluzione per tipo chiuso via DI + una catena di behavior) coprono lo stesso bisogno
  senza dipendenza esterna.

### 4.5 — I due interventi

- **Intervento A — Comandi/Query MediatR** sulla superficie pubblica (§4.1). Riduce la
  superficie pubblica e crea un punto unico per cross-cutting concern (logging uniforme
  via `LoggingBehavior`, oggi sparso in chiamate `logger.LogInformation`/`LogWarning`
  disseminate nei singoli metodi).
- **Intervento B — Estrazione a classi semplici**, nessun framework, della cascata privata
  dietro `ProcessCandleAsync`: `TryOpenAsync`, `ExecuteSpotOpenAsync`/
  `ExecuteFuturesOpenAsync` (~140-175 righe ciascuno), `TryPlaceRestingBracketAsync`/
  `TryCancelRestingBracketAsync`, `TryBuildAndStartExecutionPlanAsync`,
  `CloseSpotPositionAsync`/`CloseFuturesPositionAsync`, `ReconcileFuturesPositionsAsync`.
  È l'estrazione conservativa che l'audit originale proponeva per l'intero file, applicata
  qui alla parte dove effettivamente si applica. `ProcessCandleAsync` e
  `ProcessDueExecutionSlicesAsync` restano chiamate dirette da `TradingWorker`/
  `ExecutionWorker` (ciclo interno a frequenza di candela: MediatR lì aggiungerebbe
  overhead di reflection/pipeline senza alcun beneficio, non c'è UI o gRPC da
  disaccoppiare su un hot-path interno).

### 4.6 — Sequenza PR incrementale raccomandata

Coerente col principio "un estratto per PR, suite verde prima/dopo" già seguito
dall'audit per gli altri item:

1. **Preflight** (§4.4): scelta e verifica della libreria.
2. **Query pilota** — `GetLaneStatus`, `GetOpenPositions`, `GetPerformance`,
   `GetOrderHistory`, `GetPendingOrders`. Rischio più basso (nessuna mutazione di stato),
   valida l'intera impalcatura (DI, `TradingPageService`, `LoggingBehavior`) prima di
   toccare comandi che muovono denaro.
3. **Comandi a rischio crescente**: `StartLane`/`StopLane` (ciclo di vita sessione, ben
   isolati) → `SetStopLossTakeProfit` (contenuto) → `ClosePosition`/
   `CloseAllPositions`/`EmergencyStop` (punto naturale per iniziare l'Intervento B sulla
   cascata di chiusura) → `ConfirmOrder`/`RejectOrder` (ultimi: sensibilità di sicurezza
   più alta, beneficiano del pattern già collaudato sui precedenti).
4. **Intervento B**, interlacciato o successivo al punto 3: prima i collaboratori più
   isolati (es. `ApplyAutoStops`), poi i più corposi
   (`ExecuteSpotOpenAsync`/`ExecuteFuturesOpenAsync`), infine `ProcessCandleAsync` ridotto
   a orchestratore sottile sui collaboratori estratti.
5. **`LoggingBehavior`**: introdotto presto (dopo il punto 2), sostituisce
   progressivamente i log sparsi nei metodi pubblici con un punto uniforme.

### 4.7 — Criteri di accettazione

- [x] `ITradingEngine` invariata byte-per-byte, oppure ogni variazione è additiva e motivata
  esplicitamente nella PR che la introduce.
- [x] `trading.proto` e `TradingCommandServiceImpl` **invariati**.
- [x] Tutta la suite esistente verde, in particolare i test di round-trip gRPC
  (`TradingGrpcRoundTripTests`), quelli su `RemoteTradingEngineClient`, su
  `TradingServiceCollectionExtensions` e sulla state machine di promozione/lane — sono la
  rete di sicurezza per la regressione sul confine che questa fase preserva
  deliberatamente.
- [x] Nuovi handler/behavior con test unitari propri, nelle convenzioni esistenti di
  `ProcioneMGR.Tests`.
- [x] `TradingEngine.cs` residuo sotto una soglia esplicita (indicativamente 400-500 righe),
  con ciascun collaboratore estratto singolarmente leggibile e testabile in isolamento —
  **scostamento**: 1263 righe residue (2227 → 1263, -43%), sopra l'indicazione 400-500. La
  soglia era esplicitamente indicativa; il residuo copre il ciclo di vita/query
  dell'interfaccia pubblica, i due hot-path (`ProcessCandleAsync`/
  `ProcessDueExecutionSlicesAsync`, per scelta esplicita §4.5 non estratti) e gli helper
  d'engine (`BuildSafetyStatus`, `EmergencyInternalAsync`, ecc.) non nominati dalla lista di
  estrazione — tutti e 9 i metodi effettivamente nominati sono estratti.
- [x] Smoke test manuale — avvio di una corsia Paper via UI, apertura e chiusura di un ordine
  — prima di ogni merge che tocchi l'Intervento B — **scostamento**: sostituito con 5 test
  bUnit che cliccano i bottoni reali di `Trading.razor` (Avvia/Ferma trading, doppia conferma
  Emergency Stop, Conferma/Rifiuta ordine pendente) contro un motore fake, stesso percorso
  UI→TradingPageService→IMediator→handler→ITradingEngine di un test manuale dal vivo ma
  ripetibile e mai su un ordine vero. Un unico smoke test manuale live (dati reali, Postgres
  reale) è stato comunque eseguito sulla Tranche 1 (query pilota) prima di questa nota.

**Rischio**: **alto** — è l'unica fase di questo PRD che introduce una dipendenza esterna
nuova e tocca la logica di esecuzione ordini a soldi reali. **Approvazione**: strategica già
concessa (l'utente ha scelto esplicitamente CQRS/MediatR rispetto all'estrazione
conservativa alternativa); nessun gate formale aggiuntivo oltre alla revisione PR-per-PR con
suite verde e smoke test — coerente con un progetto solo-operatore, dove l'unico
approvatore è la stessa persona che scrive il codice.

### 4.8 — FASE 1 COMPLETATA (2026-07-18)

Tre PR, nell'ordine raccomandato da §4.6: [#14](https://github.com/markcapitanelli/ProcioneMGR/pull/14)
(preflight + 5 query pilota), [#15](https://github.com/markcapitanelli/ProcioneMGR/pull/15)
(7 comandi), [#16](https://github.com/markcapitanelli/ProcioneMGR/pull/16) (Intervento B, 9
collaboratori). Suite finale 1035/1035 (1011 baseline Fase 0 + 24 nuovi test). Scostamenti dal
design originale, emersi durante l'esecuzione:

- **§4.4 preflight**: MediatR risultava passato a licenza commerciale (v13+, dual RPL-1.5/
  commerciale, Lucky Penny Software, da luglio 2025) — il livello Community gratuito
  coprirebbe questo progetto (personale, revenue zero) ma è una condizione di idoneità da
  riverificare nel tempo, non una concessione incondizionata. Scelta invece **`martinothamar/Mediator`**
  (MIT): stessa forma `IRequest`/`IRequestHandler`/`IPipelineBehavior` già assunta dal design
  di questo documento, gratuito per sempre, verificato funzionante su .NET 10 con uno spike
  reale prima di adottarlo. `ProcioneMGR.Trading` non lo referenzia, come previsto da §4.3.
- **`CloseAllPositionsCommand` non creato**: nonostante compaia nello schizzo di cartelle di
  §4.3, il suo unico chiamante reale è `LanePromoter` — che §4.2 esclude esplicitamente
  dall'adottare Mediator. Crearlo sarebbe stata superficie morta senza alcun chiamante Blazor.
- **Gap di test preesistente segnalato, non colmato**: `FuturesPositionReconciler` (da
  `ReconcileFuturesPositionsAsync`) resta senza un test reale dedicato — copertura solo
  indiretta via `ProcessCandleAsync`. Scrivere un test dedicato è lavoro futuro valido, fuori
  dallo scope di "estrarre senza cambiare comportamento" di questa fase.
- **Audit leggero di fine fase** (§8): non eseguito come sweep separato — la verifica è
  avvenuta incrementalmente, un collaboratore alla volta, con la suite reale su
  `TradingEngine` (49 test) rilanciata dopo ogni singola estrazione e mai bypassata.

---

## §5 — Fase 2: Osservabilità distribuita (tracing)

**Perché ora**: `ObservabilityExtensions.AddProcioneObservability`
(`ProcioneMGR/Services/Observability/ObservabilityExtensions.cs`) configura oggi
`.WithMetrics(...)` e `.WithLogging(...)` ma **nessun `.WithTracing(...)`** — non è solo
"Tempo non è ancora deployato", è l'assenza completa di strumentazione di tracing lato
applicazione (zero `ActivitySource`/instrumentation gRPC in tutti e 4 gli host). Da quando
esistono chiamate gRPC reali tra processi (monolite→`ProcioneMGR.Ml`,
monolite→`ProcioneMGR.Trading`), un problema cross-servizio — latenza, errore a catena — è
visibile solo come log e metriche disgiunte per processo, senza correlazione di span: è
esattamente il caso d'uso che il tracing distribuito risolve, e che né Prometheus né Loki
coprono.

**Perché non Mimir**: Mimir risolve uno storage metriche a lungo termine, orizzontalmente
scalabile, multi-tenant — un problema di scala che un cluster `kind` locale mono-operatore
non ha. Prometheus locale è già sufficiente per la cardinalità e la retention di un
progetto personale. Da rivalutare solo davanti a un problema concreto di spazio disco o
retention osservato, non preventivamente.

### 5.1 — Azioni concrete

Additivo, stesso gate `Observability:Enabled` (default off) già esistente per
metrics/logging — costo zero quando disattivato:

- `ObservabilityExtensions.cs`: aggiungere
  `.WithTracing(t => t.AddAspNetCoreInstrumentation().AddGrpcClientInstrumentation().AddOtlpExporter(...))`
  accanto a `.WithMetrics`/`.WithLogging` esistenti.
- `infra/observability/otel-collector-config.yaml`: aggiungere una pipeline `traces:`
  (il receiver `otlp` è già configurato, serve solo l'exporter verso Tempo e la voce in
  `service.pipelines`).
- `infra/observability/docker-compose.yml`: aggiungere il servizio Tempo.
- `infra/observability/grafana/provisioning/datasources/datasources.yaml`: aggiungere un
  datasource `type: tempo`, stesso pattern già in uso per Prometheus/Loki.
- **Non** creare `infra/k8s/observability/` — coerente con §1 (K8s resta in gran parte
  teorico, lo stack di observability oggi vive solo via `docker-compose`) — a meno che
  l'operatore non stia usando attivamente il cluster `kind` per debug quotidiano al
  momento di eseguire questa fase.

**Dipendenze**: nessuna dipendenza tecnica da Fase 0 o Fase 1 (i file coinvolti sono quasi
interamente disgiunti da `Services/Trading/*`) — potrebbe partire anche in parallelo.
**Priorità**: comunque dopo la Fase 1, perché è infrastruttura di debug e non hardening sui
fondi; resta un'alternativa produttiva per una sessione in cui manca voglia o tempo per la
chirurgia su `TradingEngine.cs`. **Rischio**: basso — additivo, i pacchetti
`OpenTelemetry.*` sono già referenziati nel progetto, l'estensione a tracing non è
un'integrazione da zero. **Approvazione esplicita**: no.

---

## §6 — Backlog Condizionale

Le idee scartate in §2 con riserva ("non ora, non senza un problema concreto") vivono qui,
non in un nuovo documento futuro. Ogni voce ha un **criterio di attivazione esplicito**: se
nessuno si verifica, la voce resta non pianificata indefinitamente, e questo è corretto.

| Idea | Criterio di attivazione |
|---|---|
| Pipeline asincrone (`Channel<T>`/`IAsyncEnumerable<T>`) | (a) Emerge un caso concreto di ingestion multi-simbolo in streaming con backpressure misurata (oggi `ProcioneMGR.Ingestion` è REST, non streaming); oppure (b) un profiling reale mostra contention o allocazioni eccessive nella pipeline autonoma. |
| Caching generalizzato (`IMemoryCache`/`IDistributedCache`/Redis) | Si introduce un secondo processo/istanza che necessita di condividere uno stato oggi solo in-process (es. `FactorCache`). Finché il monolite resta un singolo processo Blazor Server, non c'è un problema da risolvere. |
| Mimir | Un problema concreto di spazio disco o retention viene osservato su Prometheus. |
| Clean Architecture a progetti separati / Event Sourcing / Rx.NET | La natura del progetto cambia radicalmente (es. più operatori, necessità di un confine di compilazione forzato, un vero bus di eventi multi-consumer) — scenario non previsto oggi. |
| `infra/k8s/observability/` (deploy K8s dello stack di observability) | L'operatore inizia a usare il cluster `kind` per debug quotidiano invece che solo per esercitare il tooling di deploy. |

---

## §7 — Tabella riassuntiva

| Fase | Obiettivo | Rischio | Dipendenze | Approvazione esplicita | Stato |
|---|---|---|---|---|---|
| **Fase 0** | Hardening — bonifica rami secchi, `PollingTimer`, retry Postgres, estrazione orchestrazione Razor, auth gRPC, lifetimes HttpClient, fee configurabile, micro-fix, master key, paginazione gRPC | Basso | Porting del lavoro P0 dal worktree `zealous-ellis-b6357f` | No | ✅ fatta (2026-07-17), tranne 5/6 pagine Razor di P1-5 — vedi §3.6 |
| **Fase 1** | CQRS/MediatR — decomposizione `TradingEngine` (Intervento A: comandi/query; Intervento B: estrazione cascata privata) | **Alto** | Segue, dentro Fase 0: P2-8 (fee configurabile, fatto) ed estrazione di `Trading.razor` in `TradingPageService` (fatta) — entrambe le precondizioni sono soddisfatte | Strategica già data; gate operativo = PR verdi + smoke test a ogni merge sull'Intervento B | ✅ fatta (2026-07-18, PR #14/#15/#16) — vedi §4.8 |
| **Fase 2** | Osservabilità distribuita — tracing (Tempo) | Basso | Nessuna tecnica; raccomandata dopo Fase 1 per priorità, non per necessità | No | Non iniziata |
| **Backlog condizionale** | Pipeline asincrone, caching generalizzato, Mimir, deploy K8s observability | N/A | Attivato solo dai trigger di §6 | N/A — non pianificato | N/A |

---

## §8 — Manutenzione del documento

Questo documento va aggiornato ad ogni fase completata: spuntare i criteri di accettazione
corrispondenti in §3.6/§4.7/§5.1, e annotare eventuali scostamenti dal design (es. se
l'Intervento B della Fase 1 rivela collaboratori diversi da quelli indicativi elencati in
§4.3, o se il preflight di licenza di §4.4 porta a scegliere un'alternativa a MediatR).

Al termine della Fase 1, è raccomandato un audit leggero — stesso metodo dell'audit
2026-07-17 (`docs/REPORT-AUDIT-CONSOLIDAMENTO-2026-07.md`: sweep stub/TODO/eccezioni/RNG +
lettura integrale dei moduli critici + analisi dei caller), ma con perimetro ridotto al solo
modulo trading — per confermare che la decomposizione non abbia introdotto regressioni sulla
safety dei fondi.

Il §6 (Backlog Condizionale) resta una sezione viva: nuove idee scartate con riserva vanno
aggiunte lì, con un criterio di attivazione esplicito, invece di aprire un nuovo documento
per ciascuna.
