# Report Analisi e Piano di Ricostruzione ProcioneMGR

**Data:** 2026-07-08
**Autore:** analisi architetturale (lettura codice sorgente + benchmark repo open source)
**Base analizzata:** 263 file `.cs` di produzione, 79 file di test (534 `[Fact]`/`[Theory]`, 570 test eseguiti), 13 documenti in `docs/`

---

## Executive Summary

ProcioneMGR **non è una piattaforma da ricostruire**: è un sistema maturo che ha già assorbito la
quasi totalità della roadmap QLIB (Alpha158, mining genetico di alpha, drift detection statistica,
execution TWAP/VWAP/Iceberg/Adaptive, stacking di modelli, attention in C# puro, experiment
tracking) e ML4T (fattori alpha + IC, ML supervisionato, CV purged, portfolio MV/RiskParity/HRP,
sentiment, autonomia LLM advisory). I 570 test passano tutti. La disciplina anti-look-ahead e
selezione/holdout è applicata *per costruzione* nelle interfacce chiave. Il valore aggiunto ora non
sta nell'aggiungere altri modelli, ma nel **rendere governabile e statisticamente difendibile ciò
che già esiste**.

**Le tre raccomandazioni prioritarie** emergono dai gap reali, non da feature mancanti:

1. **Rigore statistico sulla selezione (P1)** — la piattaforma oggi *genera migliaia di candidati*
   (mining genetico, sweep da 62.568 combo, stacking) ma misura ancora la bontà con lo Sharpe
   grezzo. Manca la difesa contro il *selection bias sotto test multipli*: **Deflated Sharpe Ratio,
   Combinatorial Purged CV (CPCV), Probability of Backtest Overfitting (PBO)**. È l'anello mancante
   del principio non negoziabile "anti-overfitting per costruzione", ed è diventato il rischio #1
   proprio *perché* la ricerca automatica funziona.
2. **Governo del ciclo di vita di modelli e strategie (P1/P2)** — `SavedMlModel` è un blob piatto
   per-utente senza versione, senza lineage all'`ExperimentRun`, senza stato champion/challenger né
   rollback; il drift viene rilevato ma non chiude il cerchio verso ritiro/retrain automatico. Serve
   un **registry leggero** che colleghi run → modello → gamba attiva → drift → ritiro.
3. **Fondamenta operative: version control + observability (P1 la prima, P2 la seconda)** — la
   cartella **non è un repository git**, non c'è CI, i "backup" sono copie del file `.db`. Per un
   sistema autonomo che muove denaro reale 24/7 questa è la fragilità più grave in assoluto, e la
   più economica da sanare.

Tutto ciò che segue è additivo: si innesta su interfacce esistenti (`IExperimentTracker`,
`IReturnPredictor`, `IPortfolioOptimizer`, `IFeatureDriftDetector`) senza riscrivere nulla.

---

## Sezione 1 — Mappatura Architetturale

Struttura reale: `ProcioneMGR` (Blazor Server + tutti i servizi), `ProcioneMGR.Client` (WASM),
`ProcioneMGR.Migrations.Postgres`, `ProcioneMGR.Tests`, più 5 tool CLI (`StrategyHunter`,
`PlatformExpand`, `FuturesVerify`, `DbBackup`, `DataMigration`).

### 1.1 Namespace → moduli → interfacce → test

