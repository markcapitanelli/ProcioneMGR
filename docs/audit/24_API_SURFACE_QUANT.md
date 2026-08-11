# 24 — SUPERFICIE API: macchina quantitativa

> Estrazione **esaustiva e meccanica** della superficie API dal sorgente: nessun campione,
> nessuna parafrasi. Ogni tipo e ogni membro pubblico (o di interfaccia) con la firma reale
> e il doc-comment che il codice gli associa.

ML, fattori alpha, validazione anti-overfitting, regime, portafoglio, serie storiche, backtest, ottimizzazione, discovery, microstruttura, analisi tecnica.

| | |
|---|---:|
| File coperti | 166 |
| Tipi | 427 |
| Membri (metodi, proprietà, costruttori, costanti) | 1513 |

**Legenda:** 🔌 interface · 📦 class · 🧾 record · 🔢 enum · ▫️ struct · `m` metodo · `p` proprietà · `c` costruttore · `k` costante

---

# `Services/ML/`

## `ProcioneMGR/Services/ML/AttentionReturnPredictor.cs`

### 📦 `AttentionReturnPredictor` `: IReturnPredictor, ISequencePredictor`

> Predittore di rendimento basato su self-attention , in C# puro SENZA TorchSharp (bivio §1.4 risolto verso "attention a mano", coerente col precedente ). L'input è una finestra di T timestep × F fattori (vedi e ). Architettura minimale ma reale: X[T,F] → (standardizzazione) → embed lineare E=X·Wᵢₙ+bᵢₙ [T,D] → + positional encoding → self-attention a 1 testa con residuo (Q,K,V,O) → readout sull'ULTIMO timestep ("ora", che via attention ha già raccolto tutta la storia) → testa FFN (tanh) → scalare (rendimento forward atteso). Addestramento con backpropagation MANUALE (softmax/matmul/residuo) e mini-batch SGD con weight decay e gradient clipping; deterministico a parità di seed (init pesi + shuffling). Persistenza JSON (pesi + normalizzazione + config), come l'MLP — nessun ITransformer ML.NET.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `bool IsFitted` | — |
| `p` | `int WindowLength` | — |
| `p` | `int FeaturesPerStep` | — |
| `c` | `AttentionReturnPredictor(int windowLength = 8, int embedDim = 16, int hiddenUnits = 16, int epochs = 150, double learningRate = 0.01, int seed = 42)` | — |
| `m` | `void Fit(MLContext mlContext, IDataView trainingData)` | — |
| `m` | `float Predict(float[] features)` | — |

### 📦 `Cache`

| | Firma | Descrizione |
|---|---|---|
| `p` | `double[,] X` | — |
| `p` | `double[,] E` | — |
| `p` | `double[,] Q, K, V, A, C, O, Z` | — |
| `p` | `double[] Zpool` | — |
| `p` | `double[] H` | — |
| `p` | `double Yhat` | — |
| `m` | `IReadOnlyList&lt;FeatureImportance&gt; ComputeFeatureImportance(MLContext mlContext, IDataView evaluationData, IReadOnlyList&lt;string&gt; featureNam…` | — |

### 🧾 `State` `(int T, int F, int D, int Hff,`

| | Firma | Descrizione |
|---|---|---|
| `m` | `void Save(MLContext mlContext, string path)` | — |
| `m` | `void Load(MLContext mlContext, string path)` | — |
| `m` | `void Dispose()` | — |

### 📦 `Grads`

| | Firma | Descrizione |
|---|---|---|
| `p` | `double[,] Win, Wq, Wk, Wv, Wo, W1` | — |
| `p` | `double[] Bin, B1, W2` | — |
| `p` | `double B2` | — |
| `c` | `Grads(int f, int d, int hff)` | — |

## `ProcioneMGR/Services/ML/DatasetBuilder.cs`

### 📦 `DatasetBuilder` `: IDatasetBuilder`

> Implementazione di . Pura/stateless -&gt; registrabile Singleton.

| | Firma | Descrizione |
|---|---|---|
| `c` | `DatasetBuilder(Alpha.IFactorCache? factorCache = null)` | — |

## `ProcioneMGR/Services/ML/GradientBoostingReturnPredictor.cs`

### 📦 `GradientBoostingReturnPredictor` `: RegressionPredictorBase`

> Gradient Boosting (ML.NET LightGBM) per la previsione del rendimento forward — cap. 12. Nel libro è il modello con il miglior rapporto performance/sforzo sui dati tabellari di fattori. Come , basato su alberi: nessuna normalizzazione delle feature necessaria.

| | Firma | Descrizione |
|---|---|---|
| `c` | `GradientBoostingReturnPredictor(int numberOfLeaves = 20, int numberOfIterations = 100, double learningRate = 0.1)` | — |
| `p` | `string Name` | — |
| `m` | `IEstimator&lt;ITransformer&gt; BuildPipeline(MLContext mlContext)` | — |

## `ProcioneMGR/Services/ML/HierarchicalClustering.cs`

### 📦 `HierarchicalClustering` `: IHierarchicalClustering`

> Implementazione di . Pura/stateless -&gt; registrabile Singleton.

| | Firma | Descrizione |
|---|---|---|
| `m` | `ClusterNode BuildDendrogram(double[,] distanceMatrix, IReadOnlyList&lt;string&gt; labels, LinkageMethod method = LinkageMethod.Average)` | — |

## `ProcioneMGR/Services/ML/IDatasetBuilder.cs`

### 🔌 `IDatasetBuilder`

> Costruisce dataset supervisionati per i modelli di previsione dei rendimenti a partire da una libreria di fattori alpha e un orizzonte di rendimento forward (il target).

## `ProcioneMGR/Services/ML/IHierarchicalClustering.cs`

### 🔢 `LinkageMethod`

> Criterio di linkage per l'agglomerative clustering (formula di Lance-Williams).

### 📦 `ClusterNode`

> Nodo di un dendrogramma: foglia (asset singolo) o fusione di due sotto-cluster.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string? Label` | Nome dell'asset, valorizzato solo per le foglie (Left/Right nulli). |
| `p` | `ClusterNode? Left` | — |
| `p` | `ClusterNode? Right` | — |
| `p` | `double Distance` | Distanza a cui questo cluster si è formato (0 per le foglie). |
| `p` | `IReadOnlyList&lt;int&gt; LeafIndices` | Indici originali (nell'ordine dei label passati a BuildDendrogram) contenuti in questo sotto-albero. |
| `p` | `bool IsLeaf` | — |

### 🔌 `IHierarchicalClustering`

> Clustering gerarchico agglomerativo (cap. 13) su una matrice di distanza: costruisce il dendrogramma unendo via via i due cluster più vicini. Riusato da Hierarchical Risk Parity (Fase C, §3.5) per la quasi-diagonalizzazione della matrice di correlazione e la bisezione ricorsiva dei pesi di portafoglio — qui si costruisce solo l'albero, indipendente dall'uso che se ne farà.

| | Firma | Descrizione |
|---|---|---|
| `m` | `ClusterNode BuildDendrogram(double[,] distanceMatrix, IReadOnlyList&lt;string&gt; labels, LinkageMethod method = LinkageMethod.Average)` | deve essere simmetrica, n x n, con diagonale nulla (distanza di un elemento da se stesso = 0). assegna un nome a ciascuna riga/colonna, nello stesso ordine. |

### 📦 `CorrelationDistance`

> Conversione di una matrice di correlazione in distanza (Mantegna), usata da PCA/HRP/clustering.

| | Firma | Descrizione |
|---|---|---|
| `m` | `double[,] FromCorrelationMatrix(double[,] correlation)` | d = sqrt(0.5 * (1 - corr)), in [0,1]: 0 quando corr=1 (identici), 1 quando corr=-1 (opposti). Metrica standard in finanza per trasformare correlazioni in distanze valide (soddisfa la disuguaglianza triangolare, a differ… |

## `ProcioneMGR/Services/ML/IPurgedTimeSeriesCv.cs`

### 🔌 `IPurgedTimeSeriesCv`

> Cross-validation temporale con purging ed embargo (López de Prado, "Advances in Financial Machine Learning"). Su serie storiche con label a rendimento forward, un semplice K-fold casuale causa leakage: un campione di training può avere un orizzonte di label che si sovrappone al periodo di test (o viceversa), gonfiando artificialmente le metriche. Assente in ML.NET di default.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;CvSplit&gt; Split(int sampleCount, int folds, int purgeWindow, int embargoPeriods)` | Divide campioni ORDINATI TEMPORALMENTE in blocchi di test contigui e non sovrapposti. Per ogni fold, il training esclude non solo il blocco di test ma anche: - purge : i campioni immediatamente prima del test (le cui la… |

## `ProcioneMGR/Services/ML/IReturnPredictor.cs`

### 🔌 `IReturnPredictor` `: IDisposable`

> Astrazione comune a tutti i modelli di previsione dei rendimenti (lineari, Random Forest, boosting, deep learning nelle fasi successive). Ogni implementazione incapsula un ITransformer di ML.NET addestrato su un con colonne Features/Label; è la via rapida (no IDataView) usata in hot-loop dal backtest ( MlStrategy ).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | Nome tecnico del modello (per persistenza/versionamento, come RegimeModel ). |
| `p` | `bool IsFitted` | True dopo o riusciti. |
| `m` | `void Fit(MLContext mlContext, IDataView trainingData)` | Addestra il modello su un IDataView con colonne "Features" (vettore) e "Label" (float). |
| `m` | `float Predict(float[] features)` | Predizione puntuale (rendimento forward atteso) dato un vettore di feature. |
| `m` | `void Save(MLContext mlContext, string path)` | Persiste il modello addestrato su file (riuso del pattern di versionamento di RegimeModel ). |
| `m` | `void Load(MLContext mlContext, string path)` | Carica un modello precedentemente salvato con . |
| `m` | `IReadOnlyList&lt;FeatureImportance&gt; ComputeFeatureImportance(MLContext mlContext, IDataView evaluationData, IReadOnlyList&lt;string&gt; featureNam…` | Permutation feature importance: per ogni feature (nell'ordine di ), quanto peggiora la qualità delle predizioni se quella feature viene mescolata casualmente nel dataset di valutazione. Richiede un modello già addestrat… |

## `ProcioneMGR/Services/ML/IRiskFactorPca.cs`

### 🔌 `IRiskFactorPca`

> PCA sui rendimenti di più simboli per estrarre risk factor statistici (cap. 13): componenti principali ortogonali che spiegano la varianza comune del paniere, utili sia come feature de-correlate per i modelli sia per capire l'esposizione al rischio sistemico.

| | Firma | Descrizione |
|---|---|---|
| `m` | `RiskFactorPcaResult Compute(IReadOnlyDictionary&lt;string, IReadOnlyList&lt;decimal&gt;&gt; returnsBySymbol, int componentCount)` | Calcola le prime componenti principali sui rendimenti (standardizzati per simbolo: PCA sulla matrice di correlazione, non di covarianza, per non far dominare gli asset più volatili solo per scala). Serie di rendimenti p… |

## `ProcioneMGR/Services/ML/ISequencePredictor.cs`

### 🔌 `ISequencePredictor`

> Marca un che ragiona su una SEQUENZA di timestep (non su un solo vettore di feature): il vettore che riceve in Fit / Predict è una finestra di passi × fattori, appiattita in ordine temporale (dal più vecchio al più recente). Serve a MlStrategy per costruire la finestra a inferenza senza stato interno (niente buffer fragili): la strategia vede questa interfaccia e impacchetta gli ultimi T vettori di fattori prima di chiamare Predict .

| | Firma | Descrizione |
|---|---|---|
| `p` | `int WindowLength` | Numero di timestep della finestra (T). |
| `p` | `int FeaturesPerStep` | Numero di fattori per timestep (F). |

## `ProcioneMGR/Services/ML/IcFeatureSelector.cs`

### 📦 `IcFeatureSelectionConfig`

> Configurazione della selezione di feature per Information Coefficient.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int ForwardHorizon` | Orizzonte del rendimento forward target dell'IC (coerente col dataset ML che seguirà). |
| `p` | `int TopN` | Quante feature tenere al massimo (le prime per \|IC\|). |
| `p` | `double MinAbsIc` | \|IC\| minimo perché una feature sia tenuta (scarta i fattori-rumore). |
| `p` | `double MinInformationRatio` | Information Ratio minimo (stabilità dell'IC nel tempo). 0 = nessun filtro. |
| `p` | `bool RequireConsistentSign` | Se true, tiene solo i fattori il cui IC rolling è coerente in segno con l'IC full-sample nella maggioranza delle finestre (IcConsistency ≥ 0.5): evita i fattori "a caso" col segno instabile. |

### 🧾 `ScoredFactor` `(FactorSpec Spec, FactorEvaluationResult Evaluation)`

> Un fattore candidato con la sua valutazione IC — l'unità ordinabile della selezione.

| | Firma | Descrizione |
|---|---|---|
| `p` | `double AbsIc` | \|IC\| full-sample: il criterio primario di ordinamento (un segnale vale sia positivo che negativo). |

### 🔌 `IIcFeatureSelector`

> Selezione automatica delle feature per Information Coefficient (Fase 3): ordina/filtra un insieme di candidati usando il ESISTENTE (IC di Spearman, Information Ratio, consistenza), così la scelta delle feature per i modelli ML smette di essere manuale e diventa guidata dalla misura. L'output è un sottoinsieme di pronto per — zero modifiche a valle. Deterministico (l'IC è deterministico). Rif. Fase 3 §3.3 (strumenti sottoutilizzati).

### 📦 `IcFeatureSelector` `: IIcFeatureSelector`

## `ProcioneMGR/Services/ML/IncrementalFactorFilter.cs`

### 🧾 `IncrementalFilterEntry` `(FactorSpec Spec, bool Kept, IncrementalIcOutcome? Outcome);`

> Esito del filtro per un singolo fattore: tenuto o scartato, col verdetto del gate. Il fattore valutato. True se entra nell'insieme selezionato. Verdetto del gate (null per il capostipite: è il primo controllo, non un candidato).

### 🧾 `IncrementalFilterResult` `(IReadOnlyList&lt;IncrementalFilterEntry&gt; Entries)`

> Risultato del filtro incrementale: ogni fattore col suo verdetto, più i soli tenuti.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;FactorSpec&gt; Kept` | — |
| `p` | `int DroppedCount` | — |

### 📦 `IncrementalFactorFilter`

> [2.6 PRD-RISANAMENTO, chiude C-03/G-16] Il ponte fra la selezione per IC e l' del modulo Microstructure — il gate anti-ridondanza che l'audit ha trovato completo, testato e irraggiungibile («il dato c'è, chi lo legge no»). La domanda a cui risponde: con 158+ fattori Alpha158 nel catalogo, molti candidati portano la STESSA informazione a orizzonti vicini. La selezione per \|IC\| assoluto li tiene tutti; questo filtro GREEDY li passa in ordine di priorità (\|IC\| decrescente, l'ordine della selezione) e tiene solo chi AGGIUNGE informazione oltre ai già tenuti — IC parziale contro l'insieme corrente, con soglia di rumore e nullo per permutazione, tutto dentro il gate. Statico e puro come il gate che usa: nessuna registrazione DI, deterministico a parità di input (il gate ha il suo seed interno). Le convenzioni di calcolo (Compute del fattore, ForwardReturns) sono le STESSE del : il filtro …

## `ProcioneMGR/Services/ML/Labeling/MetaLabeler.cs`

### 🔢 `PrimarySignal`

> Decisione di lato del modello primario su una barra.

### 🧾 `MetaLabelSample` `(`

> Un campione di addestramento per il meta-modello: il segnale primario su quella barra, e se quel segnale — portato fino a una delle tre barriere — si sia rivelato giusto.

### 🧾 `MetaLabelingReport` `(`

> Confronto fra il primario da solo e il primario filtrato dal meta-modello. Il numero di operazioni superstiti è parte del verdetto, non un dettaglio.

| | Firma | Descrizione |
|---|---|---|
| `p` | `double PrimaryPrecision` | Quota di segnali primari andati a buon fine. |
| `p` | `double FilteredPrecision` | Quota di segnali andati a buon fine fra quelli che il meta-modello ha lasciato passare. |
| `p` | `double Recall` | Quota dei segnali VINCENTI del primario che il filtro ha conservato. |
| `p` | `double SurvivalRate` | Quota di segnali sopravvissuti al filtro: se crolla, la precision che sale vale poco. |
| `p` | `double SelectionZScore` | Quanti errori standard separano i vincenti osservati nel sottoinsieme filtrato da quelli che ci si aspetterebbe pescando A CASO lo stesso numero di operazioni. Questo è il cuore del verdetto, ed è nato da un test fallit… |
| `p` | `bool IsImprovement` | Il filtro migliora davvero? Servono TRE cose insieme: che la precision salga, che resti un campione non ridicolo (almeno 30 operazioni e un quinto dei segnali), e che il guadagno superi quanto una selezione casuale prod… |

### 🔌 `IMetaLabeler`

> Costruzione dei campioni di meta-labeling e valutazione del filtro. Puro e deterministico.

### 📦 `MetaLabeler` `(ITripleBarrierLabeler? labeler = null) : IMetaLabeler`

## `ProcioneMGR/Services/ML/Labeling/MetaLabelingAnalysisService.cs`

### 🧾 `MetaLabelingAnalysis` `(`

> Esito completo dell'analisi di meta-labeling su una strategia reale.

### 🔌 `IMetaLabelingAnalysisService`

> Esegue la catena completa triple-barrier + meta-labeling su una strategia.

### 📦 `MetaLabelingAnalysisService` `(`

## `ProcioneMGR/Services/ML/Labeling/MetaModelTrainer.cs`

### 📦 `MetaRow`

> Riga di addestramento del meta-modello: feature, etichetta binaria, peso.

| | Firma | Descrizione |
|---|---|---|
| `p` | `float[] Features` | — |
| `p` | `bool Label` | — |
| `p` | `float Weight` | — |

### 📦 `MetaPrediction`

| | Firma | Descrizione |
|---|---|---|
| `p` | `float Probability` | — |

### 📦 `MetaModelConfig`

> Parametri dell'addestramento del meta-modello.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Folds` | Numero di fold della cross-validation purgata. |
| `p` | `int PurgeWindow` | Barre di purga fra train e test. Se lasciato a zero viene derivato dall'orizzonte della barriera verticale — che è il minimo corretto, non una preferenza. |
| `p` | `int EmbargoPeriods` | Barre di embargo dopo il fold di test. |
| `p` | `int NumberOfLeaves` | Foglie e iterazioni del classificatore ad alberi. |
| `p` | `int NumberOfTrees` | — |
| `p` | `int Seed` | Seed: l'addestramento dev'essere riproducibile come tutto il resto della piattaforma. |

### 🧾 `MetaModelResult` `(`

> Esito dell'addestramento out-of-fold.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsComplete` | True se ogni campione ha ricevuto una probabilità da un modello che non l'ha visto. |

### 🔌 `IMetaModelTrainer`

> Addestra il meta-modello e produce probabilità out-of-fold.

### 📦 `MetaModelTrainer` `(IPurgedTimeSeriesCv? cv = null) : IMetaModelTrainer`

## `ProcioneMGR/Services/ML/Labeling/TripleBarrierLabeler.cs`

### 🔢 `TripleBarrierOutcome`

> Quale barriera è stata toccata per prima.

### 🧾 `TripleBarrierLabel` `(`

> Etichetta di una singola barra di ingresso: quale barriera, quando, con che rendimento.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int BarsHeld` | Barre di detenzione: l'ampiezza su cui questa etichetta "occupa" la serie. |

### 📦 `TripleBarrierConfig`

> Parametri delle tre barriere. Le percentuali sono positive e misurate dall'ingresso.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal ProfitTakePercent` | Barriera favorevole, in % dall'ingresso (es. 2 = +2% per un long). Zero o meno = disattivata. |
| `p` | `decimal StopLossPercent` | Barriera avversa, in % dall'ingresso (valore POSITIVO, es. 1 = −1% per un long). Zero o meno = disattivata. |
| `p` | `int VerticalBarrierBars` | Barriera verticale: massimo numero di barre di detenzione. |
| `p` | `OrderSide Side` | Lato dell'ipotetico ingresso. |

### 🔌 `ITripleBarrierLabeler`

> Etichettatura triple-barrier e pesi di campione per etichette sovrapposte. Puro e deterministico: nessun DB, nessun orologio, nessuno stato.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;TripleBarrierLabel&gt; Label(IReadOnlyList&lt;OhlcvData&gt; candles, TripleBarrierConfig config)` | Etichetta ogni barra di ingresso risolvibile. Le barre in coda restano senza etichetta. |
| `m` | `TripleBarrierConfig SuggestConfig(IReadOnlyList&lt;OhlcvData&gt; candles, OrderSide side, int verticalBarrierBars)` | Configurazione con barriere derivate dai percentili di escursione della serie ( ), invece che da numeri scelti a mano. |
| `m` | `IReadOnlyList&lt;double&gt; AverageUniqueness(IReadOnlyList&lt;TripleBarrierLabel&gt; labels, int barCount)` | Peso di ciascuna etichetta per UNICITÀ MEDIA (AFML §4.3): quanto poco la sua finestra di vita è condivisa con le altre. Allineato per indice a . |

### 📦 `TripleBarrierLabeler` `: ITripleBarrierLabeler`

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;TripleBarrierLabel&gt; Label(IReadOnlyList&lt;OhlcvData&gt; candles, TripleBarrierConfig config)` | — |
| `m` | `TripleBarrierConfig SuggestConfig(IReadOnlyList&lt;OhlcvData&gt; candles, OrderSide side, int verticalBarrierBars)` | — |
| `m` | `IReadOnlyList&lt;double&gt; AverageUniqueness(IReadOnlyList&lt;TripleBarrierLabel&gt; labels, int barCount)` | — |

## `ProcioneMGR/Services/ML/LinearReturnPredictor.cs`

### 📦 `LinearReturnPredictor` `: RegressionPredictorBase`

> Baseline lineare regolarizzata (ML.NET SDCA) per la previsione del rendimento forward. Prima implementazione di (cap. 7 del libro): interpretabile, veloce da addestrare, punto di riferimento prima dei modelli non lineari (Random Forest, boosting) delle fasi successive.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `IEstimator&lt;ITransformer&gt; BuildPipeline(MLContext mlContext)` | — |

## `ProcioneMGR/Services/ML/MlComparisonClient.cs`

### 🔌 `IMlComparisonClient`

> Confronto OSSERVATIVO fra la predizione ML locale (già calcolata dal TradingEngine) e quella del servizio remoto procionemgr-ml (Fase 2a, dual-read). Non ritorna nulla al chiamante e non influenza mai una decisione: registra solo l'esito (metrica + log). Ogni errore/timeout del remoto è assorbito qui — mai propagato.

### 📦 `MlComparisonClient` `(`

## `ProcioneMGR/Services/ML/MlComparisonOptions.cs`

### 📦 `MlComparisonOptions`

> Opzioni del dual-read ML (Fase 2a, sezione config "Ml"). Il confronto col servizio remoto è puramente OSSERVATIVO: non influenza mai una decisione di trading.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Accende il confronto (hot-reload via IOptionsMonitor). deve comunque essere valorizzato a startup perché il client gRPC venga registrato (cambiarlo richiede riavvio). |
| `p` | `string? RemoteUrl` | Indirizzo del servizio procionemgr-ml. Vuoto → client non registrato, confronto spento. |
| `p` | `int TimeoutMs` | Deadline della chiamata gRPC di confronto (ms). Stretto: è solo osservabilità. |

## `ProcioneMGR/Services/ML/MlLabService.cs`

### 🧾 `MlConfigSnapshot` `(`

> Fotografia completa del form di MlLab.razor — usata sia per i preset/memoria dell'ultima configurazione, sia come input dei metodi di orchestrazione (train/backtest/save).

### 🧾 `MlActionResult` `(string Message, bool IsError)`

> Esito di un'azione con messaggio per l'operatore.

| | Firma | Descrizione |
|---|---|---|
| `m` | `MlActionResult Ok(string message)` | — |
| `m` | `MlActionResult Error(string message)` | — |

### 🧾 `MlLoadResult` `(string Message, bool IsError, string? Symbol, string? Timeframe, string? ModelType,`

> Esito del caricamento di un modello salvato: oltre al messaggio, i campi form che la UI deve riallineare.

### 📦 `MlLabService` `(`

> Orchestrazione estratta da Components/Pages/MlLab.razor (P1-5, PRD-CONSOLIDAMENTO- ARCHITETTURA.md §3.3): validazione, addestramento/backtest ML, tracking degli esperimenti, CRUD dei modelli salvati e (de)serializzazione validata dei preset — tutta la logica che prima viveva nel blocco @code del componente senza test indipendenti da Blazor. Il componente resta responsabile solo di ciò che è intrinsecamente Blazor: binding del form, ciclo di vita ( OnInitializedAsync / Dispose ), spinner _busy / _stage , StateHasChanged . Lo stato della "sessione di modello" (predittore addestrato, fattori, candele di test, risultato del backtest, liste salvate) vive qui perché è stato applicativo condiviso fra i passi train→backtest→save, non stato di UI. Registrato Scoped: in Blazor Server uno scope = un circuito, quindi un'istanza per sessione utente, come il componente che la consuma. [D1] Opzionale …

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;string&gt; KnownSymbols` | — |
| `p` | `List&lt;SavedMlModel&gt; SavedModels` | — |
| `p` | `List&lt;SavedFactor&gt; SavedFactors` | — |
| `p` | `bool HasTrainedModel` | — |
| `p` | `List&lt;FactorSpec&gt;? TrainedFactors` | — |
| `p` | `List&lt;OhlcvData&gt;? TestCandles` | — |
| `p` | `int TrainRowCount` | — |
| `p` | `double TrainCorrelation` | — |
| `p` | `List&lt;FeatureImportance&gt; FeatureImportance` | — |
| `p` | `MlTargetKind SessionTargetKind` | [1.V fase 2] Il target del modello IN SESSIONE (addestrato o caricato): è questo — non lo stato del form, che può cambiare dopo — a decidere cosa ha senso farci (backtest direzionale vs valutazione della previsione di v… |
| `p` | `int SessionForwardHorizon` | Orizzonte forward del modello in sessione (allineato a ). |
| `p` | `BacktestResult? Result` | — |
| `p` | `TearsheetMetrics? Tearsheet` | — |
| `p` | `List&lt;IndicatorSeries&gt; EquitySeries` | — |
| `p` | `bool ShapAvailable` | True se il modello in sessione è ad alberi (RandomForest/GradientBoosting): solo per questi TreeSHAP è definito. Per gli altri la UI resta sulla permutation importance senza fingere. |
| `p` | `ShapAnalysisResult? ShapAnalysis` | Analisi SHAP calcolata su richiesta (mai automaticamente: costa, e non serve sempre). |
| `p` | `ShapExplanation? ShapLocal` | Spiegazione locale della riga selezionata (waterfall). |
| `p` | `int ShapLocalRowIndex` | Indice della riga spiegata localmente, per mostrarne la data in UI. |
| `p` | `DateTime? ShapLocalTimestamp` | — |
| `p` | `int ShapRowCount` | Numero di righe di training disponibili per la spiegazione locale. |
| `p` | `VolForecastEvaluation? VolEvaluation` | [1.V fase 2] Esito della valutazione vol (QLIKE/MSE vs EWMA/naive) del modello in sessione. |
| `m` | `Task LoadInitialDataAsync(string? userId, CancellationToken ct = default)` | Simboli disponibili + fattori minati e modelli salvati dell'utente (OnInitializedAsync). |
| `m` | `Task LoadSavedModelsListAsync(string? userId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;MlActionResult&gt; TrainAsync(MlConfigSnapshot cfg, string? userId, CancellationToken ct = default)` | Addestra un predittore sul periodo di train (split cronologico) e ne calcola la diagnostica in-sample. Su successo popola lo stato di sessione (predittore, fattori, candele di test). La validazione dei dati (numero cand… |
| `m` | `Task&lt;MlActionResult&gt; BacktestAsync(MlConfigSnapshot cfg, CancellationToken ct = default)` | Backtesta il modello addestrato sul periodo di test mai visto. Popola Result/Tearsheet/EquitySeries. |
| `m` | `Task&lt;MlActionResult&gt; EvaluateVolForecastAsync(CancellationToken ct = default)` | Valuta il modello di volatilità in sessione sul periodo di test MAI visto in addestramento, contro le due baseline senza ML (EWMA λ=0,94 e naive "vol passata"). QLIKE è il verdetto: se il modello non batte l'EWMA out-of… |
| `m` | `Task&lt;MlActionResult&gt; ComputeShapAnalysisAsync(CancellationToken ct = default)` | Calcola l'analisi SHAP del modello in sessione: sintesi globale (importanza media con segno) e rottura per contesto. Su richiesta esplicita, mai automatica: è esatta ma costa, e la permutation importance basta finché no… |
| `m` | `MlActionResult ExplainRow(int rowIndex)` | Spiegazione locale di una singola riga di training: quanto ciascun fattore ha spostato QUELLA predizione rispetto alla baseline. viene serrato nell'intervallo valido invece di sollevare: la UI lo pilota con uno slider. |
| `m` | `Task&lt;MlActionResult&gt; SaveModelAsync(MlConfigSnapshot cfg, string modelName, string? userId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;MlLoadResult&gt; LoadSavedModelAsync(int id, DateTime from, DateTime to, string? userId, CancellationToken ct = default)` | Carica un modello salvato e ne prepara il backtest sull'intervallo / (nessuno split: il modello è già addestrato). Restituisce symbol/timeframe/ modelType del modello perché la UI riallinei i campi del form. |
| `m` | `Task DeleteSavedModelAsync(int id, string? userId, CancellationToken ct = default)` | — |

### 🧾 `ConfigDto` `(`

> Forma JSON dei preset — invariata rispetto al blocco @code originale, così i preset già salvati restano leggibili (enum come stringa).

| | Firma | Descrizione |
|---|---|---|
| `m` | `string SerializeConfig(MlConfigSnapshot cfg)` | — |
| `m` | `MlConfigSnapshot ApplyConfig(string json, MlConfigSnapshot current)` | Applica un preset alla configurazione : ogni campo con vincolo di catalogo (exchange/timeframe/modello/base-stacking/fattori) è preso dal preset SOLO se ancora valido, altrimenti si tiene il valore corrente; i campi lib… |
| `m` | `string OptimizationHandoffUrl(SavedMlModel m)` | Link a Optimization precompilata: strategia ML + questo modello, periodo che parte dalla fine del training (le soglie Long/Short si scelgono su dati che il modello non ha visto). |
| `m` | `void Dispose()` | — |

## `ProcioneMGR/Services/ML/MlModelLoader.cs`

### 📦 `MlModelLoader`

> Materializza un in una pronta all'uso (predittore deserializzato dal blob + ricostruiti dai fattori salvati). UNICO punto di verità del caricamento: lo usano sia il backtest (batch) sia il TradingEngine (streaming, Champion su lane Paper/Testnet), così un modello produce lo STESSO segnale nei due contesti — parità batch/stream per costruzione, nessuna logica duplicata che possa divergere.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReturnPredictor CreatePredictor(string modelType)` | Mappa SavedMlModel.ModelType al predittore concreto (default: lineare). |
| `m` | `Task&lt;IReturnPredictor&gt; LoadPredictorAsync(SavedMlModel saved, CancellationToken ct)` | Carica il solo dal blob, senza ricostruire né i . Usata dal servizio ml (Fase 2a), che riceve il vettore di input già pronto e non calcola mai fattori: evita a quell'host la dipendenza da / /Alpha158. |

## `ProcioneMGR/Services/ML/MlModels.cs`

### 🧾 `FactorSpec` `(string FeatureName, IAlphaFactor Factor, IReadOnlyDictionary&lt;string, decimal&gt; Para…`

> Un fattore alpha con i suoi parametri, associato a un nome di feature stabile (usato nelle colonne del dataset e nella feature-importance dei modelli). Più parametrizzazioni dello stesso fattore (es. Momentum a lookback diversi) sono feature distinte.

### 📦 `FeatureRow`

> Riga del dataset supervisionato: vettore di feature (fattori) + target (rendimento forward).

| | Firma | Descrizione |
|---|---|---|
| `p` | `float[] Features` | — |
| `p` | `float Label` | — |

### 📦 `MlDataset`

> Dataset supervisionato pronto per l'addestramento: righe allineate temporalmente (necessario per , che opera per indice di riga). La conversione a di ML.NET è on-demand tramite , così il chiamante controlla quale usare (stesso context per training/predict = determinismo).

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;FeatureRow&gt; Rows` | — |
| `p` | `IReadOnlyList&lt;string&gt; FeatureNames` | — |
| `p` | `IReadOnlyList&lt;DateTime&gt; Timestamps` | — |
| `p` | `int RowCount` | — |
| `p` | `int FeatureCount` | — |
| `m` | `IDataView ToDataView(MLContext mlContext)` | Vista ML.NET dell'intero dataset. |
| `m` | `IDataView ToDataView(MLContext mlContext, IReadOnlyList&lt;int&gt; indices)` | Vista ML.NET di un sottoinsieme di righe (per i fold della cross-validation). |

### 📦 `MlDatasetView`

> Costruzione dell' ML.NET da righe con vettore feature a dimensione dinamica.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IDataView Create(MLContext mlContext, IEnumerable&lt;FeatureRow&gt; rows, int featureCount)` | — |

### 🧾 `CvSplit` `(int Fold, IReadOnlyList&lt;int&gt; TrainIndices, IReadOnlyList&lt;int&gt; TestIndices);`

> Uno split train/test prodotto dalla cross-validation temporale (indici di riga in ).

### 🧾 `FeatureImportance` `(string FeatureName, double MeanDecreaseInRSquared, double StdDevDecreaseInRSquared);`

> Importanza di una feature per un modello addestrato, da permutation importance: quanto peggiora la qualità delle predizioni (calo di R²) quando quella feature viene mescolata casualmente, a parità delle altre. Più alta -&gt; la feature conta di più per il modello.

### 🧾 `SavedFactorSpecDto` `(string FeatureName, string FactorName, Dictionary&lt;string, decimal&gt; Parameters);`

> DTO serializzabile di un (l'interfaccia IAlphaFactor non lo è). Usato per persistere/ricostruire i fattori di un SavedMlModel : il nome del fattore si ricrea via IAlphaFactorFactory.Create , i parametri sono già serializzabili.

## `ProcioneMGR/Services/ML/MlStageMapper.cs`

### 📦 `MlStageMapper`

> Mappatura esplicita bidirezionale fra (dominio/DB) e (contratto gRPC). Switch, NON cast ordinale: un cast si romperebbe in silenzio se uno dei due enum venisse riordinato o esteso: qui invece un valore non gestito lancia rumorosamente (fail-loud), così una divergenza fra i due enum diventa un errore di compilazione/test invece di una predizione servita col modello sbagliato.

| | Firma | Descrizione |
|---|---|---|
| `m` | `ProtoStage ToProto(DataStage stage)` | — |
| `m` | `DataStage FromProto(ProtoStage stage)` | — |

## `ProcioneMGR/Services/ML/MlTargetKind.cs`

### 🔢 `MlTargetKind`

> [1.V roadmap macchina-ricerca] Cosa predice il modello. Storicamente solo il rendimento forward; dopo 445.280 combinazioni direzionali a zero sopravvissuti, la mossa onesta è predire il RISCHIO: la volatilità è persistente (stesso fatto stilizzato dietro il GARCH già in piattaforma) ed è prevedibile anche quando la direzione non lo è.

### 📦 `ForwardTargets`

> Calcolo dei target forward. Un TARGET guarda avanti per costruzione (è l'etichetta): il contratto anti-look-ahead riguarda le FEATURE, che a indice i vedono solo candles[0..i]. Il valore a i usa esclusivamente le barre (i, i+orizzonte] — mai oltre l'orizzonte dichiarato.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, int horizon, MlTargetKind kind)` | — |

## `ProcioneMGR/Services/ML/MlpReturnPredictor.cs`

### 📦 `MlpReturnPredictor` `(int hiddenUnits = 16, int epochs = 200, double learningRate = 0.01, int seed = 42)`

> Rete neurale feed-forward (MLP) per la previsione dei rendimenti — l'essenza del cap. 17 di ML4T (Jansen) in C# puro, SENZA TorchSharp: un solo strato nascosto con attivazione tanh, uscita lineare, addestramento con mini-batch gradient descent e L2 (weight decay). Scelte di progetto: - implementa direttamente (non RegressionPredictorBase , che incapsula un ITransformer ML.NET): Fit legge le righe dall'IDataView, il modello vive in array C# e la persistenza e' JSON (pesi + normalizzazione + config); - feature standardizzate su media/deviazione del TRAIN (come SDCA: le reti sono sensibili alla scala; parametri salvati per l'inferenza); - deterministico a parita' di seed (inizializzazione pesi e shuffling dei batch); - early stop implicito via numero fisso di epoche + weight decay (nessun validation split interno: la valutazione onesta e' gia' fuori, in PurgedTimeSeriesCv / split temporale…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `bool IsFitted` | — |
| `m` | `void Fit(MLContext mlContext, IDataView trainingData)` | — |
| `m` | `float Predict(float[] features)` | — |

### 🧾 `MlpState` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `void Save(MLContext mlContext, string path)` | — |
| `m` | `void Load(MLContext mlContext, string path)` | — |
| `m` | `void Dispose()` | Nessuna risorsa nativa: il modello vive in array gestiti. |

## `ProcioneMGR/Services/ML/PurgedTimeSeriesCv.cs`

### 📦 `PurgedTimeSeriesCv` `: IPurgedTimeSeriesCv`

> Implementazione di . Stateless -&gt; registrabile Singleton.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;CvSplit&gt; Split(int sampleCount, int folds, int purgeWindow, int embargoPeriods)` | — |

## `ProcioneMGR/Services/ML/RandomForestReturnPredictor.cs`

### 📦 `RandomForestReturnPredictor` `: RegressionPredictorBase`

> Random Forest (ML.NET FastForest) per la previsione del rendimento forward — cap. 11. Non lineare, cattura interazioni fra fattori che il modello lineare non vede. Gli alberi sono invarianti alla scala delle feature: nessuna normalizzazione necessaria (a differenza di ).

| | Firma | Descrizione |
|---|---|---|
| `c` | `RandomForestReturnPredictor(int numberOfTrees = 100, int numberOfLeaves = 20)` | — |
| `p` | `string Name` | — |
| `m` | `IEstimator&lt;ITransformer&gt; BuildPipeline(MLContext mlContext)` | — |

## `ProcioneMGR/Services/ML/RegressionPredictorBase.cs`

### 📦 `PredictedReturn`

> Colonna di output dei trainer di regressione ML.NET.

| | Firma | Descrizione |
|---|---|---|
| `p` | `float Score` | — |

### 🔌 `IShapExplainable`

> [D1] Modelli di cui si può estrarre la struttura ad alberi per TreeSHAP. Interfaccia separata da di proposito: SHAP ad albero non si applica ai modelli lineari, MLP o attention, e mettere il metodo sull'astrazione generale costringerebbe metà delle implementazioni a dichiarare "non supportato".

| | Firma | Descrizione |
|---|---|---|
| `m` | `ShapTreeEnsemble? TryBuildShapEnsemble(IReadOnlyList&lt;float[]&gt; background)` | Ensemble di alberi in forma neutra, con la copertura dei nodi misurata su ; null se il modello addestrato non è ad alberi. |

### 📦 `RegressionPredictorBase` `: IReturnPredictor, IShapExplainable`

> Infrastruttura comune a tutti i predittori di rendimento basati su un singolo ITransformer di regressione ML.NET con colonne Features/Label: gestione schema (vettore a dimensione dinamica), prediction engine, persistenza, permutation feature importance. Le sottoclassi implementano solo — la scelta del trainer (SDCA, FastForest, LightGBM, ...) è l'unica cosa che le distingue.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `bool IsFitted` | — |
| `p` | `int PermutationImportanceSeed` | [G-08] Seed delle permutazioni della feature importance. Era un 42 CABLATO nel corpo del metodo: deterministico, ma invisibile e non variabile — chi stima la varianza fra seed diversi non toccava questo ramo e la sottos… |
| `m` | `IEstimator&lt;ITransformer&gt; BuildPipeline(MLContext mlContext)` | Costruisce la pipeline di addestramento (eventuale pre-processing + trainer). |
| `m` | `void Fit(MLContext mlContext, IDataView trainingData)` | — |
| `m` | `float Predict(float[] features)` | — |
| `m` | `void Save(MLContext mlContext, string path)` | — |
| `m` | `void Load(MLContext mlContext, string path)` | — |
| `m` | `IReadOnlyList&lt;FeatureImportance&gt; ComputeFeatureImportance(MLContext mlContext, IDataView evaluationData, IReadOnlyList&lt;string&gt; featureNam…` | — |
| `m` | `ShapTreeEnsemble? TryBuildShapEnsemble(IReadOnlyList&lt;float[]&gt; background)` | — |
| `m` | `void Dispose()` | Il incapsula risorse native di ML.NET: senza Dispose, ogni predittore creato (es. uno per combo/finestra in uno sweep di Optimization, vedi BacktestEngine.LoadMlStrategyAsync ) le perde silenziosamente. |

## `ProcioneMGR/Services/ML/ReturnPredictorCatalog.cs`

### 📦 `ReturnPredictorCatalog`

> Crea i predittori di rendimento BASE per nome (Linear/RandomForest/GradientBoosting/Mlp). Centralizza lo switch che prima viveva duplicato in /ml, così sia la UI sia lo (che deve istanziare freschi i modelli base per l'OOF) usano la stessa fonte. Non include "Stacked" per costruzione: un ensemble di ensemble non ha senso qui e creerebbe ricorsione.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;string&gt; BaseTypes` | Tipi base combinabili in uno stacking. |
| `m` | `IReturnPredictor CreateBase(string modelType)` | — |

## `ProcioneMGR/Services/ML/RiskFactorPca.cs`

### 📦 `RiskFactorPca` `: IRiskFactorPca`

> Implementazione di via eigen-decomposizione (MathNet.Numerics). DEVIAZIONE FLAGGATA rispetto al piano (che indicava ML.NET): usiamo MathNet.Numerics invece di mlContext.Transforms.ProjectToPrincipalComponents perché quest'ultimo non espone pubblicamente gli autovalori, necessari per calcolare la varianza spiegata per componente — un dato imprescindibile in finanza per capire quanto rischio comune cattura ogni fattore. MathNet dà accesso diretto e verificabile ad autovalori/autovettori.

| | Firma | Descrizione |
|---|---|---|
| `m` | `RiskFactorPcaResult Compute(IReadOnlyDictionary&lt;string, IReadOnlyList&lt;decimal&gt;&gt; returnsBySymbol, int componentCount)` | — |

## `ProcioneMGR/Services/ML/RiskFactorPcaModels.cs`

### 📦 `PrincipalComponent`

> Una componente principale: quota di varianza spiegata, loading per simbolo, e la serie temporale del fattore.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Index` | Indice della componente, 1-based (1 = quella con più varianza spiegata). |
| `p` | `double ExplainedVarianceRatio` | Quota di varianza totale spiegata da questa componente, in [0,1]. |
| `p` | `IReadOnlyDictionary&lt;string, double&gt; Loadings` | Peso (coefficiente dell'autovettore) di ciascun simbolo su questa componente. |
| `p` | `IReadOnlyList&lt;double&gt; Scores` | Il "risk factor" stesso: punteggio della componente per ogni osservazione temporale. |

### 📦 `RiskFactorPcaResult`

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;string&gt; Symbols` | — |
| `p` | `IReadOnlyList&lt;PrincipalComponent&gt; Components` | — |
| `p` | `double TotalExplainedVarianceRatio` | Somma delle quote di varianza spiegata dalle componenti estratte (quanto del rischio comune è catturato). |

## `ProcioneMGR/Services/ML/SequenceWindowing.cs`

### 📦 `SequenceWindowing`

> Trasforma un dataset puntuale (una riga = i fattori di UNA candela, in ordine temporale) in un dataset a SEQUENZE per i modelli che ragionano su una finestra (es. ): la riga i diventa la concatenazione dei vettori di fattori delle candele [i−T+1 .. i], con label quella della candela i. Layout appiattito in ordine temporale (dal più vecchio al più recente), coerente con ciò che l'attention si aspetta. I fattori restano gli stessi (nessun cambiamento a DatasetBuilder o al FactorsJson salvato): la finestra è una vista sulle stesse feature, non nuove feature. CONTIGUITÀ TEMPORALE: si emette una finestra SOLO se le T candele sono contigue nel tempo (spaziatura uniforme, nessuna lacuna). Questo rende il windowing di training identico a quello che MlStrategy costruisce a inferenza — se il dataset compattato salta candele (fattori non calcolabili a metà serie), la finestra a cavallo del salto v…

| | Firma | Descrizione |
|---|---|---|
| `m` | `MlDataset Build(MlDataset pointwise, int windowLength)` | — |
| `m` | `long InferStepTicks(IReadOnlyList&lt;DateTime&gt; timestamps)` | Passo temporale della serie = minima differenza positiva fra timestamp consecutivi (il timeframe, quando le candele sono sulla griglia). 0 se indeterminabile. |
| `m` | `bool IsContiguous(IReadOnlyList&lt;DateTime&gt; timestamps, int start, int end, long stepTicks)` | True se i timestamp negli indici [ .. ] sono contigui, cioè ogni coppia consecutiva dista esattamente . |

## `ProcioneMGR/Services/ML/Shap/MlNetTreeExtractor.cs`

### 📦 `MlNetTreeExtractor`

> Estrae la struttura degli alberi da un ML.NET addestrato (FastForest o LightGBM) e la converte nella forma neutra , calcolando la copertura per nodo su un dataset di background. Restituisce null — non solleva — quando il modello non è ad alberi (lineare, MLP, attention, stacking): SHAP ad albero semplicemente non si applica, e il chiamante deve poter ripiegare sulla permutation importance senza gestire eccezioni per un caso previsto.

| | Firma | Descrizione |
|---|---|---|
| `m` | `ShapTreeEnsemble? TryExtract(ITransformer model, IReadOnlyList&lt;float[]&gt; background, int featureCount)` | Converte un modello ML.NET in ensemble neutro. è il dataset da cui si misura la copertura dei nodi (tipicamente il train set): senza di esso non esisterebbe una distribuzione rispetto a cui definire "feature assente". |

## `ProcioneMGR/Services/ML/Shap/ShapAnalysis.cs`

### 🧾 `ShapContextCell` `(string Context, string FeatureName, double MeanAbsShap, int Rows);`

> Una cella della matrice contesto × fattore: importanza media del fattore in quel contesto.

### 🧾 `ShapContextLens` `(`

> La lente con cui le righe vengono raggruppate nella matrice: le etichette per timestamp, l'ordine stabile delle colonne e un nome leggibile da mostrare in UI.

### 🧾 `ShapAnalysisResult` `(`

> Esito completo dell'analisi SHAP di un modello: sintesi globale e rottura per contesto. dice CON QUALE lente le colonne sono state costruite — senza, la matrice mostrerebbe numeri senza dire come sono raggruppati.

### 📦 `ShapAnalyzer`

> Orchestrazione dell'analisi SHAP sopra : campionamento delle righe, sintesi globale e rottura per contesto. Quale lente. La lente preferita sono i regimi K-means della pagina /regimes (PRD §5a): raggruppare i contributi per stato di mercato riconosciuto dalla piattaforma è più informativo di una terzina calcolata al volo. Ma quel modello dev'essere ATTIVO e della STESSA serie del modello ML, e spesso non lo è — per questo la lente arriva dall'esterno ( ) e, quando manca, si ripiega sui terzili di volatilità realizzata, sempre calcolabili dalle candele che il modello ha già. Il pannello non resta mai vuoto, e dichiara sempre quale delle due si sta guardando. Qui il regime NON decide nulla. È un asse di raggruppamento descrittivo, non un criterio operativo: non deve superare alcun gate di discriminazione. È la differenza con LaneRegimeRouter , che invece consulta il modello attivo per fil…

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;string&gt; VolatilityLabels` | Etichette della lente di ripiego, dalla più calma alla più mossa. |
| `k` | `string VolatilityLensName` | Nome leggibile della lente di ripiego. |
| `m` | `ShapContextLens BuildVolatilityLens(IReadOnlyList&lt;OhlcvData&gt; candles, int lookback = 20)` | Lente di RIPIEGO: etichetta ogni candela con la terzina di volatilità realizzata a cui appartiene. Le soglie sono i terzili della distribuzione dell'intero periodo analizzato — una definizione relativa, non una soglia a… |

## `ProcioneMGR/Services/ML/Shap/ShapTree.cs`

### 📦 `ShapTree`

> Un albero di regressione in forma neutra, con indicizzazione unificata nodi+foglie e la copertura per nodo calcolata da un dataset di background.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int[] Left` | Figlio sinistro per nodo (−1 sulle foglie). Si scende a sinistra quando x[feature] &lt;= soglia . |
| `p` | `int[] Right` | Figlio destro per nodo (−1 sulle foglie). |
| `p` | `int[] SplitFeature` | Indice della feature su cui splitta il nodo (irrilevante sulle foglie). |
| `p` | `double[] Threshold` | Soglia dello split (irrilevante sulle foglie). |
| `p` | `double[] Value` | Valore predetto dal nodo se è foglia (0 sui nodi interni). |
| `p` | `double[] Cover` | Numero di campioni di background che attraversano il nodo. |
| `p` | `int MaxDepth` | Profondità massima dell'albero — dimensiona i buffer di TreeSHAP. |
| `p` | `int NodeCount` | — |
| `m` | `bool IsLeaf(int node)` | — |
| `m` | `double ChildFraction(int node, int child)` | Frazione della copertura del nodo che finisce nel figlio indicato. Se nessun campione di background raggiunge il nodo la copertura è zero e il rapporto sarebbe 0/0: in quel caso si ripiega su 1/2 e 1/2. Non è una toppa … |
| `m` | `double Predict(ReadOnlySpan&lt;float&gt; features)` | Predizione dell'albero per un vettore di feature (traversata secca). |
| `m` | `double ExpectedValue()` | Valore atteso dell'albero sulla distribuzione di background, calcolato propagando le stesse frazioni di copertura usate da TreeSHAP. È il "punto zero" da cui partono i contributi SHAP: somma(shap) + atteso == predizione… |

### 📦 `ShapTreeEnsemble`

> Un ensemble di alberi in forma neutra: predizione = Bias + Σ peso_i · albero_i(x) .

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;ShapTree&gt; Trees` | — |
| `p` | `IReadOnlyList&lt;double&gt; Weights` | Peso di ciascun albero nella somma (LightGBM: 1; FastForest: 1/numeroAlberi). |
| `p` | `double Bias` | — |
| `p` | `int FeatureCount` | Numero di feature del vettore di input (serve a dimensionare i vettori SHAP). |
| `m` | `double Predict(ReadOnlySpan&lt;float&gt; features)` | Predizione ricostruita dalla struttura estratta. Esiste per essere CONFRONTATA con la predizione del modello ML.NET vero: se le due divergono, l'estrazione ha frainteso la convenzione degli alberi e ogni valore SHAP a v… |
| `m` | `double ExpectedValue()` | Valore atteso dell'ensemble sul background: la baseline dei contributi SHAP. |

## `ProcioneMGR/Services/ML/Shap/TreeShapExplainer.cs`

### 🧾 `ShapContribution` `(string FeatureName, double Value, float FeatureValue);`

> Contributo di una singola feature a una singola predizione.

### 🧾 `ShapExplanation` `(`

> Spiegazione locale di una predizione: Baseline + Σ Contributi == Prediction (efficienza, verificata dai test).

### 🧾 `ShapSummaryRow` `(`

> Riga della sintesi globale: importanza media e direzione prevalente di una feature.

| | Firma | Descrizione |
|---|---|---|
| `p` | `double DirectionalConsistency` | Quanto il contributo è direzionalmente coerente: \|media\| / media\|·\|. Vicino a 1 = la feature spinge quasi sempre dalla stessa parte; vicino a 0 = spinge in su e in giù a seconda del contesto (interazione o non-monot… |

### 📦 `TreeShapExplainer` `(ShapTreeEnsemble ensemble)`

> TreeSHAP path-dependent su un . Deterministico e senza stato: stessa istanza riutilizzabile su più righe.

| | Firma | Descrizione |
|---|---|---|
| `p` | `double Baseline` | Valore atteso sul background: il punto di partenza di ogni spiegazione. |
| `m` | `double[] Explain(ReadOnlySpan&lt;float&gt; features)` | Valori SHAP grezzi per una riga, indicizzati per posizione di feature. La somma più ricostruisce la predizione dell'ensemble. |
| `m` | `ShapExplanation ExplainRow(float[] features, IReadOnlyList&lt;string&gt; featureNames)` | Spiegazione locale già confezionata per la UI (contributi ordinati per \|valore\|). |
| `m` | `IReadOnlyList&lt;ShapSummaryRow&gt; Summarize(IReadOnlyList&lt;float[]&gt; rows, IReadOnlyList&lt;string&gt; featureNames)` | Sintesi globale su un campione di righe: importanza media (media dei \|SHAP\|) e direzione media per feature. È l'equivalente SHAP del summary plot, ordinato per importanza. |

### ▫️ `PathElement`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int FeatureIndex` | — |
| `p` | `double FractionZero` | — |
| `p` | `double FractionOne` | — |
| `p` | `double Weight` | — |

## `ProcioneMGR/Services/ML/StackedReturnPredictor.cs`

### 🔢 `StackingMode`

> Come combinare le predizioni dei modelli base in una singola predizione.

### 📦 `StackedReturnPredictor` `: IReturnPredictor`

> Ensemble di modelli a livello di PREDIZIONE (stacking), non di strategia (rif. docs/archive/ROADMAP-QLIB.md §1.8 ). Implementa , quindi si inserisce senza modifiche in tutto ciò che consuma quell'interfaccia ( MlStrategy , SavedMlModel con ModelType="Stacked" , /ml, /optimization, /ensemble) — stesso pattern con cui MlpReturnPredictor si è inserito. La predizione finale è una combinazione lineare delle predizioni dei modelli base: ŷ = intercetta + Σ wᵢ·baseᵢ(x) . I pesi si stimano secondo . Per si usano predizioni OUT-OF-FOLD ottenute con : nessun modello base vede le proprie predizioni di training nel meta-training (niente leakage).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `bool IsFitted` | — |
| `p` | `int PermutationImportanceSeed` | [G-08] Seed delle permutazioni della feature importance — stesso contratto e stesso default di . |
| `c` | `StackedReturnPredictor(IPurgedTimeSeriesCv? cv = null)` | Costruttore per il CARICAMENTO: lo stato reale arriva da . |
| `m` | `void Fit(MLContext mlContext, IDataView trainingData)` | — |
| `m` | `float Predict(float[] features)` | — |
| `m` | `(double[] Weights, double Intercept) FitNonNegativeRidge(double[][] basePredictions, double[] targets, double lambda)` | Ridge NON-NEGATIVO (pesi dei base ≥ 0, intercetta libera) via coordinate descent sulle normali equazioni: minimizza \|\|y − Xβ\|\|² + λ·Σ_{j≥1} β_j² con X = [1 \| predizioni base]. I pesi negativi nello stacking estrapo… |
| `m` | `double SelectLambdaByCv(double[][] basePredictions, double[] targets, int k, double fallbackLambda)` | K-fold sul livello meta per scegliere λ dalla griglia che minimizza l'MSE di validazione. |

### 🧾 `StackMeta` `(List&lt;string&gt; BaseTypes, string Mode, double[] Weights, double Intercept, int Featu…`

| | Firma | Descrizione |
|---|---|---|
| `m` | `void Save(MLContext mlContext, string path)` | — |
| `m` | `void Load(MLContext mlContext, string path)` | — |
| `m` | `IReadOnlyList&lt;FeatureImportance&gt; ComputeFeatureImportance(MLContext mlContext, IDataView evaluationData, IReadOnlyList&lt;string&gt; featureNam…` | — |
| `m` | `void Dispose()` | — |

## `ProcioneMGR/Services/ML/VolForecastEvaluator.cs`

### 🧾 `VolForecastEvaluation` `(`

> Esito del confronto out-of-sample fra la previsione di volatilità del modello e le due baseline senza ML: EWMA (RiskMetrics, λ=0,94) e naive (la vol realizzata delle ULTIME barre proiettata in avanti). QLIKE è la metrica principale — è la loss robusta standard per le previsioni di varianza (penalizza le sottostime, che per il risk management sono l'errore costoso); l'MSE sulla vol è il contorno intuitivo. Il verdetto onesto: se il modello non batte l'EWMA, il vol-targeting deve continuare a usare la misura semplice.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool ModelBeatsEwma` | — |
| `p` | `bool ModelBeatsNaive` | — |

### 📦 `VolForecastEvaluator`

> [1.V fase 2] Calcoli puri per la valutazione delle previsioni di volatilità. Tutte le serie sono CAUSALI: il valore all'indice i usa solo informazione fino a i incluso (le previsioni si confrontano poi col target forward della stessa barra, che guarda avanti per costruzione).

| | Firma | Descrizione |
|---|---|---|
| `k` | `double MinForecast` | Pavimento per le previsioni non positive (un modello lineare può produrre vol negative). |
| `m` | `double?[] EwmaPerBarVol(IReadOnlyList&lt;OhlcvData&gt; candles, double lambda = 0.94)` | Vol PER-BARRA prevista dall'EWMA RiskMetrics all'indice i, usando i rendimenti fino a i incluso: σ²_i = λ·σ²_{i-1} + (1−λ)·r_i². Seed = media dei quadrati dei primi min(20, n) rendimenti (null prima che esista almeno un… |
| `m` | `double?[] PastRealizedVol(IReadOnlyList&lt;OhlcvData&gt; candles, int horizon)` | Baseline naive: la vol realizzata (per-barra, campionaria) delle ULTIME barre fino a i incluso — "domani come ieri". Null finché la storia non basta. |
| `m` | `double Qlike(double predictedVol, double actualVol)` | QLIKE su scala varianza per una coppia (previsione, realizzato) in vol: L = σ²/h − ln(σ²/h) − 1, con h = pred² (pavimentata a ). Zero per previsione perfetta, sempre ≥ 0. |
| `m` | `(double Qlike, double Mse, int Rows) Score(IReadOnlyList&lt;double?&gt; predicted, IReadOnlyList&lt;double?&gt; actual)` | Aggrega QLIKE medio e MSE (su scala vol) sulle coppie valide: entrambe le serie non-null e realizzato &gt; 0 (con vol realizzata nulla il QLIKE diverge e la barra non informa). |

# `Services/Alpha/`

## `ProcioneMGR/Services/Alpha/Alpha158/Alpha158Catalog.cs`

### 🧾 `OpDescriptor` `(`

> Descrittore di un operatore Alpha158: codice tecnico, categoria, se è parametrizzato da un orizzonte rolling, e la funzione di calcolo (causale) che riceve le serie e l'orizzonte.

### 📦 `Alpha158Factor` `: IAlphaFactor`

> Un fattore alpha generato da un a un orizzonte fisso. Implementa la stessa interfaccia degli 8 fattori scritti a mano: si innesta senza modifiche in FactorEvaluator , DatasetBuilder , MlStrategy e nella UML. L'orizzonte è "cotto" nell'istanza (non un parametro runtime): ogni combinazione operatore×orizzonte è una feature distinta con un univoco e stabile (es. A158_ROC_20 ), così il round-trip di persistenza esistente ( SavedFactorSpecDto → IAlphaFactorFactory.Create(Name) ) funziona senza cambiare nulla: è vuoto e il nome basta a ricostruire il fattore.

| | Firma | Descrizione |
|---|---|---|
| `c` | `Alpha158Factor(OpDescriptor op, int horizon)` | — |
| `p` | `int Horizon` | Orizzonte rolling (in candele); 0 per i fattori di forma candela (KBAR), orizzonte-indipendenti. |
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; parameters)` | — |

### 📦 `Alpha158Catalog`

> Catalogo Alpha158: pochi operatori rolling causali × più orizzonti, generati come istanze invece di scrivere ~150 classi a mano (rif. docs/archive/ROADMAP-QLIB.md §1.1 ). Nessuna nuova infrastruttura di valutazione: il catalogo alimenta gli stessi FactorEvaluator / DatasetBuilder già equivalenti ad Alphalens.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int[] DefaultHorizons` | Orizzonti rolling di default (come Alpha158 di Qlib): 5/10/20/30/60 candele. |
| `p` | `int OperatorCount` | Numero di operatori distinti (KBAR + rolling). |
| `m` | `IReadOnlyList&lt;IAlphaFactor&gt; BuildCatalog(IEnumerable&lt;int&gt;? horizons = null)` | Genera l'intero catalogo: ogni operatore di forma candela una volta, ogni operatore rolling per ciascun orizzonte. Con gli orizzonti di default produce ~150 feature distinte. |
| `m` | `bool TryCreate(string name, out IAlphaFactor factor)` | Ricostruisce un fattore Alpha158 dal suo (es. A158_ROC_20 , A158_KMID ). Necessario perché IAlphaFactorFactory.Create deve poter riottenere per nome qualunque feature persistita in un SavedMlModel , per QUALSIASI orizzo… |

## `ProcioneMGR/Services/Alpha/Alpha158/RollingOps.cs`

### ▫️ `Bars`

> Serie di prezzo/volume estratte una sola volta da uno storico di candele, in (coerente con il resto della piattaforma), pronte per gli operatori rolling di . Tutte le colonne hanno la stessa lunghezza dell'input.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal[] Open` | — |
| `p` | `decimal[] High` | — |
| `p` | `decimal[] Low` | — |
| `p` | `decimal[] Close` | — |
| `p` | `decimal[] Volume` | — |
| `p` | `int Count` | — |
| `c` | `Bars(IReadOnlyList&lt;OhlcvData&gt; candles)` | — |

### 📦 `RollingOps`

> Operatori rolling causali in stile Alpha158 di Qlib, reimplementati in C#/decimal. INVARIANTE ANTI-LOOK-AHEAD (identico a ): il valore all'indice i dipende ESCLUSIVAMENTE dalla finestra che termina a i (indici ≤ i). Ogni metodo restituisce una serie allineata all'input (stessa lunghezza), con null nel warm-up o dove il valore non è calcolabile (divisione per zero, dati insufficienti). Molti operatori sono normalizzati sul prezzo/volume corrente per renderli comparabili fra simboli e regimi (es. Ma = SMA(close,d)/close ), esattamente come in Alpha158.

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal?[] Roc(decimal[] close, int d)` | ROC: rapporto prezzo di d periodi fa / prezzo corrente (Qlib: Ref($close,d)/$close). |
| `m` | `decimal?[] Ma(decimal[] close, int d)` | MA: media mobile del prezzo, normalizzata sul prezzo corrente. |
| `m` | `decimal?[] Std(decimal[] close, int d)` | STD: deviazione standard del prezzo, normalizzata sul prezzo corrente. |
| `m` | `decimal?[] Beta(decimal[] close, int d)` | BETA: pendenza (slope) della regressione lineare del prezzo su d barre, /prezzo. |
| `m` | `decimal?[] Rsqr(decimal[] close, int d)` | RSQR: R² della regressione lineare del prezzo su d barre (bontà del trend, 0..1). |
| `m` | `decimal?[] Resi(decimal[] close, int d)` | RESI: residuo della regressione lineare all'ultimo punto, normalizzato sul prezzo. |
| `m` | `decimal?[] Max(decimal[] high, decimal[] close, int d)` | MAX: massimo dei massimi su d barre, normalizzato sul prezzo corrente. |
| `m` | `decimal?[] Min(decimal[] low, decimal[] close, int d)` | MIN: minimo dei minimi su d barre, normalizzato sul prezzo corrente. |
| `m` | `decimal?[] Qtlu(decimal[] close, int d)` | QTLU: quantile alto (0.8) del prezzo su d barre, normalizzato sul prezzo corrente. |
| `m` | `decimal?[] Qtld(decimal[] close, int d)` | QTLD: quantile basso (0.2) del prezzo su d barre, normalizzato sul prezzo corrente. |
| `m` | `decimal?[] Rank(decimal[] close, int d)` | RANK: rango percentile causale del prezzo corrente nella finestra di d barre (0..1). |
| `m` | `decimal?[] Rsv(decimal[] high, decimal[] low, decimal[] close, int d)` | RSV: posizione stocastica del prezzo nel range [min-low, max-high] su d barre (0..1). |
| `m` | `decimal?[] Imax(decimal[] high, int d)` | IMAX: recenza del massimo (posizione 0=più vecchia .. d-1=più recente) /d. |
| `m` | `decimal?[] Imin(decimal[] low, int d)` | IMIN: recenza del minimo (posizione 0=più vecchia .. d-1=più recente) /d. |
| `m` | `decimal?[] Imxd(decimal[] high, decimal[] low, int d)` | IMXD: differenza fra recenza del massimo e del minimo (-1..1). |
| `m` | `decimal?[] Corr(decimal[] close, decimal[] volume, int d)` | CORR: correlazione (Pearson) fra prezzo e log(volume) su d barre. |
| `m` | `decimal?[] Cord(decimal[] close, decimal[] volume, int d)` | CORD: correlazione fra variazione di prezzo e variazione di log-volume su d barre. |
| `m` | `decimal?[] Cntp(decimal[] close, int d)` | CNTP: frazione di barre in salita su d variazioni (0..1). |
| `m` | `decimal?[] Cntn(decimal[] close, int d)` | CNTN: frazione di barre in discesa su d variazioni (0..1). |
| `m` | `decimal?[] Cntd(decimal[] close, int d)` | CNTD: differenza fra frazione in salita e in discesa (-1..1). |
| `m` | `decimal?[] Sump(decimal[] close, int d)` | SUMP: quota di guadagno sul movimento assorbito totale (RSI-like, 0..1). |
| `m` | `decimal?[] Sumn(decimal[] close, int d)` | SUMN: quota di perdita sul movimento assoluto totale (0..1). |
| `m` | `decimal?[] Sumd(decimal[] close, int d)` | SUMD: differenza fra quota di guadagno e di perdita (-1..1). |
| `m` | `decimal?[] Vma(decimal[] volume, int d)` | VMA: media mobile del volume, normalizzata sul volume corrente. |
| `m` | `decimal?[] Vstd(decimal[] volume, int d)` | VSTD: deviazione standard del volume, normalizzata sul volume corrente. |
| `m` | `decimal?[] Wvma(decimal[] close, decimal[] volume, int d)` | WVMA: volatilità del flusso \|rendimento\|·volume (dispersione/attività, ≥0). |
| `m` | `decimal?[] Vsump(decimal[] volume, int d)` | VSUMP: quota di aumento del volume sul movimento assoluto totale (0..1). |
| `m` | `decimal?[] Vsumn(decimal[] volume, int d)` | VSUMN: quota di calo del volume sul movimento assoluto totale (0..1). |
| `m` | `decimal?[] Vsumd(decimal[] volume, int d)` | VSUMD: differenza fra quota di aumento e di calo del volume (-1..1). |
| `m` | `decimal?[] Kmid(Bars b)` | — |
| `m` | `decimal?[] Klen(Bars b)` | — |
| `m` | `decimal?[] Kmid2(Bars b)` | — |
| `m` | `decimal?[] Kup(Bars b)` | — |
| `m` | `decimal?[] Kup2(Bars b)` | — |
| `m` | `decimal?[] Klow(Bars b)` | — |
| `m` | `decimal?[] Klow2(Bars b)` | — |
| `m` | `decimal?[] Ksft(Bars b)` | — |
| `m` | `decimal?[] Ksft2(Bars b)` | — |

## `ProcioneMGR/Services/Alpha/AlphaFactorFactory.cs`

### 🔌 `IAlphaFactorFactory`

> Crea istanze di fattore per nome ed espone i "prototipi" per popolare la UI (elenco fattori + definizioni parametri). Gli 8 fattori "storici" restano uno switch case esplicito; il catalogo (pochi operatori × molti orizzonti) si aggiunge in blocco senza una classe per feature — stesso principio additivo del resto della piattaforma (rif. docs/archive/ROADMAP-QLIB.md §1.1 ).

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;IAlphaFactor&gt; Prototypes` | Istanze "vuote" per leggere DisplayName/Category/ParameterDefinitions nella UI. |
| `m` | `IAlphaFactor Create(string factorName)` | — |

### 📦 `AlphaFactorFactory` `: IAlphaFactorFactory`

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;IAlphaFactor&gt; Prototypes` | — |
| `m` | `IAlphaFactor Create(string factorName)` | — |

## `ProcioneMGR/Services/Alpha/AlphaModels.cs`

### 📦 `FactorEvaluationConfig`

> Configurazione della valutazione di un fattore.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int ForwardHorizon` | Orizzonte (in candele) del rendimento forward usato come target dell'IC. |
| `p` | `int Quantiles` | Numero di quantili in cui suddividere le osservazioni per l'analisi dei rendimenti. |
| `p` | `int RollingIcWindow` | Ampiezza (in osservazioni) della finestra per l'IC rolling, da cui si stima l'IR. |
| `p` | `int[] DecayHorizons` | Orizzonti su cui misurare il decadimento dell'IC. |

### 📦 `QuantileReturn`

> Rendimento medio forward per un quantile del fattore.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Quantile` | 1 = quantile con i valori di fattore più bassi ... N = più alti. |
| `p` | `int Count` | — |
| `p` | `decimal MeanForwardReturn` | — |

### 📦 `IcByHorizon`

> IC misurato a un dato orizzonte (per la curva di decadimento).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Horizon` | — |
| `p` | `double InformationCoefficient` | — |

### 📦 `FactorEvaluationResult`

> Esito completo della valutazione di un fattore.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string FactorName` | — |
| `p` | `string DisplayName` | — |
| `p` | `int Observations` | Numero di osservazioni valide (fattore e forward-return entrambi non null). |
| `p` | `double InformationCoefficient` | IC = correlazione di Spearman (rank) tra fattore e rendimento forward, full-sample. |
| `p` | `double PearsonCorrelation` | Correlazione di Pearson (lineare) tra fattore e rendimento forward, full-sample. |
| `p` | `double RollingIcMean` | Media degli IC rolling (per finestra). |
| `p` | `double RollingIcStd` | Deviazione standard degli IC rolling. |
| `p` | `double InformationRatio` | Information Ratio del fattore = RollingIcMean / RollingIcStd (stabilità del segnale). |
| `p` | `double IcConsistency` | Frazione di finestre rolling con IC dello stesso segno dell'IC full-sample. |
| `p` | `List&lt;QuantileReturn&gt; QuantileReturns` | — |
| `p` | `decimal TopMinusBottomSpread` | Spread top-bottom: rendimento medio del quantile più alto meno quello del più basso. |
| `p` | `List&lt;IcByHorizon&gt; IcDecay` | — |
| `p` | `double IcTStatistic` | t-statistic dell'IC con errore standard Newey-West (HAC) : robusto all'autocorrelazione indotta dai forward-return sovrapposti quando l'orizzonte &gt; 1. È la significatività DIFENDIBILE dell'IC (\|t\| ≳ 2 ≈ significati… |
| `p` | `double IcTStatisticNaive` | t-statistic "ingenua" dell'IC (t = IC·√((N−2)/(1−IC²)), assume osservazioni indipendenti). Con horizon&gt;1 sovrastima la significatività: è qui solo per confronto con . |
| `p` | `int NeweyWestLags` | Numero di lag usati per la correzione Newey-West (= horizon−1, la lunghezza dell'overlap). |

### 📦 `Correlation`

> Statistica di correlazione di rango/lineare (helper condiviso).

| | Firma | Descrizione |
|---|---|---|
| `m` | `double Pearson(IReadOnlyList&lt;double&gt; x, IReadOnlyList&lt;double&gt; y)` | Correlazione di Pearson tra due serie della stessa lunghezza. 0 se degenerata. |
| `m` | `double Spearman(IReadOnlyList&lt;double&gt; x, IReadOnlyList&lt;double&gt; y)` | Correlazione di Spearman = Pearson sui ranghi (gestione tie con rango medio). |
| `m` | `double SpearmanTStatNeweyWest(IReadOnlyList&lt;double&gt; x, IReadOnlyList&lt;double&gt; y, int lags)` | t-statistic dell'IC (Spearman) con errore standard Newey-West (HAC) , robusto all'autocorrelazione dei forward-return sovrapposti (horizon &gt; 1). Metodo: l'IC di Spearman è la media del prodotto dei ranghi standardizz… |
| `m` | `double TStatIndependent(double ic, int n)` | t-statistic "ingenua" dell'IC assumendo osservazioni indipendenti: t = IC·√((n−2)/(1−IC²)). |
| `m` | `double[] Ranks(IReadOnlyList&lt;double&gt; values, int n)` | Ranghi 1..n con rango medio per i valori a pari merito (fractional ranking). |

## `ProcioneMGR/Services/Alpha/FactorCache.cs`

### 📦 `FactorCacheOptions`

> Opzioni della cache dei fattori (sezione config "FactorCache").

| | Firma | Descrizione |
|---|---|---|
| `p` | `int MaxEntries` | Numero massimo di serie memorizzate; oltre, si sfrattano le più vecchie (FIFO). Default 512. |

### 🔌 `IFactorCache`

> Cache trasparente delle serie di fattori (Fase 4): memoizza IAlphaFactor.Compute per una coppia (fattore+parametri, insieme di candele), evitando il ricalcolo ripetuto degli stessi fattori (es. training ripetuti, dataset di discovery, backtest identici) e garantendo che train e serve vedano la STESSA serie per gli stessi input. È un semplice memoizzatore: non altera il valore calcolato (invariante cache == ricalcolo ), quindi non introduce look-ahead né skew. Thread-safe. Rif. Fase 4 (coerenza train-serve). CHIAVE = impronta del fattore (nome + parametri ordinati) + impronta dei dati (symbol, timeframe, numero candele, primo/ultimo timestamp). Se arrivano nuove candele (cambia numero o ultimo timestamp) la chiave cambia ⇒ miss ⇒ ricalcolo: nessun dato stantìo.

| | Firma | Descrizione |
|---|---|---|
| `p` | `long Hits` | — |
| `p` | `long Misses` | — |
| `p` | `int Count` | — |
| `m` | `void Clear()` | — |

### 📦 `FactorCache` `: IFactorCache`

| | Firma | Descrizione |
|---|---|---|
| `c` | `FactorCache(FactorCacheOptions? options = null)` | — |
| `p` | `long Hits` | — |
| `p` | `long Misses` | — |
| `p` | `int Count` | — |
| `m` | `void Clear()` | — |

## `ProcioneMGR/Services/Alpha/FactorDriftAnalyzer.cs`

### 🧾 `FactorIcPoint` `(DateTime WindowStartUtc, DateTime WindowEndUtc, double InformationCoefficient, int Obser…`

> Un punto della serie storica dell'IC: una finestra temporale e il suo IC.

### 🔢 `FactorDriftStatus`

> Verdetto sul fattore. L'ordine riflette la gravità crescente.

### 🧾 `FactorDriftReport` `(`

> Report di deriva per un singolo fattore.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsAlert` | Vero quando il fattore merita attenzione (indebolito o invertito). |

### 📦 `FactorDriftConfig`

> Parametri del monitor. I default sono allineati a quelli già in uso in /feature-selection .

| | Firma | Descrizione |
|---|---|---|
| `p` | `int ForwardHorizon` | Orizzonte del rendimento forward su cui si misura l'IC. |
| `p` | `int WindowSize` | Ampiezza della finestra in osservazioni utilizzabili. |
| `p` | `double MinAbsIc` | \|IC\| sotto il quale un fattore si considera non informativo per ragioni ECONOMICHE. 0,02 è la stessa soglia di sopravvivenza che /feature-selection usa da sempre: un criterio nuovo qui renderebbe i due pannelli incoer… |
| `p` | `double NoiseFloorZ` | Quanti errori standard servono perché un IC sia distinguibile da zero. 1,96 ≈ 95%. |
| `p` | `int RecentWindows` | Quante finestre finali compongono il "recente" da confrontare col riferimento. |
| `p` | `int MinWindows` | Minimo di finestre perché il verdetto abbia senso (riferimento + recente). |

### 🔌 `IFactorDriftAnalyzer`

> Calcola la serie storica dell'IC di un fattore e ne giudica la deriva. Puro e deterministico: nessun DB, nessun orologio, nessuno stato — come StrategyDecayMonitor .

| | Firma | Descrizione |
|---|---|---|
| `m` | `FactorDriftReport Analyze(FactorSpec spec, IReadOnlyList&lt;OhlcvData&gt; candles, FactorDriftConfig config)` | — |

### 📦 `FactorDriftAnalyzer` `: IFactorDriftAnalyzer`

| | Firma | Descrizione |
|---|---|---|
| `m` | `double NoiseFloorFor(int windowSize, double z = 1.96)` | PAVIMENTO DI RUMORE dell'IC su una finestra di osservazioni: z / √n , l'errore standard di una correlazione attorno a zero. Questo metodo esiste per un errore che i test hanno trovato nella prima versione di questa clas… |
| `m` | `int SuggestWindowSize(int observations)` | AMPIEZZA DI FINESTRA CONSIGLIATA per osservazioni utilizzabili: circa un decimo del campione (≈10 finestre non sovrapposte), **quantizzato a passi di 250** e tenuto fra 250 e 3000. Questa funzione esiste in UN SOLO post… |
| `m` | `FactorDriftReport Analyze(FactorSpec spec, IReadOnlyList&lt;OhlcvData&gt; candles, FactorDriftConfig config)` | — |

## `ProcioneMGR/Services/Alpha/FactorDriftMonitor.cs`

### 🧾 `FactorDriftSeriesSnapshot` `(`

> Fotografia dell'ultimo calcolo di deriva, per serie.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;FactorDriftReport&gt; Alerts` | — |

### 📦 `FactorDriftSnapshot`

> Ultima fotografia nota della deriva dei fattori, per tutte le serie monitorate. Singleton: scritto dal worker, letto dalla UI. Thread-safe per sostituzione atomica del dizionario.

| | Firma | Descrizione |
|---|---|---|
| `p` | `DateTime? LastRunUtc` | Ultimo istante in cui il worker ha completato un giro (null se non ancora girato). |
| `p` | `int TrackedSeriesCount` | Quante serie sono abilitate in watchlist quando la fotografia è stata composta. Serve alla UI per dichiarare la COPERTURA: "1 fattore in deriva" su 5 serie di 228 monitorate è vero e fuorviante, perché si legge come un … |
| `p` | `IReadOnlyCollection&lt;FactorDriftSeriesSnapshot&gt; All` | — |
| `p` | `IReadOnlyList&lt;(FactorDriftSeriesSnapshot Series, FactorDriftReport Report)&gt; Alerts` | Tutti i fattori in allarme, su tutte le serie, i più gravi per primi. |
| `m` | `void Replace(IEnumerable&lt;FactorDriftSeriesSnapshot&gt; snapshots, DateTime computedAtUtc, int trackedSeriesCount = 0)` | — |

### 📦 `FactorDriftWorker` `(`

> Calcola periodicamente la deriva dei fattori sulle serie della watchlist e aggiorna . Config: FactorDrift:Enabled (default true, è sola lettura e advisory), FactorDrift:IntervalHours (default 12), FactorDrift:MaxSeries (default 5), FactorDrift:MaxCandles (default 20000).

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |
| `m` | `Task HydrateAsync(CancellationToken ct = default)` | Ricostruisce la fotografia dalla storia registrata. Non fallisce mai in modo rumoroso: se il DB non è raggiungibile si riparte a vuoto, esattamente come prima che la tabella esistesse — un monitor advisory non deve pote… |
| `m` | `int WindowSizeFor(int candleCount)` | Ampiezza della finestra per una serie. **Delega alla regola condivisa** , che è anche quella del pannello di /feature-selection : due regole diverse producevano soglie diverse e quindi verdetti diversi sulla stessa seri… |
| `m` | `Task RunOnceAsync(CancellationToken ct = default)` | Un giro completo. Pubblico per poterlo esercitare nei test senza aspettare il timer. |

## `ProcioneMGR/Services/Alpha/FactorEvaluator.cs`

### 📦 `FactorEvaluator` `: IFactorEvaluator`

> Implementazione del valutatore di fattori. Stateless -&gt; registrato come Singleton. Metriche prodotte: - IC (Information Coefficient) : correlazione di Spearman tra il fattore alla candela i e il rendimento forward su H candele. Misura quanto l'ORDINAMENTO indotto dal fattore predice l'ordinamento dei rendimenti futuri. \|IC\| &gt; ~0.03 su molte osservazioni è già interessante nei mercati reali. - IR (Information Ratio) : media/deviazione degli IC calcolati su finestre rolling. Un fattore con IC modesto ma STABILE (IR alto) è preferibile a uno erratico. - Quantile

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;decimal?&gt; ForwardReturns(IReadOnlyList&lt;OhlcvData&gt; candles, int horizon)` | — |

## `ProcioneMGR/Services/Alpha/FactorIcHistory.cs`

### 📦 `FactorIcWindow`

> ENTITÀ EF (tabella FactorIcWindows ): l'IC di UN fattore su UNA finestra di UNA serie. La riga è l'osservazione elementare della deriva: la serie storica è l'insieme delle righe ordinate per . L'indice unico su (serie, fattore, orizzonte, ampiezza, fine finestra) rende la scrittura IDEMPOTENTE: il worker gira ogni 12 ore sulle stesse candele e ricalcola le stesse finestre — senza quel vincolo la tabella crescerebbe di un duplicato per giro, per sempre.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `string Symbol` | Serie di appartenenza (es. "BTC/USDT"). |
| `p` | `string Timeframe` | Timeframe della serie (es. "1h"). |
| `p` | `string FactorName` | Nome del fattore, come lo espone IAlphaFactor.Name . |
| `p` | `int ForwardHorizon` | Orizzonte del rendimento forward su cui l'IC è stato misurato (in barre). |
| `p` | `int WindowSize` | Ampiezza della finestra in osservazioni. Fa parte della chiave logica perché un IC su 500 osservazioni e uno su 2000 sono misure DIVERSE: il pavimento di rumore è 1,96/√n, quindi mescolarle nella stessa serie confronter… |
| `p` | `DateTime WindowStartUtc` | — |
| `p` | `DateTime WindowEndUtc` | — |
| `p` | `double InformationCoefficient` | IC di Spearman sulla finestra. |
| `p` | `DateTime ComputedAtUtc` | Quando questa riga è stata calcolata (UTC). Serve a distinguere una storia viva da una ferma. |

### 🔌 `IFactorIcHistoryStore`

> Legge e scrive la storia dell'IC. Interfaccia separata dal worker perché la UI la legge senza dipendere da un BackgroundService.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyDictionary&lt;string, DateTime&gt;&gt; LoadLastComputedAsync(CancellationToken ct = default)` | Quando ogni serie è stata calcolata l'ultima volta, per chiave "SIMBOLO\|TF" . È ciò che permette al job di girare **a rotazione** sulle serie più vecchie invece di macinare sempre le stesse prime N: senza, con una watc… |

### 📦 `FactorIcHistoryStore` `(IDbContextFactory&lt;ApplicationDbContext&gt; dbFactory) : IFactorIcHistoryStore`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyDictionary&lt;string, DateTime&gt;&gt; LoadLastComputedAsync(CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Alpha/FactorMath.cs`

### 📦 `FactorMath`

> Utilità numeriche condivise dai fattori. Tutto in per coerenza con il resto della piattaforma (prezzi esatti). Le finestre rolling calcolano il valore alla candela i usando SOLO gli indici ≤ i (anti-look-ahead per costruzione).

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal Sqrt(decimal value)` | Radice quadrata in decimal (Newton-Raphson), coerente con Statistics.Sqrt . |
| `m` | `decimal Mean(IReadOnlyList&lt;decimal&gt; values, int start, int end)` | Media semplice su una finestra [start..end] inclusi. |
| `m` | `decimal StdDev(IReadOnlyList&lt;decimal&gt; values, int start, int end)` | Deviazione standard di popolazione su [start..end] inclusi. |
| `m` | `List&lt;decimal?&gt; Ema(IReadOnlyList&lt;decimal&gt; values, int period)` | EMA seeded con SMA dei primi valori (stessa convenzione del TechnicalIndicatorsService ). Restituisce una lista allineata: null in warm-up. |
| `m` | `List&lt;decimal?&gt; WilderRsi(IReadOnlyList&lt;decimal&gt; closes, int period)` | RSI di Wilder. Primo valore calcolabile all'indice . Restituisce lista allineata (null in warm-up), valori in [0, 100]. |

## `ProcioneMGR/Services/Alpha/Factors.cs`

### 📦 `MomentumFactor` `: IAlphaFactor`

> MOMENTUM con "skip": rendimento su una finestra Lookback che termina Skip candele fa. Lo skip (tipico: saltare le 1-2 barre più recenti) attenua la mean-reversion di brevissimo periodo, isolando il momentum "pulito".

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; p)` | — |

### 📦 `MeanReversionFactor` `: IAlphaFactor`

> MEAN REVERSION: z-score NEGATIVO del prezzo rispetto alla sua media rolling. Valore alto quando il prezzo è molto SOTTO la media (attesa di rimbalzo verso l'alto). z = (c[i] - mean) / std (su finestra [i-lookback+1 .. i]) -&gt;

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; p)` | — |

### 📦 `RealizedVolatilityFactor` `: IAlphaFactor`

> VOLATILITÀ REALIZZATA: deviazione standard dei rendimenti logaritmici sugli ultimi Lookback periodi. Fattore di rischio/regime (non direzionale).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; p)` | — |

### 📦 `ParkinsonVolatilityFactor` `: IAlphaFactor`

> VOLATILITÀ DI PARKINSON: stima basata sul range High-Low, più efficiente della sola close.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; p)` | — |

### 📦 `RelativeVolumeFactor` `: IAlphaFactor`

> VOLUME relativo: volume corrente rispetto alla sua media rolling, centrato su 0.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; p)` | — |

### 📦 `RsiFactor` `: IAlphaFactor`

> RSI fattorizzato: RSI di Wilder centrato e scalato in [-1, +1].

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; p)` | — |

### 📦 `MacdFactor` `: IAlphaFactor`

> MACD fattorizzato: istogramma MACD normalizzato sul prezzo (comparabile fra simboli). macd = EMA(fast) - EMA(slow); signal = EMA(macd, signalPeriod); hist = macd - signal

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; p)` | — |

### 📦 `DistanceFromMaFactor` `: IAlphaFactor`

> DISTANZA dalla media mobile: scostamento percentuale del prezzo dalla SMA(lookback).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; p)` | — |

## `ProcioneMGR/Services/Alpha/IAlphaFactor.cs`

### 🧾 `FactorParameterDefinition` `(string Key, string Label, decimal Default, decimal Min, decimal Max);`

> Descrizione di un parametro di un fattore, per UI dinamica (come per le strategie).

### 🔢 `FactorCategory`

> Categoria del fattore, utile per raggruppamento in UI e per ridurre la ridondanza.

### 🔌 `IAlphaFactor`

> Un fattore alpha : dato uno storico di candele, produce un valore numerico per ogni candela (allineato per indice alla serie), che rappresenta un segnale predittivo CANDIDATO dei rendimenti futuri. È l'analogo di IStrategy ma NON emette ordini: emette una grandezza continua che verrà (a) valutata statisticamente (Information Coefficient) e (b) usata come feature dei modelli ML delle fasi successive. CONTRATTO ANTI-LOOK-AHEAD (invariante fondamentale, come nel MarketFeatureExtractor ): il valore all'indice i dipende ESCLUSIVAMENTE da candles[0..i] . Non legge mai candles[i+1] o dati futuri. Conseguenza verificabile: il valore alla candela i è identico sia calcolato sull'intera serie sia su una serie troncata dopo i. ALLINEAMENTO: la lista restituita ha SEMPRE la stessa lunghezza dell'input; i primi elementi (warm-up della finestra) sono null , così la serie resta allineata per indice ai …

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | Nome tecnico (chiave), es. "Momentum". |
| `p` | `string DisplayName` | Nome leggibile per la UI, es. "Momentum (skip)". |
| `p` | `FactorCategory Category` | Categoria del fattore. |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |

### 📦 `FactorParametersExtensions`

> Helper comuni ai fattori (lettura parametri con default).

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal GetOrDefault(this IReadOnlyDictionary&lt;string, decimal&gt; p, string key, decimal fallback)` | — |
| `m` | `int GetIntOrDefault(this IReadOnlyDictionary&lt;string, decimal&gt; p, string key, int fallback)` | — |

## `ProcioneMGR/Services/Alpha/IFactorEvaluator.cs`

### 🔌 `IFactorEvaluator`

> Valuta la capacità predittiva di un fattore alpha rispetto ai rendimenti futuri, senza look-ahead nella COSTRUZIONE del fattore (il rendimento forward è il target , e come tale può guardare avanti: è ciò che vogliamo predire, non un input del fattore). Controparte C# di Alphalens.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;decimal?&gt; ForwardReturns(IReadOnlyList&lt;OhlcvData&gt; candles, int horizon)` | Calcola i rendimenti forward su candele: fwd[i] = (close[i+horizon] - close[i]) / close[i] . Gli ultimi horizon elementi sono null (nessun futuro disponibile). |

## `ProcioneMGR/Services/Alpha/OrderFlowFactors.cs`

### 📦 `TakerImbalanceFactor` `: IAlphaFactor`

> SBILANCIAMENTO TAKER: quota della pressione aggressiva in acquisto, mediata su Lookback barre e centrata in [-1, +1]:

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; p)` | — |

### 📦 `AvgTradeSizeFactor` `: IAlphaFactor`

> DIMENSIONE MEDIA DEL TRADE relativa alla propria storia:

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; p)` | — |

# `Services/AlphaMining/`

## `ProcioneMGR/Services/AlphaMining/AlphaExpressionFactor.cs`

### 📦 `AlphaExpressionFactor` `: IAlphaFactor`

> Adatta un albero di espressione ( ) all'interfaccia : un alpha "minato" diventa così un fattore di prima classe, riusabile ovunque un IAlphaFactor è consumato oggi (dataset ML, MlStrategy , valutazione IC), senza toccare quei consumatori. Rif. docs/archive/ROADMAP-QLIB.md §1.7 . Il incapsula l'espressione con prefisso expr: così che IAlphaFactorFactory.Create(Name) possa ricostruirlo per parsing — round-trip identico a quello degli altri fattori persistiti in un SavedMlModel .

| | Firma | Descrizione |
|---|---|---|
| `k` | `string NamePrefix` | Prefisso che marca un nome di fattore come espressione alpha serializzata. |
| `c` | `AlphaExpressionFactor(AlphaNode root)` | — |
| `p` | `AlphaNode Root` | — |
| `p` | `string Expression` | — |
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; parameters)` | — |
| `m` | `AlphaExpressionFactor FromName(string name)` | Ricostruisce il fattore dal nome "expr:&lt;espressione&gt;". |

## `ProcioneMGR/Services/AlphaMining/AlphaExpressionParser.cs`

### 📦 `AlphaExpressionParser`

> Parser dell'espressione alpha serializzata (S-expression prodotta da ), per ricostruire l'albero da un SavedFactor o da un nome di feature "expr:...". Ricorsivo discendente; per gli operatori temporali l'ultimo argomento è la finestra (intero).

| | Firma | Descrizione |
|---|---|---|
| `m` | `AlphaNode Parse(string expression)` | — |

### 📦 `Cursor` `(string text)`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Position` | — |
| `p` | `bool AtEnd` | — |
| `m` | `char Peek()` | — |
| `m` | `void Next()` | — |
| `m` | `void SkipWhitespace()` | — |
| `m` | `string ReadWhile(Func&lt;char, bool&gt; pred)` | — |
| `m` | `void Expect(char c)` | — |

## `ProcioneMGR/Services/AlphaMining/AlphaNode.cs`

### 🔢 `AlphaOp`

> Operatore di un nodo dell'albero di espressione alpha.

### 📦 `AlphaNode`

> Nodo di un albero di espressione alpha (formulaic alpha mining, rif. docs/archive/ROADMAP-QLIB.md §1.7 ). Ogni nodo compila a una serie decimal?[] allineata alle candele. CONTRATTO ANTI-LOOK-AHEAD PER COSTRUZIONE: ogni operatore usa solo candles[0..i] (i temporali leggono la finestra che termina a i), quindi qualunque albero — anche generato a caso dal miner genetico — rispetta l'invariante senza bisogno di verifiche per-nodo. I null si propagano: dove un ingresso non è calcolabile (warm-up, divisione per zero, log di non-positivo) l'uscita è null , così la serie resta allineata per indice ai prezzi.

| | Firma | Descrizione |
|---|---|---|
| `p` | `AlphaOp Op` | — |
| `p` | `string? Field` | — |
| `p` | `decimal Const` | — |
| `p` | `int Window` | — |
| `p` | `AlphaNode[] Children` | — |
| `p` | `string[] Fields` | — |
| `m` | `AlphaNode Variable(string field)` | — |
| `m` | `AlphaNode Constant(decimal value)` | — |
| `m` | `AlphaNode Unary(AlphaOp op, AlphaNode a)` | — |
| `m` | `AlphaNode Binary(AlphaOp op, AlphaNode a, AlphaNode b)` | — |
| `m` | `AlphaNode TimeUnary(AlphaOp op, AlphaNode a, int window)` | — |
| `m` | `AlphaNode TimeBinary(AlphaOp op, AlphaNode a, AlphaNode b, int window)` | — |
| `m` | `int Size()` | Numero totale di nodi (misura di complessità per la penalità anti-overfitting). |
| `m` | `int Depth()` | Profondità dell'albero. |
| `m` | `AlphaNode Clone()` | — |
| `m` | `decimal?[] Evaluate(IReadOnlyList&lt;OhlcvData&gt; candles)` | — |
| `m` | `string ToExpression()` | — |
| `m` | `bool IsTimeOp(AlphaOp op)` | — |
| `m` | `string ToString()` | — |

## `ProcioneMGR/Services/AlphaMining/GeneticAlphaMiner.cs`

### 📦 `MiningConfig`

> Configurazione della ricerca genetica di alpha.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int PopulationSize` | — |
| `p` | `int Generations` | — |
| `p` | `int MaxDepth` | — |
| `p` | `int MaxSize` | — |
| `p` | `int TournamentSize` | — |
| `p` | `double CrossoverRate` | — |
| `p` | `double MutationRate` | — |
| `p` | `double ComplexityPenalty` | Penalità di fitness per nodo: scoraggia formule complesse (anti-overfitting). |
| `p` | `int CvFolds` | Numero di fold temporali per la fitness CROSS-VALIDATA: l'IC viene misurato su sotto-periodi contigui e la fitness premia consistenza (\|IC medio\| − penalità·dev.std), non un \|IC\| di finestra unica gonfiabile dall'ov… |
| `p` | `double CvStabilityPenalty` | Penalità sulla dev.std dell'IC fra i fold: scoraggia i fattori instabili nel tempo. |
| `p` | `double MaxSelectionPbo` | Gate PBO BLOCCANTE: se la Probability of Backtest Overfitting del pannello delle formule minate ≥ questa soglia, il batch è considerato inaffidabile e restituisce vuoto. 1.0 = disattivo (nessun blocco). |
| `p` | `int[] Windows` | Finestre ammesse per gli operatori temporali. |
| `p` | `int ForwardHorizon` | — |
| `p` | `int MinObservations` | — |
| `p` | `int TopN` | — |
| `p` | `int Seed` | — |

### 📦 `MinedFactor`

> Un alpha sopravvissuto alla ricerca: espressione + diagnostica.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Expression` | — |
| `p` | `double SelectionIc` | — |
| `p` | `double Fitness` | — |
| `p` | `int Size` | — |
| `p` | `int Observations` | — |
| `p` | `double? HoldoutIc` | IC sull'holdout mai visto (valorizzato dalla verifica fuori campione). |

### 📦 `GeneticAlphaMiner`

> Formulaic alpha mining via programmazione genetica in C# puro (rif. docs/archive/ROADMAP-QLIB.md §1.7 ): evolve alberi di massimizzando \|IC\| sul periodo di SELEZIONE, con penalità di complessità contro l'overfitting. Deterministico a parità di . Disciplina: la fitness è misurata SOLO sul periodo di selezione; le formule sopravvissute vanno poi sottoposte al verdetto su un holdout mai visto ( ), come per ogni strategia/modello della piattaforma — nessun percorso con standard più bassi. Deviazione dichiarata dalla roadmap: nessuna dipendenza GeneticSharp . La GP su alberi a dimensione variabile non mappa bene sui cromosomi a lunghezza fissa di quella libreria; una implementazione diretta è più semplice, deterministica e coerente col principio "C# puro".

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;MinedFactor&gt; Mine(IReadOnlyList&lt;OhlcvData&gt; candles, MiningConfig config, CancellationToken ct = default)` | — |
| `m` | `double EvaluateIc(AlphaNode node, IReadOnlyList&lt;OhlcvData&gt; candles, int horizon, int minObs, out int obs)` | IC (Spearman) di un'espressione su un set di candele; = coppie valide. |

### 📦 `Scored`

| | Firma | Descrizione |
|---|---|---|
| `p` | `AlphaNode Tree` | — |
| `p` | `double Ic` | — |
| `p` | `double Fitness` | — |
| `p` | `int Observations` | — |
| `m` | `(double Mean, double Std, int FoldsUsed) CrossValidatedIc(decimal?[] values, decimal?[] forward, MiningConfig config, int span)` | IC (Spearman) medio e dev.std su fold temporali contigui, riusando i valori già calcolati del fattore. Un fattore che predice solo in un sotto-periodo ha dev.std alta ⇒ fitness più bassa. Ritorna FoldsUsed &lt; 2 quando… |

### 📦 `NodeSlot`

| | Firma | Descrizione |
|---|---|---|
| `p` | `AlphaNode Node` | — |
| `p` | `AlphaNode? Parent` | — |
| `p` | `int Index` | — |

# `Services/Validation/`

## `ProcioneMGR/Services/Validation/BacktestOverfitting.cs`

### 🧾 `PboResult` `(`

> Esito del calcolo del PBO: la probabilità e le diagnostiche per pannello/UI.

| | Firma | Descrizione |
|---|---|---|
| `p` | `double PboPercent` | Comodo: PBO in percentuale. |

### 📦 `BacktestOverfitting`

> Probability of Backtest Overfitting (Bailey, Borwein, López de Prado, Zhu, 2015) via Combinatorially Symmetric Cross-Validation (CSCV) . Data una matrice di rendimenti periodici (una serie per ogni strategia/combinazione candidata, tutte sullo stesso asse temporale), stima la probabilità che la strategia scelta come migliore IN-SAMPLE risulti sotto la mediana OUT-OF-SAMPLE — cioè che la selezione sia guidata dall'overfitting. Interpretazione: PBO ≈ 0.5 su un pannello di pure strategie-rumore (nessun edge, la scelta è casuale); PBO basso quando esiste un edge reale e persistente. Complementare al Deflated Sharpe: il DSR giudica il singolo migliore, il PBO giudica il processo di selezione nel suo insieme. Puro e deterministico.

| | Firma | Descrizione |
|---|---|---|
| `m` | `PboResult ProbabilityOfOverfitting(IReadOnlyList&lt;IReadOnlyList&lt;double&gt;&gt; perStrategyReturns, int partitions = 10)` | Calcola il PBO via CSCV. : una serie di rendimenti per strategia (stessa lunghezza temporale; se differiscono si usa la lunghezza minima comune). S (pari, ≥ 4): l'asse temporale è diviso in S blocchi e per ogni combinaz… |

## `ProcioneMGR/Services/Validation/CombinatorialPurgedCv.cs`

### 🧾 `CpcvSplit` `(`

> Uno split combinatorio: un sottoinsieme di gruppi usati come test, il resto come train, con le bande di purge/embargo già rimosse dal train. Analogo combinatorio di ML.CvSplit , con in più l'indice dei gruppi di test scelti (per tracciare i "percorsi" backtestabili).

### 📦 `CombinatorialPurgedCv`

> Combinatorial Purged Cross-Validation (López de Prado, "Advances in Financial ML", cap. 12): invece di un solo blocco di test contiguo per fold (come PurgedTimeSeriesCv ), si scelgono TUTTE le combinazioni di testGroups gruppi su groups totali → C(groups, testGroups) split, ciascuno con purge/embargo attorno a OGNI gruppo di test. Genera molti più percorsi out-of-sample dallo stesso storico, riducendo la varianza della stima e alimentando il calcolo del PBO. Deterministico (combinazioni in ordine lessicografico), stateless → registrabile Singleton.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;CpcvSplit&gt; Split(int sampleCount, int groups, int testGroups, int purgeWindow, int embargoPeriods)` | Divide campioni ordinati temporalmente in gruppi contigui (l'ultimo assorbe il resto) e produce C( , ) split. Per ogni split il train esclude i gruppi di test e le bande di prima ed dopo ciascun gruppo di test. |
| `m` | `IEnumerable&lt;int[]&gt; Combinations(int n, int k)` | Combinazioni di indici su , ordine lessicografico. |

## `ProcioneMGR/Services/Validation/DeflatedSharpeRatio.cs`

### 📦 `ReturnMoments`

> Momenti di una serie di rendimenti periodici (double), calcolati in forma "population" (biased) per coerenza con le formule di Bailey–López de Prado (che usano γ3 asimmetria e γ4 curtosi non in eccesso , cioè normale = 3). Separato da Optimization.Statistics (che opera su EquityPoint in decimal e annualizza): qui serve lo Sharpe per-periodo e i momenti superiori grezzi che le formule PSR/DSR richiedono.

| | Firma | Descrizione |
|---|---|---|
| `m` | `double PerPeriodSharpe(IReadOnlyList&lt;double&gt; returns, double riskFreePerPeriod = 0.0)` | Sharpe per-periodo (NON annualizzato): (media − rfPerPeriodo) / deviazione standard di popolazione. È la quantità richiesta da PSR/DSR (che poi correggono con T e i momenti). 0 se dati insufficienti o volatilità nulla (… |
| `m` | `double Skewness(IReadOnlyList&lt;double&gt; returns)` | Asimmetria (skewness) di popolazione: m3 / m2^(3/2). 0 se volatilità nulla. |
| `m` | `double Kurtosis(IReadOnlyList&lt;double&gt; returns)` | Curtosi di popolazione non in eccesso : m4 / m2^2 (normale = 3). È la γ4 usata da PSR/DSR. Ritorna 3 (valore gaussiano) se volatilità nulla, così il termine di correzione è neutro. |

### 📦 `DeflatedSharpeRatio`

> Probabilistic Sharpe Ratio e Deflated Sharpe Ratio (Bailey & López de Prado, 2014, "The Deflated Sharpe Ratio", SSRN 2460551). Rispondono alla domanda centrale quando si sceglie il "migliore" tra molti candidati: lo Sharpe osservato è statisticamente significativo, o è il massimo atteso per puro effetto del test multiplo (selection bias)? CONVENZIONE FONDAMENTALE: tutti gli Sharpe passati qui sono per-periodo (non annualizzati), coerenti con . Se hai uno Sharpe annualizzato, dividi per √(periodiPerAnno) prima di passarlo (la varianza dei trial va de-annualizzata dallo stesso fattore — il rapporto resta corretto). Puro e deterministico.

| | Firma | Descrizione |
|---|---|---|
| `k` | `double EulerMascheroni` | Costante di Eulero–Mascheroni, usata nella stima del massimo atteso di N estrazioni. |
| `m` | `double ProbabilisticSharpe(double observedSharpe, double benchmarkSharpe, int observations, double skewness, double kurtosis)` | Probabilistic Sharpe Ratio: probabilità che il vero Sharpe superi , dato lo Sharpe osservato, la lunghezza del track record e i momenti superiori dei rendimenti. PSR = Φ( (SR − SR*)·√(T−1) / √(1 − γ3·SR + (γ4−1)/4·SR²) … |
| `m` | `double ExpectedMaxSharpe(double varianceOfTrialSharpes, int trials)` | Massimo Sharpe atteso sotto l'ipotesi nulla (nessun edge) su tentativi indipendenti, data la varianza cross-trial degli Sharpe stimati: SR* ≈ √V · [ (1−γ)·Φ⁻¹(1 − 1/N) + γ·Φ⁻¹(1 − 1/(N·e)) ]. È la soglia che lo Sharpe o… |
| `m` | `double Deflated(double observedSharpe, int observations, double skewness, double kurtosis, double varianceOfTrialSharpes, int trials)` | Deflated Sharpe Ratio = PSR valutato alla soglia SR* = . È la probabilità che l'edge sia reale dopo aver corretto per selection bias (N tentativi), non-normalità (γ3, γ4) e lunghezza del track record (T). Convenzione: D… |
| `m` | `double Deflated(IReadOnlyList&lt;double&gt; allTrialSharpes, IReadOnlyList&lt;double&gt; bestStrategyReturns, int? trials = null)` | Overload di comodo: dato l'insieme degli Sharpe per-periodo di tutti i tentativi ( ) e la serie di rendimenti del migliore ( ), calcola il DSR ricavando osservato/momenti/T dalla serie e la varianza cross-trial dall'ins… |
| `m` | `double TrialVariance(IReadOnlyList&lt;double&gt; trialSharpes)` | Varianza di popolazione degli Sharpe dei tentativi (input per ). |

## `ProcioneMGR/Services/Validation/EffectiveTrials.cs`

### 📦 `EffectiveTrials`

> Numero EFFETTIVO di tentativi indipendenti per la correzione del test multiplo nel Deflated Sharpe. assume N tentativi INDIPENDENTI: se molti candidati sono la stessa strategia provata in varianti correlate (griglia fitta di parametri, simboli gemelli, walk-forward sovrapposti), contarli tutti gonfia la soglia SR* e rende il gate troppo severo — N effettivo &lt; N nominale (López de Prado, "Effective Number of Trials"). Qui si clusterizzano i tentativi per correlazione dei rendimenti, riusando + (come HRP), e si conta il numero di cluster a una soglia di correlazione: tentativi con ρ ≥ soglia collassano in un solo trial effettivo. Puro e deterministico.

## `ProcioneMGR/Services/Validation/GatePowerAnalyzer.cs`

### 📦 `GatePowerAnalyzer`

> Potenza del gate anti-overfitting : qual è l'edge più piccolo che questa piattaforma è in grado di CONFERMARE, date la lunghezza dell'holdout e l'ampiezza della ricerca. Nasce da un'osservazione del proprietario (2026-07-28): «di candidati se ne trovano, ma non consolidano mai». Le spiegazioni possibili sono due, opposte, e finora nessuna misura le separava: (a) non c'è edge , e i gate fanno il loro mestiere; (b) i gate non hanno la potenza per confermare un edge della grandezza che esiste davvero, e quindi «zero sopravvissuti» non è un'informazione sul mercato ma sullo strumento. L'esperimento di controllo esistente (fase control ) pianta un edge e la pipeline lo trova con DSR 1,00 — ma quell'edge oscilla del ±4,6% attorno alla media, cioè quindici volte il round-turn dello 0,30%. Dimostra che la macchina non è rotta; non dice niente sugli edge realistici. La differenza fra «non è rott…

| | Firma | Descrizione |
|---|---|---|
| `k` | `double DefaultDsrThreshold` | Soglia di DSR oltre la quale il candidato è difendibile (default della pipeline). |

### ▫️ `PowerPoint` `(int Trials, double? MinAnnualSharpe);`

> Una riga della curva di potenza: a parità di holdout, quanto sale l'asticella al crescere dei tentativi.

## `ProcioneMGR/Services/Validation/MinTrackRecord.cs`

### 📦 `MinTrackRecord`

> [F4 PRD Valore] Minimum Track Record Length e potenza statistica di un run di ricerca, da Bailey & López de Prado ("The Sharpe Ratio Efficient Frontier", J. of Risk 2012; "The Deflated Sharpe Ratio", JPM 2014). Il punto non è aggiungere un giudice — ne abbiamo già (DSR, PBO, gemello nullo) — ma far PARLARE l'aritmetica PRIMA di spendere i backtest: su una finestra corta, lo Sharpe minimo che può superare la soglia è calcolabile a priori, e un run che non può produrre promossi deve dirlo in testa, non scriverlo come «0 sopravvissuti» dopo ore di CPU. Le soglie NON si toccano: il benchmark esterno (Harvey-Liu-Zhu, t&gt;3) le conferma. Tutte le grandezze sono PER-PERIODO (la frequenza dei rendimenti): chi ragiona in Sharpe annualizzato converte con √ppy ai bordi — .

| | Firma | Descrizione |
|---|---|---|
| `m` | `double AnnualizedToPerPeriod(double annualizedSharpe, int periodsPerYear)` | Sharpe annualizzato → per-periodo (ppy = periodi per anno, es. 8760 per 1h). |
| `m` | `double PerPeriodToAnnualized(double perPeriodSharpe, int periodsPerYear)` | Per-periodo → annualizzato. |
| `m` | `double ExpectedMaxSharpeUnderNull(int trials, int observations)` | E[max] dello Sharpe stimato su tentativi indipendenti SENZA alcun edge (Bailey-LdP 2014): è la soglia SR* che il DSR usa come benchmark — con N tentativi, il puro caso arriva fin qui, e un candidato deve battere QUESTO,… |

## `ProcioneMGR/Services/Validation/NullTwinGenerator.cs`

### 📦 `NullTwinGenerator`

> [I2 roadmap frontiere-profitto] Genera "mercati gemelli NULLI": serie sintetiche costruite dai rendimenti reali in cui — per costruzione — non esiste alcuna struttura direzionale sfruttabile, ma la volatilità conserva il suo clustering. Una caccia eseguita su N gemelli produce la distribuzione nulla del proprio "miglior risultato": il candidato sui dati VERI è credibile solo se batte quella distribuzione. È l'esperimento di controllo del 2026-07 (edge piantato) reso organo permanente, girato al contrario: là si verificava che la pipeline TROVA l'edge quando c'è; qui si verifica che NON lo trova quando non c'è. Costruzione del nullo, in due mosse dichiarate: 1. Stationary block bootstrap dei rendimenti (blocchi geometrici con reinserimento e wrap-around, Politis–Romano — stessa famiglia di MonteCarloSamplingMode.StationaryBlock ): conserva il clustering di \|r\| a scala di blocco e dà va…

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;OhlcvData&gt; Generate(IReadOnlyList&lt;OhlcvData&gt; real, int seed, double meanBlockLength = 24)` | Genera un gemello nullo della serie (ordinata, ≥ 3 barre). Deterministico a parità di . Lunghezza media (geometrica) dei blocchi di \|r\|; default 24 barre. |

## `ProcioneMGR/Services/Validation/NullTwinJudge.cs`

### 🧾 `NullTwinVerdict`

> Verdetto del giudice del gemello nullo: la distribuzione dello Sharpe su N mercati nulli ( ) e la posizione del risultato REALE dentro di essa. Passed = il reale supera il quantile richiesto (default 99°): tutto il resto è selezione, non edge.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int ValidTwins` | Gemelli il cui backtest è andato a buon fine (i falliti sono esclusi dalla distribuzione). |
| `p` | `decimal RealSharpe` | — |
| `p` | `decimal Median` | — |
| `p` | `decimal P90` | — |
| `p` | `decimal P95` | — |
| `p` | `decimal P99` | — |
| `p` | `decimal Max` | — |
| `p` | `double RequiredPercentile` | Quantile richiesto (es. 0,99) espresso come frazione. |
| `p` | `decimal Threshold` | Il valore del quantile richiesto nella distribuzione nulla: la soglia da battere. |
| `p` | `double PercentileOfReal` | Percentile (0-100) occupato dal reale nella distribuzione nulla. |
| `p` | `double EmpiricalPValue` | Quota di gemelli con Sharpe ≥ reale (p- |
| `p` | `bool Passed` | — |

### 🔌 `INullTwinJudge`

> [A1 roadmap integrazione] Il giudice del gemello nullo, UNIFICATO: unico punto in cui si decide quanti gemelli servono e quale quantile va battuto. Nato dal bug del 2026-07-25: i tool CLI usavano 15 gemelli e la soglia al 95° — con quindici campioni il "95° percentile" coincide quasi col massimo osservato, quindi la soglia era essa stessa rumore, e un falso positivo (SEI/USDT) l'ha superata. Con ~15.000 combinazioni provate, un test al 95% lascia passare il 5% del rumore per costruzione: serve il 99° su una distribuzione stimata come si deve (200 gemelli). I default di questa classe SONO la policy; chi giudica con parametri più deboli deve motivarlo nel punto di chiamata.

### 📦 `NullTwinJudge` `(IBacktestEngine engine) : INullTwinJudge`

| | Firma | Descrizione |
|---|---|---|
| `k` | `int DefaultTwins` | — |
| `k` | `double DefaultRequiredPercentile` | — |

## `ProcioneMGR/Services/Validation/PermutationTest.cs`

### 📦 `PermutationTest`

> [T1.5 roadmap macchina-ricerca] Test di randomizzazione per lo Sharpe: quanto è probabile osservare uno Sharpe almeno così alto se la strategia NON avesse alcuna deriva sistematica? Perché a blocchi e perché lungo il tempo. La lezione pagata il 2026-07-20 (la "t di 141"): randomizzare fra asset correlati dentro una stessa finestra fabbrica significatività finta, perché le repliche non sono indipendenti. L'unica randomizzazione onesta su questi dati è LUNGO IL TEMPO. E dev'essere a BLOCCHI: capovolgere il segno di ogni barra indipendentemente distruggerebbe l'autocorrelazione dei rendimenti, producendo una distribuzione nulla più stretta del vero e quindi p-

### ▫️ `Result` `(double PValue, double ObservedSharpe, int Permutations);`

## `ProcioneMGR/Services/Validation/SelectionValidator.cs`

### 🧾 `SelectionValidation` `(`

> Verdetto di rigore sulla selezione di UN candidato scelto tra molti: incapsula lo Sharpe osservato, la soglia SR* attesa per puro effetto del test multiplo, e il Deflated Sharpe (la probabilità che l'edge sia reale dopo la correzione). Pensato per essere loggato via IExperimentTracker e mostrato nelle UI di selezione accanto allo Sharpe grezzo.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsSignificant` | Convenzione Bailey–López de Prado: DSR &gt; 0.95 ⇒ risultato difendibile. |
| `m` | `IReadOnlyDictionary&lt;string, decimal&gt; ToMetrics()` | Metriche in forma piatta (chiave→valore) per il logging sull'experiment tracker. |

### 📦 `SelectionValidator`

> Applica il Deflated Sharpe al pattern ricorrente della piattaforma: "ho provato N combinazioni, ho scelto la migliore — è significativa?". Centralizza la conversione da Sharpe annualizzato (come lo calcola Optimization.Statistics ) a per-periodo (come lo richiede il DSR), così i chiamanti (OptimizationEngine, Discovery, AlphaMining) non ripetono la de-annualizzazione. Puro e deterministico.

# `Services/Regime/`

## `ProcioneMGR/Services/Regime/IMarketFeatureExtractor.cs`

### 🔌 `IMarketFeatureExtractor`

> Estrae le dalle candele OHLCV. Tutte le feature sono calcolate usando esclusivamente dati fino alla candela corrente (no look-ahead).

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;MarketFeatures&gt; ComputeFeatures(IReadOnlyList&lt;OhlcvData&gt; candles, string timeframe, CancellationToken ct = default)` | Calcolo PURO delle feature su una lista di candele già in memoria e ordinata cronologicamente (nessun accesso al DB) — stessa matematica anti-look-ahead di . Usato per il regime one-hot con parità train/serve: dataset (… |

## `ProcioneMGR/Services/Regime/IRegimeDetector.cs`

### 🔌 `IRegimeDetector`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;RegimeModel&gt; TrainAsync(TrainingConfiguration config, bool activate = true, CancellationToken ct = default)` | Addestra un nuovo modello K-means e lo profila. Se è true lo salva come modello attivo; altrimenti lo restituisce senza persisterlo (preview). |
| `m` | `Task ActivateModelAsync(RegimeModel model, CancellationToken ct = default)` | Salva e rende attivo un modello (es. dopo una preview o dal retraining worker). |
| `m` | `Task&lt;List&lt;MarketFeatures&gt;&gt; LabelFeaturesAsync(List&lt;MarketFeatures&gt; features, CancellationToken ct = default)` | Etichetta una sequenza di feature col modello attivo più recente, di qualunque serie , applicando lo smoothing. Da preferire l'overload che dichiara la serie: questo è corretto solo se chi chiama ha già verificato che i… |
| `m` | `Task&lt;RegimeModel?&gt; LoadLatestModelAsync(CancellationToken ct = default)` | Ultimo modello attivo (più recente), di qualunque serie . Con più serie seguite contemporaneamente questo non è quasi mai ciò che serve: vedi . |
| `m` | `Task&lt;RegimeModel?&gt; LoadActiveModelAsync(string symbol, string timeframe, CancellationToken ct = default)` | Modello attivo della serie indicata; null se quella serie non ne ha uno. |

## `ProcioneMGR/Services/Regime/JumpModel.cs`

### 🧾 `JumpModelFit` `(`

> Esito di un fit del jump model: centroidi, percorso di stato e diagnostica. k centroidi nello spazio (standardizzato) delle feature. Stato assegnato a ogni riga di input (percorso OFFLINE, usa tutta la serie). Somma delle distanze quadrate + λ · numero di salti: la quantità minimizzata. Iterazioni di discesa a coordinate eseguite (nel restart vincente). true se il percorso di stato ha smesso di cambiare prima del tetto di iterazioni.

### 📦 `JumpModel`

> [C1 roadmap integrazione] Statistical jump model (Nystrup–Kolm–Lindström): clustering K-means con una penalità fissa λ per ogni SALTO di stato fra osservazioni consecutive, stimando cluster e persistenza CONGIUNTAMENTE: min_{μ, s} Σ_t \|\|x_t − μ_{s_t}\|\|² + λ · Σ_t 1[s_t ≠ s_{t−1}] È il candidato che sostituisce l'idea "HMM sopra i cluster K-means", bocciata con misura (gate R4, 2026-07-25): nessuna decodifica a valle rende persistenti regimi i cui centroidi oscillano per costruzione — qui la persistenza entra NELLA stima dei centroidi, non dopo. λ = 0 degenera esattamente in K-means; λ → ∞ degenera in un solo stato: la manopola va tarata e il suo effetto misurato (fase jumpmodel di PlatformExpand), mai assunta. Implementazione: discesa a coordinate — dato il percorso, i centroidi sono le medie di stato; dati i centroidi, il percorso ottimo è programmazione dinamica O(T·k) (la transiz…

| | Firma | Descrizione |
|---|---|---|
| `m` | `(double[][] Z, double[] Means, double[] Stds) Standardize(IReadOnlyList&lt;double[]&gt; rows)` | Standardizza per colonna (z-score) sulla matrice data. Restituisce medie e deviazioni per applicare la STESSA trasformazione a dati successivi (mai ristimare sull'out-of-sample). Colonne a varianza nulla restano a zero … |
| `m` | `double[][] ApplyStandardization(IReadOnlyList&lt;double[]&gt; rows, double[] means, double[] stds)` | Applica una standardizzazione già stimata (per l'out-of-sample). |
| `m` | `JumpModelFit Fit(double[][] x, int k, double lambda, int seed = 1, int restarts = 5, int maxIterations = 60)` | Fit con restarts: seeding k-means++ (deterministico dal seed), poi discesa a coordinate finché il percorso smette di cambiare. Con λ=0 è un K-means (di Lloyd, sulla catena). |
| `m` | `int[] DecodeOffline(double[][] x, double[][] centroids, double lambda)` | Percorso di stato ottimo dati i centroidi (programmazione dinamica, transizione uniforme λ). Guarda TUTTA la serie: va usato per il fit e le analisi, mai per una decisione live (per quella c'è ). |
| `m` | `int[] DecodeCausal(double[][] x, double[][] centroids, double lambda, int initialState = -1)` | Decodifica CAUSALE (filtro, per il live): la STESSA ricorsione in avanti del DP offline, ma a ogni barra lo stato riportato è l'argmin del costo accumulato FINO A LÌ — solo passato, mai un'occhiata avanti. Non è l'ister… |
| `m` | `List&lt;int&gt; RunLengths(IReadOnlyList&lt;int&gt; states)` | Durate (in barre) dei tratti consecutivi nello stesso stato. |

## `ProcioneMGR/Services/Regime/LaneRegimeRouter.cs`

### 📦 `RegimeRoutingRule`

> Regola di instradamento: in questo regime operano SOLO queste strategie.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int RegimeId` | Id del cluster K-means (come mostrato in /regimes ). |
| `p` | `List&lt;string&gt; Strategies` | Nomi di strategia ammessi (quelli di IStrategyFactory , es. "Supertrend"). Una lista vuota significa "in questo regime la corsia sta ferma" — è la scelta più utile del PDF, non un caso degenere: saper riconoscere il reg… |

### 📦 `RegimeRoutingOptions`

> Opzioni del router di regime. Default SPENTO.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Interruttore generale. Default FALSE: prima di dare a un modello K-means il potere di spegnere una strategia dal vivo, quel potere va guadagnato in validazione. |
| `p` | `bool DriveDecisions` | Separa l' osservare dal decidere , come già fa il feed real-time con DriveProtectiveExits . Default FALSE: acceso ma non decidente, il router classifica il regime a ogni candela e ne registra i cambi, senza impedire nul… |
| `p` | `bool AllowUnmappedRegimes` | Politica per i regimi senza regola. Default TRUE (permissivo): un regime nuovo — o un modello riaddestrato con più cluster di quanti ne conosca la configurazione — non deve zittire la corsia di soppiatto. Il caso "non s… |
| `p` | `int MinCandles` | Candele minime in memoria perché la classificazione sia tentata. |
| `p` | `TimeSpan ModelCheckTtl` | Per quanto tempo si riusa l'esito della verifica "esiste un modello attivo per questa serie" senza rinterrogare il database. Il compromesso è dichiarato: attivare un modello nuovo da /regimes impiega fino a questo tempo… |
| `p` | `List&lt;RegimeRoutingRule&gt; Rules` | — |

### 🧾 `RegimeRoutingDecision` `(bool IsKnown, int RegimeId, string Reason, IReadOnlyList&lt;string&gt; AllowedStrategies…`

> Esito della classificazione per la barra corrente. false = regime non determinabile (nessun modello attivo per la serie, candele insufficienti, router spento): in quel caso risponde sempre sì.

| | Firma | Descrizione |
|---|---|---|
| `m` | `RegimeRoutingDecision Unknown(string reason)` | — |
| `p` | `bool Observing` | True quando il router sta solo guardando : la classificazione avviene ed è registrata, ma non impedisce nulla. Vedi . |
| `p` | `bool HasRule` | True se esiste una regola esplicita per questo regime (anche con lista vuota = stai fermo). |
| `m` | `bool Allows(string strategyName)` | True se la strategia può operare nel regime corrente. |

### 🔌 `ILaneRegimeRouter`

> Classifica il regime corrente di una corsia e dice quali strategie possono operarvi.

### 📦 `LaneRegimeRouter` `(`

> [Fase 4 — docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Il router di regime che il PDF mette al centro del suo framework ibrido: classifica il regime, e in base a quello lascia operare solo le strategie che vi hanno senso. Perché serviva. Il routing per regime esisteva già, ma solo dentro il backtest e con un surrogato: RegimeConditionalStrategy usa la pendenza di una SMA con dead zone, non il K-means, e lo dichiara nel proprio commento — le strategie di questa piattaforma sono dependency-free per scelta, quindi una strategia legata al DB non potrebbe girare negli sweep dell'ottimizzatore. Il motore live, dal canto suo, il regime non lo consultava affatto. Questa classe è il "plumbing nuovo" che quel commento indicava, costruito però fuori dalla strategia: al livello della corsia, dove il DB è già disponibile e dove la decisione "chi opera adesso" appartiene naturalmente. È un filtro…

## `ProcioneMGR/Services/Regime/MarketBreadthCalculator.cs`

### 🔌 `IMarketBreadthCalculator`

> [3.8a/4.9] Breadth interna del "mercato" che la piattaforma già possiede: a ogni barra, la frazione dei simboli /USDT tracciati la cui chiusura sta sopra la PROPRIA SMA50. È l'indicatore classico di partecipazione: un rally con breadth 0,9 è mosso da tutto il listino, uno con 0,4 da due titani — regimi diversi che le feature per-simbolo non distinguono.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;Dictionary&lt;DateTime, decimal&gt;&gt; ComputeAsync(string timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)` | Breadth (0..1) per ogni timestamp del timeframe nel range richiesto. CAUSALE: la SMA50 di un simbolo a t usa solo le sue 50 chiusure fino a t. I simboli senza SMA disponibile a t non contano né sopra né sotto (denominat… |

### 📦 `MarketBreadthCalculator` `(IDbContextFactory&lt;ApplicationDbContext&gt; dbFactory) : IMarketBreadthCalculator`

## `ProcioneMGR/Services/Regime/MarketFeatureExtractor.cs`

### 📦 `MarketFeatureExtractor` `(`

> Implementazione del feature extractor. ANTI-LOOK-AHEAD: per la candela all'indice i ogni feature usa SOLO valori a indice ≤ i (rendimenti, finestre rolling, regressione su [i-49..i], ecc.). Nessuna feature legge close[i+1] o dati futuri. La conseguenza verificabile: la feature alla candela i è identica sia che si calcoli sull'intera serie sia su una serie troncata dopo i. Le prime Warmup candele (dove la finestra più lunga, SMA/regressione a 50, non è piena) vengono SCARTATE.

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;MarketFeatures&gt; ComputeFeatures(IReadOnlyList&lt;OhlcvData&gt; candles, string timeframe, CancellationToken ct = default)` | Calcolo puro (testabile) su una lista di candele già ordinata cronologicamente. |

## `ProcioneMGR/Services/Regime/MarketFeatures.cs`

### 📦 `MarketFeatures`

> Features che caratterizzano il CONTESTO di mercato a una data candela. Tutte calcolabili in tempo reale usando SOLO dati fino alla candela corrente (nessun look-ahead). NON predicono il prezzo: descrivono il regime.

| | Firma | Descrizione |
|---|---|---|
| `p` | `DateTime Timestamp` | — |
| `p` | `decimal Price` | — |
| `p` | `decimal Volatility` | Std dev dei rendimenti ultimi 20 periodi, annualizzata. |
| `p` | `decimal TrendStrength` | \|Slope\| della regressione lineare su 50 periodi, normalizzata sul prezzo medio. |
| `p` | `decimal TrendDirection` | Segno della slope (+1 up, -1 down, 0 flat). |
| `p` | `decimal VolumeRatio` | Volume corrente / volume medio ultimi 20 periodi. |
| `p` | `decimal AtrNormalized` | ATR(14) / Price. |
| `p` | `decimal RsiLevel` | RSI(14) medio ultimi 5 periodi. |
| `p` | `decimal HighLowRange` | Media di (High - Low) / Close sugli ultimi 10 periodi. |
| `p` | `decimal DistanceFromMa` | (Price - SMA50) / SMA50. |
| `p` | `decimal MarketBreadth` | [3.8a/4.9] Breadth interna: frazione (0..1) dei simboli /USDT tracciati con chiusura sopra la PROPRIA SMA50 a questa barra. Popolata solo quando la feature è richiesta ( ); 0,5 = neutro/non calcolata. |
| `p` | `int? RegimeId` | — |
| `p` | `string? RegimeLabel` | — |
| `m` | `double[] ToVector()` | Tutte le 8 feature numeriche (per analisi/profili), nell'ordine canonico. |
| `m` | `double[] ToClusteringVector(bool includeVolume = false, bool includeBreadth = false)` | Sottoinsieme di feature usato per il CLUSTERING: le 4 dimensioni ortogonali che definiscono il regime (intensità + direzione del trend), evitando le feature ridondanti/rumorose (ATR≈Volatility, RSI, HighLowRange) che ab… |

### 📦 `FeatureScaling`

> Parametri di standardizzazione (mean/std per feature) per inference futura.

| | Firma | Descrizione |
|---|---|---|
| `p` | `double[] Means` | — |
| `p` | `double[] Stds` | — |
| `p` | `string[] Names` | [3.8a] Nomi delle feature di QUESTO modello, nell'ordine del vettore. Persistiti nel FeatureScalingJson: l'inference ricostruisce il vettore giusto dal modello stesso. Default = le 4 storiche, così i modelli salvati PRI… |
| `p` | `string[] FeatureNames` | Nomi delle 4 feature storiche (i modelli pre-3.8a hanno solo queste). |
| `k` | `int FeatureCount` | Dimensione del vettore storico (baseline; i modelli 3.8a possono averne 5 o 6). |
| `m` | `string[] NamesFor(bool includeVolume, bool includeBreadth)` | Nomi per la combinazione di flag richiesta (stesso ordine di ). |
| `m` | `bool Uses(string featureName)` | True se il modello include la feature (per ricostruire i flag all'inference). |
| `m` | `float[] Transform(double[] vector)` | Standardizza un vettore feature con i parametri salvati (z = (x-mean)/std). |

### 📦 `FeatureNormalizer`

> Standardizzazione di un insieme di features (mean=0, std=1 per colonna).

## `ProcioneMGR/Services/Regime/RegimeAssignment.cs`

### 📦 `RegimeAssignment`

> Funzioni pure per assegnare le candele ai regimi: nearest-centroid, smoothing a conferma di 3 candele (anti flip-flop), e Silhouette Score (qualità del clustering).

| | Firma | Descrizione |
|---|---|---|
| `m` | `int NearestCentroid(float[] normalized, float[][] centroids)` | Indice del centroide euclideo più vicino al vettore normalizzato. |
| `m` | `int[] AssignRaw(float[][] normalizedRows, float[][] centroids)` | — |
| `m` | `int[] Smooth(int[] raw, int confirmFrames = 3)` | Smoothing: il regime cambia SOLO se un nuovo regime è confermato per candele consecutive. Riduce drasticamente i flip-flop. |
| `m` | `int[] SmoothRolling(int[] raw, int window, int confirmFrames, int k)` | Voto di maggioranza causale su una finestra mobile: smoothed[i] = regime più frequente in raw[i-window+1 .. i]. Riduce i flip senza collassare la struttura e usa SOLO dati passati (no look-ahead). Seguito da conferma a … |
| `m` | `int CountTransitions(int[] labels)` | Numero di transizioni di regime in una sequenza (per validare lo smoothing). |
| `m` | `double Silhouette(float[][] points, int[] labels, int k, int sampleSize = 2000, int seed = 0)` | Silhouette Score medio, stimato su un campione casuale (per restare O(sample²)). Per ogni punto: a = distanza media intra-cluster, b = minima distanza media verso un altro cluster; s = (b-a)/max(a,b). Range [-1, 1]. |

## `ProcioneMGR/Services/Regime/RegimeAugmentation.cs`

### 📦 `RegimeAugmentation`

> Arricchimento del vettore di feature con il REGIME di mercato corrente, codificato one-hot (follow-up "regime nel meta-learner dello stacking"). Il regime diventa K colonne one-hot APPESE al vettore di fattori esistente: nessuna modifica a IReturnPredictor né a StackedReturnPredictor.Predict (che si adattano alla dimensione del vettore), solo un vettore più largo — così ogni modello (base o stacking) può condizionare la predizione sul regime. PARITÀ TRAIN/SERVE: l'etichetta di regime è calcolata con l'UNICO percorso causale già esistente — (feature anti-look-ahead in memoria) seguito da (smoothing a finestra passata). Lo stesso metodo è usato sia in costruzione dataset (train) sia in MlStrategy (serve): sulla stessa serie producono le stesse etichette, quindi niente train/serve skew. NON si usa il nearest-centroid grezzo per-feature (senza smoothing): darebbe un'etichetta diversa da que…

| | Firma | Descrizione |
|---|---|---|
| `m` | `float[] Append(float[] baseVec, int regimeId, int regimeCount)` | Appende colonne one-hot a per il regime . Regime fuori range o sconosciuto (&lt;0, es. warm-up) → tutte zero: encoding neutro, mai una colonna sbagliata accesa. Se ≤ 0 restituisce il vettore invariato (feature disattiva… |
| `m` | `IReadOnlyList&lt;string&gt; OneHotNames(int regimeCount)` | Nomi delle colonne one-hot (allineati all'ordine di ). |

## `ProcioneMGR/Services/Regime/RegimeDetector.cs`

### 📦 `RegimeDetector` `(`

> Rilevamento dei regimi di mercato via K-means (Microsoft.ML). Singleton: il modello attivo è in cache e letto in modo thread-safe; l'addestramento è serializzato con un . I servizi scoped (BacktestEngine) sono risolti per-uso. DOPPIA NOZIONE DI REGIME (chiarimento — non è un bug): • QUESTO rilevatore (K-means multi-feature persistito) è la nozione "ricca": guida la pesatura regime-aware dell'ensemble e la profilatura strategia↔regime. • usa invece un proxy causale DB-free (slope della SMA) perché le strategie devono restare senza dipendenze per girare negli sweep dell'OptimizationEngine e nel motore live. Le due possono discordare: è voluto. Chi governa cosa: il K-means qui → allocazione/analisi; il proxy SMA → segnale intra-strategia. K può essere FISSO ( ) o AUTO-SELEZIONATO per Silhouette ( → ).

### 🧾 `CacheEntry` `(RegimeModel Model, float[][] Centroids, FeatureScaling Scaling, List&lt;RegimeProfile&gt…`

> Modello materializzato e pronto all'inferenza.

### 📦 `SeriesComparer` `: IEqualityComparer&lt;(string Symbol, string Timeframe)&gt;`

| | Firma | Descrizione |
|---|---|---|
| `p` | `SeriesComparer Instance` | — |
| `m` | `Task&lt;RegimeModel&gt; TrainAsync(TrainingConfiguration config, bool activate = true, CancellationToken ct = default)` | — |
| `m` | `Task ActivateModelAsync(RegimeModel model, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;MarketFeatures&gt;&gt; LabelFeaturesAsync(List&lt;MarketFeatures&gt; features, CancellationToken ct = default)` | — |
| `m` | `Task&lt;RegimeModel?&gt; LoadActiveModelAsync(string symbol, string timeframe, CancellationToken ct = default)` | Modello attivo della serie indicata. Null se quella serie non ne ha uno. |
| `m` | `Task&lt;RegimeModel?&gt; LoadLatestModelAsync(CancellationToken ct = default)` | — |
| `m` | `(float[][] Centroids, double Silhouette) FitKMeans(MLContext ml, float[][] matrix, int k, int maxIterations)` | Addestra un K-means (Microsoft.ML) su normalizzata e restituisce i centroidi nello spazio normalizzato più il Silhouette Score dell'assegnazione nearest-centroid. Puro rispetto a DB e stato d'istanza (usa solo l' passat… |

### 📦 `FeatureRow`

| | Firma | Descrizione |
|---|---|---|
| `p` | `float[] Features` | — |

## `ProcioneMGR/Services/Regime/RegimeModels.cs`

### 📦 `TrainingConfiguration`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ExchangeName` | — |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `DateTime From` | — |
| `p` | `DateTime To` | — |
| `p` | `int NumberOfRegimes` | — |
| `p` | `int MaxIterations` | — |
| `p` | `bool AutoSelectK` | Se true, K non è fisso: si addestra il K-means per ogni K in [ .. ] e si sceglie quello col Silhouette Score migliore (auto-selezione di K). viene aggiornato al K scelto. Se false si usa così com'è (comportamento storic… |
| `p` | `int MinRegimes` | Estremo inferiore del range di K per l'auto-selezione (min 2). Usato solo se . |
| `p` | `int MaxRegimes` | Estremo superiore del range di K per l'auto-selezione. Usato solo se . |
| `p` | `bool IncludeVolumeFeature` | [3.8a] Quinta feature di clustering: VolumeRatio (volume / media 20 periodi). Default OFF = comportamento storico bit-identico. ATTENZIONE dichiarata: accenderla CAMBIA le etichette dei regimi del modello riaddestrato —… |
| `p` | `bool IncludeBreadthFeature` | [3.8a/4.9] Sesta feature di clustering: breadth interna (% dei simboli /USDT sopra la propria SMA50 — "quanti partecipano al movimento"). Default OFF; stessa avvertenza del volume. Richiede dati multi-simbolo sullo stes… |
| `p` | `string Model` | [2.7 PRD-RISANAMENTO, 2026-08-09] Algoritmo di stima dei centroidi: (default, comportamento storico bit-identico) oppure (statistical jump model C1: la persistenza entra NELLA stima, non a valle). Rispetta il contratto … |
| `p` | `double JumpLambda` | λ del jump model (penalità per salto di stato). Usato solo con Model=Jump. 0 = degenera in K-means; il valore va tarato con la misura, mai assunto. |

### 📦 `RegimeModelKinds`

> Nomi degli algoritmi di regime selezionabili da MarketRegime:Model .

| | Firma | Descrizione |
|---|---|---|
| `k` | `string KMeans` | — |
| `k` | `string Jump` | — |
| `m` | `string Normalize(string? value)` | Parsing tollerante: sconosciuto o vuoto ⇒ KMeans (mai rompere il training per un typo in config). |

### 📦 `RegimeModel`

> Modello di regime addestrato. È anche l'entità EF (persistita nel DB). I centroidi sono nello spazio NORMALIZZATO; per l'inference si standardizza la feature con e si assegna al centroide euclideo più vicino.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `string ExchangeName` | — |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `DateTime TrainedAtUtc` | — |
| `p` | `DateTime TrainingDataFrom` | — |
| `p` | `DateTime TrainingDataTo` | — |
| `p` | `int NumberOfRegimes` | — |
| `p` | `string CentroidsJson` | JSON: array K × 8 di centroidi normalizzati. |
| `p` | `string FeatureScalingJson` | JSON: (mean/std per feature). |
| `p` | `string RegimeProfilesJson` | JSON: List&lt;RegimeProfile&gt;. |
| `p` | `double SilhouetteScore` | — |
| `p` | `bool IsActive` | — |

### 📦 `RegimeProfile`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int RegimeId` | — |
| `p` | `string SuggestedLabel` | — |
| `p` | `int SampleCount` | — |
| `p` | `double MeanVolatility` | — |
| `p` | `double MeanTrendStrength` | — |
| `p` | `double MeanTrendDirection` | — |
| `p` | `double MeanVolumeRatio` | — |
| `p` | `double MeanAtrNormalized` | — |
| `p` | `double MeanRsiLevel` | — |
| `p` | `double MeanDistanceFromMa` | — |
| `p` | `Dictionary&lt;string, StrategyPerformanceInRegime&gt; StrategyPerformances` | — |

### 📦 `StrategyPerformanceInRegime`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string StrategyName` | — |
| `p` | `decimal AverageSharpe` | — |
| `p` | `decimal AverageReturn` | — |
| `p` | `decimal WinRate` | — |
| `p` | `int TotalTrades` | — |

## `ProcioneMGR/Services/Regime/RegimeRetrainingWorker.cs`

### 📦 `RegimeRetrainingWorker` `(`

> Riallena periodicamente il modello di regime per la serie dell'ensemble (il mercato cambia). Attiva il nuovo modello SOLO se il Silhouette migliora di almeno +0.05, altrimenti lo scarta. Config: appsettings "MarketRegime:RetrainingIntervalDays" (default 7).

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

# `Services/Ensemble/`

## `ProcioneMGR/Services/Ensemble/EnsembleAllocator.cs`

### 📦 `EnsembleAllocator`

> Calcola i pesi di allocazione a partire dagli Sharpe rolling, con vincoli Min/Max. Algoritmo (water-filling vincolato): 1. Sharpe negativi -&gt; 0 (non contribuiscono al peso). 2. Pesi grezzi proporzionali agli Sharpe (se tutti 0 -&gt; equipesi). 3. Si fissa al proprio bound IL SINGOLO peso che viola di più (Max o Min), si ridistribuisce il budget rimanente tra i restanti, e si ripete. 4. Garantisce somma = 1 e rispetto dei vincoli (quando geometricamente possibile). NB: l'esempio nello spec (EMA 40%, RSI 45%, MACD 15%) viola il proprio Max (45% &gt; 40%); qui i vincoli sono rispettati: con Max=40% si ottiene es. 40/40/20 (il "leftover" va a chi può ancora assorbirlo), che somma a 100% senza superare i limiti.

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal[] ComputeWeights(IReadOnlyList&lt;decimal&gt; sharpes, decimal minFraction, decimal maxFraction)` | Restituisce i pesi (frazioni, somma 1) allineati per indice agli Sharpe in input. |

## `ProcioneMGR/Services/Ensemble/EnsembleComparator.cs`

### 🔌 `IEnsembleComparator`

> Objective, deterministic comparison of two ensembles (the one currently deployed on the trading lanes vs a candidate produced by a fresh pipeline run) for the continuous auto-reapply loop. The decision is numeric only (no "gut feeling"): a candidate replaces the incumbent ONLY when it is meaningfully better, gated by a configurable hysteresis so the deployed ensemble does not churn on marginal, noise-level improvements. If there is no incumbent (first deployment), any candidate that clears the structural floor (min legs / min distinct symbols) is accepted.

| | Firma | Descrizione |
|---|---|---|
| `m` | `EnsembleComparison Compare(EnsembleSummary? current, EnsembleSummary candidate)` | Decides whether should replace . null/empty = first deployment. |

### 📦 `EnsembleComparatorOptions`

> Tunable thresholds for (bound from the EnsembleComparator config section).

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal MinSharpeImprovementPercent` | Minimum weighted-Sharpe improvement (percent of the incumbent) required to replace — the hysteresis band. |
| `p` | `decimal MinRiskFactorImprovementPercent` | Minimum Monte-Carlo RiskFactor95 improvement (percent, lower is better) that can justify a swap on its own when Sharpe is not worse. |
| `p` | `int MinLegs` | A candidate with fewer surviving legs than this is rejected outright (too thin to deploy). |
| `p` | `int MinDistinctSymbols` | A candidate covering fewer distinct symbols than this is rejected outright (not diversified enough). |
| `p` | `decimal MinSharpeSignificanceZ` | Minimo z-score di significatività statistica del vantaggio di Sharpe del candidato sull'incumbent, oltre alla soglia percentuale di isteresi. Un miglioramento percentuale grande su un campione piccolo è rumore: pretende… |

### 📦 `EnsembleSummary`

> Compact, comparable snapshot of an ensemble (deployed or proposed). All metrics are weighted by allocation.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal WeightedAverageSharpe` | Allocation-weighted average expected/holdout Sharpe across the surviving legs. |
| `p` | `decimal WeightedAverageRiskFactor95` | Allocation-weighted average Monte-Carlo RiskFactor95 (lower = safer). 0 = unknown (not recorded for this ensemble). |
| `p` | `int SurvivingLegs` | Number of active/surviving legs. |
| `p` | `int DistinctSymbols` | Number of distinct symbols the legs span (diversification proxy). |
| `p` | `int Observations` | Effective sample size behind (e.g. the weakest leg's holdout trade count) used to test the statistical significance of a swap. 0 = unknown → the significance gate is skipped and only the percentage hysteresis applies. |
| `p` | `IReadOnlyList&lt;LegSummary&gt; Legs` | Per-leg breakdown (for logging/UI/debug). |
| `p` | `bool IsEmpty` | True when there is nothing meaningful to compare against (no legs). |

### 📦 `LegSummary`

> One leg of an .

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `string StrategyName` | — |
| `p` | `decimal WeightPercent` | — |
| `p` | `decimal Sharpe` | — |
| `p` | `decimal RiskFactor95` | — |

### 📦 `EnsembleComparison`

> Verdict of an ensemble comparison, with the numeric deltas that drove it (for transparent logging).

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool ShouldReplace` | — |
| `p` | `string Reason` | Human-readable, Italian explanation of the verdict (logged + audited — never a silent decision). |
| `p` | `decimal SharpeDelta` | candidate.Sharpe - current.Sharpe (positive = candidate better). |
| `p` | `decimal RiskFactorDelta` | candidate.RiskFactor95 - current.RiskFactor95 (negative = candidate better/safer). |
| `p` | `decimal SharpeImprovementPercent` | Sharpe improvement as a percentage of the incumbent (for the hysteresis check). |
| `p` | `decimal SignificanceZ` | z-score of the candidate's Sharpe advantage over the incumbent, given the candidate's sample size (0 when it could not be computed — unknown Observations or non-positive base). Recorded for audit. |

### 📦 `EnsembleComparator` `(EnsembleComparatorOptions options) : IEnsembleComparator`

| | Firma | Descrizione |
|---|---|---|
| `m` | `EnsembleComparison Compare(EnsembleSummary? current, EnsembleSummary candidate)` | — |
| `m` | `decimal SharpeAdvantageZ(decimal candidateSharpe, decimal incumbentSharpe, int observations)` | z-score del vantaggio di Sharpe del candidato sull'incumbent (test a un campione), usando l'errore standard asintotico dello Sharpe di Lo (2002): SE(SR) ≈ √((1 + ½·SR²) / T). Restituisce 0 se la dimensione campionaria è… |

## `ProcioneMGR/Services/Ensemble/EnsembleManager.cs`

### 📦 `EnsembleManager` `(`

> Implementazione dell'ensemble per UNA corsia di trading isolata ( ). Thread-safe via : la configurazione è letta/scritta in modo serializzato; le simulazioni girano su uno snapshot locale della config (fuori dal lock) per non bloccare letture concorrenti (UI polling + worker). Registrato come Keyed Singleton (una istanza per corsia, vedi Program.cs) invece di un singolo Singleton globale come prima del supporto multi-corsia: ogni istanza filtra/imposta / con il PROPRIO , così due corsie non vedono/toccano mai le righe l'una dell'altra. Le righe esistenti PRIMA di questo supporto hanno LaneId=0 (default di migrazione): sono automaticamente la corsia 0, senza bisogno di alcuna migrazione dati. I servizi scoped (DbContext, BacktestEngine) sono risolti per-operazione via (il manager è Singleton per-corsia).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LaneId` | — |
| `m` | `Task&lt;EnsembleConfiguration&gt; GetConfigurationAsync(CancellationToken ct = default)` | — |
| `m` | `Task UpdateConfigurationAsync(EnsembleConfiguration config, CancellationToken ct = default)` | — |
| `m` | `Task StartAsync(CancellationToken ct = default)` | — |
| `m` | `Task StopAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;EnsemblePerformance&gt; GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)` | — |
| `m` | `Task&lt;IReadOnlyList&lt;DecayReport&gt;&gt; GetDecayReportsAsync(CancellationToken ct = default)` | Confronta la performance REALIZZATA (trade chiusi dal vivo, Paper/Testnet/Live — non una ri-simulazione come ) di ogni gamba attiva con quella attesa dal backtest/holdout, via . Interroga TradeRecords direttamente (non … |
| `m` | `Task&lt;EnsembleStatus&gt; GetStatusAsync(CancellationToken ct = default)` | — |
| `m` | `Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default)` | — |

### 🧾 `RegimeContext` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal RegimeSharpe(int regimeId, string strategyName)` | — |

## `ProcioneMGR/Services/Ensemble/EnsembleModels.cs`

### 📦 `EnsembleConfiguration`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ExchangeName` | — |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `decimal TotalCapital` | — |
| `p` | `int RebalanceIntervalDays` | — |
| `p` | `int SharpeRollingDays` | — |
| `p` | `decimal MinAllocationPercent` | — |
| `p` | `decimal MaxAllocationPercent` | — |
| `p` | `decimal SharpeShrinkage` | Intensità dello shrinkage degli Sharpe verso l'equipeso prima dell'allocazione (0..1). Le stime di Sharpe sono rumorose: 0 = puro Sharpe-weighting (comportamento storico Fase 6), 1 = equipeso puro. Default 0.5 (metà fid… |
| `p` | `int MinSharpeObservations` | Numero minimo di osservazioni (punti di equity) perché lo Sharpe di una gamba sia ritenuto affidabile; sotto la soglia la gamba è portata all'equipeso. 0 = disattivo. |
| `p` | `List&lt;EnsembleStrategy&gt; Strategies` | — |
| `p` | `bool IsEnabled` | — |
| `p` | `string? RiskProfileName` | [R3] Nome del profilo di rischio della Modalità Semplice (vedi Services.Risk.RiskProfiles ). Quando è valorizzato, le soglie di sicurezza EFFETTIVE della corsia sono quelle del profilo sovrapposte a quelle globali ( ). … |
| `p` | `bool IsFutures` | True per operare su Futures perpetui a leva invece che Spot. Campo primitivo (non l'enum MarketType di Services.Trading) per evitare una dipendenza incrociata Ensemble→Trading, dato che Trading già dipende da Ensemble (… |
| `p` | `int Leverage` | Leva richiesta se IsFutures=true (ignorata per lo Spot). Va sotto SafetyConfiguration.MaxLeverageAllowed. |
| `p` | `bool RegimeAwareWeighting` | Se true la pesatura è "regime-aware": peso = 0.6·Sharpe rolling (norm) + 0.4·perf nel regime corrente (norm). Se false usa solo lo Sharpe rolling (comportamento Fase 6). |
| `p` | `decimal ExpectedRiskFactor95` | RiskFactor95 Monte-Carlo aggregato dell'ensemble al momento del deploy (dalla PipelineRecommendation.RiskLimits ), memorizzato qui perché il confronto "corrente vs candidato" del ciclo di ri-applica automatica ( ) possa… |

### 📦 `EnsembleStrategy`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string StrategyId` | — |
| `p` | `string StrategyName` | — |
| `p` | `string DisplayName` | — |
| `p` | `Dictionary&lt;string, decimal&gt; Parameters` | — |
| `p` | `decimal CurrentAllocation` | — |
| `p` | `decimal CurrentCapital` | — |
| `p` | `bool IsActive` | — |
| `p` | `int? SavedStrategyId` | — |
| `p` | `int? SavedMlModelId` | Valorizzato se questa "strategia" è in realtà un modello ML (StrategyName="Ml"): l'Id del SavedMlModel referenziato in Parameters["SavedModelId"], solo per mostrarlo in UI. |
| `p` | `decimal? StopLossPercent` | Stop/target validati nel backtest (es. dalla BestStopVariant di un pipeline run), applicati automaticamente da all'apertura di ogni posizione per questa gamba — null = nessuno stop automatico (comportamento invariato). … |
| `p` | `decimal? TakeProfitPercent` | — |
| `p` | `decimal? TrailingStopPercent` | — |
| `p` | `decimal? ExpectedSharpe` | Metriche di holdout dal backtest che ha validato questa gamba (es. dal pipeline run o da una strategia ottimizzata/salvata), usate da come termine di paragone per la performance realizzata dal vivo. Null = nessun confro… |
| `p` | `decimal? ExpectedProfitFactor` | — |
| `p` | `decimal? ExpectedMaxDrawdown` | — |
| `p` | `string? ExecutionAlgorithmName` | Algoritmo di esecuzione dell'apertura su Testnet/Live: "Twap"\|"Vwap"\|"Iceberg" per distribuire l'ordine nel tempo (riduzione impatto), oppure null/"Immediate" per il comportamento odierno (un solo ordine). Ignorato in… |
| `p` | `int? ExecutionWindowMinutes` | Finestra di esecuzione in minuti per questa gamba; null = usa il default globale. |

### 📦 `EnsembleStatus`

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsRunning` | — |
| `p` | `DateTime? LastRebalanceUtc` | — |
| `p` | `DateTime? NextRebalanceUtc` | — |
| `p` | `decimal TotalCapital` | — |
| `p` | `decimal TotalPnl` | — |
| `p` | `decimal TotalPnlPercent` | — |
| `p` | `List&lt;StrategyStatus&gt; Strategies` | — |
| `p` | `int? CurrentRegimeId` | Regime di mercato corrente (se la pesatura regime-aware è attiva e un modello esiste). |
| `p` | `string? CurrentRegimeLabel` | — |

### 📦 `StrategyStatus`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string StrategyId` | — |
| `p` | `string DisplayName` | — |
| `p` | `decimal CurrentCapital` | — |
| `p` | `decimal Allocation` | — |
| `p` | `decimal Pnl` | — |
| `p` | `decimal PnlPercent` | — |
| `p` | `decimal RollingSharpe` | — |
| `p` | `int TotalTrades` | — |
| `p` | `decimal WinRate` | — |
| `p` | `bool IsActive` | — |

### 📦 `EnsemblePerformance`

| | Firma | Descrizione |
|---|---|---|
| `p` | `List&lt;EquityPoint&gt; TotalEquityCurve` | — |
| `p` | `List&lt;StrategyEquityCurve&gt; StrategyCurves` | — |
| `p` | `List&lt;RebalanceEvent&gt; RebalanceHistory` | — |
| `p` | `decimal TotalReturn` | — |
| `p` | `decimal TotalSharpe` | — |
| `p` | `decimal MaxDrawdown` | — |
| `p` | `List&lt;StrategyStatus&gt; FinalStatuses` | Stato per-strategia a fine simulazione (capitale, allocazione, Sharpe rolling...). |
| `p` | `int? LastRegimeId` | Regime corrente a fine simulazione (regime-aware). |
| `p` | `string? LastRegimeLabel` | — |

### 📦 `StrategyEquityCurve`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string StrategyId` | — |
| `p` | `string DisplayName` | — |
| `p` | `List&lt;EquityPoint&gt; EquityCurve` | — |

### 📦 `RebalanceEvent`

| | Firma | Descrizione |
|---|---|---|
| `p` | `DateTime Timestamp` | — |
| `p` | `List&lt;RebalanceAllocation&gt; Allocations` | — |
| `p` | `string Reason` | — |

### 📦 `RebalanceAllocation`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string StrategyId` | — |
| `p` | `string DisplayName` | — |
| `p` | `decimal PreviousAllocation` | — |
| `p` | `decimal NewAllocation` | — |
| `p` | `decimal RollingSharpe` | — |

## `ProcioneMGR/Services/Ensemble/EnsemblePageService.cs`

### 🧾 `DriftEvaluationResult` `(string Message, bool IsError);`

> Esito della valutazione drift: messaggio riassuntivo per l'operatore.

### 📦 `EnsemblePageService` `(`

> Orchestrazione estratta da Components/Pages/Ensemble.razor (P1-5, PRD-CONSOLIDAMENTO- ARCHITETTURA.md §3.3): caricamento di config/status/performance per corsia (keyed DI), costruzione delle gambe (predefinita, salvata, modello ML, Champion), ciclo di vita dell'ensemble (save/start/stop/rebalance), monitor di decadimento, piani di esecuzione e valutazione drift — tutta la logica che prima viveva nel blocco @code del componente senza test indipendenti da Blazor. Il componente resta responsabile solo di ciò che è intrinsecamente Blazor: binding, PollingTimer di auto-refresh, flag di concorrenza dei bottoni, toast, StateHasChanged . La corsia ( laneId ) NON è stato interno ma un parametro esplicito di ogni metodo — stessa scelta di : è una selezione di navigazione della UI, tenerla fuori evita che un'istanza per-circuito "ricordi" una corsia stantia. Lo stato caricato (Config/Status/…) app…

| | Firma | Descrizione |
|---|---|---|
| `k` | `int DriftRecentCandles` | Finestra "recente" per la valutazione drift (candele). |
| `p` | `EnsembleConfiguration? Config` | — |
| `p` | `EnsembleStatus? Status` | — |
| `p` | `EnsemblePerformance? Performance` | — |
| `p` | `List&lt;IndicatorSeries&gt; PerfSeries` | — |
| `p` | `List&lt;SavedStrategy&gt; SavedStrategies` | — |
| `p` | `List&lt;SavedMlModel&gt; SavedMlModels` | — |
| `p` | `List&lt;DecayReport&gt; DecayReports` | — |
| `p` | `List&lt;ExecutionJob&gt; ExecutionJobs` | — |
| `p` | `List&lt;FactorDriftReport&gt; DriftReports` | — |
| `p` | `SavedMlModel? Champion` | — |
| `m` | `Task LoadSavedCatalogsAsync(CancellationToken ct = default)` | TUTTE le strategie salvate sono deployabili in un ensemble, non solo quelle da walk-forward: una strategia trovata via Discovery e salvata da /backtest ha parametri validi ma IsOptimized=false (nessuno Sharpe atteso). L… |
| `m` | `Task LoadConfigAndChampionAsync(int laneId, CancellationToken ct = default)` | Carica config + Champion della corsia. Status/decadimento/piani di esecuzione si caricano coi metodi dedicati: il componente li chiama uno a uno, così un errore su un pannello non impedisce agli altri di popolarsi (stes… |
| `m` | `Task RefreshAsync(int laneId, CancellationToken ct = default)` | — |
| `m` | `Task LoadDecayReportsAsync(int laneId, CancellationToken ct = default)` | — |
| `m` | `Task LoadExecutionJobsAsync(int laneId, CancellationToken ct = default)` | — |
| `m` | `void AddPredefined(string strategyName)` | — |
| `m` | `void AddFromSaved(int savedStrategyId)` | — |
| `m` | `void AddFromMlModel(int modelId, decimal longThreshold, decimal shortThreshold)` | — |
| `m` | `void AddChampion(decimal longThreshold, decimal shortThreshold)` | Il Champion NON è pinnato per Id: è una sentinella risolta a runtime dal registry, così la corsia segue sempre il modello promosso corrente. Il motore rifiuta l'esecuzione su Live. No-op se non c'è un Champion o se la c… |
| `m` | `void RemoveStrategy(string strategyId)` | — |
| `m` | `Task&lt;string&gt; SaveAsync(int laneId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;string&gt; StartEnsembleAsync(int laneId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;string&gt; StopEnsembleAsync(int laneId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;string&gt; RebalanceNowAsync(int laneId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;DriftEvaluationResult&gt; EvaluateDriftAsync(int modelId, CancellationToken ct = default)` | Confronta la distribuzione dei fattori del modello nella finestra di training (reference) con quella delle ultime candele (current). Un drift NON è di per sé un allarme di PnL — è un avviso che gli input sono cambiati. |

## `ProcioneMGR/Services/Ensemble/EnsembleRebalanceWorker.cs`

### 📦 `EnsembleRebalanceWorker` `(`

> Worker che esegue il rebalancing automatico dell'ensemble quando è abilitato. Controlla periodicamente la configurazione; se IsEnabled e se è passato RebalanceIntervalDays dall'ultimo rebalance, ne esegue uno nuovo.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

## `ProcioneMGR/Services/Ensemble/IEnsembleManager.cs`

### 🔌 `IEnsembleManager`

> Gestione dell'ensemble multi-strategia con allocazione dinamica del capitale basata su Sharpe rolling. La performance è una simulazione storica deterministica: ogni strategia membro viene backtestata sulla finestra, e il capitale viene riallocato periodicamente in base alla Sharpe degli ultimi N giorni.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LaneId` | Corsia di trading isolata a cui appartiene questa istanza (0 = corsia di default). |
| `m` | `Task&lt;EnsembleConfiguration&gt; GetConfigurationAsync(CancellationToken ct = default)` | — |
| `m` | `Task UpdateConfigurationAsync(EnsembleConfiguration config, CancellationToken ct = default)` | — |
| `m` | `Task&lt;EnsembleStatus&gt; GetStatusAsync(CancellationToken ct = default)` | — |
| `m` | `Task StartAsync(CancellationToken ct = default)` | — |
| `m` | `Task StopAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;EnsemblePerformance&gt; GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)` | — |
| `m` | `Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default)` | — |
| `m` | `Task&lt;IReadOnlyList&lt;DecayReport&gt;&gt; GetDecayReportsAsync(CancellationToken ct = default)` | Confronta Sharpe realizzato (trade chiusi dal vivo) vs atteso (backtest/holdout) per ogni gamba attiva. |

# `Services/Portfolio/`

## `ProcioneMGR/Services/Portfolio/HierarchicalRiskParityOptimizer.cs`

### 📦 `HierarchicalRiskParityOptimizer` `(IHierarchicalClustering clustering) : IPortfolioOptimizer`

> Hierarchical Risk Parity (López de Prado, cap. 16 di "Advances in Financial Machine Learning" — citato al cap. 5/13 del libro di Jansen). A differenza di Mean-Variance, non richiede l'inversione della matrice di covarianza (instabile quando gli asset sono molto correlati): raggruppa gli asset per similarità (clustering gerarchico sulla distanza di correlazione, riusando del cap. 13), poi alloca il peso ricorsivamente per bisezione, dando più peso ai (sotto-)cluster meno rischiosi. Pipeline: correlazione -&gt; distanza di Mantegna -&gt; dendrogramma (linkage configurabile, default Average/UPGMA per evitare il chaining del single-linkage) -&gt; ordine quasi-diagonale (l'ordine delle foglie nel dendrogramma, che riflette naturalmente la struttura di correlazione) -&gt; bisezione ricorsiva.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `PortfolioAllocation Optimize(IReadOnlyDictionary&lt;string, IReadOnlyList&lt;decimal&gt;&gt; returnsBySymbol, PortfolioOptimizationConfig? config = n…` | — |

## `ProcioneMGR/Services/Portfolio/IPortfolioOptimizer.cs`

### 🔢 `MeanVarianceObjective`

> Obiettivo dell'ottimizzazione Mean-Variance (Markowitz).

### 🔢 `CovarianceEstimator`

> Stimatore della matrice di covarianza usato dagli allocatori.

### 🔢 `RiskParityMethod`

> Metodo di Risk Parity.

### 📦 `PortfolioOptimizationConfig`

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal RiskFreeRateAnnual` | — |
| `p` | `int PeriodsPerYear` | — |
| `p` | `decimal MinWeight` | Peso minimo per asset (frazione, es. 0.05 = 5%). Long-only: 0 di default. |
| `p` | `decimal MaxWeight` | Peso massimo per asset (frazione, es. 0.40 = 40%). |
| `p` | `MeanVarianceObjective Objective` | — |
| `p` | `CovarianceEstimator CovarianceEstimator` | Stimatore di covarianza per Mean-Variance (default Ledoit-Wolf, meglio condizionato). |
| `p` | `RiskParityMethod RiskParityMethod` | Metodo di Risk Parity (default ERC esatto). |
| `p` | `ML.LinkageMethod HrpLinkage` | Linkage per il dendrogramma dell'HRP. Default (UPGMA): evita il "chaining" del single-linkage dell'articolo originale (cluster allungati che uniscono asset dissimili via un ponte di vicini), più stabile su una metrica d… |

### 📦 `PortfolioAllocation`

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyDictionary&lt;string, decimal&gt; Weights` | Pesi per simbolo, frazioni che sommano a 1 (100%). |

### 🔌 `IPortfolioOptimizer`

> Allocatore di portafoglio (cap. 5): dati i rendimenti storici di un paniere di simboli, calcola i pesi. Le implementazioni si affiancano a EnsembleAllocator (che pesa STRATEGIE in base allo Sharpe rolling) come strategie di pesatura alternative per un paniere di ASSET.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `PortfolioAllocation Optimize(IReadOnlyDictionary&lt;string, IReadOnlyList&lt;decimal&gt;&gt; returnsBySymbol, PortfolioOptimizationConfig? config = n…` | — |

## `ProcioneMGR/Services/Portfolio/MeanVarianceOptimizer.cs`

### 📦 `MeanVarianceOptimizer` `: IPortfolioOptimizer`

> Ottimizzazione Mean-Variance di Markowitz (cap. 5): soluzione analitica (non QP iterativo) per due obiettivi classici, poi vincoli long-only/Min/Max applicati riusando l'algoritmo di EnsembleAllocator (coerenza con l'allocatore già esistente per le strategie): - MaxSharpe (portafoglio tangente): w ∝ Σ⁻¹(μ - r_f), la combinazione che massimizza lo Sharpe ratio senza vincoli di segno. - MinVariance : w ∝ Σ⁻¹·1, il portafoglio a varianza minima globale (non usa μ, solo la struttura di covarianza — più robusto quando le stime di rendimento atteso sono rumorose, che è quasi sempre il caso). La soluzione grezza può contenere pesi negativi (posizioni short); qui il portafoglio è long-only per costruzione della piattaforma, quindi i negativi vengono trattati come "punteggio zero" ed esclusi, poi si applicano i vincoli Min/Max con lo stesso water-filling vincolato di EnsembleAllocator .

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `PortfolioAllocation Optimize(IReadOnlyDictionary&lt;string, IReadOnlyList&lt;decimal&gt;&gt; returnsBySymbol, PortfolioOptimizationConfig? config = n…` | — |

## `ProcioneMGR/Services/Portfolio/PortfolioMath.cs`

### 📦 `PortfolioMath`

> Helper comuni agli allocatori: validazione input, matrice dei rendimenti, media/covarianza/volatilità.

| | Firma | Descrizione |
|---|---|---|
| `m` | `(List&lt;string&gt; Symbols, Matrix&lt;double&gt; Returns) BuildMatrix(IReadOnlyDictionary&lt;string, IReadOnlyList&lt;decimal&gt;&gt; returnsBySymbo…` | — |
| `m` | `Vector&lt;double&gt; Mean(Matrix&lt;double&gt; returns)` | — |
| `m` | `Matrix&lt;double&gt; Covariance(Matrix&lt;double&gt; returns)` | — |
| `m` | `Matrix&lt;double&gt; Regularize(Matrix&lt;double&gt; covariance, double ridgeFactor = 1e-9)` | Diagonal loading (ridge) per stabilizzare Solve() su una matrice di covarianza quasi singolare — caso realistico con asset crypto fortemente correlati (es. altcoin vs BTC) o quando il numero di osservazioni è vicino al … |
| `m` | `Vector&lt;double&gt; StdDev(Matrix&lt;double&gt; returns)` | — |
| `m` | `(Matrix&lt;double&gt; Covariance, double Shrinkage) LedoitWolf(Matrix&lt;double&gt; returns)` | Stimatore di covarianza Ledoit-Wolf (2004, "A well-conditioned estimator for large-dimensional covariance matrices"): riduce (shrink) la covarianza campionaria S verso il target strutturato F = μI (μ = varianza media) c… |
| `m` | `double[] EqualRiskContribution(Matrix&lt;double&gt; covariance, int maxIterations = 1000, double tolerance = 1e-10)` | Portafoglio Equal Risk Contribution ESATTO (Maillard-Roncalli-Teiletche; algoritmo di coordinate cyclical di Griveau-Billion et al. 2013): trova w &gt; 0 tale che ogni asset contribuisca IDENTICAMENTE alla varianza del … |
| `m` | `double[] RiskContributions(Matrix&lt;double&gt; covariance, IReadOnlyList&lt;double&gt; weights)` | Contributi di rischio percentuali di un portafoglio: RC_i = w_i·(Σw)_i / (wᵀΣw), somma 1. È la quantità che l'ERC pareggia — mostrarla è il modo onesto di verificare quanto una allocazione concentra il rischio (i pesi d… |
| `m` | `double[,] CorrelationFromCovariance(Matrix&lt;double&gt; covariance)` | — |
| `m` | `Dictionary&lt;string, decimal&gt; ToConstrainedWeights(IReadOnlyList&lt;string&gt; symbols, IReadOnlyList&lt;double&gt; rawScores, decimal minWeight,…` | Converte punteggi grezzi (possibilmente negativi/nulli) in pesi vincolati [min,max] che sommano a 1, riusando l'algoritmo di EnsembleAllocator (negativi -&gt; 0, water-filling vincolato). |

## `ProcioneMGR/Services/Portfolio/ReturnMatrixBuilder.cs`

### 🧾 `AlignedReturnMatrix` `(`

> Matrice di rendimenti ALLINEATI per timestamp: tutte le serie hanno la stessa lunghezza e l'osservazione i-esima di ogni simbolo si riferisce allo stesso periodo. È il contratto d'ingresso di e . Rendimenti semplici (close/close−1) per simbolo, stessi indici temporali. Timestamp (UTC) della barra di ARRIVO di ciascun rendimento.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int SymbolCount` | — |
| `p` | `int ReturnCount` | Numero di rendimenti per serie (0 = allineamento impossibile). |

### 📦 `ReturnMatrixBuilder`

> Costruisce la matrice dei rendimenti da candele di più simboli con un inner-join sui TimestampUtc : si tengono SOLO i periodi presenti per TUTTI i simboli. Con storici di lunghezza diversa (simbolo quotato dopo, buchi di ingestione) troncare alla coda comune o riempire i buchi con 0 sfalserebbe covarianze e correlazioni — l'inner-join è l'unico allineamento che non inventa dati. Puro e testabile: nessun accesso a DB.

| | Firma | Descrizione |
|---|---|---|
| `m` | `AlignedReturnMatrix BuildAlignedReturns(IReadOnlyDictionary&lt;string, IReadOnlyList&lt;OhlcvData&gt;&gt; candlesBySymbol)` | Allinea le candele per timestamp e calcola i rendimenti semplici da Close. Le candele con Close ≤ 0 (dati sporchi) sono scartate PRIMA dell'intersezione, come se mancassero. L'ordine di arrivo delle candele è irrilevant… |

## `ProcioneMGR/Services/Portfolio/RiskParityOptimizer.cs`

### 📦 `RiskParityOptimizer` `: IPortfolioOptimizer`

> Risk Parity (cap. 5). Due modalità ( ): - EqualRiskContribution (default): ERC ESATTO — ogni asset contribuisce IDENTICAMENTE alla varianza del portafoglio, tenendo conto delle correlazioni. Risolto con l'algoritmo di coordinate cyclical (Griveau-Billion et al. 2013) in , che è robusto e convergente; la covarianza è stimata con Ledoit-Wolf per correlazioni affidabili. - InverseVolatility : w_i ∝ 1/σ_i, l'approssimazione classica (= ERC esatto solo quando le correlazioni fra asset sono uniformi). Mantenuta per confronto/retro-compatibilità.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `PortfolioAllocation Optimize(IReadOnlyDictionary&lt;string, IReadOnlyList&lt;decimal&gt;&gt; returnsBySymbol, PortfolioOptimizationConfig? config = n…` | — |

# `Services/TimeSeries/`

## `ProcioneMGR/Services/TimeSeries/EngleGrangerCointegrationTest.cs`

### 📦 `EngleGrangerCointegrationTest` `: ICointegrationTest`

> Implementazione di (Engle-Granger a due passi + ADF sui residui). P0-1: i valori critici sono quelli di MacKinnon (2010) specifici per la COINTEGRAZIONE (residuo di una regressione STIMATA), non i valori ADF standard (−2.86 al 5%). Questi ultimi sono troppo permissivi per uno spread stimato: accetterebbero troppe coppie NON cointegrate (falsi positivi → divergenze → perdite nel pairs trading). Inoltre il numero di lag dell'ADF è scelto per AIC (non più fisso a 1), su un campione comune così che i modelli siano confrontabili. Perché sui LOG dei prezzi. Sui prezzi grezzi β ha le unità di "prezzo di Y per prezzo di X", quindi il suo ordine di grandezza dice soprattutto quanto costa una moneta rispetto all'altra: AAVE/XLM è stata accettata con β = 575 solo perché AAVE vale ~1000× XLM. Un tetto su \|β\| grezzo sarebbe quindi arbitrario — boccerebbe coppie sane fra monete di prezzo diverso e …

| | Firma | Descrizione |
|---|---|---|
| `k` | `double MinPlausibleElasticity` | Banda di plausibilità dell'elasticità. È un filtro di SANITÀ, volutamente largo: β = 0,5 (o 2) descrive una coppia in cui la copertura corretta è il doppio/la metà del controvalore che le gambe aprono davvero, e oltre q… |
| `k` | `double MaxPlausibleElasticity` | — |
| `m` | `CointegrationResult Test(IReadOnlyList&lt;decimal&gt; seriesY, IReadOnlyList&lt;decimal&gt; seriesX)` | — |
| `m` | `(double Statistic, int Lags) AdfStatisticOnSeries(IReadOnlyList&lt;double&gt; series)` | [C2] ADF (auto-lag per AIC) su una serie GIÀ data — ad es. lo spread out-of-sample prodotto da un estimatore di hedge ratio, per confrontarne la stazionarietà con un altro. Più negativa = più stazionaria. NB: statistica… |
| `m` | `double MacKinnonCriticalValue(double significancePercent, int sampleSize)` | Valori critici di MacKinnon (2010) per il test di cointegrazione Engle-Granger, caso "con costante, senza trend", n=2 variabili I(1). Superficie di risposta CV(T) = β∞ + β1/T + β2/T², sensibilmente più severa dei valori… |
| `m` | `(double Statistic, int Lags) AugmentedDickeyFullerAutoLag(Vector&lt;double&gt; series, int maxLags)` | ADF con selezione del numero di lag per AIC su 0.. . Tutti i modelli usano lo STESSO campione (righe da maxLags+1 ) perché l'AIC sia confrontabile. Ritorna la statistica t = γ̂/SE(γ̂) del modello scelto e il numero di l… |

## `ProcioneMGR/Services/TimeSeries/GarchFit.cs`

### 📦 `GarchFit`

> Risultato della stima MLE di un GARCH(1,1).

| | Firma | Descrizione |
|---|---|---|
| `p` | `double Omega` | — |
| `p` | `double Alpha` | — |
| `p` | `double Beta` | — |
| `p` | `double? DegreesOfFreedom` | Gradi di libertà ν delle innovazioni Student-t (null se il fit è gaussiano). ν basso = code più grasse; ν→∞ ≈ normale. Sotto ~10 le mosse estreme sono molto più probabili della normale. |
| `p` | `IReadOnlyList&lt;double&gt; ConditionalVariances` | Varianza condizionale in-sample (σ²ₜ), allineata per indice ai rendimenti usati in Fit. |
| `p` | `double LogLikelihood` | — |
| `p` | `double Persistence` | α+β: quanto lentamente uno shock di volatilità decade. Vicino a 1 -&gt; shock molto persistenti. |
| `p` | `double LongRunVariance` | Varianza di lungo periodo implicita dal modello: ω / (1 - α - β). |
| `m` | `double ForecastVariance(int horizonSteps)` | Previsione della varianza a passi dall'ultima osservazione, via la formula standard di mean-reversion del GARCH: σ²ₜ₊ₕ = varianzaLungoPeriodo + persistenza^h · (σ²ₜ - varianzaLungoPeriodo) |
| `m` | `double TailQuantile(double p, int horizonSteps)` | Quantile p del RENDIMENTO previsto a passi (media ≈ 0), consapevole delle code grasse. Per p&lt;0.5 è negativo = perdita di coda (VaR / distanza di stop prudente). Sotto Student-t usa il quantile t·√((ν-2)/ν) — più ampi… |

## `ProcioneMGR/Services/TimeSeries/GarchModel.cs`

### 📦 `GarchModel` `: IGarchModel`

> Implementazione di : stima per massima verosimiglianza via Nelder-Mead (derivative-free — la log-verosimiglianza del GARCH è ricorsiva, il gradiente analitico è complesso e facile da sbagliare). I tre parametri (ω, α, β) sono riparametrizzati in uno spazio libero ℝ³ tramite sigmoid/exp in modo che i vincoli (ω&gt;0, α≥0, β≥0, α+β&lt;1 — necessari per varianza positiva e stazionarietà) siano SEMPRE soddisfatti qualunque punto esplori l'ottimizzatore, senza bisogno di un solutore vincolato. Con si aggiunge un quarto parametro libero per i gradi di libertà ν (riparametrizzato come ν = 2 + exp(θ₃) &gt; 2, così la varianza resta finita).

| | Firma | Descrizione |
|---|---|---|
| `m` | `GarchFit Fit(IReadOnlyList&lt;decimal&gt; returns, GarchInnovation innovation = GarchInnovation.Gaussian)` | — |

## `ProcioneMGR/Services/TimeSeries/HarRvForecaster.cs`

### 📦 `HarRvForecaster`

> [C3 roadmap integrazione] HAR-RV (Corsi 2009): previsione della varianza realizzata giornaliera con una OLS a 3 regressori — RV di ieri, RV media dell'ultima settimana (5 gg), RV media dell'ultimo mese (22 gg). L'idea è la "cascata eterogenea": operatori con orizzonti diversi reagiscono a volatilità misurate su scale diverse. Sui crypto la letteratura la dà migliore del GARCH(1,1) su QLIKE a 1-5 giorni; il gate C3 lo verifica sui NOSTRI dati prima di adottarla (fase `volgate` di PlatformExpand). La GARCH Student-t resta comunque per i quantili di coda. Convenzioni: - input/output su scala VARIANZA giornaliera (RV = somma dei quadrati dei log-rendimenti 5m); - è CAUSALE: il valore all'indice i usa solo rv[0..i] (fit incluso), e prevede la MEDIA della RV sui prossimi horizon giorni; - stima sui LIVELLI di RV con pavimento di positività sulla previsione (la variante log esiste in letteratu…

| | Firma | Descrizione |
|---|---|---|
| `k` | `int WeekWindow` | Giorni della componente settimanale. |
| `k` | `int MonthWindow` | Giorni della componente mensile (giorni di trading della letteratura; i crypto quotano 365 ma la scala resta quella). |
| `k` | `int MinFitRows` | Minimo di osservazioni di fit perché una previsione venga emessa. |
| `k` | `double MinVariance` | Pavimento della previsione di varianza (mai zero o negativa). |
| `m` | `double?[] ForecastSeries(IReadOnlyList&lt;double&gt; rv, int horizon = 1, bool onLogRv = false)` | Serie causale delle previsioni: all'indice i la previsione della RV media sui giorni i+1..i+ , con la OLS rifittata a ogni i sui soli dati passati. Null finché i regressori (22 gg) e le righe minime di fit non ci sono. … |

### 📦 `RealizedVariance`

> [C3] Varianza realizzata giornaliera dai dati intraday: somma dei quadrati dei log-rendimenti barra-a-barra, attribuiti al giorno UTC della barra di arrivo (i crypto quotano 24/7: il rendimento che scavalca la mezzanotte appartiene al giorno in cui atterra). I giorni con troppi buchi (meno di rendimenti su 288 barre 5m) vengono SCARTATI, non riempiti: una RV su mezzi dati è una sottostima silenziosa.

| | Firma | Descrizione |
|---|---|---|
| `k` | `int MinReturnsPerDay` | Minimo di rendimenti intraday perché il giorno conti (288 barre 5m piene = 288 rendimenti col chaining). |
| `m` | `IReadOnlyList&lt;(DateOnly Day, double Rv)&gt; DailyFromIntraday(IReadOnlyList&lt;OhlcvData&gt; candles)` | RV giornaliera dalle candele intraday ordinate per timestamp. Ritorna (giorno UTC, RV). |

## `ProcioneMGR/Services/TimeSeries/ICointegrationTest.cs`

### 📦 `CointegrationResult`

> Esito del test di cointegrazione di Engle-Granger fra due serie di prezzi, in LOG-livello.

| | Firma | Descrizione |
|---|---|---|
| `p` | `double HedgeRatio` | β della regressione log Y = α + β·log X + spread: un' elasticità adimensionale , non un rapporto di quantità. β ≈ 1 significa "Y e X si muovono in proporzione", cioè che il portafoglio stazionario è quello a controvalor… |
| `p` | `double Intercept` | — |
| `p` | `IReadOnlyList&lt;double&gt; Spread` | Residui della regressione (lo "spread"), allineati per indice alle serie in input. |
| `p` | `double AdfStatistic` | Statistica t del test ADF (Augmented Dickey-Fuller) sullo spread. |
| `p` | `double CriticalValue` | Valore critico MacKinnon usato per il giudizio (dipende dal livello e dalla lunghezza). Più negativo = più severo. |
| `p` | `double SignificanceLevelPercent` | Livello di significatività (%) del valore critico usato (default 5%). |
| `p` | `int AdfLags` | Numero di lag dell'ADF scelto per AIC. |
| `p` | `bool IsCointegrated` | True se l'ADF rifiuta l'ipotesi di radice unitaria al livello scelto (statistica &lt; valore critico MacKinnon): spread stazionario -&gt; serie cointegrate. |
| `p` | `bool IsHedgeRatioPlausible` | True se l'elasticità sta nella banda di plausibilità economica (vedi ). È un giudizio SEPARATO da , che resta il verdetto puramente statistico: una coppia può avere uno spread stazionario e restare comunque non operabil… |
| `p` | `bool IsTradeable` | L'unico criterio che dovrebbe decidere se una coppia entra in produzione: statistica E plausibilità economica insieme. Su da solo era passata AAVE/XLM, la peggiore delle otto candidate (−14,14%, maxDD 15,1%). |

### 🔌 `ICointegrationTest`

> Test di cointegrazione di Engle-Granger (cap. 9): due serie di prezzi NON stazionarie possono comunque muoversi insieme nel lungo periodo (essere "cointegrate") se una loro combinazione lineare (lo spread) È stazionaria — il fondamento statistico del pairs trading. Procedura in due passi: (1) regressione OLS per stimare l'hedge ratio, (2) test ADF sui residui per verificarne la stazionarietà. La regressione gira sui LOG dei prezzi, non sui prezzi grezzi. Vedi per il perché.

| | Firma | Descrizione |
|---|---|---|
| `m` | `CointegrationResult Test(IReadOnlyList&lt;decimal&gt; seriesY, IReadOnlyList&lt;decimal&gt; seriesX)` | — |

## `ProcioneMGR/Services/TimeSeries/IGarchModel.cs`

### 🔢 `GarchInnovation`

> Distribuzione delle innovazioni standardizzate zₜ = εₜ/σₜ nel GARCH.

### 🔌 `IGarchModel`

> GARCH(1,1) (cap. 9): modella la volatilità come processo essa stessa autoregressivo — σ²ₜ = ω + α·ε²ₜ₋₁ + β·σ²ₜ₋₁ — catturando il "volatility clustering" tipico dei mercati finanziari (periodi di calma e periodi turbolenti si susseguono a grappoli). Usato per il position sizing dinamico e gli stop adattivi: quando la volatilità prevista sale, si riduce l'esposizione, e viceversa.

| | Firma | Descrizione |
|---|---|---|
| `m` | `GarchFit Fit(IReadOnlyList&lt;decimal&gt; returns, GarchInnovation innovation = GarchInnovation.Gaussian)` | Distribuzione delle innovazioni: (default, retro-compatibile) o per stimare anche i gradi di libertà ν e ottenere quantili di coda realistici (sizing/stop consapevoli delle code grasse). |

## `ProcioneMGR/Services/TimeSeries/OlsRegression.cs`

### 🧾 `OlsResult` `(Vector&lt;double&gt; Coefficients, Vector&lt;double&gt; StandardErrors, Vector&lt;double…`

> Risultato di una regressione OLS: coefficienti, errori standard e residui.

### 📦 `OlsRegression`

> Minimi quadrati ordinari, riusati sia dal test di Engle-Granger che dall'ADF.

| | Firma | Descrizione |
|---|---|---|
| `m` | `OlsResult Fit(Matrix&lt;double&gt; design, Vector&lt;double&gt; y)` | — |

## `ProcioneMGR/Services/TimeSeries/PairsSpreadAnalyzer.cs`

### 📦 `PairsSpreadAnalyzer`

> Z-score rolling causale di uno spread (cap. 9): la base statistica del pairs trading. z alto -&gt; spread anomalo in eccesso (Y "caro" rispetto a X) -&gt; short dello spread; z basso -&gt; simmetrico. Il calcolo usa solo valori passati della finestra (causale, anti-look-ahead). Unico consumatore: , che ristima l'hedge ratio in walk-forward e riusa questa finestra sulla parte densa dello spread. Lo screening full-sample (hedge ratio stimato una volta sull'intero campione, con test di cointegrazione) vive invece direttamente in : non serve più un wrapper istanza dedicato (era codice morto, mai risolto da DI).

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;double?&gt; RollingZScore(IReadOnlyList&lt;double&gt; spread, int lookback)` | Z-score causale: z[i] usa solo spread[i-lookback+1 .. i]. Null durante il warm-up. |

# `Services/PairsTrading/`

## `ProcioneMGR/Services/PairsTrading/IPairsBacktestEngine.cs`

### 🔌 `IPairsBacktestEngine`

> Motore di backtest DEDICATO al pairs trading (cap. 9): a differenza di Services.Backtesting.IBacktestEngine (single-symbol per progettazione), opera su DUE serie di candele contemporaneamente. Scelta architetturale deliberata: estendere il motore single-symbol esistente per gestire un numero variabile di simboli avrebbe richiesto toccare IStrategy , tutte le strategie esistenti e i chiamanti (Optimization/Discovery/ Ensemble) — qui invece è un sotto-sistema parallelo e indipendente, zero rischio di regressione sul motore esistente.

| | Firma | Descrizione |
|---|---|---|
| `m` | `PairsBacktestResult RunBacktest(IReadOnlyList&lt;OhlcvData&gt; candlesY, IReadOnlyList&lt;OhlcvData&gt; candlesX, PairsBacktestConfiguration config)` | — |

## `ProcioneMGR/Services/PairsTrading/KalmanPairsSpreadAnalyzer.cs`

### 📦 `KalmanPairsSpreadAnalyzer`

> [C2 roadmap integrazione] Hedge ratio via filtro di Kalman : alternativa alla rolling OLS di , da confrontare in A/B sullo stesso walk-forward (la fase pairs di PlatformExpand) prima di qualunque adozione. Modello stato-spazio standard di letteratura (Montana 2009; Chan 2013): stato θ_t = [α_t, β_t]ᵀ, θ_t = θ_{t-1} + w_t, Cov(w) = Q = (δ/(1−δ))·R·I₂ osservazione log Y_t = α_t + β_t·log X_t + v_t, Var(v) = R β segue una passeggiata aleatoria: si aggiorna a OGNI barra senza parametro di finestra né di ricalibrazione — è il motivo per cui la letteratura lo preferisce alla rolling OLS (β più stabile, spread più stazionario). δ regola quanto in fretta β può muoversi: resta UN parametro, ma adimensionale (Q è scalato su R, così δ non dipende dalla scala dei log-prezzi). Causalità : lo spread emesso alla barra t è l'INNOVAZIONE e_t = log Y_t − (α+β·log X_t) valutata sullo stato PREDETTO (cioè …

| | Firma | Descrizione |
|---|---|---|
| `k` | `double DefaultDelta` | δ di default, dalla letteratura (Chan usa 1e-4): β si muove, ma lentamente. |

## `ProcioneMGR/Services/PairsTrading/PairsBacktestEngine.cs`

### 📦 `PairsBacktestEngine` `: IPairsBacktestEngine`

> Implementazione di . Pipeline: 1. allinea le due serie di candele per timestamp; 2. calcola hedge ratio/spread/z-score in modo rolling e anti-look-ahead ( ); 3. itera candela per candela: \|z\| oltre EntryZScore apre lo spread (dollar-neutral, stesso notional sulle due gambe), \|z\| sotto ExitZScore chiude; 4. aggiorna l'equity curve mark-to-market ad ogni barra. Deterministico, nessuna dipendenza da IStrategy/IBacktestEngine.

| | Firma | Descrizione |
|---|---|---|
| `m` | `PairsBacktestResult RunBacktest(IReadOnlyList&lt;OhlcvData&gt; candlesY, IReadOnlyList&lt;OhlcvData&gt; candlesX, PairsBacktestConfiguration config)` | — |
| `m` | `double?[] SpreadVolRatio(IReadOnlyList&lt;double?&gt; spread, int shortWindow, int longWindow)` | [E1] Rapporto CAUSALE fra la deviazione standard dello spread sulle ultime barre e quella sulle ultime : &gt; 1 = lo spread si è fatto più volatile del solito (regime di rottura). Anti-look-ahead: a i usa solo spread[..… |

### 📦 `PairsPortfolio` `(decimal initialCapital, decimal feePercent, decimal sizePercent, decimal slippagePercent)`

> Contabilità a due gambe, dollar-neutral (stesso notional su Y e X all'apertura). LongSpread: Long Y, Short X. ShortSpread: Short Y, Long X.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal Cash` | — |
| `p` | `decimal LastTradeNotionalPerLeg` | — |
| `m` | `void Open(PairsPositionSide side, decimal priceY, decimal priceX)` | — |
| `m` | `decimal Close(decimal priceY, decimal priceX)` | Chiude la posizione e restituisce il PnL NETTO (già dedotte slippage d'uscita e commissioni). |
| `m` | `decimal Equity(decimal priceY, decimal priceX)` | — |

## `ProcioneMGR/Services/PairsTrading/PairsBacktestModels.cs`

### 📦 `PairsBacktestConfiguration`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string SymbolY` | — |
| `p` | `string SymbolX` | — |
| `p` | `decimal InitialCapital` | — |
| `p` | `decimal PositionSizePercent` | % del capitale corrente impegnata per GAMBA (dollar-neutral: stesso notional su Y e X). |
| `p` | `decimal FeePercent` | Commissione per lato, in percentuale del notional di ciascuna gamba. |
| `p` | `int LookbackWindow` | Ampiezza della finestra (barre) usata per ristimare l'hedge ratio ad ogni ricalibrazione. |
| `p` | `int RecalibrationInterval` | Ogni quante barre ristimare l'hedge ratio (walk-forward, mai barre future). |
| `p` | `int ZScoreLookback` | Finestra per lo z-score rolling causale dello spread. |
| `p` | `decimal EntryZScore` | \|z\| oltre questa soglia apre la posizione (spread anomalo). |
| `p` | `decimal ExitZScore` | \|z\| sotto questa soglia chiude la posizione (spread rientrato). |
| `p` | `decimal StopZScore` | STOP DI DIVERGENZA: \|z\| AVVERSO oltre questa soglia forza l'uscita in perdita (il classico blow-up del pairs — lo spread può divergere all'infinito). Deve essere &gt; . 0 = disattivo (sconsigliato con denaro vero). De… |
| `p` | `int MaxHoldBars` | Stop temporale: chiude la posizione dopo questo numero di barre se non è ancora rientrata (0 = disattivo). |
| `p` | `decimal SlippagePercent` | Slippage sfavorevole (%) applicato al fill di OGNI gamba, in entrata e in uscita (0 = fill teorici). |
| `p` | `decimal MaxSpreadVolRatio` | [E1] FILTRO DI VOLATILITÀ dello spread. Salta l'apertura di una nuova posizione quando la volatilità RECENTE dello spread (finestra ) supera di questo rapporto la sua volatilità di BASE (finestra ): è il regime in cui l… |
| `p` | `int SpreadVolBaselineWindow` | Finestra di base della volatilità dello spread per il filtro (vedi ). |
| `p` | `PairsHedgeRatioEstimator HedgeRatioEstimator` | [C2] Estimatore dell'hedge ratio. Default per esito del gate C2 MISURATO (2026-07-26, fase `pairs 1d` di PlatformExpand, holdout 2026-03-01→oggi sulle 5 coppie operabili in selezione): spread OOS più stazionario in 5/5 … |
| `p` | `double KalmanDelta` | [C2] δ del filtro di Kalman (rumore di stato, adimensionale). Vedi . |

### 🔢 `PairsHedgeRatioEstimator`

> [C2] Come viene stimato l'hedge ratio del pairs, a parità di tutto il resto.

### 🔢 `PairsPositionSide`

> LongSpread = Long Y / Short X. ShortSpread = Short Y / Long X.

### 📦 `PairsTrade`

| | Firma | Descrizione |
|---|---|---|
| `p` | `DateTime EntryTime` | — |
| `p` | `DateTime? ExitTime` | — |
| `p` | `PairsPositionSide Side` | — |
| `p` | `decimal EntryPriceY` | — |
| `p` | `decimal EntryPriceX` | — |
| `p` | `decimal? ExitPriceY` | — |
| `p` | `decimal? ExitPriceX` | — |
| `p` | `decimal HedgeRatioAtEntry` | — |
| `p` | `decimal Pnl` | — |
| `p` | `decimal PnlPercent` | — |
| `p` | `string ExitReason` | Motivo dell'uscita: "MeanReversion" (rientro), "StopZScore" (divergenza), "MaxHold" (tempo), "EndOfData". |

### 📦 `PairsBacktestResult`

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal FinalCapital` | — |
| `p` | `decimal TotalReturnPercent` | — |
| `p` | `int TotalTrades` | — |
| `p` | `int WinningTrades` | — |
| `p` | `int LosingTrades` | — |
| `p` | `decimal WinRate` | — |
| `p` | `decimal MaxDrawdownPercent` | — |
| `p` | `int CandlesEvaluated` | — |
| `p` | `List&lt;PairsTrade&gt; Trades` | — |
| `p` | `List&lt;EquityPoint&gt; EquityCurve` | — |

## `ProcioneMGR/Services/PairsTrading/PairsCandleAligner.cs`

### 📦 `PairsCandleAligner`

> Allinea due serie di candele per timestamp (intersezione): due simboli possono avere gap diversi (manutenzione exchange, listing in date diverse, ecc.), quindi non si può assumere che siano già sincronizzate indice-per-indice come nel motore single-symbol.

## `ProcioneMGR/Services/PairsTrading/PairsSpreadSeries.cs`

### 📦 `PairsSpreadSeries`

> Operazioni condivise sulle serie di spread con warm-up (null iniziali). Estratte da quando è nato il secondo estimatore ( , C2): i due DEVONO standardizzare lo spread nello stesso identico modo, o l'A/B confronterebbe la definizione di z-score invece che l'estimatore dell'hedge ratio.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;double?&gt; CausalZScore(double?[] spread, int lookback)` | Z-score rolling causale su uno spread con warm-up (null iniziale): riusa la stessa finestra di sulla parte densa. |

## `ProcioneMGR/Services/PairsTrading/RollingPairsSpreadAnalyzer.cs`

### 📦 `RollingPairsAnalysis`

> Esito dell'analisi rolling dello spread: hedge ratio, spread e z-score, allineati per indice, con null durante il warm-up.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;double?&gt; HedgeRatio` | — |
| `p` | `IReadOnlyList&lt;double?&gt; Spread` | — |
| `p` | `IReadOnlyList&lt;double?&gt; ZScore` | — |

### 📦 `RollingPairsSpreadAnalyzer`

> A differenza di (hedge ratio stimato una volta sull'intero campione, adatto solo allo SCREENING di quali coppie sono cointegrate), questa versione ristima l'hedge ratio periodicamente in modo rolling/walk-forward : ogni barre, la regressione Y~X viene rifatta usando SOLO le osservazioni PASSATE (mai quelle future) — il risultato è anti-look-ahead corretto ed è quello che rende un backtest vero, non solo uno screening statistico. Come , la regressione gira sui LOG dei prezzi: le due DEVONO usare la stessa specificazione, altrimenti lo screening dichiara cointegrata una combinazione e il backtest ne negozia un'altra. Di conseguenza lo spread qui è un log-spread — adimensionale, e confrontabile fra coppie con prezzi di scala diversa, cosa che lo spread in unità di prezzo non era (il suo z-score dipendeva dal livello del prezzo di X).

# `Services/Backtesting/`

## `ProcioneMGR/Services/Backtesting/BacktestEngine.cs`

### 📦 `BacktestEngine` `(`

> Motore di backtest event-driven, long/short, una posizione alla volta. Pipeline: 1. carica le candele OHLCV dal DB per il range; 2. la strategia pre-calcola i suoi indicatori UNA volta (InitializeAsync); 3. itera candela per candela (hot loop su array decimal[] , niente LINQ): - chiede il alla strategia; - Long/Short: se serve chiude la posizione opposta (flip) e apre; - Close: chiude la posizione corrente; - aggiorna l'equity curve a ogni candela; 4. chiude l'eventuale posizione aperta sull'ultima candela e calcola le metriche. Tutto in decimal e deterministico (nessuna casualita'/parallelismo). Commissione FeePercent applicata su entry ed exit. Cancellabile; Task.Yield() ogni 1000 candele per non saturare il thread pool.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;BacktestResult&gt; RunBacktestAsync(BacktestConfiguration config, CancellationToken ct)` | — |
| `m` | `Task&lt;BacktestResult&gt; RunBacktestAsync(BacktestConfiguration config, IReadOnlyList&lt;OhlcvData&gt; candles, CancellationToken ct)` | Overload con candele gia' caricate: usato dall'ottimizzatore per non ricaricare l'OHLCV dal DB a ogni backtest (caching). Le candele devono essere gia' filtrate per il range desiderato e ordinate cronologicamente. State… |
| `m` | `Task&lt;BacktestResult&gt; RunBacktestAsync(BacktestConfiguration config, IReadOnlyList&lt;OhlcvData&gt; candles, IStrategy strategy, CancellationTok…` | Overload che usa un'istanza di strategia già pronta (vedi ) invece di crearla per nome. Contiene il core del motore: entrambi gli overload pubblici convergono qui, così la pipeline di esecuzione è unica. |

### ▫️ `MakerFillStats` `(int Attempted, int Filled, int FallbackTaker, int Missed);`

> [R3] Esito degli ingressi tentati come limite maker, per la diagnostica del risultato.

### 📦 `Portfolio` `(`

> Conto/posizione a MARGINE: all'apertura si riserva margine = equity * sizeFrac e si apre un nozionale = margine * leva; equity = cash + margine + PnL non realizzato. A leva 1 questa contabilita' coincide ESATTAMENTE (formula per formula) con la vecchia contabilita' spot, per long e short: nessun cambiamento nei risultati esistenti. Con leva &gt; 1 espone il prezzo di liquidazione (margine eroso fino al mantenimento) e accumula gli eventuali costi di funding nel PnL del trade.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal Cash` | — |
| `p` | `List&lt;BacktestTrade&gt; Trades` | — |
| `p` | `int LiquidationCount` | — |
| `p` | `decimal TotalFees` | [R2] Commissioni pagate, cumulate su entrambi i lati di ogni trade. |
| `p` | `decimal TotalSlippage` | [R2] Attrito di slippage stimato sul nozionale di ogni fill. Diagnostico: vedi . |
| `p` | `decimal TotalFunding` | [R2] Funding perpetual addebitato (0 senza leva/derivati). |
| `p` | `bool IsFlat` | — |
| `p` | `bool IsLong` | — |
| `p` | `bool IsShort` | — |
| `p` | `decimal OpenNotional` | Nozionale di apertura della posizione corrente (0 se flat). |
| `m` | `decimal Equity(decimal price)` | — |
| `m` | `decimal LiquidationPrice(decimal maintenanceFrac)` | Prezzo al quale margine + PnL scende al margine di mantenimento: liquidazione. Long: sotto l'entry; short: sopra. Con leva 1 e' cosi' lontano da non scattare mai in pratica (equivale a perdere quasi il 100% del nozional… |
| `m` | `void ChargeFunding(decimal amount)` | Addebita (o ACCREDITA, amount negativo) il funding pro-rata sul nozionale aperto: entra nel PnL del trade. [T0.2] Il segno arriva dal chiamante (rate firmato × lato): uno short con funding positivo riceve un amount nega… |
| `m` | `void OpenLong(decimal price, DateTime ts, decimal? feeFracOverride = null, bool chargeSlippage = true, decimal sizeMultiplier = 1m)` | — |
| `m` | `void OpenShort(decimal price, DateTime ts, decimal? feeFracOverride = null, bool chargeSlippage = true, decimal sizeMultiplier = 1m)` | — |
| `m` | `void Close(decimal price, DateTime ts, bool liquidated = false)` | — |

## `ProcioneMGR/Services/Backtesting/BacktestModels.cs`

### 🔢 `Signal`

> Segnale emesso da una strategia per ogni candela.

### 📦 `BacktestConfiguration`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ExchangeName` | — |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `DateTime From` | — |
| `p` | `DateTime To` | — |
| `p` | `decimal InitialCapital` | — |
| `p` | `decimal PositionSizePercent` | % del capitale corrente impegnata per ogni trade. |
| `p` | `decimal FeePercent` | Commissione per lato (entry/exit), in percentuale del notional. Default 0.1%. |
| `p` | `string StrategyName` | — |
| `p` | `Dictionary&lt;string, decimal&gt; StrategyParameters` | — |
| `p` | `decimal StopLossPercent` | Stop loss in % dal prezzo di ingresso (0 = disattivo). Overlay a livello di MOTORE (McAllen: "lo stop loss E' parte del trade"): controllato su high/low di ogni candela PRIMA del segnale di strategia, eseguito al livell… |
| `p` | `decimal TakeProfitPercent` | Take profit in % dal prezzo di ingresso (0 = disattivo). |
| `p` | `decimal TrailingStopPercent` | Trailing stop in % dal miglior prezzo raggiunto dall'ingresso (0 = disattivo). Sale con il prezzo e non scende mai: preserva i guadagni (McAllen cap. 17). |
| `p` | `decimal TrailingAtrMultiple` | [Fase 5a] Trailing "chandelier": distanza dal miglior prezzo pari a questo multiplo dell'ATR invece che a una percentuale fissa (0 = disattivo). Quando è &gt; 0 sostituisce , non vi si somma — due trailing attivi insiem… |
| `p` | `int TrailingAtrPeriod` | Periodo dell'ATR usato dal trailing chandelier (default 14, Wilder). |
| `p` | `decimal Leverage` | Leva finanziaria (futures/margin). Con leva L, e' la quota di capitale usata come MARGINE e il nozionale e' margine x L. A 1 (default) il comportamento coincide esattamente con lo spot attuale. Con L &gt; 1 il motore mo… |
| `p` | `decimal MaintenanceMarginPercent` | Margine di mantenimento in % del nozionale (default 0.5%, tipico dei perpetual su coppie liquide). La posizione viene liquidata quando margine + PnL non realizzato scende a questo livello: si perde quasi tutto il margin… |
| `p` | `decimal FundingRatePercentPer8h` | Funding rate dei perpetual in % del nozionale per periodo di 8 ore (0 = disattivo; 0.01 e' il valore "neutro" storico). Addebitato pro-rata a ogni candela con posizione aperta: a leva alta su holding lunghi pesa piu' de… |
| `p` | `decimal SlippagePercent` | Slippage in % applicato SFAVOREVOLMENTE a ogni eseguito (entry, exit, stop, target, liquidazione). 0 = fill teorici (default, comportamento invariato). |
| `p` | `EntryExecutionStyle EntryExecution` | Come viene eseguito l'INGRESSO. è il default e lascia il comportamento invariato. Le uscite restano sempre taker: uno stop protettivo è un ordine a mercato per natura — non lo si può appoggiare passivamente al book e sp… |
| `p` | `decimal MakerOffsetPercent` | Quanto passivo si mette il limite, in % sotto (long) o sopra (short) la close del segnale. Più è passivo, meglio si compra QUANDO si viene riempiti — e meno spesso si viene riempiti. |
| `p` | `int MakerMaxWaitBars` | Per quante candele il limite resta appoggiato prima di scadere. |
| `p` | `decimal MakerFeePercent` | Commissione per lato di un eseguito MAKER, in % del nozionale (tipicamente &lt; ). |
| `p` | `decimal MakerQueuePenetrationPercent` | [F-queue, roadmap profitto-intraday] PROXY DI CODA. Il limite si considera riempito solo se il prezzo PENETRA oltre il livello di questa % (long: Low ≤ limite·(1−q); short: High ≥ limite·(1+q)), non se lo SFIORA soltant… |
| `p` | `bool MakerFallbackToTaker` | Alla scadenza del limite non riempito: true = si attraversa lo spread e si entra comunque a mercato (taker), false = il segnale si perde. Sono due strategie diverse, non due sfumature della stessa: la prima paga il take… |
| `p` | `VolatilityTargetingOptions VolatilityTargeting` | Dosaggio della posizione sulla volatilità (spento di default: comportamento invariato). |
| `p` | `List&lt;FundingRatePoint&gt;? FundingHistory` | [T0.2] Serie STORICA dei funding rate (percento per 8h, FIRMATA). Null o vuota = si usa la costante come sempre. Quando presente, il motore applica il rate dell'ultimo evento ≤ timestamp della candela, rispettando il LA… |

### 📦 `VolatilityTargetingOptions`

> Dosaggio della posizione sulla volatilità realizzata, con la stessa semantica e gli stessi default del trading dal vivo ( SafetyConfiguration ): serve a poter MISURARE l'effetto sui propri dati prima di accenderlo. Spento di default = comportamento invariato.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | — |
| `p` | `decimal TargetAnnualVolatilityPercent` | — |
| `p` | `int LookbackBars` | — |
| `p` | `decimal MinExposureMultiplier` | — |
| `p` | `decimal MaxExposureMultiplier` | 1,0 = il dosaggio può solo ridurre la size, mai aumentarla. Vedi VolatilityScaler . |

### 🔢 `EntryExecutionStyle`

> Come viene piazzato l'ordine di INGRESSO nel backtest.

### 📦 `BacktestResult`

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal FinalCapital` | — |
| `p` | `decimal TotalReturnPercent` | — |
| `p` | `int TotalTrades` | — |
| `p` | `int WinningTrades` | — |
| `p` | `int LosingTrades` | — |
| `p` | `decimal WinRate` | — |
| `p` | `decimal MaxDrawdownPercent` | — |
| `p` | `int CandlesEvaluated` | — |
| `p` | `int LiquidationCount` | Numero di posizioni chiuse per liquidazione forzata (solo con leva &gt; 1). |
| `p` | `decimal TotalFeesPaid` | [R2] Commissioni pagate in valuta, su entrambi i lati di ogni trade. |
| `p` | `decimal TotalSlippagePaid` | [R2] Attrito di slippage in valuta, stimato sul nozionale di ogni fill. |
| `p` | `decimal TotalFundingPaid` | [R2] Funding perpetual NETTO in valuta: positivo = pagato, negativo = incassato. [T0.2] Con il funding firmato uno short in regime di funding positivo lo INCASSA, quindi il valore può legittimamente essere negativo. |
| `p` | `int MakerEntriesAttempted` | [R3] Ingressi tentati come limite maker (0 in modalità Taker). |
| `p` | `int MakerEntriesFilled` | [R3] Di quelli, quanti sono stati effettivamente riempiti al prezzo limite. |
| `p` | `int MakerEntriesFallbackTaker` | [R3] Limiti scaduti senza fill e poi entrati comunque a mercato (fallback taker). |
| `p` | `int MakerEntriesMissed` | [R3] Segnali PERSI perché il limite non è stato riempito e non c'era fallback. |
| `p` | `decimal MakerFillRate` | [R3] Frazione di limiti riempiti. È il numero che smonta o conferma l'ipotesi ottimistica "maker = commissione più bassa": un tasso di riempimento alto su una strategia che insegue il prezzo sarebbe sospetto, uno basso … |
| `p` | `decimal InitialCapital` | Capitale iniziale, ripetuto qui perché i rapporti sotto siano leggibili da soli. |
| `p` | `decimal TotalCosts` | [R2] Attrito totale: commissioni + slippage + funding. |
| `p` | `decimal CostDragPercent` | [R2] Costi in % del capitale iniziale. È il numero che decide se un timeframe è operabile: un rendimento netto del 3% con un cost drag del 40% non è una strategia mediocre, è una strategia che regala all'exchange tredic… |
| `p` | `decimal GrossReturnPercent` | [R2] Rendimento che ci sarebbe stato SENZA attrito. Il divario con è esattamente ciò che i costi hanno eroso. |
| `p` | `List&lt;BacktestTrade&gt; Trades` | — |
| `p` | `List&lt;EquityPoint&gt; EquityCurve` | — |

### 📦 `BacktestTrade`

| | Firma | Descrizione |
|---|---|---|
| `p` | `DateTime EntryTime` | — |
| `p` | `decimal EntryPrice` | — |
| `p` | `DateTime? ExitTime` | — |
| `p` | `decimal? ExitPrice` | — |
| `p` | `decimal Quantity` | — |
| `p` | `decimal Pnl` | — |
| `p` | `decimal PnlPercent` | — |
| `p` | `string Direction` | "Long" o "Short" (utile in tabella). |
| `p` | `bool WasLiquidated` | True se la posizione e' stata chiusa per liquidazione forzata (margine esaurito). |

### 📦 `EquityPoint`

| | Firma | Descrizione |
|---|---|---|
| `p` | `DateTime Timestamp` | — |
| `p` | `decimal Capital` | — |

## `ProcioneMGR/Services/Backtesting/BacktestPageService.cs`

### 🧾 `BacktestConfigSnapshot` `(`

> Fotografia completa del form di Backtest.razor — usata per i preset/memoria dell'ultima configurazione, per l'handoff da/verso Optimization e come input di .

### 🧾 `BacktestActionResult` `(string Message, bool IsError)`

> Esito di un'azione con messaggio per l'operatore.

| | Firma | Descrizione |
|---|---|---|
| `m` | `BacktestActionResult Ok(string message)` | — |
| `m` | `BacktestActionResult Error(string message)` | — |

### 🧾 `BracketSuggestion` `(string Message, bool IsError, decimal? StopLossPercent, decimal? TakeProfitPercent);`

> Esito di "Suggerisci SL/TP": i livelli sono null quando il suggerimento fallisce.

### 🧾 `BacktestHandoffQuery` `(`

> Contesto opzionale arrivato via query string (handoff dall'Optimization).

### 🧾 `LoadedSavedStrategy` `(string Name, string StrategyName, IReadOnlyDictionary&lt;string, decimal&gt; Parameters);`

> Strategia salvata caricata dal DB, coi parametri già fusi sui default della strategia.

### 📦 `BacktestPageService` `(`

> Orchestrazione estratta da Components/Pages/Backtest.razor (P1-5, PRD-CONSOLIDAMENTO- ARCHITETTURA.md §3.3): validazione, esecuzione del backtest con analitiche derivate (trade report, Kelly, consulente leva, Montecarlo, Performance Control), suggerimento SL/TP dai percentili di escursione, CRUD delle strategie salvate, handoff da Optimization e (de)serializzazione validata dei preset — tutta la logica che prima viveva nel blocco @code del componente senza test indipendenti da Blazor. Il componente resta responsabile solo di ciò che è intrinsecamente Blazor: binding del form, ciclo di vita, spinner/CTS di annullamento, StateHasChanged . Lo stato del "run corrente" (risultato, report, analisi di rischio) vive qui perché è stato applicativo condiviso fra i passi run→Montecarlo/PerfControl→handoff, non stato di UI. Registrato Scoped: in Blazor Server uno scope = un circuito, un'istanza per…

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;string&gt; KnownSymbols` | — |
| `p` | `ProcioneMGR.Services.ML.Labeling.MetaLabelingAnalysis? MetaLabeling` | [C4] Esito dell'ultima analisi di meta-labeling sulla strategia corrente. |
| `p` | `string? FundingModelUsed` | [E-01, Fase 1 PRD-RISANAMENTO] Quale modello di funding ha usato l'ULTIMO run: la UI lo dichiara accanto al risultato ("degradare dicendolo"). Null = mercato non leveraged, funding non pertinente. |
| `p` | `BacktestResult? Result` | — |
| `p` | `TradeReport? TradeReport` | — |
| `p` | `KellySuggestion? Kelly` | — |
| `p` | `LeverageAdvice? LeverageAdvice` | — |
| `p` | `MonteCarloResult? McResult` | — |
| `p` | `EquityControlResult? PcResult` | — |
| `p` | `List&lt;IndicatorSeries&gt; EquitySeries` | — |
| `m` | `Task LoadKnownSymbolsAsync(CancellationToken ct = default)` | — |
| `m` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitionsFor(string strategyName)` | Definizioni dei parametri della strategia; fallback al primo prototipo se il nome non esiste. |

### 🧾 `ConfigDto` `(`

> Forma JSON dei preset — invariata rispetto al blocco @code originale, così i preset già salvati restano leggibili.

| | Firma | Descrizione |
|---|---|---|
| `m` | `string SerializeConfig(BacktestConfigSnapshot cfg)` | — |
| `m` | `BacktestConfigSnapshot ApplyConfig(string json, BacktestConfigSnapshot current)` | Applica un preset a : i campi con vincolo di catalogo (exchange/timeframe/strategia) sono presi dal preset solo se ancora validi; i parametri sono i default della strategia finale con overlay dei valori del preset (chia… |
| `m` | `(BacktestConfigSnapshot Snapshot, string? Message) ApplyHandoff(BacktestHandoffQuery q, BacktestConfigSnapshot current)` | Applica il contesto arrivato via query string. Valori assenti o malformati lasciano i correnti: il link è una comodità, mai un requisito. Il messaggio è non-null solo quando è arrivato davvero un contesto (symbol presen… |
| `m` | `string OptimizationHandoffUrl(BacktestConfigSnapshot cfg)` | Link a Optimization precompilata col contesto di questo backtest. |
| `m` | `Task&lt;BacktestActionResult&gt; RunAsync(BacktestConfigSnapshot cfg, CancellationToken ct)` | Esegue il backtest e calcola le analitiche derivate (equity series, trade report, Kelly, consulente leva) + experiment tracking best-effort. L'annullamento ( ) propaga al chiamante, che possiede il CTS. |
| `m` | `void RunMonteCarlo(int shuffles, decimal noisePercent)` | Montecarlo evoluta sui PnL del run corrente (no-op senza trade). Seed fisso: riproducibile tra un click e l'altro. |
| `m` | `void RunPerformanceControl(int windowSize, decimal threshold)` | Performance Control (profitto a finestra) sui trade del run corrente (no-op senza trade). |
| `m` | `Task&lt;BracketSuggestion&gt; SuggestBracketAsync(BacktestConfigSnapshot cfg, CancellationToken ct = default)` | — |
| `m` | `Task&lt;BacktestActionResult&gt; SaveStrategyAsync(string name, string strategyName, IReadOnlyDictionary&lt;string, decimal&gt; parameters, string us…` | — |
| `m` | `Task&lt;LoadedSavedStrategy?&gt; LoadSavedStrategyAsync(int id, string? userId, CancellationToken ct = default)` | Carica una strategia salvata dell'utente; null se non trovata (o di un altro utente). |

## `ProcioneMGR/Services/Backtesting/BollingerMeanReversionStrategy.cs`

### 📦 `BollingerMeanReversionStrategy` `: IStrategy`

> Bollinger Mean Reversion: Long quando il prezzo sfonda la banda inferiore (oversold), Short quando sfonda la superiore (overbought), Close quando il prezzo rientra attraversando la banda centrale (media).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/CompositeSignalStrategy.cs`

### 📦 `CompositeSignalStrategy` `: IStrategy`

> COMPOSITE strategy: combines up to 3 elementary signals from with AND/OR logic into an entry rule, plus up to 2 OR-combined exit conditions. This is the backbone of the creative-discovery layer: because every signal is normalized to 0-100, the whole "spec" is expressible as PLAIN DECIMAL PARAMETERS — which makes generated strategies natively sweepable by OptimizationEngine, rankable by Discovery, savable as SavedStrategy and tradable by the live engine, with ZERO changes to any of those modules. Parameter encoding (all decimal): Logic 0 = AND (all entry conditions), 1 = OR (any entry condition) Direction 0 = entry opens Long, 1 = entry opens Short EntryCount 1..3 — how many Entry{Sig,Op,Thr}i triplets are active EntrySigN signal id (0..SignalCatalog.SignalCount-1) EntryOpN 0 = "signal &lt; threshold", 1 = "signal &gt; threshold" EntryThrN threshold on the normalized 0-100 scale ExitCoun…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |

### ▫️ `Condition` `(int Signal, bool GreaterThan, decimal Threshold)`

| | Firma | Descrizione |
|---|---|---|
| `m` | `bool Evaluate(decimal?[][] matrix, int index)` | — |
| `m` | `bool HasValue(decimal?[][] matrix, int index)` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/DonchianBreakoutStrategy.cs`

### 📦 `DonchianBreakoutStrategy` `: IStrategy`

> Breakout di canale Donchian (il sistema di riferimento di Trombetta, cap. 6): - Long quando la close supera il Donchian High (HHV) a EntryPeriod della barra precedente; chiusura quando la close viola il Donchian Low (LLV) a ExitPeriod della barra precedente. - Short speculare (breakdown su LLV a EntryPeriod, uscita su HHV a ExitPeriod), abilitabile con il parametro Direction . Il confronto con il canale della barra PRECEDENTE e' obbligatorio: la close, per definizione, non puo' mai superare l'HHV calcolato sulla barra in corso (retroazione). Direction: 0 = solo long, 1 = solo short, 2 = entrambi.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/EmaCrossStrategy.cs`

### 📦 `EmaCrossStrategy` `: IStrategy`

> EMA Cross: segnale Long quando l'EMA veloce incrocia SOPRA la lenta, Short quando incrocia SOTTO. Il crossing usa la candela corrente e la precedente.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/EventTriggerStrategy.cs`

### 📦 `EventTriggerStrategy` `: IStrategy`

> EVENT-TRIGGERED strategy: enters on a DISCRETE market event (not a continuous price condition) and exits after a maximum holding time — the time-bound trade structure typical of event-driven intraday systems, which NO other strategy in the platform has. Event types (0-100 percentile scale where applicable, from ): 0 VolSpike — realized volatility jumps above the Threshold percentile (crossing, not level: the bar BEFORE was below) 1 VolCrush — volatility drops below (100 - Threshold) percentile (crossing) 2 RegimeFlipUp — the causal Supertrend direction flips down→up 3 RegimeFlipDown — flips up→down 4 PriceShockDown — a single-bar return in the bottom (100 - Threshold) percentile 5 PriceShockUp — a single-bar return above the Threshold percentile Exit: unconditionally after MaxHoldBars bars from entry (Close). The engine's SL/TP/trailing overlays remain available on top (stop variants of…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/FundingHistoryProvider.cs`

### 🔌 `IFundingHistoryProvider`

> Carica la serie storica dei funding rate per un simbolo, pronta per .

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;List&lt;FundingRatePoint&gt;&gt; GetAsync(string symbol, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)` | — |

### 📦 `FundingHistoryProvider` `(IDbContextFactory&lt;ApplicationDbContext&gt; dbFactory) : IFundingHistoryProvider`

> [T0.2 roadmap macchina-ricerca] Legge i funding storici da (Metric = "FundingRate", già in percento ×100, firmati) — la serie che il sync del sentiment raccoglie e che finora nessun motore consumava. La finestra parte 8 ORE PRIMA di : il lookup del motore è a gradini (ultimo evento ≤ ts) e senza quel margine le prime candele del backtest cadrebbero prima del primo evento in finestra, degradando inutilmente alla costante.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;List&lt;FundingRatePoint&gt;&gt; GetAsync(string symbol, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)` | — |
| `m` | `string ToBaseTicker(string symbol)` | "BTC/USDT" → "BTC" (stessa convenzione del sync sentiment). |

## `ProcioneMGR/Services/Backtesting/FundingRateLookup.cs`

### 🧾 `FundingRatePoint` `(DateTime TimestampUtc, decimal RatePercentPer8h);`

> Un evento di funding: timestamp e rate in PERCENTO per 8h (×100, convenzione piattaforma). Firmato.

### 📦 `FundingRateLookup`

> [T0.2 roadmap macchina-ricerca] Lookup a gradini sulla serie storica dei funding rate: il rate applicabile a un istante t è quello dell'ultimo evento di funding ≤ t. Prima del primo evento non si inventa nulla: si torna al fallback (la costante di configurazione), così un backtest che parte prima della storia disponibile degrada in modo dichiarato invece di fingere. Il motore applica il rate pro-rata per candela (modello già esistente): qui cambia solo che il rate è quello STORICO e FIRMATO invece di una costante senza segno.

| | Firma | Descrizione |
|---|---|---|
| `m` | `FundingRateLookup? BuildOrNull(IReadOnlyList&lt;FundingRatePoint&gt;? history)` | Null se la storia è assente o vuota: il chiamante usa la costante come sempre. |
| `m` | `decimal RateFracAt(DateTime tsUtc, decimal fallbackFrac)` | Frazione per-8h applicabile a (ultimo evento ≤ ts), oppure se ts precede il primo evento della storia. |

## `ProcioneMGR/Services/Backtesting/GridMeanReversionStrategy.cs`

### 📦 `GridMeanReversionStrategy` `: IStrategy`

> [Fase 5b — docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Mean reversion a gradini fissi attorno a un ancoraggio mobile: entra quando il prezzo si allontana di EntryRungs gradini dall'SMA di riferimento, esce quando ne ha recuperato uno. È il **ciclo finito e restartabile** che il PDF descrive come cuore economico del grid trading. Non è grid trading, e il nome lo dice apposta. Un grid vero appoggia molti ordini limite simultanei sopra e sotto il prezzo e porta più posizioni insieme; questo motore è a posizione singola per costruzione ( Portfolio ha un solo stato flat/long/short), quindi un grid multi-ordine non è esprimibile qui. Chiamarlo "Grid" e attribuirgli i numeri del PDF (rendimento 8,39%, Sharpe 0,38 — già deboli di loro) significherebbe misurare una cosa e raccontarne un'altra: il rischio che questa piattaforma ha imparato a evitare. Cosa cattura davvero: l'idea che in un me…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/IBacktestEngine.cs`

### 🔌 `IBacktestEngine`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;BacktestResult&gt; RunBacktestAsync(BacktestConfiguration config, CancellationToken ct)` | Carica le candele dal DB per il range in ed esegue il backtest. |
| `m` | `Task&lt;BacktestResult&gt; RunBacktestAsync(BacktestConfiguration config, IReadOnlyList&lt;OhlcvData&gt; candles, CancellationToken ct)` | Esegue il backtest su candele gia' caricate (caching per l'ottimizzatore). Le candele devono coprire il range desiderato ed essere ordinate cronologicamente. |
| `m` | `Task&lt;BacktestResult&gt; RunBacktestAsync(BacktestConfiguration config, IReadOnlyList&lt;OhlcvData&gt; candles, IStrategy strategy, CancellationTok…` | Esegue il backtest con un'istanza di già pronta invece di crearla per nome dalla factory. Punto di aggancio per strategie che richiedono uno stato costruito esternamente (es. MlStrategy con un IReturnPredictor già addes… |

## `ProcioneMGR/Services/Backtesting/IStrategy.cs`

### 🧾 `StrategyParameterDefinition` `(string Key, string Label, decimal Default, decimal Min, decimal Max);`

> Descrizione di un parametro di strategia, usata per generare la UI dinamica.

### 🔌 `IStrategy`

> Strategia di trading. Ciclo di vita: 1. pre-calcola UNA volta gli indicatori necessari (array allineati per indice alle candele) -&gt; hot-loop O(1), niente ricalcolo. 2. viene chiamata per ogni candela e restituisce il segnale. Nota di design: lo spec prevedeva EvaluateSignal(IndicatorValues, price, ts); ho "interiorizzato" gli IndicatorValues nello stato della strategia (calcolati in InitializeAsync) per evitare allocazioni nel loop e per O(1) sull'indice corrente.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | Nome tecnico (chiave), es. "EmaCross". |
| `p` | `string DisplayName` | Nome leggibile per la UI, es. "EMA Cross". |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

### 📦 `StrategyParametersExtensions`

> Helper comuni alle strategie.

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal GetOrDefault(this IReadOnlyDictionary&lt;string, decimal&gt; p, string key, decimal fallback)` | — |

## `ProcioneMGR/Services/Backtesting/MacdTrendStrategy.cs`

### 📦 `MacdTrendStrategy` `: IStrategy`

> MACD Trend: Long quando il MACD incrocia SOPRA la Signal (istogramma da negativo a positivo), Short quando incrocia SOTTO (istogramma da positivo a negativo).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/MlStrategy.cs`

### 📦 `MlStrategy` `: IStrategy`

> Il "collante" fra i modelli ML (cap. 6-12 del libro) e il backtest: carica un già addestrato, in pre-calcola i fattori usati in addestramento su tutta la serie (stesso schema di DatasetBuilder , ma senza target), e in traduce la predizione di rendimento forward in un tramite soglie long/short. Così ogni modello diventa immediatamente back-testabile, ottimizzabile e inseribile nell'ensemble. DEVIAZIONE FLAGGATA: a differenza delle altre strategie, MlStrategy non è creabile dalla StrategyFactory per nome (switch senza reflection, zero-arg) perché richiede un predittore già addestrato e la lista dei fattori con cui è stato addestrato — non rappresentabili come Dictionary&lt;string, decimal&gt; . Si usa costruendola direttamente e passandola al nuovo overload IBacktestEngine.RunBacktestAsync(config, candles, strategy, ct) . L'integrazione con Optimization/Discovery/Ensemble/UI (selezione de…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |
| `m` | `float[]? TryGetPredictorInput(int index)` | Vettore/finestra di input per il predittore all'indice dato (o null in warm-up). Esposto perché il dual-read ML (Fase 2a, TradingEngine) invii al servizio remoto ESATTAMENTE lo stesso input usato per la decisione locale… |

## `ProcioneMGR/Services/Backtesting/MomentumStrategy.cs`

### 📦 `MomentumStrategy` `: IStrategy`

> Momentum: Long quando il momentum su LookbackPeriod supera +Threshold, Short quando scende sotto -Threshold, Close quando il momentum rientra vicino a zero (\|momentum\| &lt; Threshold/2). momentum = (price - price[index - lookback]) / price[index - lookback]

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/PriceSmaCrossStrategy.cs`

### 📦 `PriceSmaCrossStrategy` `: IStrategy`

> La strategia "preferita" di McAllen (Charting and Technical Analysis, cap. 16-17): prezzo contro media mobile semplice di lungo periodo (classico 200 DMA). - Long quando la chiusura attraversa la SMA dal basso verso l'alto; - Close (o Short, se abilitato) quando la viola dall'alto verso il basso. La SMA agisce storicamente da supporto/resistenza: sopra la media si sta nel mercato, sotto si sta in cash. Combinare con StopLossPercent/TrailingStopPercent della per replicare il sistema completo del libro (200 DMA + stop loss 6-8% + trailing stop).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/RegimeConditionalStrategy.cs`

### 📦 `RegimeConditionalStrategy` `: IStrategy`

> REGIME-CONDITIONAL meta-strategy: classifies every bar into one of three CAUSAL market buckets and delegates the signal to a different sub-strategy per bucket — "trend-follow in trends, mean-revert sideways, stand aside in the regime you distrust", as a single backtestable/optimizable strategy. Regime proxy (deliberately DB-free — declared deviation from the original spec, which suggested loading the saved K-means RegimeModel: strategies in this platform are dependency-free by design (factory is new-based), and a DB-bound strategy could not run inside OptimizationEngine sweeps or the live engine without new plumbing. The proxy is computed causally from candles: SMA(TrendPeriod) relative slope over the last TrendPeriod/4 bars → TrendUp / TrendDown / Sideways with a ±0.5% dead zone.) Sub-strategy catalog (index → strategy, 0 = none/stand-aside). Only strategies whose EvaluateSignal is a p…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;string&gt; SubStrategyCatalog` | Sub-strategy names, index-aligned to the UpStrategy/DownStrategy/FlatStrategy parameter values. |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/RsiOversoldStrategy.cs`

### 📦 `RsiOversoldStrategy` `: IStrategy`

> RSI Oversold/Overbought: Long quando RSI &lt; soglia oversold, Short quando RSI &gt; soglia overbought, altrimenti Hold. Il motore gestisce il flip della posizione sul segnale opposto.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/SignalCatalog.cs`

### 📦 `SignalCatalog`

> Catalog of ELEMENTARY SIGNALS for composable strategies, all normalized to a COMMON 0-100 scale so that thresholds are comparable across signals (a "&lt; 20" means "in the bottom quintile of its own recent history" for every unbounded signal, and the native scale for oscillators that already live in 0-100): 0 Rsi — RSI(14), native 0-100 1 StochD — Stochastic %D(14,3), native 0-100 2 BollingerB — %B(20,2) × 100 (position inside the bands; can exceed 0-100 slightly) 3 SupertrendDir — 100 when the Supertrend(10,3) trend is up, 0 when down 4 VolumeRatioPct — causal rolling percentile of volume/SMA20(volume) 5 VwapDevPct — causal rolling percentile of the deviation from the UTC-session VWAP 6 MomentumPct — causal rolling percentile of the 10-bar rate of change 7 MacdHistPct — causal rolling percentile of the MACD(12,26,9) histogram 8 DistFromSmaPct — causal rolling percentile of (close - SMA…

| | Firma | Descrizione |
|---|---|---|
| `k` | `int SignalCount` | — |
| `k` | `int PercentileWindow` | — |
| `k` | `int EventDecayBars` | [F3] Barre di decadimento lineare dei segnali post-evento (12/13): 100 alla barra evento → 0 dopo N barre. |
| `p` | `IReadOnlyList&lt;string&gt; SignalNames` | Display names, index-aligned to the signal ids (for UI/log readability). |

### 🧾 `CacheEntry` `(int Count, DateTime FirstTs, DateTime LastTs, Task&lt;decimal?[][]&gt; Task);`

> Impronta del contenuto accanto al task: la cache per ISTANZA della lista è corretta nei backtest (liste immutabili per run) ma il TradingEngine live riusa UN buffer che cresce/ scorre e ri-inizializza la strategia a ogni candela — senza il controllo d'impronta la matrice tornava stantia: più corta del buffer (IndexOutOfRange a ogni candela, trovato DAL VIVO la prima notte di Composite su una corsia) o, peggio, della stessa lunghezza con contenuto vecchio = segnali sbagliati in silenzio su una finestra rotolante. (Count, primo, ultimo timestamp) cambia sempre quando il buffer cresce o scorre.

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal?[] CausalPercentile(decimal?[] values, int window, CancellationToken ct = default)` | Causal rolling percentile rank ×100: |

## `ProcioneMGR/Services/Backtesting/StochasticStrategy.cs`

### 📦 `StochasticStrategy` `: IStrategy`

> Oscillatore stocastico (mean-reversion, un secondo oscillatore distinto dall'RSI, molto usato intraday): %K = 100*(close - LLV) / (HHV - LLV) sui minimi/massimi a KPeriod ; %D = SMA(%K, DPeriod ) (linea lenta, filtra il rumore). Long quando %D scende sotto la soglia di ipervenduto, Short quando supera l'ipercomprato — stessa struttura robusta di RsiOversold ma con un oscillatore che reagisce alla posizione della close nel range, non alla forza relativa. Riusa gli indicatori esistenti (Donchian per HHV/LLV, SMA per %D): nessun nuovo calcolo.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/StrategyFactory.cs`

### 🔌 `IStrategyFactory`

> Crea istanze di strategia per nome (switch case, niente reflection) ed espone i "prototipi" per popolare la UI (dropdown + definizioni parametri). Aggiungere una strategia = nuova classe + un case qui.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;IStrategy&gt; Prototypes` | Istanze "vuote" per leggere DisplayName/ParameterDefinitions nella UI. |
| `m` | `IStrategy Create(string strategyName)` | — |

### 📦 `StrategyFactory` `: IStrategyFactory`

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;IStrategy&gt; Prototypes` | — |
| `m` | `IStrategy Create(string strategyName)` | — |

## `ProcioneMGR/Services/Backtesting/SupertrendStrategy.cs`

### 📦 `SupertrendStrategy` `: IStrategy`

> Supertrend (trend-following su ATR, il sistema intraday crypto piu' diffuso): due bande attorno al prezzo medio (H+L)/2 a distanza Multiplier * ATR , con la logica standard di "locking" delle bande finali; il trend commuta quando la close attraversa la banda attiva. Long allo switch rialzista; allo switch ribassista Short (se AllowShort=1 ) oppure Close. ANTI-LOOK-AHEAD: la decisione alla barra i usa esclusivamente ATR/prezzo fino alla barra i (stessa convenzione "decido alla chiusura della barra" di tutte le altre strategie).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

## `ProcioneMGR/Services/Backtesting/VwapReversionStrategy.cs`

### 📦 `VwapReversionStrategy` `: IStrategy`

> VWAP Reversion (la strategia intraday "per eccellenza"): il VWAP (Volume Weighted Average Price) di SESSIONE e' il prezzo medio ponderato per i volumi dall'inizio della giornata UTC, il benchmark che ogni operatore intraday osserva. Quando il prezzo si allontana dal VWAP oltre una soglia si assume un rientro verso la media: Long se e' sotto il VWAP di Threshold , Short se e' sopra (con AllowShort=1 ), Close al riattraversamento del VWAP. SESSIONE: il VWAP si azzera a ogni cambio di data UTC (convenzione standard, coerente con le candele giornaliere e il funding degli exchange). ANTI-LOOK-AHEAD: il VWAP alla barra i usa solo le barre dall'inizio sessione fino a i inclusa (valore corrente, non futuro).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `IReadOnlyList&lt;StrategyParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)` | — |

# `Services/Optimization/`

## `ProcioneMGR/Services/Optimization/Bayesian/BayesianOptimizationEngine.cs`

### 📦 `BayesianOptions`

> Iperparametri del surrogato Gaussian Process e dell'acquisizione.

| | Firma | Descrizione |
|---|---|---|
| `p` | `double LengthScale` | Lengthscale del kernel RBF sullo spazio normalizzato [0,1]^d. |
| `p` | `double SignalVariance` | Varianza del segnale (ampiezza a priori delle funzioni). |
| `p` | `double NoiseVariance` | Rumore/regolarizzazione sulla diagonale (stabilità numerica + osservazioni rumorose). |
| `p` | `double ExplorationXi` | Parametro di esplorazione ξ dell'Expected Improvement (più alto ⇒ più esplorativo). |
| `p` | `int AcquisitionSamples` | Quanti candidati casuali campionare per massimizzare l'acquisizione a ogni passo. |
| `p` | `bool OptimizeHyperparameters` | Se true (default), e vengono STIMATI dai dati massimizzando la log-verosimiglianza marginale del GP a ogni passo, invece di restare fissi (fissi ⇒ il surrogato non si adatta e la ricerca degenera verso il casuale). I va… |
| `p` | `int MinPointsForHyperparameterFit` | Numero minimo di osservazioni per stimare gli iperparametri via marginal-likelihood. |
| `p` | `int Seed` | Seme di base: la ricerca è deterministica a parità di seme e di storia. |

### 🔌 `IHyperparameterOptimizer`

> Ottimizzatore di iperparametri: dato lo storico dei punti valutati, propone il prossimo punto da provare. Alternativa al grid search esaustivo quando lo spazio dei parametri è grande e ogni valutazione è costosa (un walk-forward completo). Rif. docs/archive/ROADMAP-QLIB §1.6. METODOLOGIA (decisione presa nell'aggancio a OptimizationEngine): l'obiettivo che GUIDA la ricerca è lo Sharpe della finestra (la stessa Statistics.SharpeRatio usata dal grid, in-sample o out-of-sample secondo SelectionMetric ) — un surrogato economico e STAZIONARIO. Il Deflated Sharpe NON è ricalcolato a ogni iterazione (sarebbe non-stazionario: la correzione da test multipli cambia con ogni nuovo trial) bensì applicato UNA VOLTA a fine ricerca come VERDETTO sul migliore, sulla distribuzione di TUTTI i punti visitati — esattamente il ruolo che il DSR ha per il grid. Il contratto resta agnostico rispetto all'obiett…

| | Firma | Descrizione |
|---|---|---|
| `m` | `double[] SuggestNext(IReadOnlyList&lt;EvaluatedPoint&gt; history, ParameterSpace space)` | Prossimo vettore di parametri (spazio reale) che massimizza l'acquisizione dato lo storico. |

### 📦 `BayesianOptimizationEngine` `(BayesianOptions? options = null) : IHyperparameterOptimizer`

> via Gaussian Process (kernel RBF, media/varianza posteriore in forma chiusa con MathNet) e acquisizione Expected Improvement . Nessuna libreria GP dedicata: kernel + Cholesky + solve sono poche decine di righe di algebra lineare. Deterministico a parità di e di storia.

| | Firma | Descrizione |
|---|---|---|
| `m` | `double[] SuggestNext(IReadOnlyList&lt;EvaluatedPoint&gt; history, ParameterSpace space)` | — |
| `m` | `(double LengthScale, double SignalVariance) FitKernel(IReadOnlyList&lt;EvaluatedPoint&gt; history, ParameterSpace space)` | Iperparametri del kernel stimati via marginal-likelihood dalla storia (spazio reale → normalizzato). Esposto per ispezione/diagnostica; ritorna i valori fissi se i punti sono &lt; . |

## `ProcioneMGR/Services/Optimization/Bayesian/BayesianSearch.cs`

### 🧾 `BayesianSearchResult` `(double[] BestParameters, double BestScore, IReadOnlyList&lt;EvaluatedPoint&gt; History);`

> Esito di una ricerca bayesiana: il punto migliore, il suo punteggio e lo storico completo.

### 📦 `BayesianSearch` `(IHyperparameterOptimizer optimizer)`

> Driver "ask-tell" della ricerca bayesiana: campiona alcuni punti iniziali a caso, poi chiede ripetutamente all' il prossimo punto e valuta l'obiettivo, finché non esaurisce le iterazioni. L'obiettivo è un delegate qualunque, così può avvolgere un backtest walk-forward il cui punteggio è il Deflated Sharpe (Fase 1) — l'ottimizzatore resta agnostico. Deterministico a parità di seme (inizializzazione + ottimizzatore seedati).

## `ProcioneMGR/Services/Optimization/Bayesian/ParameterSpace.cs`

### 🧾 `ParameterDimension` `(string Name, double Min, double Max, bool IsInteger = false, double Step = 0);`

> Una dimensione dello spazio di ricerca: intervallo [Min,Max], eventualmente intero o a passo.

### 🧾 `EvaluatedPoint` `(double[] Parameters, double Score);`

> Un punto valutato: i parametri (nello spazio reale) e il punteggio osservato dell'obiettivo.

### 📦 `ParameterSpace` `(IReadOnlyList&lt;ParameterDimension&gt; dimensions)`

> Spazio dei parametri per l'ottimizzazione bayesiana. Mappa fra coordinate reali (quelle che il backtest riceve) e normalizzate [0,1]^d (quelle su cui lavora il Gaussian Process, dove una singola lengthscale ha senso su tutte le dimensioni). La denormalizzazione "aggancia" i valori a interi/passo e li limita all'intervallo. Puro/deterministico.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;ParameterDimension&gt; Dimensions` | — |
| `m` | `double[] Normalize(double[] actual)` | Reale → [0,1]^d (per il GP). Dimensioni degeneri (Min==Max) mappano a 0.5. |
| `m` | `double[] Denormalize(double[] unit)` | [0,1]^d → reale, con snap a intero/passo e clamp all'intervallo. |

## `ProcioneMGR/Services/Optimization/IOptimizationEngine.cs`

### 🔌 `IOptimizationEngine`

## `ProcioneMGR/Services/Optimization/OptimizationEngine.cs`

### 📦 `OptimizationEngine` `(`

> Ottimizzazione parametri via Grid Search + Walk-Forward Validation. Per ogni finestra walk-forward (train in-sample / test out-of-sample): - testa tutte le combinazioni di parametri IN PARALLELO (Parallel.ForEachAsync); - per ognuna esegue 2 backtest (in-sample e out-of-sample) su candele GIA' caricate (nessuna ricarica dal DB) e ne calcola lo Sharpe annualizzato; - sceglie i parametri migliori della finestra (default: per Sharpe IN-SAMPLE, vedi nota); - concatena (compounded) l'equity out-of-sample alla curva walk-forward globale. NOTA METODOLOGICA: la selezione per-finestra usa lo Sharpe IN-SAMPLE ( ). Selezionare sull'out-of-sample equivale a ottimizzare sul test set (peeking) e gonfia lo Sharpe OOS: lo si puo' forzare via config ma sconsigliato. Lo Sharpe OOS resta la metrica di VALUTAZIONE, non di selezione. Memoria: si tengono solo gli aggregati scalari per combinazione e l'equity…

| | Firma | Descrizione |
|---|---|---|
| `m` | `string ComboKey(IReadOnlyDictionary&lt;string, decimal&gt; combo)` | InvariantCulture esplicita: la chiave è anche PARSATA altrove (es. per l'heatmap) — con la cultura corrente del thread (es. it-IT, virgola come separatore decimale) un valore come "0,001" spezzerebbe lo split per virgol… |

### ▫️ `Window` `(int Index, DateTime IsStart, DateTime IsEnd, DateTime OosStart, DateTime OosEnd);`

### 🧾 `ComboResult` `(`

### 🧾 `ComboAggregate` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ComboAggregate From(ComboResult c)` | — |
| `m` | `ComboAggregate Add(ComboResult c)` | — |

## `ProcioneMGR/Services/Optimization/OptimizationModels.cs`

### 🔢 `OptimizationSelectionMetric`

> Metrica con cui scegliere i parametri "migliori" di ogni finestra walk-forward. Default = InSampleSharpe (corretto: si seleziona sul train, si misura sul test). OutOfSampleSharpe seleziona sul test set: ottimistico/peeking, da usare con cautela.

### 🔢 `SearchStrategy`

> Come esplorare lo spazio dei parametri. GridSearch (default) = prodotto cartesiano esaustivo. Bayesian = ricerca guidata (Gaussian Process + Expected Improvement) quando lo spazio è grande e ogni valutazione (un walk-forward) è costosa. Entrambi restano vincolati allo stesso walk-forward e allo stesso verdetto finale (Deflated Sharpe su tutti i punti visitati).

### 📦 `OptimizationConfiguration`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ExchangeName` | — |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `DateTime From` | — |
| `p` | `DateTime To` | — |
| `p` | `decimal InitialCapital` | — |
| `p` | `decimal CommissionPercent` | — |
| `p` | `decimal SlippagePercent` | [R2] Attrito sfavorevole applicato a OGNI fill, in % del prezzo. Prima non esisteva su questo modello e BuildBacktestConfig non lo impostava: l'intera SELEZIONE dei parametri (e, a cascata, quella dei candidati di Disco… |
| `p` | `decimal PositionSizePercent` | % del capitale impegnata per trade durante l'ottimizzazione. |
| `p` | `string StrategyName` | — |
| `p` | `List&lt;ParameterRange&gt; ParameterRanges` | — |
| `p` | `WalkForwardConfiguration WalkForward` | — |
| `p` | `OptimizationSelectionMetric SelectionMetric` | Come selezionare i parametri della finestra. Default = in-sample (corretto). |
| `p` | `SearchStrategy SearchStrategy` | Strategia di ricerca. Default = GridSearch (comportamento storico bit-identico). |
| `p` | `int BayesianIterations` | Ramo Bayesian: passi guidati (Expected Improvement) DOPO l'esplorazione iniziale, per finestra. |
| `p` | `int BayesianInitialRandom` | Ramo Bayesian: punti iniziali casuali (esplorazione), per finestra. |
| `p` | `int BayesianSeed` | Ramo Bayesian: seme — la ricerca è deterministica a parità di seme e di storia. |

### 📦 `ParameterRange`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `decimal Min` | — |
| `p` | `decimal Max` | — |
| `p` | `decimal Step` | — |
| `p` | `bool IsInteger` | — |

### 📦 `WalkForwardConfiguration`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int InSampleMonths` | — |
| `p` | `int OutOfSampleMonths` | — |
| `p` | `int StepMonths` | — |
| `p` | `int EmbargoBars` | [T0.1 roadmap macchina-ricerca] Barre di cuscinetto SALTATE all'inizio di ogni finestra out-of-sample. Senza, IS e OOS sono contigui ( oosStart = isEnd ) e l'informazione filtra attraverso il confine: una posizione aper… |

### 🔢 `OptimizationValidationMethod`

> [T1.6 fase 2] Come giudicare i parametri in /optimization: il walk-forward storico (UN percorso out-of-sample) o il CPCV (C(gruppi, gruppiTest) percorsi → una distribuzione). Il CPCV richiede GridSearch: i backtest per (combinazione × gruppo) sono pre-calcolati sull'intera griglia.

### 📦 `CpcvConfiguration`

> [T1.6 roadmap macchina-ricerca] Configurazione della validazione CPCV per il percorso strategie: invece di UN solo percorso out-of-sample (walk-forward + holdout), C(gruppi, gruppiTest) combinazioni di gruppi contigui → una DISTRIBUZIONE di Sharpe fuori campione per candidato.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Groups` | Gruppi temporali contigui in cui dividere la serie. |
| `p` | `int TestGroups` | Gruppi usati come test in ogni combinazione: C(Groups, TestGroups) percorsi. |
| `p` | `int PurgeBars` | Barre rimosse dal train PRIMA di ogni gruppo di test (stessa semantica di CombinatorialPurgedCv). |
| `p` | `int EmbargoBars` | Barre rimosse dal train DOPO ogni gruppo di test. |

### 📦 `CpcvPathResult`

> Un percorso CPCV: la combinazione, i parametri scelti sul train e l'esito sul test mai visto.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Combination` | — |
| `p` | `IReadOnlyList&lt;int&gt; TestGroups` | — |
| `p` | `Dictionary&lt;string, decimal&gt; BestParameters` | — |
| `p` | `decimal TrainSharpe` | — |
| `p` | `decimal OosSharpe` | — |

### 📦 `CpcvResult`

> Esito CPCV: la distribuzione degli Sharpe out-of-sample sui percorsi è il prodotto — non un numero solo ma quanti percorsi reggono, con che mediana e con che code. Il PBO è calcolato sul pannello dei rendimenti full-period dei candidati (CSCV, riusa BacktestOverfitting).

| | Firma | Descrizione |
|---|---|---|
| `p` | `List&lt;CpcvPathResult&gt; Paths` | — |
| `p` | `decimal MedianOosSharpe` | — |
| `p` | `decimal P05OosSharpe` | — |
| `p` | `decimal P95OosSharpe` | — |
| `p` | `int PositivePaths` | — |
| `p` | `int TotalPaths` | — |
| `p` | `double? Pbo` | — |
| `p` | `int CombinationsTested` | — |
| `p` | `Dictionary&lt;string, decimal&gt; ModalParameters` | Parametri più spesso scelti sui train dei percorsi (moda): il candidato "stabile". |
| `p` | `decimal SelectionStability` | Quota di percorsi in cui la scelta del train coincide con la moda: stabilità della selezione. |

### 📦 `OptimizationResult`

| | Firma | Descrizione |
|---|---|---|
| `p` | `List&lt;ParameterSet&gt; BestParameters` | Top 10 combinazioni per Sharpe out-of-sample medio sulle finestre. |
| `p` | `WalkForwardResult WalkForwardAnalysis` | — |
| `p` | `Dictionary&lt;string, decimal&gt; AllResults` | key = "param1=val1,param2=val2" -&gt; Sharpe out-of-sample medio sulle finestre. |
| `p` | `TimeSpan ExecutionTime` | — |
| `p` | `int TotalCombinationsTested` | — |
| `p` | `Validation.SelectionValidation? Validation` | Verdetto anti-overfitting sul migliore selezionato: Deflated Sharpe che corregge lo Sharpe grezzo per il selection bias (aver provato N combinazioni). null se non calcolabile (curva combinata troppo corta o meno di 2 co… |

### 📦 `ParameterSet`

| | Firma | Descrizione |
|---|---|---|
| `p` | `Dictionary&lt;string, decimal&gt; Parameters` | — |
| `p` | `decimal InSampleSharpe` | — |
| `p` | `decimal OutOfSampleSharpe` | — |
| `p` | `decimal TotalReturn` | — |
| `p` | `decimal MaxDrawdown` | — |
| `p` | `int TotalTrades` | — |

### 📦 `WalkForwardResult`

| | Firma | Descrizione |
|---|---|---|
| `p` | `List&lt;WalkForwardWindow&gt; Windows` | — |
| `p` | `decimal AverageOutOfSampleSharpe` | — |
| `p` | `List&lt;EquityPoint&gt; CombinedEquityCurve` | Equity curve concatenata (compounded) dei soli test out-of-sample. |

### 📦 `WalkForwardWindow`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int WindowIndex` | — |
| `p` | `DateTime InSampleStart` | — |
| `p` | `DateTime InSampleEnd` | — |
| `p` | `DateTime OutOfSampleStart` | — |
| `p` | `DateTime OutOfSampleEnd` | — |
| `p` | `Dictionary&lt;string, decimal&gt; BestParameters` | — |
| `p` | `decimal InSampleSharpe` | — |
| `p` | `decimal OutOfSampleSharpe` | — |
| `p` | `decimal OutOfSampleReturn` | — |

### 📦 `OptimizationProgress`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int CombinationsTested` | — |
| `p` | `int TotalCombinations` | — |
| `p` | `int CurrentWindow` | — |
| `p` | `int TotalWindows` | — |
| `p` | `decimal BestSharpeSoFar` | — |
| `p` | `string Message` | — |

## `ProcioneMGR/Services/Optimization/OptimizationPageService.cs`

### 🧾 `OptRange` `(string Key, string Label, decimal Min, decimal Max, decimal Step, bool IsInteger);`

> Un range di ricerca del form (Min/Max/Step/Intero) con la sua etichetta di UI.

### 🧾 `OptimizationConfigSnapshot` `(`

> Fotografia completa del form di Optimization.razor — usata per i preset/memoria dell'ultima configurazione, per l'handoff da Backtest/ML Lab e come input di .

### 🧾 `OptActionResult` `(string Message, bool IsError)`

> Esito di un'azione con messaggio per l'operatore.

| | Firma | Descrizione |
|---|---|---|
| `m` | `OptActionResult Ok(string message)` | — |
| `m` | `OptActionResult Error(string message)` | — |

### 🧾 `OptimizationHandoffQuery` `(`

> Contesto opzionale arrivato via query string (handoff dal Backtest o dal ML Lab).

### 🧾 `HeatmapMatrix` `(string[] Xs, string[] Ys, double?[][] Z);`

> Matrice della heatmap di robustezza (Sharpe OOS medio su 2 parametri); Z null = combinazione mai valutata.

### 📦 `OptimizationPageService` `(`

> Orchestrazione estratta da Components/Pages/Optimization.razor (P1-5, PRD-CONSOLIDAMENTO- ARCHITETTURA.md §3.3): costruzione dei range di default per strategia (incluso il caso speciale "Ml" a soglie), validazione e run dello sweep walk-forward (grid/Bayesian) con experiment tracking, handoff da Backtest/ML Lab col ricentraggio dei range, (de)serializzazione validata dei preset, salvataggio della configurazione migliore e parsing della matrice heatmap — tutta la logica che prima viveva nel blocco @code del componente senza test indipendenti da Blazor. Il componente resta responsabile solo di ciò che è intrinsecamente Blazor: binding del form, progress bar ( IProgress + StateHasChanged ), CTS di annullamento e JS interop della heatmap. Lo stato del "run corrente" (risultato, config del run, equity walk-forward) vive qui perché è stato applicativo condiviso fra run→heatmap→salvataggio→lin…

| | Firma | Descrizione |
|---|---|---|
| `k` | `string MlStrategyName` | — |
| `p` | `IReadOnlyList&lt;string&gt; KnownSymbols` | — |
| `p` | `List&lt;SavedMlModel&gt; MlModels` | — |
| `p` | `OptimizationResult? Result` | — |
| `p` | `CpcvResult? CpcvResult` | [T1.6 fase 2] Esito CPCV del run corrente (mutuamente esclusivo con ). |
| `p` | `OptimizationConfiguration? ResultConfig` | Config del run corrente: i link "Backtest →" restano coerenti anche se il form cambia dopo. |
| `p` | `List&lt;IndicatorSeries&gt; EquitySeries` | — |
| `m` | `Task LoadInitialDataAsync(CancellationToken ct = default)` | NB: come nell'originale, i modelli ML NON sono filtrati per utente (la select filtra poi per symbol/timeframe). |
| `m` | `IReadOnlyList&lt;OptRange&gt; DefaultRangesFor(string strategyName)` | Range di partenza per la strategia: per "Ml" le sole soglie Long/Short (il modello si sceglie a parte); per le strategie a regole, min = default e max = default + 4 step, con step al 20% (interi: parametri "Period"/"Loo… |
| `m` | `int TotalCombinations(IReadOnlyList&lt;OptRange&gt; ranges)` | Numero di combinazioni del prodotto cartesiano dei range (0 se uno step non è positivo). |

### 🧾 `RangeDto` `(string Key, decimal Min, decimal Max, decimal Step);`

> Forma JSON dei preset — invariata rispetto al blocco @code originale, così i preset già salvati restano leggibili.

### 🧾 `ConfigDto` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `string SerializeConfig(OptimizationConfigSnapshot cfg)` | — |
| `m` | `OptimizationConfigSnapshot ApplyConfig(string json, OptimizationConfigSnapshot current)` | Applica un preset a : exchange/timeframe/strategia presi dal preset solo se ancora validi ("Ml" incluso); il modello ML si azzera se la strategia finale non è "Ml"; i range sono i default della strategia finale con over… |
| `m` | `(OptimizationConfigSnapshot Snapshot, string? Message) ApplyHandoff(OptimizationHandoffQuery q, OptimizationConfigSnapshot current)` | Applica il contesto arrivato via query string: valori assenti o malformati lasciano i correnti. I range partono dai default della strategia finale e vengono RICENTRATI sui parametri del run di provenienza (min = valore,… |
| `m` | `string BacktestHandoffUrl(Dictionary&lt;string, decimal&gt; parameters)` | Link al Backtest precompilato col contesto del run corrente e i parametri della riga scelta. |
| `m` | `Task&lt;OptActionResult&gt; RunAsync(OptimizationConfigSnapshot cfg, string? userId, IProgress&lt;OptimizationProgress&gt;? progress, CancellationTok…` | Esegue lo sweep walk-forward (grid o Bayesian) e popola Result/ResultConfig/EquitySeries + experiment tracking best-effort. Il progress e l'annullamento appartengono al chiamante ( propaga: il componente possiede il CTS… |
| `m` | `HeatmapMatrix? BuildHeatmapMatrix(string xName, string yName)` | Costruisce la matrice della heatmap dai risultati "k1=v1,k2=v2" → Sharpe OOS. Null se non c'è un run. Z null = combinazione mai valutata (tipico del Bayesian, che non copre la griglia). |
| `m` | `Task&lt;OptActionResult?&gt; SaveBestAsync(string name, string strategyName, string userId, CancellationToken ct = default)` | Salva la combinazione migliore del run corrente come strategia "ottimizzata". Null = niente da salvare (nessun run o nessun risultato): nessun messaggio, come nell'originale. |

## `ProcioneMGR/Services/Optimization/Statistics.cs`

### 📦 `Statistics`

> Statistiche per la valutazione delle strategie (Sharpe ratio annualizzato).

| | Firma | Descrizione |
|---|---|---|
| `m` | `int PeriodsPerYear(string timeframe)` | Numero di periodi all'anno per timeframe (per annualizzare lo Sharpe). |
| `m` | `decimal SharpeRatio(IReadOnlyList&lt;EquityPoint&gt; equityCurve, int periodsPerYear, decimal riskFreeRateAnnual = 0.02m)` | Sharpe ratio annualizzato calcolato sui rendimenti periodici dell'equity curve. |
| `m` | `double? DeflatedSharpeSingleTrack(IReadOnlyList&lt;EquityPoint&gt;? equityCurve, int periodsPerYear)` | Deflated Sharpe di un SINGOLO track dai suoi : con un solo trial il DSR collassa sul Probabilistic Sharpe (SR* = 0), cioè la probabilità che il vero Sharpe superi 0 dato T e i momenti (asimmetria/curtosi) dei rendimenti… |
| `m` | `decimal Sqrt(decimal value)` | Radice quadrata in decimal (Newton-Raphson), come negli indicatori. |
| `m` | `decimal AnnualizedReturn(IReadOnlyList&lt;EquityPoint&gt; equityCurve, int periodsPerYear)` | Rendimento annualizzato composto (CAGR) dai rendimenti periodici dell'equity curve. |
| `m` | `decimal MaxDrawdownPercent(IReadOnlyList&lt;EquityPoint&gt; equityCurve)` | Massimo drawdown (%) dell'equity curve, picco-a-valle. |
| `m` | `decimal CalmarRatio(IReadOnlyList&lt;EquityPoint&gt; equityCurve, int periodsPerYear)` | Calmar ratio: rendimento annualizzato diviso il massimo drawdown (in valore assoluto). Misura il rendimento "per unità" del peggior scenario di perdita subito. |
| `m` | `decimal OmegaRatio(IReadOnlyList&lt;EquityPoint&gt; equityCurve, decimal thresholdPerPeriod = 0m)` | Omega ratio rispetto a una soglia di rendimento periodico (default 0): rapporto fra la somma dei guadagni sopra soglia e la somma delle perdite sotto soglia. Un fattore &gt; 1 indica che la distribuzione dei rendimenti … |
| `m` | `decimal TailRatio(IReadOnlyList&lt;EquityPoint&gt; equityCurve)` | Tail ratio: rapporto fra il 95° e il 5° percentile (in valore assoluto) dei rendimenti periodici. Alto -&gt; le code positive sono più ampie di quelle negative. |
| `m` | `decimal HistoricalVaR(IReadOnlyList&lt;EquityPoint&gt; equityCurve, decimal confidence = 0.95m)` | Value at Risk storico: perdita (valore positivo, frazione di capitale) attesa nel worst-case al livello di confidenza dato, stimata dal percentile empirico dei rendimenti. Es. confidence=0.95 -&gt; VaR = -5° percentile … |
| `m` | `decimal HistoricalCVaR(IReadOnlyList&lt;EquityPoint&gt; equityCurve, decimal confidence = 0.95m)` | Conditional VaR (Expected Shortfall): media dei rendimenti nella coda oltre il VaR, espressa come perdita positiva. Più informativo del VaR perché misura QUANTO si perde in media nello scenario peggiore, non solo la sog… |
| `m` | `int MaxDrawdownDurationPeriods(IReadOnlyList&lt;EquityPoint&gt; equityCurve)` | Durata (in numero di periodi) del più lungo drawdown: dal picco fino al momento in cui l'equity torna a un nuovo massimo storico. Se il drawdown corrente non è ancora recuperato alla fine della serie, conta fino all'ult… |
| `m` | `decimal ExposurePercent(IReadOnlyList&lt;BacktestTrade&gt; trades, IReadOnlyList&lt;EquityPoint&gt; equityCurve)` | Esposizione (%): frazione del tempo totale della curva in cui una posizione era aperta. I trade ancora aperti a fine periodo (ExitTime nullo) contano fino all'ultimo punto della curva. |
| `m` | `decimal HitRate(IReadOnlyList&lt;BacktestTrade&gt; trades)` | Hit-rate (%): percentuale di trade chiusi in profitto. |

### 🧾 `TearsheetMetrics`

> Insieme completo di metriche di performance/rischio per un backtest (controparte di pyfolio).

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal Sharpe` | — |
| `p` | `decimal Sortino` | — |
| `p` | `decimal Calmar` | — |
| `p` | `decimal Omega` | — |
| `p` | `decimal TailRatio` | — |
| `p` | `decimal ValueAtRisk95` | — |
| `p` | `decimal ConditionalValueAtRisk95` | — |
| `p` | `decimal MaxDrawdownPercent` | — |
| `p` | `int MaxDrawdownDurationPeriods` | — |
| `p` | `decimal ExposurePercent` | — |
| `p` | `decimal HitRatePercent` | — |
| `p` | `decimal AnnualizedReturnPercent` | — |

## `ProcioneMGR/Services/Optimization/TradeStatistics.cs`

### 📦 `TradeStatistics`

> Performance report "alla Trombetta" (Strategie di trading con Python, cap. 6-7): metriche calcolate sulla LISTA DEI TRADE e sull'equity monetaria, complementari al che lavora sui rendimenti percentuali dell'equity curve. Include: Profit Factor, Average Trade, Gross Profit/Loss, media e massimo di guadagni e perdite (con data), Reward/Risk Ratio, Average Draw Down (esclusi gli zeri), rapporto AvgDD/MaxDD, ritardi tra massimi consecutivi (max e medio), Kestner Ratio e aggregati annuali/mensili dei profitti.

| | Firma | Descrizione |
|---|---|---|
| `m` | `(decimal MaxDrawdown, decimal AverageDrawdown) DrawdownMoney(IReadOnlyList&lt;EquityPoint&gt; equityCurve)` | Draw down monetario sull'equity curve: massimo (picco-a-valle, valore positivo) e medio calcolato SOLO sui punti in draw down (esclusi gli zeri dei nuovi massimi), come la funzione avgdrawdown_nozero del libro. |
| `m` | `(int MaxDelay, decimal AvgDelay) DelayBetweenPeaks(IReadOnlyList&lt;EquityPoint&gt; equityCurve)` | Ritardi tra massimi consecutivi dell'equity (profilo "orizzontale" del rischio): numero massimo e medio di periodi trascorsi senza segnare un nuovo massimo. Il ritardo in corso a fine serie viene conteggiato. |
| `m` | `decimal KestnerRatio(IReadOnlyList&lt;BacktestTrade&gt; trades)` | Kestner Ratio (versione del libro): regressione lineare sull'equity dei contributi MENSILI aggregati; rapporto tra pendenza della retta ed errore standard dei residui. Misura la regolarita' della curva dei profitti: piu… |
| `m` | `IReadOnlyList&lt;(int Year, decimal Profit)&gt; AnnualProfits(IReadOnlyList&lt;BacktestTrade&gt; trades)` | Profitti aggregati per anno (istogramma annuale del libro). Data = chiusura trade. |
| `m` | `IReadOnlyList&lt;(int Month, decimal AverageProfit)&gt; MonthlyAverageProfits(IReadOnlyList&lt;BacktestTrade&gt; trades)` | Profitto MEDIO per mese di calendario (1-12), aggregando i contributi mensili di tutti gli anni (istogramma del bias mensile del libro). Utile per capire in quali mesi il sistema fatica. Data di riferimento = chiusura d… |
| `m` | `IReadOnlyList&lt;MonthlyProfitCell&gt; MonthlyProfitMatrix(IReadOnlyList&lt;BacktestTrade&gt; trades)` | Matrice anno x mese dei profitti cumulati mensili (base della heatmap del libro). |
| `m` | `decimal Gpdi(IReadOnlyList&lt;decimal&gt; inSamplePnls, IReadOnlyList&lt;decimal&gt; outOfSamplePnls, int step = 5)` | Gandalf Persistence Distribution Index (GPDI, cap. 7): confronto percentile-per-percentile tra due distribuzioni di trade (tipicamente In Sample e Out of Sample). Restituisce la percentuale di livelli percentili in cui … |
| `m` | `decimal Percentile(IReadOnlyList&lt;decimal&gt; sorted, decimal p)` | Percentile con interpolazione lineare su lista ORDINATA, p in [0,1]. |

### 🧾 `MonthlyProfitCell` `(int Year, int Month, decimal Profit);`

> Cella della matrice anno x mese dei profitti.

### 🧾 `TradeReport`

> Performance report basato sui trade (controparte del report del libro, cap. 6).

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal NetProfit` | — |
| `p` | `decimal GrossProfit` | — |
| `p` | `decimal GrossLoss` | — |
| `p` | `decimal ProfitFactor` | — |
| `p` | `int OperationCount` | — |
| `p` | `decimal AverageTrade` | — |
| `p` | `decimal PercentWin` | — |
| `p` | `decimal RewardRiskRatio` | — |
| `p` | `decimal AverageWin` | — |
| `p` | `decimal AverageLoss` | — |
| `p` | `decimal MaxWin` | — |
| `p` | `DateTime? MaxWinDate` | — |
| `p` | `decimal MaxLoss` | — |
| `p` | `DateTime? MaxLossDate` | — |
| `p` | `decimal BudgetEquation` | PercentWin*AvgWin - PercentLoss*\|AvgLoss\|: se &gt; 0 il sistema produce utili al lordo dei costi. |
| `p` | `decimal MaxDrawdownMoney` | — |
| `p` | `decimal AverageDrawdownMoney` | — |
| `p` | `decimal DrawdownRatio` | AvgDD/MaxDD: piu' e' piccolo, piu' il MaxDD e' stato un'anomalia isolata. |
| `p` | `int MaxDelayBetweenPeaks` | — |
| `p` | `decimal AvgDelayBetweenPeaks` | — |
| `p` | `decimal KestnerRatio` | — |
| `p` | `IReadOnlyList&lt;(int Year, decimal Profit)&gt; AnnualProfits` | — |
| `p` | `IReadOnlyList&lt;(int Month, decimal AverageProfit)&gt; MonthlyAverageProfits` | — |
| `p` | `IReadOnlyList&lt;MonthlyProfitCell&gt; MonthlyProfitMatrix` | — |

# `Services/Discovery/`

## `ProcioneMGR/Services/Discovery/Dtw/DtwMatcher.cs`

### 🧾 `DtwMatch` `(`

> Un'occorrenza del pattern nella serie.

### 📦 `DtwConfig`

> Parametri della ricerca.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int BandPercent` | Ampiezza della banda di Sakoe-Chiba, in % della lunghezza del pattern. |
| `p` | `double MaxDistance` | Distanza massima perché una finestra sia considerata un'occorrenza. È su scala z-normalizzata: valori tipici 0,5–3 a seconda di quanto si vuole essere selettivi. |
| `p` | `int MaxMatches` | Massimo numero di occorrenze restituite (le migliori per distanza). |
| `p` | `int MinSeparationBars` | Separazione minima fra due occorrenze, in barre. Zero = usa la lunghezza del pattern, che è il default sensato: due occorrenze che si sovrappongono sono la stessa occorrenza. |

### 🔌 `IDtwMatcher`

> Ricerca di pattern per forma via Dynamic Time Warping. Puro e deterministico.

| | Firma | Descrizione |
|---|---|---|
| `m` | `double Distance(IReadOnlyList&lt;double&gt; a, IReadOnlyList&lt;double&gt; b, int band)` | Distanza DTW fra due sequenze già z-normalizzate, con banda. |
| `m` | `double LowerBound(IReadOnlyList&lt;double&gt; query, IReadOnlyList&lt;double&gt; candidate, int band)` | Limite INFERIORE alla distanza DTW (LB_Keogh): mai maggiore di . |
| `m` | `IReadOnlyList&lt;double&gt; ZNormalize(IReadOnlyList&lt;double&gt; values)` | Z-normalizza una sequenza (media 0, deviazione 1). Serie costante ⇒ tutti zeri. |
| `m` | `IReadOnlyList&lt;bool&gt; ToEventSeries(int seriesLength, IReadOnlyList&lt;DtwMatch&gt; matches)` | Serie booleana allineata alle candele: true sulla barra in cui un'occorrenza si CHIUDE. È la forma in cui il pattern entra nel motore Discovery come trigger evento — mai come strategia a sé. |

### 📦 `DtwMatcher` `: IDtwMatcher`

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;double&gt; ZNormalize(IReadOnlyList&lt;double&gt; values)` | — |
| `m` | `double Distance(IReadOnlyList&lt;double&gt; a, IReadOnlyList&lt;double&gt; b, int band)` | — |
| `m` | `double LowerBound(IReadOnlyList&lt;double&gt; query, IReadOnlyList&lt;double&gt; candidate, int band)` | — |
| `m` | `IReadOnlyList&lt;bool&gt; ToEventSeries(int seriesLength, IReadOnlyList&lt;DtwMatch&gt; matches)` | — |

## `ProcioneMGR/Services/Discovery/Dtw/DtwPatternAnalysisService.cs`

### 🧾 `DtwPatternAnalysis` `(`

> Esito dell'analisi di un pattern: occorrenze, event-study, nullo per forma, verdetto.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsPredictive` | Il pattern anticipa un movimento che pattern QUALUNQUE, cercati allo stesso modo, non producono. Il giudice è il nullo per forma, non il placebo a date casuali: vedi la nota in testa a . |

### 🧾 `ShapeMatchedNull` `(`

> Distribuzione del rendimento anormale post-evento ottenuta ripetendo l'INTERA procedura con pattern casuali: è il nullo corretto per eventi selezionati per forma.

### 🔌 `IDtwPatternAnalysisService`

> Misura il valore predittivo di un pattern trovato per forma.

### 📦 `DtwPatternAnalysisService` `(IDtwMatcher matcher) : IDtwPatternAnalysisService`

| | Firma | Descrizione |
|---|---|---|
| `k` | `double MinEconomicEffect` | PAVIMENTO DI RILEVANZA ECONOMICA: sotto questo rendimento anormale cumulato un effetto non è operabile, per quanto significativo risulti. Nasce da un test fallito: con il solo p- |

## `ProcioneMGR/Services/Discovery/IStrategyDiscovery.cs`

### 🔌 `IStrategyDiscovery`

## `ProcioneMGR/Services/Discovery/StrategyComposer.cs`

### 📦 `ComposedCandidate`

> A generated strategy spec: a concrete, ready-to-backtest parameterization.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string StrategyName` | — |
| `p` | `Dictionary&lt;string, decimal&gt; Parameters` | — |
| `p` | `string Key` | Canonical identity key (dedupe + traceability in logs/audit). |
| `p` | `string Description` | Human-readable description ("RSI 70 → Long"). |

### 📦 `ComposerConfiguration`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int MaxCandidates` | — |
| `p` | `int Seed` | — |
| `p` | `bool EnableComposite` | — |
| `p` | `bool EnableEvent` | — |
| `p` | `bool EnableRegime` | — |
| `p` | `List&lt;int&gt; SignalPool` | Signal ids allowed in composite specs (empty = the whole catalog). |

### 📦 `ComposerScreeningConfiguration`

> Screening + fixed-parameter walk-forward settings (mirrors the hunt gates).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ExchangeName` | — |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `DateTime From` | — |
| `p` | `DateTime To` | — |
| `p` | `decimal InitialCapital` | — |
| `p` | `decimal SlippagePercent` | — |
| `p` | `decimal FeePercent` | Commissione per lato (%) — allineata ai default di PipelineCosts (Bitget, conservativa). |
| `p` | `decimal FundingRatePercentPer8h` | Funding dei perpetual (%/8h) — allineato ai default di PipelineCosts; era assente (0). |
| `p` | `decimal MinScreenSharpe` | Selection-range gates before the walk-forward confirmation. |
| `p` | `int MinTrades` | — |
| `p` | `int ConfirmTopN` | How many screened specs per series get the walk-forward confirmation. |
| `p` | `int OosWindowMonths` | Fixed-parameter walk-forward: evaluate on rolling OOS windows of this many months. |
| `p` | `decimal MinOosSharpe` | — |

### 🔌 `ICompositeSignalGenerator`

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;ComposedCandidate&gt; Generate(ComposerConfiguration config, int quota)` | — |

### 🔌 `IEventTriggerGenerator`

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;ComposedCandidate&gt; Generate(ComposerConfiguration config, int quota)` | — |

### 🔌 `IRegimeMapGenerator`

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;ComposedCandidate&gt; Generate(ComposerConfiguration config, int quota)` | — |

### 🔌 `IStrategyComposer`

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;ComposedCandidate&gt; Compose(ComposerConfiguration config)` | Generates candidate specs (deterministic per seed, deduped, plausibility-filtered). |

### 📦 `CompositeSignalGenerator` `: ICompositeSignalGenerator`

> Systematic composition of 2-3 elementary conditions into entry rules. Deterministic: enumerates the full plausible space in a fixed order, then takes a seeded sample. Plausibility: per-signal (operator, threshold) menus only contain semantically sensible combos (e.g. Supertrend direction is only "&gt;50" or "&lt;50"); contradictions are impossible by construction (distinct signals per spec). Diversity: coarse 15-point threshold steps + canonical-key dedupe.

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;ComposedCandidate&gt; Generate(ComposerConfiguration config, int quota)` | — |
| `m` | `string Canonical(Dictionary&lt;string, decimal&gt; parameters)` | — |
| `m` | `List&lt;ComposedCandidate&gt; SeededSample(List&lt;ComposedCandidate&gt; all, int quota, int seed)` | — |

### 📦 `EventTriggerGenerator` `: IEventTriggerGenerator`

> Enumerates the discrete-event trigger space (event × direction × threshold × holding time).

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;ComposedCandidate&gt; Generate(ComposerConfiguration config, int quota)` | — |

### 📦 `RegimeMapGenerator` `: IRegimeMapGenerator`

> Enumerates regime→strategy assignments using the platform's known family bias (trend-followers in trends, mean-reverters sideways, optional stand-aside).

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;ComposedCandidate&gt; Generate(ComposerConfiguration config, int quota)` | — |

### 📦 `StrategyComposer` `(`

> Creative-discovery orchestrator: generates candidate specs from the three archetype generators (deterministic per seed), then evaluates them with the SAME honesty rules of the classic hunt: full selection-range screen (Sharpe + trade-count gates) → fixed-parameter walk-forward on rolling OOS windows for the top few → DiscoveryCandidate output for the standard holdout gauntlet. Registered SCOPED (declared deviation from the "Singleton" in the spec: it depends on IBacktestEngine, which is scoped).

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;ComposedCandidate&gt; Compose(ComposerConfiguration config)` | — |
| `m` | `List&lt;(DateTime From, DateTime To)&gt; BuildOosWindows(DateTime from, DateTime to, int windowMonths)` | Rolling, non-overlapping OOS windows covering [from, to]. Public for direct testability. |

## `ProcioneMGR/Services/Discovery/StrategyDiscoveryEngine.cs`

### 📦 `StrategyDiscoveryEngine` `(`

> Motore di ricerca strategie. Per ogni combinazione (strategia × coppia × timeframe) lancia un'ottimizzazione walk-forward e ne estrae la migliore configurazione di parametri (per Sharpe out-of-sample medio = robusta, non overfittata). Ordina tutte le candidate per Sharpe OOS: in cima ci sono le strategie più proficue e affidabili.

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;ParameterRange&gt; DefaultRanges(string strategyName)` | Griglie di parametri di default per strategia (modeste, per spazzare in fretta). |

## `ProcioneMGR/Services/Discovery/StrategyDiscoveryModels.cs`

### 📦 `StrategyDiscoveryConfiguration`

> Configurazione della ricerca di strategie: spazza un universo di (strategia × coppia × timeframe) e, per ciascuna, ottimizza i parametri in walk-forward.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ExchangeName` | — |
| `p` | `List&lt;string&gt; Symbols` | — |
| `p` | `List&lt;string&gt; Timeframes` | — |
| `p` | `List&lt;string&gt; Strategies` | Nomi strategia da provare (vuoto = tutte quelle disponibili). |
| `p` | `DateTime From` | — |
| `p` | `DateTime To` | — |
| `p` | `decimal InitialCapital` | — |
| `p` | `decimal CommissionPercent` | — |
| `p` | `decimal SlippagePercent` | [R2] Attrito per fill propagato all'ottimizzatore. Vedi per il motivo per cui il default è onesto e non zero. |
| `p` | `WalkForwardConfiguration WalkForward` | — |
| `p` | `int TopN` | Quante candidate restituire (ordinate per Sharpe out-of-sample). |

### 📦 `DiscoveryCandidate`

> Una candidata: la migliore combinazione di parametri per una (strategia, coppia, timeframe).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string StrategyName` | — |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `Dictionary&lt;string, decimal&gt; Parameters` | — |
| `p` | `decimal OutOfSampleSharpe` | — |
| `p` | `decimal InSampleSharpe` | — |
| `p` | `decimal TotalReturn` | — |
| `p` | `decimal MaxDrawdown` | — |
| `p` | `int TotalTrades` | — |
| `p` | `int Windows` | — |
| `p` | `Validation.SelectionValidation? Validation` | Verdetto anti-overfitting (Fase 1) ereditato dallo sweep di ottimizzazione della candidata: Deflated Sharpe che corregge lo Sharpe OOS per il numero di combinazioni provate. null se non calcolabile. Permette di ordinare… |

### 📦 `StrategyDiscoveryResult`

| | Firma | Descrizione |
|---|---|---|
| `p` | `List&lt;DiscoveryCandidate&gt; Candidates` | Candidate ordinate per Sharpe out-of-sample decrescente (le più "proficue e robuste"). |
| `p` | `int JobsRun` | — |
| `p` | `int CombinationsTested` | — |
| `p` | `TimeSpan ExecutionTime` | — |

### 📦 `DiscoveryProgress`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Completed` | — |
| `p` | `int Total` | — |
| `p` | `string Message` | — |
| `p` | `decimal BestSharpeSoFar` | — |

# `Services/Microstructure/`

## `ProcioneMGR/Services/Microstructure/BinanceDumpDownloader.cs`

### 🔢 `DumpMarket`

> Mercato del dump: i due domini hanno formati leggermente diversi (vedi BinanceDumpParser).

### 📦 `BinanceDumpDownloader` `(HttpClient http, string? cacheDirectory = null)`

> Scarica (e mette in cache) i dump giornalieri di Binance.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string CacheDirectory` | Cartella di cache. Fuori dal repo per costruzione (default: temp di sistema). |
| `p` | `long DownloadedBytes` | Byte scaricati dalla rete in questa sessione (la cache non conta). |
| `p` | `int CacheHits` | File serviti dalla cache senza toccare la rete. |
| `p` | `int MissingDays` | Giorni assenti a monte (404): non sono errori, sono buchi da dichiarare. |
| `m` | `string AggTradesUrl(DumpMarket market, string symbol, DateOnly day)` | URL del dump dei trade aggregati di un giorno. |
| `m` | `string KlinesUrl(DumpMarket market, string symbol, string timeframe, DateOnly day)` | URL del dump delle klines di un giorno. |
| `m` | `string BookDepthUrl(string symbol, DateOnly day)` | URL del dump della profondità del book di un giorno. Esiste SOLO sui futures USD-M: sullo spot Binance non pubblica alcun dato di book, ed è la ragione per cui l'OFI top-of-book vero non è misurabile storicamente. |
| `m` | `Task&lt;string?&gt; EnsureAsync(string url, CancellationToken ct = default)` | Garantisce lo zip in cache e ne restituisce il percorso; null se il giorno non esiste a monte (404). |
| `m` | `StreamReader OpenCsv(string zipPath)` | Apre il primo (e unico) CSV dentro lo zip, in streaming. |
| `m` | `Task&lt;bool&gt; MatchesChecksumAsync(string path, string expectedSha256, CancellationToken ct = default)` | — |

### 📦 `ZipEntryStream` `(ZipArchive archive, Stream inner) : Stream`

> Tiene in vita l'archivio finché lo stream dell'entry è aperto.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool CanRead` | — |
| `p` | `bool CanSeek` | — |
| `p` | `bool CanWrite` | — |
| `p` | `long Length` | — |
| `p` | `long Position` | — |
| `m` | `void Flush()` | — |
| `m` | `int Read(byte[] buffer, int offset, int count)` | — |
| `m` | `long Seek(long offset, SeekOrigin origin)` | — |
| `m` | `void SetLength(long value)` | — |
| `m` | `void Write(byte[] buffer, int offset, int count)` | — |
| `m` | `void Dispose(bool disposing)` | — |

## `ProcioneMGR/Services/Microstructure/BinanceDumpParser.cs`

### 📦 `BinanceDumpParser`

> Legge i CSV dei dump Binance. Istanza per file (tiene i contatori di quel file).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int MalformedLines` | Righe che non è stato possibile interpretare (colonne mancanti, numeri non validi). |
| `p` | `int ParsedLines` | Righe interpretate correttamente. |
| `m` | `DateTime FromEpoch(long value)` | Converte un epoch in ms o µs in UTC, decidendo l'unità dall'ordine di grandezza. |
| `m` | `IEnumerable&lt;AggTrade&gt; ReadAggTrades(TextReader reader)` | Trade aggregati, in streaming. L'ordine è quello del file (cronologico crescente): non si riordina, così un file fuori ordine si vede a valle invece di essere mascherato qui. |
| `m` | `IEnumerable&lt;BookDepthSnapshot&gt; ReadBookDepth(TextReader reader)` | Snapshot di profondità. Le righe dello stesso istante vanno raggruppate: il file ha una riga per banda, quindi uno snapshot completo sono 12 righe consecutive con lo stesso timestamp. |
| `m` | `IEnumerable&lt;OhlcvData&gt; ReadKlines(TextReader reader, string symbol, string timeframe)` | Klines dei dump, lette direttamente in — la stessa entità delle candele della piattaforma. Non è un dettaglio di comodità: significa che il proxy si calcola col TakerImbalanceFactor VERO, quello che gira in produzione, … |

## `ProcioneMGR/Services/Microstructure/IncrementalIcGate.cs`

### 🧾 `IcCandidate` `(string Name, IReadOnlyList&lt;double&gt; Values);`

> Un candidato da confrontare col proxy: valori allineati alle barre, NaN dove manca.

### 📦 `IncrementalIcConfig`

> Parametri del gate. I default sono quelli già in uso altrove nella piattaforma.

| | Firma | Descrizione |
|---|---|---|
| `p` | `double MinAbsIc` | \|IC\| minimo per contare come informazione economicamente rilevante (0,02 = soglia storica di /feature-selection). |
| `p` | `double NoiseFloorZ` | Errori standard richiesti perché un IC sia distinguibile da zero. |
| `p` | `int NullDraws` | Giri del nullo a rotazione (200 = come il giudice del gemello nullo). |
| `p` | `double NullPercentile` | Percentile del nullo, tenuto per il REPORT (la decisione passa dal p- |
| `p` | `double MaxNullPValue` | p- |
| `p` | `int Seed` | Seme del generatore: la misura deve essere riproducibile bit per bit. |
| `p` | `int MinObservations` | Minimo di osservazioni sotto cui non si emette verdetto. |
| `p` | `double? RoundTripCostBps` | Costo di un giro completo (entrata + uscita) in punti base. Attiva il SECONDO LIVELLO del verdetto: non «questo candidato informa?» ma «informa abbastanza da pagarsi il giro?». PERCHÉ È NEL GATE E NON NEL CHIAMANTE. Nel… |

### 🧾 `IncrementalIcOutcome` `(`

> Esito per un candidato su un orizzonte.

| | Firma | Descrizione |
|---|---|---|
| `p` | `double ForwardSigmaBps` | Deviazione standard dei rendimenti a questo orizzonte, in punti base (0 se non calcolabile). |
| `p` | `double GrossEdgeBps` | Edge lordo atteso per un segnale a 1σ, in punti base: \|IC parziale\| × σ. |
| `p` | `double IcRequiredByCosts` | \|IC\| che servirebbe perché l'edge lordo pareggi il costo del giro (0 = livello economico spento). |
| `p` | `bool IsTradable` | Vero solo se il candidato informa **e** l'edge lordo copre il costo del giro. È il secondo livello: senza, un "AGGIUNGE" statistico si legge come "si può operare". |

### 🧾 `IncrementalIcReport` `(`

> Verdetto complessivo del gate.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool AnyTradable` | Vero se almeno un candidato supera anche il livello economico. |

### 📦 `IncrementalIcGate`

> Misura l'informazione INCREMENTALE di uno o più candidati sopra un proxy già disponibile.

| | Firma | Descrizione |
|---|---|---|
| `m` | `double PartialSpearman(IReadOnlyList&lt;double&gt; x, IReadOnlyList&lt;double&gt; y, IReadOnlyList&lt;double&gt; z)` | Correlazione parziale di Spearman fra e tenuto conto di : ρ(x,y\|z) = (ρxy − ρxz·ρyz) / √((1 − ρxz²)(1 − ρyz²)) Sui ranghi, come tutto l'IC della piattaforma. Restituisce 0 se il denominatore degenera (candidato identic… |

## `ProcioneMGR/Services/Microstructure/MicrostructureModels.cs`

### 🧾 `AggTrade` `(`

> Un trade aggregato del tape (una riga di aggTrades ): tutti gli scambi consecutivi allo stesso prezzo, dallo stesso lato, in un colpo solo. è la convenzione di Binance e va letta al contrario di come suona: se il COMPRATORE era il maker, allora l'aggressore — chi ha attraversato lo spread — era il venditore. Sbagliare questo segno capovolgerebbe l'intero order flow, ed è il primo errore che i test verificano.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsTakerBuy` | Vero se l'aggressore era in acquisto (taker buy): è il volume "informato" della letteratura. |

### 🧾 `TapeBar` `(`

> Barra del tape aggregata su un intervallo fisso (nel pilota: 10 secondi). È l'unità che il piano C5 §9.2 aveva scelto proprio per non archiviare tick: l'informazione che serve a un segnale di order flow (volume ai due lati, conteggio, prezzo) sopravvive all'aggregazione.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal Volume` | — |
| `p` | `decimal? Imbalance` | Sbilanciamento in [-1, +1]: (buy − sell) / (buy + sell). null su una barra senza scambi — uno zero finto sarebbe un "equilibrio perfetto" mai osservato, e la piattaforma ha già la regola di non inventare valori dove il … |
| `p` | `bool IsEmpty` | — |

### 🧾 `BestQuote` `(`

> Miglior bid/ask con le rispettive quantità: l'input dell'OFI VERO. Non è ricostruibile dai dump storici (i file bookTicker non esistono su data.binance.vision), ma è esattamente ciò che il feed R1 riceve già oggi da {sym}@bookTicker e che BinanceStreamMapper scarta: se il pilota andrà accesso dal vivo, la formula che lo consuma è già qui e già verificata.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal Mid` | — |
| `p` | `decimal? QueueImbalance` | Sbilanciamento statico del top-of-book: (qB − qA)/(qB + qA), in [-1, +1]. |

### 🧾 `BookDepthSnapshot` `(`

> Fotografia della PROFONDITÀ a bande percentuali dal mid, che è la forma in cui il book esiste storicamente (dump bookDepth : uno snapshot ogni 30 secondi, bande ±0,20% e ±1…5%). Non è il top-of-book: è la liquidità cumulata entro una distanza dal mid. La banda più fine (±0,20%) è la più vicina alla domanda "com'è messo il book adesso" e quella su cui si misura. La differenza col top-of-book va dichiarata, non nascosta: vedi la nota in .

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal? BidNotional(decimal band)` | Notional disponibile sul lato BID entro % dal mid (banda negativa nel file). |
| `m` | `decimal? AskNotional(decimal band)` | Notional disponibile sul lato ASK entro % dal mid. |
| `m` | `decimal? Imbalance(decimal band)` | Sbilanciamento di profondità nella banda: (bid − ask)/(bid + ask), in [-1, +1]. |

## `ProcioneMGR/Services/Microstructure/OrderFlowImbalance.cs`

### 📦 `OrderFlowImbalance`

> Order flow imbalance: la formula di Cont-Kukanov-Stoikov e la sua variante su bande di profondità.

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal TopOfBookOfi(BestQuote previous, BestQuote current)` | OFI di UN evento di book (Cont-Kukanov-Stoikov 2014, eq. 2): e = 1{Pᵇₙ ≥ Pᵇₙ₋₁}·qᵇₙ − 1{Pᵇₙ ≤ Pᵇₙ₋₁}·qᵇₙ₋₁ − 1{Pᵃₙ ≤ Pᵃₙ₋₁}·qᵃₙ + 1{Pᵃₙ ≥ Pᵃₙ₋₁}·qᵃₙ₋₁ Letta per casi, è esattamente la pressione netta al top-of-book: bid… |
| `m` | `decimal TopOfBookOfi(IReadOnlyList&lt;BestQuote&gt; quotes)` | OFI accumulato su una sequenza di quote: la somma degli eventi, che è la definizione della variabile con cui CKS regredisce le variazioni di prezzo. Con meno di due quote non esiste alcun evento, quindi il risultato è z… |
| `m` | `decimal? DepthBandOfi(BookDepthSnapshot previous, BookDepthSnapshot current, decimal band)` | OFI sulle bande di profondità fra due snapshot: (Δnotional bid − Δnotional ask) , normalizzato sul notional medio totale della banda perché il numero sia confrontabile fra simboli e nel tempo (senza normalizzare, BTC do… |

## `ProcioneMGR/Services/Microstructure/TapeAggregator.cs`

### 📦 `TapeAggregator`

> Aggrega i trade del tape in barre di durata fissa su una griglia allineata all'epoch.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;IReadOnlyList&lt;TapeBar&gt;&gt; GroupBy(IReadOnlyList&lt;TapeBar&gt; fine, TimeSpan coarse)` | Raggruppa barre fini in barre più larghe (es. sei barre da 10s in una da 1 minuto). Serve al confronto del gate: il proxy vive sulla barra larga (una candela), i candidati fini dentro. Il fattore fra le due durate deve … |
| `m` | `decimal? Imbalance(IReadOnlyList&lt;TapeBar&gt; bars)` | Sbilanciamento aggregato di un gruppo di barre: (buy − sell)/(buy + sell), null se non c'è volume. |

# `Services/Analysis/`

## `ProcioneMGR/Services/Analysis/CandlestickPatternDetector.cs`

### 🔢 `CandlePatternType`

> Tipi di pattern candlestick riconosciuti (McAllen, "Charting and Technical Analysis", cap. 4-6 e 14).

### 🧾 `CandlePattern` `(`

> Pattern rilevato su una barra (l'indice e' quello della barra che COMPLETA il pattern).

### 📦 `CandlestickPatternDetector`

> Riconoscimento dei pattern candlestick (McAllen, cap. 4-6) + Key Reversal Day (cap. 14). Principio del libro: un pattern di inversione ha valore SOLO dopo un trend ("un minimo di cinque giorni di avanzata o declino") — i pattern direzionali vengono quindi emessi solo se il contesto (variazione netta sulle N barre precedenti) e' coerente. Doji e spinning top isolati sono emessi come neutri: sta al chiamante pesarli col contesto.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal DojiBodyMaxPercent` | Corpo massimo (in % del range) perche' una candela sia un doji. |
| `p` | `decimal SpinningTopBodyMaxPercent` | Corpo massimo (in % del range) per lo spinning top. |
| `p` | `int TrendLookback` | Barre di contesto per definire il trend precedente (il libro suggerisce &gt;= 5). |
| `p` | `decimal TrendMinMovePercent` | Variazione % minima sulle barre di lookback perche' ci sia un trend. |
| `p` | `int KeyReversalLookback` | Finestra del massimo/minimo per il Key Reversal Day. |
| `p` | `decimal VolumeConfirmFactor` | Fattore sopra la media del volume per considerare una barra "confermata". |
| `m` | `IReadOnlyList&lt;CandlePattern&gt; Detect(IReadOnlyList&lt;OhlcvData&gt; candles)` | — |

## `ProcioneMGR/Services/Analysis/ChartPatternDetector.cs`

### 🔢 `ChartPatternType`

> Tipi di pattern grafici di inversione (McAllen, cap. 9-10).

### 🧾 `ChartPatternMatch` `(`

> Pattern grafico individuato dai pivot. Confirmed = il prezzo ha completato il pattern chiudendo oltre la neckline (senza conferma e' solo un'ipotesi, come da libro).

### 📦 `ChartPatternDetector`

> Riconoscimento dei pattern grafici di inversione dai punti di swing (McAllen cap. 9-10): Double Top/Bottom (due picchi/valli allo stesso livello, conferma sotto/sopra il trough centrale) e Head & Shoulders dritto/inverso (tre picchi con il centrale piu' estremo, conferma alla violazione della neckline). La conferma volumetrica va verificata a parte con (breakout a basso volume = sospetto).

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal PeakTolerancePercent` | Tolleranza % tra i due picchi (o le due spalle) perche' siano "allo stesso livello". |
| `p` | `decimal MinDepthPercent` | Profondita' minima del trough centrale in % (evita pattern piatti insignificanti). |
| `c` | `ChartPatternDetector(int pivotWindow = 3)` | — |
| `m` | `IReadOnlyList&lt;ChartPatternMatch&gt; Detect(IReadOnlyList&lt;OhlcvData&gt; candles)` | — |

## `ProcioneMGR/Services/Analysis/CyclicalAnalyzer.cs`

### 📦 `CyclicalAnalyzer`

> Elementi di analisi ciclica (Trombetta, cap. 5): Activity Factor (volumi medi per ora), bias orario dei prezzi (body medio per ora + robustezza statistica), bias sul giorno della settimana (contributo intraday e overnight) e stagionalita' per giorno dell'anno. Metodo del libro: ripetere ogni analisi su piu' periodi (lungo/medio/breve) e dare piu' peso ai periodi lunghi (ComboStats con pesi 3-2-1); un bias e' affidabile solo se il valor medio e' confermato dalla percentuale di occorrenze concordi (percPosNeg &gt; 50%). Qui ogni metodo lavora su UNA serie: il chiamante la ripete su slice temporali diverse e combina i risultati con .

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;HourlyActivity&gt; ActivityFactor(IReadOnlyList&lt;OhlcvData&gt; candles)` | Activity Factor: volume medio scambiato per ciascuna ora del giorno (0-23, UTC). NormalizedMax divide per il massimo della serie (max = 1) per confronti tra strumenti o periodi con volumi di ordini di grandezza diversi. |
| `m` | `IReadOnlyList&lt;HourlyBias&gt; HourlyPriceBias(IReadOnlyList&lt;OhlcvData&gt; candles)` | Bias orario dei prezzi: body medio (close-open) per ciascuna ora del giorno, con la percentuale di occorrenze concordi col segno della media (percPosNeg del libro) e la versione normalizzata max/min (+1 = miglior ora, -… |
| `m` | `IReadOnlyList&lt;DayOfWeekBias&gt; DayOfWeekBias(IReadOnlyList&lt;OhlcvData&gt; candles)` | Bias sul giorno della settimana su serie DAILY: contributo medio intraday ((close-open)/open) e overnight ((open-close[-1])/close[-1]), ciascuno con la percentuale di occorrenze concordi col segno della media. |
| `m` | `IReadOnlyList&lt;SeasonalityPoint&gt; Seasonality(IReadOnlyList&lt;OhlcvData&gt; candles)` | Stagionalita' per giorno dell'anno su serie DAILY: variazione % media close-su-close per ciascun giorno (1..366) e curva cumulata (la "equity" della stagionalita'). |

### 🧾 `HourlyActivity` `(int Hour, int Samples, decimal AverageVolume, decimal NormalizedMax);`

> Volume medio per ora del giorno (Activity Factor).

### 🧾 `HourlyBias` `(int Hour, int Samples, decimal AverageBody, decimal Normalized, decimal ConcordantPercen…`

> Bias orario: body medio, versione normalizzata [-1,1] e % di occorrenze concordi.

### 🧾 `HourlyComboStat` `(int Hour, decimal WeightedConcordantPercent);`

> Robustezza combinata multi-periodo del bias orario (ComboStats).

### 🧾 `DayOfWeekBias` `(`

> Bias del giorno della settimana: contributi intraday e overnight con concordanza.

### 🧾 `SeasonalityPoint` `(int DayOfYear, int Samples, decimal AvgChangePercent, decimal CumulativePercent);`

> Punto della curva di stagionalita' (giorno dell'anno 1..366).

### 🧾 `SeasonalYearOutcome` `(int Year, decimal ChangePercent, bool IsSuccess);`

> Esito della finestra stagionale per un singolo anno.

### 🧾 `SeasonalWindowResult`

> Esito complessivo del test di una finestra stagionale.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;SeasonalYearOutcome&gt; Years` | — |
| `p` | `int YearsTested` | — |
| `p` | `decimal SuccessPercent` | — |
| `p` | `decimal AverageChangePercent` | — |

## `ProcioneMGR/Services/Analysis/EventStudy.cs`

### 🧾 `EventStudyConfig` `(`

> Parametri dell'event-study. Le finestre sono in BARRE della serie passata (1h ⇒ ore, 1d ⇒ giorni). Finestra di stima della baseline (rendimento medio "normale"), PRIMA del gap. Cuscinetto fra stima e finestra evento: l'anticipazione non contamina la baseline. Barre PRIMA dell'evento incluse nello studio: misura anticipazione/leakage. Barre dall'evento in poi (la barra 0 è la prima all'evento o dopo). Insiemi di pseudo-eventi a date CASUALI (stessa numerosità) per il p-

### 🧾 `EventStudyResult` `(`

> Esito: AAR/CAAR per offset (da −Pre a +Post), la CAAR pre-evento (anticipazione), la CAAR post-evento con t cross-evento, e il p-

| | Firma | Descrizione |
|---|---|---|
| `m` | `int OffsetAt(int index)` | Offset (in barre dall'evento) dell'elemento di Aar/Caar. |
| `m` | `bool IsSignificant(double alpha = 0.05)` | — |

### 📦 `EventStudy`

> [T2.7 roadmap macchina-ricerca] Event-study RIGOROSO, in contrasto con le medie post-evento semplici di NewsImpactAnalyzer : 1. Abnormal return : ogni rendimento della finestra evento è confrontato con la baseline del titolo stimata su una finestra precedente separata da un gap — "dopo l'evento è salito" non significa niente se saliva comunque; 2. Finestra pre-evento : una CAAR già positiva PRIMA dell'evento segnala anticipazione o leakage del timestamp (notizie retrodatate, calendario noto in anticipo); 3. Placebo temporale : la stessa statistica su insiemi di date casuali (lezione T1.5: la randomizzazione onesta è lungo il tempo). Se le date a caso "reagiscono" quanto le vere, l'effetto è rumore. Puro e deterministico a parità di seme. Richiede candele ordinate cronologicamente.

## `ProcioneMGR/Services/Analysis/ExcursionAnalyzer.cs`

### 📦 `ExcursionAnalyzer`

> Analisi delle escursioni di barra per il posizionamento probabilistico dello stop loss (Trombetta, cap. 4, "Probabilita' e direzione") + effetto memoria (autocorrelazione ritardata delle variazioni percentuali). Idea: nelle giornate che chiudono positive, la massima ricorrezione open-&gt;low e' contenuta; il 95esimo/99esimo percentile di quella distribuzione e' un livello di stop oltre il quale la probabilita' che la barra si chiuda comunque positiva crolla. Simmetrico per lo short con l'escursione open-&gt;high delle giornate negative.

| | Firma | Descrizione |
|---|---|---|
| `m` | `StopLossSuggestion SuggestStopLoss(IReadOnlyList&lt;OhlcvData&gt; candles)` | Percentili delle escursioni avverse, per stop loss di posizioni aperte sull'open di barra. Le distanze sono espresse in % dell'open (adimensionali, confrontabili tra strumenti). |
| `m` | `TakeProfitSuggestion SuggestTakeProfit(IReadOnlyList&lt;OhlcvData&gt; candles)` | Percentili delle escursioni FAVOREVOLI, per il take profit — speculare a . Long: escursione open-&gt;high delle sole barre POSITIVE (quanto corrono i vincitori verso l'alto prima di chiudere); Short: open-&gt;low delle … |
| `m` | `RiskBracket SuggestBracket(IReadOnlyList&lt;OhlcvData&gt; candles, OrderSide side, bool use99thPercentile = false)` | Bracket SL+TP pronto da applicare per un dato lato, calcolato dai percentili di escursione (default 95°). È il "calcolo automatico" usato dalla pipeline e dal backtest: distanze in % dal prezzo d'ingresso. Ritorna 0 dov… |

### ▫️ `HorizonSample` `(decimal Adverse, decimal Favorable, decimal EntryAtrPercent, bool FavorableOutcome);`

> MAE/MFE (in % dall'ingresso) ed esito di ogni ingresso ipotetico tenuto per horizon barre.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;LagCorrelation&gt; LaggedAutocorrelation(IReadOnlyList&lt;decimal&gt; values, int maxLag = 10)` | Autocorrelazione ritardata ("effetto memoria", cap. 4): correlazione di Pearson tra la serie delle variazioni percentuali e le sue copie ritardate di 1..maxLag periodi. Correlazioni "deboli" (10-30%) su lag brevi sono g… |
| `m` | `ContinuationStats ContinuationProbability(IReadOnlyList&lt;decimal&gt; values, decimal thresholdPercent = 0m)` | Probabilita' di continuazione: quante volte, dopo una variazione positiva della serie (superiore a ), la variazione successiva e' ancora positiva. E' il "test di consistenza" che il libro applica ai massimi di barra. |

### 🧾 `StopLossSuggestion`

> Livelli di stop loss suggeriti dalle distribuzioni delle escursioni avverse (% dell'open).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int PositiveBars` | — |
| `p` | `int NegativeBars` | — |
| `p` | `decimal LongStopPercentile95` | Distanza % sotto l'open che contiene il 95% delle ricorrezioni delle barre positive. |
| `p` | `decimal LongStopPercentile99` | — |
| `p` | `decimal ShortStopPercentile95` | Distanza % sopra l'open che contiene il 95% delle escursioni delle barre negative. |
| `p` | `decimal ShortStopPercentile99` | — |

### 🧾 `TakeProfitSuggestion`

> Livelli di take profit suggeriti dalle distribuzioni delle escursioni favorevoli (% dell'open).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int PositiveBars` | — |
| `p` | `int NegativeBars` | — |
| `p` | `decimal LongTakeProfitPercentile95` | Distanza % sopra l'entry che cattura il 95% delle escursioni favorevoli delle barre positive (long). |
| `p` | `decimal LongTakeProfitPercentile99` | — |
| `p` | `decimal ShortTakeProfitPercentile95` | Distanza % sotto l'entry che cattura il 95% delle escursioni favorevoli delle barre negative (short). |
| `p` | `decimal ShortTakeProfitPercentile99` | — |

### 🧾 `RiskBracket` `(decimal StopLossPercent, decimal TakeProfitPercent);`

> Bracket protettivo pronto da applicare: distanze % dall'entry per stop loss e take profit (0 = non disponibile).

### 🔢 `VolatilityRegime` `{ Low, Normal, High }`

> Regime di volatilità all'ingresso, per terziali dell'ATR% causale (R1.5).

### 🧾 `HorizonExcursion` `(int Horizon, int Samples, decimal StopPercentile, decimal TakeProfitPercentile, decimal …`

> Escursioni MAE/MFE su un orizzonte di detenzione (R1.5): SL/TP come percentile della massima escursione avversa/favorevole dei trade vincenti. Distanze in % dal prezzo d'ingresso.

### 🧾 `RegimeConditionedBracket` `(`

> Bracket MAE/MFE disaggregato per regime di volatilità più il complessivo, con il regime corrente (ultima candela) per l'uso adattivo. contiene sempre le tre chiavi Low/Normal/High (Samples=0 dove non ci sono abbastanza trade).

### 🧾 `LagCorrelation` `(int Lag, decimal Correlation);`

> Correlazione di Pearson tra la serie delle variazioni e la sua copia ritardata di Lag periodi.

### 🧾 `ContinuationStats` `(int Setups, int Successes, decimal SuccessPercent);`

> Esito del test di continuazione (occorrenze e probabilita' di successo).

## `ProcioneMGR/Services/Analysis/GapLapAnalyzer.cs`

### 📦 `GapLapAnalyzer`

> Analisi dei Gap e dei Lap di prezzo (Trombetta, cap. 4): base statistica per i sistemi "Gap Filling" (mean reverting sul riassorbimento) o trend following (continuazione). Definizioni (per ogni barra rispetto alla precedente): - Gap Up: open &gt; high precedente; entita' = open - high[-1] - Gap Down: open &lt; low precedente; entita' = open - low[-1] - Lap Up: close[-1] &lt; open &lt;= high[-1]; entita' = open - close[-1] - Lap Down: low[-1] &lt;= open &lt; close[-1]; entita' = open - close[-1] Sotto-eventi: - Refilled: il prezzo ha ricolmato il salto in barra (gap up: low &lt;= high[-1]; gap down: high &gt;= low[-1]; lap: raggiunta la close[-1]). - DeepRefilled: (solo gap) violata anche la close[-1]. - Pos / Neg: la barra del gap ha chiuso sopra / sotto la propria apertura. Nota crypto: su mercati continui (spot 24/7) open ~= close[-1], quindi Gap/Lap "veri" emergono solo su dati con b…

| | Firma | Descrizione |
|---|---|---|
| `m` | `GapLapReport Analyze(IReadOnlyList&lt;OhlcvData&gt; candles, decimal pointValue = 1m)` | Controvalore monetario di 1 punto di prezzo (bigpoint |

### 📦 `EventAccumulator`

> Accumulatore per una categoria di evento (gap up, gap down, lap up, lap down).

| | Firma | Descrizione |
|---|---|---|
| `m` | `void Add(decimal entity, decimal dayResult, bool refilled, bool deepRefilled)` | — |
| `m` | `GapLapCategoryStats ToStats(int totalBars, decimal pointValue, bool hasDeep)` | — |

### 🔢 `GapType`

> Tipo di gap secondo il contesto di trend (McAllen cap. 13).

### 🧾 `GapEvent` `(`

> Singolo gap classificato per contesto, con volume ed eventuale riempimento.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsFilled` | — |

### 🧾 `GapLapReport`

> Report complessivo dell'analisi gap/lap su una serie.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int TotalBars` | Barre analizzate (dalla seconda in poi: serve la precedente per il confronto). |
| `p` | `decimal PointValue` | — |
| `p` | `GapLapCategoryStats GapUp` | — |
| `p` | `GapLapCategoryStats GapDown` | — |
| `p` | `GapLapCategoryStats LapUp` | — |
| `p` | `GapLapCategoryStats LapDown` | — |

### 🧾 `GapLapCategoryStats`

> Statistiche di una categoria di gap/lap (la "tavola riassuntiva" del libro).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Count` | — |
| `p` | `decimal PercentOfBars` | — |
| `p` | `decimal EntitySum` | Somma delle entita' (bacino potenziale cumulato, in punti prezzo). |
| `p` | `decimal EntityAvg` | — |
| `p` | `decimal MoneyAvg` | — |
| `p` | `int RefilledCount` | — |
| `p` | `decimal RefilledPercent` | % di eventi ricolmati in barra: alta -&gt; vocazione mean reverting. |
| `p` | `decimal RefilledEntityAvg` | — |
| `p` | `decimal RefilledMoneyAvg` | — |
| `p` | `int? DeepRefilledCount` | Solo per i gap: ricolmati fino a violare la close precedente. Null per i lap. |
| `p` | `decimal? DeepRefilledPercent` | — |
| `p` | `decimal? DeepRefilledMoneyAvg` | — |
| `p` | `int PositiveCount` | — |
| `p` | `decimal PositivePercent` | % di barre chiuse sopra l'apertura dopo l'evento: alta -&gt; vocazione trend following. |
| `p` | `decimal PositiveMoneyAvg` | — |
| `p` | `int NegativeCount` | — |
| `p` | `decimal NegativePercent` | — |
| `p` | `decimal NegativeMoneyAvg` | — |

## `ProcioneMGR/Services/Analysis/MarketEventDetector.cs`

### 🔢 `MarketEventKind`

> Tipo di evento di mercato rilevato dai soli prezzi/volumi (nessuna fonte esterna).

### 🧾 `MarketEvent` `(DateTime TimestampUtc, MarketEventKind Kind, double Magnitude);`

> Un evento: quando, di che tipo, e quanto estremo (in unità della sua soglia).

### 📦 `MarketEventDetectorConfig`

> Soglie del rilevatore. I default sono deliberatamente conservativi: pochi eventi veri.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int VolWindow` | Finestra della σ rolling dei rendimenti per Crash/Surge. |
| `p` | `double ReturnSigma` | Soglia in σ per Crash (sotto −k) e Surge (sopra +k). |
| `p` | `int VolSpikeShortWindow` | — |
| `p` | `int VolSpikeLongWindow` | — |
| `p` | `double VolSpikeRatio` | VolSpike quando σ_breve / σ_lunga supera questo rapporto. |
| `p` | `int VolumeWindow` | — |
| `p` | `double VolumeMultiple` | VolumeBlowout quando volume &gt; multiplo × mediana rolling. |
| `p` | `int CooldownBars` | Barre minime fra due eventi dello STESSO tipo: un cluster è UN episodio, non dieci. |

### 📦 `MarketEventDetector`

> [T2.7 roadmap macchina-ricerca] Rileva "eventi di mercato" dai prezzi stessi — crash, squeeze, spike di volatilità, blowout di volume — su tutta la profondità OHLCV (sei anni), dove l'alt-data ne copre venti giorni. CAUSALE per costruzione: la decisione alla barra i usa solo statistiche delle barre PRECEDENTI (la barra giudicata non contribuisce mai alla propria soglia). Gli eventi alimentano l' e, in prospettiva, filtri di strategia — che passano dal gate standard come ogni altra ipotesi.

| | Firma | Descrizione |
|---|---|---|
| `m` | `List&lt;MarketEvent&gt; Detect(IReadOnlyList&lt;OhlcvData&gt; candles, MarketEventDetectorConfig? config = null)` | — |

## `ProcioneMGR/Services/Analysis/SupportResistanceAnalyzer.cs`

### 📦 `SupportResistanceAnalyzer`

> Supporti, resistenze, trend a massimi/minimi e ritracciamenti percentuali (McAllen, cap. 7-8 e 15). Metodo: si individuano i punti di swing (pivot: massimo/minimo locale su una finestra simmetrica di K barre), si raggruppano i pivot vicini in LIVELLI di prezzo (piu' tocchi = livello piu' significativo, come da libro), si classifica il trend dalla sequenza degli swing (higher highs + higher lows = uptrend) e si misura quanto il prezzo abbia ritracciato l'ultimo swing (33% sano, 50% tipico, oltre il 66% = probabile inversione).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int PivotWindow` | Semilarghezza della finestra pivot: high[i] deve essere il massimo di [i-K, i+K]. |
| `p` | `decimal LevelTolerancePercent` | Tolleranza % per raggruppare pivot vicini nello stesso livello. |
| `p` | `int VolumeWindow` | Finestra per la media del volume nella conferma dei breakout. |
| `p` | `decimal BreakoutVolumeFactor` | Fattore sopra la media del volume perche' un breakout sia "confermato" (cap. 15). |
| `m` | `SupportResistanceReport Analyze(IReadOnlyList&lt;OhlcvData&gt; candles)` | — |
| `m` | `IReadOnlyList&lt;SwingPoint&gt; FindPivots(IReadOnlyList&lt;OhlcvData&gt; candles)` | Pivot: estremo locale su una finestra simmetrica di barre. In caso di pareggio sull'estremo vince la barra piu' a sinistra (regola standard: confronto stretto verso sinistra, non stretto verso destra) — evita che due ba… |
| `m` | `SwingTrend ClassifyTrend(IReadOnlyList&lt;SwingPoint&gt; pivots)` | Trend dalla sequenza degli swing (Dow/McAllen cap. 1): higher highs + higher lows = uptrend; lower highs + lower lows = downtrend; altrimenti laterale/indeterminato. |

### 🧾 `SwingPoint` `(int Index, DateTime Timestamp, decimal Price, bool IsHigh);`

> Punto di swing (pivot) sui massimi o sui minimi.

### 🧾 `PriceLevel` `(decimal Price, int Touches, int LastTouchIndex, int BarsSinceLastTouch);`

> Livello di supporto/resistenza aggregato dai pivot. Piu' tocchi = piu' significativo.

### 🧾 `BreakoutEvent` `(`

> Attraversamento di un livello da parte della chiusura, con conferma volumetrica.

### 🔢 `SwingTrend`

> Trend classificato dagli swing.

### 🧾 `RetracementInfo` `(`

> Ritracciamento dell'ultimo swing con i livelli 33/50/66 (McAllen cap. 15).

### 🧾 `SupportResistanceReport`

> Report complessivo di supporti/resistenze, trend e ritracciamento.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;SwingPoint&gt; Pivots` | — |
| `p` | `IReadOnlyList&lt;PriceLevel&gt; Levels` | — |
| `p` | `IReadOnlyList&lt;BreakoutEvent&gt; Breakouts` | — |
| `p` | `SwingTrend Trend` | — |
| `p` | `RetracementInfo? Retracement` | — |
| `p` | `PriceLevel? NearestSupport` | — |
| `p` | `PriceLevel? NearestResistance` | — |

## `ProcioneMGR/Services/Analysis/VolumeAnalyzer.cs`

### 📦 `VolumeAnalyzer`

> Interpretazione del volume come "grande confermatore" del trend (McAllen, cap. 15): in un uptrend sano il volume e' piu' alto sulle barre in rialzo che su quelle in ribasso (e viceversa nei downtrend). Quando i massimi vengono fatti a basso volume e i sell-off ad alto volume, e' distribuzione: il trend non e' confermato e il segnale e' di allerta.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;VolumeConfirmation&gt; ConfirmTrend(IReadOnlyList&lt;OhlcvData&gt; candles, int window = 20)` | Conferma volumetrica su finestra scorrevole: per ciascuna barra (dalla finestra piena in poi) confronta il volume medio delle barre positive con quello delle negative nell'ultima finestra e lo incrocia con la direzione … |

### 🧾 `VolumeConfirmation` `(`

> Fotografia della conferma volumetrica su una finestra terminante alla barra Index.

# `Services/Indicators/`

## `ProcioneMGR/Services/Indicators/ITechnicalIndicatorsService.cs`

### 🔌 `ITechnicalIndicatorsService`

> Calcolo di indicatori tecnici lato server. Stateless -&gt; registrato come Singleton. NOTA SULLA FIRMA: lo spec chiedeva List&lt;decimal&gt; con null/NaN per i valori non calcolabili, ma decimal e' un

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;List&lt;decimal?&gt;&gt; CalculateEmaAsync(List&lt;decimal&gt; values, int period, CancellationToken ct = default)` | EMA (Exponential Moving Average) con seed = SMA dei primi valori. |
| `m` | `Task&lt;List&lt;decimal?&gt;&gt; CalculateRsiAsync(List&lt;decimal&gt; closes, int period = 14, CancellationToken ct = default)` | RSI (Relative Strength Index) con smoothing di Wilder. |
| `m` | `List&lt;decimal?&gt; CalculateRsi(List&lt;decimal&gt; closes, int period = 14, CancellationToken ct = default)` | Variante SINCRONA di : il calcolo è CPU-bound e l'async è solo un Task.FromResult — i chiamanti sincroni prima ripiegavano su .GetAwaiter().GetResult() (sync-over-async inutile). |
| `m` | `Task&lt;List&lt;decimal?&gt;&gt; CalculateSmaAsync(List&lt;decimal&gt; values, int period, CancellationToken ct = default)` | SMA (Simple Moving Average) a finestra scorrevole. |
| `m` | `Task&lt;List&lt;decimal?&gt;&gt; CalculateObvAsync(List&lt;decimal&gt; closes, List&lt;decimal&gt; volumes, CancellationToken ct = default)` | [3.8a] On-Balance Volume: somma cumulata del volume col segno della variazione di prezzo (OBV[0]=0). |

## `ProcioneMGR/Services/Indicators/IndicatorSeries.cs`

### 🔢 `IndicatorSeriesType`

### ▫️ `IndicatorPoint` `(long Time, double Value);`

> Punto (tempo in secondi Unix UTC, valore) per il grafico.

### 📦 `IndicatorSeries`

> Una serie da sovrapporre al grafico (EMA, Bollinger, RSI, MACD, equity curve...). = "price" sovrappone sulla scala prezzi; "osc" la mette in un riquadro inferiore (per oscillatori come RSI/MACD).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Title` | — |
| `p` | `string Color` | — |
| `p` | `IndicatorSeriesType Type` | — |
| `p` | `string Scale` | — |
| `p` | `IReadOnlyList&lt;IndicatorPoint&gt; Points` | — |

## `ProcioneMGR/Services/Indicators/TechnicalIndicatorsService.cs`

### 📦 `TechnicalIndicatorsService` `: ITechnicalIndicatorsService`

> Implementazione stateless degli indicatori tecnici. Tutti gli algoritmi sono O(n) (formula ricorsiva per le EMA, sliding window per SMA/deviazione standard). Il calcolo e' sincrono ma esposto come Task per uniformita' API; la cancellazione e' cooperativa (controllata periodicamente nei loop).

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;List&lt;decimal?&gt;&gt; CalculateEmaAsync(List&lt;decimal&gt; values, int period, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;decimal?&gt;&gt; CalculateRsiAsync(List&lt;decimal&gt; closes, int period = 14, CancellationToken ct = default)` | — |
| `m` | `List&lt;decimal?&gt; CalculateRsi(List&lt;decimal&gt; closes, int period = 14, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;decimal?&gt;&gt; CalculateSmaAsync(List&lt;decimal&gt; values, int period, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;decimal?&gt;&gt; CalculateObvAsync(List&lt;decimal&gt; closes, List&lt;decimal&gt; volumes, CancellationToken ct = default)` | — |
| `m` | `List&lt;decimal?&gt; Ema(IReadOnlyList&lt;decimal&gt; values, int period, CancellationToken ct = default)` | — |
| `m` | `List&lt;decimal?&gt; Rsi(IReadOnlyList&lt;decimal&gt; closes, int period = 14, CancellationToken ct = default)` | — |
| `m` | `List&lt;decimal?&gt; Sma(IReadOnlyList&lt;decimal&gt; values, int period, CancellationToken ct = default)` | — |
| `m` | `List&lt;decimal?&gt; Obv(IReadOnlyList&lt;decimal&gt; closes, IReadOnlyList&lt;decimal&gt; volumes, CancellationToken ct = default)` | On-Balance Volume: somma cumulata del volume col segno della variazione di prezzo. Non-null da indice 0 (OBV[0]=0). Scala arbitraria: chi lo consuma guardi la VARIAZIONE. |
| `m` | `decimal Sqrt(decimal value)` | Radice quadrata in decimal (Newton-Raphson) per non perdere precisione. |

# `ProcioneMGR.Ml/`

## `ProcioneMGR.Ml/InferenceServiceImpl.cs`

### 📦 `InferenceServiceImpl` `(`

> Implementazione gRPC del servizio di inferenza (Fase 2a). Riceve un vettore di feature GIA' calcolato dal chiamante, carica il modello (per id o Champion del registry), ne fa inferenza e restituisce il rendimento predetto. SOLA LETTURA: nessuna scrittura sul DB nel path di predict.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;PredictSignalResponse&gt; PredictSignal(PredictSignalRequest request, ServerCallContext context)` | — |

## `ProcioneMGR.Ml/NoOpEncryptionService.cs`

### 📦 `NoOpEncryptionService` `: IEncryptionService`

> che lancia sempre eccezione. Serve solo a soddisfare la dipendenza del costruttore di ApplicationDbContext (l'EncryptedStringConverter è applicato alle colonne credenziali degli exchange, che il path di inferenza ML non tocca MAI — legge solo SavedMlModels, sola lettura). Deliberatamente NON un passthrough silenzioso ( Encrypt(x) =&gt; x ): questo è un servizio long-running con un endpoint gRPC. Se in futuro qualcuno aggiungesse per errore una query su ExchangeCredentials in questo processo, un passthrough scriverebbe/leggerebbe credenziali IN CHIARO su colonne che il resto del sistema tratta come cifrate — fallimento silenzioso. Lanciare trasforma quello scenario in un crash immediato. Conseguenza: a questo host non va distribuita NESSUNA master key.

| | Firma | Descrizione |
|---|---|---|
| `m` | `string Encrypt(string plaintext)` | — |
| `m` | `string Decrypt(string ciphertext)` | — |

## `ProcioneMGR.Ml/Program.cs`

### 📦 `Program` `;`
