# 08 — MATRICE DI TRACCIABILITÀ

**dominio → file/classi → stato → gap → azione**, per tutti i domini dichiarati nel mandato.

**Stato:** ✅ completo e integrato · ⚙️ completo, spento per decisione · ⚠️ parziale · ❌ non integrato
**Azione:** `riusare` · `consolidare` · `decidere` · `correggere` · `non toccare`

---

## Matrice principale

| # | Dominio | File / classi principali | UI | Runtime | Persistenza | Test | Stato | Gap | Azione |
|---|---|---|---|---|---|---|---|---|---|
| 1 | **OHLCV** | `Services/Ingestion/MarketDataSyncService`, `MarketDataSyncWorker`, `SeriesFreshnessWatchWorker`, `BarBuilder`, `Data/OhlcvData` | `/market/watchlist`, `/dashboard`, `/market/bars` | `MarketDataSyncWorker` | `OhlcvData`, `TrackedSeries` | `AddOhlcvIngestionTests`, `AuditStressIngestionTests`, `SeriesFreshnessTests`, `BarBuilderTests` | ✅ | G-11 (qualità dati) | consolidare |
| 2 | **Alpha158** | `Alpha/Alpha158/Alpha158Catalog` (172), `RollingOps` (518), innesto in `AlphaFactorFactory.cs:60` | `/feature-selection` (checkbox) | pipeline, training | `SavedFactor` | `Alpha158FactorTests`, `AuditAlpha158EdgeCaseTests` | ✅ | **G-04** deriva non sorvegliata; **G-16** nessun gate incrementale | consolidare |
| 3 | **IC** | `Alpha/FactorEvaluator` (192), `ML/IcFeatureSelector` (93), `Alpha/FactorDriftAnalyzer` (281), `FactorIcHistoryStore` (298) | `/feature-selection`, Home | `FactorDriftWorker`, `FeatureEngineeringStage` | `FactorIcWindow` | `IcFeatureSelectorTests`, `FactorIcTStatTests`, `FactorDriftAnalyzerTests` | ✅ | **G-15 leakage di selezione (da verificare)** | correggere |
| 4 | **ML** | `ML/DatasetBuilder`, `RegressionPredictorBase`, `ReturnPredictorCatalog`, `MlModelLoader`, `MlLabService` (808) | `/ml`, `/registry` | `MlModelTrainingStage` | `SavedMlModel` | 15+ file | ✅ | G-08 determinismo, G-12 naming | consolidare |
| 5 | **Purged CV** | `ML/PurgedTimeSeriesCv` (41), `Validation/CombinatorialPurgedCv` (106) | indiretta | training, ottimizzazione | — | `PurgedTimeSeriesCvTests`, `AuditCvLeakageTests`, `OptimizationEmbargoTests`, `OptimizationCpcvTests` | ✅ | **G-07** interfaccia morta | correggere |
| 6 | **Walk-forward** | `Optimization/OptimizationEngine` (755), `WalkForwardConfiguration`, `Bayesian/BayesianOptimizationEngine` (205) | `/optimization` | `OptimizationPageService` | `ExperimentRun` | `OptimizationSearchStrategyTests`, `BayesianOptimizerTests` | ✅ | — | riusare |
| 7 | **Regime detection** | `Regime/RegimeDetector` (548, K-means), `MarketFeatureExtractor` (215), `RegimeAssignment`, `RegimeAugmentation`, **`JumpModel` (288)** | `/regimes` | `RegimeRetrainingWorker`, `RegimeAnalysisStage` | `RegimeModel` | `RegimeAutoKTests`, `RegimeLabelWindowTests`, `RegimeAugmentationTests`, `JumpModelTests` | ✅ / ❌ | **C-04 `JumpModel` orfano** | decidere |
| 8 | **Strategy discovery** | `Discovery/StrategyDiscoveryEngine` (211), `StrategyComposer` (556), `AlphaMining/GeneticAlphaMiner` (437) | `/discovery`, `/alpha-mining` | `StrategyDiscoveryStage`, `CreativeDiscoveryStage` | `ExperimentRun`, `SavedFactor` | `CreativeDiscoveryTests`, `AlphaMiningTests`, `GeneticMinerCvGateTests` | ✅ | overfitting per costruzione (mitigato) | riusare |
| 9 | **Ensemble** | `Ensemble/EnsembleManager` (642, keyed per corsia), `EnsembleAllocator` (152), `EnsembleComparator` (241), `EnsembleRebalanceWorker` | `/ensemble` | `EnsembleRebalanceWorker`, `EnsembleAssemblyStage` | `EnsembleState`, `EnsembleRebalanceHistory` | `EnsembleManagerDecayTests`, `EnsembleComparatorTests`, `EnsembleAllocatorTests` | ✅ | routing regime ⚙️ off (deliberato) | riusare |
| 10 | **Portfolio** | `Portfolio/HierarchicalRiskParityOptimizer` (82) ✅, `MeanVarianceOptimizer` (53) ❌, `RiskParityOptimizer` (37) ❌, `PortfolioMath` (218) | `/portfolio` | **solo HRP** via `DecisionStages.cs:90` | pesi in `EnsembleState` | `PortfolioOptimizerTests`, `PortfolioShrinkageErcTests`, `HrpLinkageTests`, `AuditPortfolioDegenerateTests` | ⚠️ | **C-05** 2 optimizer su 3 senza sbocco | consolidare |
| 11 | **GARCH** | `TimeSeries/GarchModel` (119), `GarchFit`, `HarRvForecaster` (159), `ML/VolForecastEvaluator` (135) | `/volatility` | `VolatilityRegimeStage`, `RegimeChangeDetector`, `AnalysisStages.cs:295` | ❌ nessuna | `GarchModelTests`, `HarRvForecasterTests` | ⚙️ | **fuori dal sizing per decisione dichiarata** | **non toccare** |
| 12 | **Pairs trading** | `PairsTrading/PairsBacktestEngine` (284), `KalmanPairsSpreadAnalyzer` (118), `RollingPairsSpreadAnalyzer` (100), `TimeSeries/EngleGrangerCointegrationTest` (196) | `/pairs-trading` | `PairsScreeningStage` | ⚠️ solo artefatti | `PairsBacktestEngineTests`, `KalmanPairsSpreadAnalyzerTests`, `CointegrationOnRealDataTests` | ✅ | **G-17** persistenza | consolidare |
| 13 | **Sentiment / news** | `AltData/RssNewsSource`, `ForexFactoryIngestor`, `RetailSentimentIngestor`, `NewsImpactAnalyzer` (214) · `Sentiment/` 21 file: 3 scorer + `SentimentCompositeCalculator` + `SentimentFeatureFactor` | `/sentiment` | `SentimentSyncWorker`, `AltDataSyncStage`, `NewsImpactCheckStage` | `AltDataPoint`, `SentimentMetricPoint` | 12+ file | ⚠️ | **C-06** feature ML spenta | consolidare |
| 14 | **Backtest** | `Backtesting/BacktestEngine` (692), 14 strategie, `SignalCatalog` (394), `BacktestPageService` (484) | `/backtest`, `/strategies` | `HoldoutValidationStage`, ogni stage di ricerca | `SavedStrategy`, `ExperimentRun` | `BacktestEngineTests`, `BacktestCostAccountingTests`, `CostPropagationTests`, `MakerFillModelTests` | ✅ | — | riusare |
| 15 | **Risk / Kelly** | `Risk/KellyCalculator` (228), `MonteCarloAnalyzer` (210), `PerformanceControlService` (212), `LeverageAdvisor` (140), `CorrelatedExposureGuard` (244), `RiskProfile` (188), `MarginMath` | `/backtest`, `/bot` | `RiskSizingStage` (`ModelStages.cs:504,590`), `TradingEngine` | ⚠️ | `KellyCalculatorTests`, `MonteCarloAnalyzerTests`, `AuditSafetyKellyExtremeTests`, `CorrelatedExposureTests` | ✅ | `PerformanceControl` e `LeverageAdvisor` solo in backtest | riusare |
| 16 | **SafetyChecker** 🔒 | `Trading/SafetyChecker` (122, **statico e puro**), `SafetyConfiguration` (130), `LaneSafetyMonitor` (70) | `/trading`, `/bot`, `/admin/protections` | `TradingEngine.cs:956`, `ExecutionSlicePlanner.cs:54` | `TradingAuditLog` | `SafetyCheckerTests`, `SafetyCheckerLeverageTests`, `LaneRiskProfileEndToEndTests` | ✅ | **C-02** default `DriveProtectiveExits` | correggere |
| 17 | **Execution** | `Trading/TradingEngine` (1.668), `Trading/Internal/*` (12 file), `TradingWorker`, `ExecutionWorker`, `Exchanges/*` | `/trading` via CQRS | `TradingWorker` per candela | `Order`, `OpenPosition`, `TradeRecord`, `ExecutionJob` | 12 file `TradingEngine*Tests` | ✅ | — | **riusare invariato** |
| 18 | **Nested execution** | `Execution/ExecutionAlgorithms` (214: Immediate/TWAP/VWAP/Iceberg/Adaptive), `ExecutionSimulator` (89), `Trading/Internal/ExecutionSlicePlanner` (124) | `/execution` | `ExecutionWorker`, `TradingEngine.cs:1070` | `ExecutionJob`, `ExecutionJobSlice` | `ExecutionTests`, `AuditStressNestedExecutionTests`, `ExecutionSquareRootImpactTests` | ✅ | — | riusare |
| 19 | **Scheduler** | `Pipeline/PipelineSchedulerWorker` (392), `PipelineEngine` (572), `CampaignPlanner` (404), `RegimeChangeDetector` (188) | `/pipeline`, `/campaign` | 3 hosted service | `PipelineRun`, `PipelineArtifact`, `VettingCampaign` | `PipelineSchedulerWorkerTests`, `CampaignPlannerTests`, `PipelineEngineConcurrencyTests` | ✅ | campaign ⚙️ off | riusare |
| 20 | **Supervisor AI** | `Llm/` 19 file: 5 provider, `DelegatingLlmClient`, `LlmCallGuard` (323), `AiCommittee` (213), `PipelineSupervisor` (445), `Agents/DelegatingSupervisorAgent` | `/admin/ai-supervisor`, `/admin/autonomy` | `LlmSupervisorWorker`, `RunApplyEvaluator.cs:81-106` | `AiCredential`, `LlmUsageRecord`, `TradePostMortem` | `AiMultiProviderTests`, `LlmFailoverTimeoutTests`, `LlmCallGuardTests`, `AiCommitteeTests`, `SupervisorAgentTests` | ✅ | comitato ⚙️ off | riusare |
| 21 | **Auto-promozione Paper→Testnet** 🔒 | `Trading/PromotionEvaluator` (273), `LanePromoter` (72), `PromotionWorker` (111) | `/trading`, `/admin/autonomy`, `/dashboard` | `PromotionWorker` ogni 6 h | `TradingEngineState` | `PromotionEvaluatorTests`, `AuditPromotionStateMachineTests`, `LanePromotionFlattenTests`, `DormantFleetPromotionTests` | ✅ | — | **riusare invariato** |
| 22 | **Experiment tracker** | `Experiments/ExperimentTracker` (109), `ExperimentTrackerExtensions` (metodi `Safe*`) | `/experiments` + 3 pagine che tracciano | 7 punti di aggancio | `ExperimentRun`, `ExperimentArtifact` | `ExperimentTrackerTests`, `AuditPipelineExperimentLoggingTests` | ✅ | **G-08** manca `RunSeed` | consolidare |
| 23 | **Concept drift** | `Monitoring/Drift/`: `PsiDriftDetector`, `KsDriftDetector`, `PageHinkleyDetector`, `FeatureDriftMonitor` (95), `FeatureDriftWorker` (188) · `StrategyDecayMonitor` (246) | `/admin/autonomy`, `/ensemble` | `FeatureDriftWorker` → `registry.RetireAsync(requestRetrain:true)` | `DriftCheckResult` | `DriftDetectorTests`, `FeatureDriftMonitorTests`, `FeatureDriftWorkerPersistenceTests` | ⚙️ | `Drift:Enabled=false` | consolidare (Fase 7) |
| 24 | **UI** | 89 `.razor` (22.607 righe), 32 rotte, `NavModel` (fonte unica), 8 page service, 9 componenti condivisi | — | Blazor Server + polling | `UserPageConfig` | `AuditBlazorUiTests`, `BotPageRenderTests`, `ProtectionsPageRenderTests`, 8 `*PageServiceTests` | ✅ | **C-05** `/portfolio` senza applica; nessuna UI per Microstructure/JumpModel | consolidare |
| 25 | **Microstructure** | `Microstructure/`: `IncrementalIcGate` (467), `BinanceDumpDownloader` (177), `BinanceDumpParser` (201), `TapeAggregator` (110), `OrderFlowImbalance` (100) | ❌ **nessuna** | ❌ **nessuno** | ❌ **nessuna** | ✅ 5 file | ❌ | **C-03** | **decidere** |