| Namespace | File chiave | Interfaccia/contratto pubblico | Test associati |
|---|---|---|---|
| `Services/Alpha` | `Factors.cs` (8 fattori), `FactorEvaluator`, `FactorMath` | `IAlphaFactor` (anti-look-ahead per costruzione), `IFactorEvaluator` (IC Spearman, IR, quantili, decay) | `AlphaFactorTests`, `SentimentAlphaFactorTests`, `FeatureImportanceTests` |
| `Services/Alpha/Alpha158` | `Alpha158Catalog`, `RollingOps` | genera N×M `IAlphaFactor` (~110-150) | `Alpha158FactorTests` (troncamento parametrico) |
| `Services/AlphaMining` | `GeneticAlphaMiner`, `AlphaNode`, `AlphaExpressionParser`, `AlphaExpressionFactor` | GP su alberi, fitness = \|IC\|, deterministico (seed) | `AlphaMiningTests` |
| `Services/ML` | 21 file: `Linear/RandomForest/GradientBoosting/Mlp/Attention/Stacked ReturnPredictor`, `DatasetBuilder`, `PurgedTimeSeriesCv`, `RiskFactorPca`, `HierarchicalClustering`, `SequenceWindowing` | `IReturnPredictor` (Fit/Predict/Save/Load/FeatureImportance), `ISequencePredictor`, `IPurgedTimeSeriesCv`, `IDatasetBuilder` | `LinearReturnPredictorTests`, `TreeReturnPredictorTests`, `MlpReturnPredictorTests`, `AttentionReturnPredictorTests`, `StackedReturnPredictorTests`, `PurgedTimeSeriesCvTests`, `MlDatasetBuilderTests`, `RiskFactorPcaTests`, `HierarchicalClusteringTests` |
| `Services/Backtesting` | `BacktestEngine`, 14 strategie, `MlStrategy`, `RegimeConditionalStrategy`, `CompositeSignalStrategy`, `SignalCatalog`, `StrategyFactory` | `IStrategy` (init precalcola → hot-loop O(1)), `IBacktestEngine` | `BacktestEngineTests`, `BacktestLeverageTests`, `BacktestStopLossTests`, `MlStrategyTests`, `MlStrategySequenceTests`, `NewStrategiesTests`, `IntradayStrategiesTests` |
| `Services/Optimization` | `OptimizationEngine` (grid walk-forward), `Statistics`, `TradeStatistics` | `IOptimizationEngine` | `OptimizationStatisticsTests`, `OptimizationComboKeyTests`, `TradeStatisticsTests`, `TearsheetStatisticsTests`, `MonteCarloAnalyzerTests` |
| `Services/Portfolio` | `MeanVariance/RiskParity/HierarchicalRiskParity Optimizer`, `PortfolioMath` | `IPortfolioOptimizer` | `PortfolioOptimizerTests` |
| `Services/TimeSeries` | `GarchModel`, `EngleGrangerCointegrationTest`, `OlsRegression`, `PairsSpreadAnalyzer` | `IGarchModel`, `ICointegrationTest` | `GarchModelTests`, `CointegrationTests` |
| `Services/PairsTrading` | `PairsBacktestEngine`, `RollingPairsSpreadAnalyzer`, `PairsCandleAligner` | `IPairsBacktestEngine` | `PairsBacktestEngineTests`, `RollingPairsSpreadAnalyzerTests` |
| `Services/Regime` | `RegimeDetector` (K-means ML.NET), `MarketFeatureExtractor`, `RegimeRetrainingWorker` | `IRegimeDetector`, `IMarketFeatureExtractor` | (coperto in ensemble/ML) |
| `Services/Ensemble` | `EnsembleManager`, `EnsembleAllocator`, `EnsembleComparator`, `EnsembleRebalanceWorker` | allocazione capitale regime-aware, rolling-Sharpe | `EnsembleAllocatorTests`, `EnsembleComparatorTests`, `EnsembleManagerDecayTests` |
| `Services/Discovery` | `StrategyDiscoveryEngine`, `StrategyComposer` | `IStrategyDiscovery` (combinatoria AND/OR su catalogo fisso) | `StrategyDiscoveryDefaultsTests`, `CreativeDiscoveryTests` |
| `Services/Execution` | `ExecutionAlgorithms` (Twap/Vwap/Iceberg/Immediate/Adaptive), `ExecutionSimulator`, `ExecutionAlgorithmFactory` | `IExecutionAlgorithm` (BuildPlan) | `ExecutionTests` |
| `Services/Trading` | `TradingEngine`, `SafetyChecker`, `PromotionEvaluator`, `LanePromoter`, `PromotionWorker`, `ExecutionWorker`, `SafetyConfiguration` | `ITradingEngine`; Paper→Testnet auto, **mai Live** | `SafetyCheckerTests`, `SafetyCheckerLeverageTests`, `PromotionEvaluatorTests`, `TradingEngineExecutionTests`, `TradingEngineStopTests`, `MultiLaneIsolationTests`, `MarginMathTests` |
| `Services/Monitoring` | `StrategyDecayMonitor` (reattivo su Sharpe realizzato) | | `StrategyDecayMonitorTests` |
| `Services/Monitoring/Drift` | `Psi/Ks/PageHinkley DriftDetector`, `FeatureDriftMonitor`, `FeatureDriftWorker`, `DriftMath` | `IFeatureDriftDetector`, `IFeatureDriftMonitor` | `DriftDetectorTests`, `FeatureDriftMonitorTests` |
| `Services/Pipeline` | `PipelineEngine`, `PipelineSchedulerWorker` (Cronos), `PipelineApplier`, `PipelineDagValidator`, 5 `Stages/*` | `IPipelineEngine`, `IPipelineStage` | `PipelineTests`, `PipelineEngineConcurrencyTests`, `PipelineSchedulerWorkerTests`, `PipelineStopTargetVariantTests` |
| `Services/Experiments` | `ExperimentTracker`, `ExperimentTrackerExtensions` | `IExperimentTracker` (StartRun/LogMetrics/LogArtifact/Complete) | `ExperimentTrackerTests` |
| `Services/Llm` | `AnthropicLlmClient`, `PipelineSupervisor`, `LlmSupervisorWorker` | `ILlmClient`; **advisory/veto-only** | `PipelineSupervisorTests` |
| `Services/Agents` | `ClaudeSupervisorAgent`, `LoggingSupervisorAgent` | `IPipelineSupervisorAgent` | `SupervisorAgentTests` |
| `Services/AltData` / `Sentiment` | `RssNewsSource`, `RetailSentimentIngestor`, `ForexFactoryIngestor`, `NewsImpactAnalyzer`, `KeywordSentimentScorer` | `IAltDataSource`, `ISentimentScorer` | `RssNewsSourceTests`, `RetailSentimentIngestorTests`, `NewsImpactAnalyzerTests`, `NewsImpactClassifierTests`, `ForexFactoryIngestorTests`, `AltDataSyncServiceTests`, `KeywordSentimentScorerTests` |
| `Services/Risk` | `KellyCalculator`, `PerformanceControlService` | | `KellyCalculatorTests`, `PerformanceControlTests`, `LeverageAdvisorTests` |
| `Components/Pages` | 26 pagine Blazor (`MlLab`, `AlphaMining`, `ExecutionLab`, `Experiments`, `Pipeline`, `Ensemble`, `Trading`, `Optimization`, `Discovery`, `Regimes`, `PairsTrading`, `Sentiment`, `AiSupervisor`, `Backtest`, `MarketAnalysis`, `Volatility`, …) | UI in italiano | (integration via engine test) |

