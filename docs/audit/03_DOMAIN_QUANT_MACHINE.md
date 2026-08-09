# 03 — LA MACCHINA QUANTITATIVA

Un blocco per meccanismo. Per ciascuno: **esiste? · completo? · integrato? · usato dalla UI? ·
invocato dal runtime? · I/O chiari? · persistenza? · logging/tracking? · testato? · pronto per
ricostruzione?**

Legenda: ✅ sì · ⚠️ parziale · ❌ no · ⚙️ deliberatamente spento

---

## Quadro sinottico

| # | Meccanismo | Esiste | Completo | Integrato | UI | Runtime | Persist. | Test | Verdetto |
|---|---|---|---|---|---|---|---|---|---|
| 1 | Data ingestion OHLCV | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **riusare** |
| 2 | Normalization / alignment | ✅ | ✅ | ✅ | ⚠️ | ✅ | — | ✅ | riusare |
| 3 | Feature/factor generation | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **riusare** |
| 4 | Alpha158CSharp | ✅ | ✅ | ✅ | ✅ | ✅ | — | ✅ | riusare (⚠️ deriva non sorvegliata) |
| 5 | IC / factor analysis | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **riusare** |
| 6 | ML training/validation/inference | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **riusare** |
| 7 | Linear / RF / LightGBM / MLP | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | riusare (⚠️ naming) |
| 8 | Purged CV | ✅ | ✅ | ✅ | ⚠️ | ✅ | — | ✅ | riusare |
| 9 | Walk-forward | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | riusare |
| 10 | Regime detection | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | riusare (⚠️ `JumpModel` morto) |
| 11 | Strategy discovery | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | riusare |
| 12 | Ensemble regime-aware | ✅ | ✅ | ⚠️ | ✅ | ✅ | ✅ | ✅ | riusare (routing ⚙️ off) |
| 13 | Portfolio MV/RP/HRP | ✅ | ✅ | ⚠️ | ✅ | ⚠️ | ⚠️ | ✅ | **consolidare** |
| 14 | GARCH | ✅ | ✅ | ⚠️ | ✅ | ✅ | ❌ | ✅ | riusare (sizing ⚙️ no) |
| 15 | Pairs trading | ✅ | ✅ | ✅ | ✅ | ✅ | ⚠️ | ✅ | riusare |
| 16 | Sentiment / news | ✅ | ✅ | ⚠️ | ✅ | ✅ | ✅ | ✅ | consolidare |
| 17 | Backtest engine | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **riusare** |
| 18 | Risk engine / Kelly | ✅ | ✅ | ✅ | ✅ | ✅ | ⚠️ | ✅ | riusare |
| 19 | Execution engine | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **riusare** |
| 20 | Nested execution | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | riusare |
| 21 | SafetyChecker | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **riusare invariato** |
| 22 | Experiment tracker | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | riusare |
| 23 | Concept drift | ✅ | ✅ | ✅ | ✅ | ⚙️ | ✅ | ✅ | riusare (accendere) |
| 24 | Scheduler / supervisor AI | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | riusare |
| 25 | Auto-promozione Paper→Testnet | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | **riusare invariato** |
| 26 | Microstructure (OFI/tape/IcGate) | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ✅ | **integrare o eliminare** |

---

## 1. Data ingestion OHLCV

**Dove:** `Services/Ingestion/` (10 file, 902 righe) · `Services/Exchanges/` (12 file, 2.260 righe)
**Tipi:** `IOhlcvIngestionService`, `IMarketDataSyncService` → `MarketDataSyncService` |
`RemoteMarketDataSyncService`, `MarketDataSyncWorker`, `SeriesFreshnessWatchWorker`, `BarBuilder`,
`ExchangeClockSyncWorker`, `IExchangeClientFactory` (Binance, Bitget)

