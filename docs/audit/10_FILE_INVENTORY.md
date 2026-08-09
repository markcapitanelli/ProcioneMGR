# 10 — INVENTARIO FILE-BY-FILE

Inventario consultabile di tutti i file sorgente del repository, raggruppati per area.
Per ruolo, stato e gap di ciascun file vedi [01_PROJECT_MAP.md](01_PROJECT_MAP.md).

**Generato da scansione diretta del repository al 2026-08-08.**

---

## Totali

| Area | File | Righe |
|---|---:|---:|
| Codice `.cs` di produzione (7 progetti + 5 tool) | 439 | 72.824 |
| UI `.razor` | 89 | 22.607 |
| Test `.cs` | 262 | 53.700 |
| Migrazioni EF Core `.cs` | 42 | — |
| **Totale sorgenti** | **832** | **149.131+** |

## Distribuzione per modulo (top 20)

| Modulo | File | Righe |
|---|---:|---:|
| `Services/Trading` | 62 | 9.465 |
| `tools/` (5 CLI) | 5 | 6.632 |
| `Services/Pipeline` | 28 | 6.340 |
| `Services/ML` | 37 | 5.234 |
| `Services/Backtesting` | 24 | 3.674 |
| `Services/Llm` | 19 | 3.623 |
| `Services/Alpha` | 14 | 2.831 |
| `Services/Optimization` | 9 | 2.604 |
| `Services/Exchanges` | 12 | 2.260 |
| `Services/Analysis` | 9 | 2.230 |
| `Services/Sentiment` | 21 | 2.093 |
| `Services/Regime` | 12 | 2.088 |
| `Services/MarketData` | 9 | 1.921 |
| `Data/` | 23 | 1.731 |
| `Services/Ensemble` | 7 | 1.596 |
| `Services/Risk` | 8 | 1.514 |
| `Services/Discovery` | 6 | 1.347 |
| `Services/Microstructure` | 6 | 1.166 |
| `Services/Validation` | 10 | 1.154 |
| `Services/Fleet` | 5 | 978 |

## Legenda dei simboli usati nelle note

🔴 problema critico · 🟠 gap · ⚙️ spento per decisione · 🔒 safety-critical

---

## Indice rapido delle aree con osservazioni

| Area | Osservazione |
|---|---|
| `ProcioneMGR (root)` | 🔴 `appsettings.json.pre-audit-test-20260729-141448` contiene segreti (C-01) |
| `Services/Microstructure` | 🟠 zero DI, zero UI — isola CLI (C-03) |
| `Services/Regime` | 🟠 `JumpModel.cs` orfano (C-04) |
| `Services/Portfolio` | 🟠 2 optimizer su 3 senza sbocco operativo (C-05) |
| `Services/MarketData` | 🔴 `RealtimeMarketDataModels.cs:117` default contro la misura B3 (C-02) |
| `Services/Trading` | 🔒 `SafetyChecker.cs` statico e puro — non modificare |
| `Services/Validation` | 🔒 i gate anti-overfitting — non indebolire |
| `tools/PlatformExpand` | 🟠 5.848 righe in un file (G-09) |

---

## Inventario per area

### `ProcioneMGR (root)/` — 1 file, 706 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `ProcioneMGR/Program.cs` | 706 | — |

### `ProcioneMGR.Contracts/` — 1 file, 42 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `Grpc/SharedSecretClientInterceptor.cs` | 42 | SharedSecretClientInterceptor |

### `ProcioneMGR.Ingestion/` — 2 file, 81 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `NoOpEncryptionService.cs` | 27 | NoOpEncryptionService |
| `Program.cs` | 54 | Program |

### `ProcioneMGR.Ml/` — 3 file, 166 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `InferenceServiceImpl.cs` | 82 | InferenceServiceImpl |
| `NoOpEncryptionService.cs` | 27 | NoOpEncryptionService |
| `Program.cs` | 57 | Program |

### `ProcioneMGR.Trading/` — 3 file, 514 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `Program.cs` | 147 | Program |
| `SharedSecretAuthInterceptor.cs` | 55 | SharedSecretAuthInterceptor |
| `TradingCommandServiceImpl.cs` | 312 | TradingCommandServiceImpl |

### `ProcioneMGR/Components/` — 8 file, 593 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `Account/IdentityComponentsEndpointRouteBuilderExtensions.cs` | 152 | IdentityComponentsEndpointRouteBuilderExtensions |
| `Account/IdentityNoOpEmailSender.cs` | 20 | IdentityNoOpEmailSender |
| `Account/IdentityRedirectManager.cs` | 54 | IdentityRedirectManager |
| `Account/IdentityRevalidatingAuthenticationStateProvider.cs` | 47 | IdentityRevalidatingAuthenticationStateProvider |
| `Account/PasskeyInputModel.cs` | 7 | PasskeyInputModel |
| `Account/PasskeyOperation.cs` | 7 | PasskeyOperation |
| `Layout/NavModel.cs` | 229 | NavItem,NavSection,NavModel |
| `Shared/PollingTimer.cs` | 77 | PollingTimer |

### `ProcioneMGR/Data/` — 23 file, 1731 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `AiCredential.cs` | 21 | AiCredential |
| `AltDataPoint.cs` | 44 | AltDataPoint |
| `AppRoles.cs` | 15 | AppRoles |
| `ApplicationDbContext.cs` | 604 | ApplicationDbContext |
| `ApplicationUser.cs` | 13 | ApplicationUser |
| `DatabaseMigrator.cs` | 219 | DatabaseMigrationOptions,MigrationOutcome,DatabaseMigrator |
| `DatabaseServiceCollectionExtensions.cs` | 31 | DatabaseServiceCollectionExtensions |
| `DbInitializer.cs` | 34 | DbInitializer |
| `EnsembleState.cs` | 37 | EnsembleState,EnsembleRebalanceHistory |
| `ExchangeCredential.cs` | 80 | ExchangeName,ExchangeCredential |
| `ExchangeCredentialCiphertext.cs` | 38 | ExchangeCredentialCiphertext |
| `HostHeartbeat.cs` | 27 | HostHeartbeat |
| `LlmUsageRecord.cs` | 29 | LlmUsageRecord |
| `ModelStage.cs` | 22 | ModelStage |
| `OhlcvData.cs` | 63 | OhlcvData |
| `OrchestratorDecision.cs` | 37 | OrchestratorDecision |
| `SavedFactor.cs` | 53 | SavedFactor |
| `SavedMlModel.cs` | 107 | SavedMlModel |
| `SavedStrategy.cs` | 42 | SavedStrategy |
| `SentimentMetricPoint.cs` | 92 | SentimentMetricPoint,SentimentMetricSources,SentimentMetrics |
| `TrackedSeries.cs` | 37 | TrackedSeries |
| `TradePostMortem.cs` | 52 | TradePostMortem |
| `UserPageConfig.cs` | 34 | UserPageConfig |

### `ProcioneMGR/Services/Admin/` — 2 file, 223 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `DatabaseBackupHelper.cs` | 171 | PgConnectionInfo,IntegrityResult,BackupResult,BackupInfo,DatabaseBackupHelper |
| `DatabaseBackupService.cs` | 52 | DatabaseBackupService |

### `ProcioneMGR/Services/Agents/` — 4 file, 303 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `ClaudeSupervisorAgent.cs` | 174 | ClaudeSupervisorAgent |
| `DelegatingSupervisorAgent.cs` | 30 | DelegatingSupervisorAgent |
| `IPipelineSupervisorAgent.cs` | 64 | IPipelineSupervisorAgent,SupervisorJudgment,SupervisorAgentOptions |
| `LoggingSupervisorAgent.cs` | 35 | LoggingSupervisorAgent |

