# PRD — Scoperta di pattern e interpretabilità anti-overfitting (2026-07-27)

*Valutazione di fattibilità del report "Dalla Ricerca di Pattern alla Validazione Anti-Overfitting"
(ricerca esterna allegata dall'utente) e piano di integrazione. Le fasi vivono in
[ROADMAP.md](ROADMAP.md) come nuovo Filone D.*

## 1. Origine

L'utente ha condotto una ricerca esterna (report generato con "Qwen", ~20 pagine, 133 riferimenti)
sul tema: come trasformare "cerco nei dati storici le zone dove si sarebbe guadagnato e
ricostruisco il contesto" — un'euristica che il documento stesso segnala come esposta al data
snooping se applicata alla lettera — in un processo che parte dalla struttura del mercato (fattori,
regimi) per **predire** la performance, invece di partire dalla performance osservata per trovarne
la causa a posteriori.

Propone: fattori Alpha158 → modello LightGBM → interpretazione SHAP → pattern-matching DTW/SAX →
regimi HMM/clustering → validazione CPCV + Walk-Forward condizionata al regime → monitoraggio del
concept drift, organizzati in un'architettura a tre moduli (Data Mining & Discovery → Strategy
Discovery & Interpretation → Validation & Deployment).

Il documento cita esplicitamente "ProcioneMGR", "Alpha158CSharp" e il motto "Safety > Solidità >
Velocità" nelle sue conclusioni: la ricerca è stata condotta con contesto del progetto, non è un
paper generico calato da fuori. Va quindi giudicata come **una seconda opinione informata**, non
come uno standard esterno indipendente — e verificata riga per riga contro quello che la
piattaforma ha già costruito e già misurato, perché la ricerca non poteva conoscere gli esiti
misurati ieri (gate C1, chiuso il 2026-07-26).

## 2. Verdetto di fattibilità

**Tecnicamente, quasi tutto il framework è già costruito** — spesso a uno standard più alto di
quello che il documento descrive. Delle circa 14 tecniche proposte, **9 esistono già in
piattaforma** (in alcuni casi superandole), **2 sono già state provate e chiuse con esito negativo
misurato** (una delle quali è proprio la proposta di punta del documento), e **solo 3-4 sono
realmente assenti**. Il documento non sbaglia nel metodo — la sua architettura a tre moduli è
sensata, ed è già di fatto come la piattaforma è organizzata — ma arriva con mesi di ritardo
rispetto al lavoro già fatto qui.

**Il punto più importante da capire prima di investire altro tempo**: la proposta centrale del
documento — "Walk-Forward Analysis Condizionata ai Regimi", cioè rilevare i regimi e validare la
strategia solo nei periodi giusti — è stata costruita e testata con un rigore **maggiore** di
quello che il documento descrive, ed è stata chiusa il 2026-07-26 con esito negativo misurato: il
`JumpModel` (l'evoluzione statistica dell'HMM proposto dal report) mostra regimi che persistono
davvero sul giornaliero (25-46 giorni), ma **non discriminano** la performance delle strategie fra
la prima e la seconda metà dello storico (correlazione di Spearman fra le due metà: −0,29 su BTC,
0,18 su ETH — dentro la zona del rumore, verificato contro un nullo a rotazione circolare a 200
giri). Il router che dovrebbe usare questi regimi resta acceso in "osservazione", non in decisione.

Questo non è un'obiezione al documento: è esattamente il tipo di verifica che il documento stesso
raccomanda. È solo già stata fatta, con dati reali, un giorno prima che il documento arrivasse.

**Cosa vale davvero la pena costruire**: le 3-4 cose genuinamente assenti, scelte perché reggono da
sole indipendentemente dall'esito di una nuova caccia — trattando le due tecniche di puro
pattern-matching geometrico (DTW, SAX) per quello che sono, **generatori di candidati a basso prior
di successo** (si applicano a pattern direzionali su OHLCV singolo, la classe di edge che qui ha già
prodotto otto zeri e che la stessa revisione di letteratura 2026 della piattaforma conferma esausta),
da far passare dal collaudo già esistente — non una nuova pista di validazione, non una promessa.

## 3. Confronto punto per punto

Il documento descrive un'architettura a tre moduli. La uso come struttura del confronto: ogni riga
è una tecnica proposta, con lo stato reale in piattaforma e il verdetto.

### 3.1 Modulo 1 — Data Mining & Discovery (pattern e regimi)

