# 06 — GAP DI INTEGRAZIONE

Ogni gap: **descrizione · evidenza · impatto · severità · proposta**.

**Severità:** `Critical` (sicurezza o perdita di capitale) · `High` (rischio operativo o valore
perso rilevante) · `Medium` · `Low` · `Info`.

**Classi:** `security` · `ui-disconnect` · `missing-di` · `missing-endpoint` · `missing-contract` ·
`missing-event-wiring` · `missing-persistence` · `missing-scheduler` · `missing-engine-integration` ·
`missing-risk-gate` · `missing-safety-gate` · `missing-experiment-tracking` · `missing-reproducibility` ·
`missing-config` · `dead-code` · `duplicated-logic` · `inconsistent-naming` · `broken-flow`.

---

## Sommario

| ID | Titolo | Classe | Severità |
|---|---|---|---|
| **C-01** | Segreti reali in file tracciato da git | `security` | 🔴 **Critical** |
| **C-02** | `DriveProtectiveExits=true` contro la misura B3 | `missing-config` / `missing-safety-gate` | 🔴 **High** |
| **C-03** | Microstructure: modulo completo senza DI né UI | `missing-di` / `ui-disconnect` | 🟠 High |
| **C-04** | `JumpModel`: modello di regime mai cablato | `dead-code` / `missing-engine-integration` | 🟠 Medium |
| **C-05** | MV e Risk Parity non raggiungono l'operatività | `missing-contract` / `broken-flow` | 🟠 Medium |
| **C-06** | Sentiment→feature ML spento di default | `missing-config` | 🟡 Medium |
| **G-04** | Deriva dei 158 fattori Alpha158 non sorvegliata | `missing-engine-integration` | 🟡 Medium |
| **G-07** | `ICombinatorialPurgedCv` astrazione morta | `dead-code` | 🔵 Low |
| **G-08** | Determinismo incompleto: 3 `new Random(42)` cablati | `missing-reproducibility` | 🟡 Medium |
| **G-09** | `tools/PlatformExpand`: 5.848 righe di ricerca parallela | `duplicated-logic` | 🟡 Medium |
| **G-10** | `appsettings.json` reale non verificabile | — | 🔵 Info |
| **G-11** | Nessun punto unico di validazione dei dati in ingresso | `missing-contract` | 🟡 Medium |
| **G-12** | "LightGBM" è FastTree | `inconsistent-naming` | 🔵 Low |
| **G-13** | Nessun DTO fra UI e dominio | `missing-contract` | 🔵 Low |
| **G-14** | `events.proto` senza publisher né subscriber | `missing-event-wiring` / `dead-code` | 🔵 Low |
| **G-15** | Selezione feature per IC: rischio di leakage di selezione | `missing-risk-gate` | 🟠 **High (metodologico)** |
| **G-16** | `IncrementalIcGate` esiste ma non protegge `AlphaFactorFactory` | `missing-risk-gate` | 🟡 Medium |
| **G-17** | Risultati pairs senza persistenza dedicata | `missing-persistence` | 🔵 Low |
| **G-18** | Due serie di audit nella stessa cartella | — | 🔵 Info |

---

## 🔴 C-01 — Segreti reali in un file tracciato da git

**Classe:** `security` · **Severità:** Critical

**Descrizione.** Un backup datato di `appsettings.json` è entrato nell'indice git e contiene segreti
di produzione valorizzati.

**Evidenza.**
```
$ git ls-files | grep pre-audit
ProcioneMGR/appsettings.json.pre-audit-test-20260729-141448
```
Contenuto (valori **non** riprodotti, solo forma):

| Chiave | Forma | Significato |
|---|---|---|
| `Security:MasterKey` | 44 caratteri, valorizzato | base64 di 32 byte = **chiave AES-256** |
| `Trading:GrpcSharedSecret` | 44 caratteri, valorizzato | segreto condiviso fra guscio e motore |
| `ConnectionStrings:PostgresConnection` | contiene `Password=` | credenziali del database |

`.gitignore:34` esclude `ProcioneMGR/appsettings.json` ma **non** i backup datati — ed è
esattamente il caso che il commento a `.gitignore:24` dichiarava di voler coprire
(«I backup datati NON erano coperti: "appsettings.json.bak-20260726-015604" non corrisponde a *.bak»).
La regola è stata scritta per un pattern e il file ne usa un altro.