### `ProcioneMGR/Services/Alpha/` — 14 file, 2831 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `Alpha158/Alpha158Catalog.cs` | 172 | OpDescriptor,Alpha158Factor,Alpha158Catalog |
| `Alpha158/RollingOps.cs` | 518 | Bars,RollingOps |
| `AlphaFactorFactory.cs` | 94 | IAlphaFactorFactory,AlphaFactorFactory |
| `AlphaModels.cs` | 207 | FactorEvaluationConfig,QuantileReturn,IcByHorizon,FactorEvaluationResult,Correlation |
| `FactorCache.cs` | 114 | FactorCacheOptions,IFactorCache,FactorCache |
| `FactorDriftAnalyzer.cs` | 281 | FactorIcPoint,FactorDriftStatus,FactorDriftReport,FactorDriftConfig,IFactorDriftAnalyzer,FactorDriftAnalyzer |
| `FactorDriftMonitor.cs` | 335 | FactorDriftSeriesSnapshot,FactorDriftSnapshot,FactorDriftWorker |
| `FactorEvaluator.cs` | 192 | FactorEvaluator |
| `FactorIcHistory.cs` | 298 | FactorIcWindow,IFactorIcHistoryStore,FactorIcHistoryStore |
| `FactorMath.cs` | 106 | FactorMath |
| `Factors.cs` | 308 | MomentumFactor,MeanReversionFactor,RealizedVolatilityFactor,ParkinsonVolatilityFactor,RelativeVolumeFactor,... |
| `IAlphaFactor.cs` | 65 | FactorParameterDefinition,FactorCategory,IAlphaFactor,FactorParametersExtensions |
| `IFactorEvaluator.cs` | 26 | IFactorEvaluator |
| `OrderFlowFactors.cs` | 115 | TakerImbalanceFactor,AvgTradeSizeFactor |

### `ProcioneMGR/Services/AlphaMining/` — 4 file, 842 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `AlphaExpressionFactor.cs` | 47 | AlphaExpressionFactor |
| `AlphaExpressionParser.cs` | 91 | AlphaExpressionParser |
| `AlphaNode.cs` | 267 | AlphaOp,AlphaNode |
| `GeneticAlphaMiner.cs` | 437 | MiningConfig,MinedFactor,GeneticAlphaMiner |

### `ProcioneMGR/Services/AltData/` — 7 file, 800 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `AltDataSyncService.cs` | 124 | IAltDataSyncService,AltDataSyncService |
| `ForexFactoryIngestor.cs` | 119 | ForexFactoryIngestor |
| `IAltDataSource.cs` | 34 | RawNewsItem,IAltDataSource |
| `NewsImpactAnalyzer.cs` | 214 | ImpactStats,CategoryImpact,SourceImpact,RetailSentimentAgreement,NewsImpactReport,INewsImpactAnalyzer,NewsI... |
| `NewsImpactClassifier.cs` | 134 | NewsCategory,NewsImpactClassifier |
| `RetailSentimentIngestor.cs` | 101 | RetailSentimentIngestor |
| `RssNewsSource.cs` | 74 | NewsFeeds,RssNewsSource |

### `ProcioneMGR/Services/Analysis/` — 9 file, 2230 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `CandlestickPatternDetector.cs` | 313 | CandlePatternType,CandlePattern,CandlestickPatternDetector |
| `ChartPatternDetector.cs` | 165 | ChartPatternType,ChartPatternMatch,ChartPatternDetector |
| `CyclicalAnalyzer.cs` | 310 | CyclicalAnalyzer,HourlyActivity,HourlyBias,HourlyComboStat,DayOfWeekBias,SeasonalityPoint,SeasonalYearOutco... |
| `EventStudy.cs` | 187 | EventStudyConfig,EventStudyResult,EventStudy |
| `ExcursionAnalyzer.cs` | 411 | ExcursionAnalyzer,StopLossSuggestion,TakeProfitSuggestion,RiskBracket,VolatilityRegime,HorizonExcursion,Reg... |
| `GapLapAnalyzer.cs` | 325 | GapLapAnalyzer,GapType,GapEvent,GapLapReport,GapLapCategoryStats |
| `MarketEventDetector.cs` | 153 | MarketEventKind,MarketEvent,MarketEventDetectorConfig,MarketEventDetector |
| `SupportResistanceAnalyzer.cs` | 299 | SupportResistanceAnalyzer,SwingPoint,PriceLevel,BreakoutEvent,SwingTrend,RetracementInfo,SupportResistanceR... |
| `VolumeAnalyzer.cs` | 67 | VolumeAnalyzer,VolumeConfirmation |

### `ProcioneMGR/Services/Backtesting/` — 24 file, 3674 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `BacktestEngine.cs` | 692 | BacktestEngine |
| `BacktestModels.cs` | 267 | Signal,BacktestConfiguration,VolatilityTargetingOptions,EntryExecutionStyle,BacktestResult,BacktestTrade,Eq... |
| `BacktestPageService.cs` | 484 | BacktestConfigSnapshot,BacktestActionResult,BracketSuggestion,BacktestHandoffQuery,LoadedSavedStrategy,Back... |
| `BollingerMeanReversionStrategy.cs` | 81 | BollingerMeanReversionStrategy |
| `CompositeSignalStrategy.cs` | 165 | CompositeSignalStrategy |
| `DonchianBreakoutStrategy.cs` | 111 | DonchianBreakoutStrategy |
| `EmaCrossStrategy.cs` | 70 | EmaCrossStrategy |
| `EventTriggerStrategy.cs` | 158 | EventTriggerStrategy |
| `FundingHistoryProvider.cs` | 46 | IFundingHistoryProvider,FundingHistoryProvider |
| `FundingRateLookup.cs` | 46 | FundingRatePoint,FundingRateLookup |
| `GridMeanReversionStrategy.cs` | 112 | GridMeanReversionStrategy |
| `IBacktestEngine.cs` | 23 | IBacktestEngine |
| `IStrategy.cs` | 49 | StrategyParameterDefinition,IStrategy,StrategyParametersExtensions |
| `MacdTrendStrategy.cs` | 69 | MacdTrendStrategy |
| `MlStrategy.cs` | 193 | MlStrategy |
| `MomentumStrategy.cs` | 73 | MomentumStrategy |
| `PriceSmaCrossStrategy.cs` | 70 | PriceSmaCrossStrategy |
| `RegimeConditionalStrategy.cs` | 131 | RegimeConditionalStrategy |
| `RsiOversoldStrategy.cs` | 63 | RsiOversoldStrategy |
| `SignalCatalog.cs` | 394 | SignalCatalog |
| `StochasticStrategy.cs` | 101 | StochasticStrategy |
| `StrategyFactory.cs` | 54 | IStrategyFactory,StrategyFactory |
| `SupertrendStrategy.cs` | 116 | SupertrendStrategy |
| `VwapReversionStrategy.cs` | 106 | VwapReversionStrategy |

### `ProcioneMGR/Services/Carry/` — 5 file, 573 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `CarryBacktestEngine.cs` | 135 | CarryBacktestEngine |
| `CarryDecider.cs` | 49 | CarryAction,CarryDecider |
| `CarryEngine.cs` | 144 | CarryMode,CarryLegOrder,CarryExecutionResult,ICarryExecutor,CarrySymbolState,CarryEngine,PaperCarryExecutor |
| `CarryModels.cs` | 64 | CarryConfiguration,CarryEpisode,CarryBacktestResult |
| `CarryWorker.cs` | 181 | CarryOptions,CarryWorker |

### `ProcioneMGR/Services/Config/` — 2 file, 372 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `AdminConfigRules.cs` | 271 | AdminConfigRules |
| `AppConfigWriter.cs` | 101 | IAppConfigWriter,AppConfigWriter |

### `ProcioneMGR/Services/Discovery/` — 6 file, 1347 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `Dtw/DtwMatcher.cs` | 244 | DtwMatch,DtwConfig,IDtwMatcher,DtwMatcher |
| `Dtw/DtwPatternAnalysisService.cs` | 255 | DtwPatternAnalysis,ShapeMatchedNull,IDtwPatternAnalysisService,DtwPatternAnalysisService |
| `IStrategyDiscovery.cs` | 9 | IStrategyDiscovery |
| `StrategyComposer.cs` | 556 | ComposedCandidate,ComposerConfiguration,ComposerScreeningConfiguration,ICompositeSignalGenerator,IEventTrig... |
| `StrategyDiscoveryEngine.cs` | 211 | StrategyDiscoveryEngine |
| `StrategyDiscoveryModels.cs` | 72 | StrategyDiscoveryConfiguration,DiscoveryCandidate,StrategyDiscoveryResult,DiscoveryProgress |

### `ProcioneMGR/Services/Ensemble/` — 7 file, 1596 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `EnsembleAllocator.cs` | 152 | EnsembleAllocator |
| `EnsembleComparator.cs` | 241 | IEnsembleComparator,EnsembleComparatorOptions,EnsembleSummary,LegSummary,EnsembleComparison,EnsembleComparator |
| `EnsembleManager.cs` | 642 | EnsembleManager |
| `EnsembleModels.cs` | 189 | EnsembleConfiguration,EnsembleStrategy,EnsembleStatus,StrategyStatus,EnsemblePerformance,StrategyEquityCurv... |
| `EnsemblePageService.cs` | 290 | DriftEvaluationResult,EnsemblePageService |
| `EnsembleRebalanceWorker.cs` | 56 | EnsembleRebalanceWorker |
| `IEnsembleManager.cs` | 26 | IEnsembleManager |