### 1.2 Strumenti ML/AI presenti (implementazione concreta)

| Modello/algoritmo | Libreria | Valutazione | Integrazione end-to-end |
|---|---|---|---|
| Linear/RF/GradientBoosting return predictor | ML.NET 5.0 (+ LightGBM) | correlazione train, permutation feature importance, CV purged | `IReturnPredictor` → `MlStrategy` → `BacktestEngine` → ensemble |
| MLP return predictor | C# puro (backprop manuale) | idem | idem |
| **Attention return predictor** | **C# puro (no TorchSharp)** | `ISequencePredictor` + `SequenceWindowing` | scelta esplicita (a) della roadmap QLIB §1.4 |
| **Stacked return predictor** | meta-learner ridge su predizioni out-of-fold via `IPurgedTimeSeriesCv` | anti-leakage per costruzione | `IReturnPredictor` → si inserisce ovunque, zero modifiche ai consumatori |
| Alpha158 factor set | C# puro (`RollingOps` + MathNet per beta/regressione) | `FactorEvaluator` (IC Spearman, IR, decay) | genera `IAlphaFactor` → `DatasetBuilder` |
| **GeneticAlphaMiner** | C# puro (GP su alberi, seed deterministico) | fitness \|IC\| su selezione, penalità complessità, holdout IC | produce espressioni → `AlphaExpressionFactor` (`IAlphaFactor`) |
| RegimeDetector | ML.NET K-means | silhouette, profili | alimenta `RegimeConditionalStrategy` + ensemble regime-aware |
| RiskFactorPca / HierarchicalClustering | ML.NET / C# | varianza spiegata / dendrogramma | HRP, riduzione dimensionale |
| GARCH(1,1) | C# puro (MLE) | log-likelihood | volatilità/sizing |
| Portfolio MV/RiskParity/HRP | MathNet (algebra) | | `IPortfolioOptimizer` |
| Execution TWAP/VWAP/Iceberg/Adaptive | C# puro (Adaptive = Almgren-Chriss closed-form) | `ExecutionSimulator` (fill/slippage) | `IExecutionAlgorithm`, openings-only, Testnet/Live, default-off |
| LLM supervisor | Anthropic SDK (`claude-opus-4-8`) | advisory/veto-only, mai operativo | `ILlmClient` → `PipelineSupervisor` |

### 1.3 Esecuzione test

```
dotnet test ProcioneMGR.Tests
Superato!  Non superati: 0. Superati: 570. Ignorati: 0. Totale: 570. Durata: 51 s
```

- **570/570 verdi, 0 falliti.** Copertura qualitativa ampia e ben distribuita: Alpha, ML (tutti i
  predittori incl. attention/stacked), Backtesting (leverage, stop-loss, sequenze), Trading/Safety
  (multi-lane isolation, margin math, leverage), Pipeline (concorrenza, scheduler), Drift,
  Execution, Portfolio, PairsTrading, AltData/Sentiment.
- **Warning rilevato durante il restore (finding, non test):** `SQLitePCLRaw.lib.e_sqlite3` 2.1.11
  → **NU1903, vulnerabilità nota di gravità alta** ([GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)).
- **Aree con copertura più leggera (non assente, ma sottile):** `Regime` (testato indirettamente),
  UI Blazor (nessun test bUnit/Playwright — atteso per Blazor Server), `Llm` (un solo file, mockato).

---

## Sezione 2 — Benchmark vs Repo Open Source

Confronto verificato su architettura reale delle repo, non per analogia. La colonna "ProcioneMGR"
riflette il codice letto, non i documenti.

