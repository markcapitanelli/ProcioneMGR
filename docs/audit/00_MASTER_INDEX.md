# 00 — MASTER INDEX (audit di ricostruzione)

> **Data audit:** 2026-08-08 · **Branch:** `claude/procionemgr-architecture-analysis-32b584`
> **Metodo:** lettura diretta del codice sorgente. Nessun file sorgente modificato, nessun servizio
> esterno contattato, nessun ordine inviato, nessuna credenziale letta in chiaro.

---

## ⚠️ Nota sulla coesistenza con l'audit precedente

`docs/audit/` conteneva **già** un audit del 2026-08-04 in 14 documenti (`00_INDEX.md` …
`13_DEEP_DIVE_CODE.md`). Questo audit usa nomi file **diversi**, quindi **nulla è stato
sovrascritto**. Conseguenza: nella stessa cartella convivono due numerazioni parallele.

| Serie | File | Scopo |
|---|---|---|
| **Serie A** (2026-08-04) | `00_INDEX.md`, `01_PROJECT_OVERVIEW.md`, … `13_DEEP_DIVE_CODE.md` | audit descrittivo, esportato nel notebook NotebookLM |
| **Serie B** (questo audit, 2026-08-08) | `00_MASTER_INDEX.md`, `01_PROJECT_MAP.md`, … `10_FILE_INVENTORY.md`, `project_map.json` | audit di **ricostruzione**: mappa file-per-file, gap di integrazione, blueprint |

→ Vedi `09_OPEN_QUESTIONS.md` §Q1 per la decisione da prendere sulla convivenza delle due serie.

---

## Indice dei documenti prodotti

| # | Documento | Contenuto |
|---|---|---|
| 00 | **00_MASTER_INDEX.md** (questo) | riassunto esecutivo, stato, criticità, prossimi passi |
| 01 | [01_PROJECT_MAP.md](01_PROJECT_MAP.md) | mappa navigabile: ogni file significativo con ruolo, layer, stato, gap |
| 02 | [02_ARCHITECTURE_AS_IS.md](02_ARCHITECTURE_AS_IS.md) | architettura **reale**, diagrammi Mermaid, DI, bootstrap, hosted services |
| 03 | [03_DOMAIN_QUANT_MACHINE.md](03_DOMAIN_QUANT_MACHINE.md) | la macchina quantitativa meccanismo per meccanismo |
| 04 | [04_ALGORITHMS_AND_MODELS.md](04_ALGORITHMS_AND_MODELS.md) | catalogo algoritmi/modelli con input, output, assunzioni, rischi |
| 05 | [05_UI_BACKEND_BINDING.md](05_UI_BACKEND_BINDING.md) | 32 pagine Blazor → servizi → engine, matrice di binding |
| 06 | [06_INTEGRATION_GAPS.md](06_INTEGRATION_GAPS.md) | **elenco dei gap** classificati per tipo e severità |
| 07 | [07_RECONSTRUCTION_BLUEPRINT.md](07_RECONSTRUCTION_BLUEPRINT.md) | architettura target + fasi 0-7 con acceptance criteria |
| 08 | [08_TRACEABILITY_MATRIX.md](08_TRACEABILITY_MATRIX.md) | matrice dominio → file → stato → gap → azione |
| 09 | [09_OPEN_QUESTIONS.md](09_OPEN_QUESTIONS.md) | ambiguità irrisolte e decisioni da prendere |
| 10 | [10_FILE_INVENTORY.md](10_FILE_INVENTORY.md) | inventario file-by-file per area |
| **20** | **[20_DEEP_DIVE_CODE_ANALYSIS.md](20_DEEP_DIVE_CODE_ANALYSIS.md)** | **secondo passaggio: lettura di merito del codice, verifica delle formule, 6 reperti** |
| **21** | **[21_FILE_BY_FILE_CATALOG.md](21_FILE_BY_FILE_CATALOG.md)** | tutti gli 838 file con la descrizione che il codice dà di sé |
| **22** | **[22_DOCS_INVENTORY.md](22_DOCS_INVENTORY.md)** | tutti i 119 documenti Markdown, per categoria |
| **23** | **[23_API_SURFACE_CORE.md](23_API_SURFACE_CORE.md)** | **superficie API del nucleo operativo**: trading, esecuzione, rischio, sicurezza, exchange |
| **24** | **[24_API_SURFACE_QUANT.md](24_API_SURFACE_QUANT.md)** | **superficie API della macchina quantitativa**: ML, alpha, validazione, regime, portafoglio, backtest |
| **25** | **[25_API_SURFACE_PLATFORM.md](25_API_SURFACE_PLATFORM.md)** | **superficie API di piattaforma e automazioni**: pipeline, AI, sentiment, dati, UI, CLI |
| **26** | **[26_CONFIG_AND_DATA_SCHEMA.md](26_CONFIG_AND_DATA_SCHEMA.md)** | **ogni opzione con il suo default** + ogni entità con ogni campo + ogni RPC |
| **27** | **[27_TEST_INVENTORY.md](27_TEST_INVENTORY.md)** | **ogni metodo di test**: l'elenco delle garanzie effettive |
| **28** | **[28_GRAPH_REACHABILITY_AND_FLOW.md](28_GRAPH_REACHABILITY_AND_FLOW.md)** | **grafo, raggiungibilità, cicli, DI morta, catene interrotte** |
| **30** | **[30_CARRY_CAPACITA_2026-08.md](30_CARRY_CAPACITA_2026-08.md)** | **[I16/F12] capacità e universo del carry: verdetto NEGATIVO**, il premio è sparito — misura su 42.644 eventi di funding reale |
| — | [project_map.json](project_map.json) | versione machine-readable della mappa |