| Tecnica (PDF) | Stato in piattaforma | Verdetto |
|---|---|---|
| **Dynamic Time Warping (DTW)** | Assente | Genuinamente nuovo — §5b |
| **Symbolic Aggregate approXimation (SAX)** + mining di sequenze | Assente | Genuinamente nuovo, priorità inferiore — §5c |
| **Hidden Markov Model (HMM)** per regimi | **Provato e rimosso**: `StickyHmmSmoother` (in `tools/PlatformExpand/Program.cs`) — 0 riferimenti nell'app, rimozione in corso (Filone A3). Il problema non era l'algoritmo di decodifica (Viterbi) ma il clustering sottostante | Non reintrodurre senza un set di feature radicalmente diverso |
| **Clustering multidimensionale** (K-Means/DBSCAN) per regimi | Esiste: [`RegimeDetector.cs`](../ProcioneMGR/Services/Regime/RegimeDetector.cs) (K-means), pagina [`/regimes`](pagine/regimes.md). **Misurato**: persistenza mediana 2,2 giorni — "non operabile" con la stessa soglia che la letteratura citata dal PDF userebbe | Nessuna azione; DBSCAN non aggiungerebbe nulla che il gate C1 non abbia già chiuso |
| **Statistical jump model** (evoluzione naturale dell'HMM per la persistenza, non nel PDF) | Esiste: [`JumpModel.cs`](../ProcioneMGR/Services/Regime/JumpModel.cs) — più avanzato di quanto proposto. **Gate C1 fallito**: persiste (1d, 25-46gg) ma non discrimina (Spearman −0,29/0,18 fra metà) | Chiuso, esito negativo. Riapre solo con feature diverse (breadth/volume/macro) che superino ENTRAMBE le gambe |
| **Microstruttura / Order Flow Imbalance (OFI)** | Proxy da kline estese ESISTE: [`OrderFlowFactors.cs`](../ProcioneMGR/Services/Alpha/OrderFlowFactors.cs) (`TakerImbalanceFactor`, trade-flow — non book). OFI vero (book depth) assente ma **già pianificato**: Filone C, item C5 / "Fase 3 rivista" — pilota 90gg, 3 simboli, costo misurato (124× lo storico se fatto ingenuamente) | Nessuna nuova fase: innesto nel piano C5 già scritto — §5d |

### 3.2 Modulo 2 — Strategy Discovery & Interpretation

| Tecnica (PDF) | Stato in piattaforma | Verdetto |
|---|---|---|
| **Fattori Alpha158** | Esiste: [`Alpha158Catalog.cs`](../ProcioneMGR/Services/Alpha/Alpha158/Alpha158Catalog.cs), 158 fattori, usato in Feature Selection e ML Lab | Nessuna azione |
| **LightGBM** | Esiste: [`GradientBoostingReturnPredictor.cs`](../ProcioneMGR/Services/ML/GradientBoostingReturnPredictor.cs) — letteralmente `mlContext.Regression.Trainers.LightGbm(...)`, selezionabile in [ML Lab](pagine/ml.md) | Nessuna azione. Rifinitura minore possibile: esporre `min_data_in_leaf`/early stopping oltre ai 3 iperparametri attuali (non urgente) |
| **Selezione feature intelligente** (il PDF avverte: più fattori ≠ meglio) | Esiste ed è **oltre** il PDF: [`/feature-selection`](pagine/feature-selection.md) con IC + Information Ratio + consistenza di segno, non solo IC grezzo | Nessuna azione |
| **Scoperta automatica di nuovi fattori** (il PDF si ferma a "selezionare" fra Alpha158) | Esiste ed è **oltre** il PDF: [Alpha Mining genetico](pagine/alpha-mining.md), evolve formule nuove, verdetto su holdout + PBO/CSCV | Nessuna azione |
| **SHAP** | **Assente**. Esiste solo permutation importance (globale, in [ML Lab](pagine/ml.md)) | Genuinamente nuovo, priorità alta — §5a |

### 3.3 Modulo 3 — Validation & Deployment

| Tecnica (PDF) | Stato in piattaforma | Verdetto |
|---|---|---|
| **K-Fold CV** (il PDF stesso lo boccia per la finanza) | Non usata per serie temporali | Concorde col PDF |
| **Purged CV** (purging + embargo) | Esiste: [`PurgedTimeSeriesCv.cs`](../ProcioneMGR/Services/ML/PurgedTimeSeriesCv.cs), usata nel meta-learner dello stacking | Nessuna azione |
| **Combinatorial Purged CV (CPCV)** | Esiste: [`CombinatorialPurgedCv.cs`](../ProcioneMGR/Services/Validation/CombinatorialPurgedCv.cs), usata in Optimization e come CSCV in Alpha Mining | Nessuna azione |
| **Walk-Forward Analysis** | Esiste, motore di [Discovery](pagine/discovery.md) e Optimization | Nessuna azione |
| **Walk-Forward Condizionata ai Regimi** (proposta di punta del PDF) | **Costruita e testata con più rigore**: [`JumpModel.cs`](../ProcioneMGR/Services/Regime/JumpModel.cs) + fase `jumpstability` (profilo per-regime, split-half contro nullo a rotazione). **Gate fallito** (§2) | Chiuso. Non ripetere: sarebbe la stessa domanda già risposta |
| **Deflated Sharpe Ratio (DSR)** | Il PDF **non la nomina**. Esiste con N-effettivo (cluster di trial correlati) — [`DeflatedSharpeRatio.cs`](../ProcioneMGR/Services/Validation/DeflatedSharpeRatio.cs) | Piattaforma oltre il PDF |
| **PBO (Probability of Backtest Overfitting)** | Menzionata di sfuggita dal PDF. Esiste via CSCV in [`BacktestOverfitting.cs`](../ProcioneMGR/Services/Validation/BacktestOverfitting.cs), usata sia in Optimization sia in Alpha Mining | Piattaforma oltre il PDF |
| **Gemello sintetico + esperimento di controllo** (edge piantato) | **Assente dal PDF** — non lo menziona affatto. Esiste ed è lo strumento che ha smascherato l'unico falso positivo reale finora (SEI/USDT) | Piattaforma oltre il PDF — la differenza più significativa fra i due |
| **Monitoraggio del concept drift / IC decay** | Parziale: esiste a livello di STRATEGIA ([`StrategyDecayMonitor.cs`](../ProcioneMGR/Services/Monitoring/StrategyDecayMonitor.cs), Sharpe realizzato vs atteso). Assente a livello di FATTORE (IC nel tempo, drift di distribuzione) | Gap reale, priorità medio-alta — §5e |

## 4. Non-obiettivi

- **Non riaprire il tema regime-conditional.** Il gate C1 l'ha chiuso il 2026-07-26 con un metodo
  più severo di quello del PDF. Riapre solo con un set di feature radicalmente diverso
  (breadth/volume cross-asset, dati macro) che superi ENTRAMBE le gambe del gate (persistenza E
  discriminazione).
- **Non ri-cacciare pattern direzionali-tecnici puri su OHLCV singolo, maggiori, 1h/4h.** Otto
  cacce a zero più il consenso di letteratura 2026 già raccolto dalla piattaforma (nessuna fonte
  seria sostiene questa classe). Se DTW/SAX si applicano, puntarli su angoli non esauriti:
  continuazione post-crash/surge (F3, già positiva), o dentro le classi market-neutral (pairs,
  carry) dove l'edge misurato è positivo.
- **Non costruire una LOB reale prima del verdetto del pilota C5.** Il costo è misurato (124× lo
  storico se fatto ingenuamente): nessuna scorciatoia che lo aggiri.
- **Non creare una quarta pista di validazione.** DTW e SAX restano GENERATORI di candidati
  (feature booleane / trigger evento) che attraversano il collaudo già esistente
  (CPCV+DSR+PBO+gemello+controllo) — non un nuovo criterio di giudizio parallelo.
- **Nessun tocco al percorso live.** Ogni componente di questo PRD vive nel guscio freddo
  (ricerca/UI): nessuno modifica `TradingEngine`, nessuno introduce un nuovo scrittore concorrente,
  nessuno bypassa conferma manuale o promozione umana Testnet→Live.

## 5. Componenti nuovi — requisiti

### 5a. SHAP-lite per i modelli esistenti (priorità alta, rischio basso)

**Obiettivo**: spiegazioni locali (per singola predizione) e globali per i modelli ad albero già in
[ML Lab](pagine/ml.md) (RandomForest, GradientBoosting/LightGBM), oltre alla permutation importance
attuale, che è solo globale.

**Requisiti**:
- TreeSHAP esatto per i modelli ad albero (algoritmo chiuso, implementabile in C# senza dipendenze
  pesanti — niente Python embarcato). Non estendere a MLP/Attention/Stacking in v1: KernelSHAP
  model-agnostico è molto più costoso e a rischio di instabilità (il PDF stesso segnala che SHAP è
  sensibile al background di calcolo) — eventuale v2 solo su richiesta esplicita.
- Nuovo pannello in ML Lab: toggle Permutation ↔ SHAP; summary plot globale (come oggi) + waterfall
  per una barra storica selezionata (spiegazione locale).
- **Heatmap per regime**: rottura del summary SHAP per etichetta di regime, riusando le etichette
  K-means ESISTENTI di [`RegimeDetector.cs`](../ProcioneMGR/Services/Regime/RegimeDetector.cs) come
  lente descrittiva. Attenzione: uso completamente diverso dal router live (§3.1) — qui il regime è
  solo un asse di raggruppamento per un grafico, non deve superare nessun gate di discriminazione
  perché non decide nulla.
- Avvertenza in UI, sempre visibile (stile `GuidaPanel` esistente): "SHAP misura correlazione, non
  causalità — verificare economicamente ogni fattore che sembra dominante prima di fidarsene",
  ripresa alla lettera dall'ammonimento del PDF.

**Non-goal**: nessuna pretesa causale; nessuna modifica alla pipeline di training; nessun nuovo
modello.

**Costo stimato**: giorni (l'algoritmo TreeSHAP è noto e compatto; il grosso del lavoro è UI + il
breakdown per regime).

### 5b. DTW — pattern-matching su forma (priorità media, rischio alto di esito negativo)

**Obiettivo**: dato un pattern-modello (una finestra storica scelta dall'utente, es.
simbolo/TF/intervallo), trovare le occorrenze storiche più simili per forma e trasformarle in un
evento booleano riusabile dal motore [Discovery](pagine/discovery.md) esistente.

**Requisiti**:
- Normalizzazione z-score obbligatoria della finestra (media zero, varianza unitaria) prima del
  confronto — senza, DTW confronta livelli di prezzo invece che forme.
- Vincolo di banda (Sakoe-Chiba, es. 10% della lunghezza) sia per il costo computazionale sia
  perché il PDF stesso segnala che DTW senza vincoli trova allineamenti spurii in presenza di
  rumore.
- Output = **solo** una serie di timestamp "pattern presente/assente", non una strategia. Questa
  serie entra nel motore creativo esistente come un nuovo tipo di trigger evento (la stessa
  famiglia già usata per gli shock di volatilità in
  [`StrategyComposer.cs`](../ProcioneMGR/Services/Discovery/StrategyComposer.cs)) — zero nuova
  infrastruttura di validazione, riuso totale del gauntlet CPCV+DSR+PBO+gemello.
- **Gate non negoziabile prima di fidarsi di qualunque risultato positivo**: stesso principio della
  fase `control` già in `tools/PlatformExpand` — piantare un pattern sintetico con edge noto nella
  serie e verificare che DTW+pipeline lo trovi, prima di cercare pattern reali.
- Controllo di complessità: pruning con lower-bound (es. LB_Keogh) per restare trattabile sulla
  scala dati attuale (~7,45M candele); niente scan esaustivo O(n²) senza pruning.

**Perché il rischio è alto**: DTW su OHLCV grezzo è, nella sostanza, un altro modo di cercare edge
direzionale-tecnico — la classe che qui ha già prodotto otto zeri e che la letteratura 2026
raccolta dalla piattaforma conferma esausta per singolo strumento. Non è una ragione per non
provarlo (è una tecnica diversa, non la stessa domanda), ma è una ragione per non aspettarsi che
funzioni, e per puntarlo su angoli non esauriti (continuazione crash/surge, mercati-neutrali)
invece che rifare majors 1h/4h direzionale.

**Costo stimato**: giorni, gated dal controllo sintetico prima di procedere oltre.

### 5c. SAX + mining di sequenze (priorità bassa-media, condizionata a 5b)

**Obiettivo**: non un'alternativa a DTW ma il suo **pre-filtro economico** — converte le barre in
stringhe simboliche, usa un motore di mining di sotto-sequenze leggero (PrefixSpan semplificato,
nessuna dipendenza esterna pesante) per restringere rapidamente lo spazio di ricerca, poi DTW
conferma/classifica le occorrenze shortlisted con distanza esatta. È il pattern "SAX indicizza, DTW
verifica" della letteratura sul mining di serie temporali (il PDF li presenta come alternativi; in
pratica compongono meglio insieme).