### `ProcioneMGR/Services/Exchanges/` — 12 file, 2260 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `BinanceClient.cs` | 688 | BinanceClient |
| `BitgetClient.cs` | 806 | BitgetClient |
| `ExchangeClientException.cs` | 20 | ExchangeClientException |
| `ExchangeClientFactory.cs` | 42 | ExchangeClientFactory |
| `ExchangeClock.cs` | 167 | IExchangeClock,ExchangeClock,ExchangeClockSyncWorker |
| `ExchangeRateLimitHandler.cs` | 156 | ExchangeRateLimitHandler |
| `ExchangeTrading.cs` | 139 | struct,PlaceOrderRequest,PlaceOrderResult,CancelOrderResult,OrderStatusResult,OpenOrder,AccountBalance,Symb... |
| `FuturesTrading.cs` | 110 | SetLeverageResult,FuturesPosition,FuturesBalance,IFuturesExchangeClient |
| `IExchangeClient.cs` | 59 | IExchangeClient |
| `IExchangeClientFactory.cs` | 17 | IExchangeClientFactory |
| `Ohlcv.cs` | 24 | struct |
| `Timeframes.cs` | 32 | Timeframes |

### `ProcioneMGR/Services/Execution/` — 5 file, 470 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `ExecutionAlgorithmFactory.cs` | 35 | IExecutionAlgorithmFactory,ExecutionAlgorithmFactory |
| `ExecutionAlgorithms.cs` | 214 | ExecutionPlanning,ImmediateExecutionAlgorithm,TwapExecutionAlgorithm,VwapExecutionAlgorithm,IcebergExecutio... |
| `ExecutionModels.cs` | 106 | ExecutionSide,ExecutionIntent,ExecutionSlice,MarketImpactModel,ExecutionPlan,ExecutionParameters,ExecutionF... |
| `ExecutionSimulator.cs` | 89 | IExecutionSimulator,ExecutionSimulator |
| `IExecutionAlgorithm.cs` | 26 | IExecutionAlgorithm |

### `ProcioneMGR/Services/Experiments/` — 4 file, 269 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `ExperimentEntities.cs` | 70 | ExperimentRun,ExperimentArtifact |
| `ExperimentTracker.cs` | 109 | ExperimentTracker |
| `ExperimentTrackerExtensions.cs` | 53 | ExperimentTrackerExtensions |
| `IExperimentTracker.cs` | 37 | IExperimentTracker |

### `ProcioneMGR/Services/Fleet/` — 5 file, 978 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `FleetModels.cs` | 147 | FleetOptions,FleetLaneState,FleetCandidate,FleetState,FleetAction,AssignCandidateToLane,StopAndFreeLane,Pro... |
| `FleetOrchestrator.cs` | 115 | FleetOrchestrator |
| `FleetOrchestratorWorker.cs` | 273 | FleetOrchestratorWorker |
| `FleetStateReader.cs` | 263 | IFleetStateReader,FleetStateReader,struct |
| `GreyDeployer.cs` | 180 | GreyChoice,GreyDeployResult,IGreyDeployer,GreyDeployer |

### `ProcioneMGR/Services/Health/` — 1 file, 194 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `HostHeartbeats.cs` | 194 | HeartbeatOptions,HeartbeatHealth,HeartbeatMonitorLogic,HeartbeatNotice,HeartbeatTransitionTracker,HostHeart... |

### `ProcioneMGR/Services/Indicators/` — 3 file, 640 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `ITechnicalIndicatorsService.cs` | 69 | ITechnicalIndicatorsService |
| `IndicatorSeries.cs` | 40 | IndicatorSeriesType,struct,IndicatorSeries |
| `TechnicalIndicatorsService.cs` | 531 | TechnicalIndicatorsService |

### `ProcioneMGR/Services/Ingestion/` — 10 file, 902 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `BarBuilder.cs` | 128 | AggregatedBar,BarBuilder |
| `IMarketDataSyncService.cs` | 15 | IMarketDataSyncService |
| `IOhlcvIngestionService.cs` | 26 | struct,struct,IOhlcvIngestionService |
| `IngestionServiceCollectionExtensions.cs` | 65 | IngestionServiceCollectionExtensions |
| `MarketDataSyncService.cs` | 132 | MarketDataSyncService |
| `MarketDataSyncWorker.cs` | 78 | MarketDataSyncWorker |
| `OhlcvIngestionService.cs` | 176 | OhlcvIngestionService |
| `RemoteMarketDataSyncService.cs` | 37 | RemoteMarketDataSyncService |
| `SeriesFreshness.cs` | 114 | SeriesFreshness |
| `SeriesFreshnessWatchWorker.cs` | 131 | SeriesFreshnessWatchWorker |

### `ProcioneMGR/Services/Llm/` — 19 file, 3623 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `AiKeyStore.cs` | 168 | AiProviders,AiKeySource,IAiKeyStore,AiKeyStore |
| `AnthropicLlmClient.cs` | 268 | LlmOptions,AnthropicLlmClient |
| `Committee/AiCommittee.cs` | 213 | CommitteeOptions,CommitteeOption,CommitteeQuestion,CommitteeVote,CommitteeVerdict,IAiCommittee,AiCommittee |
| `ILlmClient.cs` | 18 | ILlmClient |
| `LlmCallGuard.cs` | 323 | LlmCallOutcome,LlmCallResult,LlmGuardStatus,ILlmCallGuard,LlmCallGuard |
| `LlmClientResolver.cs` | 33 | ILlmClientResolver,LlmClientResolver |
| `LlmSupervisorWorker.cs` | 122 | LlmSupervisorWorker |
| `LlmUsage.cs` | 385 | LlmCallContext,struct,LlmBudgetVerdict,LlmUsageSnapshot,LlmUsageRow,LlmBudgetOptions,ILlmUsageSink,LlmUsage... |
| `ModelAutoSelector.cs` | 87 | ModelAutoSelector |
| `Narration/DigestNarrator.cs` | 123 | IDigestNarrator,DigestNarrator |
| `Narration/PostMortemAnalyzer.cs` | 125 | PostMortemCauses,TradeFacts,PostMortemAnalyzer |
| `Narration/PostMortemService.cs` | 275 | PostMortemOptions,IPostMortemService,PostMortemService |
| `Narration/PostMortemWorker.cs` | 49 | PostMortemWorker |
| `Narration/RejectionDigest.cs` | 190 | RejectionCauses,RejectionGroup,RejectedCandidateFacts,RunRejectionDigest,RejectionDigestBuilder |
| `Narration/RejectionExplainService.cs` | 189 | IRejectionExplainService,RunRejectionSummary,RejectionExplainService |
| `Narration/RejectionNarrator.cs` | 224 | RejectionNote,RejectionNarration,IRejectionNarrator,RejectionNarrator |
| `NvidiaLlmClient.cs` | 347 | IModelCatalogProvider,OpenAiCompatibleLlmClient,NvidiaLlmClient,GeminiLlmClient,GroqLlmClient,HuggingFaceLl... |
| `PipelineSupervisor.cs` | 445 | LlmArtifactKinds,IPipelineSupervisor,PipelineSupervisor |
| `SupervisorAdvisory.cs` | 39 | SupervisorAdvisory,ParameterSuggestion |