---

## Domini trasversali

| Dominio | File | Stato | Gap | Azione |
|---|---|---|---|---|
| **Determinismo / seed** | seed in `GeneticAlphaMiner`, `EventStudy`, `IncrementalIcGate`, `DtwPatternAnalysisService`, predittori ML | ⚠️ | **G-08**: 3 `new Random(42)` cablati; nessun `RunSeed` globale | correggere |
| **Versioning dei modelli** | `Registry/ModelRegistry` (161) con `ModelStage` e gate DSR | ✅ | — | riusare |
| **Audit trail** | `TradingAuditLog`, `PipelineArtifact`, `ExperimentArtifact`, `OrchestratorDecision`, `DriftCheckResult` | ✅ | — | riusare |
| **Riproducibilità di un run** | `PipelineConfiguration` + artefatti | ⚠️ | manca `RunSeed` e `DataQualityReport` | consolidare |
| **Sicurezza dei segreti** | `AesGcmEncryptionService`, `IMasterKeyStatus`, `MasterKeyProbe`, `ExchangeCredentialCiphertext` | 🔴 | **C-01 file tracciato con segreti** | **correggere subito** |
| **Isolamento fra corsie** | `TradingLanes`, keyed DI, `LaneExecutionLease` (advisory lock), `LaneInvariantWatchdog`, `LaneQuarantine` | ✅ | — | riusare |
| **Confine verso Live** 🔒 | `CarryMode` (solo Paper/Testnet), `PromotionEvaluator`, `RequireManualConfirmationForLive`, gate master key in `StartAsync` | ✅ | — | **riusare invariato** |
| **Notifiche** | `Notifications/`: `NotificationDispatcher`, Telegram, `DailyDigestWorker` | ⚙️ | `Notifications:Enabled=false` | decidere |
| **Osservabilità** | `Observability/ProcioneMetrics`, `MetricsCollector`, OTLP | ⚠️ | export ⚙️ off; `/metrics` funziona | consolidare |
| **Multi-host / microservizi** | `Contracts/Protos/*.proto`, `RemoteTradingEngineClient`, `RemoteEngineConfigStore`, `HostHeartbeat` | ⚙️ | tutti i toggle remoti off; **G-14** `events.proto` inutilizzato | riusare |