**Impatto.** La `MasterKey` è la chiave con cui `AesGcmEncryptionService` cifra le credenziali
exchange a riposo (`ExchangeCredentialCiphertext`). Chiunque abbia accesso alla storia del
repository può decifrarle. Il `GrpcSharedSecret` è l'unica autenticazione fra guscio e motore di
trading (`SharedSecretAuthInterceptor`).

**Severità: Critical** anche con repository privato: la superficie include ogni clone, fork, backup,
CI cache e chiunque abbia mai avuto accesso in lettura.

**Proposta.**
1. `git rm --cached ProcioneMGR/appsettings.json.pre-audit-test-20260729-141448` e commit.
2. Aggiungere a `.gitignore` un pattern che copra **la famiglia**, non il singolo caso:
   `ProcioneMGR/appsettings.json.*` (con eccezione esplicita per `.example`).
3. **Ruotare tutti e tre i segreti** — la rimozione dall'indice non basta, restano nella storia.
   - master key: ri-cifrare le credenziali exchange esistenti con la nuova chiave
   - segreto gRPC: aggiornare entrambi i Secret K8s (`scripts/k8s-trading-secret.ps1`,
     `k8s-ui-secret.ps1` — **leggere gli script per i nomi delle chiavi, non inventarli**)
   - password Postgres
4. Purgare la storia (`git filter-repo`) o, se il costo è accettabile, considerare che la rotazione
   rende la storia innocua.
5. Verificare anche `infra/k8s/trading-config.env` e `ui-config.env`, tracciati.

> Segnalazione già presente in memoria dal 2026-07-29 come **non risolta**: questo audit la conferma
> ancora aperta al 2026-08-08.

---

## 🔴 C-02 — `DriveProtectiveExits` di default contraddice la decisione misurata

**Classe:** `missing-config` + `missing-safety-gate` · **Severità:** High (Critical a feed acceso)

**Descrizione.** L'invariante documentata dice `false`; il codice dice `true`.

**Evidenza.**

| Fonte | Valore |
|---|---|
| `CLAUDE.md` regola 7 | «`DriveProtectiveExits = false` … non sono sviste, sono risultati di misure. Non "correggerli"» |
| `docs/REPORT-B3-EXITLAG-2026-07-28.md` | uscire al tocco è **peggio** in 24/24 configurazioni |
| `Services/MarketData/RealtimeMarketDataModels.cs:117` | `public bool DriveProtectiveExits { get; set; } = true;` |
| `ProcioneMGR/appsettings.json.example` | `MarketData:Realtime:DriveProtectiveExits = true` |
| `Services/Regime/LaneRegimeRouter.cs:32` | commento che cita `DriveProtectiveExits` accanto a «Default FALSE» |

**Perché oggi non esplode.** `MarketData:Realtime:Enabled = false`: il feed non parte, quindi nessun
tick arriva e il flag è inerte. `TradingEngine.cs:645` implementa correttamente il bivio
osserva/decide.

**Impatto.** Il giorno in cui qualcuno accende il feed real-time — operazione presentata come
additiva e reversibile in `/admin/protections` — **eredita in automatico l'assetto che la misura ha
bocciato**, e nulla nella UI lo avverte. È precisamente la classe di difetto che la piattaforma ha
già combattuto: un interruttore che sembra innocuo e cambia il comportamento di uscita dalle
posizioni.

**Proposta.**
1. `RealtimeMarketDataModels.cs:117` → `public bool DriveProtectiveExits { get; set; }` (default `false`),
   con commento che cita il report B3.
2. `appsettings.json.example` → `false`.
3. In `/admin/protections`, mostrare accanto all'interruttore l'esito della misura B3 (una riga:
   «misurato 2026-07-28: guidare le uscite dai tick peggiora in 24/24 configurazioni»).
4. Aggiungere un test che **asserisca il default**, così la regola 7 smette di vivere solo in un `.md`.

> ⚠️ Non posso verificare il valore effettivo in `appsettings.json` (gitignored, assente dal
> worktree). Se lì è già `false`, il rischio operativo attuale è nullo — ma il **default del codice**
> resta sbagliato per chiunque parta da zero o usi l'example. Vedi Q3 in `09_OPEN_QUESTIONS.md`.

---

## 🟠 C-03 — Microstructure: modulo completo, testato, irraggiungibile