**Decisione di sequenza**: non costruire SAX in parallelo a DTW. Prima si costruisce e gate-a 5b;
se il controllo sintetico passa E almeno un angolo non esaurito mostra un segnale che sopravvive a
holdout+DSR+PBO, allora SAX si giustifica come accelerazione. Altrimenti, costruire due motori di
pattern-shape senza sapere se la classe di idea regge sarebbe overengineering.

**Costo stimato**: giorni, condizionato a un segnale minimo da 5b.

### 5d. OFI vero — innesto nel pilota già pianificato (Filone C, item C5)

> **AGGIORNAMENTO DI ESECUZIONE (2026-07-28) — la dipendenza da C5 è caduta.** Questo paragrafo dava
> per necessario aspettare i 90 giorni di raccolta del pilota. Non lo era: i dump pubblici di Binance
> (`data.binance.vision`) contengono il **tape storico** e la **profondità del book storica**, quindi
> la domanda «il book aggiunge IC oltre al proxy?» è stata misurata subito, su 30 giorni × 3 simboli,
> **senza accendere alcuna raccolta e senza lasciare un costo permanente**. Un limite reale è emerso e
> va tenuto presente: i file `bookTicker` non esistono (404), quindi il top-of-book tick per tick non
> è ricostruibile e il book storicamente disponibile è la profondità a bande ogni 30 secondi. Verdetto,
> metodo e limiti nel [report di D3](REPORT-D3-OFI-2026-07-28.md).