---

## Copertura del mandato

Ogni dominio richiesto nell'incarico, con verdetto in una riga.

| Dominio richiesto | Presente | Verdetto |
|---|---|---|
| dati OHLCV | ✅ | completo e integrato |
| fattori alpha e IC | ✅ | completo; deriva Alpha158 non sorvegliata |
| ML: Linear / RF / LightGBM / MLP | ✅ | tutti presenti (+ Attention, Stacked); "LightGBM" è FastTree |
| CV purged | ✅ | purged + embargo + CPCV |
| regime detection | ✅ | K-means in esercizio; `JumpModel` orfano |
| strategy discovery walk-forward | ✅ | discovery + composer + GP + walk-forward |
| ensemble regime-aware | ✅ | keyed per corsia; routing off per misura |
| portfolio: MV / Risk Parity / HRP | ✅ | tutti e 3 implementati, **solo HRP operativo** |
| GARCH | ✅ | presente; fuori dal sizing per decisione |
| pairs trading | ✅ | OLS + Kalman + cointegrazione |
| sentiment/news | ✅ | 3 livelli; feature ML spenta |
| backtest | ✅ | motore + 14 strategie + costi verificati |
| risk management / Kelly | ✅ | Kelly binario/continuo/empirico + Monte Carlo + esposizione correlata |
| trading con SafetyChecker | ✅ | statico e puro, 2 call site |
| pipeline autonomo + scheduler + supervisor AI + auto-promozione | ✅ | 19 stage, veto-only, Paper→Testnet |
| experiment tracker | ✅ | 7 punti di aggancio |
| concept drift | ✅ | 3 rilevatori, ciclo chiuso, spento |
| nested execution | ✅ | 5 algoritmi, cablati nel motore |
| Alpha158CSharp | ✅ | catalogo + operatori rolling causali |

**Nessun dominio del mandato risulta assente.** Due sono presenti ma non raggiungibili
(Microstructure, `JumpModel`); tre sono presenti e spenti per decisione documentata.
