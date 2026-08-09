# 04 — CATALOGO ALGORITMI E MODELLI

Ogni voce riporta: nome · file · classe/metodo · categoria · scopo · formula/pseudocodice quando
ricavabile · input · output · parametri · assunzioni · dipendenze · integrazione attuale · rischi di
leakage/lookahead/overfitting · determinismo · metriche prodotte · stato · note.

**Stato:** `IMPL` implementato e cablato · `IMPL-OFF` implementato, cablato, spento per decisione ·
`DISC` implementato ma irraggiungibile dall'app · `DEAD` nessun riferimento · `EXP` sperimentale ·
`DUP` logica duplicata.

---

## A. Generazione di fattori (alpha)

### A1 · Alpha158 catalog
- **File / tipo:** `Services/Alpha/Alpha158/Alpha158Catalog.cs` → `Alpha158Catalog.BuildCatalog()`, `TryCreate(name, out factor)`
- **Categoria:** feature engineering · **Stato:** `IMPL`
- **Scopo:** catalogo di fattori stile qlib generato per composizione: `OpDescriptor` (pochi operatori) × molti orizzonti.
- **Input:** serie `Bars` (OHLCV) · **Output:** `IAlphaFactor` con `Name` autodescrittivo
- **Parametri:** orizzonti di default; il nome codifica operatore + orizzonte ⇒ ricostruzione dal nome
- **Assunzioni:** operatori **causali** (`RollingOps`): nessuna finestra guarda avanti
- **Integrazione:** `AlphaFactorFactory.cs:60` li inietta nei prototipi ⇒ visibili a dataset, IC selector, pipeline, `/feature-selection`
- **Rischi:** ⚠️ **158 fattori × ricerca = molte prove effettive** ⇒ il DSR va calcolato su `EffectiveTrials`, altrimenti l'inflazione da selezione è garantita. ⚠️ la loro **deriva non è sorvegliata** (`FactorDriftMonitor.cs:105` li esclude per costo)
- **Determinismo:** ✅ puro · **Test:** `Alpha158FactorTests`, `AuditAlpha158EdgeCaseTests`

### A2 · Operatori rolling causali
- **File:** `Services/Alpha/Alpha158/RollingOps.cs` (518 righe) · **Stato:** `IMPL`
- **Scopo:** primitive rolling (media, dev.std., min/max, rank, correlazione, regressione…) su cui è costruito Alpha158
- **Rischio chiave:** è **il** punto dove un lookahead entrerebbe silenziosamente in tutti i 158 fattori. Va trattato come codice critico quanto il `SafetyChecker`.

### A3 · Fattori scritti a mano (8)
- **File:** `Services/Alpha/Factors.cs` (308) · **Stato:** `IMPL`
- `MomentumFactor`, `MeanReversionFactor`, `RealizedVolatilityFactor`, `ParkinsonVolatilityFactor`, `RelativeVolumeFactor`, `RsiFactor`, `MacdFactor`, `DistanceFromMaFactor`
- **Nota:** sono gli **unici** sorvegliati dal monitor di deriva.

### A4 · Fattori di order flow
- **File:** `Services/Alpha/OrderFlowFactors.cs` (115) → `TakerImbalanceFactor`, `AvgTradeSizeFactor` · **Stato:** `IMPL`
- **Input:** campi estesi delle kline (taker buy volume) · **Nota:** non richiedono il tape, quindi funzionano anche senza il modulo Microstructure.

### A5 · Fattore sentiment come feature ML
- **File:** `Services/Sentiment/SentimentFeatureFactor.cs` (40) · **Stato:** `IMPL-OFF`
- **Integrazione:** `AlphaFactorFactory.cs:70`, gated da `Sentiment:EnableMlFeature` = **false**
- **Rischio dichiarato:** il sentiment è disponibile solo dal momento in cui si è iniziato a raccoglierlo ⇒ backtest lunghi avrebbero un buco iniziale.