| Domanda | Risposta |
|---|---|
| Integrato | ✅ È la radice di tutto: `OhlcvData` alimenta ogni modulo a valle |
| UI | ✅ `/market/watchlist` (gestione serie), `/market/bars` (barre a volume/dollaro) |
| Runtime | ✅ `MarketDataSyncWorker` periodico + `SeriesFreshnessWatchWorker` |
| I/O | in: `TrackedSeries` (symbol, timeframe) → out: `OhlcvData` |
| Persistenza | ✅ `OhlcvData` (~12M candele, 221 serie secondo l'audit precedente) |
| Logging | ✅ + notifica sulla transizione a "ferma" |
| Test | ✅ `AddOhlcvIngestionTests`, `AuditStressIngestionTests`, `SeriesFreshnessTests`, `KlineExtendedFieldsTests`, `ExchangeRateLimitAndClockTests` |

**Nota di progetto non ovvia:** `SeriesFreshnessWatchWorker` sta **sempre nel guscio**, anche con
ingestione remota, perché «è il sync l'imputato: la guardia non può stargli in casa»
(`Program.cs:125-129`). Rimedio a un incidente reale: una serie ferma dieci mesi con solo un
`LogWarning` che nessuno leggeva.

**Pronto per ricostruzione:** sì, riusare.

---

## 2. Normalization / cleaning / alignment

**Dove:** `Services/Portfolio/ReturnMatrixBuilder.cs` (allineamento multi-serie),
`Services/PairsTrading/PairsCandleAligner.cs`, `Services/Regime/MarketFeatures.cs`
(`FeatureNormalizer`, `FeatureScaling`), `Services/Alpha/Alpha158/RollingOps.cs` (causalità).

Non esiste un modulo unico "cleaning": la normalizzazione è **locale a ciascun consumatore**. È una
scelta difendibile (ogni consumatore sa cosa gli serve) ma significa che **non c'è un punto unico
dove verificare le assunzioni sui dati**. Vedi G-11.

---

## 3-4. Feature/factor generation e Alpha158CSharp

**Dove:** `Services/Alpha/` (14 file, 2.831 righe)
**Punto di innesto unico:** `AlphaFactorFactory.cs:60`
```
_basePrototypes = [.. HandwrittenPrototypes, .. Alpha158Catalog.BuildCatalog()];
```
⇒ i fattori Alpha158 **sono** nel catalogo prototipi, quindi disponibili ovunque si usi la factory:
`DatasetBuilder`, `IcFeatureSelector`, `MlStrategy`, `FeatureEngineeringStage`, `/feature-selection`.

`Alpha158Catalog` (172 righe) genera il catalogo per composizione (`OpDescriptor`: pochi operatori ×
molti orizzonti) sopra `RollingOps` (518 righe, operatori rolling **causali**).
`AlphaFactorFactory.TryCreate` ricostruisce un fattore **dal nome** a qualsiasi orizzonte ⇒ il
round-trip di salvataggio/ricarica funziona senza serializzare la definizione.

| Domanda | Risposta |
|---|---|
| UI | ✅ `/feature-selection` con checkbox «Includi catalogo Alpha158 (N fattori, più lento)» |
| Runtime | ✅ via pipeline e training |
| Persistenza | ✅ `SavedFactor`, `FactorIcWindow` |
| Test | ✅ `Alpha158FactorTests`, `AuditAlpha158EdgeCaseTests`, `AlphaFactorTests`, `OrderFlowFactorsTests` |

⚠️ **Gap dichiarato (G-04):** `FactorDriftMonitor.cs:105` esclude **di proposito** i fattori Alpha158
dal monitor di deriva, per costo (158 × N serie × finestre). Conseguenza: **la deriva dei 158 fattori
non è sorvegliata da nulla**. È una decisione motivata, ma il costo va reso esplicito nella UI.

---

## 5. IC / factor analysis

`FactorEvaluator` (192) → IC, IR, rendimenti per quantile, correlazioni.
`IcFeatureSelector` (93) → selezione per IC con soglie (`minAbsIc`, `minIr`, coerenza di segno).
`FactorDriftAnalyzer` (281) → deriva e persistenza dell'IC nel tempo.
`FactorIcHistoryStore` (298) → storia su tabella, così la Home la ritrova dopo un riavvio.

Integrazione: ✅ `/feature-selection`, Home (alert di deriva), `FeatureEngineeringStage`,
`FactorDriftWorker` (job). Persistenza ✅. Test ✅ (`IcFeatureSelectorTests`, `FactorIcTStatTests`,
`FactorDriftAnalyzerTests`, `FactorDriftMonitorTests`).

---

## 6-7. ML: training, validazione, inferenza — Linear / RF / LightGBM / MLP

**Dove:** `Services/ML/` (37 file, 5.234 righe)

| Predittore | File | Implementazione reale | Nota |
|---|---|---|---|
| Linear | `LinearReturnPredictor.cs` (20) | OLS/ridge | |
| Random Forest | `RandomForestReturnPredictor.cs` (32) | ML.NET **FastForest** | |
| "LightGBM" | `GradientBoostingReturnPredictor.cs` (36) | ML.NET **FastTree** | ⚠️ **non è LightGBM**: è gradient boosting ML.NET. Vedi G-12 |
| MLP | `MlpReturnPredictor.cs` (298) | rete scritta a mano | |
| Attention | `AttentionReturnPredictor.cs` (559) | attention su finestre | extra rispetto al mandato |
| Stacked | `StackedReturnPredictor.cs` (428) | stacking con ridge non-negativa | extra |

Catena: `DatasetBuilder` (fattori + `RegimeAugmentation` one-hot) → `PurgedTimeSeriesCv` →
predittore → `IShapExplainable` → `TreeShapExplainer`/`ShapAnalyzer`.
**Meta-labeling** completo: `TripleBarrierLabeler` → `MetaLabeler` → `MetaModelTrainer` →
`MetaLabelingAnalysisService` (dietro `/backtest`).

| Domanda | Risposta |
|---|---|
| UI | ✅ `/ml` (`MlLabService`, 808 righe), `/registry`, `/experiments` |
| Runtime | ✅ `MlModelTrainingStage`; Champion eseguito dal motore |
| Persistenza | ✅ `SavedMlModel` + `ModelRegistry` con gate DSR |
| Determinismo | ✅ `MlDeterminismTests` esiste |
| Test | ✅ 15+ file dedicati |

---

## 8. Purged CV

`PurgedTimeSeriesCv.cs` (41 righe) — CV temporale con **purge** ed **embargo**.
`CombinatorialPurgedCv.cs` (106) — CPCV, usata da `OptimizationEngine` e `BacktestOverfitting` (PBO).

⚠️ **G-07:** l'interfaccia `ICombinatorialPurgedCv` **non è registrata in DI e nessuno la usa**: si
lavora sulla classe concreta. Astrazione morta, da rimuovere o onorare.

Test: ✅ `PurgedTimeSeriesCvTests`, `AuditCvLeakageTests` (esplicitamente contro il leakage),
`OptimizationEmbargoTests`, `OptimizationCpcvTests`.

---

## 9. Walk-forward

`OptimizationEngine.cs` (755) — grid search + walk-forward + CPCV;
`Bayesian/BayesianOptimizationEngine.cs` (205) — surrogato GP + Expected Improvement.
UI ✅ `/optimization` con `OptimizationPageService` (538) e heatmap. Persistenza ✅ via experiment
tracker. Test ✅ (`OptimizationSearchStrategyTests`, `BayesianOptimizerTests`, `BayesianKernelFitTests`).

---

## 10. Regime detection

**In esercizio:** `RegimeDetector.cs` (548) — K-means sui feature estratti da
`MarketFeatureExtractor` (215) + `MarketBreadthCalculator`. Persistito in `RegimeModel`,
riaddestrato da `RegimeRetrainingWorker`.
Consumatori: `EnsembleManager`, `RegimeAugmentation` (one-hot nel dataset), `RegimeConditionalStrategy`,
`RegimeChangeDetector` (trigger), `LaneRegimeRouter`, `/regimes`.

🔴 **`JumpModel.cs` (288 righe) è orfano.** Modello di regime con penalità di salto (riduce le
transizioni spurie rispetto al K-means puro), completo, con `JumpModelTests` che ne verificano proprio
la superiorità sul K-means quanto a stabilità. **Zero riferimenti in produzione.** `JumpModelFit` non
è referenziato neppure dai test.
→ **È il caso più netto di ricerca fatta e mai raccolta.**

---

## 11. Strategy discovery

`StrategyDiscoveryEngine.cs` (211) — sweep strategia × coppia × timeframe.
`StrategyComposer.cs` (556) — composizione **sistematica**: `CompositeSignalGenerator`,
`EventTriggerGenerator`, `RegimeMapGenerator` (generatori deterministici, Singleton).
`GeneticAlphaMiner.cs` (437) — programmazione genetica su espressioni alpha (`AlphaNode`,
`AlphaExpressionParser`), con gate CV (`GeneticMinerCvGateTests`).
UI ✅ `/discovery`, `/alpha-mining`. Runtime ✅ `StrategyDiscoveryStage`, `CreativeDiscoveryStage`.
Tracking ✅ entrambe le pagine chiamano `IExperimentTracker`.

---

## 12. Ensemble regime-aware

`EnsembleManager.cs` (642) — una istanza **keyed per corsia**; riceve `IRegimeDetector`,
`IMarketFeatureExtractor`, `IStrategyDecayMonitor` e la **fee viva del motore** (non più 0,1% fisso —
correzione G3 del 2026-07-31: i pesi si calcolano sui costi che si pagano davvero).
`EnsembleAllocator` (152) alloca; `EnsembleComparator` (241) decide con **isteresi** se un nuovo
ensemble è meglio del corrente; `EnsembleRebalanceWorker` ribilancia (solo nel monolite: è uno
scrittore).

⚠️ Il **routing per regime** (`LaneRegimeRouter`, 265 righe) è registrato e attivo
(`RegimeRouting:Enabled=true`) ma **non decide** (`DriveDecisions=false`): classifica e basta.
**È deliberato** (regola 7 di `CLAUDE.md`), non un difetto.

---

## 13. Portfolio construction: MV / Risk Parity / HRP

| Optimizer | File | Raggiunge l'operatività? |
|---|---|---|
| **HRP** | `HierarchicalRiskParityOptimizer.cs` (82) | ✅ `DecisionStages.cs:90` → pesi delle gambe dell'ensemble → `PipelineApplier` → `EnsembleState` → motore |
| Mean-Variance | `MeanVarianceOptimizer.cs` (53) | ❌ solo `/portfolio` |
| Risk Parity / ERC | `RiskParityOptimizer.cs` (37) | ❌ solo `/portfolio` |

Supporto: `PortfolioMath` (218, include shrinkage Ledoit-Wolf secondo `PortfolioShrinkageErcTests`),
`ReturnMatrixBuilder` (94), `HierarchicalClustering` + `CorrelationDistance` per l'HRP.

🟡 **Gap C-05.** L'operatore confronta quattro allocazioni in `/portfolio` (Max Sharpe, Min Var, ERC,
HRP) e **non può applicarne nessuna**: l'unico percorso verso l'operatività passa dalla pipeline e
usa HRP a codice fisso. È il classico "controllo che rassicura senza cambiare la realtà".