### `ProcioneMGR/Services/ML/` — 37 file, 5234 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `AttentionReturnPredictor.cs` | 559 | AttentionReturnPredictor |
| `DatasetBuilder.cs` | 74 | DatasetBuilder |
| `GradientBoostingReturnPredictor.cs` | 36 | GradientBoostingReturnPredictor |
| `HierarchicalClustering.cs` | 105 | HierarchicalClustering |
| `IDatasetBuilder.cs` | 29 | IDatasetBuilder |
| `IHierarchicalClustering.cs` | 75 | LinkageMethod,ClusterNode,IHierarchicalClustering,CorrelationDistance |
| `IPurgedTimeSeriesCv.cs` | 22 | IPurgedTimeSeriesCv |
| `IReturnPredictor.cs` | 38 | IReturnPredictor |
| `IRiskFactorPca.cs` | 17 | IRiskFactorPca |
| `ISequencePredictor.cs` | 18 | ISequencePredictor |
| `IcFeatureSelector.cs` | 93 | IcFeatureSelectionConfig,ScoredFactor,IIcFeatureSelector,IcFeatureSelector |
| `Labeling/MetaLabeler.cs` | 229 | PrimarySignal,MetaLabelSample,MetaLabelingReport,IMetaLabeler,MetaLabeler |
| `Labeling/MetaLabelingAnalysisService.cs` | 207 | MetaLabelingAnalysis,IMetaLabelingAnalysisService,MetaLabelingAnalysisService |
| `Labeling/MetaModelTrainer.cs` | 178 | MetaRow,MetaPrediction,MetaModelConfig,MetaModelResult,IMetaModelTrainer,MetaModelTrainer |
| `Labeling/TripleBarrierLabeler.cs` | 222 | TripleBarrierOutcome,TripleBarrierLabel,TripleBarrierConfig,ITripleBarrierLabeler,TripleBarrierLabeler |
| `LinearReturnPredictor.cs` | 20 | LinearReturnPredictor |
| `MlComparisonClient.cs` | 70 | IMlComparisonClient,MlComparisonClient |
| `MlComparisonOptions.cs` | 20 | MlComparisonOptions |
| `MlLabService.cs` | 808 | MlConfigSnapshot,MlActionResult,MlLoadResult,MlLabService |
| `MlModelLoader.cs` | 85 | MlModelLoader |
| `MlModels.cs` | 71 | FactorSpec,FeatureRow,MlDataset,MlDatasetView,CvSplit,FeatureImportance,SavedFactorSpecDto |
| `MlStageMapper.cs` | 33 | MlStageMapper |
| `MlTargetKind.cs` | 82 | MlTargetKind,ForwardTargets |
| `MlpReturnPredictor.cs` | 298 | MlpReturnPredictor |
| `PurgedTimeSeriesCv.cs` | 41 | PurgedTimeSeriesCv |
| `RandomForestReturnPredictor.cs` | 32 | RandomForestReturnPredictor |
| `RegressionPredictorBase.cs` | 180 | PredictedReturn,IShapExplainable,RegressionPredictorBase |
| `ReturnPredictorCatalog.cs` | 23 | ReturnPredictorCatalog |
| `RiskFactorPca.cs` | 103 | RiskFactorPca |
| `RiskFactorPcaModels.cs` | 26 | PrincipalComponent,RiskFactorPcaResult |
| `SequenceWindowing.cs` | 83 | SequenceWindowing |
| `Shap/MlNetTreeExtractor.cs` | 197 | MlNetTreeExtractor |
| `Shap/ShapAnalysis.cs` | 177 | ShapContextCell,ShapContextLens,ShapAnalysisResult,ShapAnalyzer |
| `Shap/ShapTree.cs` | 140 | ShapTree,ShapTreeEnsemble |
| `Shap/TreeShapExplainer.cs` | 280 | ShapContribution,ShapExplanation,ShapSummaryRow,TreeShapExplainer |
| `StackedReturnPredictor.cs` | 428 | StackingMode,StackedReturnPredictor |
| `VolForecastEvaluator.cs` | 135 | VolForecastEvaluation,VolForecastEvaluator |

### `ProcioneMGR/Services/MarketData/` — 9 file, 1921 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `BinanceStreamMapper.cs` | 197 | BinanceStreamMapper |
| `BitgetStreamMapper.cs` | 129 | BitgetStreamMapper |
| `IExchangeStreamMapper.cs` | 43 | IExchangeStreamMapper |
| `IWebSocketTransport.cs` | 111 | IWebSocketTransport,IWebSocketTransportFactory,ClientWebSocketTransport,ClientWebSocketTransportFactory |
| `LiquidationAccumulation.cs` | 127 | LiquidationEvent,BinanceLiquidationMapper,LiquidationBucket,LiquidationAggregator |
| `LiquidationSyncWorker.cs` | 270 | LiquidationsOptions,LiquidationSyncWorker |
| `RealtimeMarketDataModels.cs` | 137 | struct,struct,struct,struct,RealtimeFeedOptions |
| `RealtimePriceWorker.cs` | 478 | RealtimePriceWorker |
| `WebSocketPriceFeed.cs` | 429 | FeedHealth,SeriesHealth,WebSocketPriceFeed |

### `ProcioneMGR/Services/Microstructure/` — 6 file, 1166 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `BinanceDumpDownloader.cs` | 177 | DumpMarket,BinanceDumpDownloader |
| `BinanceDumpParser.cs` | 201 | BinanceDumpParser |
| `IncrementalIcGate.cs` | 467 | IcCandidate,IncrementalIcConfig,IncrementalIcOutcome,IncrementalIcReport,IncrementalIcGate |
| `MicrostructureModels.cs` | 111 | AggTrade,TapeBar,BestQuote,BookDepthSnapshot |
| `OrderFlowImbalance.cs` | 100 | OrderFlowImbalance |
| `TapeAggregator.cs` | 110 | TapeAggregator |

### `ProcioneMGR/Services/Monitoring/` — 10 file, 891 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `Drift/DriftMath.cs` | 56 | DriftMath |
| `Drift/DriftModels.cs` | 108 | DriftSeverity,DriftResult,DriftThresholds,FactorDriftReport,DriftCheckResult |
| `Drift/FeatureDriftMonitor.cs` | 95 | FeatureDriftMonitor |
| `Drift/FeatureDriftWorker.cs` | 188 | DriftMonitorOptions,FeatureDriftWorker |
| `Drift/IFeatureDriftDetector.cs` | 23 | IFeatureDriftDetector |
| `Drift/IFeatureDriftMonitor.cs` | 18 | IFeatureDriftMonitor |
| `Drift/KsDriftDetector.cs` | 49 | KsDriftDetector |
| `Drift/PageHinkleyDetector.cs` | 45 | PageHinkleyDetector |
| `Drift/PsiDriftDetector.cs` | 63 | PsiDriftDetector |
| `StrategyDecayMonitor.cs` | 246 | IStrategyDecayMonitor,DecayMonitorOptions,DecayReport,StrategyDecayMonitor |

### `ProcioneMGR/Services/Notifications/` — 6 file, 593 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `DailyDigest.cs` | 250 | DigestOptions,DigestSchedule,DigestData,DailyDigestComposer,DailyDigestWorker |
| `INotifier.cs` | 47 | NotificationSeverity,INotifier,INotificationProvider,NotificationOptions |
| `LoggingNotifier.cs` | 22 | LoggingNotifier |
| `NotificationDispatcher.cs` | 188 | NotificationOutcome,NotificationResult,NotificationChannelStatus,NotificationDispatcher |
| `NotificationServiceCollectionExtensions.cs` | 31 | NotificationServiceCollectionExtensions |
| `TelegramNotifier.cs` | 55 | TelegramNotifier |

### `ProcioneMGR/Services/Observability/` — 3 file, 431 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `MetricsCollector.cs` | 228 | MetricsCollector,HistogramSummary,MetricsSnapshot |
| `ObservabilityExtensions.cs` | 52 | ObservabilityExtensions |
| `ProcioneMetrics.cs` | 151 | ProcioneMetrics |

### `ProcioneMGR/Services/Optimization/` — 9 file, 2604 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `Bayesian/BayesianOptimizationEngine.cs` | 205 | BayesianOptions,IHyperparameterOptimizer,BayesianOptimizationEngine |
| `Bayesian/BayesianSearch.cs` | 49 | BayesianSearchResult,BayesianSearch |
| `Bayesian/ParameterSpace.cs` | 53 | ParameterDimension,EvaluatedPoint,ParameterSpace |
| `IOptimizationEngine.cs` | 20 | IOptimizationEngine |
| `OptimizationEngine.cs` | 755 | OptimizationEngine |
| `OptimizationModels.cs` | 228 | OptimizationSelectionMetric,SearchStrategy,OptimizationConfiguration,ParameterRange,WalkForwardConfiguratio... |
| `OptimizationPageService.cs` | 538 | OptRange,OptimizationConfigSnapshot,OptActionResult,OptimizationHandoffQuery,HeatmapMatrix,OptimizationPage... |
| `Statistics.cs` | 406 | Statistics,TearsheetMetrics |
| `TradeStatistics.cs` | 350 | TradeStatistics,MonthlyProfitCell,TradeReport |