**Classe:** `missing-di` + `ui-disconnect` · **Severità:** High (valore perso)

**Evidenza.**
- `Services/Microstructure/` — 6 file, 1.166 righe
- `grep "Microstructure" ProcioneMGR/Program.cs` → **nessun risultato**
- `grep -rl "Microstructure" ProcioneMGR/Components/` → **nessun risultato**
- Test presenti: `IncrementalIcGateTests`, `TapeAggregatorTests`, `OrderFlowImbalanceTests`,
  `MicrostructureParserTests`, `BinanceDumpDownloaderTests`
- Unico consumatore: `tools/PlatformExpand/Program.cs`

**Impatto.** `IncrementalIcGate` (467 righe) risponde alla domanda «questo fattore aggiunge IC
**oltre** a quelli che ho già?». Con `AlphaFactorFactory` che offre 158+ candidati, è esattamente il
gate anti-ridondanza che manca (vedi G-16). OFI e tape sono le uniche feature di microstruttura
disponibili, e la ricerca D3 (2026-07-28) le aveva misurate.

**Proposta (scelta esplicita fra due).**
- **(A) Integrare** — registrare in DI, aggiungere `MicrostructureIngestionStage` alla pipeline,
  persistere `TapeBar` in una tabella, esporre `IncrementalIcGate` in `/feature-selection` come
  filtro sui candidati. Costo stimato: medio. Valore: alto (il gate serve subito).
- **(B) Eliminare** — spostare sotto `tools/` dichiarandolo esplicitamente codice di ricerca CLI,
  e togliere l'illusione che faccia parte della piattaforma.

Non fare nulla è la sola opzione senza valore: costa manutenzione e non rende.

---

## 🟠 C-04 — `JumpModel`: modello di regime mai cablato

**Classe:** `dead-code` + `missing-engine-integration` · **Severità:** Medium

**Evidenza.** `Services/Regime/JumpModel.cs` (288 righe). Riferimenti in produzione: **zero**.
Unico consumatore: `ProcioneMGR.Tests/JumpModelTests.cs`. `JumpModelFit` non è referenziato neanche
dai test.

**Cosa fa.** Clustering di stati con **penalità λ sulle transizioni**: con λ=0 degenera in K-means,
con λ>0 produce regimi più persistenti. I test verificano proprio che
`jumpTransitions < kmeansTransitions` — cioè che risolve il difetto noto del K-means puro (regimi
che sfarfallano).

**Impatto.** Il rilevamento in esercizio (`RegimeDetector`, K-means) ha il problema che `JumpModel`
risolve. La ricerca è stata fatta, validata, e non raccolta.

**Proposta.** Introdurre `IRegimeModel` con due implementazioni (`KMeansRegimeModel` = attuale,
`JumpRegimeModel`), selezionabili da `MarketRegime:Model` (default: `KMeans`, comportamento
invariato). Misurare in parallelo per un ciclo, poi decidere. In alternativa, eliminare il file
dichiarando la ricerca chiusa in negativo — ma i test dicono il contrario.

---

## 🟠 C-05 — Mean-Variance e Risk Parity non raggiungono l'operatività

**Classe:** `missing-contract` + `broken-flow` · **Severità:** Medium

**Evidenza.**

| Optimizer | Consumatori |
|---|---|
| `HierarchicalRiskParityOptimizer` | `PortfolioOptimization.razor` **+ `Stages/DecisionStages.cs:90`** |
| `MeanVarianceOptimizer` | `PortfolioOptimization.razor` |
| `RiskParityOptimizer` | `PortfolioOptimization.razor` |

`DecisionStages.cs:19` inietta `HierarchicalRiskParityOptimizer` **come tipo concreto**, non come
`IPortfolioOptimizer`: la scelta dell'allocatore è cablata nel codice.

**Impatto.** `/portfolio` mostra quattro allocazioni (Max Sharpe, Min Var, ERC, HRP) e non offre
alcun modo di applicarne una. L'operatore fa un'analisi che non può diventare una decisione, e
l'unico allocatore che conta è deciso dallo sviluppatore, non da chi guarda i numeri.

**Proposta.**
1. `EnsembleAssemblyStage` dipenda da `IPortfolioOptimizer` risolto per **nome** da configurazione
   (`Pipeline:PortfolioOptimizer`, default `HRP` ⇒ comportamento invariato).