Il contributo di questo PRD è specificare cosa calcolare nello step 3.3 del
piano già scritto (`docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md` §9.3, ereditato come C5 in
[ROADMAP.md](ROADMAP.md)):

- **Formula**: order flow imbalance firmato al top-of-book (stile Cont-Kukanov-Stoikov: variazione
  netta della size disponibile al miglior bid/ask fra due snapshot, con segno secondo il lato che
  si muove), calcolato sugli snapshot a 10s già pianificati in `OrderBookSnapshots` — non serve il
  tape grezzo.
- **Confronto esplicito e quantitativo**: passare l'OFI vero e
  [`TakerImbalanceFactor`](../ProcioneMGR/Services/Alpha/OrderFlowFactors.cs) (il proxy da kline
  estese, già esistente) dallo stesso [`/feature-selection`](pagine/feature-selection.md) per
  rispondere alla domanda che lo step 3.3 già pone: il book vero aggiunge IC oltre al proxy
  trade-flow che già abbiamo, o è ridondante?
- Se ridondante: si spegne la raccolta book a fine pilota, si tiene solo il proxy da kline (già
  gratis). Esito negativo qui è un risultato valido, non un fallimento — coerente con la cultura di
  misura della piattaforma.

**Costo**: quello già stimato in C5 (nessun costo aggiuntivo introdotto da questo PRD).