### `ProcioneMGR/Services/PairsTrading/` — 7 file, 721 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `IPairsBacktestEngine.cs` | 19 | IPairsBacktestEngine |
| `KalmanPairsSpreadAnalyzer.cs` | 118 | KalmanPairsSpreadAnalyzer |
| `PairsBacktestEngine.cs` | 284 | PairsBacktestEngine |
| `PairsBacktestModels.cs` | 119 | PairsBacktestConfiguration,PairsHedgeRatioEstimator,PairsPositionSide,PairsTrade,PairsBacktestResult |
| `PairsCandleAligner.cs` | 50 | PairsCandleAligner |
| `PairsSpreadSeries.cs` | 31 | PairsSpreadSeries |
| `RollingPairsSpreadAnalyzer.cs` | 100 | RollingPairsAnalysis,RollingPairsSpreadAnalyzer |

### `ProcioneMGR/Services/Pipeline/` — 28 file, 6340 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `AutoBracket.cs` | 59 | AutoBracket |
| `CampaignEntities.cs` | 89 | VettingCampaign,CampaignStatus,CampaignConfigState |
| `CampaignOptions.cs` | 16 | CampaignOptions |
| `CampaignPageService.cs` | 120 | CampaignPageService |
| `CampaignPlanner.cs` | 404 | ICampaignPlanner,CampaignPlanner |
| `CampaignPlannerWorker.cs` | 42 | CampaignPlannerWorker |
| `IPipelineEngine.cs` | 56 | IPipelineEngine,IPipelineStageCatalog |
| `IPipelineStage.cs` | 51 | IPipelineStage |
| `PipelineApplier.cs` | 264 | IPipelineApplier,ApplyResult,PipelineApplier |
| `PipelineCandleCache.cs` | 35 | PipelineCandleCache |
| `PipelineDagValidator.cs` | 51 | PipelineDagValidator |
| `PipelineEngine.cs` | 572 | PipelineEngine |
| `PipelineEntities.cs` | 107 | PipelineConfiguration,PipelineRun,PipelineArtifact |
| `PipelineModels.cs` | 639 | SeriesSpec,PipelineDateRanges,StageConfig,StageConfigExtensions,StageParameterDefinition,struct,StageDepend... |
| `PipelinePageService.cs` | 385 | PipelineActionResult,PipelineConfigDraft,PipelineSaveResult,PipelinePageService |
| `PipelineRules.cs` | 90 | PipelineRuleSet,IPipelineRulesProvider,PipelineRulesProvider |
| `PipelineSchedulerWorker.cs` | 392 | AutoReapplyOptions,AutoReapplyArtifactKinds,AutoResumeArtifactKinds,PipelineSchedulerWorker,AutoReapplyDeci... |
| `PipelineStageCatalog.cs` | 79 | PipelineStageCatalog |
| `RegimeChangeDetector.cs` | 188 | RegimeTriggerOptions,RegimeTriggerCheck,IRegimeChangeDetector,RegimeChangeDetector |
| `RegimeChangeTriggerWorker.cs` | 83 | RegimeChangeTriggerWorker |
| `RunApplyEvaluator.cs` | 171 | IRunApplyEvaluator,RunApplyOutcome,RunApplyEvaluator |
| `Stages/AnalysisStages.cs` | 475 | FeatureEngineeringStage,RegimeAnalysisStage,VolatilityRegimeStage,PairsScreeningStage |
| `Stages/CreativeDiscoveryStage.cs` | 141 | CreativeDiscoveryStage |
| `Stages/DataStages.cs` | 199 | DataIngestionStage,AltDataSyncStage |
| `Stages/DecisionStages.cs` | 579 | EnsembleAssemblyStage,RiskSizingStage,NewsImpactCheckStage,RecommendationStage,ExecutionPlanStage |
| `Stages/ModelStages.cs` | 788 | MlModelTrainingStage,StrategyDiscoveryStage,HoldoutValidationStage,RobustnessProbeStage,OverfittingGate,struct |
| `Stages/NullTwinValidationStage.cs` | 139 | NullTwinValidationStage |
| `Stages/PowerCheckStage.cs` | 126 | PowerCheckStage |

### `ProcioneMGR/Services/Portfolio/` — 6 file, 559 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `HierarchicalRiskParityOptimizer.cs` | 82 | HierarchicalRiskParityOptimizer |
| `IPortfolioOptimizer.cs` | 75 | MeanVarianceObjective,CovarianceEstimator,RiskParityMethod,PortfolioOptimizationConfig,PortfolioAllocation,... |
| `MeanVarianceOptimizer.cs` | 53 | MeanVarianceOptimizer |
| `PortfolioMath.cs` | 218 | PortfolioMath |
| `ReturnMatrixBuilder.cs` | 94 | AlignedReturnMatrix,ReturnMatrixBuilder |
| `RiskParityOptimizer.cs` | 37 | RiskParityOptimizer |

### `ProcioneMGR/Services/Preferences/` — 1 file, 78 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `PageConfigStore.cs` | 78 | IPageConfigStore,PageConfigStore |

### `ProcioneMGR/Services/Regime/` — 12 file, 2088 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `IMarketFeatureExtractor.cs` | 28 | IMarketFeatureExtractor |
| `IRegimeDetector.cs` | 38 | IRegimeDetector |
| `JumpModel.cs` | 288 | JumpModelFit,JumpModel |
| `LaneRegimeRouter.cs` | 265 | RegimeRoutingRule,RegimeRoutingOptions,RegimeRoutingDecision,ILaneRegimeRouter,LaneRegimeRouter |
| `MarketBreadthCalculator.cs` | 76 | IMarketBreadthCalculator,MarketBreadthCalculator |
| `MarketFeatureExtractor.cs` | 215 | MarketFeatureExtractor |
| `MarketFeatures.cs` | 171 | MarketFeatures,FeatureScaling,FeatureNormalizer |
| `RegimeAssignment.cs` | 192 | RegimeAssignment |
| `RegimeAugmentation.cs` | 76 | RegimeAugmentation |
| `RegimeDetector.cs` | 548 | RegimeDetector |
| `RegimeModels.cs` | 93 | TrainingConfiguration,RegimeModel,RegimeProfile,StrategyPerformanceInRegime |
| `RegimeRetrainingWorker.cs` | 98 | RegimeRetrainingWorker |

### `ProcioneMGR/Services/Registry/` — 1 file, 161 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `ModelRegistry.cs` | 161 | ModelRegistryOptions,PromotionOutcome,IModelRegistry,ModelRegistry |

### `ProcioneMGR/Services/Risk/` — 8 file, 1514 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `BotPageService.cs` | 247 | BotPageService |
| `CorrelatedExposureGuard.cs` | 244 | CorrelatedExposureAssessment,CorrelatedExposureContribution,CorrelatedExposureOptions,ICorrelatedExposureGu... |
| `KellyCalculator.cs` | 228 | KellyCalculator,KellySuggestion |
| `LeverageAdvisor.cs` | 140 | LeverageAdvisor,LeverageScenario,LeverageAdvice |
| `MarginMath.cs` | 45 | MarginMath |
| `MonteCarloAnalyzer.cs` | 210 | MonteCarloAnalyzer,MonteCarloSamplingMode,MonteCarloConfig,MonteCarloResult |
| `PerformanceControlService.cs` | 212 | PerformanceControlService,EquityControlResult |
| `RiskProfile.cs` | 188 | RiskProfile,RiskProfiles |

### `ProcioneMGR/Services/Security/` — 6 file, 501 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `AesGcmEncryptionService.cs` | 153 | AesGcmEncryptionService |
| `DataProtectionSetup.cs` | 52 | DataProtectionSetup |
| `EncryptedStringConverter.cs` | 18 | EncryptedStringConverter |
| `ExchangeCredentialReader.cs` | 129 | DecryptedExchangeCredential,IExchangeCredentialReader,ExchangeCredentialReader |
| `IEncryptionService.cs` | 26 | IEncryptionService,IMasterKeyStatus |
| `MasterKeyProbe.cs` | 123 | MasterKeyProbeResult,IMasterKeyProbe,MasterKeyProbe,MasterKeyProbeWorker |