2. Esporre la scelta in `/pipeline` accanto agli altri parametri di stage.
3. In `/portfolio`, aggiungere «usa questa allocazione nel prossimo run» che scrive quel parametro.
   **Non** un percorso diretto verso l'esecuzione: l'allocazione deve comunque passare per pipeline,
   gate e `PipelineApplier`.

---

## 🟡 C-06 — Sentiment come feature ML spento di default

**Classe:** `missing-config` · **Severità:** Medium

**Evidenza.** `AlphaFactorFactory.cs:70` — il ramo `_prototypesWithSentiment` è attivo solo se
`_sentimentOptions?.CurrentValue.EnableMlFeature == true`; `appsettings.json.example` →
`Sentiment:EnableMlFeature = false`.

**Impatto.** La catena esiste ed è testata (`SentimentFeatureFactorTests`,
`SentimentAlphaFactorTests`), ma nessun modello ML vede mai il sentiment.

**Nota di merito.** Lo spegnimento ha una ragione tecnica reale: il sentiment è disponibile **solo
dal momento in cui si è iniziato a raccoglierlo**, quindi un backtest lungo avrebbe un buco iniziale
che falserebbe il confronto.

**Proposta.** Non accendere alla cieca. Rendere la limitazione esplicita: `DataAvailability` deve
dichiarare da quando il sentiment esiste, e la feature va abilitata **solo** su finestre coperte.
Poi accendere e misurare l'IC incrementale (idealmente con `IncrementalIcGate` — vedi C-03).

---

## 🟡 G-04 — La deriva dei fattori Alpha158 non è sorvegliata

**Classe:** `missing-engine-integration` · **Severità:** Medium

**Evidenza.** `Services/Alpha/FactorDriftMonitor.cs:105`:
> «Solo i fattori scritti a mano, non il catalogo Alpha158: 158 fattori × N serie × finestre…»

**Impatto.** I fattori Alpha158 sono nei prototipi (`AlphaFactorFactory.cs:60`) e possono finire nei
modelli in esercizio, ma **se il loro IC si spegne nessuno se ne accorge**. La sorveglianza copre 8
fattori su 166.

**Proposta.** Sorvegliare i soli fattori Alpha158 **effettivamente selezionati** da un modello attivo
(tipicamente pochi), invece di tutti e 158. Il costo diventa proporzionale all'uso, non al catalogo.

---

## 🔵 G-07 — `ICombinatorialPurgedCv` è un'astrazione morta

**Classe:** `dead-code` · **Severità:** Low

**Evidenza.** L'interfaccia è dichiarata in `Validation/CombinatorialPurgedCv.cs` ma **non è
registrata in DI**, e i consumatori (`OptimizationEngine`, `BacktestOverfitting`) usano la classe
concreta.

**Proposta.** Rimuovere l'interfaccia, oppure registrarla e usarla. Un'interfaccia che nessuno
risolve è un falso segnale di estensibilità.

---

## 🟡 G-08 — Determinismo incompleto

**Classe:** `missing-reproducibility` · **Severità:** Medium

**Evidenza.** I seed sono pervasivi e ben scelti (`GeneticAlphaMiner.Seed=42`, `EventStudy.Seed=42`,
`IncrementalIcGate.Seed=20260728`, `DtwPatternAnalysisService` `new Random(20260727)` con commento
«deterministico come tutto il resto della piattaforma»). **Ma in tre punti la sorgente casuale è
cablata e ignora il seed configurato:**

| File | Riga |
|---|---|
| `ML/MlpReturnPredictor.cs` | 234 → `new Random(42)` (mentre il costruttore accetta `seed`) |
| `ML/RegressionPredictorBase.cs` | 109 → `new Random(42)` |
| `ML/StackedReturnPredictor.cs` | 382 → `new Random(42)` |

**Impatto.** Chi cambia seed per stimare la **varianza** di un modello (pratica corretta e necessaria
prima di credere a un risultato) non cambia quei rami: la varianza misurata è sottostimata.
Inoltre non esiste un **seed di run globale**: la riproducibilità è per componente, non per
esperimento.

**Proposta.**
1. Filtrare il `seed` del costruttore in tutti i rami dei tre file.
2. Introdurre un `RunSeed` nel `PipelineContext` e in `ExperimentRun`, propagato a tutti i
   componenti stocastici e **registrato nell'artefatto** ⇒ un run diventa riproducibile per intero.