---

## 14. GARCH

`GarchModel.cs` (119) + `GarchFit.cs` (67) — GARCH(1,1). `HarRvForecaster.cs` (159) — HAR-RV
(log-HAR, usato da `AnalysisStages.cs:295`). `VolForecastEvaluator` (135) — QLIKE.
Consumatori: `/volatility`, `VolatilityRegimeStage`, `RegimeChangeDetector` (banda di vol).

⚙️ **GARCH NON entra nel position sizing, ed è deliberato.** `VolatilityScaler` usa la vol
**realizzata**; il doc-comment dichiara: *«La piattaforma ha anche un GARCH(1,1) in `/volatility`,
che è una previsione e sarebbe plausibilmente migliore, ma non è stato validato per questo uso:
usarlo qui significherebbe schierare qualcosa che nessuno ha misurato.»*
→ **Non è un gap: è disciplina.** Va tenuto, non "corretto".

Il `VolatilityScaler` stesso è documentato con onestà rara: il miglioramento di Sharpe 0,12→0,43 è su
un paniere di 24 monete, ma **su singolo simbolo — che è il caso di una corsia — batte l'esposizione
costante solo in 2 casi su 12**. È tenuto come manopola di drawdown, non come fonte di rendimento.

---

## 15. Pairs trading

`PairsBacktestEngine.cs` (284) con due stimatori dell'hedge ratio:
- rolling OLS (`RollingPairsSpreadAnalyzer`)
- **filtro di Kalman** (`KalmanPairsSpreadAnalyzer`, scelto a `PairsBacktestEngine.cs:39`)