### `ProcioneMGR/Services/Sentiment/` — 21 file, 2093 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `DelegatingSentimentScorer.cs` | 39 | SentimentScorerProviders,DelegatingSentimentScorer |
| `HashingTextVectorizer.cs` | 88 | HashingTextVectorizer |
| `ISentimentScorer.cs` | 20 | ISentimentScorer |
| `KeywordSentimentScorer.cs` | 48 | KeywordSentimentScorer |
| `LlmSentimentScorer.cs` | 189 | LlmSentimentScorer |
| `Metrics/BinanceFuturesSentimentClient.cs` | 151 | BinanceFuturesSentimentClient |
| `Metrics/FearGreedClient.cs` | 66 | FearGreedClient |
| `Metrics/ISentimentMetricSource.cs` | 31 | SentimentMetricSample,ISentimentMetricSource,IBackfillableMetricSource |
| `Metrics/SentimentMetricSyncService.cs` | 121 | ISentimentMetricSyncService,SentimentMetricSyncService |
| `OnnxSentimentPilotService.cs` | 180 | OnnxPilotTrainResult,OnnxSentimentPilotService |
| `OnnxSentimentScorer.cs` | 196 | OnnxSentimentScorer |
| `SentimentAlphaFactor.cs` | 70 | ScoredNewsItem,SentimentAlphaFactor |
| `SentimentCompositeCalculator.cs` | 195 | SentimentCompositeCalculator |
| `SentimentFeatureFactor.cs` | 40 | SentimentFeatureFactor |
| `SentimentNewsProvider.cs` | 62 | ISentimentNewsProvider,SentimentNewsProvider |
| `SentimentOptions.cs` | 68 | SentimentOptions |
| `SentimentScorerComparisonService.cs` | 220 | ScorerComparisonRequest,ScorerComparisonEntry,ScorerDisagreement,ScorerComparisonResult,SentimentScorerComp... |
| `SentimentSnapshot.cs` | 56 | SentimentSnapshot,SymbolSentiment |
| `SentimentSnapshotService.cs` | 90 | SentimentSnapshotCache,ISentimentSnapshotService,SentimentSnapshotService |
| `SentimentSourceHealthRegistry.cs` | 36 | SourceHealth,SentimentSourceHealthRegistry |
| `SentimentSyncWorker.cs` | 127 | SentimentSyncWorker |

### `ProcioneMGR/Services/TimeSeries/` — 8 file, 718 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `EngleGrangerCointegrationTest.cs` | 196 | EngleGrangerCointegrationTest |
| `GarchFit.cs` | 67 | GarchFit |
| `GarchModel.cs` | 119 | GarchModel |
| `HarRvForecaster.cs` | 159 | HarRvForecaster,RealizedVariance |
| `ICointegrationTest.cs` | 63 | CointegrationResult,ICointegrationTest |
| `IGarchModel.cs` | 35 | GarchInnovation,IGarchModel |
| `OlsRegression.cs` | 38 | OlsResult,OlsRegression |
| `PairsSpreadAnalyzer.cs` | 41 | PairsSpreadAnalyzer |

### `ProcioneMGR/Services/Trading/` — 62 file, 9465 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `Behaviors/LoggingBehavior.cs` | 30 | LoggingBehavior&lt;TMessage |
| `Commands/ClosePositionCommand.cs` | 15 | ClosePositionCommand,ClosePositionCommandHandler |
| `Commands/ConfirmOrderCommand.cs` | 15 | ConfirmOrderCommand,ConfirmOrderCommandHandler |
| `Commands/EmergencyStopCommand.cs` | 15 | EmergencyStopCommand,EmergencyStopCommandHandler |
| `Commands/RejectOrderCommand.cs` | 15 | RejectOrderCommand,RejectOrderCommandHandler |
| `Commands/SetStopLossTakeProfitCommand.cs` | 21 | SetStopLossTakeProfitCommand,SetStopLossTakeProfitCommandHandler |
| `Commands/StartLaneCommand.cs` | 15 | StartLaneCommand,StartLaneCommandHandler |
| `Commands/StopLaneCommand.cs` | 15 | StopLaneCommand,StopLaneCommandHandler |
| `DecimalValueMapper.cs` | 77 | DecimalValueMapper |
| `EngineConfigSections.cs` | 122 | EngineConfigSections |
| `EngineConfigService.cs` | 284 | EngineConfigSectionView,EngineConfigWriteResult,EngineConfigService |
| `EngineConfigStore.cs` | 268 | IEngineConfigStore,EngineNotificationChannelStatus,EngineConfigSnapshot,LocalEngineConfigStore,RemoteEngine... |
| `ExecutionJobModels.cs` | 99 | ExecutionJob,ExecutionJobSlice,ExecutionJobSlices |
| `ExecutionWorker.cs` | 49 | ExecutionWorker |
| `ITradingEngine.cs` | 69 | ITradingEngine |
| `Internal/AutoStopApplier.cs` | 38 | AutoStopApplier |
| `Internal/BracketOrderManager.cs` | 87 | BracketOrderManager |
| `Internal/ExecutionQuality.cs` | 61 | ExecutionQuality |
| `Internal/ExecutionSlicePlanner.cs` | 124 | ExecutionSlicePlanner |
| `Internal/FillSanityCheck.cs` | 57 | FillSanityCheck |
| `Internal/FuturesPositionReconciler.cs` | 80 | FuturesPositionReconciler |
| `Internal/OrderReconciler.cs` | 85 | ReconcileStatus,ReconcileOutcome,OrderReconciler |
| `Internal/PositionCloser.cs` | 367 | PositionCloser |
| `Internal/PositionOpener.cs` | 413 | PositionOpener |
| `Internal/ProtectiveExitEvaluator.cs` | 134 | ProtectiveExitKind,struct,ProtectiveExitEvaluator |
| `Internal/SignalOrderBuilder.cs` | 116 | SignalOrderBuilder |
| `Internal/TradingPersistence.cs` | 130 | TradingPersistence |
| `Internal/VolatilityScaler.cs` | 105 | VolatilityScaler |
| `LaneCountCoherenceProbe.cs` | 179 | LaneCountCoherenceResult,LaneCountCoherenceProbe,LaneCountCoherenceProbeWorker |
| `LaneDirectory.cs` | 83 | LaneSummary,ILaneDirectory,LaneDirectory |
| `LaneEpisodes.cs` | 205 | LaneEpisodeSource,LaneEpisode,LaneEpisodeBuilder |
| `LaneExecutionLease.cs` | 109 | ILaneLease,ILaneLeaseFactory,NpgsqlLaneLeaseFactory |
| `LaneInvariantChecker.cs` | 64 | LaneInvariantChecker |
| `LaneInvariantOptions.cs` | 26 | LaneInvariantOptions |
| `LaneInvariantWatchdog.cs` | 265 | LaneInvariantWatchdog |
| `LanePromoter.cs` | 72 | ILanePromoter,LanePromoter |
| `LaneQuarantineStore.cs` | 103 | ILaneQuarantineStore,LaneQuarantineStore |
| `LaneSafetyMonitor.cs` | 70 | ILaneRiskProfileSink,LaneSafetyMonitor |
| `LiveExecutionOptions.cs` | 22 | LiveExecutionOptions |
| `PromotionEvaluator.cs` | 273 | PromotionEvaluatorOptions,LaneMetrics,PromotionDecision,IPromotionEvaluator,PromotionEvaluator |
| `PromotionWorker.cs` | 111 | PromotionWorker |
| `ProtectiveExitAudit.cs` | 104 | ProtectiveExitAnomaly,ProtectiveExitAudit |
| `ProtectiveExitDiagnosticsService.cs` | 248 | ProtectiveExitDiagnosticsService |
| `ProtectiveExitLagAnalyzer.cs` | 410 | ProtectiveExitLagAnalyzer,ProtectiveExitLagRequest,ProtectiveExitLagObservation,ProtectiveExitLagReport |
| `ProtectiveExitShadow.cs` | 143 | ProtectiveExitShadow,IProtectiveExitShadowRecorder,ProtectiveExitShadowOptions,ProtectiveExitShadowRecorder |
| `Queries/GetLaneStatusQuery.cs` | 12 | GetLaneStatusQuery,GetLaneStatusQueryHandler |
| `Queries/GetOpenPositionsQuery.cs` | 12 | GetOpenPositionsQuery,GetOpenPositionsQueryHandler |
| `Queries/GetOrderHistoryQuery.cs` | 12 | GetOrderHistoryQuery,GetOrderHistoryQueryHandler |
| `Queries/GetPendingOrdersQuery.cs` | 12 | GetPendingOrdersQuery,GetPendingOrdersQueryHandler |
| `Queries/GetPerformanceQuery.cs` | 12 | GetPerformanceQuery,GetPerformanceQueryHandler |
| `RemoteTradingEngineClient.cs` | 180 | RemoteTradingEngineClient |
| `SafetyChecker.cs` | 122 | SafetyCheckResult,SafetyChecker |
| `SafetyConfiguration.cs` | 130 | SafetyConfiguration |
| `TradingContractMapper.cs` | 286 | TradingContractMapper |
| `TradingEngine.cs` | 1668 | TradingEngine |
| `TradingEntities.cs` | 95 | TradingEngineState,TradingAuditLog,LaneQuarantine |
| `TradingLanes.cs` | 87 | TradingLanes |
| `TradingModels.cs` | 280 | TradingMode,MarketType,OrderSide,OrderType,OrderStatus,TradingEngineStatus,OpenPosition,Order,TradingPerfor... |
| `TradingOrderQueries.cs` | 40 | TradingOrderQueries |
| `TradingPageService.cs` | 571 | TradingPageService,LaneStoryStrategy,LaneStoryInfo |
| `TradingServiceCollectionExtensions.cs` | 360 | TradingServiceCollectionExtensions |
| `TradingWorker.cs` | 193 | TradingWorker |