### 🔴 E-01 — Il funding storico è raccolto, riempito all'indietro e mai letto

*(quinto passaggio, [28_GRAPH_REACHABILITY_AND_FLOW.md](28_GRAPH_REACHABILITY_AND_FLOW.md) §2)*

Ogni pezzo della catena esiste, è corretto e documentato: il sync raccoglie i funding rate,
`tools/PlatformExpand` ne riempie all'indietro la storia profonda, `IFundingHistoryProvider` la
legge, `BacktestEngine` sa usarla. **Manca solo chi assegni `BacktestConfiguration.FundingHistory`**
— lo fa un solo punto in tutto il repository, ed è un **file di test**.

Conseguenza: ogni backtest futures usa una **costante** 0,01% per 8h, mentre `CarryWorker` **dal
vivo usa il funding reale**. Il motore che decide e quello che valida usano due modelli diversi —
la stessa divergenza che il `VolatilityScaler` evita condividendo la funzione verbatim fra backtest
e live. Il carry è l'unico edge misurato positivo.
**Severità: High.**

### I cinque passaggi, e cosa ha trovato ciascuno

Ogni passaggio ha trovato cose nuove perché ha cambiato **metodo**, non perché ha guardato più a lungo.

| Passaggio | Documenti | Metodo | Reperti principali |
|---|---|---|---|
| **1 · strutturale** | 00-10 | mappa, cablaggi DI, integrazione | C-01 segreti · C-02 default · C-03 Microstructure · C-04 JumpModel · C-05 portfolio |
| **2 · di merito** | 20 | apertura dei file critici, verifica delle formule con esempi eseguiti | **D-01** gate DSR con N≤15 · **D-02** esposizione Futures · Q6 chiusa |
| **3 · esaustivo sui tipi** | 21, 22 | `<summary>` di **ogni** file + inventario di **ogni** documento | **D-06** la rotazione della master key non esiste · D-05 smentito |
| **4 · esaustivo sui membri** | 23-27 | estrazione **meccanica** di ogni tipo, membro, opzione, campo, test | copertura completa per costruzione; D-02 corroborato dal doc di `MaxTotalExposurePercent` |
| **5 · relazionale** | 28 | grafo delle dipendenze, raggiungibilità dai punti di ingresso, cicli, DI risolta | **E-01** funding storico mai letto · E-03 DI morta · E-04 sette scansioni su 12M righe · **zero file irraggiungibili** |