`EngleGrangerCointegrationTest.cs` (196) — cointegrazione **su log-prezzi** (correzione documentata).
UI ✅ `/pairs-trading`. Runtime ✅ `PairsScreeningStage`.
⚠️ Persistenza parziale: i risultati vivono negli artefatti di pipeline, non in una tabella dedicata.

---

## 16. Sentiment / news

Tre livelli:
1. **News** (`Services/AltData/`): RSS + ForexFactory (calendario) + retail sentiment (FXSSI,
   MyFxBook) → `AltDataPoint`; `NewsImpactAnalyzer` + `NewsImpactClassifier` misurano l'impatto sul
   prezzo (con `EventStudy`).
2. **Scoring** (`Services/Sentiment/`): `ISentimentScorer` con tre implementazioni dietro un
   delegante hot-reload — `Keyword` (default, costo zero), `Llm`, `Onnx` (pilota). Ognuno dei
   non-lessicali **ripiega da solo** sul lessicale.
3. **Metriche di mercato**: `FearGreedClient` + `BinanceFuturesSentimentClient` →
   `SentimentMetricPoint`; `SentimentCompositeCalculator` produce uno z-score composito.

Consumatori reali: `/sentiment`, `NewsImpactCheckStage`, **`FundingHistoryProvider`** (funding per i
backtest futures) e **`CarryWorker`**.