---

## 🟡 G-09 — `tools/PlatformExpand`: ricerca parallela alla pipeline

**Classe:** `duplicated-logic` · **Severità:** Medium

**Evidenza.** `tools/PlatformExpand/Program.cs` — **5.848 righe in un solo file**, il singolo file
più grande del repository. Riusa i servizi ma orchestra fasi di ricerca (`huntdense`, `voloverlay`,
`volsingle`, … citate nei doc-comment) **in parallelo** a `PipelineEngine`.

**Impatto.** Due modi di fare ricerca con gate potenzialmente diversi. I risultati citati nei
commenti del codice di produzione (es. l'onestà di `VolatilityScaler`) vengono **da qui**, non dalla
pipeline: se i due divergono nei costi o nei gate, i numeri non sono confrontabili.

**Proposta.** Non riscrivere. Estrarre i **gate** condivisi (già in parte fatto con `NullTwinJudge`,
«unico punto di policy per pipeline e tool CLI» — `Program.cs:159-161`) e applicare lo stesso
principio a `PowerCheck`, `DSR`/`EffectiveTrials` e alla contabilità dei costi. Spezzare il file per
fase, senza cambiarne la logica.

---

## 🔵 G-10 — Configurazione effettiva non verificabile

**Classe:** — · **Severità:** Info

`ProcioneMGR/appsettings.json` è gitignored (correttamente) e **assente da questo worktree**. Tutti i
default citati in questo audit vengono da `appsettings.json.example` e dai default di classe.
Lo stato **runtime** può differire. Vedi Q3.

---

## 🟡 G-11 — Nessun punto unico di validazione dei dati in ingresso

**Classe:** `missing-contract` · **Severità:** Medium

**Evidenza.** La normalizzazione è locale a ciascun consumatore: `ReturnMatrixBuilder`,
`PairsCandleAligner`, `FeatureNormalizer`, `RollingOps`. Non esiste un `IDataQualityGate`.

**Impatto.** Assunzioni implicite (assenza di buchi, allineamento dei timestamp, gestione dei
delisting) sono verificate in modo disomogeneo. `DataAvailability` copre la **quantità**, non la
**qualità**.

**Proposta.** Un `DataQualityReport` prodotto da `DataIngestionStage` e allegato come artefatto:
buchi, duplicati, candele a volume nullo, salti di timestamp. Non blocca — **dichiara**
(fail-open sulla diagnostica, regola 4).

---

## ~~G-12 — "LightGBM" è in realtà FastTree~~ ❌ **RITIRATO il 2026-08-08 — reperto errato**

**Verificato aprendo il file durante l'esecuzione della Fase 2:**
`GradientBoostingReturnPredictor.BuildPipeline` chiama `mlContext.Regression.Trainers.LightGbm(...)`
e il progetto referenzia il pacchetto `Microsoft.ML.LightGbm` 5.0.0. **È vero LightGBM**; anche
`MlNetTreeExtractor` gestisce esplicitamente `LightGbmRegressionModelParameters` per lo SHAP.
L'etichetta UI «Gradient Boosting (LightGBM)» è corretta. L'errore era nel deep dive (documento 20),
che aveva dedotto "FastTree" dal nome della classe base senza aprire il `BuildPipeline`.
Nessuna azione richiesta.

---

## 🔵 G-13 — Nessun DTO fra UI e dominio

**Classe:** `missing-contract` · **Severità:** Low

Le pagine consumano direttamente i modelli di dominio (`BacktestResult`, `TradingPerformance`,
`EnsembleConfiguration`…). Accoppiamento accettabile in Blazor Server, ma cambiare un modello rompe
il markup. I page service **mitigano già** dove esistono (espongono viste dedicate come
`BacktestConfigSnapshot`, `MlConfigSnapshot`, `LaneStoryInfo`).

**Proposta.** Estendere il pattern dei page service alle pagine 🟡 quando ne acquisiranno azioni.

---

## 🔵 G-14 — `events.proto` senza publisher né subscriber

**Classe:** `missing-event-wiring` · **Severità:** Low

`ProcioneMGR.Contracts/Protos/events.proto` esiste; non risulta un produttore o consumatore attivo.
La comunicazione fra host è gRPC richiesta-risposta + stato condiviso su Postgres.

**Proposta.** Rimuoverlo o dichiararlo esplicitamente "riservato per uso futuro" nel file stesso.

---

## 🟠 G-15 — Selezione feature per IC: rischio di leakage di selezione

**Classe:** `missing-risk-gate` · **Severità:** High (metodologico)

**Descrizione.** `IcFeatureSelector` sceglie i fattori per IC. Se la selezione avviene **su tutto il
campione** e poi il modello è validato con purged CV, il leakage è già avvenuto **prima** della CV:
i fold di test hanno contribuito a scegliere le feature.

**Stato della verifica.** `AuditCvLeakageTests` esiste e presidia il leakage di CV. **Non ho
verificato** se la selezione IC è confinata alla finestra di training di ciascun fold nel percorso
`MlModelTrainingStage` / `MlLabService`. → **Q6 in `09_OPEN_QUESTIONS.md`.**

**Impatto se confermato.** Ogni Sharpe prodotto dalla catena con selezione IC sarebbe ottimistico, e
il DSR non lo correggerebbe (il DSR corregge le prove multiple, non il leakage).

**Proposta.** Test esplicito: selezionare le feature **dentro** il fold e verificare che le feature
scelte cambino fra fold. Se oggi non è così, spostare la selezione dentro il ciclo di CV.

---

## 🟡 G-16 — `IncrementalIcGate` non protegge la fabbrica dei fattori

**Classe:** `missing-risk-gate` · **Severità:** Medium

Con 158+ fattori candidati, molti sono **ridondanti fra loro** (stessa informazione, orizzonti
vicini). `IcFeatureSelector` filtra per IC assoluto e IR, ma **non** per contributo incrementale.
Il gate che serve esiste — `IncrementalIcGate` — ed è nell'isola C-03.

**Proposta.** Legato a C-03(A): esporlo come filtro opzionale in `/feature-selection` e in
`FeatureEngineeringStage`.

---

## 🔵 G-17 — Risultati di pairs trading senza persistenza dedicata

**Classe:** `missing-persistence` · **Severità:** Low

I risultati di `PairsBacktestEngine` vivono negli artefatti di pipeline o nella sessione della
pagina; non c'è una tabella di coppie candidate con hedge ratio e stato di cointegrazione nel tempo.

**Proposta.** Se il pairs diventa una linea operativa (e la ricerca dice che il market-neutral è la
direzione promettente), serve una `PairCandidate` persistita con storico.

---

## 🔵 G-18 — Due serie di audit nella stessa cartella

**Classe:** — · **Severità:** Info

`docs/audit/` contiene ora la serie del 2026-08-04 (`00_INDEX` … `13_DEEP_DIVE_CODE`) e questa
(`00_MASTER_INDEX`, `01_PROJECT_MAP`, …). Numerazioni sovrapposte, contenuti diversi.
**Proposta:** vedi Q1.

---

## Cosa NON è un gap (e va difeso dalle "correzioni")

Questi punti **sembrano** disconnessioni e non lo sono. Elencarli serve a impedire che un futuro
intervento li "aggiusti".

| Apparente gap | Perché è corretto così |
|---|---|
| GARCH non entra nel position sizing | non validato per quell'uso; `VolatilityScaler` lo dichiara nel codice |
| `RegimeRouting:DriveDecisions=false` | risultato di misura (regola 7 di `CLAUDE.md`) |
| `Fleet:Enabled=false` + `DryRun=true` | in AF2a il braccio esecutivo non esiste per progetto |
| `Committee:Enabled=false` | doppio gate deliberato (serve anche `Fleet:UseCommittee`) |
| `Carry:Enabled=false` | forward test opt-in; `CarryMode` esclude Live **a livello di tipo** |
| `SafetyChecker` non iniettabile | è il punto: statico e puro (regola 1) |
| Nessun endpoint REST | la UI parla ai servizi via DI; `/health` è l'unica eccezione, anonima per le probe K8s |
| `PositionCloser` salta l'anti-spam n.6 | chiudere riduce il rischio; l'anti-spam presidia l'apertura |
| `EnsembleRebalanceWorker` solo nel monolite | è uno scrittore: un solo host per scrittore |
| Migrazioni caricate per nome | evita un ciclo di progetti; dichiarato in `DbInitializer` |