### 5e. Factor drift monitor (priorità medio-alta, rischio basso)

**Obiettivo**: estendere il pattern già collaudato di
[`StrategyDecayMonitor.cs`](../ProcioneMGR/Services/Monitoring/StrategyDecayMonitor.cs) (realizzato
vs atteso, a livello di strategia) al livello di FATTORE — l'IC di ogni fattore Alpha158/minato
cambia nel tempo, e oggi nessuno se ne accorge finché non si ri-passa a mano da
`/feature-selection`.

**Requisiti**:
- Riusa [`FactorEvaluator.cs`](../ProcioneMGR/Services/Alpha/FactorEvaluator.cs) /
  [`IcFeatureSelector.cs`](../ProcioneMGR/Services/ML/IcFeatureSelector.cs) esistenti su finestre
  rolling (es. 90 giorni) invece che su tutto il periodo.
- Persistenza leggera di una serie storica di IC per fattore (nuova tabella minima, o riuso di
  `ExperimentRuns` come già fa il resto della piattaforma per non moltiplicare gli schemi).
  → **Fatto il 2026-07-28** con la tabella minima `FactorIcWindows` (una riga per finestra, upsert
  idempotente). Il primo giro aveva deviato da questo requisito tenendo solo una fotografia in
  memoria; la deviazione è stata chiusa dal proprietario, e la ragione per cui l'argomento
  «è solo una cache» non bastava sta nel [report del filone D](REPORT-FILONE-D-2026-07-27.md) §5.