⚠️ **Gap C-06:** `SentimentFeatureFactor` → `AlphaFactorFactory.cs:70`, ma gated da
`Sentiment:EnableMlFeature` = **`false`**. La catena sentiment→feature ML esiste e non gira.

---

## 17. Backtest engine

`BacktestEngine.cs` (692) + 14 strategie + `SignalCatalog` (394).
Contabilità dei costi verificata da test dedicati (`BacktestCostAccountingTests`,
`CostPropagationTests`, `MakerFillModelTests`, `ExecutionSquareRootImpactTests` — impatto √).
Stop-loss/leva testati (`BacktestStopLossTests`, `BacktestLeverageTests`).
UI ✅ `/backtest` via `BacktestPageService` (484). Tracking ✅.

---

## 18. Risk engine / Kelly

| Componente | File | Consumatore |
|---|---|---|
| `KellyCalculator` (228) | Kelly binario + **empirico** (code grasse, ≤ del binario) | `BacktestPageService`, `ModelStages.cs:590` (RiskSizingStage) |
| `MonteCarloAnalyzer` (210) | Monte Carlo evoluto, modalità di campionamento | `BacktestPageService`, `ModelStages.cs:504` |
| `PerformanceControlService` (212) | Equity control | **solo** `BacktestPageService` |
| `LeverageAdvisor` (140) | Consulente leva con liquidazione | **solo** `BacktestPageService` |
| `CorrelatedExposureGuard` (244) | Limite di esposizione **correlata fra corsie** | ✅ `TradingEngine` (attivo, `Enabled=true`) |
| `RiskProfile` / `RiskProfiles` (188) | Profili Prudente/Equilibrato/Dinamico | ✅ `/bot`, `LaneSafetyMonitor` |
| `MarginMath` (45) | Matematica del margine | `PositionOpener` |

---

## 19-20. Execution engine e nested execution

`Services/Execution/` (5 file): `IExecutionAlgorithm` con 5 implementazioni — `Immediate` (default,
riproduce il comportamento storico), `Twap`, `Vwap`, `Iceberg`, `Adaptive` — più
`ExecutionSimulator` (fill simulati) e `MarketImpactModel`.

