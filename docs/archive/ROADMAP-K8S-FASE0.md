# ProcioneMGR — Roadmap Kubernetes, Fase 0: Fondamenti e Valutazione Preparatoria

**Da un PDF generico ("Da Monolito a Cloud-Native... Roadmap Pragmatica per l'Orchestrazione di
ProcioneMGR su Kubernetes") a un piano ancorato al codice reale** — la Fase 0 riscritta contro lo
stato effettivo della piattaforma (non contro un monolite ipotetico), con vincoli di sicurezza sul
trading reale che il documento originale non conosceva.

---

## 0. Premessa: perché questo documento non è un riassunto del PDF fornito

Il PDF fornito dall'utente è una consulenza generica prodotta da un altro LLM (Qwen), corretta come
teoria di modernizzazione monolite→Kubernetes ma **non verificata contro il codice di ProcioneMGR**.
Tre assunzioni del PDF sono sbagliate per questo repo, e vanno corrette prima di poter usare il resto
del documento come riferimento:

| Assunzione del PDF | Realtà verificata in ProcioneMGR |
|---|---|
| Frontend React separato da costruire (Fase 5 del PDF) | **Blazor Server** — 93 file `.razor`, nessun `package.json`, nessun frontend separato pianificato o esistente |
| Auth stateless via JWT (Fase 1 del PDF) | **Cookie auth ASP.NET Core Identity** (`Program.cs:38-43` `AddAuthentication(...).AddIdentityCookies()`, `AddIdentityCore<ApplicationUser>` a riga 395) — e comunque Blazor Server è **intrinsecamente stateful**: ogni sessione utente è un circuito SignalR pinnato a una specifica istanza server, indipendentemente da come si gestisce l'auth |
| Monolite generico da scomporre, "punto di fallimento singolo" implicito | Sistema maturo e già analizzato (`docs/REPORT-ANALISI-RICOSTRUZIONE-2026-07.md`, `docs/ROADMAP-QLIB.md`, 640+ test), con worker in-process che **muovono denaro reale** dietro safety-gate hard-coded — la scomposizione non è un esercizio astratto di refactoring, è un cambio di topologia che tocca direttamente il rischio operativo |

Di conseguenza, questa Fase 0 **non ripete** la struttura generica del PDF (analisi statica generica
+ cluster di staging + E2E + VPA + scelta piattaforma + naming) ma la **reinterpreta** voce per voce
sopra i fatti reali del repo, aggiungendo una sezione che il PDF non ha affatto: i vincoli di
sicurezza sul trading che nessuna topologia Kubernetes futura può violare (Sezione 2).

**Cosa resta valido del PDF**: il principio di fondo — non toccare funzionalità in Fase 0, solo
osservare, testare, misurare e decidere — e la lista delle attività (analisi statica, staging
K8s, baseline E2E, profiling risorse, naming). È l'esecuzione di ciascuna che qui viene riscritta.

---

## 1. Mappatura architetturale per Strangler Fig

Il PDF suggerisce di identificare i "nodi ideali" per l'introduzione di microservizi tramite analisi
statica generica. `docs/REPORT-ANALISI-RICOSTRUZIONE-2026-07.md` §1.1 ha già fatto la mappatura
namespace→interfacce→test; questa sezione la **estende** con la lente specifica di Kubernetes:
quali componenti possono girare su N pod, quali devono girare su esattamente 1, quali sono candidati
naturali a diventare servizi separati.

### 1.1 Singleton non-negoziabili (mai >1 replica senza leader election)

Tutti i seguenti sono `IHostedService`/`BackgroundService` registrati come singleton nel processo
unico oggi. Se il futuro Deployment K8s li facesse girare su più pod identici (scaling orizzontale
o anche solo una rolling update mal configurata con `maxSurge`), si otterrebbero **istanze duplicate
dello stesso worker che agiscono in parallelo sugli stessi dati/ordini**:

| Worker | File | Rischio se duplicato |
|---|---|---|
| `TradingWorker` | `Services/Trading/TradingWorker.cs` | Ordini duplicati sulla stessa corsia |
| `ExecutionWorker` | `Services/Trading/ExecutionWorker.cs` | Slicing TWAP/VWAP duplicato → doppio notional eseguito |
| `EnsembleRebalanceWorker` | `Services/Ensemble/EnsembleRebalanceWorker.cs` | Ribilanciamento capitale concorrente e incoerente |
| `PromotionWorker` | `Services/Trading/PromotionWorker.cs` | Doppia promozione Paper→Testnet della stessa corsia |
| `PipelineSchedulerWorker` | `Services/Pipeline/PipelineSchedulerWorker.cs` | Run di pipeline duplicati (Cronos) |
| `RegimeRetrainingWorker` | `Services/Regime/RegimeRetrainingWorker.cs` | Retrain concorrente, race su scrittura modello |
| `FeatureDriftWorker` | `Services/Monitoring/Drift/FeatureDriftWorker.cs` | Alert duplicati (innocuo ma rumoroso) |
| `LlmSupervisorWorker` | `Services/Llm/LlmSupervisorWorker.cs` | Doppia chiamata API Anthropic (costo, non rischio) |
| `MetricsCollector` | `Services/Observability/MetricsCollector.cs` | Metriche incoerenti tra repliche |
| `MarketDataSyncWorker` | `Services/Ingestion/MarketDataSyncWorker.cs` | Ingestion ridondante (idempotente su OHLCV, rischio basso) |

`TradingWorker`/`ExecutionWorker`/`EnsembleRebalanceWorker` sono inoltre registrati **per corsia**
(`Program.cs:285-334`, `for (var lane = 0; lane < TradingLanes.Count; lane++)` con
`AddKeyedSingleton`) — oggi 3 corsie fisse (`Services/Trading/TradingLanes.cs`). Non sono 3 processi
indipendenti isolabili facilmente: vivono nello stesso `IServiceProvider` di un unico processo.

**Implicazione per il design K8s (da decidere in Fase 2, non ora)**: il primo Deployment K8s per
questi worker dovrà avere `replicas: 1` esplicito, senza HPA, con eventualmente un
`PodDisruptionBudget` che *non* forzi mai `maxUnavailable: 0` insieme a `maxSurge > 0` sullo stesso
rolling update (altrimenti per una finestra temporanea giri 2 pod contemporaneamente). L'alternativa
più robusta — leader election (`client-go` leaderelection o equivalente) — è rimandabile: a scala
solo-dev con 3 corsie, `replicas: 1` con riavvio rapido via Deployment è sufficiente e molto più
semplice.

### 1.2 Stateful-per-design (richiede sticky session o redesign)

La UI Blazor Server stessa: ogni utente ha un circuito SignalR con stato lato server (component
tree, scoped DI). Su un Deployment con >1 replica, senza **session affinity** a livello di
Ingress/Service (`sessionAffinity: ClientIP` o cookie-based affinity sull'Ingress controller), un
reconnect del client a un pod diverso da quello originario causa la perdita del circuito
(l'utente vede l'app "congelata" o deve ricaricare). Il PDF assumeva un frontend React stateless
proprio per evitare questo problema — non è la realtà di questo repo, quindi va gestito
esplicitamente: o sticky session (soluzione a breve termine, compatibile con `replicas: 1` visto
che comunque i worker di trading lo richiedono) o migrazione futura a Blazor WebAssembly/Auto
(fuori scope Fase 0, va solo annotato come opzione a lungo termine).

### 1.3 Candidati naturali a estrazione futura (fuori scope Fase 0, solo identificati)

- **Tool CLI già disaccoppiati** (`tools/PlatformExpand`, `tools/StrategyHunter`, `tools/DbBackup`,
  `tools/FuturesVerify`, `tools/SpotVerify`, `tools/TriggerVerify` — 6 progetti, non nella
  `ProcioneMGR.sln` principale, già eseguiti oggi come processi `dotnet run` separati): candidati
  diretti a diventare `Job`/`CronJob` Kubernetes in una fase successiva, senza alcun redesign —
  sono già processi indipendenti.
- **`MarketDataSyncWorker`** (`Services/Ingestion/MarketDataSyncWorker.cs`): legge da exchange
  esterni e scrive solo su Postgres, nessun side-effect di trading. È il worker con il profilo di
  rischio più basso se duplicato (idempotente) ed è il candidato più promettente a diventare un
  servizio indipendente dal resto della UI/trading in una fase di scomposizione futura (Strangler
  Fig: si isola per primo, si fa puntare al nuovo servizio, si ritira il codice in-process).

Questa sezione **non decide** di estrarre nulla ora — la Fase 0 del PDF è esplicitamente "nessuno
sviluppo di nuove funzionalità". Serve solo a identificare l'ordine naturale per quando (Fase 1+)
si deciderà di scomporre.

---

## 2. Vincoli di sicurezza non negoziabili (sezione assente nel PDF, necessaria qui)

Il PDF generico non menziona mai rischio finanziario perché non sapeva che ProcioneMGR fa trading
con denaro reale. Questi vincoli, verificati nel codice, **devono sopravvivere identici** a
qualunque topologia Kubernetes futura — sono il criterio con cui va valutata ogni proposta delle
fasi successive:

- **`TradingMode` enum** (`Services/Trading/TradingModels.cs:6-11`): `Paper` | `Testnet` | `Live`.
- **Sentinel `MlChampion` Live-forbidden** (`Services/Trading/TradingEngine.cs:61` definisce
  `ChampionStrategyName = "MlChampion"`; righe 519-522: `if (_state.Mode == TradingMode.Live)` →
  eccezione con messaggio esplicito *"CONFINE DI SICUREZZA: il Champion del registry non può MAI
  alimentare una lane Live. Consentito solo Paper/Testnet."*).
- **Conferma manuale obbligatoria per Live** (`Services/Trading/SafetyChecker.cs:79`: ordine Live
  rifiutato se `!order.ManuallyConfirmed`; riga 101: leva oltre `MaxLeverageAllowed` rifiutata).
- **`appsettings.json.example`**: `Trading.Safety.RequireManualConfirmationForLive = true`,
  `Trading.LiveExecution.Enabled = false` di default; `PromotionEvaluator` — promozione automatica
  **solo** Paper→Testnet, mai a Live (commento esplicito nel file: *"MAI a Live (Testnet→Live resta
  manuale)"*).

**Regola derivata per Kubernetes**: nessuna topologia futura (HPA, rolling update, DaemonSet,
canary) può introdurre una finestra temporale in cui più di un'istanza di `TradingWorker`/
`ExecutionWorker`/`PromotionWorker` per la stessa corsia sia attiva contemporaneamente. Questo
vincolo è più stringente del generico "HPA impreciso per carichi non-CPU-bound" citato dal PDF
(Sezione 4 del PDF originale, su HPA/metriche custom): qui non è una questione di efficienza, è
una questione di correttezza — due esecuzioni concorrenti del motore di trading non sono "uno spreco
di risorse", sono un rischio diretto di doppio ordine o doppia promozione. Ogni fase successiva
(specialmente la Fase 2 "Orchestrazione" e la Fase 4 "Autoscaling" del PDF originale) deve trattare
i worker di trading come **esplicitamente esclusi** da qualunque meccanismo di autoscaling
orizzontale, a differenza della UI Blazor (che potrebbe, in futuro e con sticky session, scalare a
più repliche per il solo traffico HTTP).

---

## 3. Ambiente di staging Kubernetes locale

**Decisione presa**: cluster locale con **kind** (Kubernetes IN Docker), non un cluster cloud.
Motivazione: nessuna infrastruttura cloud esiste oggi per questo progetto, lo sviluppo è solo-dev, e
l'esposizione a denaro reale è oggi limitata a Bitget Demo/Testnet (Binance Futures inutilizzabile
per l'utente da MiCA in EU/Italia dal 2026-07-01 — vedi memoria "Futures MiCA restriction"). Non c'è
urgenza né giustificazione di costo per impegnare un cluster cloud prima di validare l'approccio.

**Perché kind e non k3d/minikube**: la macchina di sviluppo ha già Docker Desktop installato e
funzionante (usato oggi da Testcontainers nei test di integrazione — `ProcioneMGR.Tests/
Infrastructure/PostgresFixture.cs`, `postgres:16-alpine`/`postgres:18`). `kind` riusa lo stesso
Docker daemon senza richiedere un secondo hypervisor/runtime come farebbe `minikube` con alcuni
driver, ed è lo strumento più usato per CI/staging "vero Kubernetes API-compatibile" a costo zero.
`k3d` (basato su k3s) è un'alternativa valida ma introduce una distribuzione K8s alleggerita (k3s)
invece dell'upstream puro — per uno staging che deve "replicare fedelmente" un futuro ambiente
enterprise (come richiede il PDF), `kind` è la scelta più conservativa.

**Cosa serve in questa fase**:
- File di configurazione kind minimale (1 control-plane + 1-2 worker node, sufficiente a validare
  scheduling/probes/ConfigMap/Secret — non serve topologia multi-nodo complessa per uno staging
  solo-dev).
- Namespace dedicato `procionemgr-staging`, separato da eventuali esperimenti futuri.
- `kubectl` configurato contro il context kind.

**Cosa NON serve ancora** (rimandato a Fase 1/2 del PDF, quando esisterà un'immagine Docker da
distribuire): Ingress controller, cert-manager, secret store esterno (Vault/Key Vault — oggi i
segreti sono già gestiti correttamente fuori da git, vedi `.gitignore` righe 26-35, e non c'è ancora
nulla da montare in un pod). Installare questi componenti ora, prima che esista un solo Dockerfile,
sarebbe lavoro speso senza nulla da testare.

---

## 4. Baseline di test E2E per il sistema legacy

Il PDF chiede un sistema di test E2E come "linea di base immutabile" prima di ogni cambiamento
infrastrutturale. Verificato: **oggi questo non esiste**. `ProcioneMGR.Tests` ha ~640+ test xUnit,
tutti unit/integration a livello di servizio (inclusi test con Testcontainers su Postgres reale),
ma **zero copertura E2E della UI** — nessun bUnit, nessun Playwright, nessuna interazione
browser-driven testata (confermato in `docs/REPORT-ANALISI-RICOSTRUZIONE-2026-07.md`: *"UI Blazor
(nessun test bUnit/Playwright — atteso per Blazor Server)"*).

Questo è un gap diretto rispetto al principio del PDF, non un'osservazione a margine: senza una
baseline E2E, non c'è modo di dimostrare che una futura containerizzazione/deployment K8s non abbia
rotto silenziosamente un flusso critico della UI (login, apertura pagina Trading, avvio Pipeline).

**Proposta**: introdurre uno smoke test E2E minimale con **Playwright per .NET**
(`Microsoft.Playwright`, ecosistema xUnit già in uso — nessuna nuova toolchain JS da introdurre,
coerente con la scelta "C# puro" già seguita nel resto della piattaforma), su un piccolo insieme di
percorsi critici, non su copertura esaustiva:

1. Login (`ApplicationUser`/Identity) → redirect corretto post-auth.
2. `/trading` — pagina carica, mostra le 3 corsie senza eccezioni.
3. `/pipeline` — pagina carica, stato scheduler visibile.
4. `/admin/autonomy` — pagina carica (esiti drift persistiti, tabella DriftCheckResults).
5. `/metrics` — dashboard carica senza eccezioni (verifica indiretta che `MetricsCollector` sia up).

Questi 5 percorsi vanno eseguiti **contro il sistema legacy così com'è oggi** (avviato con
`scripts/run-postgres.ps1`, nessuna modifica), prima di qualunque containerizzazione, e devono
restare verdi identici quando (Fase 1+) l'app girerà in un container e poi su kind — è la prova che
la migrazione non ha cambiato comportamento osservabile.

---

## 5. Raccolta dati di risorse/performance

Il PDF suggerisce il Vertical Pod Autoscaler (VPA) in modalità "suggerimento" per 24-48h — non
applicabile ora: l'app non gira ancora su Kubernetes (nessun pod da osservare). L'equivalente
pre-containerizzazione è **`dotnet-counters`/`dotnet-trace`** puntati sul processo reale in
esecuzione oggi (`dotnet run -c Release` via `scripts/run-postgres.ps1`), durante una finestra di
osservazione che copra i pattern di carico reali della piattaforma, non solo idle:

- Uso normale della UI (navigazione tra pagine Blazor).
- Un run completo di `PipelineSchedulerWorker` (pipeline a 15 stadi — `Services/Pipeline/
  PipelineStageCatalog.cs` + `Services/Pipeline/Stages/*`, vedi anche `docs/ROADMAP-QLIB.md`).
- Uno sweep di `OptimizationEngine` (grid search — nella campagna di luglio 2026 fino a 62.568
  combinazioni, il carico CPU/memoria più pesante noto della piattaforma).
- Un training ML (`Services/ML/*ReturnPredictor`), specialmente `GradientBoostingReturnPredictor`
  (LightGBM) su una serie storica intera (~7,45M candele nel DB Postgres complessivo, ma un singolo
  training opera su una finestra symbol/timeframe molto più piccola).

L'obiettivo è ottenere high-water mark realistici di CPU/memoria da questi scenari per impostare
`resources.requests`/`resources.limits` iniziali nel primo manifest Kubernetes (Fase 1/2 del PDF,
quando esisterà il Dockerfile) — evitando sia la sovrastima (spreco) sia la sottostima (OOMKill
durante un training o uno sweep pesante, il caso peggiore possibile perché interromperebbe un
processo che potrebbe avere una posizione di trading aperta).

---

## 6. Scelta della piattaforma Kubernetes (decisione differita)

Il PDF chiede di scegliere tra AWS EKS, Azure AKS, GCP GKE o on-premise/OpenShift già in Fase 0.
Per questo progetto la scelta **va deliberatamente rimandata**: non esiste oggi alcuna infrastruttura
cloud in uso, il sistema è ancora in Demo/Testnet (nessuna urgenza operativa), e impegnarsi ora su un
provider specifico significherebbe scegliere in assenza di dati (nessuna Fase 1/2 completata che
dimostri quali servizi gestiti servano davvero — Key Vault? Application Insights? nessuno dei due è
usato oggi).

**Cosa si fissa ora**: partire in locale con kind (Sezione 3), che è per costruzione portabile
verso qualunque provider (manifest K8s standard, nessuna feature vendor-specific usata in questa
fase).

**Criteri da rivalutare dopo Fase 1-2** (non decisi ora, solo registrati per la decisione futura):
- **Costo**: rilevante per un progetto solo-dev — un cluster gestito (EKS/AKS/GKE) ha un costo
  fisso mensile del control plane oltre ai nodi; un VPS con k3s (Hetzner/DigitalOcean) è
  significativamente più economico a bassa scala.
- **Residenza dati / MiCA**: dato che la piattaforma opera in ambito EU/Italia con vincoli
  normativi già noti (memoria: "Futures MiCA restriction"), un provider con region EU (Azure
  West Europe, AWS eu-*, GCP europe-*, o un VPS EU-based) è preferibile quando si deciderà.
  Nota: la scelta della *piattaforma K8s* non è la scelta del *venue di trading* (che resta
  Bitget), ma dati potenzialmente sensibili (credenziali cifrate, storico posizioni) transitano
  comunque nel cluster.
- **Integrazione secret store**: se in futuro si adotta Azure Key Vault o AWS Secrets Manager, la
  scelta del provider cloud semplifica l'integrazione nativa (Secrets Store CSI Driver) — ma oggi
  i segreti sono già gestiti correttamente senza queste dipendenze (env var + `appsettings.json`
  gitignored), quindi non è un fattore bloccante.
- **Familiarità**: nessuna preferenza pregressa nota — da chiedere esplicitamente quando la
  decisione diventerà rilevante.

---

## 7. Convenzione di naming/versioning

Il PDF raccomanda una convenzione coerente per immagini Docker e servizi, utile fin da ora anche se
il primo Dockerfile arriverà solo in Fase 1 — fissarla in Fase 0 evita di doverla cambiare dopo.
A differenza di quando il PDF è stato scritto pensando a un repo generico, ProcioneMGR **è già** un
repository git tracciato (`origin` → `github.com/markcapitanelli/ProcioneMGR`, dal 2026-07-08), il
che rende disponibile fin da subito un identificatore stabile: lo SHA del commit.

**Proposta**:
- Immagine dell'app principale: `procionemgr-web:<git-sha-corto>` (es.
  `procionemgr-web:7385846`), mai `:latest` in ambienti diversi da locale/dev.
- Se in futuro `MarketDataSyncWorker` o i tool CLI verranno estratti (Sezione 1.3): stesso schema,
  `procionemgr-<componente>:<git-sha-corto>` (es. `procionemgr-ingestion:<sha>`).
- Namespace K8s: `procionemgr-{env}` (`procionemgr-staging` già fissato in Sezione 3;
  `procionemgr-prod` quando/se si arriverà a produzione su K8s).
- Label standard su ogni risorsa (convenzione Kubernetes raccomandata, non specifica del PDF):
  `app.kubernetes.io/name=procionemgr`, `app.kubernetes.io/component=<web|trading-worker|ingestion|...>`,
  `app.kubernetes.io/part-of=procionemgr`.

---

## Tabella riassuntiva: attività Fase 0

| # | Attività | Cosa cambia rispetto al PDF generico | Priorità |
|---|---|---|---|
| 1 | Mappatura Strangler Fig (Sezione 1) | Basata su worker reali già mappati, non analisi statica generica | P0 |
| 2 | Vincoli di sicurezza trading (Sezione 2) | Sezione intera assente nel PDF — specifica di questo dominio | P0 |
| 3 | Cluster staging locale kind (Sezione 3) | PDF assumeva ambiente enterprise cloud-first; qui locale, a costo zero | P0 |
| 4 | Baseline E2E Playwright (Sezione 4) | PDF assumeva test E2E già pensati per un frontend React; qui adattati a Blazor Server | P0 |
| 5 | Profiling risorse pre-K8s (Sezione 5) | VPA non applicabile senza pod; sostituito con dotnet-counters sul processo reale | P1 |
| 6 | Scelta piattaforma K8s | Deliberatamente **non decisa ora** (il PDF la chiedeva subito) | Differita |
| 7 | Naming/versioning (Sezione 7) | Ancorata al git SHA reale (repo non era nemmeno git-tracked quando l'analisi originale fu scritta) | P1 |

---

## Prossimo passo operativo

Le attività P0 (Sezioni 1-4) non richiedono decisioni ulteriori e possono partire nell'ordine: (1)
bootstrap del cluster kind locale — attività meccanica, nessun rischio; (2) scrittura della suite
Playwright dei 5 percorsi critici **contro il sistema legacy attuale**, prima di qualunque altra
modifica — è la rete di sicurezza per tutto ciò che segue. La Sezione 2 (vincoli di sicurezza) non è
un'attività da eseguire ma un **criterio di accettazione** da applicare a ogni proposta di design
delle fasi successive (Fase 1 "Modernizzazione Backend e Containerizzazione" del PDF originale, da
riscrivere con lo stesso metodo di questo documento quando si arriverà a quel punto). Il profiling
risorse (Sezione 5) può partire in parallelo, non ha dipendenze dalle altre attività.

Nessuna di queste attività modifica codice di produzione o comportamento della piattaforma — sono
tutte osservazione, test e infrastruttura locale, coerenti con il principio del PDF stesso: la Fase 0
"non comporta lo sviluppo diretto di nuove funzionalità".