### `ProcioneMGR/Services/Validation/` — 10 file, 1154 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `BacktestOverfitting.cs` | 103 | PboResult,BacktestOverfitting |
| `CombinatorialPurgedCv.cs` | 106 | CpcvSplit,ICombinatorialPurgedCv,CombinatorialPurgedCv |
| `DeflatedSharpeRatio.cs` | 160 | ReturnMoments,DeflatedSharpeRatio |
| `EffectiveTrials.cs` | 100 | EffectiveTrials |
| `GatePowerAnalyzer.cs` | 192 | GatePowerAnalyzer,struct |
| `MinTrackRecord.cs` | 96 | MinTrackRecord |
| `NullTwinGenerator.cs` | 111 | NullTwinGenerator |
| `NullTwinJudge.cs` | 144 | NullTwinVerdict,INullTwinJudge,NullTwinJudge |
| `PermutationTest.cs` | 68 | PermutationTest,struct |
| `SelectionValidator.cs` | 74 | SelectionValidation,SelectionValidator |

### `tools/` — 5 file, 6632 righe

| File | Righe | Tipi principali |
|---|---:|---|
| `DbBackup/Program.cs` | 98 | — |
| `FuturesVerify/Program.cs` | 113 | — |
| `PlatformExpand/Program.cs` | 5848 | — |
| `SpotVerify/Program.cs` | 164 | — |
| `StrategyHunter/Program.cs` | 409 | — |
---

## UI — file `.razor` (89 file, 22.607 righe)

### `Components/Pages/` — pagine con rotta

| File | Righe | Rotta |
|---|---:|---|
| `Admin/AiSupervisor.razor` | 1389 | "/admin/ai-supervisor" |
| `Admin/Autonomy.razor` | 1974 | "/admin/autonomy" |
| `Admin/Backup.razor` | 199 | "/admin/backup" |
| `Admin/Protections.razor` | 533 | "/admin/protections" |
| `AdminUsers.razor` | 140 | "/admin/users" |
| `AlphaMining.razor` | 374 | "/alpha-mining" |
| `Backtest.razor` | 832 | "/backtest" |
| `Bot.razor` | 297 | "/bot" |
| `Campaign.razor` | 236 | "/campaign" |
| `Dashboard.razor` | 385 | "/dashboard" |
| `Discovery.razor` | 419 | "/discovery" |
| `Ensemble.razor` | 761 | "/ensemble" |
| `Error.razor` | 36 | "/Error" |
| `ExchangeSettings.razor` | 391 | "/settings/exchanges" |
| `ExecutionLab.razor` | 315 | "/execution" |
| `Experiments.razor` | 333 | "/experiments" |
| `FeatureSelection.razor` | 761 | "/feature-selection" |
| `Home.razor` | 288 | "/" |
| `InformationBars.razor` | 322 | "/market/bars" |
| `MarketAnalysis.razor` | 745 | "/market-analysis" |
| `Metrics.razor` | 358 | "/metrics" |
| `MlLab.razor` | 1021 | "/ml" |
| `NotFound.razor` | 4 | "/not-found" |
| `OhlcvChart.razor` | 110 | — (componente) |
| `Optimization.razor` | 736 | "/optimization" |
| `PairsTrading.razor` | 486 | "/pairs-trading" |
| `Pipeline.razor` | 705 | "/pipeline" |
| `PortfolioOptimization.razor` | 432 | "/portfolio" |
| `Regimes.razor` | 416 | "/regimes" |
| `Registry.razor` | 163 | "/registry" |
| `Sentiment.razor` | 1137 | "/sentiment" |
| `Strategies.razor` | 137 | "/strategies" |
| `Trading.razor` | 1574 | "/trading" |
| `Volatility.razor` | 288 | "/volatility" |
| `Watchlist.razor` | 307 | "/market/watchlist" |

### `Components/Layout/` e `Components/Shared/`

| File | Righe | Ruolo |
|---|---:|---|
| `Layout/MainLayout.razor` | 32 | |
| `Layout/NavMenu.razor` | 225 | |
| `Layout/NavModel.cs` | 229 | |
| `Layout/ReconnectModal.razor` | 31 | |
| `Shared/AdvancedPanel.razor` | 35 | |
| `Shared/Breadcrumb.razor` | 41 | |
| `Shared/CommandPalette.razor` | 190 | |
| `Shared/ConfigPresets.razor` | 139 | |
| `Shared/DataAvailability.razor` | 122 | |
| `Shared/GuidaPanel.razor` | 23 | |
| `Shared/LaneSelector.razor` | 125 | |
| `Shared/PollingTimer.cs` | 77 | |
| `Shared/Stat.razor` | 38 | |

### `Components/Account/` — boilerplate Identity

39 file `.razor` + 6 `.cs`. Scaffolding ASP.NET Core Identity, non modificato: registrazione, login, 2FA, gestione account, ruoli.

---

## Test — `ProcioneMGR.Tests/` (262 file, 53.700 righe)

Raggruppati per dominio coperto. Rapporto test:produzione ≈ **0,74:1** in righe.