### A6 · Alpha mining genetico
- **File:** `Services/AlphaMining/GeneticAlphaMiner.cs` (437) + `AlphaNode.cs` (267) + `AlphaExpressionParser.cs` (91) · **Stato:** `IMPL`
- **Scopo:** programmazione genetica su alberi di espressione (`AlphaOp`) per scoprire fattori formulaici
- **Parametri:** `MiningConfig` con `Seed = 42` (riga 43), popolazione, generazioni
- **Output:** `MinedFactor` → `AlphaExpressionFactor` (ricostruibile dal testo dell'espressione)
- **Rischi:** 🔴 **overfitting per costruzione** — la GP cerca finché trova. Mitigato da un gate CV (`GeneticMinerCvGateTests`), ma il risultato va **sempre** passato per DSR con `EffectiveTrials` pari alle espressioni valutate.
- **Determinismo:** ✅ `new Random(config.Seed)` (riga 86)

---

## B. Valutazione dei fattori

### B1 · Information Coefficient
- **File:** `Services/Alpha/FactorEvaluator.cs` (192) → `IFactorEvaluator`
- **Formula:** IC = correlazione (Spearman/Pearson) fra valore del fattore a *t* e rendimento forward su orizzonte *h*; IR = media(IC)/dev.std.(IC) sulle finestre
- **Output:** `FactorEvaluationResult` con `IcByHorizon`, `QuantileReturn[]`, correlazioni
- **Rischio lookahead:** il rendimento forward **deve** essere strettamente futuro rispetto al fattore. Presidiato da `AuditCvLeakageTests`.
- **Stato:** `IMPL`

### B2 · Selezione feature per IC
- **File:** `Services/ML/IcFeatureSelector.cs` (93) → `IIcFeatureSelector`
- **Parametri:** `IcFeatureSelectionConfig` — `forwardHorizon`, `topN`, `minAbsIc`, `minIr`, `requireConsistentSign`
- **Output:** `ScoredFactor[]` · **UI:** `/feature-selection` · **Stato:** `IMPL`
- **Rischio:** 🔴 **selezione su tutto il campione = leakage di selezione**. Va confinata alla finestra di training di ogni fold.

### B3 · Deriva dell'IC
- **File:** `Services/Alpha/FactorDriftAnalyzer.cs` (281), `FactorIcHistory.cs` (298), `FactorDriftMonitor.cs` (335)
- **Output:** `FactorDriftReport` con `FactorDriftStatus`; persistito in `FactorIcWindow` · **Stato:** `IMPL`

---

## C. Modelli predittivi

| ID | Nome | File | Algoritmo reale | Parametri | Determinismo | Stato |
|---|---|---|---|---|---|---|
| C1 | Linear | `ML/LinearReturnPredictor.cs` | OLS/ridge | — | ✅ chiuso | `IMPL` |
| C2 | Random Forest | `ML/RandomForestReturnPredictor.cs` | **ML.NET FastForest** | n. alberi, foglie | ✅ seed | `IMPL` |
| C3 | LightGBM | `ML/GradientBoostingReturnPredictor.cs` | **ML.NET LightGBM** (pacchetto `Microsoft.ML.LightGbm`) | foglie, iterazioni, learning rate | ✅ | `IMPL` |
| C4 | MLP | `ML/MlpReturnPredictor.cs` (298) | rete a mano | `hiddenUnits=16`, `epochs=200`, `lr=0.01`, `seed=42` | ⚠️ vedi sotto | `IMPL` |
| C5 | Attention | `ML/AttentionReturnPredictor.cs` (559) | attention su finestre | `windowLength=8`, `embedDim=16`, `hiddenUnits=16`, `epochs=150`, `lr=0.01`, `seed=42` | ⚠️ | `IMPL` |
| C6 | Stacked | `ML/StackedReturnPredictor.cs` (428) | stacking, ridge non-negativa | `StackingMode` | ⚠️ | `IMPL` |

> ✅ **C3 — correzione del 2026-08-08:** un primo passaggio aveva dedotto "FastTree" dal nome
> della classe base; l'apertura di `BuildPipeline` mostra `Trainers.LightGbm(...)` col pacchetto
> `Microsoft.ML.LightGbm`. **È vero LightGBM**, etichette corrette, G-12 ritirato.

> ⚠️ **Determinismo parziale (G-08).** I predittori accettano un `seed`, ma in tre punti la sorgente
> casuale è **cablata**: `MlpReturnPredictor.cs:234`, `RegressionPredictorBase.cs:109`,
> `StackedReturnPredictor.cs:382` usano `new Random(42)` invece del seed configurato. Chi cambia
> seed per stimare la varianza del modello **non cambia quei rami**.

### C7 · Meta-labeling (López de Prado)
- **File:** `ML/Labeling/TripleBarrierLabeler.cs` (222), `MetaLabeler.cs` (229), `MetaModelTrainer.cs` (178), `MetaLabelingAnalysisService.cs` (207)
- **Pseudocodice triple-barrier:**
  ```
  per ogni segnale primario a t:
      barriera superiore = prezzo(t) · (1 + tp)
      barriera inferiore = prezzo(t) · (1 − sl)
      barriera verticale = t + maxHolding
      etichetta = quale barriera viene toccata per prima
  ```
- **Output:** `TripleBarrierLabel`, `MetaLabelSample`, `MetaLabelingReport`
- **Assunzione critica:** le barriere si valutano su barre **successive** a *t*.
- **Integrazione:** ✅ catena completa dietro `/backtest` · **Stato:** `IMPL`

### C8 · SHAP su alberi
- **File:** `ML/Shap/TreeShapExplainer.cs` (280), `ShapTree.cs`, `MlNetTreeExtractor.cs` (197), `ShapAnalysis.cs` (177)
- **Scopo:** TreeSHAP esatto sugli ensemble ML.NET; `ShapContextLens` condiziona le attribuzioni per contesto
- **Output:** `ShapExplanation`, `ShapSummaryRow`, `ShapAnalysisResult` · **Stato:** `IMPL`

### C9 · PCA sui fattori di rischio
- **File:** `ML/RiskFactorPca.cs` (103) → `IRiskFactorPca`; output `RiskFactorPcaResult`, `PrincipalComponent`
- **Integrazione:** **solo** `/portfolio` · **Stato:** `IMPL` (uso limitato)

### C10 · Clustering gerarchico
- **File:** `ML/HierarchicalClustering.cs` (105) — `LinkageMethod`, `CorrelationDistance` = √(0,5·(1−ρ))
- **Consumatore:** HRP · **Stato:** `IMPL`

---

## D. Validazione e anti-overfitting

### D1 · Purged Time-Series CV
- **File:** `ML/PurgedTimeSeriesCv.cs` (41) → `IPurgedTimeSeriesCv`
- **Pseudocodice:**
  ```
  per ogni fold k:
      test  = [i_k, j_k]
      train = tutto tranne test, MENO purge (label overlap) e MENO embargo dopo il test
  ```
- **Perché:** senza purge, l'etichetta di un campione di training che si sovrappone al test contamina il fold.
- **Test:** `PurgedTimeSeriesCvTests`, `AuditCvLeakageTests`, `OptimizationEmbargoTests` · **Stato:** `IMPL`

### D2 · Combinatorial Purged CV
- **File:** `Validation/CombinatorialPurgedCv.cs` (106) — `CpcvSplit`
- **Consumatori:** `OptimizationEngine`, `BacktestOverfitting` (PBO)
- ⚠️ **`ICombinatorialPurgedCv` non è registrata in DI né usata**: astrazione morta (G-07) · **Stato:** `IMPL` (interfaccia `DEAD`)

### D3 · Deflated Sharpe Ratio
- **File:** `Validation/DeflatedSharpeRatio.cs` (160) + `ReturnMoments` (Sharpe per periodo, skewness, kurtosis)
- **Formula (Bailey & López de Prado):** DSR = Φ( (SR − SR₀)·√(n−1) / √(1 − γ₁·SR + (γ₂−1)/4·SR²) ),
  con SR₀ soglia attesa dal massimo di N prove indipendenti
- **Input:** serie di rendimenti + numero di **prove effettive** (`EffectiveTrials`)
- **Rischio:** il DSR è valido solo se il numero di prove è dichiarato con onestà. Sottostimarlo è il modo più semplice per fabbricare significatività.
- **Stato:** `IMPL` — usato come gate nel `ModelRegistry`

### D4 · Prove effettive
- **File:** `Validation/EffectiveTrials.cs` (100) — corregge il conteggio per la correlazione fra prove · **Stato:** `IMPL`

### D5 · Minimum Track Record Length
- **File:** `Validation/MinTrackRecord.cs` (96) → `MinTrl(...)`, `MinDetectableSharpe(...)`, conversioni annualizzato↔per-periodo
- **Uso:** `PowerCheckStage` — **dichiara prima del run** quale Sharpe è aritmeticamente rilevabile con la storia disponibile. Risposta diretta al problema documentato in `docs/REPORT-PERCHE-NON-CONSOLIDA-2026-07-28.md` (Sharpe 1,0 ⇒ ~6,2 anni per essere significativo).
- **Stato:** `IMPL` — uno dei presidi più preziosi della piattaforma

### D6 · Gemello nullo (null twin)
- **File:** `Validation/NullTwinGenerator.cs` (111) → `Generate(real, seed, meanBlockLength = 24)`; giudice `NullTwinJudge.cs` (144)
- **Metodo:** **block bootstrap** (blocchi di lunghezza media 24) che preserva l'autocorrelazione ma distrugge l'edge ⇒ la strategia deve battere il 99° percentile di 200 gemelli
- **Attenzione già pagata:** non randomizzare su **asset correlati** — fabbrica falsa significatività (errore documentato, 2026-07-20)
- **Determinismo:** ✅ seed esplicito · **Stato:** `IMPL` — giudice unificato per pipeline e CLI

### D7 · PBO (Probability of Backtest Overfitting)
- **File:** `Validation/BacktestOverfitting.cs` (103) → `PboResult` · **Stato:** `IMPL`

### D8 · Potenza dei gate
- **File:** `Validation/GatePowerAnalyzer.cs` (192) — con quanti dati un gate distingue davvero segnale da rumore · **Stato:** `IMPL`

### D9 · Test di permutazione
- **File:** `Validation/PermutationTest.cs` (68); `BlockBootstrapPermutationTests` · **Stato:** `IMPL`

---

## E. Regime

### E1 · RegimeDetector (K-means) — **in esercizio**
- **File:** `Regime/RegimeDetector.cs` (548) → `IRegimeDetector`
- **Input:** `MarketFeatures` da `MarketFeatureExtractor` (215) + breadth
- **Output:** `RegimeModel` persistito, `RegimeProfile`, `StrategyPerformanceInRegime`
- **Normalizzazione:** `FeatureNormalizer` / `FeatureScaling` — **i parametri di scala vanno stimati sul training**, altrimenti c'è leakage
- **Consumatori:** ensemble, `RegimeAugmentation` (one-hot nel dataset ML), `RegimeConditionalStrategy`, `LaneRegimeRouter`, `RegimeChangeDetector`, `/regimes`
- **Stato:** `IMPL`

### E2 · JumpModel — 🔴 `DEAD`
- **File:** `Regime/JumpModel.cs` (288) → `JumpModel.Fit(z, k, lambda, seed)`, `Standardize(x)`, `RunLengths(states)`
- **Algoritmo:** clustering di stati con **penalità λ sulle transizioni** ⇒ con λ=0 degenera in K-means; con λ>0 produce regimi più persistenti (meno transizioni spurie). Esattamente ciò che i test verificano (`JumpModelTests`: `jumpTransitions < kmeansTransitions`).
- **Integrazione:** **nessuna**. `JumpModelFit` non è referenziato neanche dai test.
- **Nota:** è la sostituzione naturale di E1 e costerebbe poco cablarla dietro un flag. Vedi Fase 2 del blueprint.

### E3 · Router di regime
- **File:** `Regime/LaneRegimeRouter.cs` (265) — `RegimeRoutingRule`, `RegimeRoutingDecision`
- **Stato:** `IMPL-OFF` — `Enabled=true` ma `DriveDecisions=false`: classifica, non decide. **Deliberato.**

---

## F. Strategie (14 concrete su `IStrategy`)

| Strategia | File | Famiglia | Parametri principali |
|---|---|---|---|
| `EmaCrossStrategy` | `Backtesting/EmaCrossStrategy.cs` | trend | periodi fast/slow |
| `PriceSmaCrossStrategy` | idem | trend | periodo SMA |
| `MacdTrendStrategy` | idem | trend | fast/slow/signal |
| `MomentumStrategy` | idem | trend | lookback |
| `SupertrendStrategy` | idem | trend | ATR period, multiplier |
| `DonchianBreakoutStrategy` | idem | breakout | canale |
| `RsiOversoldStrategy` | idem | mean reversion | periodo, soglie |
| `BollingerMeanReversionStrategy` | idem | mean reversion | periodo, σ |
| `StochasticStrategy` | idem | mean reversion | %K, %D |
| `VwapReversionStrategy` | idem | mean reversion | finestra |
| `GridMeanReversionStrategy` | idem | griglia | passo, livelli |
| `RegimeConditionalStrategy` | idem | meta | mappa regime→strategia |
| `CompositeSignalStrategy` | idem | meta | combinazione da `SignalCatalog` |
| `EventTriggerStrategy` | idem | evento | trigger da `MarketEventDetector` |
| `MlStrategy` (193) | idem | ML | predittore + soglia; usata anche dal Champion |

`SignalCatalog.cs` (394) è il vocabolario dei segnali componibili usato da `StrategyComposer`.

> 🔴 **Rischio di classe, già misurato e documentato:** dieci ondate di ricerca hanno dato esito
> negativo sul **direzionale-tecnico** (vedi `docs/REPORT-PERCHE-NON-CONSOLIDA-2026-07-28.md` e
> `procione-mgr-roadmap-profitto-intraday`). Gli unici edge positivi misurati sono **carry** e
> forward test Paper. Il catalogo strategie è quindi ampio ma appartiene in massima parte alla
> classe che non ha funzionato. Va detto in ogni ricostruzione: non è un problema di codice.

---

## G. Portfolio

### G1 · Hierarchical Risk Parity — **l'unico che arriva all'operatività**
- **File:** `Portfolio/HierarchicalRiskParityOptimizer.cs` (82) → `Optimize(returnsBySymbol, config)`
- **Pseudocodice:** distanza da correlazione √(0,5(1−ρ)) → clustering gerarchico → quasi-diagonalizzazione → bisezione ricorsiva con pesi inversi alla varianza
- **Dipendenze:** `IHierarchicalClustering`
- **Integrazione:** `/portfolio` **+** `DecisionStages.cs:90` ⇒ pesi delle gambe dell'ensemble
- **Stato:** `IMPL`

### G2 · Mean-Variance — `DISC` dall'operatività
- **File:** `Portfolio/MeanVarianceOptimizer.cs` (53); obiettivi in `MeanVarianceObjective` (Max Sharpe, Min Var); stimatori di covarianza in `CovarianceEstimator` (incl. shrinkage Ledoit-Wolf, `PortfolioShrinkageErcTests`)
- **Rischio noto:** la MV è notoriamente instabile su medie stimate ⇒ lo shrinkage è necessario, ed è presente
- **Integrazione:** **solo** `/portfolio`

### G3 · Risk Parity / ERC — `DISC` dall'operatività
- **File:** `Portfolio/RiskParityOptimizer.cs` (37), `RiskParityMethod` · **Integrazione:** solo `/portfolio`

---

## H. Volatilità e serie storiche

### H1 · GARCH(1,1)
- **File:** `TimeSeries/GarchModel.cs` (119) → `Fit(returns, innovation = Gaussian)`; `GarchInnovation` supporta anche code grasse
- **Formula:** σ²ₜ = ω + α·ε²ₜ₋₁ + β·σ²ₜ₋₁ · **Output:** `GarchFit` (ω, α, β, previsione)
- **Integrazione:** `/volatility`, `VolatilityRegimeStage`, `RegimeChangeDetector`
- ⚙️ **Fuori dal position sizing per decisione dichiarata** (vedi `VolatilityScaler`)

### H2 · HAR-RV
- **File:** `TimeSeries/HarRvForecaster.cs` (159) + `RealizedVariance`
- **Formula:** RV̂ₜ₊₁ = β₀ + β_d·RV_d + β_w·RV_w + β_m·RV_m (finestre giorno/settimana/mese), con opzione **log-RV**
- **Integrazione:** `AnalysisStages.cs:295` · **Causalità:** verificata da `HarRvForecasterTests` (troncando la serie, le previsioni passate non cambiano) · **Stato:** `IMPL`

### H3 · Valutazione delle previsioni di volatilità
- **File:** `ML/VolForecastEvaluator.cs` (135) — QLIKE e affini · **Stato:** `IMPL`

### H4 · Cointegrazione di Engle-Granger
- **File:** `TimeSeries/EngleGrangerCointegrationTest.cs` (196) → `CointegrationResult`
- **Metodo:** regressione OLS fra le due serie (**su log-prezzi** — correzione documentata) + ADF sui residui
- **Stato:** `IMPL`

### H5 · Dosaggio sulla volatilità realizzata
- **File:** `Trading/Internal/VolatilityScaler.cs` (105)
- **Formula:** moltiplicatore = clamp( volTarget / volRealizzata_annualizzata, `MinExposureMultiplier`, `MaxExposureMultiplier` )
- **Proprietà di sicurezza:** `MaxExposureMultiplier = 1,0` di default ⇒ **può solo ridurre**, mai superare i limiti validati a `StartAsync`
- **Onestà dichiarata nel codice:** su singolo simbolo batte l'esposizione costante in **2 casi su 12**. Tenuto come manopola di drawdown.
- **Stato:** `IMPL`

---

## I. Pairs trading

### I1 · Hedge ratio rolling OLS — `RollingPairsSpreadAnalyzer.cs` (100)
### I2 · Hedge ratio con filtro di Kalman
- **File:** `PairsTrading/KalmanPairsSpreadAnalyzer.cs` (118) — `DefaultDelta`
- **Metodo:** β trattato come stato latente con rumore δ; aggiornamento ricorsivo ⇒ hedge ratio adattivo
- **Selezione:** `PairsBacktestEngine.cs:39` sceglie fra i due tramite `PairsHedgeRatioEstimator`
- **Invariante:** i due **devono** standardizzare lo spread allo stesso modo (`PairsSpreadSeries.cs`)
- **Stato:** `IMPL`

### I3 · Motore di backtest pairs — `PairsBacktestEngine.cs` (284), z-score entry/exit, `PairsTrade`, `PairsBacktestResult` · `IMPL`

---

## L. Rischio e sizing

### L1 · Kelly
- **File:** `Risk/KellyCalculator.cs` (228)
- **Metodi:** `BinaryKelly(p, b)` = p − (1−p)/b · `ContinuousKelly(μ, σ)` = μ/σ² · `ContinuousKellyNumeric(μ, σ, maxFraction=2.0)` · `EmpiricalKelly(returns)` (dalla distribuzione reale, cattura le code, **di norma ≤ del binario**) · `FromTradeHistory(trades)` → `KellySuggestion`
- **Integrazione:** `BacktestPageService`, `RiskSizingStage` (`ModelStages.cs:590`)
- **Rischio:** 🔴 Kelly pieno è aggressivo; con parametri stimati va frazionato. Presidiato da `AuditSafetyKellyExtremeTests`.
- **Stato:** `IMPL`

### L2 · Monte Carlo — `Risk/MonteCarloAnalyzer.cs` (210), `MonteCarloSamplingMode` (i.i.d. / a blocchi), `MonteCarloResult` · `IMPL`
### L3 · Equity/Performance control — `Risk/PerformanceControlService.cs` (212) → `EquityControlResult` · `IMPL` (solo backtest)
### L4 · Consulente leva — `Risk/LeverageAdvisor.cs` (140), bootstrap con liquidazione → `LeverageAdvice` · `IMPL` (solo backtest)
### L5 · Esposizione correlata fra corsie
- **File:** `Risk/CorrelatedExposureGuard.cs` (244) → `ICorrelatedExposureGuard`
- **Scopo:** impedisce che N corsie prendano di fatto la stessa scommessa; cache delle correlazioni **condivisa** fra corsie
- **Integrazione:** ✅ dentro `TradingEngine`, attivo (`Trading:CorrelatedExposure:Enabled=true`) · **Stato:** `IMPL`
### L6 · Profili di rischio — `Risk/RiskProfile.cs` (188): Prudente/Equilibrato/Dinamico → `LaneSafetyMonitor` · `IMPL`

---

## M. Esecuzione

| ID | Algoritmo | File | Logica | Stato |
|---|---|---|---|---|
| M1 | Immediate | `Execution/ExecutionAlgorithms.cs` | tutto subito (default, comportamento storico) | `IMPL` |
| M2 | TWAP | idem | fette uguali nel tempo | `IMPL` |
| M3 | VWAP | idem | fette pesate sul profilo di volume | `IMPL` |
| M4 | Iceberg | idem | fetta visibile piccola, resto nascosto | `IMPL` |
| M5 | Adaptive | idem | fette adattate alle condizioni | `IMPL` |
| M6 | Simulatore di fill | `Execution/ExecutionSimulator.cs` (89) | fill con `MarketImpactModel` | `IMPL` |
| M7 | Impatto √ | `MarketImpactModel` | impatto ∝ √(quantità/ADV) — `ExecutionSquareRootImpactTests` | `IMPL` |
| M8 | Pianificatore fette | `Trading/Internal/ExecutionSlicePlanner.cs` (124) | **rivaluta `SafetyChecker` sull'aggregato** (riga 54) prima di affettare | `IMPL` 🔒 |
| M9 | Qualità di esecuzione | `Trading/Internal/ExecutionQuality.cs` (61) | slippage realizzato vs atteso | `IMPL` |

---

## N. Sicurezza operativa

### N1 · SafetyChecker 🔒
- **File:** `Trading/SafetyChecker.cs` (122) → `SafetyChecker.Evaluate(order, status, config, timestamp)` → `SafetyCheckResult`
- **Natura:** **statico e puro** — non iniettabile, non mockabile, per progetto
- **Controlli (da `SafetyConfiguration`, 130 righe):** dimensione massima posizione · esposizione totale massima · drawdown massimo · perdita giornaliera massima · leva massima · anti-spam sui segnali (n.6, solo in apertura) · conferma manuale obbligatoria per Live
- **Call site:** `TradingEngine.cs:956`, `ExecutionSlicePlanner.cs:54`
- **Test:** `SafetyCheckerTests`, `SafetyCheckerLeverageTests`, `AuditSafetyKellyExtremeTests`

### N2 · FillSanityCheck — `Trading/Internal/FillSanityCheck.cs` (57): scarta i fill assurdi (bug B1, PnL −1,8M da testnet) · `IMPL`
### N3 · Invarianti di corsia — `LaneInvariantChecker.cs` (64) + `LaneInvariantWatchdog.cs` (265) → `LaneQuarantine` · `IMPL`
### N4 · Lease di esecuzione — `LaneExecutionLease.cs` (109), **advisory lock Postgres** per corsia · `IMPL`
### N5 · Sentinella d'ombra delle uscite
- **File:** `Trading/ProtectiveExitShadow.cs` (143), `ProtectiveExitLagAnalyzer.cs` (410)
- **Scopo:** con `DriveProtectiveExits=false` il tick **osserva** e non decide; si misura quanto anticiperebbe l'uscita senza dargliene il potere. `TradingEngine.cs:645` implementa il bivio.
- **Proprietà dichiarata:** l'osservazione **non tocca** `BestPriceSinceEntry`, altrimenti il feed sposterebbe il trailing "dalla porta di servizio". L'anticipo misurato è quindi un **limite inferiore**.
- **Stato:** `IMPL` — esempio di come si misura una feature prima di darle potere

---

## O. Drift

| ID | Rilevatore | File | Statistica | Stato |
|---|---|---|---|---|
| O1 | PSI | `Monitoring/Drift/PsiDriftDetector.cs` (63) | Population Stability Index: Σ (p_i − q_i)·ln(p_i/q_i) su bin | `IMPL` |
| O2 | KS | `KsDriftDetector.cs` (49) | Kolmogorov-Smirnov: max |F₁−F₂| | `IMPL` |
| O3 | Page-Hinkley | `PageHinkleyDetector.cs` (45) | test sequenziale sul cambio di media | `IMPL` |
| O4 | Aggregatore | `FeatureDriftMonitor.cs` (95) | combina i tre → `DriftResult`/`DriftSeverity` | `IMPL` |
| O5 | Decadimento | `Monitoring/StrategyDecayMonitor.cs` (246) | realizzato vs atteso dal backtest → `DecayReport` | `IMPL` |

Firma comune: `Detect(reference, current, thresholds)` → `DriftResult`.
Ciclo chiuso: `FeatureDriftWorker.cs:127` → `registry.RetireAsync(..., requestRetrain: true)`.
⚙️ `Drift:Enabled=false` ⇒ oggi non gira.

---

## P. Analisi statistica classica

| Algoritmo | File | Output | Integrazione |
|---|---|---|---|
| Gap/Lap | `Analysis/GapLapAnalyzer.cs` (325) | `GapLapReport`, `GapEvent` | `/market-analysis` |
| Escursioni | `Analysis/ExcursionAnalyzer.cs` (411) | `StopLossSuggestion`, `TakeProfitSuggestion`, `RiskBracket`, `ContinuationStats` | `/market-analysis` **+ auto SL/TP** (`AutoBracket`) |
| Ciclicità | `Analysis/CyclicalAnalyzer.cs` (310) | bias orario/settimanale, stagionalità | `/market-analysis` |
| Candlestick | `Analysis/CandlestickPatternDetector.cs` (313) | `CandlePattern` | `/market-analysis` |
| Supporti/resistenze | `Analysis/SupportResistanceAnalyzer.cs` (299) | `PriceLevel`, `SwingPoint`, `BreakoutEvent` | `/market-analysis` |
| Chart pattern | `Analysis/ChartPatternDetector.cs` (165) | `ChartPatternMatch` | `/market-analysis` |
| Volume | `Analysis/VolumeAnalyzer.cs` (67) | `VolumeConfirmation` | `/market-analysis` |
| Event study | `Analysis/EventStudy.cs` (187) | rendimenti anomali attorno a un evento, `Seed=42` | `NewsImpactAnalyzer`, DTW |
| Rilevatore eventi | `Analysis/MarketEventDetector.cs` (153) | `MarketEvent` | `SignalCatalog` |
| DTW | `Discovery/Dtw/DtwMatcher.cs` (244) + `DtwPatternAnalysisService.cs` (255) | `DtwMatch`, `ShapeMatchedNull` (nullo **per forma**) | `/market-analysis` |

> **Nota su `ExcursionAnalyzer`:** è la sorgente dell'auto SL/TP data-driven (percentili di escursione)
> usata da `AutoBracket` in pipeline e backtest. Uno dei pochi punti dove l'analisi statistica
> **arriva davvero** all'operatività.

---

## Q. Microstructure — `DISC`

| Algoritmo | File | Cosa calcola | Perché conta |
|---|---|---|---|
| OFI | `Microstructure/OrderFlowImbalance.cs` (100) | squilibrio del flusso ordini | misurato (D3, 2026-07-28) ma **senza il pilota C5** |
| Tape → barre | `TapeAggregator.cs` (110) | barre da trade aggregati | base per feature di microstruttura |
| Gate IC incrementale | `IncrementalIcGate.cs` (467) | il fattore aggiunge IC **oltre** ai presenti? `Seed = 20260728` | **il gate anti-ridondanza che manca al resto della piattaforma** |
| Dump Binance | `BinanceDumpDownloader.cs` (177) + `BinanceDumpParser.cs` (201) | tape e profondità storici | ⚠️ `bookTicker` **non disponibile** (404) |

**Tutti `DISC`:** zero DI, zero UI, unico consumatore `tools/PlatformExpand`.
`IncrementalIcGate` è la perdita più dolorosa: è esattamente il gate che servirebbe ad `AlphaFactorFactory`
con 158+ fattori candidati.

---

## R. Duplicazioni e codice sperimentale

| Tipo | Dove | Nota |
|---|---|---|
| `DUP` potenziale | `tools/PlatformExpand/Program.cs` (5.848 righe in un file) | riusa i servizi ma orchestra la ricerca **in parallelo** alla pipeline: due modi di fare la stessa cosa, senza contratto condiviso → G-09 |
| `EXP` | `OnnxSentimentScorer` + `OnnxSentimentPilotService` | pilota dichiarato |
| `EXP` | `AttentionReturnPredictor`, `StackedReturnPredictor` | oltre il mandato, ben testati |
| `DEAD` | `JumpModel`, `JumpModelFit`, `ICombinatorialPurgedCv` | |
| Astrazione a un solo uso | `IPortfolioOptimizer` con 3 impl. ma 1 sola in produzione | vedi C-05 |

---

## S. Riepilogo dei rischi metodologici

| Rischio | Dove si manifesta | Presidio attuale | Sufficiente? |
|---|---|---|---|
| Lookahead nei fattori | `RollingOps`, `FactorEvaluator` | causalità per costruzione + `AuditCvLeakageTests` | ✅ |
| Leakage di CV | training/validation | purge + embargo + CPCV | ✅ |
| **Leakage di selezione** | `IcFeatureSelector` su tutto il campione | — | ⚠️ **da verificare fold-per-fold** |
| Inflazione da prove multiple | Alpha158 (158), genetic miner, grid | DSR + `EffectiveTrials` | ✅ se le prove sono dichiarate oneste |
| Overfitting di composizione | `StrategyComposer` | screening + gate | ⚠️ |
| Falsa significatività da correlazione | randomizzazione su asset correlati | **errore già pagato e documentato** | ✅ per consapevolezza, ❌ come vincolo di codice |
| Deriva non sorvegliata | 158 fattori Alpha158 | nessuno | ❌ **G-04** |
| Determinismo incompleto | 3 `new Random(42)` cablati | seed per componente | ⚠️ **G-08** |