> Il terzo passaggio ha **smentito un mio reperto** (D-05, che il codice dichiara deliberato) e ne ha
> trovato uno che **cambia il piano di rimedio del Critical** (D-06).
> Il quarto chiude la copertura: non più un campione ampio, ma **l'intera superficie del codice**.
> Il quinto trova ciò che **nessuna lettura file-per-file può trovare**, perché il difetto non sta
> dentro un file: sta nello spazio fra due file che dovrebbero parlarsi e non lo fanno (E-01).

### Copertura complessiva dell'audit

| | |
|---|---:|
| File sorgente catalogati | **838** |
| Righe coperte | **187.495** |
| Tipi documentati | **1.778** |
| Membri documentati (metodi, proprietà, costruttori, costanti) | **7.989** |
| Opzioni di configurazione con default | **359** (44 classi) |
| Campi di entità persistite | **398** (34 tabelle) |
| Metodi di test catalogati | **2.096** |
| RPC gRPC | **12** (2 servizi, 45 messaggi) |
| Documenti Markdown inventariati | **119** |

---

## Riassunto esecutivo

### La premessa iniziale va corretta (con evidenza)

L'incarico partiva da due diagnosi: *«UI e macchina sconnesse in molte parti»* e *«molti meccanismi
non correttamente interconnessi»*. **La prima non è confermata dai fatti; la seconda sì, ma in modo
molto più localizzato di quanto suggerisse.**

Evidenza contraria alla prima diagnosi:

| Verifica | Esito |
|---|---|
| Rotte Blazor funzionali ↔ voci di `NavModel.cs` | **32 ↔ 32**, nessuna pagina orfana, nessuna voce che punta al vuoto (34 `@page` in totale: le 2 in più sono `/Error` e `/not-found`, di sistema) |
| Catena drift → registry → richiesta di riaddestramento | **chiusa** — `FeatureDriftWorker.cs:127` → `registry.RetireAsync(..., requestRetrain: true)` |
| Catena registry → motore di trading | **chiusa** — `TradingEngine.cs:81` esegue il Champion come `MlStrategy`, solo Paper/Testnet |
| Catena supervisore AI → decisione operativa | **chiusa e vincolata a solo-veto** — `RunApplyEvaluator.cs:81-106` |
| Catena backtest → promozione Paper→Testnet | **chiusa** — `PromotionEvaluator` → `LanePromoter` → `PromotionWorker` |
| Layer CQRS (Mediator) | **pienamente usato**: 14 `mediator.Send(...)` da `TradingPageService.cs` |
| Experiment tracker | **collegato a 7 punti** (3 pagine + 4 servizi, incluso `PipelineEngine`) |
| Copertura `SafetyChecker` sui percorsi d'ordine | **completa** sui due percorsi di apertura |

**Diagnosi corretta:** la piattaforma è **densamente cablata**, con commenti che documentano il
*perché* di ogni scelta. I problemi reali sono di **tre nature diverse**, che vanno tenute distinte
perché richiedono rimedi opposti:

1. **Isole di ricerca mai integrate** — codice completo e testato, raggiungibile solo da CLI o da
   nessuno. *(→ integrare o eliminare)*
2. **Disconnessioni deliberate e misurate** — spente apposta perché la misura ha detto di no.
   *(→ NON toccare: documentarle meglio)*
3. **Incoerenze fra invariante dichiarata e default del codice** — la classe più pericolosa, perché
   somiglia alla n.2 ma non lo è. *(→ correggere)*

### Stato complessivo della piattaforma