**Cablaggio verificato:** `TradingEngine` riceve `IExecutionAlgorithmFactory` nel costruttore
(riga 46) e pianifica via `ExecutionSlicePlanner.TryBuildAndStartExecutionPlanAsync`
(`TradingEngine.cs:1070`), che **rivaluta `SafetyChecker` sull'ordine aggregato** prima di affettare
(`ExecutionSlicePlanner.cs:54`). Le fette diventano `ExecutionJob` su DB, eseguite da
`ExecutionWorker`.

UI ✅ `/execution` (Execution Lab) con confronto fra algoritmi e pannello "Modello di costo"
**hot-reload** (`IOptionsMonitor<ExecutionParameters>` — scelta esplicita: con un POCO catturato al
boot il pannello «sembrerebbe funzionare e non cambierebbe nulla»).
Test ✅ `ExecutionTests`, `AuditStressNestedExecutionTests`, `ExecutionQualityTests`.

---

## 21. SafetyChecker 🔒

`SafetyChecker.cs` (122 righe) — **statico e puro**, non iniettabile né mockabile *per progetto*
(regola 1 di `CLAUDE.md`; `Program.cs:141-142` dichiara che l'interfaccia istanza era codice morto,
rimosso).

**Call site — esattamente due, entrambi sul percorso che apre esposizione:**
1. `TradingEngine.cs:956` — ogni ordine
2. `ExecutionSlicePlanner.cs:54` — ordine aggregato prima dell'affettamento