| Capacità | qlib (Py) | Lean (C#) | freqtrade (Py) | nautilus_trader (Rust/Py) | **ProcioneMGR** |
|---|---|---|---|---|---|
| Catalogo alpha + IC | Alpha158/360, Alphalens | — (indicatori) | indicatori | — | **Alpha158 C# + FactorEvaluator (IC/IR/decay)** ✅ |
| Alpha mining automatico | RD-Agent (LLM) | — | — | — | **GP genetica in C# puro** ✅ (unico tra i C#) |
| Model zoo ML | 20+ (GBDT, GRU, Transformer, TRA) | via ML.NET/py | FreqAI (adaptive retrain) | — | Linear/RF/LGBM/MLP/**Attention**/**Stacked** ✅ |
| CV temporale corretta | purged/embargo | — | — | — | `PurgedTimeSeriesCv` (purge+embargo) ✅ |
| **Anti-overfitting statistico** (DSR/CPCV/PBO) | parziale (recorder) | — | — | — | **❌ GAP — solo Sharpe grezzo** |
| Experiment tracking | **MLflow (Recorder)** | cloud | — | — | `ExperimentTracker` interno (7 consumatori) ✅ |
| **Model/strategy registry** (versioni, champion, rollback) | MLflow model registry | cloud registry | — | — | **❌ `SavedMlModel` blob piatto** |
| **Feature store / point-in-time handler** | **sì (Data handler + cache)** | data feed | dataframe cache | catalog | **❌ ricalcolo on-demand, no versioning** |
| Backtest event-driven | sì | sì | sì | **sì (nanosecondo, deterministico)** | sì (a candela) ✅ |
| Execution algos (TWAP/VWAP/Iceberg) | — | sì (Execution model) | limitato | **sì (order book, latenza, fee)** | **TWAP/VWAP/Iceberg/Adaptive + simulator** ✅ |
| Portfolio construction modulare | sì | **sì (pipeline Insight→Target)** | — | sì | MV/RiskParity/HRP ✅ (ma non "pipeline Insight") |
| Hyperopt | grid/optuna | — | **Optuna (bayesiano)** | grid | **grid esaustivo** (❌ no bayesiano) |
| Regime/market dynamics | sì | — | — | — | K-means ✅ |
| Live trading multi-exchange | limitato | **sì (broker plurimi)** | **sì (molti exchange)** | **sì (venue plurime)** | Binance/Bitget (Paper/Testnet/Live) ✅ |
| Autonomia LLM | RD-Agent | — | — | — | **supervisor advisory/veto** ✅ (unico) |
| **Version control + CI** | git+CI | git+CI | git+CI | git+CI | **❌ NON è un repo git, no CI** |
| Observability/telemetria | MLflow UI | dashboard cloud | Telegram/API | logging strutturato | ad-hoc (UI momento) ❌ |

**Cosa fa ProcioneMGR meglio dei riferimenti:** è l'unica piattaforma **C# puro** con mining
genetico di alpha + supervisione LLM advisory + autonomia Paper→Testnet con safety hard-gate su
Live; nessuna dipendenza Python in produzione; disciplina anti-look-ahead codificata nelle
interfacce. qlib/ml4t sono Python-research (no live robusto), freqtrade è ottimo live ma povero di
ricerca ML rigorosa (no purged CV, no IC), Lean è forte su framework/execution ma non fa
ricerca/mining di alpha, nautilus è il migliore su execution/latenza ma non è una piattaforma di
*ricerca* ML.

**Dove i riferimenti sono avanti (gap reali):** (1) qlib ha **DSR/CPCV/recorder MLflow** e un
**data handler point-in-time con cache** — è esattamente il rigore statistico e la feature-store
che a ProcioneMGR mancano; (2) Lean ha la **pipeline modulare Universe→Alpha→Portfolio→Risk→
Execution** con `Insight`/`PortfolioTarget` come tipi di dominio (ProcioneMGR ha i pezzi ma non il
contratto unificante); (3) freqtrade usa **Optuna (bayesiano)** dove ProcioneMGR fa grid esaustivo;
(4) tutti hanno **git+CI** e telemetria.

**Fonti:** [microsoft/qlib](https://github.com/microsoft/qlib) · [qlib DeepWiki](https://deepwiki.com/microsoft/qlib) · [QuantConnect Lean — Algorithm Framework](https://www.quantconnect.com/docs/v2/writing-algorithms/algorithm-framework/overview) · [Lean engine](https://www.lean.io/) · [freqtrade FreqAI](https://www.freqtrade.io/en/stable/freqai/) · [nautilus_trader](https://github.com/nautechsystems/nautilus_trader) · [Deflated Sharpe Ratio — Bailey & López de Prado (SSRN)](https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2460551) · [The Probability of Backtest Overfitting](https://www.davidhbailey.com/dhbpapers/deflated-sharpe.pdf)

---

## Sezione 3 — Analisi Critica

### 3.1 Punti di forza (da preservare — non toccare)

1. **`IAlphaFactor` anti-look-ahead per costruzione.** Il contratto è nero su bianco: `value[i]`
   dipende solo da `candles[0..i]`, verificabile per troncamento. Alpha158 lo testa con un singolo
   `[Theory]` parametrico su tutto il catalogo generato. Questo è *production-grade* e va difeso in
   ogni innesto futuro.
2. **Additività via interfaccia.** `MlpReturnPredictor`, `AttentionReturnPredictor`,
   `StackedReturnPredictor` si sono inseriti implementando `IReturnPredictor` senza toccare i
   consumatori (`MlStrategy`, `/ml`, `/optimization`). Stesso pattern per `RiskParityOptimizer`
   (`IPortfolioOptimizer`) e i detector di drift (`IFeatureDriftDetector`). È il DNA giusto: ogni
   proposta di seguito lo rispetta.
3. **Determinismo.** `GeneticAlphaMiner` è seedato (`Seed=42`), la CV purged è deterministica. Il
   principio "stesso input → stesso output" è rispettato dove conta.
4. **Safety hard-gate.** `SafetyChecker` + `PromotionWorker`/`LanePromoter` promuovono **solo
   Paper→Testnet, mai Live**; `MultiLaneIsolationTests` garantisce isolamento delle 3 corsie.
   L'autonomia è reale ma incapsulata.
5. **LLM propone, backtest dispone.** `PipelineSupervisor`/`ClaudeSupervisorAgent` sono
   **advisory/veto-only** (`ILlmClient`): nessun percorso in cui l'LLM apre un ordine. Coerente al
   100% con il principio non negoziabile.
6. **CV purged + embargo** (`IPurgedTimeSeriesCv`) già presente e usata dallo stacking per le
   predizioni out-of-fold — base perfetta su cui costruire la CPCV (§3.2 gap 1).

### 3.2 Gap reali vs best practice (priorizzati)

#### GAP-1 — Rigore statistico sulla selezione: DSR / CPCV / PBO — **P1**
- **Descrizione:** la piattaforma seleziona il "migliore" tra migliaia di candidati (mining,
  sweep 62.568 combo, top-N alpha) usando **Sharpe/IC grezzo**. Manca la correzione per
  *selection bias sotto test multipli*: Deflated Sharpe Ratio (Bailey–López de Prado),
  Combinatorial Purged CV, Probability of Backtest Overfitting.
- **Impatto:** altissimo su robustezza/PnL. Con la ricerca automatica ora operativa, il rischio
  dominante è "il migliore su N prove è un fluke": è esattamente lo scenario per cui DSR/PBO sono
  stati inventati. Senza, ogni verdetto out-of-sample resta vulnerabile al data-snooping.
- **Come lo risolvono i riferimenti:** qlib integra recorder + metriche corrette; la letteratura
  (SSRN 2460551) dà le formule chiuse per DSR e CSCV/CPCV per PBO.
- **Effort:** medio (3-5 gg). C# puro + MathNet; riusa `IPurgedTimeSeriesCv` e `Statistics`.

#### GAP-2 — Model/Strategy Registry con ciclo di vita — **P1/P2**
- **Descrizione:** `SavedMlModel` è un blob per-utente (`ModelBytes`, no `Version`, no FK a
  `ExperimentRun`, no stato champion/challenger, no `RetiredAt`/motivo). Il drift viene *rilevato*
  (`FeatureDriftMonitor`) ma non *agisce*: nessun cerchio chiuso drift → ritiro → retrain → nuovo
  champion.
- **Impatto:** alto su autonomia/governabilità. Oggi non si può rispondere a "quale modello è
  attivo sulla gamba X, da quale run nasce, e cosa lo sostituirebbe se degrada?".
- **Come lo risolvono i riferimenti:** MLflow model registry (stage: Staging/Production/Archived) in
  qlib; registry cloud in Lean.
- **Effort:** medio (4-6 gg). Additivo: nuova entità EF `ModelVersion`/`StrategyVersion` che
  referenzia `ExperimentRun.Id`, + stato di lifecycle. Riusa il pattern JSON-column di
  `PipelineRun`.

#### GAP-3 — Version control + CI — **P1 (fondamentale, costo minimo)**
- **Descrizione:** la cartella **non è un repository git**, nessun `.gitignore`, nessun `.github`.
  I "backup" sono copie di `app.db` in `backup/`.
- **Impatto:** massimo sul rischio operativo: nessuna storia, nessun rollback del *codice*, nessun
  bisezione dei bug, nessuna CI che esegua i 570 test a ogni modifica. Per un sistema autonomo che
  muove denaro reale è la fragilità più grave e la più economica da eliminare.
- **Effort:** basso (0,5 gg): `git init`, `.gitignore` (.NET: `bin/`, `obj/`, `*.db*`, secrets),
  commit iniziale, GitHub Actions `dotnet test`.

#### GAP-4 — Feature cache / coerenza train-serve — **P2**
- **Descrizione:** i fattori (incl. Alpha158, ~150) vengono ricalcolati on-demand a ogni backtest/
  training/inferenza. Nessuna materializzazione versionata. Rischio di *train/serve skew* silenzioso
  se un operatore rolling cambia.
- **Impatto:** medio (correttezza + performance). qlib ha un data handler point-in-time con
  expression cache.
- **Effort:** medio (3-5 gg). Additivo: `IFactorCache` che chiude la `(symbol, tf, factorHash,
  range)` → serie, con hash dell'espressione come chiave (coerente con l'hash di config già usato
  da `ExperimentTracker`).

#### GAP-5 — Hyperparameter optimization bayesiana — **P2/P3**
- **Descrizione:** `OptimizationEngine` fa grid esaustivo (62.568 combo reali). Nessun surrogato.
  La roadmap QLIB §1.6 lo classificava P3 "opportunistico"; con StrategyHunter che sweepa spazi
  grandi, sale a P2 nella pratica.
- **Impatto:** medio (velocità/copertura dello spazio, non correttezza).
- **Come:** freqtrade usa Optuna. In C#: GP surrogate (kernel RBF + Expected Improvement) via
  MathNet, dietro un nuovo `IHyperparameterOptimizer` affiancato al grid.
- **Effort:** medio (4-6 gg).

#### GAP-6 — Observability / telemetria strutturata — **P2**
- **Descrizione:** metriche e stato vivono nella UI del momento; nessun export OpenTelemetry,
  nessuna serie storica di drift/PnL/latenza/errori, nessun alerting fuori-UI.
- **Impatto:** medio-alto per un sistema 24/7 non presidiato. Non è PnL diretto, è la capacità di
  *accorgersi* dei problemi.
- **Effort:** medio (3-5 gg). `OpenTelemetry.*` NuGet + meter custom sui worker esistenti.

### 3.3 Strumenti sottoutilizzati (valore già pagato, non incassato)

1. **`FactorEvaluator` (IC/IR/decay) non guida la feature selection automatica.** Calcola l'IC di
   ogni fattore ma la scelta delle feature per i modelli ML è manuale in `/ml`. Un
   `IcFeatureSelector` che ordina/filtra i fattori per IC+decay prima del training è a costo quasi
   zero e chiude il cerchio Alpha158 → ML.
2. **`RegimeDetector` alimenta l'ensemble in modo grossolano.** È regime-aware nell'allocazione, ma
   il cluster di regime **non** entra come feature nel meta-learner dello `StackedReturnPredictor`
   (la roadmap QLIB §1.8 lo suggeriva). One-hot del regime nel meta-training è additivo e già
   disponibile system-wide.
3. **`ExperimentTracker` non è ancora la fonte del confronto storico.** È scritto da 7 consumatori
   ma la UI `/experiments` è di sola lettura per run singoli; il confronto side-by-side e il collegamento
   run→modello attivo (GAP-2) trasformerebbero dati già raccolti in decisioni.
4. **`ExecutionSimulator` non misura l'implementation shortfall.** Gli algo esecutivi esistono ma la
   qualità del fill non è loggata come metrica confrontabile — è anche il prerequisito-dati per
   qualunque futura RL di execution (QLIB §1.9), oggi impossibile perché lo storico di fill non si
   accumula.
5. **`MonteCarloAnalyzer` / `TearsheetStatistics` esistono** ma non sono agganciati al verdetto di
   selezione: sarebbero input naturali del DSR/PBO (GAP-1).

### 3.4 Debiti tecnici / fragilità

| Debito | Gravità | Nota |
|---|---|---|
| **Nessun git/CI** | Alta | vedi GAP-3 |
| **`SQLitePCLRaw` 2.1.11 vulnerabile (NU1903)** | Alta | aggiornare la dipendenza transitiva; SQLite è dev-only ma la CVE è reale |
| Secret management | Media | verificare che chiavi/API (`ANTHROPIC_API_KEY`, credenziali exchange) non finiscano in un futuro commit — `.gitignore` + user-secrets |
| `SavedMlModel` senza versione/lineage | Media | vedi GAP-2 |
| Backup = copia file `.db` | Media | ora c'è Postgres in prod; serve strategia di backup Postgres (pg_dump schedulato), non copia SQLite |
| Ricalcolo fattori ripetuto | Media | performance + rischio skew (GAP-4) |
| Grid esaustivo costoso | Bassa | funziona, ma scala male (GAP-5) |
| Copertura test UI Blazor | Bassa | accettabile per Blazor Server; i path critici sono nei servizi |

---

## Sezione 4 — Piano di Ricostruzione

Ordinamento per ROI e dipendenze reali: prima le **fondamenta abilitanti** (git, poi il rigore
statistico che rende *misurabile e difendibile* tutto il resto), poi il **governo del ciclo di
vita**, poi le **ottimizzazioni**. Nessuna fase richiede TorchSharp (l'attention è già in C# puro),
quindi nessun bivio tecnologico pendente.

---

### Fase 0 — Fondamenta operative: version control + CI + security patch (P1)
**Obiettivo:** codice sotto git, 570 test in CI a ogni push, dipendenza vulnerabile rimossa.
**Dipendenze:** nessuna.
**Moduli coinvolti:** root (`git init`), nuovo `.gitignore`, `.github/workflows/ci.yml`,
`ProcioneMGR.csproj` + `ProcioneMGR.Tests.csproj` (bump `SQLitePCLRaw`).
**Nuove dipendenze NuGet:** nessuna (solo bump della transitiva vulnerabile).
**Test da aggiungere:** nessuno nuovo — la CI *è* il deliverable (esegue `dotnet test`).
**Criteri di successo:** repo git con commit iniziale che esclude `bin/obj/*.db*`/secrets; workflow
verde con 570/570; `dotnet list package --vulnerable` pulito.
**Stima effort:** 0,5-1 gg.

### Fase 1 — Rigore statistico sulla selezione: DSR + CPCV + PBO (P1)
**Obiettivo:** ogni verdetto out-of-sample (mining, sweep, discovery) accompagnato da Deflated
Sharpe, PBO e, dove applicabile, valutazione CPCV — non più solo Sharpe grezzo.
**Dipendenze:** Fase 0 (per versionare il cambiamento).
**Moduli coinvolti:** nuovo `Services/Validation/` con `DeflatedSharpe`, `CombinatorialPurgedCv`
(estende il pattern di `IPurgedTimeSeriesCv`), `BacktestOverfittingProbability`; aggancio in
`OptimizationEngine`, `StrategyDiscoveryEngine`, `GeneticAlphaMiner` (post-selezione) e nella UI di
verdetto (`/optimization`, `/discovery`, `/alpha-mining`); metriche loggate via `IExperimentTracker`.
**Nuove dipendenze NuGet:** nessuna (MathNet copre normale/erf; il resto è aritmetica).
**Test da aggiungere:** `DeflatedSharpeTests` (casi noti dal paper), `CombinatorialPurgedCvTests`
(numero di split = C(N,k), nessuna sovrapposizione dopo purge), `PboTests` (PBO≈0.5 su rumore,
basso su segnale vero), determinismo.
**Criteri di successo:** su un pannello di N strategie casuali (rumore), PBO→~0.5 e DSR non
significativo; su una strategia con edge reale, DSR>0 significativo. Le UI di selezione mostrano la
colonna DSR/PBO accanto allo Sharpe.
**Stima effort:** 3-5 gg.

### Fase 2 — Model/Strategy Registry + ciclo di vita chiuso col drift (P1/P2)
**Obiettivo:** ogni modello/strategia attiva è versionato, tracciabile al run che l'ha generato, con
stato champion/challenger e retirement automatico su drift.
**Dipendenze:** Fase 0; usa `IExperimentTracker` e `IFeatureDriftMonitor` esistenti.
**Moduli coinvolti:** nuove entità EF `ModelVersion`/`StrategyVersion` (FK a `ExperimentRun.Id`,
stato `Staging|Champion|Challenger|Retired`, `RetiredReason`); `SavedMlModel` esteso con `Version` +
lineage; nuovo `RegistryService`; `FeatureDriftWorker` esteso per marcare `Retired` e accodare un
retrain; UI `/experiments` → tab "Registry" con champion attivo per gamba.
**Nuove dipendenze NuGet:** nessuna. **Migrazione EF:** una (additiva, campi nullable).
**Test da aggiungere:** `RegistryPromotionTests` (challenger→champion solo con DSR migliore),
`DriftRetirementTests` (drift oltre soglia ⇒ stato Retired + retrain accodato, **mai** auto-Live).
**Criteri di successo:** dato un drift simulato oltre soglia PSI, il modello passa a `Retired`, un
retrain è accodato, e la promozione del sostituto passa dal gate DSR (Fase 1). Nessun percorso
tocca Live automaticamente.
**Stima effort:** 4-6 gg.

### Fase 3 — Strumenti sottoutilizzati: IC feature selection + regime nel meta-learner (P2)
**Obiettivo:** chiudere i cerchi già a costo pagato (§3.3).
**Dipendenze:** Fase 1 (per validare che il guadagno è reale, non snooping).
**Moduli coinvolti:** nuovo `IcFeatureSelector` in `Services/ML` (ordina/filtra `IAlphaFactor` per
IC+decay via `FactorEvaluator`); `StackedReturnPredictor` esteso con feature one-hot del regime da
`RegimeDetector`; `ExecutionSimulator` che logga implementation shortfall come metrica
(`IExperimentTracker`).
**Nuove dipendenze NuGet:** nessuna.
**Test da aggiungere:** `IcFeatureSelectorTests` (seleziona i fattori a IC alto su segnale
sintetico), `StackedRegimeFeatureTests` (nessun leakage: regime al tempo i da `candles[0..i]`),
`ExecutionShortfallLoggingTests`.
**Criteri di successo:** su dataset sintetico, la selezione IC scarta i fattori-rumore; lo shortfall
compare come metrica confrontabile nei run di execution.
**Stima effort:** 3-5 gg.

### Fase 4 — Feature cache / coerenza train-serve (P2)
**Obiettivo:** materializzazione versionata dei fattori, eliminazione del ricalcolo e guardia
anti-skew.
**Dipendenze:** nessuna forte; sinergica con Fase 3.
**Moduli coinvolti:** nuovo `IFactorCache` (`Services/Alpha`), chiave = hash dell'espressione/spec
(coerente con l'hash di config di `ExperimentTracker`); `DatasetBuilder` e `MlStrategy` consultano
la cache; invalidazione su nuovo range dati.
**Nuove dipendenze NuGet:** nessuna (persistenza EF o file, come i modelli).
**Test da aggiungere:** `FactorCacheTests` (hit/miss, invariante: valore cache == ricalcolo,
invalidazione su cambio spec).
**Criteri di successo:** backtest ripetuto sullo stesso range non ricalcola i fattori; il test di
identità cache-vs-ricalcolo passa (nessuno skew).
**Stima effort:** 3-5 gg.

### Fase 5 — Observability strutturata (P2)
**Obiettivo:** serie storiche di drift/PnL/latenza/errori esportabili, alerting fuori-UI.
**Dipendenze:** nessuna.
**Moduli coinvolti:** `OpenTelemetry` meter/tracer nei worker (`TradingWorker`, `PromotionWorker`,
`FeatureDriftWorker`, `PipelineSchedulerWorker`, `ExecutionWorker`); export configurabile
(Prometheus/OTLP), default-off in dev.
**Nuove dipendenze NuGet:** `OpenTelemetry`, `OpenTelemetry.Exporter.*` (managed, no runtime nativo).
**Test da aggiungere:** `TelemetryEmissionTests` (i worker emettono le metriche attese su eventi
chiave).
**Criteri di successo:** un drift/una promozione emettono metriche osservabili da un collector
locale; nessun impatto quando l'export è off.
**Stima effort:** 3-5 gg.

### Fase 6 — Bayesian optimization (P2/P3, opportunistico)
**Obiettivo:** ridurre il costo degli sweep grandi mantenendo il grid come default.
**Dipendenze:** Fase 1 (il criterio ottimizzato deve essere DSR, non Sharpe grezzo — altrimenti si
ottimizza il rumore più efficientemente).
**Moduli coinvolti:** nuovo `IHyperparameterOptimizer` + `BayesianOptimizationEngine` (GP RBF + EI,
MathNet) affiancato a `OptimizationEngine`; switch "Grid/Bayesiano" in `/optimization`.
**Nuove dipendenze NuGet:** nessuna.
**Test da aggiungere:** `BayesianOptimizerTests` (converge al minimo di una funzione test nota in <N
valutazioni; deterministico con seed).
**Criteri di successo:** su una funzione obiettivo nota, raggiunge l'ottimo con meno valutazioni del
grid a parità di qualità.
**Stima effort:** 4-6 gg.

> **Nota su Transformer/TRA (QLIB §1.4) e RL execution (§1.9):** l'attention è **già** in C# puro
> (`AttentionReturnPredictor`), quindi il bivio TorchSharp resta *chiuso* e non è nel piano. La RL
> per l'execution resta P3 a lungo termine e **dipende dallo storico di fill** che la Fase 3 inizia
> ad accumulare (implementation shortfall loggato) — non prima.

---

## Sezione 5 — Roadmap Temporale (Gantt testuale)

```
Settimana:        1        2        3        4        5        6
Fase 0 (git/CI)  [==]                                              P1  (sblocca tutto: versiona ogni fase successiva)
Fase 1 (DSR/CPCV)   [========]                                     P1  (dipende da F0)
Fase 2 (Registry)            [==========]                         P1/P2 (dipende da F0; usa DSR di F1 nel gate)
Fase 3 (IC+regime)              [========]                         P2  (parallelo a F2; dipende da F1)
Fase 4 (FeatureCache)                    [========]                P2  (sinergica con F3, indip.)
Fase 5 (Observability)                   [========]                P2  (indip., parallelizzabile)
Fase 6 (Bayesian)                                 [==========]     P3  (dipende da F1: ottimizza DSR)
```

- **Sequenziali obbligate:** F0 → tutto; F1 → F2 (gate DSR) e F1 → F3 (validazione guadagno) e
  F1 → F6 (obiettivo = DSR).
- **Parallelizzabili:** F3 ∥ F2 (toccano moduli diversi), F4 ∥ F5 (indipendenti), F5 può partire
  in qualsiasi momento dopo F0.
- **Percorso critico:** F0 → F1 → F2 (~4 settimane). Le altre riempiono la capacità residua.

---

## Sezione 6 — Rischi e Mitigazioni

| Rischio | Probabilità | Impatto | Mitigazione |
|---|---|---|---|
| **`git init` cattura secret/db nel primo commit** | Media | Alto | `.gitignore` *prima* del primo `git add`; verificare con `git status` che `*.db*` e secrets siano esclusi; usare user-secrets (già presente `UserSecretsId`) |
| DSR/PBO mal implementati danno falsa sicurezza | Media | Alto | test su casi noti del paper + su rumore puro (PBO≈0.5); code-review con la formula chiusa alla mano |
| Registry introduce accoppiamento con codice esistente | Bassa | Medio | entità additive, campi nullable, `SavedMlModel` retro-compatibile; migrazione EF solo additiva |
| Feature cache introduce skew invece di eliminarlo | Bassa | Alto | test-invariante "valore cache == ricalcolo" obbligatorio; invalidazione su hash spec |
| Bayesian ottimizza il rumore più in fretta del grid | Media | Medio | vincolo di dipendenza: F6 **dopo** F1, obiettivo = DSR non Sharpe |
| Observability rallenta i worker | Bassa | Basso | export off di default in dev; meter leggeri, campionati |
| Regressione su path di trading reale | Bassa | Alto | tutte le fasi sono additive e default-off sul lato Live; `SafetyChecker` e il gate "mai auto-Live" restano invarianti; CI (F0) esegue i 570 test a ogni cambiamento |

**Invarianti da non violare in nessuna fase:** anti-look-ahead per costruzione; selezione
in-sample / verdetto su holdout; determinismo seedato; nessuna promozione automatica a Live;
additività (implementare interfacce esistenti, non duplicarle); LLM advisory/veto, mai operativo.

---

## Sezione 7 — Prossimo Passo Operativo

**La primissima cosa, oggi, prima di qualunque feature: mettere il codice sotto git con CI (Fase 0).**
È mezza giornata di lavoro, azzera il rischio operativo più grave (nessuna storia/rollback su un
sistema che muove denaro) e rende versionabile e revisionabile ogni fase successiva.

File da creare/modificare:

1. **`/.gitignore`** (nuovo) — pattern .NET: `bin/`, `obj/`, `.vs/`, `*.db`, `*.db-shm`,
   `*.db-wal`, `*.bak*`, `backup/`, `*.user`, `appsettings.*.local.json`, secrets. **Creare e
   verificare *prima* di `git add`.**
2. **`git init` + primo commit** — dopo aver confermato con `git status` che nessun `.db`, `.bak` o
   secret è in staging.
3. **`/.github/workflows/ci.yml`** (nuovo) — `actions/setup-dotnet@v4` (net10.0) →
   `dotnet restore` → `dotnet build` → `dotnet test ProcioneMGR.Tests` → `dotnet list package
   --vulnerable` (fail su high). Trigger su push/PR.
4. **`ProcioneMGR/ProcioneMGR.csproj` + `ProcioneMGR.Tests/ProcioneMGR.Tests.csproj`** — bump della
   dipendenza transitiva `SQLitePCLRaw` a una versione senza NU1903 (aggiungere un
   `PackageReference` esplicito alla versione patchata se il bump transitivo non basta).

Verifica di completamento: workflow verde, **570/570 test**, `dotnet list package --vulnerable`
pulito, `git log` con il commit iniziale che **non** contiene `.db`/secret.

Subito dopo, su tua conferma, si parte con la **Fase 1 (Deflated Sharpe + CPCV + PBO)** in
`Services/Validation/`, riusando `IPurgedTimeSeriesCv` e `Statistics` esistenti e loggando le nuove
metriche via `IExperimentTracker` — nessuna nuova dipendenza, rischio di regressione basso,
allineamento pieno col principio non negoziabile "anti-overfitting per costruzione".
```
```

---

*Nota di onestà metodologica: i giudizi su robustezza e PnL sono qualitativi e non verificabili
senza dati di mercato reali e un periodo di Paper trading. Ogni proposta è però corredata di un
criterio di successo misurabile via test xUnit o metrica loggata, così che il valore possa essere
validato in-sample/out-of-sample con la stessa disciplina già in uso nella piattaforma.*