| Dimensione | Valutazione | Nota |
|---|---|---|
| Ampiezza funzionale | 🟢 Molto alta | 439 file `.cs` (72.824 righe) + 89 `.razor` (22.607) + 262 file di test (53.700) |
| Copertura di test | 🟢 Alta | rapporto test:produzione ≈ 0,74:1 in righe |
| Qualità della documentazione nel codice | 🟢 Eccellente | commenti che dichiarano il *perché*, con data e riferimento al report che ha motivato la scelta |
| Coerenza UI ↔ navigazione | 🟢 Completa | 32/32 |
| Integrazione della catena quant | 🟡 Parziale | la catena principale è chiusa; 3 rami laterali no (vedi sotto) |
| Invarianti di sicurezza | 🟡 Solide ma con 1 incoerenza | nessun percorso automatico verso Live; ma vedi C-02 |
| Igiene dei segreti | 🔴 **Critica** | vedi C-01 |
| Riproducibilità / determinismo | 🟡 Parziale | seed presenti nei punti chiave, ma non c'è un seed di run globale |

**Voto sintetico: 7,5/10.** Piattaforma matura e ben ragionata, con un problema di sicurezza aperto
da sanare subito e un debito di integrazione circoscritto.

---

## Principali criticità riscontrate

### 🔴 C-01 — Segreti reali in un file tracciato da git

`ProcioneMGR/appsettings.json.pre-audit-test-20260729-141448` è **tracciato** (`git ls-files` lo
elenca) e contiene:

- `Security:MasterKey` → 44 caratteri = base64 di 32 byte = **chiave AES-256 valorizzata**
- `Trading:GrpcSharedSecret` → 44 caratteri, **valorizzato**
- `ConnectionStrings:PostgresConnection` → **contiene `Password=`**

`.gitignore:34` esclude correttamente `ProcioneMGR/appsettings.json`, ma **non** questo backup datato
— esattamente il caso che il commento a `.gitignore:24` dichiarava di voler coprire. La master key è
quella con cui si cifrano le credenziali exchange a riposo: finché resta nella storia di git, quelle
credenziali sono da considerare in chiaro.
**Severità: Critical. Impatto: compromissione delle credenziali exchange.**
→ dettaglio in [06_INTEGRATION_GAPS.md](06_INTEGRATION_GAPS.md) §C-01.

> ⚠️ **Correzione al piano di rimedio (reperto D-06, terzo passaggio).** Nel documento 06 avevo
> prescritto «ruotare la master key e ri-cifrare le credenziali». **Lo strumento di ri-cifratura non
> esiste**: `AesGcmEncryptionService` dichiara la rotazione come l'**unico `TODO` reale della
> codebase** (manca il supporto multi-chiave e la ri-cifratura di massa). Ruotare oggi rende
> illeggibili le credenziali a database, e vanno **reinserite a mano** in `/settings/exchanges` —
> operazione di pochi minuti per un operatore singolo, ma da fare a corsie ferme e con backup.
> Procedura completa in [20_DEEP_DIVE_CODE_ANALYSIS.md](20_DEEP_DIVE_CODE_ANALYSIS.md) §5-bis.

### 🔴 C-02 — `DriveProtectiveExits` di default contraddice la decisione misurata

`CLAUDE.md` (regola 7) dichiara: *«`DriveProtectiveExits = false` … non sono sviste, sono risultati
di misure. Non "correggerli"»*, e `docs/REPORT-B3-EXITLAG-2026-07-28.md` documenta che uscire al
tocco è **peggio** in 24/24 configurazioni.

Ma il codice dice il contrario:
`ProcioneMGR/Services/MarketData/RealtimeMarketDataModels.cs:117` → `public bool
DriveProtectiveExits { get; set; } = true;`
e `ProcioneMGR/appsettings.json.example` → `MarketData:Realtime:DriveProtectiveExits = true`.

Oggi è inerte perché `MarketData:Realtime:Enabled = false`. **Ma chiunque accenda il feed real-time
eredita in automatico l'assetto che la misura ha bocciato**, senza che nulla lo avverta.
**Severità: High (Critical se si accende il feed).**

### 🟠 C-03 — Il modulo Microstructure è un'isola CLI

`ProcioneMGR/Services/Microstructure/` — 6 file, 1.166 righe (`IncrementalIcGate` 467 righe,
`BinanceDumpParser`, `TapeAggregator`, `OrderFlowImbalance`, `BinanceDumpDownloader`) — ha:

- **zero registrazioni DI** in `Program.cs` e in ogni `*ServiceCollectionExtensions.cs`
- **zero riferimenti** da `ProcioneMGR/Components/` (nessuna UI)
- unico consumatore: `tools/PlatformExpand/Program.cs` (CLI da 5.848 righe)

È ricerca vera (OFI, tape, gate IC incrementale) che la piattaforma **non può usare**.

### 🟠 C-04 — `JumpModel` è codice orfano

`ProcioneMGR/Services/Regime/JumpModel.cs` (288 righe): modello di regime con penalità di salto,
completo e testato (`ProcioneMGR.Tests/JumpModelTests.cs`). **Riferimenti in produzione: zero.**
Il rilevamento di regime in esercizio usa `RegimeDetector` (K-means). `JumpModelFit` non è
referenziato da nessuno, neanche dai test.

### 🟡 C-05 — Portfolio construction arriva all'esecuzione solo per un terzo

Dei tre optimizer registrati, **solo HRP** raggiunge l'operatività
(`DecisionStages.cs:90` → pesi dell'ensemble → `PipelineApplier` → `EnsembleState`).
`MeanVarianceOptimizer` e `RiskParityOptimizer` sono iniettati **solo** in
`PortfolioOptimization.razor`: producono un confronto che l'operatore guarda e poi non può applicare.

### 🟡 C-06 — Il sentiment come feature ML è spento

`SentimentFeatureFactor` → `AlphaFactorFactory.cs:70`, ma il ramo è gated da
`Sentiment:EnableMlFeature`, **default `false`**. La catena esiste, non gira.

---

## Principali aree sconnesse (sintesi)

| Area | Cosa esiste | Dove si ferma | Tipo |
|---|---|---|---|
| Microstructure (OFI, tape, IC gate) | 1.166 righe testate | CLI `PlatformExpand` | isola mai integrata |
| `JumpModel` (regime a salti) | 288 righe testate | test | orfano |
| Mean-Variance / Risk Parity | implementati | pagina `/portfolio` | analisi senza sbocco |
| Sentiment → feature ML | implementato | flag `EnableMlFeature=false` | spento |
| GARCH → position sizing | implementato | pagina `/volatility` + stage pipeline | **deliberato** (non validato per il sizing) |
| `ICombinatorialPurgedCv` | interfaccia | nessuno la implementa/usa | astrazione morta |
| `Committee` AI (AF3) | implementato | `Committee:Enabled=false` + `Fleet:UseCommittee=false` | spento |
| `Fleet` orchestrator (Queen Bee) | implementato | `Fleet:Enabled=false`, e comunque `DryRun=true` | spento per progetto |

**Distinzione essenziale:** le ultime quattro righe **non sono difetti**. Sono interruttori spenti
per scelta documentata. Confonderle con le prime quattro è l'errore che questo audit vuole evitare.

---

### 🔴 D-01 — Il gate anti-overfitting usa il più piccolo di tre conteggi di tentativi

*(trovato nel deep dive, [20_DEEP_DIVE_CODE_ANALYSIS.md](20_DEEP_DIVE_CODE_ANALYSIS.md) §2)*

Nello stesso run convivono tre numeri di "tentativi" che **non si parlano**: `PowerCheckStage` ne
assume 300, `StrategyDiscoveryEngine` misura il numero **reale** (`CombinationsTested`, usato solo
per la UI), e il gate DSR usa `validated.Count` ≤ `topN` = **15**.

Con la formula del codice stesso, la soglia SR\* da battere cresce con N: **1,77σ a N=15 contro
3,56σ a N=3.000**. Se un run prova 3.000 combinazioni — normale per una discovery
strategia × coppia × timeframe × griglia — **la barra applicata è la metà di quella dovuta**.
Il numero giusto è già misurato: manca solo chi lo passi al gate.
**Severità: High. Impatto: ogni DSR storico è ottimistico.**

### 🟠 D-02 — `MaxTotalExposurePercent` non vincola l'aggregato sui Futures

*(deep dive §3)* Il check 2 somma `UsedCapital` (che sui Futures è il **margine**) con
`order.Notional` (che è il **nozionale leveraged**). Il commento a `TradingEngine.cs:1528` dichiara
l'asimmetria «volutamente conservativa, non un bug»: lo è sul singolo ordine, **non sull'accumulo**.
Esempio eseguito: con `MaxOpenPositions=10`, leva 5×, il controllo calcola `2.800 ≤ 5.000` → passa,
mentre l'esposizione reale è il **100%** del capitale contro un limite dichiarato del 50%.
Il tetto effettivo è `MaxPositionSizePercent × MaxOpenPositions`; coi default coincide con
`MaxTotalExposurePercent` **per coincidenza** (10% × 5 = 50%).

---

## Top 10 disconnessioni, per gravità

| # | Disconnessione | Severità | Rif. |
|---|---|---|---|
| 1 | Segreti (MasterKey, gRPC secret, password DB) in file tracciato da git | Critical | C-01 |
| 2 | **Gate DSR con N ≤ 15 contro migliaia di combinazioni provate** | **High** | **D-01** |
| 3 | **`MaxTotalExposurePercent` non vincola l'esposizione Futures aggregata** | **Medium-High** | **D-02** |
| 4 | `DriveProtectiveExits=true` di default contro la misura B3 | High | C-02 |
| 5 | Microstructure: 1.166 righe senza DI né UI, solo CLI | High | C-03 |
| 6 | Validazione Selection/Holdout applicata in un solo punto (salvataggio UI) | Medium | D-03 |
| 7 | `JumpModel`: modello di regime mai cablato | Medium | C-04 |
| 8 | MV/Risk Parity non raggiungono l'ensemble né l'esecuzione | Medium | C-05 |
| 9 | Sentiment→feature ML spento di default | Medium | C-06 |
| 10 | Nessun seed di run globale: riproducibilità per-componente, non per-run | Medium | G-08 |

Seguono: `tools/PlatformExpand` senza gate condivisi (G-09) · deriva Alpha158 non sorvegliata (G-04)
· `ICombinatorialPurgedCv` astrazione morta (G-07) · gemello nullo con stoppini simmetrici (D-04)
· ora del digest legata al fuso del server (D-05).

---

## Prossimi passi consigliati (in quest'ordine)

1. **Subito — C-01.** Rimuovere il file dall'indice git, purgare la storia, **ruotare** master key,
   segreto gRPC e password Postgres. Finché non è fatto, tutto il resto è secondario.
2. **Subito — C-02.** Portare il default di `DriveProtectiveExits` a `false` nella classe e
   nell'example, allineandolo alla misura. È una riga, e chiude un'esposizione silenziosa.
3. **Subito — D-01.** Portare `CombinationsTested` nel `PipelineContext` e usarlo come N nominale
   del DSR. È il gate che protegge dall'illusione, ed è oggi il più permissivo dei tre conteggi
   presenti nello stesso run. Aspettarsi che i DSR storici si abbassino: è il risultato corretto.
4. **Subito — D-02.** Rendere omogenee le unità del check di esposizione totale sui Futures, e
   correggere il commento che lo dichiara «non un bug».
5. **Decidere su C-03 e C-04.** Integrare o eliminare: sono le due voci che pesano di più sulla
   percezione di «macchina sconnessa». Vedi [07_RECONSTRUCTION_BLUEPRINT.md](07_RECONSTRUCTION_BLUEPRINT.md) Fase 2.
6. **Chiudere C-05** portando la scelta dell'optimizer dentro `EnsembleAssemblyStage`.
7. **Solo dopo**, la ricostruzione vera e propria per fasi.

> **Vincolo di sicurezza che nessuna proposta di questo audit viola:** nessun percorso automatico
> verso Live. L'unica automazione ammessa che tocca una corsia Live resta la **retrocessione** a
> Testnet, opt-in e in dry-run (`PromotionEvaluator.AutoDemoteLiveToTestnet=false`,
> `DemoteLiveDryRun=true`).