Difese complementari:
- `FillSanityCheck` (fill patologici, bug B1: PnL −1,8M da testnet)
- `LaneInvariantWatchdog` + `LaneQuarantine` (invarianti contabili)
- `LaneExecutionLease` (advisory lock Postgres: un solo esecutore per corsia)
- `LaneSafetyMonitor` (soglie **effettive** per corsia, sostituisce il monitor globale ovunque)
- `MasterKeyProbe` (all'avvio dichiara a voce alta se la master key non decifra)
- Gate Live: conferma manuale obbligatoria (`RequireManualConfirmationForLive=true`)

⚠️ **Annotazione onesta:** `PositionCloser.cs:18` dichiara che la chiusura salta l'anti-spam n.6,
attivo solo in apertura. È corretto (chiudere riduce il rischio), ma va scritto nel blueprint per
non essere "scoperto" in futuro.

---

## 22. Experiment tracker

`ExperimentTracker.cs` (109) + `ExperimentTrackerExtensions.cs` (metodi `Safe*` che non fanno
fallire il run se il tracking fallisce — **fail-open sulla diagnostica**, regola 4).
Sette punti di aggancio: `/alpha-mining`, `/discovery`, `/execution`, `BacktestPageService`,
`MlLabService`, `OptimizationPageService`, `PipelineEngine.cs:440-490`.
Persistenza ✅ `ExperimentRun` + `ExperimentArtifact`. UI ✅ `/experiments`.
Test ✅ `ExperimentTrackerTests`, `AuditPipelineExperimentLoggingTests`.

---

## 23. Concept drift

Tre rilevatori registrati come `IEnumerable<IFeatureDriftDetector>`: **PSI**, **KS**,
**Page-Hinkley**. `FeatureDriftMonitor` li aggrega; `FeatureDriftWorker` gira periodicamente.

**Ciclo chiuso verificato** (`FeatureDriftWorker.cs:122-129`):
```
alert ≥ MinAlertsToRetire  ⇒  registry.RetireAsync(model.Id, reason, requestRetrain: true)
                           ⇒  metrics.RecordModelRetired(...)
```
Affiancato dal monitor **reattivo** `StrategyDecayMonitor` (realizzato vs atteso dal backtest).

⚙️ `Drift:Enabled = false` di default ⇒ **oggi il ciclo non gira**. Il codice è pronto: è una
decisione di attivazione, non di sviluppo.

---

## 24. Scheduler e supervisore AI

`PipelineSchedulerWorker` (392) — schedulazione + **auto-reapply** (`AutoReapply:Enabled=true`).
`RunApplyEvaluator` (171) — la catena **condivisa** fra scheduler e campaign planner:

```
1. Supervisore AI  → può SOLO porre un veto (errore o assenza ⇒ approva)
2. EnsembleComparator + isteresi → il nuovo è oggettivamente meglio?
3. PipelineApplier → scrive EnsembleState
```
Un veto è raro e registrato una-volta-per-run (`metrics.RecordLlmVeto()`).

Layer AI: 5 provider dietro `DelegatingLlmClient` con failover verificato dal vivo, `LlmCallGuard`
(breaker + retry + budget per provider), `AiCommittee` a **menù chiuso** (risposta fuori menù =
astensione; quorum mancato = default deterministico).

🔒 **Invariante verificata:** nessun servizio di esecuzione è iniettato in `Services/Llm/`.
Il layer AI dà pareri e veti, non esegue.

---

## 25. Auto-promozione Paper → Testnet

`PromotionEvaluator` (273, logica pura) → `LanePromoter` (agisce: stop→restart della corsia) →
`PromotionWorker` (ogni 6 h).

Criteri di promozione: Sharpe realizzato ≥ 0,8 · ≥ 30 trade · DD ≤ 15% · ≥ 3 settimane · win rate ≥ 45%.
Reversibilità: retrocessione Testnet→Paper se Sharpe < 0,5 per ≥ 2 settimane.
Retrocessione **di sicurezza** Live→Testnet: opt-in (`false`) e in **dry-run** (`true`) per default.

🔒 **Verificato: non esiste alcun percorso automatico verso Live.**
Test ✅ `PromotionEvaluatorTests`, `AuditPromotionStateMachineTests`, `LanePromotionFlattenTests`,
`DormantFleetPromotionTests`.

---

## 26. Microstructure — 🔴 l'isola

**Dove:** `Services/Microstructure/` (6 file, 1.166 righe)

| File | Cosa fa |
|---|---|
| `IncrementalIcGate.cs` (467) | Il fattore aggiunge IC **oltre** a quelli già in uso? Gate incrementale |
| `BinanceDumpDownloader.cs` (177) | Scarico dai dump storici `data.binance.vision` |
| `BinanceDumpParser.cs` (201) | Parsing tape/profondità (tre trappole di formato già risolte) |
| `TapeAggregator.cs` (110) | Tape → barre |
| `OrderFlowImbalance.cs` (100) | OFI |
| `MicrostructureModels.cs` (111) | `AggTrade`, `TapeBar`, `BestQuote`, `BookDepthSnapshot` |

| Domanda | Risposta |
|---|---|
| Esiste / completo | ✅ / ✅ |
| **Testato** | ✅ 5 file: `IncrementalIcGateTests`, `TapeAggregatorTests`, `OrderFlowImbalanceTests`, `MicrostructureParserTests`, `BinanceDumpDownloaderTests` |
| **Registrato in DI** | ❌ **zero** occorrenze in `Program.cs` e in ogni `*ServiceCollectionExtensions.cs` |
| **Usato dalla UI** | ❌ **zero** riferimenti in `Components/` |
| **Invocato dal runtime** | ❌ nessun hosted service, nessuno stage di pipeline |
| Unico consumatore | `tools/PlatformExpand/Program.cs` (CLI) |
| Persistenza | ❌ nessuna entità dedicata |

**Verdetto: integrare o eliminare.** È lavoro finito e verificato che la piattaforma non può usare.
Tenerlo così è il peggiore dei due mondi: costa manutenzione e non rende nulla.

---

## Sintesi per la ricostruzione

| Categoria | Moduli | Azione |
|---|---|---|
| **Riusare invariati** | SafetyChecker, promozione, execution/nested, backtest, ingestion, IC/fattori, ML, tracker, drift | nessuna modifica |
| **Consolidare** | Portfolio (3 optimizer → 1 percorso), Sentiment (accendere la feature ML), Alpha158 (sorvegliarne la deriva) | lavoro contenuto |
| **Decidere** | Microstructure, JumpModel | integrare o eliminare |
| **Non toccare** | GARCH fuori dal sizing, `DriveDecisions=false`, Fleet in DryRun, Committee off | sono misure, non sviste |
| **Correggere** | `DriveProtectiveExits` default, `ICombinatorialPurgedCv`, segreti tracciati | difetti veri |