| Dominio | File di test |
|---|---|
| **Sicurezza e invarianti** (16) | `AuditPromotionStateMachineTests`, `AuditSafetyKellyExtremeTests`, `DataProtectionApplicationNameTests`, `DormantFleetPromotionTests`, `ExchangeCredentialReaderTests`, `ExchangeSigningTests`, `LaneExecutionLeaseTests`, `LaneInvariantCheckerTests`, `LaneInvariantWatchdogTests`, `LanePromotionFlattenTests`, `MasterKeyGuardTests`, `MasterKeyProbeTests`, `PromotionEvaluatorTests`, `SafetyCheckerLeverageTests`, `SafetyCheckerTests`, `SafetySectionPersistenceTests` |
| **Trading ed esecuzione** (42) | `AuditStressNestedExecutionTests`, `BinanceClientOrderStatusTests`, `DynamicLanesTests`, `ExcursionBracketTests`, `ExecutionQualityTests`, `ExecutionSquareRootImpactTests`, `ExecutionTests`, `FuturesPositionReconcilerTests`, `LaneCountCoherenceProbeTests`, `LaneEpisodeTests`, `LaneRiskProfileEndToEndTests`, `LaneSelectorTests`, `MakerFillModelTests`, `MarginMathTests`, `MultiLaneIsolationTests`, `OrderFlowFactorsTests`, `OrderFlowImbalanceTests`, `ProtectiveExitAuditTests`, `ProtectiveExitDiagnosticsServiceTests`, `ProtectiveExitLagAnalyzerTests`, `ProtectiveExitMetricTests`, `ProtectiveExitShadowTests`, `RemoteTradingEngineClientTests`, `RestingBracketPersistenceTests`, `RestingStopOrderTests`, `TradingCommandHandlersTests`, `TradingContractMapperTests`, `TradingEngineChampionTests`, `TradingEngineCredentialDecryptTests`, `TradingEngineEquityRetentionTests`, `TradingEngineExecutionTests`, `TradingEngineFillSanityTests`, `TradingEngineFuturesEquityTests`, `TradingEngineReconcileTests`, `TradingEngineSizingTests`, `TradingEngineStopTests`, `TradingEngineTickExitTests`, `TradingGrpcRoundTripTests`, `TradingPageServiceTests`, `TradingQueryHandlersTests`, `TradingServiceCollectionExtensionsTests`, `TradingWorkerClosedBarTests` |
| **Backtest e costi** (16) | `BacktestCostAccountingTests`, `BacktestEngineTests`, `BacktestLeverageTests`, `BacktestPageServiceTests`, `BacktestStopLossTests`, `CarryBacktestEngineTests`, `ChandelierAndGridTests`, `CostPropagationTests`, `DonchianIndicatorAndStrategyTests`, `HourOfDaySignalTests`, `IntradayStrategiesTests`, `NewStrategiesTests`, `PairsBacktestEngineTests`, `PipelineCostsTests`, `SignalCatalogCacheTests`, `SignalReversalThrottleTests` |
| **ML e predittori** (26) | `AttentionReturnPredictorTests`, `AuditStressMlTrainingTests`, `FeatureImportanceTests`, `HierarchicalClusteringTests`, `LinearReturnPredictorTests`, `MetaLabelerTests`, `MetaLabelingAnalysisServiceTests`, `MetaModelTrainerTests`, `MlComparisonClientTests`, `MlDatasetBuilderTests`, `MlDeterminismTests`, `MlGrpcRoundTripTests`, `MlLabServiceTests`, `MlSavedModelIntegrationTests`, `MlStageMapperTests`, `MlStrategySequenceTests`, `MlStrategyTests`, `MlTargetKindTests`, `MlpReturnPredictorTests`, `PurgedTimeSeriesCvTests`, `RiskFactorPcaTests`, `ShapContextLensTests`, `StackedReturnPredictorTests`, `TreeReturnPredictorTests`, `TreeShapTests`, `TripleBarrierLabelerTests` |
| **Fattori, IC, alpha** (38) | `AiCommitteeTests`, `Alpha158FactorTests`, `AlphaFactorTests`, `AlphaMiningTests`, `AltDataSyncServiceTests`, `AuditAlpha158EdgeCaseTests`, `BotPageServiceTests`, `CandlestickPatternDetectorTests`, `ConfigurationUiCoverageTests`, `CyclicalAnalyzerTests`, `EnsemblePageServiceTests`, `FactorCacheTests`, `FactorDriftAnalyzerTests`, `FactorDriftMonitorTests`, `FactorIcTStatTests`, `ForexFactoryIngestorTests`, `GeneticMinerCvGateTests`, `IcFeatureSelectorTests`, `IncrementalIcGateTests`, `IndicatorsOnRealDataTests`, `MicrostructureParserTests`, `NotificationDispatcherTests`, `NotificationHttpLoggingTests`, `OptimizationPageServiceTests`, `OptimizationStatisticsTests`, `PipelineFunnelMetricsTests`, `PipelinePageServiceTests`, `RemoteMarketDataSyncServiceTests`, `SentimentAlphaFactorTests`, `SentimentFeatureFactorTests`, `SentimentMetricClientTests`, `SentimentMetricSyncServiceTests`, `SentimentScorerComparisonServiceTests`, `TearsheetStatisticsTests`, `TechnicalIndicatorsTests`, `TradeStatisticsTests`, `ValidationStatisticsTests`, `WebSocketPriceFeedTests` |
| **Validazione anti-overfitting** (7) | `BlockBootstrapPermutationTests`, `EffectiveTrialsTests`, `GatePowerAnalyzerTests`, `MinTrackRecordTests`, `NullTwinGeneratorTests`, `NullTwinJudgeTests`, `OverfittingGateTests` |
| **Ottimizzazione** (6) | `BayesianKernelFitTests`, `BayesianOptimizerTests`, `OptimizationComboKeyTests`, `OptimizationCpcvTests`, `OptimizationEmbargoTests`, `OptimizationSearchStrategyTests` |
| **Regime ed ensemble** (13) | `EnsembleAllocatorTests`, `EnsembleComparatorTests`, `EnsembleManagerDecayTests`, `EnsembleSimulationCacheTests`, `JumpModelTests`, `LaneRegimeRouterTests`, `RegimeAugmentationTests`, `RegimeAutoKTests`, `RegimeChangeTriggerTests`, `RegimeLabelWindowTests`, `RegimeRouterEngineTests`, `StrategyDecayMonitorTests`, `VolumeSignalsAndRegimeFeaturesTests` |
| **Portfolio e rischio** (11) | `AuditPortfolioDegenerateTests`, `CorrelatedExposureTests`, `HrpLinkageTests`, `KellyCalculatorTests`, `LeverageAdvisorTests`, `MonteCarloAnalyzerTests`, `PerformanceControlTests`, `PortfolioOptimizerTests`, `PortfolioShrinkageErcTests`, `ReturnMatrixBuilderTests`, `RiskProfileTests` |
| **Serie storiche e pairs** (7) | `CointegrationOnRealDataTests`, `CointegrationTests`, `GarchModelTests`, `HarRvForecasterTests`, `KalmanPairsSpreadAnalyzerTests`, `PairsVolFilterTests`, `RollingPairsSpreadAnalyzerTests` |
| **Pipeline e campagne** (15) | `AuditPipelineExperimentLoggingTests`, `CampaignPlannerTests`, `CreativeDiscoveryTests`, `DtwMatcherTests`, `DtwPatternAnalysisTests`, `EventStudyTests`, `EventTriggerGeneratorTests`, `PipelineAutoResumeTests`, `PipelineEngineConcurrencyTests`, `PipelineSchedulerWorkerTests`, `PipelineStopTargetVariantTests`, `PipelineSupervisorComparisonTests`, `PipelineSupervisorTests`, `PipelineTests`, `StrategyDiscoveryDefaultsTests` |
| **Sentiment, news, alt-data** (10) | `DelegatingSentimentScorerTests`, `KeywordSentimentScorerTests`, `LlmSentimentScorerTests`, `NewsImpactAnalyzerTests`, `NewsImpactClassifierTests`, `OnnxSentimentPilotTests`, `RetailSentimentIngestorTests`, `RssNewsSourceTests`, `SentimentCompositeCalculatorTests`, `SentimentSyncWorkerTests` |
| **Layer AI** (10) | `AiMultiProviderTests`, `DigestNarrativeTests`, `LlmCallGuardTests`, `LlmFailoverTimeoutTests`, `LlmUsageTests`, `PostMortemTests`, `ProviderCompatibilityTests`, `RejectionExplainIntegrationTests`, `RejectionExplainTests`, `SupervisorAgentTests` |
| **Dati, ingestion, exchange** (14) | `AddOhlcvIngestionTests`, `AuditStressIngestionTests`, `BarBuilderTests`, `BinanceDumpDownloaderTests`, `BitgetClientTests`, `FundingHistoryTests`, `KlineExtendedFieldsTests`, `LiquidationAccumulationTests`, `RealtimeFeedSwitchTests`, `RealtimeStreamMapperTests`, `SeriesFreshnessTests`, `SeriesFreshnessWatchWorkerTests`, `SymbolFiltersTests`, `TapeAggregatorTests` |
| **Drift e monitoraggio** (10) | `DriftDetectorTests`, `ExperimentTrackerTests`, `FeatureDriftMonitorTests`, `FeatureDriftWorkerPersistenceTests`, `FleetOrchestratorTests`, `FleetReaderEvaluateTests`, `HostHeartbeatTests`, `ModelRegistryTests`, `ObservabilityTests`, `TelegramNotifierTests` |
| **UI, config, infrastruttura** (18) | `AdminConfigRulesTests`, `AppConfigWriterTests`, `AuditBlazorUiTests`, `BotPageRenderTests`, `CarryEngineTests`, `ConfigurationBindingTests`, `ContractsSmokeTests`, `DatabaseMigratorTests`, `EngineConfigGrpcTests`, `EngineConfigTests`, `ExchangeSettingsPageTests`, `ExcursionAnalyzerTests`, `ExcursionHorizonTests`, `GapLapAnalyzerTests`, `PageConfigStoreTests`, `ProtectionsPageRenderTests`, `SupportResistanceTests`, `VolatilityScalerTests` |
| Altri (3) | `AuditCvLeakageTests`, `ExchangeRateLimitAndClockTests`, `StackingNonNegativeRidgeTests` |

**Nota.** I file con prefisso `Audit*` sono test nati da audit precedenti e presidiano
regressioni specifiche: `AuditCvLeakageTests` (leakage di CV), `AuditSafetyKellyExtremeTests`
(Kelly a valori estremi), `AuditPromotionStateMachineTests` (macchina a stati delle promozioni),
`AuditStressNestedExecutionTests`, `AuditPortfolioDegenerateTests`, `AuditBlazorUiTests`.
Sono, di fatto, le invarianti rese eseguibili.