- Alert quando l'|IC| rolling scende sotto la soglia di sopravvivenza già in uso in
  `/feature-selection` (0.02) per un numero sufficiente di finestre, o quando il segno si inverte
  rispetto al periodo di selezione originale — stessa logica di soglia+persistenza di
  `StrategyDecayMonitor`, non un criterio nuovo da inventare.
- Superficie: nuovo pannello (sparkline IC storico per fattore) in `/feature-selection`; alert
  accanto al widget decadimento-strategia già in Home.
- **Nessuna azione automatica.** Solo segnalazione — coerente con "AI advisory-only" e con
  `StrategyDecayMonitor` che oggi non declassa nulla da solo.

**Costo stimato**: giorni (riuso quasi totale di codice esistente; il lavoro è schema + job di
calcolo periodico + UI).

## 6. Dove vivono i pezzi nuovi

```
Discovery (nuovi generatori)            Pipeline esistente (invariata)          Collaudo esistente (invariato)
┌───────────────────────────┐          ┌───────────────────────────┐          ┌───────────────────────────┐
│ DTW template match     ───┼─ evento ─┼▶ EventTriggerStrategy     │          │ CPCV + DSR + PBO          │
│ SAX pre-filtro (cond.)    │  booleano│  (StrategyComposer)       ┼─candid.──┼▶ gemello sintetico        │
└───────────────────────────┘          │ Alpha158 + fattori minati│          │ esperimento di controllo │
                                        │ IC feature selection     │          │ holdout                  │
┌───────────────────────────┐          └───────────────┬───────────┘          └─────────────┬─────────────┘
│ OFI (innesto C5)       ───┼─ fattore ─────────────────┘                                    │
└───────────────────────────┘                                                                ▼
┌───────────────────────────┐          ┌───────────────────────────┐          Registry / Ensemble
│ SHAP-lite              ───┼─────────▶│ lettura dei modelli ML   │          (invariato — nessuna nuova
│ Factor drift monitor   ───┼─────────▶│ già esistenti (sola      │           via di promozione)
└───────────────────────────┘          │ lettura, nessun training)│
                                        └───────────────────────────┘
```

Tutto vive nel guscio freddo (ricerca/UI). Nessun componente scrive su `TradingEngine`, nessuno
introduce un nuovo scrittore concorrente, nessuno bypassa i patti di sicurezza esistenti (conferma
manuale Live, promozione umana Testnet→Live, AI advisory-only).

## 7. Fasi, priorità e gate

Riassunto — il dettaglio gate-by-gate vive in [ROADMAP.md](ROADMAP.md), Filone D.

| Fase | Cosa | Priorità | Rischio | Dipendenze |
|---|---|---|---|---|
| D1 | SHAP-lite (TreeSHAP + heatmap per regime) | Alta | Basso | Nessuna |
| D2 | Factor drift monitor (IC rolling + alert) | Medio-alta | Basso | Nessuna |
| D3 | OFI vero — innesto formula in C5 | Eredita quella di C5 | Basso (è misura, non cablaggio) | C5 (Filone C) |
| D4 | DTW pattern-shape discovery | Media | Alto (probabile ennesimo zero) | Gate controllo sintetico |
| D5 | SAX pre-filtro + mining sequenze | Bassa | Alto, condizionato | Segnale minimo da D4 |

## 8. Metriche di successo

1. **D1/D2 non richiedono un edge nuovo per avere valore**: successo = i modelli esistenti diventano
   più leggibili e un IC che decade si vede prima che diventi un problema silenzioso,
   indipendentemente da cosa succede con D4/D5.
2. **D3 eredita il gate di C5**: successo = risposta chiara (sì/no) alla domanda "il book vero
   aggiunge IC oltre il proxy", entro i 90 giorni di pilota già pianificati.
3. **D4/D5 hanno successo anche se il risultato è negativo**, a condizione che passino dal gate del
   controllo sintetico prima (altrimenti un negativo direbbe solo "gli strumenti non funzionano",
   non "l'idea non regge") — coerente con come la piattaforma ha già trattato le altre otto cacce a
   zero.
4. **Nessuna riga di codice tocca `TradingEngine`, `ExecutionJob`, o un percorso di scrittura
   Live/Testnet.** Verificabile a fine fase con lo stesso censimento di raggiungibilità/
   registrazione DI già usato nell'audit del 2026-07-26.
