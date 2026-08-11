# 27 — INVENTARIO DEI TEST: cosa è realmente garantito

> Ogni file di test, ogni classe, **ogni metodo di test**. È l'elenco delle garanzie
> effettive della piattaforma: ciò che non è qui, non è verificato automaticamente.

| | |
|---|---:|
| File di test | 276 |
| Classi di test | 276 |
| **Metodi di test** | **2164** |
| Righe di test | 55.600 |

I file con prefisso `Audit*` nascono da audit precedenti e presidiano regressioni specifiche:
sono le invarianti rese eseguibili.

---

## `FactorDriftMonitorTests.cs` — 32 test, 850 righe

> Lo store della storia dell'IC, sul database vero: le due domande che la UI gli fa (la serie di un fattore, la fotografia di una serie) e il caso spinoso della griglia che cambia ampiezza.

`AnEmptySnapshot_HasNoAlertsAndNoRun`, `Alerts_PutTheMostSevereFirst`, `Replace_SwapsTheWholePictureInsteadOfMerging`, `SeriesSnapshot_ExposesOnlyItsAlerts`, `RunOnce_PopulatesTheSnapshotForEachTrackedSeries`, `RunOnce_OnASeriesWhereAFactorDies_RaisesAnAlert`, `RunOnce_SkipsSeriesWithTooFewCandles`, `RunOnce_IgnoresDisabledSeries`, `RunOnce_RespectsTheMaxSeriesCap`, `RunOnce_WithNoTrackedSeries_LeavesAnEmptyButValidSnapshot`, `RunOnce_WritesTheIcWindowsToTheHistory`, `RunningTwice_UpdatesTheSameRowsInsteadOfDuplicatingThem`, `AfterAShellRestart_TheAlertIsAlreadyThereWithoutRecomputing`, `Hydrate_DoesNotResurrectASeriesRemovedFromTheWatchlist`, `Hydrate_WithAnEmptyHistory_LeavesTheSnapshotEmptyAndSilent`, `OnPureNoise_TheHistoryRecordsWindowsButRaisesNoAlert`, `WindowSizeFor_IsQuantizedSoTheRecordedSeriesKeepsOneDefinition`, `TheJobAndThePanel_ProposeTheSameWindowOnTheSameSample`, `TheJobRotates_TheSecondRoundPicksTheSeriesItHasNotSeenYet`, `ASeriesThatCannotBeComputed_DoesNotStarveTheOthers`, `AfterARotatedRound_TheSnapshotStillShowsTheSeriesOfPreviousRounds`, `RunOnce_WithAChampion_AlsoWatchesItsFactors_ButNotThoseOfStaging`, `RunOnce_AChampionOnAnotherSeries_AddsNothingHere`, `RunOnce_BrokenOrUnknownChampionFactors_DegradeWithoutBreakingTheRound`, `TheSnapshotDeclaresHowManySeriesAreInTheWatchlist`, `SaveThenLoad_GivesBackTheSeriesInChronologicalOrder`, `SavingTheSameWindowTwice_OverwritesTheValueAndInsertsNothing`, `WhenTheWindowSizeChanges_OnlyTheMostRecentGridIsReturned`, `WindowsFromDifferentRounds_AreNeverReturnedOverlapping`, `LoadSnapshot_ReturnsOnlyTheRequestedSeries`, `LoadSeries_ForAFactorNeverRecorded_IsEmptyNotAnError`, `DifferentForwardHorizons_AreDifferentSeriesAndDoNotMix`

## `TradingPageServiceTests.cs` — 28 test, 563 righe

> Test dell'orchestrazione estratta da Trading.razor (P1-5, audit consolidamento 2026-07-17): prima di questa estrazione, questa logica viveva nel @code del componente e non aveva test indipendenti da Blazor — solo i comportamenti visibili in markup erano coperti (bUnit, AuditBlazorUiTests). Qui si verificano i dettagli di comportamento che il markup non esercita direttamente: la gestione della staleness gRPC, la validazione delle soglie di sicurezza, il parsing degli edit SL/TP/Trailing.

`RefreshAsync_Success_PopulatesStateAndClearsStaleness`, `RefreshAsync_RpcException_SetsStaleSinceOnlyOnFirstFailure`, `RefreshAsync_SuccessAfterFailure_ClearsStaleness`, `RefreshAsync_NonRpcException_IsSwallowed_WithoutTouchingStaleness`, `StartAsync_CallsEngineStart_WithGivenMode_AndSetsSuccessMessage`, `StartAsync_EngineThrows_SetsErrorMessage`, `StopAsync_CallsEngineStop_AndSetsMessage`, `EmergencyAsync_CallsEngineEmergencyStop_WithFixedReason`, `CloseAsync_CallsEngineClosePosition_WithGivenPositionId`, `ConfirmAsync_CallsEngineConfirmOrder_WithGivenOrderIdAndUserId`, `RejectAsync_CallsEngineRejectOrder_WithGivenOrderIdAndUserId`, `SaveSafetyAsync_InvalidValues_RejectsWithoutCallingWriter`, `SaveSafetyAsync_NegativeFeePercent_RejectsWithoutCallingWriter`, `SaveSafetyAsync_ZeroFeePercent_IsAccepted`, `RefreshPromotions_AfterAFailure_ClearsTheErrorOnTheNextSuccess`, `RefreshPromotions_KeepsReportingWhileTheFailurePersists`, `SaveSafetyAsync_ValidValues_PersistsAndReportsSuccess`, `SaveSafetyAsync_WriterThrows_ReportsErrorMessage`, `SaveSafetyAsync_EngineWarning_IsSurfacedToTheOperator`, `ReloadSafetyAsync_EngineUnreachable_DeclaresDefaultsInsteadOfPassingThemOffAsReal`, `ReloadSafetyAsync_ReadsWhatTheEngineApplies_NotTheShellFile`, `SlValue_NotEdited_FallsBackToPositionValue`, `SlValue_Edited_TakesPrecedenceOverPositionValue`, `ParseLevel_ValidatesPositiveDecimalsOnly`, `SaveSlTpAsync_SendsEditedValues_ThenClearsThePendingEdit`, `PromoteAsync_TargetLaneMatchesViewedLane_RefreshesEngineStatus`, `PromoteAsync_TargetLaneDiffersFromViewedLane_DoesNotRefreshEngineStatus`, `PromoteAsync_PromoterThrows_ReportsErrorMessage_AndStopsBeingBusy`

## `AiMultiProviderTests.cs` — 26 test, 584 righe

`Nvidia_SendsOpenAiShape_AndParsesContent`, `Nvidia_HttpError_SurfacesStatusAndBody`, `Nvidia_MissingKey_FailsWithTheRemedy`, `Nvidia_EmptyContent_IsAnExplainedError_NotAnEmptyAdvisory`, `Guard_ClassifiesNvidiaErrors_BySameTaxonomy`, `NewProviders_SendToTheirDefaultEndpoint_WithTheirModel`, `NewProviders_MissingKey_FailsWithTheirEnvVarRemedy`, `Guard_ClassifiesAnyCompatProvider_BySameTaxonomy`, `EnvVarNames_AreTheDocumentedOnes`, `Failover_ActiveFails_NextInChainServes_AndTruthIsTraceable`, `Failover_SkipsProvidersWithoutKey`, `Failover_AllFail_ThrowsLastError_ClassifiableByGuard`, `Failover_Disabled_OnlyActiveIsTried`, `Failover_CancellationIsNotAFailure_NoHopping`, `AutoSelector_Gemini_PicksLatestNonPreviewFlash_FromTheRealCatalogShape`, `AutoSelector_Groq_PrefersVersatileLlama`, `AutoSelector_EmptyOrAllNonChat_ReturnsNull`, `AutoSelector_UnknownProviderStillPicksSomethingChatLike`, `FailoverChain_EmptyConfigUsesDefault_PollutedConfigIsDeduplicated`, `ListModels_SendsGetToModelsEndpoint_AndParsesSortedIds`, `ListModels_HttpError_KeepsTheSpeakingContract`, `ListModels_Anthropic_UsesItsOwnDialect`, `Delegating_WithResolver_RoutesToEveryKnownProvider`, `Delegating_RoutesByProvider_HotReload`, `SetGetRemove_Roundtrip_WithSourceReporting`, `DatabaseKey_SurvivesProcessRestart_ViaReload`

## `PipelineTests.cs` — 26 test, 677 righe

`TypedGetters_ParseInvariantCulture_AndFallBack`, `ValidChain_NoProblems`, `MissingDependency_IsReported`, `AnyOfDependency_SatisfiedByEitherStage`, `DependencyOrderedAfter_IsNotSatisfied`, `UnknownStage_IsReported`, `NoEnabledStages_IsReported`, `ApplyVariant_SetsTheRightStops`, `ProfitFactor_ComputedFromTrades`, `Context_RoundTripsThroughJson`, `WithSurvivor_TemplateContainsTheNumbers`, `NoSurvivors_SaysDoNotTrade`, `Deterministic_SameContextSameText`, `WithMoodSnapshot_LabelComesFromComposite_AndFieldsArePopulated`, `WithMoodExtremes_TheyBecomeAlerts_AndAppearInFullText`, `WithoutSnapshot_LegacyPathIsUnchanged`, `NormalVol_SizingIsFractionalKellyCapped`, `HighVol_ReducesSizing`, `DuplicateStrategySymbolTimeframe_DifferentParameters_BothSizedCorrectly`, `SingleLeg_CarriesBestStopVariantIntoProposedLeg`, `LiveMode_NeverAutoExecutes_AndWarnsAboutSafety`, `PaperMode_ProducesActionsFromLegs`, `FeatureEngineering_EvaluatesAndSelectsFactors_Deterministically`, `VolatilityRegime_FitsGarchAndClassifies`, `PairsScreening_TestsEveryPairOnce`, `ValidateInput_PairsScreening_RequiresTwoSymbols`

## `AdminConfigRulesTests.cs` — 25 test, 248 righe

> Validazione lato server dei pannelli admin. Il rischio che copre non è teorico: l'attributo min= di un input HTML non vincola il binding di Blazor, quindi prima di qualunque numero digitato finiva in appsettings.json — e da lì nei worker, che in alcuni casi (intervallo 0 su un PeriodicTimer) muoiono all'avvio successivo. I test qui sotto sono divisi in due gruppi: i DEFAULT devono passare tutti (una regola che rifiuta la configurazione di fabbrica è un bug della regola), e ogni regola ha il suo caso patologico.

`Defaults_AreAlwaysAccepted`, `UnknownType_PassesThrough`, `Drift_IntervalZero_IsRejected`, `LiveExecution_TickBelowFiveSeconds_IsRejected`, `Llm_MaxTokensTooSmall_IsRejected`, `Safety_PanelInvariants_AreAlsoEnforcedServerSide`, `Safety_NegativeFee_IsRejected`, `Safety_VolTargetingOnWithZeroTarget_IsRejected`, `Safety_ExposureMultiplierRangeInverted_IsRejected`, `Safety_ZeroFillDeviationBands_AreRejected`, `Promotion_HardBlockBelowMaxDrawdown_IsRejected`, `Promotion_DemoteThresholdAbovePromoteThreshold_IsRejected`, `Carry_ExitAboveEnter_IsRejected`, `Carry_LiveMode_IsRejected`, `Sentiment_FearGreedBoundsInverted_IsRejected`, `Realtime_BackoffCeilingBelowInitialDelay_IsRejected`, `CorrelatedExposure_LookbackBelowMinimumOverlap_IsRejected`, `RegimeRouting_DuplicateRegimeIds_AreRejected`, `RegimeRouting_EmptyStrategyList_IsAccepted`, `Notifications_TelegramWithoutChatId_IsRejected`, `Ml_DualReadEnabledWithoutUrl_IsRejected`, `Registry_DeflatedSharpeOutsideProbabilityRange_IsRejected`, `Execution_ReferenceVolatilityZero_IsRejected`, `LaneInvariants_ZeroMultipliers_AreRejected`, `RegimeTrigger_VolBandOfOne_IsRejected`

## `RejectionExplainTests.cs` — 24 test, 483 righe

> [G6] Spiegazione dei candidati bocciati dai gate. Il contratto che questi test difendono, in ordine di importanza: il riassunto è DETERMINISTICO e non dipende dall'AI (livello 2: AI spenta ⇒ digest identico); l'AI non può far comparire in pagina un candidato che non esiste (le note con chiavi inventate si scartano, contate); il classificatore delle cause resta allineato ai messaggi VERI del motore, o lo dice.

`Classify_SenzaMotivo_DichiaraLIgnoranza`, `Classify_MotivoSconosciuto_FinisceInOther`, `Label_CopreOgniCausaProdottaDalClassificatore`, `Build_ListaVuota_DigestVuoto`, `Build_TuttiSopravvissuti_NessunaBocciatura`, `Build_RaggruppaPerCausaEContaTutti`, `Build_TopN_LimitaSoloIlDettaglioNonIConteggi`, `Build_OrdineDeterministico_SuSharpeUguali`, `Build_ContaLaFasciaGrigiaCollFiltroCondiviso`, `Build_PortaINumeriVeriDelVerdetto`, `BuildPrompt_ContieneNumeriVeriEChiavi`, `Parse_TieneSoloLeNoteConChiaviInviate`, `Parse_ModelloCheInventaTutto_NonProduceNemmenoUnaNota`, `Parse_ChiaveRipetuta_UnaSolaVolta`, `Parse_NoteVuoteScartate`, `Parse_SopravviveAiFencesMarkdown`, `Parse_RispostaSenzaJson_Lancia`, `Narrate_DigestVuoto_NonChiamaLAi`, `Narrate_AiNonDisponibile_RestituisceNullSenzaEccezioni`, `Narrate_RispostaIllegibile_RestituisceNull`, `Narrate_RispostaBuona_PortaModelloENote`, `Narrate_UsaIlPathDichiarato`, `AdminConfigRules_ValidaIlTettoDeiBocciatiRiportati`, `LlmOptions_SpiegazioneSpentaPerDefault`

## `TearsheetStatisticsTests.cs` — 23 test, 289 righe

> Test delle metriche estese del tearsheet (Sortino, Calmar, Omega, VaR/CVaR, drawdown duration, exposure, hit-rate).

`Sortino_NoDownside_IsZero_NoDivideByZero`, `Sortino_PositiveTrend_WithSomeLosses_IsPositive`, `Sortino_TooFewPoints_IsZero`, `AnnualizedReturn_KnownDoubling_MatchesHandComputed`, `AnnualizedReturn_ExtremeExtrapolation_ReturnsZero_NoOverflow`, `CalmarRatio_NoDrawdown_IsZero_NoDivideByZero`, `CalmarRatio_WithDrawdown_IsPositive_ForNetGain`, `Omega_NoLosses_IsZero_NoDivideByZero`, `Omega_MoreGainsThanLosses_IsGreaterThanOne`, `TailRatio_SymmetricReturns_IsCloseToOne`, `HistoricalVaR_IsPositive_ForLosingTail`, `HistoricalCVaR_IsAtLeastAsSevereAsVaR`, `MaxDrawdownDuration_RecoveredDrawdown_CountsUntilNewHigh`, `MaxDrawdownDuration_NeverRecovered_CountsUntilEnd`, `MaxDrawdownDuration_MonotonicUp_IsZero`, `ExposurePercent_HalfTimeInMarket_IsAboutFifty`, `ExposurePercent_OpenTradeAtEnd_ClampsToCurveEnd`, `HitRate_MixedTrades_MatchesRatio`, `HitRate_NoTrades_IsZero`, `ComputeTearsheet_ReturnsAllFieldsConsistentWithIndividualMethods`, `DeflatedSharpeSingleTrack_TooShortOrNullCurve_IsNull`, `DeflatedSharpeSingleTrack_StrongSteadyTrack_IsSignificant`, `DeflatedSharpeSingleTrack_ZeroDriftNoise_IsNotSignificant`

## `DtwMatcherTests.cs` — 22 test, 387 righe

> [D4] Verifica del matching per forma. Segue i quattro livelli di `docs/STANDARD-VERIFICA.md`. I due test che decidono se il pezzo vale qualcosa: - — se LB_Keogh NON fosse un vero limite inferiore, il pruning scarterebbe in silenzio proprio le corrispondenze migliori, e tutto il resto sarebbe costruito sulla sabbia. - — il gate non negoziabile della roadmap: prima di fidarsi di un risultato su dati reali, la macchina deve ritrovare un pattern che sappiamo esserci.

`ZNormalize_ProducesZeroMeanUnitDeviation`, `ZNormalize_OnAConstantSeries_ReturnsZerosInsteadOfNaN`, `ZNormalize_MakesTheSameShapeIdenticalAtAnyPriceLevel`, `IdenticalSequences_HaveZeroDistance`, `DtwToleratesTimeStretching_WhereEuclideanWouldNot`, `DistanceIsSymmetric`, `EmptySequences_GiveInfiniteDistanceInsteadOfThrowing`, `ANarrowBandStillAllowsDifferentLengths`, `LowerBound_IsNeverGreaterThanTheRealDistance`, `LowerBound_IsZeroForIdenticalSequences`, `PlantedPattern_IsFoundAtThePlantedPositions`, `PlantedPattern_IsFoundEvenWhenStretchedInTime`, `PureNoise_DoesNotProduceAFloodOfMatches`, `OverlappingMatches_AreCollapsedIntoOne`, `MatchesAreReturnedInChronologicalOrder`, `EventSeries_MarksTheClosingBar_NotTheOpeningOne`, `EventSeries_IgnoresMatchesOutsideTheSeries`, `DegenerateInputs_DoNotThrow`, `AFlatSeries_DoesNotMatchAShapedPattern`, `RandomFuzzing_AlwaysProducesFiniteNonNegativeDistances`, `LargeSeries_CompletesInReasonableTime`, `SearchIsDeterministic`

## `ValidationStatisticsTests.cs` — 22 test, 282 righe

> Test della libreria di rigore statistico (Fase 1): Deflated/Probabilistic Sharpe, Combinatorial Purged CV e Probability of Backtest Overfitting. Verifica identità esatte note dalla letteratura, monotonicità e i due comportamenti-cardine: pannello di rumore ⇒ PBO≈0.5 e DSR non significativo; edge reale e persistente ⇒ PBO≈0 e DSR alto. Tutto deterministico (RNG seedato).

`PerPeriodSharpe_ZeroVariance_ReturnsZero`, `PerPeriodSharpe_ZeroMean_ReturnsZero`, `Skewness_SymmetricSeries_IsApproximatelyZero`, `Kurtosis_DegenerateSeries_ReturnsGaussianValue`, `ProbabilisticSharpe_BenchmarkEqualsObserved_IsExactlyHalf`, `ProbabilisticSharpe_IsMonotonicInObservedSharpe`, `ExpectedMaxSharpe_SingleTrial_IsZero`, `ExpectedMaxSharpe_GrowsWithNumberOfTrials`, `Deflated_MoreTrials_LowersTheDeflatedSharpe`, `Deflated_StrongEdgeFewTrials_IsSignificant`, `Deflated_IsDeterministic`, `Cpcv_NumberOfSplits_EqualsBinomialCoefficient`, `Cpcv_TrainAndTest_NeverOverlap`, `Cpcv_PurgeAndEmbargo_ExcludeBandsAroundTestGroups`, `Cpcv_InvalidTestGroups_Throws`, `Combinations_AreDistinctSortedAndLexicographic`, `SelectionValidator_StrongChosenFewTrials_IsSignificant`, `SelectionValidator_MoreTrials_LowerDeflatedSharpe`, `Pbo_PureNoisePanel_IsNearOneHalf`, `Pbo_OnePersistentEdge_IsLow`, `Pbo_IsDeterministic_ForSameInput`, `Pbo_InvalidPartitions_Throws`

## `BitgetClientTests.cs` — 20 test, 419 righe

> Regressione per un bug reale trovato verificando dal vivo le credenziali Bitget appena configurate dall'utente: usava l'endpoint "singolo account" (.../account/account) senza il parametro "symbol" che Bitget richiede, ottenendo sempre un errore applicativo (code 400172 "Parameter verification failed") — errore che veniva ingoiato silenziosamente restituendo un FuturesBalance vuoto, indistinguibile da un vero saldo zero. Fix: usa l'endpoint "lista account" (.../account/accounts), che con il solo productType restituisce l'array di conti per moneta di margine.

`GetFuturesBalanceAsync_ParsesUsdtAccount_FromAccountsListEndpoint`, `GetFuturesBalanceAsync_EmptyAccountsArray_ReturnsZeroWithoutThrowing`, `GetFuturesBalanceAsync_ApplicationError_ReturnsZero_DoesNotThrow`, `GetOrderStatusAsync_SpotFilled_ParsesPriceAvgBaseVolumeAndOrderId`, `GetOrderStatusAsync_SpotLiveUnfilled_EmptyDecimalFieldsTolerated`, `GetOrderStatusAsync_EmptyDataArray_IsCertainNotFound`, `GetOrderStatusAsync_Error43001_IsCertainNotFound_NotUncertain`, `GetOrderStatusAsync_OtherApplicationError_IsUncertain_NeverNotFound`, `GetOrderStatusAsync_Http500_IsUncertain`, `GetFuturesOrderStatusAsync_MixDetail_ParsesStateFieldAndDemoProductType`, `PlaceOrderAsync_LookupFilled_PopulatesRealFill`, `PlaceOrderAsync_LookupFails_PlaceStillSucceeds_FillNull`, `PlaceOrderAsync_SpotMarketBuy_NotVerified_RejectedWithoutNetworkCall`, `PlaceOrderAsync_SpotMarketBuy_VerifiedByConfig_GoesThrough`, `PlaceOrderAsync_SpotLimitBuy_NotAffectedByGuard`, `PlaceFuturesOrderAsync_StillOpenThenFilled_OneRetryRecoversFill`, `DemoSymbolHint_OnTestnet_ExplainsThatTheDemoSimulatesFewContracts`, `DemoSymbolHint_OnLive_LeavesTheErrorAlone`, `DemoSymbolHint_OnAnyOtherError_ChangesNothing`, `PlaceFuturesOrderAsync_OnDemoWithUnsupportedSymbol_ReturnsTheExplainedError`

## `IncrementalIcGateTests.cs` — 20 test, 454 righe

> [D3, il gate] Verifica del giudice che risponde alla domanda di C5 §3.3: il book aggiunge informazione OLTRE al proxy trade-flow? Tre livelli, gli stessi che la piattaforma pretende da qualunque misura nuova (vedi docs/STANDARD-VERIFICA.md): 1. RIFERIMENTO INDIPENDENTE — la correlazione parziale calcolata per due strade diverse (formula chiusa vs residui di una regressione) deve dare lo stesso numero; 2. EDGE PIANTATO — se l'informazione incrementale c'è, il gate DEVE trovarla, altrimenti un esito negativo su dati veri direbbe solo "il giudice non funziona"; 3. RUMORE PURO — su tanti semi diversi il gate non deve produrre nemmeno un falso positivo.

`PartialSpearman_TwoIndependentRoutesGiveTheSameNumber`, `PartialSpearman_WhenTheCandidateIsTheProxy_IsZeroNotUndefined`, `PartialSpearman_RemovesWhatTheProxyAlreadyExplains`, `APlantedIncrementalEdge_IsFound`, `ACandidateThatOnlyEchoesTheProxy_IsRejected`, `PureNoise_FalsePositiveRate_StaysAtItsNominalLevel`, `AnEdgeTooSmallToPayTheCosts_IsRejectedEvenIfStatisticallyReal`, `PartialSpearmanMulti_WithOneControl_AgreesWithTheTwoSeriesFormula`, `WithTwoControls_ACandidateThatIsAMixOfThem_IsRejected`, `WithTwoControls_AGenuinelyNewSignal_IsStillFound`, `WithNoControls_TheGateRefusesToJudge`, `ASignalThatInformsButCannotPayTheRoundTrip_IsNotCalledTradable`, `WithNoCostModel_TheSecondLevelIsSilentInsteadOfInventingNumbers`, `AnEdgeBigEnoughToPayTheRoundTrip_IsCalledTradable`, `TheNullOfTheBest_IsStricterThanTheNullOfASingleCandidate`, `RowsWhereAnythingIsMissing_AreDroppedForEveryone`, `TooFewObservations_DeclaresInsteadOfGuessing`, `RanksOfARotatedSeries_AreTheRotatedRanks`, `TheOptimisedNull_GivesTheSameNumbersAsTheStraightforwardOne`, `TheGateIsDeterministic_SameInputSameVerdict`

## `OptimizationPageServiceTests.cs` — 20 test, 508 righe

> Test dell'orchestrazione estratta da Optimization.razor (P1-5, PRD-CONSOLIDAMENTO- ARCHITETTURA.md §3.3): prima di questa estrazione tutta la logica — range di default per strategia, preset validati, handoff da Backtest/ML Lab col ricentraggio dei range, costruzione della config di sweep (incluso il range "pinnato" SavedModelId per i modelli ML), parsing della matrice heatmap e salvataggio della configurazione migliore — viveva nel blocco @code del componente, senza test indipendenti da Blazor. Il motore di ottimizzazione qui è un fake che cattura la config e restituisce un risultato predefinito: il walk-forward reale ha già i propri test — questo file verifica l'ORCHESTRAZIONE, alla giusta altitudine.

`DefaultRangesFor_Ml_ReturnsThresholdRangesOnly`, `DefaultRangesFor_RuleStrategy_AppliesIntegerHeuristicAndStepShape`, `TotalCombinations_CartesianProduct_AndZeroStepGuard`, `ApplyConfig_RoundTrip_WithRangeOverlay`, `ApplyConfig_InvalidStrategy_KeepsCurrent_AndZeroesMlModelForNonMl`, `ApplyConfig_MlStrategy_KeepsModelId_AndThresholdRanges`, `ApplyHandoff_FromBacktest_RecentersRangesOnParameters`, `ApplyHandoff_FromMlLab_SelectsModelAndThresholds`, `ApplyHandoff_NoContext_NoMessage_MalformedParameters_DefaultRanges`, `RunAsync_MlWithoutModel_ReturnsError_WithoutInvokingEngine`, `RunAsync_HappyPath_BuildsConfigAndPopulatesState`, `RunAsync_MlStrategy_AppendsPinnedSavedModelIdRange`, `RunAsync_Cpcv_PopulatesCpcvResult_NotWalkForwardResult`, `RunAsync_CpcvWithBayesian_ReturnsError_WithoutInvokingEngine`, `RunAsync_NewRun_ClearsPreviousCpcvResult`, `SaveBestAsync_CpcvRun_PersistsModalParameters_WithMedianSharpe`, `ApplyConfig_CpcvFields_RoundTrip_AndLegacyPresetDefaultsToWalkForward`, `BuildHeatmapMatrix_ParsesGrid_WithNullForUnvisitedCombos`, `SaveBestAsync_NoRun_ReturnsNull_BlankName_Error_HappyPath_PersistsOptimized`, `BacktestHandoffUrl_FallsBackWithoutRun_FullUrlAfterRun`

## `PipelineSchedulerWorkerTests.cs` — 19 test, 483 righe

> Test di integrazione di con un DB Postgres reale (Testcontainers) e un scriptato, per controllare esattamente quando il motore viene invocato senza dipendere da un run pipeline reale (lento, non deterministico).

`IsDue_NextRunAtNull_ReturnsTrue`, `IsDue_NextRunAtInPast_ReturnsTrue`, `IsDue_NextRunAtInFuture_ReturnsFalse`, `ComputeNextRun_DailyExpression_ReturnsNextMidnight`, `ComputeNextRun_InvalidExpression_ReturnsNull`, `ComputeNextRun_IsDeterministic_SameInputsSameOutput`, `TickAsync_DueEnabledPaperConfig_LaunchesScheduledRun`, `TickAsync_NextRunInFuture_DoesNotLaunch`, `TickAsync_ScheduleDisabled_DoesNotLaunch`, `TickAsync_LiveMode_SkipsLaunch_ButAdvancesNextRunAt`, `TickAsync_TriggerIsScheduled_PassedToEngine`, `TickAsync_EngineBusy_DoesNotAdvanceNextRunAt_RetriesNextTick`, `TickAsync_EngineThrowsOtherError_DoesNotCrash_AdvancesNextRunAt`, `TickAsync_InvalidCronExpression_DoesNotCrash_DoesNotLaunch`, `TickAsync_MultipleDueConfigs_EachEvaluatedIndependently`, `ProcessCompletedRuns_BetterCandidate_AppliesAndRecordsDecision`, `ProcessCompletedRuns_SupervisorVeto_DoesNotApply_ButRecordsDecision`, `ProcessCompletedRuns_SupervisorVeto_NotifiesExactlyOnce`, `ProcessCompletedRuns_IsIdempotent_DoesNotReprocess`

## `CampaignPlannerTests.cs` — 17 test, 576 righe

> Test del Campaign Planner (Fase 1, PRD Autonomia Operativa §4) con motore pipeline FAKE (stesso approccio dei PipelineSchedulerWorkerTests): rotazione su 0 sopravvissuti, backoff, stop-su-successo (Observing + avvio corsie Paper), rotazione-esaurita → WaitingForTrigger, ripresa-su-wake con trigger "Event", gate globale e per-campagna, slot singolo occupato.

`Tick_GlobalGateOff_DoesNothing`, `Tick_CampaignDisabled_DoesNothing`, `Tick_StartsFirstConfig_WithCampaignTrigger_AndSetsPending`, `Tick_PendingRunStillRunning_Waits`, `NoSurvivors_RotatesToNextConfig_ThenExhaustion_WaitsForTrigger`, `Survivors_Applied_StopsRotation_Observing_StartsOnlyStoppedPaperLanes`, `Survivors_QuarantinedLane_ApplyProceeds_StartFailureIsNotFatal`, `Survivors_NotApplied_RotationContinues`, `FailedRun_MarksConfig_AndRotationContinues`, `Wake_ResumesWaitingCampaign_NextRunHasEventTrigger_AndBypassesBackoff`, `Wake_DoesNotTouchObservingCampaigns`, `Realign_StoppedPaperLane_RestartedOnce_EmergencyLaneOnlyNotified`, `Realign_NonPaperStoppedLane_NotifiedNotRestarted`, `Realign_RunningLanes_NoActionNoNoise`, `Notifications_EmittedOnApplied_AndOnExhaustion`, `SlotBusy_RetriesOnNextTick`, `LiveConfig_IsSkipped_NextConfigUsed`

## `LlmUsageTests.cs` — 17 test, 414 righe

> Il giro completo su Postgres: flush idempotente e ripresa dei totali dopo un riavvio.

`TrackingOff_CountsNothing_EnforcesNothing`, `DailyCallLimit_ExhaustsAtTheThreshold`, `DailyTokenLimit_SumsPromptAndCompletion`, `ZeroLimits_MeanNoCeiling`, `MidnightRollover_ResetsTheDailyBudget_AndRearmsTheNotification`, `MonthlyLimit_SurvivesTheDailyRollover`, `Snapshot_GroupsByProviderModelPath`, `ExhaustedBudget_SkipsWithoutCalling_AndWithoutMovingTheBreaker`, `ExhaustedBudget_NotifiesOncePerTransition`, `ForceProbe_DoesNotBypassTheBudget`, `AvailableBudget_LetsTheCallThrough_WithThePathInContext`, `Usage_IsAttributedToTheServingProvider_WithAmbientPath`, `NoAmbientPath_FallsBackToDirect`, `MissingUsageField_DoesNotBreakTheCall`, `ReasoningThatEatsAllTokens_StillCountsTheUsage`, `Flush_UpsertsAggregatedRows_AndIsIdempotent`, `Restart_ResumesTodaysBudget_FromTheDatabase`

## `NotificationDispatcherTests.cs` — 17 test, 284 righe

> Test del canale di notifica (Fase 4, PRD Autonomia §7): gate default OFF, selezione provider, rate-limit a finestra scorrevole con coalescing (i soppressi vengono riportati, mai persi in silenzio), e la garanzia più importante per i producer: il dispatcher NON propaga MAI — una notifica fallita non deve far cadere un watchdog o un planner.

`DisabledByDefault_NothingIsSent`, `Enabled_RoutesToSelectedProvider_CaseInsensitive`, `UnknownProvider_DoesNotThrow_AndSendsNothing`, `ProviderFailure_IsSwallowed_ProducerNeverFails`, `RateLimit_SuppressesExcess_AndReportsCoalescedCount`, `RateLimit_WindowSlides_OldSendsExpire`, `Diagnostic_ReportsDelivered_WhenTheProviderAccepts`, `Diagnostic_ReportsFailure_WithTheReason_WhenTheProviderThrows`, `Diagnostic_DistinguishesDisabled_FromDelivered`, `Diagnostic_ReportsUnknownProvider_InsteadOfPretendingItWorked`, `Diagnostic_SharesTheRateLimitWithTheProducers_NotAParallelBudget`, `Producers_StillNeverSeeAnException_WhenTheProviderThrows`, `ChannelSpy_RecordsAFailedDelivery_WithReasonAndTime`, `ChannelSpy_ConsecutiveFailures_Accumulate`, `ChannelSpy_ADeliveryResetsTheFailureCounter_ButKeepsTheHistory`, `ChannelSpy_UnknownProvider_CountsAsFailure`, `ChannelSpy_DisabledAndRateLimited_AreNotFailures`

## `TradingContractMapperTests.cs` — 17 test, 288 righe

> Unità della mappatura dominio↔trading.proto (Fase 2b). Copre le due classi di errore che il round-trip gRPC da solo non isolerebbe: la conversione dei decimal (denaro) e la mappatura degli enum (dove un cast ordinale trasformerebbe Paper in Testnet).

`DecimalValue_NegativeValue_HasNanosWithTheSameSignAsUnits`, `DecimalValue_RoundingCarry_DoesNotProduceInvalidNanos`, `DecimalValue_BeyondNineDecimals_RoundsInsteadOfTruncating`, `DecimalValue_Malformed_Throws`, `DecimalValue_ZeroUnits_AllowsEitherSignOfNanos`, `DecimalValue_Nullable_KeepsAbsentDistinctFromZero`, `TradingMode_RoundTrips`, `TradingMode_IsNotMappedByOrdinal`, `TradingMode_Unspecified_Throws`, `MarketType_RoundTrips`, `MarketType_Unspecified_Throws`, `OrderSide_RoundTrips`, `OrderSide_Unspecified_Throws`, `Timestamp_AcceptsUnspecifiedKind_FromPostgres`, `OpenPosition_RoundTrips_IncludingOptionalFields`, `Performance_RoundTrips_WithEquityCurveAndTrades`, `LaneStatus_EmptyReason_MapsBackToNull`

## `WebSocketPriceFeedTests.cs` — 17 test, 499 righe

> [R1] Test del ciclo di vita della connessione real-time, con un transport finto al posto della rete: connessione, sottoscrizione, riconnessione dopo una caduta, tolleranza ai frame inutili, filtro sulle quotazioni implausibili e rilevamento di staleness. Il comportamento più importante è che una CADUTA È NORMALE: la rete cade, e un feed che non riprende da solo lascia gli stop ciechi senza che nessuno se ne accorga.

`Feed_EmitsTicks_FromReceivedFrames`, `SeriesHealth_TracksPerSymbol_ASilentSymbolIsVisible`, `SeriesHealth_ForgetsUnsubscribedSymbols`, `Feed_Reconnects_AfterChannelDrop`, `Feed_SendsSubscribeFrames_WhenExchangeRequiresThem`, `Feed_DropsImplausibleTicks`, `Feed_SurvivesThrowingHandler`, `Feed_RecyclesLiveConnection_WhenSubscriptionsChange_Binance`, `Feed_RecyclesLiveConnection_WhenSubscriptionsChange_Bitget`, `UpdateSubscriptions_ReportsChangeOnlyWhenActuallyDifferent`, `UpdateSubscriptions_ToleratesTwoLanesOnTheSameSymbol`, `Feed_ServesBothTimeframes_WhenTwoLanesShareTheSymbol_Binance`, `Feed_ToleratesSpotAndFuturesLanes_OnTheSameSymbol_Bitget`, `UpdateSubscriptions_IgnoresOtherExchanges`, `Health_IsStale_WhenSilentBeyondThreshold`, `Feed_WithNoSubscriptions_NeverConnects`, `BackoffDelay_GrowsAndStaysWithinCap`

## `AiCommitteeTests.cs` — 16 test, 227 righe

> [AF5.4] Lo scheduling del digest: un orario, una volta al giorno, mai a raffica.

`Parse_ValidVote_IsCounted`, `Parse_MarkdownFences_AreTolerated`, `Parse_AnythingOffContract_IsAbstention`, `Majority_Wins`, `Tie_FallsBackToTheDefault`, `GarbageFromEveryProvider_IsIdenticalToCommitteeOff`, `BelowQuorum_EvenAUnanimousSingleVote_IsNotAMajority`, `Disabled_NeverCallsAnyone`, `ExhaustedBudget_SkipsTheWholeRound`, `MenuWithoutTheDefault_IsRejectedLoudly`, `BeforeTheHour_NotDue`, `AtTheHour_Due`, `AlreadySentToday_NotDueAgain`, `SentYesterday_DueToday`, `LateStart_StillSendsTheSameDay`, `Composer_AlwaysEndsWithTheDeadMansSwitchLine`

## `AuditBlazorUiTests.cs` — 16 test, 543 righe

> Audit FASE 4 — test bUnit dei componenti critici: 1. La Dashboard renderizza con dati fittizi (evidenze di promozione incluse) e la UI è in italiano. 2. Il pulsante "Promuovi a Live" in /trading è SEMPRE disabilitato, e "Avvia trading" in modalità Live resta disabilitato finché l'operatore non spunta la conferma esplicita. 3. Il form dati della Dashboard valida lato client (intervallo invertito, symbol mancante) SENZA invocare il servizio di ingestione. UN FALLIMENTO INTERMITTENTE CON CAUSA **NON IDENTIFICATA** (2026-07-28), annotato qui perché chi lo rivedrà non ricominci da zero. Trading_ConfirmPendingOrder_CallsEngine_WithCorrectOrderId è fallito **una volta** in una suite intera (1832/1833) e non si è più ripresentato in tre suite successive; non è riproducibile da solo, con la sua cl…

`Dashboard_RendersWithFakeData_ShowsPromotionHighlight_InItalian`, `Trading_SwitchingLane_AdoptsThatLaneMode_NotTheFormDefault`, `Trading_PromotionsTable_HasNoLiveButtonAtAll_ButOffersTestnet`, `Trading_StartInLiveMode_RequiresExplicitConfirmationCheckbox`, `Trading_ClickAvviaTrading_CallsEngineStart_ThroughMediator`, `Trading_ClickFermaTrading_CallsEngineStop_ThroughMediator`, `Trading_EmergencyStop_FirstClickOnlyAsksConfirmation_SecondClickCallsEngine`, `Trading_ConfirmPendingOrder_CallsEngine_WithCorrectOrderId`, `Trading_RejectPendingOrder_CallsEngine_WithCorrectOrderId`, `Dashboard_InvalidDateRange_ShowsItalianError_AndNeverCallsIngestion`, `Dashboard_EmptySymbol_ShowsItalianError_AndNeverCallsIngestion`, `Trading_SaveSafetyForm_PersistsEditedThreshold`, `Trading_SaveSafetyForm_InvalidValues_ShowError_AndDoNotPersist`, `Trading_PannelloRitardoUscite_CePureSenzaDati_ESpiegaIlVerdetto`, `Trading_SenzaOrfane_NessunBloccoRosso`, `Trading_EsitoDellaChiusuraOrfana_NonVive_DentroIlBloccoCheSparisce`

## `EngineConfigTests.cs` — 16 test, 298 righe

> Il canale con cui il guscio legge e riscrive la configurazione DEL MOTORE (2026-07-29). Perché esiste: il disegno originale faceva condividere ai due processi un solo appsettings.json su PVC. Verificato dal vivo che non regge col guscio fuori dal cluster — il file era rimasto a {} e ogni soglia mostrata in UI era quella del guscio, non quella applicata dal motore. Questi test difendono soprattutto il CONFINE: SetEngineConfig scrive su un processo che firma ordini veri, quindi ciò che conta non è che funzioni ma che non funzioni su tutto il resto .

`EngineNotifications_AreConfigurable_ButTheTokenNeverTravels`, `TopologySections_AreReadable_ButNeverWritable`, `SimilarlyNamedSections_AreNotConfusedWithForbiddenOnes`, `EveryWritableSection_IsAlsoReadable`, `Write_OnASectionOutsideTheAllowList_IsRefused`, `Read_ReturnsCodeDefaults_ForKeysAbsentFromTheFile`, `Read_ReflectsTheFile_WhenTheSectionExists`, `Read_SkipsForbiddenSections_InsteadOfFailingTheWholeScreen`, `Read_WithNoSectionsRequested_ReturnsEveryKnownOne`, `Write_PersistsTheSection_AndReturnsItReread`, `Write_AppliesTheSameValidationAsThePanels`, `Write_RejectsMalformedJson_WithAReadableMessage`, `Write_WarnsWhenAnotherProviderWillKeepWinningOverTheFile`, `Write_DoesNotWarn_WhenTheFileIsTheOnlySource`, `Write_MakesTheNewValueVisibleImmediately_WithoutRelyingOnAFileWatcher`, `Write_PreservesSiblingSections`

## `LaneEpisodeTests.cs` — 16 test, 267 righe

> [2026-08-06] Episodi di corsia. Il problema che risolvono, misurato sul database vero: la corsia 0 aveva 610 ordini di 7 strategie diverse su 3 simboli in un elenco unico, e Order.StrategyId è un GUID che non corrisponde a nulla in SavedStrategies — lo storico era orfano. I confini però c'erano già: 11 voci di StartEngine nel registro di audit, mai usate. Il contratto che questi test difendono: gli episodi separano correttamente gli esperimenti, e ciò che è dedotto non viene mai spacciato per dichiarato .

`SenzaAvvii_NessunEpisodio`, `OgniAvvioApreUnEpisodio_EIlPrecedenteSiChiude`, `GliOrdiniFinisconoNellEpisodioGiusto`, `OrdineSulConfine_VaAllEpisodioNuovo`, `OrdiniPrimaDelPrimoAvvio_NonEntranoInNessunEpisodio`, `PayloadNuovo_EDichiarato`, `PayloadVecchio_SimboloDedotto_StrategieMaiInventate`, `PayloadVecchioSenzaOrdini_SiDichiaraIgnoto`, `PayloadIllegibile_DegradaSenzaPerdereIlConfine`, `SimboliMisti_VinceIlPiuFrequente_EInModoStabile`, `EpisodioDichiarato_PortaGliIdDelleStrategieSalvate`, `EpisodioDedotto_NonHaIdSalvati`, `OrdiniAnterioriAlPrimoAvvio_SonoOrfani_ENonEntranoInNessunEpisodio`, `OrdineSullIstanteDelPrimoAvvio_NonEOrfano`, `SenzaEpisodi_NessunOrfano`, `TreViteSuTreSimboli_DiventanoTreEpisodiSeparati`

## `PostMortemTests.cs` — 16 test, 227 righe

> [G4] Post-mortem delle operazioni chiuse in perdita. Il contratto, in ordine di importanza: (1) dove la causa è ARITMETICA la stabilisce il codice e l'AI non viene nemmeno interpellata; (2) l'AI sceglie SOLO dentro il menù chiuso, e qualunque altra cosa vale come nessuna risposta ⇒ Inconcludente ; (3) il testo che raggiunge il comitato è un conteggio di cause, non un'opinione.

`Extract_PortaIFattiVeriEStimaIlLordo`, `Extract_MotivoDiUscitaAssente_LoDichiara`, `DeterministicCause_CostiCheMangianoIlLordo`, `DeterministicCause_LiquidazioneVincesuTutto`, `DeterministicCause_PerditaVera_LasciaIlDubbio`, `Menu_OgniVoceHaUnEtichetta`, `Menu_LeCauseCalcolabiliNonSonoOffrbileAllAi`, `BuildPrompt_ContieneIFattiEIlMenu`, `ParseVerdict_CausaDelMenu_Accettata`, `ParseVerdict_FuoriMenu_ValeComeNessunaRisposta`, `ParseVerdict_SopravviveAiFencesMarkdown`, `ParseVerdict_SenzaJson_Lancia`, `Summarize_NessunPostMortem_StringaVuota`, `Summarize_ContaLeCauseInOrdineDiFrequenza`, `Summarize_Deterministico_APariMerito`, `Opzioni_SpentePerDefault`

## `ProtectiveExitLagAnalyzerTests.cs` — 16 test, 699 righe

> [B3] Il gate B3 chiede il confronto tick-vs-candela, ma in assetto osservativo i tick vengono scartati e la serie source=tick non può esistere: il confronto che deve autorizzare l'accensione richiedeva l'accensione. chiude la domanda offline usando le candele fini come surrogato dei tick. Una misura del genere è pericolosa proprio perché è facile che dia il risultato che si spera: basta sbagliare di un passo il momento in cui un percorso "scopre" l'uscita e il feed sembra anticipare di un'intera barra senza aver fatto nulla. Da qui il primo test, che è il controllo della misura e non una sua applicazione: con risoluzione fine UGUALE a quella di corsia l'anticipo deve essere ESATTAMENTE zero. Se lo strumento non sa dire "nessun vantaggio" quando non ce n'è, nessuno dei numeri successivi va…

`Stessa_risoluzione_nessun_anticipo`, `Barre_fini_senza_informazione_nessun_anticipo`, `Anticipo_piantato_ritrovato_col_valore_esatto`, `Rottura_che_prosegue_il_ritardo_costa`, `Ottimismo_del_fill_a_candela_misurato`, `Short_simmetrico_al_long`, `Prezzi_precedenti_allingresso_non_fanno_uscire`, `Il_trailing_dei_due_percorsi_non_si_contamina`, `Su_rumore_puro_il_ritardo_non_costa_ne_rende`, `Risoluzione_fine_piu_grossa_della_corsia_e_rifiutata`, `Timeframe_sconosciuto_e_rifiutato_invece_di_ripiegare`, `Senza_stop_non_ce_uscita_protettiva_da_misurare`, `Serie_piatta_nessuna_uscita_e_lo_dichiara`, `Laggregato_dice_zero_mentre_i_due_lati_dicono_il_contrario`, `ConUnSoloTipo_ilSeparatoCoincideConLaggregato`, `LeUsciteDiscordiNonEntranoInNessunTipo`

## `TradingServiceCollectionExtensionsTests.cs` — 16 test, 391 righe

> LA GARANZIA DI SICUREZZA CENTRALE DELLA FASE 2b. Il vincolo "mai due esecuzioni simultanee sulla stessa corsia" non Ã¨ retto da un lock distribuito, ma dal fatto che monolite e servizio remoto non registrano MAI entrambi un motore attivo per la stessa lane. Qui lo si verifica per COSTRUZIONE (composizione DI, deterministica e istantanea) invece che a runtime con due processi vivi â€” un test del genere sarebbe lento, e soprattutto fallirebbe a intermittenza proprio nello scenario che deve escludere con certezza. Si RISOLVONO davvero le istanze invece di ispezionare i ServiceDescriptor: le registrazioni sono factory lambda, quindi il descriptor non espone il tipo concreto e un test su di essi passerebbe anche se la factory costruisse la classe sbagliata.

`ToggleOff_RegistersLocalEngineAndWorkers_ForEveryLane`, `ToggleOn_ReplacesEveryLaneEngineWithRemoteClient`, `ToggleOn_RegistersNoLocalTradingWorkers`, `EnsembleRebalanceStaysInTheMonolith_RegardlessOfToggle`, `TradingServiceHost_RegistersNoEnsembleRebalanceWorker`, `TradingServiceHost_IgnoresTheToggle_AndRunsTheEngineItself`, `RealtimeFeed_LivesOnlyWhereTheEngineIsLocal`, `EngineConfigStore_FollowsWhereTheEngineActuallyLives`, `CarryWorker_LivesOnlyWhereTheEngineIsLocal`, `RealtimeFeed_IsOneForTheFleet_NotOnePerLane`, `ToggleOff_StartsWorkersInTheHistoricalOrder`, `NonKeyedFallback_ResolvesLaneZero_InBothModes`, `ToggleOn_WithoutRemoteUrl_FailsFast`, `ToggleOn_WithoutGrpcSharedSecret_FailsFast`, `TradingServiceHost_RegistersNoGrpcClient`, `TradingServiceHost_WithoutRemoteUrl_DoesNotFailFast`

## `LaneInvariantWatchdogTests.cs` — 15 test, 601 righe

> Regressione della Fase 0-A3 (PRD Autonomia Operativa §3): il watchdog degli invarianti contabili deve accorgersi DA SOLO dello stato in cui la corsia 2 è rimasta per ore il 2026-07-18 (PnL -1,8M su capitale 10k) — quarantena persistita, trading fermato, posizioni LASCIATE aperte, audit scritto — e TradingEngine.StartAsync deve rifiutare il riavvio finché un umano non rimuove la quarantena (che un riavvio azzererebbe capitale/PnL, cancellando l'evidenza).

`Tick_RealCorsia2State_QuarantinesLane_StopsEngine_LeavesPositionsOpen`, `Tick_HealthyAndStoppedLanes_NoAction`, `Tick_SecondPass_DoesNotDuplicateQuarantineOrStop`, `Tick_Disabled_NoActionEvenOnCorruptedLane`, `Tick_PositionsOfOtherMode_NotCountedInExposure`, `Tick_Quarantine_EmitsCriticalNotification`, `StartAsync_QuarantinedLane_Refuses_UntilHumanClears`, `Tick_PosizioneSuCorsiaFuoriRange_AllertaSenzaChiudereNulla`, `Tick_PosizioneOrfana_AllertaUnaVoltaSola`, `Tick_PosizioniSulleCorsieConfigurate_NessunAllarmeDiOrfane`, `ProcessCandle_AggiornaIlBattito_ELoStatusLoEspone`, `Tick_CorsiaRunningColBattitoStantioEFermo_AllertaUnaVolta_SenzaQuarantena`, `Tick_BattitoVecchioMaCheAvanza_EIlReplayDiAvvio_NonUnDigiuno`, `Tick_BattitoFresco_NessunAllarme_ERiarmoDopoIlRecupero`, `Tick_CorsiaRunningCheNonHaMaiValutato_AllertaDopoDueSguardi`

## `LaneRegimeRouterTests.cs` — 15 test, 332 righe

> [Fase 4 — docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Il router di regime: classifica il regime corrente col K-means vero e lascia operare solo le strategie che vi hanno senso. Fino a qui il routing per regime esisteva soltanto dentro il backtest, e per di più con un surrogato (pendenza di una SMA), mentre il motore live il regime non lo consultava affatto. Ciò che questi test difendono non è la classificazione — quella è del detector, già testato — ma le tre proprietà che rendono il filtro sicuro da accendere su una corsia vera: fallisce verso il permesso , distingue "non so" da "qui non si opera" , e non tocca le posizioni già aperte .

`Observing_ClassifiesButNeverBlocks`, `DriveDecisions_TurnsTheSameRuleIntoABlock`, `AllowsOnlyTheStrategiesMappedToTheCurrentRegime`, `EmptyRuleMeansStandAside_NotEveryoneAllowed`, `UnmappedRegime_IsPermissiveByDefault`, `UnmappedRegime_CanBeMadeRestrictiveExplicitly`, `Disabled_IsInert`, `NoActiveModel_AllowsEverything`, `ModelOfAnotherSeries_IsNeverBorrowed`, `EachSeriesGetsItsOwnModel_SoLanesDoNotStealTheRouterFromEachOther`, `NotEnoughCandles_AllowsEverything`, `NoFeatures_AllowsEverything`, `UnlabelledBars_AllowEverything`, `AFailureInTheRouter_NeverBecomesAFailureOfTheLane`, `Cancellation_IsPropagated_NotSwallowedAsAFailure`

## `RiskProfileTests.cs` — 15 test, 259 righe

> [R3] Test dei profili di rischio della Modalità Semplice e della loro applicazione per corsia. Il test più importante è quello sull'INVARIANTE di sizing: un profilo che lo viola non produce una corsia "aggressiva", produce una corsia che non fa MAI trading, perché il SafetyChecker rifiuta ogni singolo ordine. È un guasto silenzioso e assurdo da diagnosticare dal vivo, mentre qui costa un assert.

`EveryProfile_RespectsTheSizingInvariant`, `EveryProfile_HasCoherentAndPositiveLimits`, `Profiles_AreOrderedFromProudentToDynamic`, `TurnoverCaps_StayWithinWhatR2MeasuredAsSustainable`, `EstimatedAnnualCost_GrowsWithTurnover_AndIsReadable`, `NoScalpingProfile_IsOffered`, `Find_UnknownOrEmpty_ReturnsNull_MeaningGlobalThresholds`, `Find_IsCaseInsensitive`, `Apply_ProfileOwnsRiskAppetite`, `Apply_GlobalKeepsVenueFactsAndSafetyNets`, `LaneMonitor_WithoutProfile_IsTransparent`, `LaneMonitor_WithProfile_OverlaysIt`, `LaneMonitor_ProfileCanBeCleared`, `LaneMonitors_AreIndependentOfEachOther`, `LaneMonitor_StillSeesGlobalChanges_ForFieldsTheProfileDoesNotOwn`

## `BacktestPageServiceTests.cs` — 14 test, 392 righe

> Test dell'orchestrazione estratta da Backtest.razor (P1-5, PRD-CONSOLIDAMENTO-ARCHITETTURA.md §3.3): prima di questa estrazione tutta la logica — validazione, run del backtest con analitiche derivate, suggerimento SL/TP, handoff dall'Optimization, preset validati e CRUD delle strategie salvate — viveva nel blocco @code del componente, senza test indipendenti da Blazor. Qui è esercitata direttamente su con le dipendenze reali (motore di backtest, StrategyFactory, analitiche di rischio, Postgres effimero) e un tracker no-op.

`ParameterDefinitionsFor_UnknownStrategy_FallsBackToFirstPrototype`, `ApplyConfig_RoundTrip_AppliesSerializedValues_WithParameterOverlay`, `ApplyConfig_InvalidStrategyAndTimeframe_KeepsCurrent_MalformedJson_Unchanged`, `ApplyHandoff_FullContext_AppliesAndReturnsMessage`, `ApplyHandoff_NoContext_NoMessage_ParametersAreDefaults`, `ApplyHandoff_InvalidStrategy_MalformedParameters_KeepsCurrentWithDefaults`, `OptimizationHandoffUrl_ContainsContext`, `RunAsync_BlankSymbolOrBadRange_ReturnsError`, `RunAsync_NoCandlesInRange_ReturnsFetchError`, `RunAsync_HappyPath_PopulatesResultReportAndAnalytics`, `RunMonteCarloAndPerformanceControl_WithoutRun_AreNoOps`, `SuggestBracket_InsufficientData_ReturnsError`, `SuggestBracket_HappyPath_ReturnsPositiveLevels`, `SaveStrategy_BlankName_Error_ThenRoundTripWithUserIsolation`

## `CreativeDiscoveryTests.cs` — 14 test, 324 righe

> Test puri del layer di scoperta creativa: SignalCatalog (normalizzazione causale), le tre meta-strategie (Composite/EventTrigger/RegimeConditional) e i generatori del composer (determinismo, diversità, plausibilità). Nessun DB, dati sintetici seedati.

`CausalPercentile_IsTruncationInvariant_AndBounded`, `SignalMatrix_IsCachedPerCandleListInstance`, `Composite_AndLogic_FiresOnlyWhenAllConditionsTrue`, `Composite_OrLogic_FiresWhenAnyConditionTrue`, `Composite_ContradictorySpec_Throws`, `Composite_IsDeterministic_AndTruncationInvariant`, `EventTrigger_PriceShockDown_EntersAndClosesAfterMaxHold`, `EventTrigger_InvalidParams_Throws`, `RegimeConditional_DelegatesByBucket_AndClosesOnSwitch`, `RegimeConditional_AllNone_Throws`, `CompositeGenerator_IsDeterministic_Diverse_AndPlausible`, `GeneratedCompositeSpecs_AllInitializeWithoutErrors`, `EventAndRegimeGenerators_AreDeterministicAndValid`, `ComposerWindows_CoverTheRangeWithoutOverlap`

## `MlTargetKindTests.cs` — 14 test, 228 righe

> [1.V roadmap macchina-ricerca] La volatilità come TARGET di predizione. Dopo 445.280 combinazioni direzionali con zero sopravvissuti, predire il rischio è la domanda con più probabilità di risposta (la volatilità è persistente). Questi test fissano: (a) la correttezza numerica dei tre target, (b) il default invariato, (c) la GUARDIA di semantica — un modello che predice volatilità non può essere salvato, perché tutto ciò che consuma un SavedMlModel interpreta la predizione come rendimento atteso e la confronterebbe con le soglie long/short (vol alta ≠ compra).

`ForwardReturn_MatchesTheHistoricalDefinition`, `ForwardAbsReturn_IsTheAbsoluteValue_LosingTheSignOnPurpose`, `ForwardRealizedVol_MatchesAHandComputedCase`, `ForwardRealizedVol_WithHorizonOne_IsRejected`, `TailBars_WithoutFullHorizon_YieldNull_NeverPartialValues`, `DatasetBuilder_WithVolTarget_LabelsAreTheRealizedVol`, `SaveModel_WithNonReturnTarget_IsNoLongerBlockedAtSave_GuardMovedToConsumers`, `SavedMlModel_IsDirectional_OnlyForForwardReturn_DefaultIncluded`, `MlModelLoader_LoadAsync_RefusesNonDirectionalModel_WithSemanticMessage`, `BacktestAsync_OnVolSession_IsRefused_PointingToVolEvaluation`, `Qlike_IsZeroForPerfectForecast_AndPositiveOtherwise`, `EwmaPerBarVol_OnConstantReturns_ConvergesToThatVol`, `PastRealizedVol_MatchesHandComputedWindow`, `Score_SkipsRowsWithZeroRealizedVol_AndMisalignedSeriesThrow`

## `RegimeChangeTriggerTests.cs` — 14 test, 232 righe

> Test del trigger contestuale (Fase 2, PRD Autonomia §5): decisione PURA del detector (cambio cluster sintetico, banda vol nei due versi), realized vol, e il worker — cooldown, wake del planner (mai lancio diretto), gate a monte di Campaign:Enabled, notifica.

`Evaluate_SameRegime_VolInBand_NoTrigger`, `Evaluate_ClusterChanged_Triggers`, `Evaluate_VolExpansionBeyondBand_Triggers`, `Evaluate_VolCompressionBelowBand_Triggers`, `Evaluate_MissingData_NeverTriggers`, `Evaluate_BothConditions_ReasonListsBoth`, `ComputeRealizedVolatility_ConstantPrices_IsZero`, `ComputeRealizedVolatility_TooFewPrices_ReturnsNull`, `ComputeRealizedVolatility_AlternatingReturns_MatchesStdDev`, `Tick_Triggered_WakesPlanner_AndNotifies`, `Tick_Cooldown_SuppressesSecondFire_UntilElapsed`, `Tick_NobodyWoken_DoesNotConsumeCooldown`, `Tick_CampaignGateOff_DetectorNeverCalled`, `Tick_NotTriggered_NoWake`

## `StrategyDecayMonitorTests.cs` — 14 test, 300 righe

> Test unitari (dati sintetici, nessun DB) per . Dal fix M5 lo Sharpe realizzato è su base PER-CANDELA (bucket del timeframe, vuoti = 0) come lo Sharpe atteso dell'holdout — prima era "a trade" con sqrt(trade/anno), un'unità di misura diversa che rendeva la soglia percentuale di alert priva di significato. Il vecchio numero sopravvive come informativo.

`FewerTradesThanWindow_NoAlert_ReportsInsufficientCount`, `NoExpectedSharpe_NoAlert_ReportsMetricsUnavailable`, `DecayedStream_RealizedFarBelowExpected_TriggersAlert`, `RealizedCloseToExpected_NoAlert`, `NonPositiveExpectedSharpe_SkipsRatioAlert_ButReportsDelta`, `AllTradesInSingleBucket_RealizedSharpeIsZero_NoDivisionByZero`, `UniformReturnsOnePerBucket_ZeroVariance_RealizedSharpeIsZero`, `RealizedProfitFactor_MatchesGrossProfitOverGrossLoss`, `RealizedTradeSharpe_MatchesIndependentlyComputedPerTradeAnnualization`, `RealizedSharpe_MatchesIndependentlyComputedPeriodAnnualization`, `BuildPeriodReturns_ExactBucketVector`, `BuildPeriodReturns_SpanBeyondMaxBuckets_CoarsensBucketAndScalesAnnualization`, `SameBasisSanity_OneTradePerCandle_MatchesHoldoutSharpeWithin25Percent`, `TradesForOtherStrategies_AreIgnored`

## `TripleBarrierLabelerTests.cs` — 14 test, 260 righe

> [C4] Verifica dell'etichettatura triple-barrier. Come per gli altri strumenti di misura della piattaforma, i test costruiscono serie in cui la risposta giusta è NOTA per costruzione — un percorso che tocca solo il profitto, uno che tocca solo lo stop, uno che non tocca niente, e il caso ambiguo in cui li tocca entrambi nella stessa barra.

`PriceReachingUpperBarrier_IsLabelledProfit`, `PriceReachingLowerBarrier_IsLabelledStop`, `PriceTouchingNeitherBarrier_IsLabelledVertical`, `WhenBothBarriersAreTouchedInTheSameBar_TheStopWins`, `LabelIgnoresTheEntryBarItself_OnlyTheFutureCounts`, `TailBarsWithoutEnoughFuture_AreLeftUnlabelled`, `ForAShort_ADroppingPriceIsProfit`, `ForAShort_ARisingPriceIsStop`, `TripleBarrier_DisagreesWithFixedHorizon_WhenThePathHitsTheStopFirst`, `NonOverlappingLabels_AllHaveFullUniqueness`, `FullyOverlappingLabels_ShareTheirWeight`, `OverlappingLabelsFromRealSeries_GetWeightsBelowOne`, `SuggestConfig_DerivesBarriersFromTheSeriesExcursions`, `Labelling_IsDeterministic`

## `AuditSafetyKellyExtremeTests.cs` — 13 test, 203 righe

> Audit FASE 1 — SafetyChecker sotto scenari estremi (capitale nullo/negativo, drawdown 100%, perdita catastrofica, clock skew, boundary esatti dei limiti) e criterio di Kelly su distribuzioni patologiche (wipeout totale, covarianza singolare). Principio della piattaforma: fail-CLOSED — nel dubbio l'ordine si rifiuta.

`ZeroOrNegativeCapital_HugeOrder_MustBeRejected_FailClosed`, `ExtremeDrawdown_100Percent_BlocksAndTriggersEmergencyStop`, `CatastrophicDailyLoss_95Percent_BlocksAndTriggersEmergencyStop`, `ClockSkew_LastOrderInTheFuture_FailsSafe_Rejected`, `ExtremeLeverage_125x_IsRejected_WithoutEmergencyStop`, `EmergencyStopActive_EvenPerfectOrder_IsRejected`, `DailyLoss_ExactlyAtLimit_IsRejected_AndTriggersEmergencyStop_FailClosed`, `DailyLoss_JustBelowLimit_IsStillAllowed`, `Drawdown_ExactlyAtLimit_IsRejected_GreaterOrEqualConvention`, `EmpiricalKelly_DatasetWithTotalWipeout_IsDrasticallyMoreConservative`, `BinaryKelly_EdgeCases_AlwaysInZeroOneRange`, `ContinuousKelly_DegenerateInputs_ReturnZero`, `MultiAssetKelly_DuplicatedAsset_SingularCovariance_NoThrow_FiniteNormalized`

## `DigestNarrativeTests.cs` — 13 test, 205 righe

> [G9] Narrativa di sintesi in cima al digest giornaliero. Il contratto: il digest è il dead-man's-switch del proprietario — se non arriva, la piattaforma è muta. Una funzione di comodo come questa non deve poterlo toccare in alcun modo. Da qui la proprietà che questi test difendono per prima: senza narrativa il messaggio è identico carattere per carattere a quello di prima della funzione.

`Compose_SenzaNarrativa_MessaggioIdenticoAllaVersioneSenzaParametro`, `Compose_ConNarrativa_LaMetteSopraSenzaToccareIDati`, `Compose_NarrativaSopraICorsie`, `DigestOptions_NarrativaSpentaPerDefault`, `BuildPrompt_ContieneLeStesseRigheDelMessaggio`, `BuildPrompt_DatiVuoti_DichiaraIlVuoto`, `Clean_TogliMarkdownEPortaSuUnaRiga`, `Clean_TagliaIModelliProlissi`, `Clean_VuotoRestaVuoto`, `Narrate_AiNonDisponibile_RestituisceNull`, `Narrate_RispostaVuota_RestituisceNullNonUnaRigaVuota`, `Narrate_RispostaBuona_TornaPulita`, `Narrate_UsaIlPathDichiarato`

## `ExchangeRateLimitAndClockTests.cs` — 13 test, 319 righe

> [R1] Test della disciplina verso le API REST degli exchange: ritiro sui rate-limit e allineamento dell'orologio per le richieste firmate. Entrambi nascono da guasti che colpiscono le CHIUSURE tanto quanto le aperture: un ordine di stop rifiutato perché l'IP è in ban da 429, o perché il timestamp è fuori dalla recvWindow, è una perdita reale — non un fastidio operativo.

`RateLimited_429_IsRetried_AndEventuallySucceeds`, `RateLimited_GivesUpAfterMaxRetries_ReturningLastResponse`, `Http418_IpBanned_IsAlsoTreatedAsRateLimit`, `ServerError_IsNotRetried`, `Success_PassesThroughUntouched`, `Throttle_SpacesOutBurstOfRequests`, `HungConnection_IsAbortedByPerAttemptTimeout_AsTaskCanceled`, `CallerCancellation_IsNotDisguisedAsTimeout`, `Clock_WithoutMeasuredOffset_MatchesLocalTime`, `Clock_AppliesMeasuredOffset`, `Clock_OffsetIsPerExchange`, `Clock_RejectsImplausibleOffset`, `Clock_RejectsImplausibleNegativeOffset`

## `FactorDriftAnalyzerTests.cs` — 13 test, 307 righe

> [D2] Verifica del monitor di deriva dei fattori. Il metodo è lo stesso usato altrove nella piattaforma: si costruiscono serie in cui la risposta giusta è NOTA per costruzione (un fattore che informa e poi smette, uno che si capovolge, uno che non ha mai informato) e si controlla che il verdetto la trovi — invece di verificare che il codice "giri".

`JudgeSeries_ReproducesTheVerdictOfAFreshAnalysis`, `JudgeSeries_TakesTheWindowSizeFromTheSeries_NotFromTheConfig`, `JudgeSeries_WithTooFewPoints_SaysInsufficientInsteadOfGuessing`, `FactorThatKeepsWorking_IsStable`, `FactorThatStopsWorking_IsFlaggedAsWeakening`, `FactorThatInverts_IsFlaggedAsSignFlip`, `FactorThatNeverWorked_IsNotAnAlert`, `TooFewWindows_ReportsInsufficientInsteadOfGuessing`, `Series_UsesNonOverlappingWindowsInChronologicalOrder`, `AnalyzeMany_PutsAlertsFirst`, `NoiseFloor_ScalesWithWindowSize`, `PureNoiseFactor_NeverProducesAnAlert_AcrossManySeeds`, `Analysis_IsDeterministic`

## `LaneSelectorTests.cs` — 13 test, 205 righe

> Il selettore di corsia. Prima era una &lt;select&gt; : la corsia corrente era un numero e basta, e per sapere cosa ci girasse bisognava sceglierla e guardare. Con tre corsie si poteva tenere a mente; con dodici, no. Ciò che questi test difendono non è l'estetica ma le due regole che rendono il componente utile quando le corsie sono tante: chi resta visibile lo decide l'utilità, non l'id (prima chi opera, poi chi è configurato, infine le vuote), e la corsia selezionata non finisce mai nascosta — altrimenti sceglierla dal menu la farebbe sparire dentro il menu stesso.

`ShowsEveryLane_WhenTheyFit`, `UnconfiguredLane_IsShownAsSuch_NotHidden`, `RunningLane_ShowsTheIndicator`, `SelectedLane_IsMarkedActive`, `ClickingALane_RaisesTheChange`, `ClickingTheAlreadySelectedLane_DoesNothing`, `WithManyLanes_TheExcessCollapsesUnderAMoreButton`, `TheMoreButton_ActuallyExpands_WithoutBootstrapJavaScript`, `ExpandedView_StaysInNumericOrder`, `RunningLanesWinTheVisibleSlots_EvenWithHighIds`, `TheSelectedLane_IsNeverHiddenBehindTheMenu`, `ConfiguredLanesComeBeforeEmptyOnes`, `VisibleLanes_StayInNumericOrder`

## `LlmSentimentScorerTests.cs` — 13 test, 188 righe

> Test di (Fase B): parsing difensivo, clamp, fallback sul lessico per OGNI esito non-Ok (il contratto "mai un'eccezione verso il chiamante"), batch con allineamento pretenzioso. Il guard è quello VERO (fake solo il client): il comportamento breaker/timeout testato è quello di produzione.

`ScoreAsync_ParsesPlainNumber`, `ScoreAsync_ToleratesSurroundingTextAndComma`, `ScoreAsync_ClampsOutOfRange`, `ScoreAsync_GarbageResponse_FallsBackToKeyword`, `ScoreAsync_UnconfiguredClient_FallsBack_WithoutCalling`, `ScoreAsync_ClientThrows_FallsBack_NeverThrows`, `ScoreBatchAsync_AlignedArray_UsesLlmForAll_InTwoCalls`, `ScoreBatchAsync_MisalignedArray_FallsBackForThatBatch`, `ScoreBatchAsync_ClientFails_FallsBack_SameLength`, `TryParseScore_ValidInputs`, `TryParseScore_InvalidInputs`, `TryParseScoreArray_AcceptsExactLength_AndClamps`, `TryParseScoreArray_RejectsMisaligned`

## `PipelinePageServiceTests.cs` — 13 test, 434 righe

> Test dell'orchestrazione estratta da Pipeline.razor (P1-5, PRD-CONSOLIDAMENTO- ARCHITETTURA.md §3.3): bozze dell'editor (nuova/da esistente col merge delle fasi nuove), catena di validazione del salvataggio, CRUD config, controllo run, dettaglio con confronto e decisione di ri-applica, export markdown — prima tutto nel @code del componente, senza test indipendenti da Blazor. Motore e applier sono fake che catturano le chiamate (il motore vero ha i propri test); catalogo fasi fake minimale; config/run/artifact su Postgres reale.

`BuildNewConfigDraft_HasSafeDefaults`, `BuildEditDraft_MergesNewPrototypesDisabled_AndOrdersByOrder`, `MoveStage_SwapsAndRenumbers_OutOfRangeIsNoOp`, `SaveConfig_ValidationChain`, `SaveConfig_NewRow_Persists_AndReloads`, `SaveConfig_Edit_ScheduleChange_ResetsNextRunAt`, `CloneAndDelete_RoundTrip`, `Reload_ParsesLastRecommendation_FromMostRecentCompletedRun`, `SelectRun_LoadsSummaries_PreviousComparison_AndDecisionArtifact`, `RefreshLive_SignalsJustFinished_OnlyOnRunningToNullTransition`, `StartAndResume_DelegateToEngine_PauseCancelUseLiveRunId`, `ApplyRecommendation_NullOrEmpty_IsSilentNoOp_WithLegs_Delegates`, `ExportHref_And_UniverseSummary`

## `Alpha158FactorTests.cs` — 12 test, 240 righe

> Test del catalogo Alpha158 (rif. docs/archive/ROADMAP-QLIB.md §1.1 ). Il cuore è l'invariante ANTI-LOOK-AHEAD verificato in un solo test parametrico su TUTTO il catalogo (~150 feature), non un test scritto a mano per feature. Più: coerenza del round-trip per nome (persistenza), dimensione del catalogo, e correttezza numerica di alcuni operatori rappresentativi.

`EntireCatalog_IsAntiLookAhead`, `EveryCatalogFactor_RoundTripsThroughFactoryByName`, `Catalog_HasExpectedSize_AndUniqueNames`, `CustomHorizon_RoundTrips_EvenOutsideDefaults`, `TryCreate_RejectsUnknownOrMalformedNames`, `Factory_Create_ThrowsForTrulyUnknownFactor`, `Kbar_KmidAndKlen_MatchDefinitionAndWarmupIsZeroLength`, `Rsv_And_Rank_And_Cntp_StayInUnitInterval`, `Rsqr_OnPerfectlyLinearSeries_IsOne`, `Roc_MatchesRefRatioDefinition`, `Cntp_OnMonotonicUpSeries_IsOne`, `Compute_IsDeterministic`

## `AlphaFactorTests.cs` — 12 test, 251 righe

> Test del modulo Alpha: invariante ANTI-LOOK-AHEAD (il valore a i non cambia troncando la serie dopo i), correttezza dell'Information Coefficient su dati sintetici a segno noto, e proprietà strutturali di quantili / forward returns.

`AllFactors_ReturnSeriesAlignedToInput`, `Factories_CreateAndPrototypes_AreConsistent`, `Momentum_OnMonotonicSeries_IsPositive_AndWarmupIsNull`, `MeanReversion_IsNegative_WhenPriceAboveMean`, `RsiFactor_StaysWithinMinusOneToOne`, `ForwardReturns_LastHorizonEntries_AreNull`, `ForwardReturns_AreComputedCorrectly`, `InformationCoefficient_IsStronglyPositive_ForPredictiveFactor`, `InformationCoefficient_IsNearZero_ForRandomFactor`, `Evaluate_ProducesQuantilesAndDecay`, `Spearman_PerfectMonotonic_IsOne`, `Spearman_PerfectInverse_IsMinusOne`

## `ExecutionTests.cs` — 12 test, 219 righe

> Test del layer di esecuzione (rif. docs/archive/ROADMAP-QLIB.md §1.2 ): i piani conservano ESATTAMENTE la quantità totale, VWAP segue il profilo di volume, e il simulatore mostra la tesi centrale — distribuire l'ordine (TWAP/VWAP) riduce l'implementation shortfall rispetto all'esecuzione immediata quando la size è significativa. Il default "Immediate" resta il comportamento odierno.

`Immediate_IsSingleFullOrder`, `EveryAlgorithm_PreservesTotalQuantityExactly`, `Twap_UsesRequestedNumberOfSlices`, `Vwap_ConcentratesOnHighVolumeCandles`, `Iceberg_SplitsIntoClipsOfConfiguredSize`, `Adaptive_PreservesTotalQuantityExactly`, `Adaptive_DegradesToVwapLike_WhenVolatilityIsZero`, `Adaptive_FrontLoadsMore_WithHigherVolatility`, `Adaptive_FallsBackToTwap_WhenVolumeIsZero`, `Adaptive_SingleCandle_ReturnsSingleSlice_NoThrow`, `Simulator_Twap_ReducesShortfall_VsImmediate_ForLargeBuy`, `Simulator_Sell_CostIsBelowArrival`

## `GarchModelTests.cs` — 12 test, 193 righe

> Test di : recupero (approssimato) dei parametri di un processo GARCH(1,1) simulato con parametri noti, vincoli di stazionarietà rispettati, "volatility clustering" (uno shock alza la varianza prevista), e mean-reversion della previsione verso la varianza di lungo periodo.

`Fit_OnSimulatedGarchProcess_RecoversPersistenceApproximately`, `Fit_Parameters_SatisfyStationarityConstraints`, `ConditionalVariances_AreAlignedWithInput_AndAllPositive`, `VolatilityClustering_ShockRaisesForecastVariance`, `ForecastVariance_LongHorizon_ConvergesToLongRunVariance`, `ForecastVariance_OneStep_MatchesGarchRecursion`, `Fit_TooFewObservations_Throws`, `ForecastVariance_InvalidHorizon_Throws`, `Fit_Gaussian_LeavesDegreesOfFreedomNull`, `TailQuantile_Gaussian_MatchesNormalQuantile`, `Fit_StudentT_EstimatesFiniteFatTailDegreesOfFreedom`, `TailQuantile_StudentT_IsWiderThanGaussian_OnFatTailedData`

## `MlLabServiceTests.cs` — 12 test, 495 righe

> Test dell'orchestrazione estratta da MlLab.razor (P1-5, PRD-CONSOLIDAMENTO-ARCHITETTURA.md §3.3): prima di questa estrazione tutta la logica — validazione, addestramento/backtest, CRUD dei modelli salvati e (de)serializzazione validata dei preset — viveva nel blocco @code del componente, senza test indipendenti da Blazor. Qui è esercitata direttamente su con le dipendenze reali (factory alpha, dataset builder, backtest engine, Postgres effimero) e un tracker no-op, incluso il round-trip completo train→backtest→save→load.

`ApplyConfig_RoundTrip_AppliesSerializedValues`, `ApplyConfig_MalformedJson_ReturnsCurrentUnchanged`, `ApplyConfig_DropsInvalidCatalogValues_KeepsCurrentForScalars_AppliesFreeFields`, `LoadInitialDataAsync_LoadsSymbolsAndOnlyOwnFactorsAndModels`, `TrainAsync_InsufficientCandles_ReturnsError_NoModel`, `TrainAsync_NoFactorsSelected_ReturnsError`, `TrainThenBacktestThenSaveThenLoad_HappyPath`, `DeleteSavedModelAsync_IgnoresOtherUsersModel_ThenDeletesOwn`, `ComputeShap_WithAnActiveRegimeModel_UsesTheKMeansLens`, `ComputeShap_WithNoActiveRegimeModel_FallsBackToVolatility`, `ComputeShap_WhenTheRegimeDetectorThrows_StillProducesTheAnalysis`, `ComputeShap_WithoutRegimeDependencies_FallsBackWithoutThrowing`

## `PromotionEvaluatorTests.cs` — 12 test, 127 righe

> Verifica la logica pura di promozione/retrocessione delle corsie. Il criterio di SICUREZZA più importante: nessuna metrica, per quanto eccellente, produce mai una promozione a Live — è verificato esplicitamente. Copre anche i singoli criteri di promozione, il blocco assoluto sul drawdown, e la retrocessione Testnet→Paper quando l'edge svanisce.

`ExcellentPaperLane_PromotesToTestnet`, `LowSharpe_DoesNotPromote`, `FewTrades_DoesNotPromote`, `HighDrawdown_DoesNotPromote`, `TooFewWeeks_DoesNotPromote`, `LowWinRate_DoesNotPromote`, `HardDrawdownBlock_NeverPromotes_EvenIfOtherwiseGreat`, `AutoPromoteDisabled_MarksReadyButDoesNotPromote`, `TestnetLane_WithGodTierMetrics_IsNeverPromotedToLive`, `LiveLane_IsNeverTouched`, `TestnetLane_EdgeGone_DemotesToPaper`, `SameMetrics_SameDecision_Deterministic`

## `RealtimeStreamMapperTests.cs` — 12 test, 211 righe

> [R1] Test dei parser degli stream WebSocket. Nessuna rete: si passano al mapper esattamente i payload che gli exchange pubblicano. Il requisito trasversale più importante è la TOLLERANZA: un frame inatteso, malformato o di un canale che non usiamo deve produrre "niente di utile", MAI un'eccezione — un parser che lancia farebbe cadere la connessione (e quindi silenziare gli stop) per un messaggio irrilevante.

`Binance_BookTicker_ParsedAsTick`, `Binance_ClosedKline_ParsedAsBar`, `Binance_UnclosedKline_IsIgnored`, `Binance_UnknownSymbol_IsIgnored`, `Binance_Endpoint_ContainsBothChannelsPerSymbol`, `Binance_Endpoint_SeparatesSpotFromFutures`, `Binance_FuturesEndpoint_UsesFuturesDomain`, `Bitget_Ticker_ParsedAsTick`, `Bitget_ControlFramesAndGarbage_NeverThrow`, `Bitget_SubscribeFrame_UsesPublicProductType`, `Bitget_RequiresApplicationHeartbeat_BinanceDoesNot`, `PriceTick_Plausibility_RejectsGarbageQuotes`

## `SafetyCheckerTests.cs` — 12 test, 165 righe

> Verifica che OGNI safety check rifiuti correttamente l'ordine pericoloso e che un ordine valido passi. Logica pura (SafetyChecker.Evaluate), deterministica.

`ValidOrder_IsAllowed`, `PositionTooLarge_IsRejected`, `TotalExposureExceeded_IsRejected`, `DailyLossExceeded_IsRejected_AndTriggersEmergencyStop`, `DrawdownExceeded_IsRejected_AndTriggersEmergencyStop`, `TooManyOpenPositions_IsRejected`, `OrdersTooClose_IsRejected`, `LiveOrderWithoutConfirmation_IsRejected`, `LiveOrderWithConfirmation_PassesConfirmationRule`, `WhenEmergencyStopped_AnyOrder_IsRejected`, `InvalidQuantityOrPrice_IsRejected`, `MultipleViolations_AllCollected`

## `ChandelierAndGridTests.cs` — 11 test, 288 righe

> [Fase 5b] La strategia a gradini fissi. Il test più importante non è sul profitto — è che la strategia faccia quello che il suo nome dichiara : cicli finiti che raccolgono un gradino, non un grid multi-ordine (inesprimibile in un motore a posizione singola).

`AtrTrailing_ClosesThePosition_LikeThePercentOne`, `AtrTrailing_ReplacesPercentTrailing_InsteadOfStacking`, `AtrTrailing_IsInert_WhenNotConfigured`, `AtrTrailing_WiderMultiple_ExitsNoEarlier`, `EntersBelowAnchor_AndHarvestsOneRung`, `DoesNotEnter_WithinTheRung`, `MoreRungs_RequireADeeperMove`, `AnchorIsCausal_NeverIncludesTheCurrentBar`, `ShortSide_IsSymmetric`, `IsRegisteredInTheCatalog`, `RejectsInvalidParameters`

## `CointegrationTests.cs` — 11 test, 186 righe

> Test di e : una coppia costruita per essere cointegrata deve superare il test con l'elasticità recuperata vicino al vero beta; due random walk indipendenti (nessuna relazione di lungo periodo) non devono risultare cointegrate. Le serie sono costruite nella specificazione che il test usa davvero, cioè sui LOG: X è un random walk GEOMETRICO (log X random walk, prezzi sempre positivi come un OHLCV vero) e Y = e^α · X^β · e^ε con ε stazionario, cioè log Y = α + β·log X + ε. Costruirle in livello (Y = α + βX) misurerebbe una relazione diversa da quella stimata: per X grande log(α + βX) ≈ log β + log X, quindi l'elasticità tenderebbe a 1 qualunque sia il β di partenza.

`CointegratedPair_IsDetected_WithHedgeRatioClosToTrue`, `MacKinnonCriticalValue_IsStricterThanPlainAdf_AndReportsLags`, `IndependentRandomWalks_AreNotCointegrated`, `Spread_HasSameLengthAsInput`, `MismatchedLengths_Throws`, `TooFewObservations_Throws`, `HugePriceScaleGap_DoesNotByItselfMakeThePairImplausible`, `StationarySpreadButElasticityOutOfBand_IsNotTradeable`, `NonPositivePrice_Throws`, `RollingZScore_NullDuringWarmup_ThenPopulated`, `RollingZScore_IsCausal_TruncationDoesNotChangePastValues`

## `DelegatingSentimentScorerTests.cs` — 11 test, 132 righe

> Test di e : instradamento hot-reload sullo scorer configurato (default = lessico, il comportamento storico) e determinismo del vettorizzatore (la premessa di parità del pilota ONNX).

`Default_RoutesToKeyword`, `LlmProvider_RoutesToLlm_CaseInsensitive`, `OnnxProvider_WithoutModel_FallsBackToKeyword`, `UnknownProvider_FallsBackToKeyword`, `HotSwap_TakesEffectOnNextCall`, `Vectorizer_IsDeterministic`, `Vectorizer_EmptyText_IsZeroVector`, `Vectorizer_NonEmpty_IsL2Normalized`, `Vectorizer_DifferentTexts_DifferentVectors`, `Tokenize_LowercasesAndDropsSingleChars`, `Fnv1a_IsStable`

## `DynamicLanesTests.cs` — 11 test, 235 righe

> L'elenco che alimenta il selettore di corsia. Non cambia il conteggio: si misura contro quello corrente, così può convivere con qualunque configurazione senza diventare capriccioso.

`DefaultIsUnchanged_SoNothingMovesForWhoNeverConfiguresIt`, `Configure_AcceptsTheAllowedRange`, `Configure_RefusesNonsense`, `Configure_WithTheSameValue_IsHarmlessEvenAfterUse`, `Configure_WithADifferentValueAfterUse_FailsLoudly`, `AddTradingLanes_RegistersExactlyTheConfiguredNumberOfEngines`, `AddTradingLanes_RefusesAnImpossibleLaneCount`, `FleetGrowth_DoesNotWidenTheAutoApplyFootprint`, `FleetSmallerThanTheFootprint_ShrinksIt`, `ListsEveryLane_IncludingTheNeverConfiguredOnes`, `SurvivesAnUnreadableConfiguration`

## `EnsembleComparatorTests.cs` — 11 test, 129 righe

> Verifica il confronto oggettivo "nuovo ensemble vs corrente" con hysteresis: sostituzione solo per miglioramenti reali (Sharpe sopra soglia o RF95 nettamente migliore a Sharpe non inferiore), niente cambi per rumore, e il floor strutturale (minimo gambe / simboli distinti).

`BetterCandidate_AboveHysteresis_Replaces`, `WorseCandidate_Keeps`, `MarginalImprovement_BelowHysteresis_Keeps`, `NoCurrentEnsemble_AppliesFirst`, `CandidateBelowMinLegs_Rejected`, `CandidateBelowMinDistinctSymbols_Rejected`, `SaferRiskFactor_AtEqualSharpe_Replaces`, `SharpeFromNonPositiveBase_AnyPositive_Replaces`, `LargeImprovement_TinySample_NotSignificant_Keeps`, `SameImprovement_LargeSample_Significant_Replaces`, `UnknownSample_FallsBackToHysteresisOnly`

## `EnsemblePageServiceTests.cs` — 11 test, 381 righe

> Test dell'orchestrazione estratta da Ensemble.razor (P1-5, PRD-CONSOLIDAMENTO- ARCHITETTURA.md §3.3): caricamento per corsia (keyed DI), composizione delle gambe (predefinita/salvata/ML/Champion), ciclo di vita save/start/stop/rebalance, serie di performance, monitor drift e piani di esecuzione — prima tutto nel @code del componente, senza test indipendenti da Blazor. I manager di ensemble sono fake keyed per corsia che catturano le chiamate; cataloghi/candele/job vivono su Postgres effimero reale.

`LoadConfigAndChampion_UsesKeyedManagerOfLane_AndQueriesChampionForConfigSymbol`, `RefreshAsync_BuildsPerfSeries_TotalPlusPerStrategy`, `LoadSavedCatalogs_OrdersOptimizedFirst`, `AddPredefined_AddsWithDefaultParameters`, `AddFromSaved_CopiesParams_ExpectedSharpeOnlyIfOptimized`, `AddFromMlModel_PinsModelIdAndThresholds`, `AddChampion_AddsSentinel_OncePerLane_NoOpWithoutChampion`, `RemoveStrategy_RemovesById`, `SaveStartStopRebalance_DriveKeyedManagerAndFlags`, `EvaluateDrift_ModelMissing_InsufficientCandles_HappyPath`, `LoadExecutionJobs_FiltersByLane_Take20Desc`

## `IntradayStrategiesTests.cs` — 11 test, 230 righe

> Test puri (senza DB) delle strategie intraday nuove — Supertrend, Stochastic, VwapReversion — e dell'indicatore ATR che le abilita. Su serie sintetiche costruite per innescare un comportamento noto, così le asserzioni sono deterministiche e verificabili.

`Atr_KnownVector_MatchesHandComputation`, `Atr_MismatchedLengths_Throws`, `Stochastic_CloseAtBottomOfRange_IsOversold_Long`, `Stochastic_IsDeterministic`, `Stochastic_InvalidThresholds_Throws`, `VwapReversion_PriceBelowSessionVwap_Long_AboveShort`, `VwapReversion_ResetsEachUtcDay`, `VwapReversion_AllowShortFalse_NoShort`, `Supertrend_SustainedUptrend_EmitsLong_Deterministic`, `Supertrend_AllowShortFalse_DownSwitchIsClose_NotShort`, `Supertrend_InvalidParams_Throws`

## `LlmCallGuardTests.cs` — 11 test, 254 righe

> Test del chokepoint delle chiamate Claude: classificazione degli errori del SDK (il billing è un 400 riconoscibile solo dal testo), circuit breaker con notifiche one-shot, half-open probe col cooldown, ripristino automatico. Tutto in-memory, nessuna chiamata reale e nessun DB.

`Classify_BillingBadRequest_IsRetryableWithCause`, `Classify_PlainBadRequest_IsPermanent`, `Classify_TableOfTypedExceptions`, `Breaker_OpensAfterThreshold_WithSingleWarning_ThenSkips`, `Breaker_HalfOpenProbe_AfterCooldown_FailureStaysOpenSilently`, `Breaker_ForceProbe_BypassesCooldown_AndSuccessRecoversWithSingleInfo`, `PermanentErrors_DoNotTripBreaker`, `PermanentErrorDuringProbe_ClosesBreaker_ApiIsReachableAgain`, `NotConfigured_SkipsWithoutCalling_AndWithoutMovingBreaker`, `InternalTimeout_IsRetryableWithTimeoutCause`, `ExternalCancellation_Rethrows_WithoutCountingAsFailure`

## `NullTwinJudgeTests.cs` — 11 test, 274 righe

`SottoIlMinimoDiGemelliValidi_NessunVerdetto`, `QuantiliConConvenzioneDeiTool`, `PassaSoloChiSuperaStrettamenteLaSoglia`, `PercentileEPValueSonoComplementari`, `LaPolicyDiDefaultEQuellaDichiarata`, `BocciaIlFinalistaDentroIlNullo_EPromuoveChiLoBatte`, `FailSafe_NonGiudicabileLasciaPassareAVoceAlta`, `RispettaIlTettoDeiCandidati_IMiglioriPerSharpeHoldout`, `HoldoutTroppoCorto_NonChiamaIlGiudiceELasciaPassare`, `IgnoraICandidatiGiaBocciatiDallHoldout`, `IlCatalogoEsponeLoStage_PrimaDellaRobustnessProbe`

## `OrderFlowImbalanceTests.cs` — 11 test, 160 righe

> [D3] L'OFI vero (Cont-Kukanov-Stoikov) verificato caso per caso contro il valore calcolato A MANO dalla formula pubblicata, che è l'unico riferimento indipendente possibile per una definizione: non esiste una seconda implementazione da confrontare, esistono i sei casi elementari (bid fermo, migliorato, ritirato; e i tre simmetrici sull'ask) e il loro segno. Perché tanta cura su quattro righe di aritmetica: un OFI col segno sbagliato produce un IC perfettamente plausibile — semplicemente col segno opposto a quello vero — e nessun controllo statistico a valle lo smaschererebbe.

`PricesUnchanged_TheOfiIsTheNetChangeInSizes`, `BidImproves_TheWholeNewBidSizeCountsAsBuyingPressure`, `BidIsPulled_TheOldSizeCountsNegative`, `AskImproves_ItIsSellingPressureWithTheOppositeSign`, `AskIsPulled_TheOldAskSizeCountsPositive`, `TheFormulaIsAntisymmetric_MirroringBidAndAskFlipsTheSign`, `OverASequence_TheOfiIsTheSumOfTheEvents`, `ASingleQuote_HasNoEventsSoTheOfiIsZero`, `DepthBandOfi_IsSignedTowardsTheSideThatGrew_AndNormalized`, `DepthBandOfi_WithoutTheBand_IsNullInsteadOfZero`, `DepthBandOfi_OnAnEmptyBook_IsNull`

## `PipelineSupervisorTests.cs` — 11 test, 394 righe

> Verifica il layer AI di supervisione (SOLO advisory): l'LLM è sostituito da un fake, così nessun test tocca la rete. Copre parsing, persistenza come PipelineArtifact, idempotenza per-run, il percorso d'errore PERMANENTE (che persiste un advisory di errore) e quello TRANSITORIO (che NON persiste nulla: il run resta pendente e viene ritentato quando il problema — es. credito API — rientra), più la pulizia manuale del backlog di advisory in errore.

`SuperviseRunAsync_ParsesJson_AndPersistsAdvisoryArtifact`, `SuperviseRunAsync_IsIdempotentPerRun`, `SuperviseRunAsync_OnLlmFailure_PersistsErrorAdvisory`, `ParseAdvisory_ToleratesSurroundingText_AndNormalizesConfidence`, `SuperviseRunAsync_OnRetryableFailure_PersistsNothing_SoTheRunIsRetried`, `SuperviseRunAsync_WithBreakerOpen_SkipsWithoutCallingTheLlm`, `SuperviseRunAsync_ForceProbe_RecoversAndWritesRealAdvisory`, `DeleteErrorAdvisories_RemovesOnlyErrors_InsideTheWindow`, `UserPrompt_IncludesOperationalContext_AndBoundsRecommendationJson`, `UserPrompt_IncludesMarketMoodSection_WhenCacheIsPopulated`, `SuperviseRunAsync_NotifiesDecisions_OnlyWhenOptedIn`

## `PortfolioOptimizerTests.cs` — 11 test, 197 righe

> Test degli allocatori di portafoglio (cap. 5/13): Mean-Variance (Max Sharpe / Min Variance), Risk Parity naive (inverse-volatility) e HRP. Casi noti (un asset molto più volatile di un altro, uno con rendimento atteso molto più alto) verificano che il segno dell'allocazione sia quello atteso; le proprietà strutturali (somma=1, vincoli Min/Max) sono verificate ovunque.

`MinVariance_FavorsLowVolatilityAsset`, `MaxSharpe_FavorsHigherExpectedReturnAsset`, `MeanVariance_RespectsMaxWeightBound`, `MeanVariance_TooFewSymbols_Throws`, `MeanVariance_MismatchedLengths_Throws`, `RiskParity_WeightsAreInverselyProportionalToVolatility`, `RiskParity_EqualVolatility_GivesEqualWeights`, `RiskParity_TooFewSymbols_Throws`, `Hrp_TwoAssets_FavorsLowVolatility_LikeInverseVariance`, `Hrp_MultipleAssets_AllWeightsPositiveAndSumToOne`, `Hrp_TooFewSymbols_Throws`

## `ProtectionsPageRenderTests.cs` — 11 test, 295 righe

> Rendering di /admin/protections (audit 2026-07-29). La pagina nasce da un buco preciso: quattro protezioni che decidono se un'operazione può aprirsi o chiudersi — feed real-time, esposizione correlata, router di regime, watchdog degli invarianti — esistevano nel codice, giravano in produzione, e si configuravano SOLO editando appsettings.json a mano. Questi test proteggono le due proprietà che rendono la pagina utilizzabile senza fare danni: che i controlli ci siano davvero, e che i due interruttori «decide» restino subordinati al loro interruttore «osserva».

`Protections_ShowsEveryGuard_WithItsOwnSwitch`, `ObserveBeforeDecide_TheDecideSwitchesAreDisabledWhileTheFeatureIsOff`, `ObserveBeforeDecide_TheDecideSwitchesOpenUpOnceTheFeatureIsOn`, `RegimeRules_EmptyStrategyList_IsShownAsADecision_NotAsAnEmptyRow`, `InvalidThreshold_IsRefusedBeforeTouchingTheConfigFile`, `RemoteTrading_ThePageSaysWhereItIsWriting`, `LocalTrading_NoBannerAtAll`, `UnreachableEngine_ShowsDefaultsAndSaysSo_InsteadOfPassingThemOffAsTruth`, `SaveGoesToTheEngineStore_NotToTheLocalFile`, `WhenTheEngineWarnsThatAnEnvOverridesTheFile_ThePanelRepeatsIt`, `Protections_IsReachableFromTheSidebar_ForAdminsOnly`

## `RejectionExplainIntegrationTests.cs` — 11 test, 341 righe

> [G6] LIVELLO 3 dello standard di verifica: il giro completo su Postgres VERO — artifact dei verdetti letto, digest costruito, narrazione persistita e riletta. Quello che i test di unità non possono dire: che il Kind nuovo non disturbi gli altri artifact del run, che l'idempotenza regga su una tabella vera, e che il digest si formi anche quando l'AI non ha mai risposto — che è la condizione normale con la prosa spenta.

`Digest_SiFormaDalDbSenzaMaiChiamareLAi`, `Digest_RunSenzaArtifactDeiVerdetti_RestituisceVuotoSenzaEsplodere`, `Digest_ArtifactCorrotto_DichiaraIlVuotoNonInventa`, `Explain_ScrivePersisteERilegge`, `Explain_Idempotente_NonRipagaLaStessaProsa`, `Explain_Force_SostituisceInveceDiAccumulare`, `Explain_RunSenzaBocciati_NonChiamaLAiNeScrive`, `Explain_NonTocaGliAltriArtifactDelRun`, `Explain_NarratoreCheNonProduce_NonScriveArtifact`, `GetRecent_ElencaSoloRunConBocciatiPiuRecentiPrima`, `GetRecent_PortaLaNarrazioneQuandoCE`

## `SupportResistanceTests.cs` — 11 test, 221 righe

> Test di pivot, livelli S/R, trend a swing, ritracciamenti e pattern grafici (McAllen cap. 7-10, 15).

`FindPivots_DetectsLocalExtremes`, `Levels_ClusterTouches_AndCountThem`, `Trend_HigherHighsAndLows_IsUptrend`, `Retracement_MeasuresLastSwing`, `Breakout_AboveResistance_VolumeDecidesConfirmation`, `ChartPattern_DoubleTop_WithConfirmation`, `ChartPattern_HeadAndShoulders_Detected`, `ChartPattern_DoubleBottom_IsBullish`, `GapClassification_TrendContextDecidesType`, `GapClassification_FilledDetection`, `VolumeAnalyzer_DistributionWarning`

## `TradingGrpcRoundTripTests.cs` — 11 test, 359 righe

> Prova che il servizio di trading serve davvero via gRPC su HTTP/2 (host reale ProcioneMGR.Trading, non una chiamata C# diretta) e che i comandi attraversano la (de)serializzazione protobuf conservando la loro semantica — in particolare il TRI-STATO di SetStopLossTakeProfit, dove collassare "assente" e "zero" significherebbe disarmare uno stop loss per sbaglio. Il motore vero è sostituito da un fake che REGISTRA le chiamate: qui si verifica il filo (wire, mapping, codici di stato), non la logica di esecuzione — quella è già coperta dai test del TradingEngine e non cambia in Fase 2b, dato che il motore è riusato verbatim. Nessun DB.

`GetLaneStatus_OverRealGrpc_PreservesEveryFieldExactly`, `GetLaneStatus_UnknownLane_IsNotFound`, `ConfirmOrder_OverRealGrpc_ReachesTheEngineWithTheOperator`, `ConfirmOrder_EmptyUserId_ArrivesAsNull`, `SetStopLossTakeProfit_PreservesTriState_AcrossTheWire`, `SetStopLossTakeProfit_PassesRealValues`, `StartLane_MapsModeExplicitly_NotByOrdinal`, `StartLane_UnspecifiedMode_IsInvalidArgument_NotARawException`, `StartLane_DomainRefusal_BecomesFailedPrecondition`, `MissingSharedSecretHeader_IsRejected_Unauthenticated`, `ServerWithoutConfiguredSecret_RejectsEveryCall_FailClosed`

## `AppConfigWriterTests.cs` — 10 test, 262 righe

> è il writer generalizzato dietro i pannelli /trading e /admin/autonomy: un bug qui corrompe appsettings.json per TUTTE le sezioni. I contratti chiave: scrive l'intera sezione (nessuna chiave persa per costruzione), non tocca le sezioni sorelle, crea i path mancanti, preserva le chiavi di documentazione "_comment*".

`Roundtrip_WritesAllProperties_AndSiblingSectionsSurvive`, `NestedPath_CreatesMissingNodes`, `NestedPath_DoesNotClobberSiblingSubsections`, `CommentKeys_ArePreserved`, `ParentSection_KeepsNestedSubsectionsThePocoDoesNotModel`, `NestedObject_ThePocoDOESModel_IsOverwritten_NotPreserved`, `Enums_AreWrittenByName_NotByOrdinal`, `InvalidJson_ThrowsWithoutDestroyingFile`, `SaveValue_TouchesOnlyTheTargetKey_SiblingScalarsAndObjectsSurvive`, `SaveValue_CreatesMissingParents_AndWritesStrings`

## `AuditAlpha158EdgeCaseTests.cs` — 10 test, 185 righe

> Audit FASE 1 — casi limite del catalogo Alpha158 NON coperti dai test funzionali esistenti: input degeneri (prezzo costante, volume zero, doji perfetti, candele-glitch a prezzo 0, input vuoto/singolo) e valori estremi. Contratto atteso per TUTTO il catalogo: mai un'eccezione, serie sempre allineate all'input, null dove il valore non è calcolabile. In decimal non esistono NaN/Inf: ogni valore presente è finito per costruzione, quindi "gestione NaN/Inf corretta" = nessun OverflowException e null nei casi degeneri.

`EntireCatalog_ConstantPriceAndVolume_NoThrow_SeriesAligned`, `EntireCatalog_ZeroVolumeEverywhere_NoThrow`, `EntireCatalog_ZeroPriceGlitches_NoThrow`, `EntireCatalog_PerfectDojis_HighEqualsLow_NoThrow`, `EntireCatalog_EmptyAndSingleCandle_NoThrowAndAligned`, `EntireCatalog_FewerCandlesThanHorizon_AllNull_NoThrow`, `EntireCatalog_HugePrices_1e12_NoOverflow`, `EntireCatalog_TinyPrices_1e_8_NoOverflow`, `HorizonFactors_NeverProduceValuesBeforeWindowCompletes`, `BoundedOperators_StayInRange_EvenOnDegenerateInput`

## `BlockBootstrapPermutationTests.cs` — 10 test, 196 righe

> [T1.5 roadmap macchina-ricerca] Block bootstrap + permutation test: la randomizzazione GIUSTA (lungo il tempo, a blocchi) dopo la lezione dei 400 panieri correlati che produssero t = 141. I test di calibrazione sono il cuore: un test statistico che non è calibrato — che non dà p alti sul rumore e p bassi su un edge piantato — è un generatore di certezze finte, peggio di niente.

`PlantedDrift_GetsALowPValue`, `PureNoise_GetsAnUnremarkablePValue`, `PValue_IsCalibratedOnAverage_AcrossManyNoiseSeries`, `SameSeed_SameAnswer`, `DegenerateSeries_YieldPOne_NotACrash`, `DefaultMode_IsIidShuffle_HistoricalBehaviourUnchanged`, `BlockMode_OnStreakyPnls_SeesDeeperDrawdownsThanIid`, `BlockMode_IsDeterministicWithSeed_AndDrawsFromTheSource`, `Gate_PopulatesPermutationPValue_ButDoesNotBlockByDefault`, `Gate_WithThreshold_KillsNoise_KeepsPlantedEdge`

## `CarryEngineTests.cs` — 10 test, 171 righe

> [E3 roadmap profitto-intraday] Orchestrazione live del carry. Questi test fissano: (a) la regola di decisione è la STESSA del backtest (CarryDecider — un solo punto di verità); (b) il motore apre due gambe quando il funding è alto e chiude quando scende, tramite l'executor; (c) la finestra non piena → Hold; (d) il failsafe strutturale: CarryMode non ha il valore Live, quindi operare con denaro reale è IRRAPPRESENTABILE.

`HighFunding_OpensBothLegs_AtConfiguredNotional`, `FundingDrops_WhenInPosition_Closes`, `WindowNotFull_Holds_NoExecution`, `OpenFailure_LeavesFlat_DoesNotMarkInPosition`, `AlreadyInPosition_HighFunding_HoldsWithoutReopening`, `CarryMode_HasNoLiveValue_LiveIsUnrepresentable`, `PaperExecutor_IsAlwaysPaperMode_AndSucceedsWithoutExchange`, `ResolveMode_Paper_IsAcceptedSilently`, `ResolveMode_Testnet_DegradesToPaper_AndSaysWhy`, `ResolveMode_AnythingElse_FallsBackToPaper`

## `EngineConfigGrpcTests.cs` — 10 test, 272 righe

> Livello 3 (integrazione) per il canale di configurazione del motore: gli endpoint gRPC serviti dall'host REALE ProcioneMGR.Trading , non chiamate C# dirette a EngineConfigService . Nasce da una lacuna trovata rileggendo docs/STANDARD-VERIFICA.md dopo aver dichiarato il lavoro finito: EngineConfigTests copre il servizio in-process, e la verifica dal vivo copre il percorso completo dal browser, ma il pezzo in mezzo — l'adattatore gRPC, con la sua traduzione dei rifiuti in codici di stato — non aveva alcun test. È esattamente la regola 1 di quel documento: «il verde a livello di classe non è integrazione». Ciò che conta qui NON è che la scrittura funzioni (lo dice già il livello 1) ma che i RIFIUTI arrivino sul filo distinguibili l'uno dall'altro: chi chiama deve poter dire «non ti è permess…

`SetEngineConfig_OnAForbiddenSection_IsPermissionDenied_NotInvalidArgument`, `SetEngineConfig_OnSecretsAndTopology_IsAlwaysRefused`, `GetEngineConfig_NeverLeaksSecrets_EvenWhenAskedForThemExplicitly`, `SetEngineConfig_WithAnInvalidValue_IsInvalidArgument_WithTheHumanMessage`, `SetEngineConfig_WithMalformedJson_IsInvalidArgument`, `RoundTrip_WriteThenRead_ReturnsWhatWasWritten`, `GetEngineConfig_WithNoSections_ReturnsAllReadable_AndSaysWhereItWrites`, `SendTestNotification_ReportsDisabled_WhenTheEngineChannelIsOff`, `SendTestNotification_ReportsDelivered_OnceTheChannelIsConfigured`, `SetEngineConfig_OnNotifications_RefusesTelegramWithoutAChatId`

## `ExecutionQualityTests.cs` — 10 test, 382 righe

> Verifica end-to-end sul motore: l'arrival price viene fissato prima della chiamata all'exchange, sopravvive alla chiusura e produce la metrica. Stesso impianto di (client scriptato + sonda sul meter).

`Buy_FilledAboveArrival_IsPositiveCost`, `Sell_FilledBelowArrival_IsPositiveCost`, `PriceImprovement_IsNegative`, `MissingOrDegenerateInputs_AreNull_NotZero`, `MatchesExecutionJobConvention`, `OrderExposesShortfall_OnlyWhenArrivalPriceIsKnown`, `TestnetOpen_RecordsArrivalPriceAndShortfall`, `PaperOpen_LeavesExecutionUnmeasured`, `Close_KeepsArrivalPrice_WhichTheFillUsedToOverwrite`, `OrderLatency_IsRecordedWithOutcome`

## `MinTrackRecordTests.cs` — 10 test, 171 righe

> [F4 PRD Valore] Il power check MinTRL: la formula (Bailey-López de Prado 2012/2014) e lo stage che la dichiara in testa al run. I numeri di ancoraggio non sono inventati: sono l'aritmetica che la piattaforma ha già incontrato empiricamente (huntdense 2026-07-31: su un holdout di mesi il puro caso arriva a Sharpe 2,2-2,9 al 99° con centinaia di tentativi, e nessun candidato reale lo supera). Qui quella esperienza diventa un teorema verificato, non un ricordo.

`MinTrl_IsInfinite_WhenObservedDoesNotBeatBenchmark`, `MinTrl_QuartersWhenTheGapDoubles_GaussianCase`, `MinDetectableSharpe_IsTheInverseOfMinTrl`, `Anchor_AnnualizedSharpeOne_NeedsAlmostThreeYears_AgainstZeroBenchmark`, `Anchor_FourMonthHoldout_With300Trials_DemandsImplausibleSharpe`, `ExpectedMaxUnderNull_GrowsWithTrials_ShrinksWithObservations`, `Stage_DeclaresUnderpoweredRun_UpFront_WithoutBlockingByDefault`, `Stage_Enforce_StopsTheRun_WithTheExplanation`, `Stage_LongHoldoutFewTrials_HasPower`, `Stage_Summary_CarriesTheHeadlineNumbers`

## `NewsImpactClassifierTests.cs` — 10 test, 97 righe

> Test di : classificazione per categoria e rilevamento simboli con confronto a WORD BOUNDARY — il caso critico è evitare falsi positivi da semplice substring ("ban" dentro "banana", "sol" dentro "absolute", "ada" dentro "canada").

`Classify_ReturnsExpectedCategory`, `Classify_PrioritizesRegulatoryOverOther_WhenMultipleKeywordsPresent`, `DetectSymbols_FindsBitcoinAndEthereum`, `DetectSymbols_UsesWordBoundary_NoFalsePositiveFromSubstring`, `DetectSymbols_NoMatch_ReturnsEmpty`, `Classify_IsCaseInsensitive`, `Classify_RecognizesForexMacroCategories`, `Classify_PrioritizesCentralBanksOverMacro_WhenBothPresentButCentralBanksStronger`, `DetectSymbols_FindsForexMajorPairs`, `DetectSymbols_UsesWordBoundary_NoFalsePositiveOnEuroSubstring`

## `ProtectiveExitDiagnosticsServiceTests.cs` — 10 test, 374 righe

> [B3] La sentinella d'ombra scriveva su una tabella che nessuna query leggeva, l'allarme sulle posizioni orfane viveva solo nei log del pod, e la misura del ritardo era raggiungibile solo da riga di comando. Codice corretto, testato, e mai chiamato da niente : la stessa forma di C4 prima del suo consumo — verde a livello di classe, inesistente a livello di prodotto. Questi test coprono le letture che il pannello di /trading consuma. Il più importante è : una posizione orfana è un problema della piattaforma, non della corsia che si sta guardando, e mostrarla solo a chi per caso ha selezionato la corsia giusta significa non mostrarla.

`Gli_ombra_sono_per_corsia_e_dal_piu_recente`, `Le_orfane_non_si_filtrano_per_corsia_visualizzata`, `Senza_orfane_la_lista_e_vuota`, `Non_chiude_una_posizione_di_una_corsia_che_esiste`, `Chiude_lorfana_al_prezzo_attuale_e_lascia_traccia`, `Senza_prezzo_recente_non_chiude`, `Senza_configurazione_della_corsia_si_spiega_invece_di_esplodere`, `Senza_stop_configurato_si_spiega`, `Senza_risoluzione_piu_fine_si_dichiara_non_misurabile`, `Con_i_dati_a_posto_la_misura_gira_coi_bracket_veri`

## `RealtimeFeedSwitchTests.cs` — 10 test, 270 righe

> L'interruttore del feed real-time deve essere una manopola vera (2026-07-29). Fino a questo giro RealtimePriceWorker leggeva Enabled UNA volta e usciva: accendere o spegnere il feed richiedeva un riavvio del processo — cioè, col motore in cluster, il riavvio del pod che sta operando su tre corsie. Una manopola che per funzionare pretende di riavviare il motore non è una manopola, ed è esattamente la classe di difetto che questo audit è nato per togliere. La proprietà che conta, e che questi test fissano: a feed spento non si apre alcuna connessione , e accendere/spegnere apre e chiude davvero, senza toccare il resto del processo.

`JustConnected_WithNothingReceivedYet_IsNotAnAlarm`, `ConnectedButMuteBeyondTheGrace_IsStillAnAlarm`, `ReceivedThenWentSilent_IsAnAlarmWithNoGrace`, `ReceivingNormally_IsNeverAnAlarm`, `SeriesAddedMidSession_GetsItsOwnGrace_ThenAlertsIfNeverDelivering`, `Disabled_NoSessionIsEverOpened`, `TurnedOnAtRuntime_TheFeedStarts_WithoutRestartingTheProcess`, `TurnedOffAtRuntime_TheSessionCloses`, `OffThenOnAgain_RestartsInsteadOfStayingDeadForever`, `StoppingTheHost_EndsTheWorker_EvenWhileTheFeedIsIdle`

## `RetailSentimentIngestorTests.cs` — 10 test, 117 righe

> Test di : conversione long%→SentimentScore in [-1,+1], mapping dei simboli crypto (BTCUSD→BTC) al ticker canonico già usato dalla piattaforma, e verifica che il formato JSON reale dell'endpoint FXSSI (fixture) sia deserializzabile come ci si aspetta (nessuna chiamata di rete).

`ParseRatios_ConvertsLongPercentToSentimentScoreRange`, `ParseRatios_50PercentLong_IsNeutralZero`, `ParseRatios_100PercentLong_IsPlusOne`, `ParseRatios_ZeroPercentLong_IsMinusOne`, `ParseRatios_MapsCryptoTickers_ToCanonicalSymbol`, `ParseRatios_KeepsForexPairsAsIs_NoCryptoMapping`, `ParseRatios_AllItemsTaggedAsRetailSentiment`, `ParseRatios_MalformedValue_IsSkippedNotThrown`, `ParseRatios_EachItemHasAUniqueUrl_ForDedupe`, `RealApiJsonShape_DeserializesIntoBrokerSymbolDictionaries`

## `SentimentMetricClientTests.cs` — 10 test, 127 righe

> Test dei parser PURI dei client di metriche sentiment (Sentiment 2.0): nessuna rete, fixture JSON REALI catturate dal vivo il 2026-07-19 dalle API pubbliche (alternative.me /fng/ e fapi.binance.com /futures/data/*) — la cattura stessa è la prova di raggiungibilità.

`ParseFng_RealFixture_ParsesAllDailyPoints`, `ParseFng_MalformedEntry_IsSkippedWithoutFailingTheSource`, `ParseFng_EmptyOrMissingData_ReturnsEmpty`, `ParseRatioSeries_RealGlobalLongShortFixture_MapsSymbolAndValues`, `ParseRatioSeries_RealTakerFixture_UsesBuySellRatioField`, `ParseRatioSeries_RealTopTraderFixture_Parses`, `ParseOpenInterestSeries_RealFixture_EmitsBothMetricsPerPoint`, `ParseFundingRates_RealFixture_ConvertsToPercent`, `ToBaseTicker_MapsUsdtMarketsToBaseAsset`, `ParseRatioSeries_MalformedEntry_IsSkipped`

## `TradingEngineTickExitTests.cs` — 10 test, 323 righe

> [R1] Test del percorso a TICK real-time ( ). Due proprietà sono di sicurezza, non di comodità, e i test che le coprono valgono più di tutti gli altri qui dentro: - un tick NON apre mai una posizione (gli ingressi restano governati dalle candele chiuse, l'unico percorso che il backtest valida); - una raffica di tick sotto lo stop produce UNA sola chiusura, mai una cascata di ordini. Il resto verifica che tick e candela decidano allo stesso modo, essendo passati dalla stessa funzione pura ( ProtectiveExitEvaluator ).

`Tick_BelowStop_ClosesPosition`, `Tick_AboveStop_LeavesPositionOpen`, `TickBurst_BelowStop_ClosesExactlyOnce`, `Tick_NeverOpensPosition_EvenWhenStrategyWouldSignalLong`, `Tick_AboveTakeProfit_ClosesWithTakeProfitReason`, `Tick_HittingBothStopAndTarget_PrefersStop`, `Ticks_AdvanceTrailingStop_AndCloseOnPullback`, `Tick_WithNonPositivePrice_IsIgnored`, `Tick_WhenEngineStopped_DoesNothing`, `Tick_AndCandle_ProduceTheSameExitLevel`

## `VolatilityScalerTests.cs` — 10 test, 197 righe

> Test del dosaggio della posizione sulla volatilità ( ), l'unico risultato di ricerca sopravvissuto al controllo a esposizione media costante (docs/REPORT-DOSAGGIO-VOLATILITA.md). Il test che conta più di tutti è : il tetto a 1,0 è ciò che rende impossibile, accendendo questa funzione, superare i limiti di sicurezza già validati a StartAsync. Se un giorno quel default cambia, quel test deve gridare.

`Disabled_ReturnsOne_BehaviourUnchanged`, `NotEnoughHistory_ReturnsOne_RatherThanGuessing`, `FlatPrices_ReturnsOne_NoDivisionByZeroVolatility`, `HigherVolatility_ProducesSmallerMultiplier`, `WithDefaults_CanOnlyReduceExposure_NeverIncrease`, `Floor_IsRespected_EvenInExtremeVolatility`, `Timeframe_ChangesAnnualisation_SoTheSameSeriesScalesDifferently`, `RealizedVolatility_MatchesAKnownCase`, `Backtest_WithTargetingOff_IsBitIdenticalToBefore`, `Backtest_WithTargetingOn_OpensASmallerPositionInAVolatileMarket`

## `AuditPromotionStateMachineTests.cs` — 9 test, 354 righe

> Audit FASE 3 — la state machine della promozione automatica, attaccata dal lato AVVERSARIALE: (a) fuzz di 20.000 combinazioni metriche×opzioni×modalità su per dimostrare che NESSUN input produce mai un suggerimento Live o un'azione su una corsia Live; (b) il di fronte a decisioni avvelenate (ShouldPromote con SuggestedMode=Live, come farebbe un evaluator buggato o una config corrotta) NON deve mai chiamare il promoter; (c) il tick sopravvive al fallimento di una corsia e processa le altre.

`Decide_Fuzz20k_NeverSuggestsLive_AndFromLiveOnlyTestnetIsReachable`, `Decide_Fuzz20k_FlagOff_LiveLanesAreBitIdenticalToTheHistoricalBehaviour`, `Decide_LiveDemotion_RequiresHistory_AndFiresOnDegradation`, `Tick_PromotesRunningPaperLane_AndDemotesFadedTestnetLane`, `Tick_PoisonedDecision_SuggestingLive_IsNeverActedUpon_AndIsLoggedAsError`, `Tick_StoppedLanes_AreNeverTouched`, `Tick_OneLaneFails_OthersAreStillProcessed`, `Tick_LiveDemotionDecision_IsActedUpon_WithAWarningNotification`, `Tick_PoisonedLiveDecisions_AreNeverActedUpon`

## `BacktestStopLossTests.cs` — 9 test, 229 righe

> Test dell'overlay stop loss / take profit / trailing stop del motore di backtest (McAllen cap. 17: "lo stop loss E' parte del trade"). Candele sintetiche + strategia script che entra long alla prima barra e non emette altro.

`NoStops_BehaviorUnchanged_PositionClosedAtEnd`, `StopLoss_Long_ExitsAtStopLevel`, `StopLoss_GapBelowStop_FillsAtOpen`, `TakeProfit_Long_ExitsAtTarget`, `TrailingStop_Long_LocksInGains`, `StopLoss_Short_ExitsAboveEntry`, `StopAndTarget_SameCandle_StopWins`, `EntryCandle_NotStoppedByOwnExcursion`, `PriceSmaCross_GeneratesLongAboveSma_AndClosesBelow`

## `BotPageServiceTests.cs` — 9 test, 281 righe

> [R3] Test dell'orchestrazione della Modalità Semplice. Le proprietà che contano di più sono due, entrambe di sicurezza: - da qui si avvia SOLO in Paper, mai in Testnet o Live; - avviare senza strategie viene rifiutato con una spiegazione, invece di accendere un motore che non farebbe nulla e lasciare l'utente a fissare una pagina immobile.

`Start_AlwaysUsesPaper_NeverTestnetOrLive`, `Start_WithoutStrategies_IsRefusedWithAnExplanation`, `Start_PersistsCapitalAndProfile_OnTheLane`, `Save_PersistsWithoutStarting`, `Load_RestoresPreviouslyChosenProfile`, `Load_UnknownStoredProfile_FallsBackToDefault_WithoutThrowing`, `TimeframeMismatch_IsDetected_WhenLaneDivergesFromProfile`, `ApplyLatestResearch_WithoutAvailableRun_ReportsInsteadOfThrowing`, `ApplyLatestResearch_PicksTheMostRecentRunWithLegs`

## `CandlestickPatternDetectorTests.cs` — 9 test, 171 righe

> Test del riconoscimento dei pattern candlestick (McAllen cap. 4-6 e 14).

`Doji_AfterUptrend_IsBearishAlert`, `HammerShape_ContextDecidesName`, `ShootingStar_OnlyAfterAdvance`, `Engulfing_RequiresOppositeTrend`, `Harami_LargeThenSmallInsideBody`, `ThreeWhiteSoldiers_Detected`, `KeyReversal_NewHighButCloseBelowPrevClose`, `RisingThreeMethods_ContinuationPattern`, `EmptySeries_NoThrow`

## `DtwPatternAnalysisTests.cs` — 9 test, 282 righe

> [D4, misura] Test della catena completa: forma → occorrenze → event-study col placebo → verdetto. La coppia che decide tutto: - — si pianta un pattern SEGUITO da un movimento vero: la catena deve accorgersene; - — si pianta lo stesso pattern seguito dal nulla: la catena NON deve accorgersi di niente. Solo insieme dimostrano che il verdetto misura il segnale e non la propria voglia di trovarlo.

`PatternFollowedByARealMove_IsDeclaredPredictive`, `TheShapeMatchedNull_IsStricterThanTheRandomDatePlacebo`, `PatternFollowedByNothing_IsDeclaredNoise`, `TooFewMatches_AreRefusedInsteadOfMeasured`, `ShortSeries_IsRefusedWithAReadableReason`, `EmptyInputs_DoNotThrow`, `OccurrenceFrequencyIsReported_BecauseTheObjectiveIsShortHorizonTrading`, `AnalysisIsDeterministic`, `RandomTemplates_OnNoiseSeries_RarelyDeclareASignal`

## `EnsembleAllocatorTests.cs` — 9 test, 101 righe

> Test della pesatura vincolata (water-filling) dell'ensemble.

`SpecExample_RespectsConstraints_SumsToOne`, `AllZeroOrNegative_GivesEqualWeights`, `SingleStrategy_GetsEverything`, `HigherSharpe_GetsMoreCapital`, `RespectsMinFloor_ForZeroSharpeStrategy`, `Shrinkage_Zero_LeavesSharpesUnchanged`, `Shrinkage_One_CollapsesToMean_GivesEqualWeights`, `Shrinkage_MovesWeightsTowardEqual`, `Shrinkage_MinObservations_EqualizesUndertrustedLeg`

## `EventStudyTests.cs` — 9 test, 238 righe

> [T2.7 roadmap macchina-ricerca] Event-study rigoroso + rilevatore di eventi di mercato. Il criterio di validazione dell'item, dichiarato nella roadmap: (a) un effetto PIANTATO viene recuperato (CAAR post positiva, placebo significativo); (b) il PLACEBO su rumore puro non produce significatività (le date a caso non "reagiscono"); (c) tutto deterministico a parità di seme — la stessa disciplina di T1.5, perché il placebo È una randomizzazione temporale.

`PlantedEffect_IsRecovered_WithSignificantPlacebo`, `PureNoise_RandomDates_AreNotSignificant`, `SameSeed_SameResult_Deterministic`, `EventsTooCloseToBoundaries_AreExcluded_NotSilentlyMangled`, `AbnormalReturn_SubtractsBaselineDrift`, `Detector_FindsPlantedCrash_AndCooldownDedupesTheCluster`, `Detector_FindsVolumeBlowout_AgainstRollingMedian`, `PostCrashSignal_Is100AtTheEvent_DecaysLinearly_AndZeroWithoutEvents`, `Detector_IsCausal_FutureBarsDoNotChangePastEvents`

## `FleetOrchestratorTests.cs` — 9 test, 239 righe

> [AF2] Il core puro della Queen Bee, attaccato come la promozione: fuzz 20k stati sugli invarianti (mai un'azione su impronta/Live/Testnet/quarantena/campagne/emergency, mai due assegnazioni sulla stessa corsia, mai un candidato senza frequenza sopra soglia) + scenari a mano per il comportamento voluto + la proprietà di quiete (stato sano ⇒ nessuna azione).

`Decide_Fuzz20k_NeverTouchesProtectedLanes_NeverDoubleAssigns`, `Decide_HealthyQuietState_Produces100TicksOfNothing`, `Assignment_OldestCandidate_GetsTheLowestFreeLane`, `Retirement_RequiresHistory_AndFiresOnLosers`, `GreyBand_IsProposed_NeverAssigned`, `ExposureGuardOff_BlocksNewAssignments_BeyondTheThreshold`, `NoFreeLanes_ReportsTheBlock_WithTheQueueSize`, `MultipleEligibleCandidates_ExposeTheMenu_WithTheRuleChoiceAsDefault`, `HandledCandidates_AndLowFrequencyOnes_NeverEnterTheQueue`

## `HierarchicalClusteringTests.cs` — 9 test, 133 righe

> Test di : struttura del dendrogramma su una matrice di distanza nota (2 coppie ben separate), differenza fra i criteri di linkage, e .

`FourPoints_TwoClearPairs_MergesWithinPairsFirst`, `ThreePoints_LinkageMethod_ChangesSecondMergeDistance`, `LeafNodes_HaveZeroDistanceAndSingleIndex`, `MismatchedMatrixSize_Throws`, `TooFewLabels_Throws`, `CorrelationDistance_PerfectCorrelation_IsZero`, `CorrelationDistance_PerfectAnticorrelation_IsOne`, `CorrelationDistance_ZeroCorrelation_IsSqrtHalf`, `CorrelationDistance_NonSquareMatrix_Throws`

## `KellyCalculatorTests.cs` — 9 test, 131 righe

> Test del criterio di Kelly (Jansen ML4T, cap. 5).

`BinaryKelly_HandComputed`, `FromTradeHistory_UsesWinRateAndPayoff`, `FromTradeHistory_NoLosses_ReturnsZero`, `ContinuousKelly_ClosedForm`, `ContinuousKellyNumeric_MatchesBookExample`, `EmpiricalKelly_FatLeftTail_BetsLessThanNormalApproximation`, `EmpiricalKelly_NoEdge_ReturnsZero`, `MultiAssetKelly_PrefersHigherSharpeAsset`, `MultiAssetKelly_InvalidInput_Throws`

## `LlmFailoverTimeoutTests.cs` — 9 test, 229 righe

> [2026-08-05] Il failover deve partire anche quando un provider SI APPENDE. Il difetto che questi test chiudono , trovato provando l'app dal vivo: il guard crea un token linked (token del chiamante + timeout della chiamata) e passa quello al client; il delegante vedeva SOLO quel token — cancellato sia dallo shutdown sia dal timeout — e il suo when (ct.IsCancellationRequested) lo faceva rilanciare senza provare il provider successivo. Risultato osservato: Nvidia in timeout, e Groq/Gemini/HuggingFace — vivi, con chiave, raggiungibili — mai interpellati. Un provider che si appende è il modo più comune in cui un provider gratuito smette di funzionare: è esattamente il caso per cui la catena esiste. La distinzione che il rimedio introduce : ogni anello ha un proprio budget di tempo. Scaduto que…

`ProviderCheSiAppende_PassaAlSuccessivo`, `DueProviderAppesi_ServeIlTerzo`, `TuttiAppesi_LanciaSenzaRestarePiantato`, `CancellazioneEsterna_NienteFailover`, `BudgetAZero_ComportamentoStorico`, `ProviderSano_NessunTentativoInPiu`, `ErroreNormale_FailoverComePrima`, `Default_BudgetPerProviderAttivo`, `AdminConfigRules_ValidaIlBudgetPerProvider`

## `MetaLabelerTests.cs` — 9 test, 201 righe

> [C4] Verifica del meta-labeling. Il test che conta è l'ultimo: un **edge piantato** con asimmetria di barriera nota, che la catena deve recuperare. Senza quello, un risultato positivo su dati reali non direbbe se ha funzionato il metodo o il caso — è lo stesso principio della fase `control` di PlatformExpand.

`OnlyBarsWithASignal_BecomeSamples`, `SignalsAreLabelledFromTheirOwnSide`, `ATimeExitIsNotCountedAsSuccess`, `MisalignedSignals_AreRejectedInsteadOfSilentlyTruncated`, `Evaluate_ReportsPrecisionRecallAndSurvivalTogether`, `APrecisionGainOnACollapsedSampleIsNotCountedAsImprovement`, `ARealImprovementIsRecognised`, `MisalignedProbabilities_AreRejected`, `PlantedEdge_IsRecoveredByTheChain`

## `MetaModelTrainerTests.cs` — 9 test, 212 righe

> [C4, chiusura] Verifica del meta-modello. Il test che decide se il pezzo vale qualcosa è accoppiato a : il primo pretende che un segnale imparabile venga trovato, il secondo che il rumore NON produca un miglioramento. Solo insieme dicono che il modello impara invece di adattarsi.

`LearnableSignal_IsRecoveredOutOfFold`, `PureNoise_RarelyProducesAFakeImprovement`, `SelectionZScore_IsZeroWhenTheFilterPicksAtRandom`, `PurgeWindow_IsNeverShorterThanTheLabelHorizon`, `SampleWeightsAreHonouredByTheTrainer`, `TooFewSamples_AreDeclaredInsteadOfScoredInSample`, `SingleClassSamples_ProduceNoModel`, `MisalignedFeatures_AreRejected`, `TrainingIsReproducible`

## `ModelRegistryTests.cs` — 9 test, 274 righe

> Test del Model Registry (Fase 2): gate del Deflated Sharpe sulla promozione a Champion, invariante "un solo Champion per (Symbol, Timeframe)", e ciclo chiuso col drift (Champion in Alert → Retired + retrain accodato, mai Live). DB Postgres effimero (Testcontainers) via EnsureCreated.

`Promote_FirstChampion_SucceedsWhenNoIncumbent`, `Promote_NonDirectionalModel_IsRejectedBySemantics_EvenWithGoodDsr`, `Promote_LowerDsr_IsRejected_AndIncumbentStays`, `Promote_HigherDsr_ReplacesIncumbent_AndKeepsSingleChampion`, `Promote_WithoutDsr_IsRejected`, `Champion_IsScopedPerSymbolTimeframe`, `Retire_WithRetrain_SetsReasonAndRetrainMarker`, `DriftWorker_ChampionInAlert_IsRetiredAndRetrainRequested`, `DriftWorker_StagingModelInAlert_IsNotRetired`

## `PairsBacktestEngineTests.cs` — 9 test, 242 righe

> Test di : allineamento per timestamp, generazione di trade su uno spread costruito per divergere e rientrare (mean-reverting per costruzione), determinismo, contabilità dollar-neutral a due gambe, e casi limite.

`RunBacktest_OnOscillatingSpread_GeneratesTrades`, `RunBacktest_IsDeterministic`, `RunBacktest_AlignsCandlesByTimestamp_IgnoringGaps`, `RunBacktest_NoOverlappingTimestamps_ReturnsInitialCapital`, `RunBacktest_Trades_AreDollarNeutralAtEntry`, `RunBacktest_Slippage_ReducesFinalCapital`, `RunBacktest_MaxHoldBars_CapsHoldingAndTagsExit`, `RunBacktest_TightDivergenceStop_ProducesStopZScoreExits`, `RunBacktest_AllClosedTrades_HaveKnownExitReason`

## `ProtectiveExitAuditTests.cs` — 9 test, 139 righe

> [2026-08-06] Il controllo che avrebbe dovuto accorgersi al posto del proprietario. Il caso vero che lo motiva: short ETC/USDT a 7,07 con take profit a 6,378554 ; la barra 4h del 06/08 08:00 ha segnato minimo 6,31 e la posizione è rimasta aperta. Il primo test riproduce esattamente quei numeri.

`ShortColTargetToccato_EUnAnomalia_ColSuoPrimoIstante`, `ProtezioniMaiToccate_NessunaAnomalia`, `PerUnLong_IlTargetLoToccaIlMassimo_ELoStopIlMinimo`, `PerUnoShort_IlVersoEOpposto`, `LeBarrePrecedentiAllAperturaNonContano`, `LaBarraDellAperturaConta`, `SenzaProtezioniImpostate_NienteDaControllare`, `ProtezioneAZero_NonEUnLivello`, `LeBarreDiUnAltroSimboloNonContano`

## `PurgedTimeSeriesCvTests.cs` — 9 test, 114 righe

> Test di : copertura completa del test set attraverso i fold, nessuna sovrapposizione train/test, e correttezza delle bande di purge/embargo.

`Split_ProducesRequestedNumberOfFolds`, `Split_TestSetsCoverAllSamplesExactlyOnce_NoPurgeEmbargo`, `Split_TestSetsAreContiguousAndNonOverlapping`, `Split_NoPurgeEmbargo_TrainIsExactlyComplementOfTest`, `Split_WithPurgeAndEmbargo_TrainExcludesBandsAroundTest`, `Split_TrainAndTest_NeverOverlap_ForAnyPurgeEmbargo`, `Split_LastFold_AbsorbsRemainder`, `Split_TooFewSamplesForFolds_Throws`, `Split_InvalidFolds_Throws`

## `RiskFactorPcaTests.cs` — 9 test, 148 righe

> Test di : casi limite noti (correlazione perfetta -&gt; una sola componente spiega tutta la varianza; simboli indipendenti -&gt; varianza spiegata ripartita), proprietà strutturali (autovettori normalizzati, lunghezza degli score) e validazione input.

`PerfectlyCorrelatedSymbols_FirstComponentExplainsAllVariance`, `IndependentSymbols_VarianceIsSpreadAcrossComponents`, `Loadings_AreUnitNormalized_PerComponent`, `Scores_HaveSameLengthAsInputSeries`, `ExplainedVarianceRatios_AreDescending`, `TooFewSymbols_Throws`, `MismatchedSeriesLengths_Throws`, `TooFewObservations_Throws`, `ComponentCountOutOfRange_Throws`

## `SecurityDefaultsTests.cs` — 9 test, 86 righe

> [C-02, Fase 1 PRD-RISANAMENTO] Le "regole da non violare" di CLAUDE.md rese ESEGUIBILI. Fin qui vivevano solo in un file Markdown, e infatti una era già stata violata senza che nessuno se ne accorgesse: DriveProtectiveExits aveva default true mentre la misura B3 (docs/REPORT-B3-EXITLAG-2026-07-28.md: uscire al tocco è peggio in 24/24 configurazioni) e la regola 7 dicevano false — chi accendeva il feed ereditava in silenzio l'assetto bocciato. Da oggi un default di sicurezza che cambia fa fallire la CI, e chi lo cambia DELIBERATAMENTE deve aggiornare anche questo file, cioè dichiararlo.

`RealtimeFeed_IsObservationalByDefault`, `RegimeRouting_ClassifiesButDoesNotDecide`, `Promotion_NeverAutomatesTowardsLive`, `LiveOrders_RequireManualConfirmation`, `VolatilityScaling_CanOnlyReduceExposure`, `CarryMode_CannotRepresentLive`, `FleetOrchestrator_IsOffAndDryRunByDefault`, `SlicedLiveExecution_IsOffByDefault`, `CarryForwardTest_IsOffByDefault`

## `TapeAggregatorTests.cs` — 9 test, 144 righe

> [D3 / C5 §9.2] Aggregazione del tape in barre da N secondi. I test guardano i BORDI, che è dove un aggregatore sbaglia in silenzio: un trade esattamente sul confine, una barra senza scambi, un trade del giorno dopo in coda al file (i dump giornalieri di Binance ne contengono).

`ATradeExactlyOnTheBoundary_BelongsToTheNewBar`, `EmptyBarsAreKept_SoTheGridStaysRegular`, `TradesOutsideTheWindowAreIgnored`, `TheVolumeIsConserved_NothingIsLostOrCountedTwice`, `TheCloseOfABar_IsThePriceOfItsLastTrade`, `GroupBy_RefusesADurationThatIsNotAnIntegerMultiple`, `GroupBy_DropsTheTruncatedFirstGroupInsteadOfMixingIt`, `GroupImbalance_AggregatesVolumesNotAverages`, `AnEmptyGroup_HasNoImbalance`

## `VolumeSignalsAndRegimeFeaturesTests.cs` — 9 test, 198 righe

> [3.8a roadmap macchina-ricerca] OBV/MFI/VWAP riusabili + volume/breadth nei regimi. Fissa: (a) la correttezza numerica dei tre indicatori contro conti a mano; (b) i due segnali nuovi del catalogo (append-only, anti-look-ahead per troncamento); (c) la retro-compatibilità del clustering — con i flag OFF i vettori sono IDENTICI a prima e un FeatureScaling salvato senza "Names" deserializza alle 4 feature storiche.

`Obv_MatchesHandComputedCumulativeSignedVolume`, `Mfi_ExtremesAndWarmup_BehaveLikeAVolumeWeightedRsi`, `Mfi_PathologicalFlowRatio_DoesNotOverflow`, `Mfi_WeighsByVolume_NotJustDirection`, `RollingVwap_IsVolumeWeightedTypicalPrice_OverTheWindow`, `CatalogSignals_MfiNative_ObvSlopePercentile_AreCausalByTruncation`, `ClusteringVector_DefaultUnchanged_OptInAppends`, `FeatureScaling_DeserializedWithoutNames_FallsBackToTheFourHistoricalFeatures`, `NormalizeFeatures_WithFlags_ScalesTheExtraColumns_AndRecordsNames`

## `AlphaMiningTests.cs` — 8 test, 188 righe

> Test del formulaic alpha mining (rif. docs/archive/ROADMAP-QLIB.md §1.7 ): gli alberi di espressione sono anti-look-ahead per costruzione, la serializzazione fa round-trip, i fattori minati si ricreano dal nome tramite la factory esistente, e il miner genetico è deterministico e trova un segnale su una serie con momentum reale.

`ExpressionTree_IsAntiLookAhead`, `Serialization_RoundTrips`, `MinedFactor_RoundTripsThrough_SavedFactorSpecDto`, `MinedFactor_RoundTripsThroughAlphaFactory`, `Miner_FindsPredictiveFactor_OnMomentumSeries`, `Miner_IsDeterministic_ForSameSeed`, `ComputeSelectionPbo_OnMinedPanel_IsValidProbability_AndDeterministic`, `ComputeSelectionPbo_FewerThanTwoFactors_ReturnsNull`

## `AuditCvLeakageTests.cs` — 8 test, 167 righe

> Audit FASE 1 — proprietà anti-leakage della cross-validation temporale, verificate come INVARIANTI su griglie di parametri (non su singoli esempi): nessun indice di train dentro le bande purge/embargo, nessuna finestra label del train che tocca il test quando purge ≥ orizzonte della label, determinismo, conteggio combinatorio del CPCV.

`PurgedCv_NoTrainIndexEverFallsInPurgeOrEmbargoBand`, `PurgedCv_TrainLabelWindow_NeverOverlapsTestSet_WhenPurgeCoversHorizon`, `PurgedCv_IsDeterministic`, `PurgedCv_ExtremeBands_TrainCanBecomeEmpty_ButNeverLeaks`, `Cpcv_TrainNeverIntersectsAnyPurgeEmbargoBand`, `Cpcv_TestIndices_AreExactlyTheUnionOfChosenGroups`, `Cpcv_IsDeterministic_AndCombinationsAreLexicographic`, `Cpcv_AdjacentTestGroups_OverlappingBands_AreUnionedWithoutDuplicates`

## `AuditPortfolioDegenerateTests.cs` — 8 test, 191 righe

> Audit FASE 1 — ottimizzatori di portafoglio su matrici di covarianza sintetiche DEGENERI: non positive definite, esattamente singolari (asset duplicato), con asset a varianza zero, rendimenti tutti costanti. Contratto atteso: mai eccezioni né NaN (un NaN esploderebbe nel cast a decimal), pesi sempre normalizzati (somma 1) e dentro i vincoli Min/Max.

`Erc_NonPositiveDefiniteCovariance_StaysFiniteAndNormalized`, `Erc_IndefiniteThreeAssetMatrix_StaysFiniteAndNormalized`, `RiskContributions_NegativePortfolioVariance_FailsSafeToZeros`, `MeanVariance_DuplicatedAsset_SingularCovariance_AllEstimatorsAndObjectives_ProduceValidWeights`, `AllOptimizers_ZeroVarianceAsset_NoThrow_ValidWeights`, `MeanVariance_AllReturnsConstant_TotallyDegenerate_NoThrow`, `LedoitWolf_ConstantReturns_ShrinkageInUnitInterval_NoNaN`, `AllOptimizers_HonorMinMaxBounds_OnCorrelatedUniverse`

## `BinanceClientOrderStatusTests.cs` — 8 test, 167 righe

> Test del lookup di stato ordine per clientOrderId (fix C2): endpoint corretti, media di fill ricavata da cummulativeQuoteQty/executedQty (l'endpoint di QUERY non restituisce fills[] come il place), e soprattutto la distinzione che chiude la finestra dell'ordine duplicato: -2013 ("Order does not exist") = NON TROVATO certo; qualunque altro errore = IGNOTO ( ), mai "non trovato".

`GetOrderStatusAsync_Filled_AvgPriceFromCumulativeQuoteOverExecuted`, `GetOrderStatusAsync_OpenNotExecuted_NoFillFields`, `GetOrderStatusAsync_Error2013_IsCertainNotFound_NotUncertain`, `GetOrderStatusAsync_OtherApiError_IsUncertain_NeverNotFound`, `GetOrderStatusAsync_Http500_IsUncertain`, `GetOrderStatusAsync_CancelledUnfilled_IsTerminalUnfilled`, `GetFuturesOrderStatusAsync_Filled_UsesFapiEndpointAndAvgPrice`, `GetFuturesOrderStatusAsync_NewOrder_AvgPriceZeroTreatedAsNoFill`

## `CorrelatedExposureTests.cs` — 8 test, 306 righe

> [Fase 2 — docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Prima di questo guard tutti i limiti di rischio a runtime erano scalari e ciechi alla correlazione: tetto sulla singola posizione, tetto sull'esposizione della corsia, numero massimo di posizioni. Tre corsie long su tre altcoin che si muovono con BTC contavano quindi come tre scommesse indipendenti, mentre erano una sola scommessa di taglia tripla — ed è nei crash, quando le correlazioni crypto tendono a 1, che la differenza si paga tutta insieme. I due comportamenti che questi test difendono, perché sono quelli che si sbagliano per primi: il segno (una copertura genuina non deve essere punita come se aggiungesse rischio) e il fail-safe verso il permesso (senza storico non si blocca al buio).

`ThreeCorrelatedLongs_AggregateBeyondLimit_AreCaught`, `UncorrelatedPositions_DoNotBlock`, `CorrelatedShort_OffsetsInsteadOfAdding`, `SamePositionSymbol_CountsFully_WithoutEstimating`, `WithoutPriceHistory_DoesNotBlock`, `Disabled_IsInert`, `PaperPositions_DoNotConstrainTestnet`, `AlignedLogReturns_JoinOnTimestamp_NotOnPosition`

## `DonchianIndicatorAndStrategyTests.cs` — 8 test, 171 righe

> Test di SMA, Donchian Channel e della strategia DonchianBreakout (cap. 3 e 6).

`Sma_MatchesNaive_AndAlignsWarmup`, `Donchian_HhvLlv_MatchNaive`, `Donchian_MismatchedLengths_Throws`, `DonchianBreakout_Long_EntersOnBreakout_ExitsOnLlvViolation`, `DonchianBreakout_ShortDirection_EntersOnBreakdown`, `DonchianBreakout_LongOnly_IgnoresBreakdown`, `DonchianBreakout_InvalidParameters_Throw`, `Factory_CreatesDonchianBreakout`

## `DriftDetectorTests.cs` — 8 test, 119 righe

> Test dei detector di concept drift (rif. docs/archive/ROADMAP-QLIB.md §1.5 ): su due campioni dalla STESSA distribuzione non deve scattare drift; su una distribuzione chiaramente spostata deve scattare (PSI alto / KS p→0 / Page-Hinkley oltre soglia). Dati insufficienti ⇒ None.

`Psi_SameDistribution_NoDrift`, `Psi_ShiftedDistribution_Alerts`, `Ks_SameDistribution_HighPValue_NoDrift`, `Ks_ShiftedDistribution_LowPValue_Alerts`, `PageHinkley_StationaryStream_NoDrift`, `PageHinkley_MeanShiftedStream_Alerts`, `AllDetectors_InsufficientData_ReturnNoneWithoutThrowing`, `ConstantReference_IsHandledGracefully`

## `FundingHistoryTests.cs` — 8 test, 166 righe

> [T0.2 roadmap macchina-ricerca] Funding storico e FIRMATO nel motore di backtest. Il difetto corretto: il motore addebitava una costante senza segno a qualunque posizione. Nella realtà il funding è firmato e va per lato — con funding positivo il long paga e lo short INCASSA. La costante penalizzava sistematicamente gli short, cioè metà del catalogo, ed era esattamente il tipo di distorsione invisibile che una selezione "onesta sui costi" non può permettersi.

`Lookup_ReturnsLatestEventAtOrBeforeTimestamp`, `Lookup_BeforeFirstEvent_FallsBackToConstant_InsteadOfInventing`, `Lookup_NullOrEmptyHistory_YieldsNull_SoTheConstantPathIsUsed`, `PositiveFunding_LongPays_ShortReceives`, `HistoricalSeries_OverridesTheConstant_AndRespectsSign`, `SeriesStartingMidRun_UsesConstantBeforeAndSeriesAfter`, `ZeroConstantAndNoHistory_ChargesNothing_BehaviourUnchanged`, `ProviderBaseTicker_MatchesSentimentConvention`

## `FuturesPositionReconcilerTests.cs` — 8 test, 291 righe

> Test DEDICATO di — colma il gap segnalato ma non chiuso in Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.8: "resta senza un test reale dedicato — copertura solo indiretta via ProcessCandleAsync") ed emerso di nuovo nell'audit leggero §8: il fake futures di ritorna GetPositionAsync = null fisso e l'unico test futures apre una posizione e si ferma PRIMA che la riconciliazione scatti — quindi i tre rami reali del riconciliatore (chiusura forzata su flat remoto, allerta-una-volta su posizione remota non tracciata, no-op su posizione combaciante) non erano mai esercitati direttamente. È un percorso a soldi veri: il ramo "flat remoto + aperta in locale" forza la chiusura al miglior prezzo noto come Liquidation/ExternalClose e registra un trade liquidato. Qui il collaboratore è testato in isolam…

`RemoteFlat_LocalOpen_ForceClosesAtLastKnownPrice_AsExternalClose`, `RemoteFlat_ClosesOnlyPositionsForThisSymbol`, `RemoteOpen_LocalUnknown_NotYetAlerted_AlertsOnceAndAudits`, `RemoteOpen_LocalUnknown_AlreadyAlerted_StaysAlerted_NoDuplicateAudit`, `RemoteOpen_LocalKnown_NoAction_ResetsAlertFlag`, `PaperMode_SkipsEntirely_NoExchangeCall`, `NoCredentials_SkipsEntirely_NoExchangeCall`, `NetworkError_OnGetPosition_SkipsCycle_LeavesAlertUnchanged_NoClose`

## `JumpModelTests.cs` — 8 test, 175 righe

> [C1 roadmap integrazione] Lo statistical jump model su regimi sintetici PIANTATI: la verità è nota per costruzione, quindi si misura il recupero, non lo si racconta. La proprietà che conta: λ=0 degenera in K-means (flicker), λ moderato recupera la segmentazione persistente vera, λ enorme collassa in un solo stato — la manopola fa quello che la letteratura promette.

`LambdaModerata_RecuperaLaSegmentazionePiantata`, `LambdaZero_EKMeans_FlickeraSuiClusterVicini`, `LambdaEnorme_CollassaInUnSoloStato`, `StessoSeed_StessoRisultato`, `DecodificaCausale_ConcordaConLOfflineSuDatiPuliti`, `DecodificaCausale_HaIsteresi_NonFlickera`, `Standardizzazione_MediaZeroVarianzaUno_ERiapplicabile`, `RunLengths_SuPercorsoNoto`

## `LaneInvariantCheckerTests.cs` — 8 test, 130 righe

> Test PURI degli invarianti contabili (Fase 0-A3, PRD Autonomia Operativa §3): nessun DB, nessun motore. Il caso guida è quello REALE della corsia 2 Testnet del 2026-07-18 (docs/TEST-UI-2026-07-18.md, bug B1): PnL -1.817.925 su capitale 10.000 con leva 2 — uno stato che il watchdog deve riconoscere alla prima passata.

`HealthyLane_NoViolations`, `RealCorsia2Case_PnlAndCapital_BothViolated`, `RealCorsia2Case_AbsurdNotional_ViolatesExposure`, `UnrealizedPnl_CountsTowardTotalPnl`, `SmallNegativeAvailable_WithinTolerance_NotViolated`, `NonPositiveCapital_IsViolation_AndShortCircuits`, `ExposureUsesEntryPrice_WhenMarkNotYetArrived`, `LeverageBelowOne_TreatedAsOne`

## `MakerFillModelTests.cs` — 8 test, 256 righe

> [R3] Test del modello di fill per gli ingressi MAKER nel backtest. Il motivo per cui questo modello esiste: la frontiera dei costi (docs/REPORT-RICERCA-2026-07.md) mostrava che a commissioni maker un candidato in perdita diventava profittevole. Quel numero però assumeva che ogni ordine limite venisse riempito al suo prezzo, che è l'assunzione ottimistica per eccellenza: un limite passivo si riempie solo quando il mercato ci viene addosso. Senza modellare il mancato riempimento, "passare a maker" sembra uno sconto sulle commissioni e basta. I test qui sotto fissano le proprietà del modello; la misura di quanto costi davvero la selezione avversa sulle strategie reali va fatta sui dati, non qui.

`PriceNeverComesBack_LimitIsNotFilled_AndTheSignalIsLost`, `PriceDipsToTheLimit_FillsAtTheLimitPrice_WithMakerFee`, `QueuePenetration_WickThatOnlyKissesTheLimit_DoesNotFill`, `QueuePenetration_DecisiveMoveThroughTheLimit_Fills`, `QueuePenetration_Zero_IsBitIdenticalToTouchFill`, `LimitExpiresUnfilled_WithFallback_EntersAtMarketInstead`, `PersistentSignal_PlacesOneLimitPerOpportunity_NotOnePerCandle`, `TakerIsTheDefault_AndLeavesBehaviourUnchanged`

## `MasterKeyRotationTests.cs` — 8 test, 151 righe

> Keyring della rotazione master key (Fase 0 PRD-RISANAMENTO, 2026-08-08 — chiude il TODO storico di : "manca il supporto multi-chiave"). Le proprietà che contano, tutte qui: 1. un payload cifrato con la chiave VECCHIA si decifra quando la vecchia è in PreviousMasterKeys; 2. si CIFRA sempre con la corrente (un servizio con la SOLA corrente rilegge tutto il nuovo); 3. la classificazione distingue vecchio da nuovo — è ciò che guida la ri-cifratura di massa; 4. SENZA keyring il payload vecchio fallisce come sempre (nessun ammorbidimento di default); 5. un payload MANOMESSO fallisce anche col keyring pieno (il fallback prova altre chiavi, non perdona i tag rotti); 6. il formato v1 è invariato: niente migrazione dei payload esistenti.

`OldPayload_DecryptsViaRing`, `Encrypt_AlwaysUsesCurrentKey`, `IsEncryptedWithCurrentKey_ClassifiesOldAndNew`, `WithoutRing_OldPayload_StillThrows`, `TamperedPayload_ThrowsEvenWithFullRing`, `RingOrder_TriesAllPreviousKeys`, `DuplicateOfCurrentKey_InPrevious_IsIgnored`, `V1Format_Unchanged_NoMigrationNeeded`

## `MicrostructureParserTests.cs` — 8 test, 158 righe

> [D3] Lettura dei dump Binance. Le righe usate qui sono COPIATE dai file veri (spot e futures del 2026-07-25), non inventate: i due formati differiscono in tre punti — header, unità del timestamp, maiuscole del booleano — e un parser tarato su uno solo leggerebbe l'altro producendo barre tutte vuote tranne una. Che poi darebbe IC zero, indistinguibile dal verdetto "nessuna informazione".

`SpotAggTrades_AreReadWithoutHeaderAndWithMicrosecondTimestamps`, `FuturesAggTrades_SkipTheHeaderAndUseMillisecondTimestamps`, `IsBuyerMaker_MeansTheAggressorWasTheSeller`, `Epoch_UnitIsDeducedFromTheOrderOfMagnitude`, `MalformedLines_AreCountedNotSilentlyDropped`, `BookDepth_GroupsTheRowsOfEachSnapshotAndExposesTheBands`, `BookDepth_AMissingBand_IsNullNotZero`, `Klines_AreReadIntoTheSameOhlcvEntityAsThePlatformCandles`

## `MlDatasetBuilderTests.cs` — 8 test, 169 righe

> Test di : allineamento feature/target, scarto delle righe incomplete (warm-up/coda), e correttezza della conversione a IDataView di ML.NET.

`Build_DropsWarmupAndTailRows_KeepsOnlyCompleteRows`, `Build_MultipleFactors_FeatureVectorMatchesFactorCount`, `Build_RowsAreInTemporalOrder`, `Build_LabelMatchesForwardReturn_Directly`, `ToDataView_ProducesCorrectSchemaAndRowCount`, `ToDataView_WithIndices_SelectsOnlyRequestedRows`, `Build_NoFactors_Throws`, `Build_InvalidHorizon_Throws`

## `MonteCarloAnalyzerTests.cs` — 8 test, 106 righe

> Test della Montecarlo Analysis evoluta (Trombetta cap. 8).

`Run_Deterministic_WithSeed`, `Run_OriginalEquityAndDrawdown_HandComputed`, `Run_ShufflesPreserveProfit_WhenAllOperationsUsed`, `Run_Percentile95_IsBetween_BestAndWorst`, `Run_ExtraCosts_ReduceFinalProfit`, `Run_SubsetRecombination_UsesRequestedSampleSize`, `Run_EmptyTrades_ReturnsEmptyResult_NoThrow`, `Run_InvalidConfig_Throws`

## `RegimeAugmentationTests.cs` — 8 test, 260 righe

> Test del regime one-hot appeso al vettore di feature (follow-up "regime nel meta-learner dello stacking"). Copre: (a) regressione — feature disattivata ⇒ comportamento identico; (b) dimensione +K quando attiva; (c) anti-look-ahead — candele future non cambiano l'etichetta di un punto passato; (d) parità train/serve — (batch) e (streaming) producono lo STESSO vettore aumentato sulla stessa serie.

`Append_SetsSingleOneHotColumn_UnknownIsAllZeros`, `Append_DisabledOrEmpty_ReturnsInputUnchanged`, `DatasetBuilder_Default_IsBitIdenticalToBefore`, `DatasetBuilder_WithRegime_AppendsKOneHotColumns`, `DatasetBuilder_RegimeIdsMisaligned_Throws`, `LabelByCandle_FutureCandlesDoNotChangePastRegime`, `TrainServe_Parity_DatasetAndMlStrategyProduceSameAugmentedVector`, `MlStrategy_RegimeWithSequencePredictor_ThrowsAtConstruction`

## `SentimentCompositeCalculatorTests.cs` — 8 test, 155 righe

> Test PURI di : z-score sul baseline, rinormalizzazione dei pesi con componenti mancanti, bounds del composite, flag contrarian esattamente alle soglie, Δ7d del Fear &amp; Greed, variazione % dell'open interest, input vuoto → neutro senza flag.

`EmptyInput_ProducesNeutralSnapshot_WithoutExtremes`, `FearGreed_ExtremeFear_FlagsContrarian_AndComputesDelta7d`, `FearGreed_AtThresholds_FlagsExactlyAtBoundaries`, `ZScore_RequiresEnoughObservations_AndNonFlatSeries`, `FundingSpike_FlagsLongSqueezeRisk_AndPushesCompositePositive`, `Weights_AreRenormalizedOverAvailableComponents`, `OpenInterest_Change24h_IsContextOnly_NeverInComposite`, `SymbolNews_FeedsSymbolComposite_MarketNewsFeedsMarketComposite`

## `ShapContextLensTests.cs` — 8 test, 203 righe

> [D1] Verifica della LENTE con cui la matrice SHAP raggruppa le righe. Il PRD §5a chiedeva i regimi K-means; la lente è iniettabile e ripiega sui terzili di volatilità quando quel modello non c'è. Qui si controlla che il raggruppamento sia corretto, che il ripiego non cambi il comportamento preesistente, e che una riga senza etichetta esca dalla matrice invece di finire in una colonna inventata.

`AnInjectedLens_GroupsRowsIntoItsOwnColumns`, `ColumnOrderFollowsTheLens_NotTheOrderOfAppearance`, `RowsWithoutALabel_StayOutOfTheMatrix`, `ALensWithASingleState_ProducesExactlyOneColumn`, `WithoutALens_TheResultIsTheVolatilityFallback`, `PassingTheVolatilityLensExplicitly_MatchesTheImplicitFallback`, `VolatilityLens_OnASeriesTooShort_IsEmptyInsteadOfWrong`, `AnalysisWithALensIsDeterministic`

## `TechnicalIndicatorsTests.cs` — 8 test, 222 righe

> Test di correttezza degli indicatori su dati noti + cross-validation con implementazioni di riferimento "ingenue" + invarianti strutturali.

`Rsi_KnownVector_FirstValue_Is_70_46`, `Rsi_AllValues_StayWithin_0_100`, `Rsi_MatchesNaiveReference`, `Ema_Seed_Equals_Sma_AndMatchesNaive`, `Macd_Histogram_Equals_Macd_Minus_Signal`, `Macd_Line_Equals_FastEma_Minus_SlowEma`, `Bollinger_Ordering_And_Middle_Is_Sma`, `ShortSeries_ReturnsAllNull_ButSameLength`

## `TradeStatisticsTests.cs` — 8 test, 147 righe

> Test del performance report basato sui trade (TradeStatistics, Trombetta cap. 6-7).

`TradeReport_BasicMetrics_HandComputed`, `TradeReport_EmptyTrades_AllZero_NoThrow`, `DrawdownMoney_KnownCurve_HandComputed`, `DelayBetweenPeaks_KnownCurve_HandComputed`, `KestnerRatio_LinearGrowth_IsHigh_ErraticIsLower`, `AnnualAndMonthlyAggregates_GroupCorrectly`, `Gpdi_IdenticalDistributions_Is_Zero_BetterOos_Is100`, `Gpdi_EmptyInput_IsZero`

## `TradingEngineFillSanityTests.cs` — 8 test, 418 righe

> Regressione del bug CRITICO B1 (docs/TEST-UI-2026-07-18.md): la corsia 2 Testnet è andata a PnL -1,8M su capitale 10k perché il testnet ha risposto "Filled" con quantità cumulative (100x+) e prezzo 0, e il motore le ha adottate così com'erano. Il fix (FillSanityCheck) valida il fill di RITORNO contro la quantità richiesta e il prezzo corrente: fuori banda l'APERTURA viene rifiutata come esito incerto (audit FillSanityRejected, mai adottare il fill), la CHIUSURA si finalizza al prezzo di riferimento locale (rifiutarla riaprirebbe l'oversell H2). Stesso pattern di : client scriptati a code, un accesso oltre lo script fa fallire il test (fail loudly, nessun default silenzioso).

`SpotOpen_FillPriceZero_Rejected_NoPosition_CapitalUntouched`, `SpotOpen_FillQuantity100x_Rejected_NoPosition_CapitalUntouched`, `SpotOpen_ReconciledFillInsane_Rejected_NoPosition`, `FuturesOpen_FillQuantity100x_Rejected_NoPosition_CapitalUntouched`, `SpotOpen_SaneSlippage_StillAdopted`, `SpotClose_FillPriceInsane_FinalizedAtReferencePrice`, `SpotClose_ReconciledFillPriceInsane_FinalizedAtReferencePrice`, `FuturesClose_FillPriceZero_FinalizedAtReferencePrice`

## `TreeShapTests.cs` — 8 test, 285 righe

> [D1] Verifica di TreeSHAP. La domanda non è "il codice gira" ma "i numeri sono quelli giusti", e per rispondere servono riferimenti indipendenti dall'implementazione: 1. RICOSTRUZIONE — la struttura estratta da ML.NET riproduce le predizioni del modello vero. Senza questo, ogni valore SHAP sarebbe spazzatura ben formattata. 2. EFFICIENZA — baseline + Σ contributi == predizione, la proprietà che rende sommabili le attribuzioni. 3. SHAPLEY PER FORZA BRUTA — su pochi fattori si enumerano tutti i 2^n sottoinsiemi e si applica la formula di Shapley: è il valore ESATTO, calcolato in modo completamente diverso. Se TreeSHAP coincide, l'algoritmo veloce è corretto. 4. FEATURE INERTE — una feature che il modello non usa deve ricevere esattamente zero.

`ExtractedEnsemble_ReproducesModelPredictions`, `Shap_SatisfiesEfficiency`, `Shap_MatchesBruteForceShapleyValues`, `UnusedFeature_GetsExactlyZeroAttribution`, `NonTreeModel_ReturnsNullInsteadOfThrowing`, `UntrainedModel_ReturnsNull`, `Summary_RanksTheDrivingFeaturesAboveTheNoiseFeature`, `ExplainRow_OrdersContributionsByAbsoluteImpact`

## `BacktestLeverageTests.cs` — 7 test, 213 righe

> Test della contabilita' a margine del motore: leva, liquidazione intrabar, funding e slippage. Con leva 1 e tutto a 0 il comportamento deve restare IDENTICO allo spot.

`Leverage1_MatchesLegacySpotAccounting`, `Leverage5_PnlIsFiveTimesNotionalReturn`, `Leverage10_Long_LiquidatedOnAdverseMove`, `Leverage10_Short_LiquidatedOnRally`, `StopLoss_PreventsLiquidation`, `Slippage_WorsensBothFills`, `Funding_ChargedProRata_EntersTradePnl`

## `ExcursionHorizonTests.cs` — 7 test, 160 righe

> R1.5 — Auto SL/TP da MAE/MFE sull'ORIZZONTE di detenzione, condizionato per regime di volatilità. Le escursioni a barra singola sottostimano il rischio di stop; qui ogni barra è un ingresso tenuto per H barre e MAE/MFE si accumulano. Verifica: i tre regimi sono popolati; il regime ad alta volatilità produce uno stop più largo di quello a bassa volatilità (il cuore del condizionamento); l'orizzonte più lungo non riduce l'escursione; il bracket adattivo ripiega sul complessivo quando il regime corrente è troppo sparso; input degenere ⇒ zeri; horizon non valido ⇒ eccezione.

`HorizonBracket_PopulatesAllThreeRegimeKeys`, `HighVolRegime_HasWiderStop_ThanLowVol`, `MultiBarHorizon_CapturesMoreRisk_ThanSingleBar`, `AdaptiveBracket_FallsBackToOverall_WhenRegimeSparse`, `ShortSide_ProducesPositiveBracket_OnDowntrend`, `DegenerateInput_ReturnsZeros`, `InvalidHorizon_Throws`

## `FactorCacheTests.cs` — 7 test, 124 righe

> Test della cache dei fattori (Fase 4): trasparenza (cache == ricalcolo, nessuno skew), hit/miss, invalidazione al cambio di parametri o di dati, e sfratto FIFO sotto capacità. È un memoizzatore puro: non deve mai cambiare il valore calcolato.

`GetOrCompute_MatchesDirectCompute`, `SecondCall_IsAHit_AndReturnsSameReference`, `DifferentParameters_MissAndRecompute`, `NewData_InvalidatesCache`, `DifferentSymbol_IsADifferentKey`, `RespectsMaxEntries_WithFifoEviction`, `EmptyCandles_ComputesWithoutCaching`

## `FleetReaderEvaluateTests.cs` — 7 test, 122 righe

> [AF2/F5] Il verdetto di un run come candidato di flotta. Il caso che ha originato il fix (2026-08-03, primo journal vuoto): un run con ZERO sopravvissuti non ha gambe nella raccomandazione, e il lettore derivava i trade/mese dalle gambe — quindi la fascia grigia, che vive per definizione nei run a zero sopravvissuti, non entrava MAI in coda.

`ZeroSurvivors_WithContoTradeRejection_IsGrey_WithItsOwnFrequency`, `GreyDsrBand_AlsoQualifies`, `MeritRejections_AreNotGrey`, `LosingGrey_IsNotGrey`, `BestGreyLeads_AndTheOthersAreCounted`, `Survivors_ArePass_WithTheThinnestLegFrequency`, `UnusableWindow_MeansNoCandidate`

## `ForexFactoryIngestorTests.cs` — 7 test, 87 righe

> Test di su un frammento HTML di fixture ricalcato dalla pagina reale (nessuna chiamata di rete — stesso principio di RssNewsSourceTests : solo il fetch HTTP è non deterministico/esterno, non il parsing).

`ParseCalendar_ExtractsAllRealEvents_SkippingDayBreakers`, `ParseCalendar_AllItemsTaggedAsEconomicCalendar`, `ParseCalendar_ExtractsTitleCurrencyAndImpact`, `ParseCalendar_MapsImpactIconClasses_ToHumanReadableLevels`, `ParseCalendar_CarriesDayForwardAcrossRowsWithoutDataDayDateline`, `ParseCalendar_EachEventHasAUniqueUrl_ForDedupe`, `ParseCalendar_EmptyHtml_ReturnsEmptyList`

## `LaneCountCoherenceProbeTests.cs` — 7 test, 152 righe

> [AF0] La sonda che confronta il numero di corsie del guscio con quello del motore remoto. La distinzione che questi test difendono: disallineamento (entrambi i numeri noti e diversi → allarme Critical) e ignoranza (motore muto o valore illeggibile → nessun allarme) sono esiti diversi. Un allarme costruito sull'ignoranza sarebbe l'ennesimo controllo che grida a prescindere — la classe di difetto opposta ma gemella di quello che rassicura a prescindere. Collezione TradingLanes: si configura lo static di processo, come in TradingLanesCountTests.

`CoherentFleet_NoAlarm`, `MismatchedFleet_OneCriticalNotification`, `UnreachableEngine_IsIgnorance_NotMismatch`, `KeyAbsentFromEngineConfig_MeansTheCodeDefault_AndThatIsARealMismatch`, `UnparseableValue_NoFalseAlarm`, `NumericJsonForm_AlsoParses`, `MissingNotifier_DoesNotThrowOnMismatch`

## `MetaLabelingAnalysisServiceTests.cs` — 7 test, 184 righe

> [C4, consumo] Test di INTEGRAZIONE della catena completa: strategia reale → segnali barra per barra → etichette triple-barrier → meta-modello out-of-fold → verdetto. È il livello che mancava: le classi erano coperte una per una, ma nessun test verificava che messe insieme facessero qualcosa di sensato — ed è esattamente ciò che la pagina Backtest invoca. I componenti sono REALI (nessun mock): `EmaCross` dal catalogo vero, i fattori alpha veri, il servizio indicatori vero. L'unica cosa sintetica sono le candele, perché serve conoscere la risposta giusta.

`FullChain_ProducesACoherentReportOnRealComponents`, `OnASeriesWithoutEdge_TheVerdictIsNotAnImprovement`, `TooFewCandles_AreRefusedWithAReadableReason`, `AStrategyThatBarelyTrades_IsRefusedInsteadOfMeasuredOnNothing`, `EmptySeries_DoesNotThrow`, `WorksWithARealCatalogStrategy`, `AnalysisIsDeterministic`

## `MlpReturnPredictorTests.cs` — 7 test, 140 righe

> Test di (cap. 17 ML4T in C# puro): apprendimento di relazioni lineari e NON lineari, determinismo a parità di seed, persistenza JSON e feature importance.

`Fit_LearnsLinearRelationship`, `Fit_LearnsNonlinearRelationship_BetterThanMean`, `Fit_SameSeed_IsDeterministic`, `SaveAndLoad_RoundTrip_ProducesSamePredictions`, `FeatureImportance_RanksInformativeFeatureHigher`, `Predict_BeforeFit_Throws`, `Predict_WrongFeatureCount_Throws`

## `NewsImpactAnalyzerTests.cs` — 7 test, 175 righe

> Test di su OHLCV sintetici con un pattern di impatto NOTO (candele costruite apposta perché il ritorno atteso a ogni orizzonte sia calcolabile a mano), così le asserzioni verificano il numero esatto, non solo "diverso da zero"/"non NaN".

`Analyze_ComputesExactReturn_ForKnownPriceStep`, `Analyze_NoPriceMovement_ReturnsExactlyZero_NotNaN`, `Analyze_NewsBeyondCandleRange_IsExcludedNotThrown`, `Analyze_GroupsByCategoryAndSource_Independently`, `Analyze_RetailSentimentCrossSource_SeparatesAgreeingFromDisagreeing`, `Analyze_RetailSentiment_RequiresBothSourcesInSameHour_ToCountAsMatch`, `Analyze_EmptyNews_ReturnsEmptyReport_NotThrown`

## `OrderFlowFactorsTests.cs` — 7 test, 147 righe

> [3.8b roadmap macchina-ricerca] Fattori order-flow sui campi klines recuperati da T0.3. La proprietà più importante: NULL dove i campi estesi mancano — un fattore che leggesse zero su una candela non reingerita produrrebbe un imbalance di -1 finto (tutto vendite) e il modello imparerebbe un artefatto della migrazione, non il mercato.

`TakerImbalance_IsCenteredAndBounded`, `TakerImbalance_RollingMean_AveragesTheWindow`, `MissingExtendedFields_YieldNull_NeverAFakeZero`, `AvgTradeSize_RelativeToItsOwnHistory`, `AvgTradeSize_ZeroTrades_YieldNull`, `Factory_ExposesAndRoundTrips_TheNewFactors`, `AntiLookAhead_TruncationInvariance`

## `PipelineSupervisorComparisonTests.cs` — 7 test, 244 righe

> Test della Fase C (secondo parere multi-provider) di : artifact SEPARATO con Kind proprio (i filtri di worker/pannello/test sull'advisory primaria non devono vederlo), best-effort dichiarato (un fallimento del provider di confronto non tocca mai l'advisory primaria), skip su provider coincidente/ignoto/senza chiave, default spento.

`ComparisonEnabled_WritesSecondArtifact_WithOwnKindAndProviderInStageName`, `ComparisonFailure_PrimaryAdvisorySurvives_NoComparisonArtifact`, `SameProviderAsActive_ComparisonSkipped`, `ComparisonDisabledByDefault_NoSecondCall`, `UnconfiguredComparisonProvider_Skipped`, `PrimaryErrorAdvisory_NoComparisonAttempted`, `WorkerAntiJoin_IgnoresComparisonArtifacts`

## `PortfolioShrinkageErcTests.cs` — 7 test, 131 righe

> E1 — potenziamenti portafoglio: covarianza Ledoit-Wolf (shrinkage verso μI, ben condizionata) e Equal Risk Contribution ESATTO (coordinate cyclical, tiene conto delle correlazioni). Verifica le proprietà matematiche degli stimatori in isolamento e che gli allocatori li usino.

`LedoitWolf_ShrinkageIntensity_InUnitInterval`, `LedoitWolf_ShrinksOffDiagonalsTowardZero`, `LedoitWolf_FewObservations_ShrinksMoreThanMany`, `Erc_EqualizesRiskContributions`, `Erc_CorrelatedPair_GetsLessThanInverseVolWould`, `Erc_UncorrelatedEqualVol_GivesEqualWeights`, `RiskParity_ErcMode_PrefersUncorrelatedAsset_OverInverseVol`

## `ReturnMatrixBuilderTests.cs` — 7 test, 138 righe

> è il punto in cui storici DISALLINEATI (simbolo quotato dopo, buchi di ingestione, candele sporche) diventano la matrice allineata che gli allocatori e la PCA richiedono: un errore qui sfalsa TUTTE le covarianze a valle senza sollevare eccezioni.

`InnerJoin_UsesOnlyCommonTimestamps_AndComputesReturnsFromClose`, `InputOrder_DoesNotMatter`, `NonPositiveCloses_AreDiscardedBeforeTheJoin`, `NoOverlap_ReturnsEmptyMatrix_NotException`, `SingleCommonBar_YieldsNoReturns`, `EmptySeries_ReturnsEmptyMatrix`, `DuplicateTimestamps_LastOneWins`

## `RssNewsSourceTests.cs` — 7 test, 119 righe

> Test di su un feed RSS 2.0 campione (nessuna chiamata di rete: il parsing è testato in isolamento, come raccomandato per l'ingestion — la parte realmente non deterministica/esterna è solo il fetch HTTP, non la logica di estrazione).

`ParseFeed_ExtractsAllItems`, `ParseFeed_ExtractsTitleSummaryUrl`, `ParseFeed_ExtractsPublishDate_AsUtc`, `ParseFeed_EmptyChannel_ReturnsEmptyList`, `NewsFeeds_KnownFeeds_AreAllHttpsUrls`, `NewsFeeds_IncludesFxStreetGeneralAndCentralBanksFeeds`, `ParseFeed_FxStreetPubDateFormat_ParsesCorrectly`

## `SeriesFreshnessTests.cs` — 7 test, 138 righe

> [B2] Il gate B2 chiedeva «7 giorni senza buchi nelle candele» e nessuno dei due strumenti che dovevano misurarlo sapeva vedere una serie che ha smesso di avanzare: lo stato di sync scriveva OK: 1 candele perché il cursore incrementale ri-chiedeva l'ultima candela nota e l'exchange gliela ridava, e l'audit di copertura misurava la densità della serie sul proprio intervallo — dove una serie ferma è densa al 100%. Il test che conta davvero è : riproduce la situazione reale trovata a database il 2026-07-28, che è la ragione per cui questa regola esiste.

`Il_caso_MKR_una_serie_ferma_da_mesi_che_si_dichiarava_sana`, `Il_caso_TON_ferma_da_meno_ma_ferma`, `Le_serie_vive_di_oggi_non_sono_ferme`, `La_tolleranza_e_esattamente_dove_e_scritta`, `Vuota_o_di_timeframe_ignoto_non_significa_fresca`, `Una_candela_nel_futuro_non_produce_ritardo_negativo`, `Il_conteggio_delle_candele_processate_non_puo_salvare_una_serie_ferma`

## `SupervisorAgentTests.cs` — 7 test, 136 righe

> Verifica gli agenti supervisori del ciclo di ri-applica. Punti chiave: il Logging approva sempre (delega alle metriche); il Claude usa un fake ILlmClient (nessuna rete) attraverso il guard condiviso — su assenza di API key, breaker aperto o errore ricade SUBITO su "approva" (un problema AI non blocca mai una sostituzione giustificata dai numeri — l'AI può solo porre un veto, mai forzare); il DelegatingSupervisorAgent instrada il provider per-chiamata (hot-swap).

`Logging_AlwaysApproves`, `Claude_NotConfigured_FallsBackToApprove`, `Claude_OnError_FallsBackToApprove`, `Claude_VetoIsHonored`, `Parse_ToleratesSurroundingText_DefaultsApproveTrueWhenOmitted`, `Claude_WithOpenBreaker_ApprovesImmediately_WithoutCallingTheLlm`, `Delegating_SwitchesProviderPerCall_WithoutRestart`

## `TradingCommandHandlersTests.cs` — 7 test, 142 righe

> Test dei 7 comandi (Fase 1, PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.6 passo 3): ciascuno deve risolvere il motore della corsia GIUSTA (LaneId sulla richiesta) e passargli gli argomenti esatti, senza alterarli. `CloseAllPositionsCommand` non esiste: il suo unico chiamante reale è `LanePromoter`, esplicitamente escluso da Mediator (PRD §4.2) — nessun caller Blazor da migrare, quindi nessun comando da creare (sarebbe superficie morta).

`StartLaneCommand_ResolvesRequestedLane_AndPassesMode`, `StopLaneCommand_ResolvesRequestedLane`, `EmergencyStopCommand_PassesReasonThrough`, `ClosePositionCommand_PassesPositionIdThrough`, `SetStopLossTakeProfitCommand_PassesAllValuesThrough`, `ConfirmOrderCommand_PassesOrderIdAndUserIdThrough`, `RejectOrderCommand_PassesOrderIdAndUserIdThrough`

## `TradingEngineReconcileTests.cs` — 7 test, 400 righe

> Regressione dei bug CRITICI C2/H2 (audit 2026-07): dopo un esito di rete INCERTO il motore controllava solo GetOpenOrdersAsync — ma un MARKET riempito durante il blip NON è tra gli ordini aperti, quindi veniva scambiato per "mai piazzato": posizione reale non tracciata (nessuno stop la gestisce) + ordine DUPLICATO alla candela successiva (apertura), oppure posizione locale aperta per sempre con retry in oversell (chiusura). Il fix interroga lo STATO per clientOrderId ( ) e adotta il fill reale. I client sono scriptati a CODE: ogni lookup/piazzamento consuma il prossimo esito previsto; un accesso oltre lo script fa fallire il test (fail loudly, nessun default silenzioso).

`OpenUncertain_ThenLookupFilled_AdoptsRealFill`, `OpenUncertain_NotFound_SafeReject_NextCandleOpensExactlyOnePosition`, `OpenUncertain_StillOpenOnExchange_CancelledThenFillAdopted`, `OpenUncertain_LookupAlwaysUncertain_BestEffortCancel_AuditCritical_NoPosition`, `CloseUncertain_LookupFilled_FinalizesWithRealExitPrice_AndRefundsCapital`, `CloseUncertain_NotFound_PositionStays_NextCandleRetriesAndCloses`, `FuturesOpenUncertain_LookupFilled_AdoptsRealFill_WithIsolatedMargin`

## `BacktestCostAccountingTests.cs` — 6 test, 177 righe

> [R2] Test della contabilità dei costi nel backtest. Il vincolo più importante è che questa contabilità sia PURAMENTE DIAGNOSTICA: fee e slippage erano già dentro il PnL prima di R2 (le prime dentro il Portfolio, il secondo dentro i prezzi di fill), quindi esporli non deve spostare di un centesimo nessun risultato preesistente. Un test che lo verifica vale più di quelli sui totali: se domani qualcuno "sistemasse" la contabilità sottraendo di nuovo i costi, il PnL verrebbe conteggiato due volte in silenzio. Il resto misura il CostDragPercent , che è la grandezza su cui R2 decide se un timeframe veloce sia operabile o no.

`CostAccounting_DoesNotChangePnl`, `Fees_AreAccounted_AndMatchTheCapitalLost`, `Slippage_IsAccountedSeparatelyFromFees`, `CostDrag_GrowsWithTurnover`, `GrossReturn_ExceedsNetReturn_ByExactlyTheCostDrag`, `NoTrades_LeavesCostsAtZero_WithoutDividingByZero`

## `BayesianOptimizerTests.cs` — 6 test, 105 righe

> Test dell'ottimizzazione bayesiana (Fase 6): il surrogato Gaussian Process + Expected Improvement converge all'ottimo di funzioni analitiche note in poche valutazioni, rispetta i confini/interi dello spazio, ed è deterministico a parità di seme (requisito non negoziabile della piattaforma).

`Maximize_1D_ConvergesNearOptimum`, `Maximize_2D_ConvergesNearOptimum`, `Maximize_IsDeterministic_ForSameSeed`, `Maximize_BeatsPureRandomSearch_OnSameBudget`, `SuggestNext_StaysWithinBounds_AndSnapsIntegers`, `SuggestNext_EmptyHistory_ReturnsInBoundsPoint`

## `CarryBacktestEngineTests.cs` — 6 test, 108 righe

> [E3 roadmap profitto-intraday] Motore di backtest del carry delta-neutro. Questi test fissano: (a) a funding costante alto la strategia entra e incassa il funding meno i costi delle due gambe; (b) l'A/B a funding zero: netto = −costi (nessun income, si pagano solo i fill); (c) l'isteresi enter&gt;exit è obbligatoria; (d) determinismo.

`ConstantHighFunding_EntersAndCollectsFundingMinusTwoLegCosts`, `ZeroFunding_NeverEnters_NetIsZero`, `NegativeFunding_ShortWouldPay_StaysOut`, `FundingDropsMidway_ExitsAndBanksTheEpisode`, `ExitThresholdNotBelowEnter_IsRejected`, `SameInput_SameResult_Deterministic`

## `CyclicalAnalyzerTests.cs` — 6 test, 155 righe

> Test dell'analisi ciclica (Activity Factor, bias orario/settimanale, stagionalita' - cap. 5).

`ActivityFactor_AveragesPerHour_AndNormalizes`, `HourlyPriceBias_ComputesAverageBody_AndConcordance`, `CombineHourlyBias_WeightsLongPeriodMore`, `DayOfWeekBias_SeparatesIntradayAndOvernight`, `Seasonality_CumulativeCurve_IsRunningSumOfAverages`, `TestSeasonalWindow_CountsYearsAndDirection`

## `EffectiveTrialsTests.cs` — 6 test, 101 righe

> R1.4 — numero EFFETTIVO di tentativi per il Deflated Sharpe: i tentativi con rendimenti correlati (griglia fitta di parametri, simboli gemelli) vengono clusterizzati e contano una volta sola, così la soglia SR* non sovrastima il test multiplo. Verifica: serie indipendenti ⇒ N effettivo = N nominale; gruppi correlati ⇒ N effettivo = numero di gruppi; soglia 1 disattiva; e la conseguenza a valle (DSR con N effettivo &lt; DSR con N nominale gonfiato) — il gate diventa correttamente meno severo quando i tentativi sono ridondanti.

`IndependentSeries_EffectiveEqualsNominal`, `ThreeCorrelatedGroups_CollapseToThree`, `ThresholdOne_DisablesCollapsing`, `DegenerateAndShortSeries_StayDistinct`, `SingleOrEmpty_ReturnsCount`, `EffectiveTrials_RaisesDeflatedSharpe_VsInflatedNominal`

## `ExchangeCredentialReaderTests.cs` — 6 test, 226 righe

> Bug B2 (docs/TEST-UI-2026-07-18.md): una riga di ExchangeCredentials cifrata con una master key DIVERSA da quella del processo corrente abbatteva l'intera query EF (il converter decifra dentro la materializzazione → AuthenticationTagMismatchException) e con essa la pagina /settings/exchanges. legge il ciphertext grezzo (vista keyless) e decifra RIGA PER RIGA: qui si verifica che la riga "straniera" venga flaggata senza rompere le altre, che non trapeli mai plaintext parziale, e che il percorso trading preferisca una riga decifrabile quando esiste. Le righe sono inserite via SQL grezzo con ciphertext PRE-calcolato (mai via converter EF): il converter cattura l'IEncryptionService del PRIMO model building e renderebbe il seed dipendente dall'ordine di esecuzione della suite.

`LoadForUser_RowFromForeignKey_IsFlagged_WithoutBreakingTheOthers`, `LoadForUser_OnlyPassphraseFromForeignKey_FlagsTheWholeRow_NoPartialPlaintext`, `LoadForUser_GarbageNonBase64Row_IsFlagged_NotThrown`, `FindForTrading_PrefersTheDecryptableRow_OverAnOlderUndecryptableOne`, `FindForTrading_OnlyUndecryptableRows_ReturnsTheFlaggedRow`, `FindForTrading_NoMatchingRows_ReturnsNull`

## `GatePowerAnalyzerTests.cs` — 6 test, 156 righe

> [2026-07-28] Potenza del gate anti-overfitting: qual e' l'edge piu' piccolo che la piattaforma potrebbe CONFERMARE, se fosse vero. Nasce dalla domanda del proprietario — «di candidati se ne trovano ma non consolidano mai» — e serve a separare due diagnosi opposte: non c'e' edge (e i gate hanno ragione), oppure il gate non ha la potenza per confermare un edge della grandezza che esiste (e «zero sopravvissuti» e' un'informazione sullo strumento, non sul mercato). Il test che vale piu' di tutti e' : la teoria deve riprodurre i valori di DSR realmente registrati dalla pipeline sui candidati bocciati. Se non li riproducesse, la tabella degli anni sarebbe un esercizio di aritmetica scollegato dalla piattaforma di cui pretende di parlare.

`Lo_sharpe_minimo_riporta_il_DSR_esattamente_alla_soglia`, `Piu_tentativi_alzano_lasticella`, `Un_edge_enorme_si_conferma_in_poco_tempo`, `Gli_anni_necessari_seguono_la_forma_attesa`, `Il_modello_riproduce_i_DSR_osservati_sui_candidati_veri`, `Con_quattro_mesi_di_holdout_nessun_edge_realistico_e_confermabile`

## `HarRvForecasterTests.cs` — 6 test, 168 righe

> Test di e (C3): causalità (invariante di troncamento), warm-up, capacità di battere il naive su un processo di varianza persistente, e la contabilità della RV giornaliera dai 5m (giorni bucati scartati).

`ForecastSeries_IsAntiLookAhead_TruncationDoesNotChangePastValues`, `ForecastSeries_WarmupIsNull_ThenEmits`, `ForecastSeries_BeatsNaive_OnPersistentVarianceProcess`, `ForecastSeries_InvalidHorizon_Throws`, `ForecastSeries_LogVariant_IsCausal_Positive_AndRobustToSpikes`, `DailyFromIntraday_ComputesSumOfSquaredLogReturns_AndSkipsSparseDays`

## `HostHeartbeatTests.cs` — 6 test, 142 righe

> Il giro completo su Postgres vero: il writer fa upsert della SOLA riga del proprio ruolo, e la riga si aggiorna invece di moltiplicarsi.

`NoRow_IsUnknown_NotStale`, `Staleness_IsAThreshold_NotAMood`, `Transitions_NotifyOncePerDirection`, `StaleAtFirstObservation_StillNotifies`, `UnknownForever_NeverNotifies`, `Writer_UpsertsItsOwnRow_NeverDuplicates`

## `IncrementalFactorFilterTests.cs` — 6 test, 266 righe

> [2.6 PRD-RISANAMENTO] Il ponte che rende raggiungibile l'IncrementalIcGate dalla selezione fattori (C-03/G-16: «il gate c'è, chi lo chiama no»). Il metodo è quello dell'edge piantato (docs/STANDARD-VERIFICA.md): si costruisce una serie dove la ridondanza è VERA per costruzione — il rendimento dipende da due componenti indipendenti x e y, un fattore legge x, il suo echo rilegge x, un terzo legge y — e si pretende che il filtro tenga x e y e scarti l'echo.

`EchoDelCapostipite_Scartato_IndipendenteTenuto`, `ITenutiDiventanoControlli_UnEchoDelSecondoTenuto_VieneScartato`, `FattoreSingolo_TenutoSenzaVerdetto_EListaVuotaResiste`, `Deterministico_StessiInput_StessoVerdetto`, `Stage_GateSpentoDiDefault_SelezionaAncheIlRidondante`, `Stage_GateAcceso_ScartaIlRidondante_ELoDiceNeiLog`

## `KalmanPairsSpreadAnalyzerTests.cs` — 6 test, 203 righe

> Test di (C2): stesse invarianti della rolling OLS (anti-look-ahead, warm-up, recupero del β vero su coppia sintetica) più la proprietà per cui il filtro esiste — su un β che DERIVA nel tempo, l'errore di inseguimento del Kalman deve essere minore di quello della rolling OLS (che β lo vede solo attraverso una finestra in ritardo).

`Analyze_IsAntiLookAhead_TruncationDoesNotChangePastValues`, `Analyze_HedgeRatio_ConvergesToTrueBeta_OnCointegratedPair`, `Analyze_AdaptsToBetaRegimeChange_FasterThanRollingOls`, `Analyze_WarmupBeforeWindow_IsNull_AndFirstEmissionMatchesOls`, `Analyze_InvalidDelta_Throws`, `Engine_DefaultIsKalman_AndRollingOlsRemainsSelectable`

## `KeywordSentimentScorerTests.cs` — 6 test, 53 righe

> Test di : segno e range del punteggio, testo neutro, word-boundary.

`Score_PositiveHeadline_IsPositive`, `Score_NegativeHeadline_IsNegative`, `Score_NeutralHeadline_IsZero`, `Score_MixedHeadline_IsBetweenBounds`, `Score_IsAlwaysWithinRange`, `Score_UsesSummaryToo`

## `MarginMathTests.cs` — 6 test, 59 righe

> MarginMath è condivisa tra BacktestEngine e TradingEngine: se questa formula è sbagliata, sia il backtest sia il monitoraggio live del rischio di liquidazione sarebbero sbagliati nello stesso modo. Valori attesi calcolati indipendentemente a mano.

`LiquidationPrice_Long_10x_MatchesHandComputation`, `LiquidationPrice_Short_10x_MatchesHandComputation`, `LiquidationPrice_HigherLeverage_IsCloserToEntry`, `LiquidationPrice_ZeroQuantity_ReturnsZero`, `LiquidationDistanceFraction_10x_MatchesHandComputation`, `LiquidationDistanceFraction_NonPositiveLeverage_ReturnsMaxCaution`

## `MasterKeyGuardTests.cs` — 6 test, 230 righe

> Gate LIVE del motore: vedi per il razionale.

`PlaceholderKey_IsDetected`, `RealBase64Key_IsNotFlagged`, `ArbitraryPassphrase_IsNotFlagged`, `Live_WithPlaceholderKey_IsBlockedBeforeAnythingElse`, `Live_WithRealKey_PassesTheGate`, `Paper_WithPlaceholderKey_StartsNormally`

## `MasterKeyProbeTests.cs` — 6 test, 150 righe

> Fase 3-C2 (PRD Autonomia §6): l'avvio con la master key sbagliata deve diventare RUMOROSO (LogCritical + notifica + stato per il banner UI) invece di morire in silenzio sul percorso credenziali finché una pagina non va in 500.

`UnreadableCredentials_SetResult_AndNotifyCritical`, `AllReadable_NoNotification`, `NoCredentialsAtAll_IsHealthy`, `RefreshAfterCredentialChange_ClearsAnAlarmThatIsNoLongerTrue`, `RefreshAfterCredentialChange_RaisesAnAlarmThatHasJustBecomeTrue`, `RefreshAfterCredentialChange_NeverThrows_AndKeepsThePreviousResult`

## `NullTwinGeneratorTests.cs` — 6 test, 145 righe

> [I2 roadmap frontiere-profitto] Il gemello sintetico NULLO: block bootstrap dei rendimenti + segno i.i.d. per barra. Questi test fissano il contratto del nullo: (a) stessa "anagrafica" della serie reale (lunghezza, timestamp, prima candela); (b) i moduli dei rendimenti vengono dalla popolazione reale (il clustering di \|r\| è ereditato, non inventato); (c) la struttura DIREZIONALE muore — un drift fortissimo nel reale non sopravvive nel gemello; (d) determinismo a parità di seme; (e) candele sempre valide (High/Low coerenti, mai prezzi ≤ 0).

`Twin_KeepsLengthTimestampsFirstCandleAndIdentity`, `Twin_IsDeterministicPerSeed_AndDiffersAcrossSeeds`, `Twin_AbsoluteReturns_ComeFromTheRealPopulation`, `Twin_KillsDirectionalStructure_StrongDriftDoesNotSurvive`, `Twin_CandlesAreAlwaysValid_AndVolumesComeFromTheRealSeries`, `Generate_RejectsDegenerateInput`

## `PerformanceControlTests.cs` — 6 test, 98 righe

> Test del Performance/Equity Control (Trombetta cap. 8).

`WindowProfitControl_InhibitsAfterLosingWindow_ReactivatesAfterRecovery`, `WindowProfitControl_AllWinners_ExecutesEverything`, `EquityMovingAverageControl_StopsInDrawdown`, `Result_Reports_AreConsistentWithCurves`, `NestedReports_DrawdownConsistentWithTopLevel`, `EmptyTrades_NoThrow`

## `PipelineStopTargetVariantTests.cs` — 6 test, 68 righe

> Verifica il parsing/applicazione delle varianti stop+target della prova di robustezza, esteso per includere il TAKE PROFIT e le combinazioni SL+TP ("SL2_TP4"), e l'auto-inserimento delle varianti TP per le cacce che elencano solo stop (autonomia).

`ApplyVariant_Combined_SetsStopAndTakeProfit`, `ApplyVariant_TakeProfitOnly_SetsOnlyTakeProfit`, `ApplyVariant_TrailingPlusTakeProfit_SetsBoth`, `ApplyVariant_Base_SetsNothing`, `EnsureTakeProfitVariants_AddsTpGridWhenAbsent`, `EnsureTakeProfitVariants_LeavesExplicitTpListUntouched`

## `ProtectiveExitShadowTests.cs` — 6 test, 352 righe

> [B3, sentinella] Con le uscite protettive NON guidate dai tick, il tick OSSERVA: registra che avrebbe fatto scattare un'uscita, e quando il percorso a candele la fa scattare davvero ne nasce un confronto. Serve a vedere il caso singolo che il replay offline non poteva vedere — un crollo con gap — non a produrre una media, che su tre corsie richiederebbe anni. Il test che conta più di tutti è : la sentinella deve essere INERTE. Se osservando cambiasse anche solo il best-since-entry del trailing, il feed avrebbe acquisito potere sulle uscite dalla porta di servizio — senza che nessun toggle lo dica e senza che nessuno se ne accorga, perché l'effetto si vedrebbe solo come uno stop che scatta "un po' prima" del previsto.

`Il_tick_osserva_e_non_tocca_nulla`, `Il_confronto_nasce_quando_la_candela_chiude_davvero`, `Quando_il_ritardo_conviene_il_segno_e_negativo`, `Una_raffica_di_tick_produce_un_solo_confronto`, `Con_le_uscite_guidate_dai_tick_il_comportamento_e_quello_di_prima`, `La_sentinella_allerta_solo_sopra_soglia_e_solo_a_sfavore`

## `RemoteTradingEngineClientTests.cs` — 6 test, 216 righe

> Test di (Fase 2b microservizi), sui due aspetti che NON passano da gRPC (quello Ã¨ coperto da ): 1. Le due letture di ordini che bypassano il servizio e interrogano Postgres direttamente, confrontate CONTRO IL MOTORE VERO sullo stesso database. Nate come prova dell'affermazione "identiche riga per riga" quando il client portava una COPIA delle query; oggi entrambi i lati compongono da TradingOrderQueries e la deriva Ã¨ impossibile per costruzione â€” il confronto resta come cintura: fallirebbe se qualcuno reintroducesse una query locale scavalcando l'helper. (Nota onesta sul suo limite, ed Ã¨ parte del perchÃ© l'helper esiste: il confronto vede solo le dimensioni presenti nei dati seminati â€” un filtro aggiunto su una colonna che qui non varia produrrebbe risultati identici comunque.) 2.…

`GetOrderHistoryAsync_MatchesTheRealEngine_OnTheSameDatabase`, `GetOrderHistoryAsync_HonoursTheFromFilter_LikeTheRealEngine`, `GetPendingOrdersAsync_MatchesTheRealEngine_OnlyLivePending`, `ProcessCandleAsync_Throws_BecauseTheWorkerLivesInTheRemoteService`, `ProcessDueExecutionSlicesAsync_Throws_BecauseTheWorkerLivesInTheRemoteService`, `LaneId_IsExposed_ForKeyedResolution`

## `RollingPairsSpreadAnalyzerTests.cs` — 6 test, 119 righe

> Test di : anti-look-ahead (invariante di troncamento, come per gli IAlphaFactor), recupero approssimato dell'hedge ratio su una coppia sintetica cointegrata, e struttura del warm-up. Come in , le serie sintetiche sono costruite sui LOG (log Y = α + β·log X + rumore): è la specificazione che l'analizzatore stima, e le due devono combaciare o il backtest negozierebbe uno spread diverso da quello dichiarato cointegrato.

`Analyze_IsAntiLookAhead_TruncationDoesNotChangePastValues`, `Analyze_HedgeRatio_ApproximatesTrueBeta_OnCointegratedPair`, `Analyze_WarmupBeforeLookbackWindow_IsNull`, `Analyze_ZScore_NullOnlyDuringCombinedWarmup`, `Analyze_MismatchedLengths_Throws`, `Analyze_InvalidLookbackWindow_Throws`

## `SentimentAlphaFactorTests.cs` — 6 test, 132 righe

> Test di : media rolling delle notizie nella finestra, null in assenza di notizie, filtro per simbolo, e l'invariante anti-look-ahead (stesso contratto degli altri IAlphaFactor — verificato per troncamento).

`Compute_AveragesNewsWithinLookbackWindow`, `Compute_NoNewsInWindow_IsNull`, `Compute_NewsOutsideLookbackWindow_IsExcluded`, `Compute_FiltersBySymbol_WhenSpecified`, `Compute_IsAntiLookAhead_TruncationDoesNotChangePastValues`, `Compute_ReturnsSeriesAlignedToInputLength`

## `SeriesFreshnessWatchWorkerTests.cs` — 6 test, 196 righe

> [E7] La guardia di freschezza deve accorgersi DA SOLA di una serie che ha smesso di avanzare — il caso MKR/USDT, ferma dieci mesi con «OK: 1 candele» a ogni giro — e dirlo UNA volta per transizione, non una per giro. Il complemento (livello 2 dello standard): su serie sane non deve inventare nulla.

`Tick_SerieFerma_NotificaUnaVoltaSola`, `Tick_SerieSana_NessunAllarme`, `Tick_SerieDisabilitata_NonSiGiudica`, `Tick_SerieCheRiprende_RiarmaLAllarme`, `Tick_SerieAbilitataSenzaCandele_EFerma`, `Tick_PiuSerieFermeInsieme_UnaNotificaAggregata`

## `TreeReturnPredictorTests.cs` — 6 test, 137 righe

> Test di e : apprendono una relazione non lineare che il modello lineare non può catturare, e la persistenza (Save/Load) funziona tramite la stessa base condivisa.

`RandomForest_LearnsNonLinearRelationship_BetterThanChance`, `GradientBoosting_LearnsNonLinearRelationship_BetterThanChance`, `RandomForest_InvalidHyperparameters_Throw`, `GradientBoosting_InvalidHyperparameters_Throw`, `RandomForest_SaveAndLoad_RoundTrip_ProducesSamePredictions`, `GradientBoosting_SaveAndLoad_RoundTrip_ProducesSamePredictions`

## `AltDataSyncServiceTests.cs` — 5 test, 166 righe

> Test di : inserimento con classificazione/scoring automatici, deduplica fra sync successive, e resilienza a una fonte che lancia un'eccezione (non deve far fallire l'intera sync — stesso principio di MarketDataSyncService ).

`SyncAllAsync_InsertsNewItems_WithClassificationAndSentiment`, `SyncAllAsync_SecondRun_DoesNotDuplicate`, `SyncAllAsync_OneSourceThrows_OthersStillSynced`, `SyncAllAsync_ScorerThrowsOnOneItem_ItemSkippedAndRetriedNextRun_OthersSaved`, `SyncAllAsync_ItemsWithoutUrl_DedupeByTitle`

## `AttentionReturnPredictorTests.cs` — 5 test, 154 righe

> Test dell' (§1.4, attention in C# puro). Il test chiave è di CORRETTEZZA del backprop manuale: la feature predittiva sta sul timestep più VECCHIO della finestra, mentre il readout è sull'ultimo timestep — solo un'attention che instrada correttamente l'informazione (e i cui gradienti sono giusti) può impararlo. Più: round-trip Save/Load e determinismo.

`Learns_TemporalPattern_FromOldestTimestep`, `SaveLoad_RoundTrip_ReproducesPredictions`, `IsDeterministic_ForSameSeed`, `SequenceWindowing_BuildsCorrectLayout`, `SequenceWindowing_SkipsWindowsAcrossTimeGap`

## `BinanceDumpDownloaderTests.cs` — 5 test, 141 righe

> [D3] Scarico dei dump storici. Nessun test qui tocca la rete: si verificano gli URL (una lettera sbagliata nel percorso darebbe 404 su tutto e sembrerebbe "dato non disponibile"), la verifica del checksum e il comportamento sul giorno mancante.

`TheUrlsFollowThePublishedLayout`, `AMissingDay_IsNotAnError_ButIsCounted`, `ADownloadedFileIsCached_TheSecondCallDoesNotTouchTheNetwork`, `TheCsvInsideTheZipIsReadable_AndTheArchiveIsReleased`, `ChecksumVerification_SpotsATruncatedFile`

## `BotPageRenderTests.cs` — 5 test, 170 righe

> [R3] Rendering della Modalità Semplice ( /bot ). Due cose vanno viste sullo SCHERMO, non solo nei servizi: - il costo annuo implicito del profilo, che è la lezione di R2 tradotta per chi non conosce il dominio: se sparisse dalla pagina, l'utente sceglierebbe la frequenza operativa senza sapere che la sta pagando; - l'assenza del pulsante di avvio quando non c'è nulla da far girare, invece di un pulsante che accende un motore inerte.

`Bot_ShowsTheThreeProfiles_AndTheirImpliedAnnualCost`, `Bot_WithoutStrategies_OffersResearch_NotAStartButton`, `Bot_WithStrategies_OffersStart_AndSaysItIsSimulationOnly`, `Bot_WarnsWhenLaneTimeframeDivergesFromTheProfile`, `Bot_NoScalpingProfileIsOfferedOnScreen`

## `FeatureDriftWorkerPersistenceTests.cs` — 5 test, 208 righe

> [U4] Prima di questa persistenza gli esiti del drift vivevano SOLO nei log: la UI non poteva mostrare né l'ultimo esito né lo storico, e "nessuna riga" non distingueva "tutto pulito" da "il worker non gira". Contratti: una riga per modello per tick ANCHE se pulito, top-feature in JSON, flag ChampionRetired coerente col registry, prune oltre la retention.

`Tick_CleanModel_PersistsOneRowWithZeroDrift`, `Tick_ChampionInAlert_PersistsRowWithRetireFlagAndTopFeatures`, `Tick_TwoTicks_TwoRowsPerModel_HistoryAccumulates`, `Tick_PrunesRowsOlderThanRetention`, `BuildTopFeaturesJson_OrdersBySeverityAndCapsAtFive`

## `GapLapAnalyzerTests.cs` — 5 test, 118 righe

> Test dell'analisi Gap/Lap (Trombetta cap. 4).

`GapUp_Refilled_Deep_Pos_ClassifiedCorrectly`, `GapDown_NotRefilled_Neg_ClassifiedCorrectly`, `LapUp_And_LapDown_UseCloseAsReference`, `ContinuousMarket_OpenEqualsPrevClose_NoEvents`, `EmptyOrSingleBar_NoThrow`

## `LaneExecutionLeaseTests.cs` — 5 test, 86 righe

> [B0 PRD core-caldo] Il lease di esecuzione per corsia su Postgres VERO: "mai due esecutori sulla stessa corsia" deve essere applicato dal database, non dalla disciplina di deploy. Ogni factory qui simula un PROCESSO distinto (connessione propria, senza pool — il pool terrebbe vivo il lock dopo la Dispose, ed è esattamente il bug che Pooling=false previene).

`StessaCorsia_IlSecondoContendenteVieneRespinto`, `CorsieDiverse_ConvivonoSenzaContesa`, `IlRilascio_RendeLaCorsiaRiacquisibile`, `IsAlive_VeroFinchéLaSessioneVive`, `DatabaseDiversi_NonSiVedono`

## `LanePromotionFlattenTests.cs` — 5 test, 275 righe

> [M2] Promozione/retrocessione di corsia senza mescolare i mondi: flatten PRIMA del cambio modalità (senza emergency stop), discriminatore con purge delle righe di un'altra modalità al load, e i confini del (ordine delle chiamate, Live sempre vietato).

`CloseAll_ClosesEverything_WithoutEmergencyStop`, `CloseAll_NoPositions_NoAuditNoise`, `EnsureLoaded_PurgesPositionsFromOtherMode_WithAudit`, `Promoter_FlattensBeforeStopAndRestart_InOrder`, `Promoter_ToLive_StillForbidden_NoEngineCalls`

## `LaneRiskProfileEndToEndTests.cs` — 5 test, 221 righe

> [R3] Verifica che il profilo di rischio arrivi DAVVERO fino alle decisioni del motore. I test di RiskProfileTests provano che il profilo compone le soglie giuste; questi provano che quelle soglie governano il comportamento reale della corsia. È la differenza fra "il calcolo è corretto" e "il calcolo è collegato a qualcosa" — e la seconda è quella che fallisce silenziosamente quando il cablaggio si rompe.

`StartingALane_ActivatesItsRiskProfile`, `LaneWithoutProfile_KeepsGlobalThresholds`, `ProfileGovernsPositionSize_OfRealOrders`, `ProfileTurnoverCap_ThrottlesNewEntries`, `UnknownProfileName_FallsBackToGlobal_WithoutBlockingTheLane`

## `LeverageAdvisorTests.cs` — 5 test, 83 righe

> Test del consulente per la leva (bootstrap con pavimento di liquidazione).

`Advise_TooFewTrades_Warns`, `Advise_RiskGrowsWithLeverage`, `Advise_RecommendationRespectsHalvingTolerance`, `Advise_NegativeEdge_RecommendsMinimumLeverage`, `Advise_Deterministic_WithSeed`

## `MlComparisonClientTests.cs` — 5 test, 107 righe

> Prova di sicurezza centrale della Fase 2a: il confronto dual-read col servizio ml remoto è PURAMENTE osservativo. Qualunque risposta (match/mismatch), timeout o errore del remoto deve essere assorbito — MAI un'eccezione che risalga verso il ciclo di trading — e registrato con l'esito corretto sulla metrica procione.ml.comparisons .

`Compare_Match_RecordsMatch_AndDoesNotThrow`, `Compare_Divergence_RecordsMismatch_AndDoesNotThrow`, `Compare_DeadlineExceeded_RecordsTimeout_AndDoesNotThrow`, `Compare_RemoteError_RecordsError_AndDoesNotThrow`, `Compare_UnexpectedException_IsAbsorbed_AsError`

## `MlStrategyTests.cs` — 5 test, 167 righe

> Test end-to-end della Fase A: dataset dai fattori -&gt; addestramento -&gt; -&gt; backtest reale via . Chiude l'anello "modello addestrabile e back-testabile" descritto nella roadmap (§3.8).

`EvaluateSignal_AboveLongThreshold_ReturnsLong`, `EvaluateSignal_BelowShortThreshold_ReturnsShort`, `EvaluateSignal_DuringWarmup_ReturnsHold`, `InitializeAsync_UnfittedPredictor_Throws`, `EndToEnd_TrainedPredictor_ProducesTradesThroughRealBacktestEngine`

## `OptimizationStatisticsTests.cs` — 5 test, 63 righe

> Test deterministici del calcolo Sharpe (il punto più delicato dell'ottimizzazione).

`PeriodsPerYear_IsCorrect`, `Sharpe_KnownEquity_MatchesHandComputed`, `Sharpe_ConstantReturns_IsZero_NoDivideByZero`, `Sharpe_TooFewPoints_IsZero`, `Sharpe_PositiveTrend_IsPositive`

## `PageConfigStoreTests.cs` — 5 test, 114 righe

> Test del PageConfigStore (preset di configurazione pagina + "ultima configurazione usata"): round-trip salva/carica, upsert sullo stesso nome, isolamento per utente/pagina, e lista dei soli preset con nome (l'ultima configurazione resta fuori dal dropdown).

`SaveLoad_RoundTripsJson`, `Save_SameName_Upserts`, `Load_IsIsolated_PerUserAndPage`, `ListNames_ExcludesLastUsed_AndSorts`, `Delete_RemovesOnlyThatPreset`

## `PipelineAutoResumeTests.cs` — 5 test, 211 righe

> Fase 3-C1 (PRD Autonomia §6): i run "Paused" con trigger AUTOMATICO riprendono da soli — l'evidenza della sessione 2026-07-18 è un run interrotto dallo spegnimento rimasto Paused tutto il giorno (unico chiamante di ResumeRunAsync = il bottone in /pipeline). I Paused MANUALI restano manuali; budget di tentativi con marker persistenti; a esaurimento notifica.

`PausedScheduledRun_IsResumed_AttemptRecorded`, `PausedManualRun_IsNeverTouched`, `LiveConfigRun_IsNeverTouched`, `SlotBusy_NoAttemptConsumed`, `ResumeFailure_ConsumesAttempt_ThenGivesUpAndNotifiesOnce`

## `PipelineFunnelMetricsTests.cs` — 5 test, 110 righe

> [2026-07-28] L'IMBUTO, cioe' dove muoiono i candidati. Fino a oggi la pipeline registrava solo "Candidates" e "Survivors": 32 run, 2.049 candidati, zero sopravvissuti, e nessun modo di sapere quale gate li stesse uccidendo. Sono tre diagnosi opposte — un candidato bocciato per «solo 8 trade in holdout» non dice niente sul mercato, dice che la finestra e' troppo corta per la sua frequenza; uno bocciato con Sharpe -1,9 dice che perde davvero; uno bocciato dal DSR dice che guadagna ma non e' distinguibile dal caso. Confonderle era il motivo per cui la domanda «perche' non consolida mai» e' rimasta aperta per settimane. Il raggruppamento in CLASSI e' la parte che puo' rompersi in silenzio: i motivi contengono il valore misurato («DSR 0,677 ≤ 0,95»), quindi contarli per stringa darebbe una cat…

`I_motivi_con_valori_diversi_finiscono_nella_stessa_classe`, `I_sopravvissuti_non_sono_scarti`, `Un_guasto_non_si_confonde_con_un_verdetto`, `Un_motivo_sconosciuto_resta_visibile`, `Senza_scarti_non_si_producono_righe`

## `PipelineRangeValidationTests.cs` — 5 test, 60 righe

> [D-03, Fase 1 PRD-RISANAMENTO] L'invariante selezione/holdout come politica unica ( ): prima viveva SOLO nel salvataggio della UI, e una configurazione nata altrove (pre-controllo, SQL a mano, tool) girava con l'holdout sovrapposto alla selezione — ogni numero "out-of-sample" contaminato in silenzio. Ora la stessa funzione blocca il form E l'avvio del run.

`ValidRanges_PassValidation`, `HoldoutTouchingSelectionEnd_IsAllowed`, `OverlappingHoldout_IsRejected`, `InvertedOrEmptyWindows_AreRejected`, `DefaultInstance_IsRejected_NotSilentlyAccepted`

## `RegimeRouterEngineTests.cs` — 5 test, 234 righe

> [Fase 4] Il router visto dal motore, non in isolamento. Copre il caso che una prima stesura di questo codice sbagliava: filtrare saltando l'intero giro della strategia quando NON c'era una posizione aperta sembrava equivalente, ma lasciava passare il caso peggiore — con una posizione aperta il filtro non veniva nemmeno consultato, e su un segnale di inversione il motore chiudeva e riapriva dal lato opposto , cioè apriva in un regime vietato proprio perché c'era una posizione. L'altra metà dell'invariante è che le CHIUSURE restino sempre permesse: un router che potesse impedirle sarebbe un rischio, non un filtro.

`DisallowedStrategy_NeverOpens`, `AllowedStrategy_OpensNormally`, `ReversalCannotSmuggleAnOpeningIntoAForbiddenRegime`, `UnknownRegime_BehavesExactlyAsBeforeTheRouter`, `NoRouterAtAll_IsTheOldBehaviour`

## `SafetyCheckerLeverageTests.cs` — 5 test, 71 righe

> Copre il check #10 di SafetyChecker.Evaluate (leva massima per Futures) e verifica che lo Spot resti invariato: nessun controllo di leva si applica quando MarketType è Spot, anche se per qualche motivo Order.Leverage fosse valorizzato oltre il limite.

`Futures_LeverageWithinLimit_IsAllowed`, `Futures_LeverageOverLimit_IsRejected`, `Futures_LeverageExactlyAtLimit_IsAllowed`, `Spot_LeverageFieldIgnored_EvenIfAboveLimit`, `Futures_LeverageViolation_DoesNotTriggerEmergencyStop`

## `SentimentFeatureFactorTests.cs` — 5 test, 94 righe

> Test dell'opt-in ML di Sentiment 2.0: produce gli stessi numeri del diretto (delega pura, filtro simbolo dalle candele) e lo espone nei prototipi SOLO col flag EnableMlFeature, mentre Create("Sentiment") funziona sempre col provider (round-trip dei modelli salvati) e il costruttore legacy resta invariato.

`Compute_MatchesDirectSentimentAlphaFactor_WithSymbolFilterFromCandles`, `Factory_Prototypes_ContainSentimentOnlyWithOptInFlag`, `Factory_Create_Sentiment_WorksRegardlessOfFlag_WhenProviderIsPresent`, `Factory_LegacyConstructor_HasNoSentiment_AndCreateThrows`, `Factory_BaseCatalogIsUnchanged_ByTheOptionalDependencies`

## `StackingNonNegativeRidgeTests.cs` — 5 test, 116 righe

> E1 — meta-learner dello stacking: pesi NON-NEGATIVI (i pesi negativi estrapolano male fuori campione) e λ scelto per cross-validation invece che fisso. Verifica la proiezione di non-negatività (un base che l'OLS peserebbe negativo viene azzerato), il recupero dei pesi su target additivo pulito, e che la CV preferisca più regolarizzazione quando le predizioni base sono rumorose/collineari.

`NonNegativeRidge_ClampsWeightThatOlsWouldMakeNegative`, `NonNegativeRidge_RecoversAdditiveWeights`, `NonNegativeRidge_AllWeightsNonNegative_OnNoisyBases`, `SelectLambdaByCv_PrefersMoreRegularization_WhenBasesAreNoisy`, `SelectLambdaByCv_PrefersLessRegularization_WhenBasesAreInformative`

## `SymbolFiltersTests.cs` — 5 test, 44 righe

> Arrotondamento quantità/prezzo ai filtri LOT_SIZE/PRICE_FILTER (anti -1100).

`RoundQuantity_FloorsToStepSize`, `RoundQuantity_CoarseStep`, `RoundQuantity_NoStep_ReturnsAsIs`, `RoundPrice_FloorsToTick`, `IsTradable_EnforcesMinQtyAndMinNotional`

## `TradingEngineEquityRetentionTests.cs` — 5 test, 211 righe

> [M1] Curva equity in-memory bounded ( ) e max-drawdown di sessione PERSISTITO: prima il MaxDrawdown viveva solo nella curva in-memory, quindi un riavvio lo azzerava — e il gate assoluto HardMaxDrawdownPercent del PromotionEvaluator poteva promuovere una corsia che aveva già bucato il limite prima del riavvio.

`TrimEquity_UnderLimit_Untouched`, `TrimEquity_OverLimit_DropsOldestBlock`, `TrimEquity_RepeatedGrowth_StaysBounded`, `MaxDrawdown_SurvivesRestart_EvenAfterFullRecovery`, `MaxDrawdown_ResetOnNewSession`

## `TradingEngineExecutionTests.cs` — 5 test, 316 righe

> Test dell'esecuzione live "a fette" (TWAP/VWAP/Iceberg) nel (rif. docs/archive/ROADMAP-QLIB.md §1.2 ). Verifica gli invarianti critici trovati in fase di design: media ponderata dopo N fette, emergency stop a metà piano che chiude SOLO il riempito e annulla il job, riavvio che abbandona il job ma preserva la posizione reale, e il bypass di MaxPositionSizePercent chiuso da un pre-check aggregato. Il tempo è controllato backdatando i job in cache (riflessione, SOLO nel test) così tutte le fette diventano "dovute" senza attese reali; il throttle MinOrderIntervalSeconds è messo a 0.

`ExecutionPlan_Twap_AccumulatesWeightedAverageEntryAndFullQuantity`, `EmergencyStop_MidPlan_ClosesFilledQuantityOnly_AndCancelsJob`, `StartAsync_OrphanedRunningJob_MarkedCancelled_PositionSurvives`, `ExecutionPlan_TotalExceedsMaxPositionSize_RejectedUpfront_NoJobCreated`, `Metrics_TwapExecutionAndClose_EmitTradeJobAndSlippageCounters`

## `TradingEngineStopTests.cs` — 5 test, 265 righe

> Test del wiring automatico stop-loss/take-profit/trailing dal backtest (via EnsembleStrategy) al TradingEngine live: applicazione all'apertura, priorità della modifica manuale, e comportamento causale del trailing (livello calcolato sul best-since-entry PRIMA della candela corrente, come nel motore di backtest — vedi BacktestEngineTests/BacktestStopLossTests).

`AutoStopLoss_AppliedAtOpen_ClosesPositionWhenHit`, `StopLoss_TriggersIntrabar_WhenWickPiercesButCloseIsAbove`, `ManualStopLoss_TakesPriorityOverAutomaticOne`, `TrailingStop_RatchetsUpAndClosesOnPullback`, `NoStopConfigured_LegacyEnsemble_NeverSetsAutomaticStop`

## `TradingQueryHandlersTests.cs` — 5 test, 131 righe

> Test dei 5 handler pilota (Fase 1, PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.6 "query pilota"): ciascuno deve risolvere il motore della corsia GIUSTA (LaneId sulla richiesta, non un'istanza fissa) e restituirne il risultato inalterato. Due corsie keyed registrate con valori diversi provano che il routing avviene per dato (§4.2), non per una singola istanza implicita — usa sempre la sola corsia 0 e non eserciterebbe questo aspetto.

`GetLaneStatusQuery_ResolvesRequestedLane_NotAlwaysLaneZero`, `GetOpenPositionsQuery_ReturnsResolvedEnginesPositions`, `GetPerformanceQuery_PassesFromThroughToEngine`, `GetOrderHistoryQuery_ReturnsResolvedEnginesOrders`, `GetPendingOrdersQuery_ReturnsResolvedEnginesPendingOrders`

## `AuditStressMlTrainingTests.cs` — 4 test, 170 righe

> Audit FASE 2.2 — training pesante: LightGBM su 100k righe × 30 feature e MLP (C# puro) su 20k righe, con concept-drift detection attiva sulle stesse feature (PSI/KS/Page-Hinkley) e misure di tempo/allocazioni. Verifica: il training completa, le predizioni sono finite, i detector scattano sulle distribuzioni spostate e NON scattano su quelle identiche, e la memoria viene rilasciata dopo Dispose (nessuna ritenzione da training ripetuti).

`HeavyLightGbm_100kRows_TrainsPredictsAndReleasesMemory`, `HeavyMlp_20kRows_TrainsDeterministicallyWithFiniteOutputs`, `ConceptDriftDetectors_DuringTrainingLoop_AlertOnShift_SilentOnSame`, `RepeatedTrainings_WithDriftChecks_DoNotAccumulateMemory`

## `BarBuilderTests.cs` — 4 test, 95 righe

> Test delle barre a volume/controvalore costante (Jansen ML4T, cap. 2).

`VolumeBars_AggregateUntilThreshold`, `DollarBars_UseTypicalPriceTimesVolume`, `SuggestThresholds_TargetBarCount`, `InvalidThreshold_Throws`

## `BayesianKernelFitTests.cs` — 4 test, 66 righe

> E1 — il kernel del GP bayesiano ora STIMA i suoi iperparametri via log-verosimiglianza marginale invece di tenerli fissi (fissi ⇒ il surrogato non si adatta e la ricerca degenera verso il casuale). Verifica che la stima recuperi la scala giusta: lengthscale grande su dati lisci, piccola su dati oscillanti; che sotto il minimo di punti si usino i fallback; e che resti deterministica.

`FitKernel_SmoothData_PrefersLargeLengthscale`, `FitKernel_WigglyData_PrefersSmallLengthscale`, `FitKernel_BelowMinPoints_ReturnsFallback`, `FitKernel_IsDeterministic`

## `ContractsSmokeTests.cs` — 4 test, 99 righe

> Test di fumo dei contratti gRPC/Protobuf (Fase 0 microservizi): il C# generato dai .proto di ProcioneMGR.Contracts serializza e deserializza round-trip senza perdita. I contratti NON sono ancora cablati nell'app (accade in Fase 2+): qui si valida solo che siano ben formati.

`LaneStatus_RoundTripsThroughProtobuf`, `SetStopLossTakeProfit_AbsentField_IsDistinguishableFromZero`, `PredictSignal_RoundTripsThroughProtobuf`, `MarketDataSyncedEvent_RoundTripsThroughProtobuf`

## `CostPropagationTests.cs` — 4 test, 204 righe

> [R2] REGRESSIONE su un'asimmetria trovata preparando l'ingestione a 1m. Il percorso di SELEZIONE (Optimization, e a cascata Discovery) costruiva i backtest senza mai impostare SlippagePercent : i parametri e i candidati venivano scelti a sole commissioni, mentre la successiva validazione holdout della pipeline applicava i costi pieni. Non era solo un errore di contabilità, era un errore di SELEZIONE: ottimizzando senza attrito si premiano i parametri ad alto turnover, il cui vantaggio apparente è esattamente il costo che non si sta pagando. Sui timeframe lenti l'ottimismo è modesto; a 1m lo slippage pesa quanto la commissione, e la classifica dei candidati si riempirebbe di strategie che perdono denaro prima ancora che il gate onesto le veda.

`OptimizationConfiguration_DefaultsToHonestSlippage_NotZero`, `Optimizer_PropagatesSlippage_ToEveryBacktest`, `Optimizer_WithHonestDefault_NeverBacktestsWithoutFriction`, `Discovery_PropagatesCosts_ToTheOptimizer`

## `DataProtectionApplicationNameTests.cs` — 4 test, 82 righe

> Il nome applicativo di Data Protection è la discriminante con cui vengono derivate le chiavi che firmano i cookie di autenticazione: due processi con discriminanti diverse non possono leggere i cookie l'uno dell'altro. BUG REALE (2026-07-20): SetApplicationName veniva applicato SOLO dentro il ramo if (keyRingPath) — cioè mai in sviluppo locale, dove quel path è vuoto per scelta. Il default di ASP.NET Core deriva allora la discriminante dal ContentRootPath , e due copie dello stesso repository in cartelle diverse (un worktree git accanto al checkout principale) ne ottengono due diverse pur condividendo il keyring del profilo utente. Sintomo osservato: si arriva alla pagina di login, si accede, e si resta fuori — senza alcun messaggio d'errore. I test esercitano la composizione REALE ( , us…

`ApplicationName_IsSet_EvenWithoutAKeyRingPath`, `ApplicationName_IsTheSame_WithAndWithoutAKeyRingPath`, `EmptyKeyRingPath_IsTreatedAsAbsent_ButTheNameStaysSet`, `ApplicationName_IsAStableLiteral_NotDerivedFromThePath`

## `DatabaseMigratorTests.cs` — 4 test, 95 righe

> [2026-08-05] Migrate-on-startup. Il contratto: applicare lo schema all'avvio NON deve poter rompere l'avvio. Un host che non ha l'assembly delle migrazioni (i satelliti non lo ricevono), un lock occupato da un altro host, o l'interruttore spento devono produrre una RIGA DI LOG e la prosecuzione — mai un'eccezione che risale, e mai il silenzio che lascia credere che lo schema sia allineato.

`Spento_NonTocaNienteELoDichiara`, `SenzaAssemblyDelleMigrazioni_DichiaraENonLancia`, `Default_MigrazioneAccesa`, `DueChiamateConcorrenti_NessunaEsplode`

## `EventTriggerGeneratorTests.cs` — 4 test, 101 righe

> [R3 — ROADMAP-RENDIMENTO] Il generatore di event-trigger emetteva varianti di Threshold anche sugli eventi flip del Supertrend, dove quel parametro non lega (sono cambi di segno, non percentili). Il risultato erano DUPLICATI ESATTI: stessa strategia, stesso risultato, contati come tentativi distinti dal DSR e usciti come "confermati" doppi dalla caccia densa del 2026-07-25. Questi test difendono due cose: che il generatore non produca più quei duplicati, e che la premessa sia VERA — cioè che sui flip la soglia sia davvero inerte sulla strategia reale. La seconda conta più della prima: se un giorno qualcuno facesse legare Threshold sui flip, il test della premessa fallirebbe e il fix del generatore andrebbe ritirato insieme.

`FlipEvents_GetASingleThresholdVariant`, `NonFlipEvents_KeepTheirThresholdSweep`, `AllGeneratedKeys_AreUnique`, `ThePremiseIsTrue_ThresholdIsInertOnFlips_OnTheRealStrategy`

## `ExchangeSigningTests.cs` — 4 test, 50 righe

> Verifica la firma HMAC della richiesta. Il valore atteso è stato calcolato in modo INDIPENDENTE (HMACSHA256 di sistema, via PowerShell) sullo stesso (secret, query) usato dall'esempio Binance: pinna l'output esatto della funzione di firma. Se la firma è sbagliata, ogni ordine reale verrebbe rifiutato dall'exchange.

`Binance_HmacSha256Hex_MatchesIndependentComputation`, `Hex_IsLowercase64Chars`, `Bitget_HmacSha256Base64_IsDeterministicAndValidBase64`, `DifferentSecret_ProducesDifferentSignature`

## `ExcursionAnalyzerTests.cs` — 4 test, 83 righe

> Test dell'analisi delle escursioni e dell'effetto memoria (Trombetta cap. 4).

`SuggestStopLoss_SeparatesPositiveAndNegativeBars`, `LaggedAutocorrelation_AlternatingSeries_Lag1Negative_Lag2Positive`, `ContinuationProbability_MonotoneUptrend_Is100Percent`, `ContinuationProbability_Threshold_FiltersSmallMoves`

## `ExperimentTrackerTests.cs` — 4 test, 122 righe

> Test dell'Experiment Tracker (rif. docs/archive/ROADMAP-QLIB.md §1.3 ): ciclo di vita di un run (Running → metriche → Completed), merge delle metriche, hash "git-like" dei parametri (config identiche ⇒ hash identico), e robustezza best-effort degli helper Safe* (non lanciano).

`StartLogComplete_PersistsRunWithMetrics`, `IdenticalParameters_ProduceIdenticalHash_DifferentDoNot`, `LogArtifact_IsPersistedAgainstRun`, `SafeHelpers_NeverThrow_EvenForMissingRun`

## `FactorIcTStatTests.cs` — 4 test, 97 righe

> R1.3 — significatività dell'IC con t-stat Newey-West (HAC), robusta all'autocorrelazione dei forward-return sovrapposti (horizon &gt; 1). Verifica che l'overlap ABBASSA la significatività (\|t_NW\| &lt; \|t_ingenua\|), come atteso, e che l'evaluator popola i campi.

`NeweyWest_WithOverlap_LowersSignificanceVsZeroLag`, `NeweyWest_NoOverlap_CloseToIndependentTStat`, `DegenerateSeries_ReturnsZero`, `Evaluator_PopulatesNeweyWestFields`

## `GeneticMinerCvGateTests.cs` — 4 test, 136 righe

> E1 — miner genetico: fitness CROSS-VALIDATA (l'IC misurato su fold temporali, la fitness premia consistenza non un \|IC\| di finestra unica gonfiabile) + gate PBO BLOCCANTE (se la selezione è complessivamente overfit il batch viene svuotato). Verifica la discriminazione della CV su serie sintetiche e il meccanismo di blocco deterministico.

`CrossValidatedIc_RewardsConsistentFactor_OverFoldLuckyOne`, `Miner_WithCvFitness_StillDeterministicAndNonEmpty`, `BlockingPboGate_EmptiesBatch_AtOrBelowPanelPbo_PassesAbove`, `BlockingPboGate_Disabled_ByDefault`

## `IcFeatureSelectorTests.cs` — 4 test, 101 righe

> Test della selezione feature per Information Coefficient (Fase 3): l'ordinamento è per \|IC\| decrescente, i filtri (\|IC\| minimo, TopN) si applicano correttamente, ed è deterministico — così la scelta delle feature dei modelli ML diventa guidata dalla misura, non manuale.

`Rank_IsSortedByDescendingAbsIc`, `Select_TopN_CapsCountAndKeepsTheStrongest`, `Select_MinAbsIc_FiltersOutWeakFactors`, `Rank_IsDeterministic`

## `LinearReturnPredictorTests.cs` — 4 test, 88 righe

> Test di : apprendimento di una relazione lineare nota, persistenza (Save/Load) e comportamento prima dell'addestramento.

`Fit_LearnsKnownLinearRelationship`, `Predict_BeforeFit_Throws`, `Save_BeforeFit_Throws`, `SaveAndLoad_RoundTrip_ProducesSamePredictions`

## `LiquidationAccumulationTests.cs` — 4 test, 180 righe

> [F4 roadmap frontiere-profitto] Accumulo liquidazioni: parsing dello stream pubblico (!forceOrder@arr), aggregazione per (ticker, ora, lato) e flush IDEMPOTENTE su SentimentMetricPoints. Il dato non è ricostruibile a posteriori: il contratto qui fissato è che nessun percorso (payload sporchi, doppi flush, riavvii) possa corromperlo in silenzio.

`Parse_LongLiquidation_UsesAvgPriceAndBaseTicker`, `Parse_ShortLiquidation_NonUsdt_Malformed_AndOtherEvents`, `Aggregator_BucketsPerTickerHourAndSide_AndPrunes`, `Flush_WritesHourTotals_AndSecondFlushUpdatesInsteadOfDuplicating`

## `MultiLaneIsolationTests.cs` — 4 test, 264 righe

> Verifica che le corsie di trading (LaneId) siano isolate a livello dati pur condividendo lo stesso database (colonna discriminante LaneId invece di DbContext separati - vedi docs/REPORT-MULTI-LANE.md): operazioni su una corsia non devono mai leggere, scrivere o cancellare i dati di un'altra corsia.

`OpenPosition_OnOneLane_NotVisibleOnAnotherLane`, `StartAsync_Paper_OnlyWipesOwnLanePositions`, `TradeHistoryAndPerformance_AreIsolatedPerLane`, `EnsembleManager_Configuration_IsIsolatedPerLane`

## `ObservabilityTests.cs` — 4 test, 177 righe

> Test di fumo del wiring OTLP opt-in (Fase 0 microservizi): con Observability:Enabled=true il container DI si costruisce e i provider OTel si risolvono senza che alcun collector sia in ascolto (l'exporter OTLP è fire-and-forget); con il flag OFF non viene registrato nulla. Nessuna dipendenza da Postgres: classe separata fuori dalla collection.

`ProcioneMetrics_RecordsAllCounters`, `DriftWorker_EmitsDriftAndRetirementMetrics_ForChampion`, `AddProcioneObservability_Enabled_BuildsContainerWithoutCollector`, `AddProcioneObservability_Disabled_RegistersNothing`

## `OnnxSentimentPilotTests.cs` — 4 test, 173 righe

> Test end-to-end del pilota ONNX (PRD-ONNX-SENTIMENT-PILOT, Livello 1): addestramento ML.NET su etichette deboli → export ConvertToOnnx → caricamento in ONNX Runtime → PARITÀ fra i due runtime attraverso lo scorer reale. È il livello 1 dello standard di verifica: il riferimento indipendente dell'inferenza ONNX è il framework che ha addestrato il modello.

`Train_Export_Load_Parity_EndToEnd`, `TrainedModel_PreservesSentimentDirection`, `Train_WithTooFewRows_FailsHonestly`, `Scorer_WithoutModel_FallsBackToKeyword`

## `OptimizationCpcvTests.cs` — 4 test, 173 righe

> [T1.6 roadmap macchina-ricerca] CPCV esteso al percorso strategie: da UN percorso out-of-sample (walk-forward + holdout) a una DISTRIBUZIONE di Sharpe su C(gruppi, gruppiTest) percorsi. Il test che conta: con un ottimo PIANTATO (qualità massima a X=7, coerente su tutti i gruppi), il CPCV deve sceglierlo su ogni percorso (stabilità 100%) e la distribuzione OOS deve essere tutta positiva. È l'esperimento di controllo in miniatura: se il meccanismo non trova un edge costruito per esserci, non può dire nulla sui dati veri.

`PlantedOptimum_IsChosenOnEveryPath_AndTheWholeOosDistributionIsPositive`, `PurgeAndEmbargo_ReduceTheTrainGroups_ButPathsStillResolve`, `SameInput_SameDistribution_Deterministic`, `TooFewCandles_FailsLoudly_InsteadOfMeasuringNoise`

## `OptimizationEmbargoTests.cs` — 4 test, 214 righe

> [T0.1 roadmap macchina-ricerca] Test dell'embargo nel walk-forward dell'ottimizzatore. Il difetto che l'embargo corregge: GenerateWindows produce finestre IS/OOS CONTIGUE ( oosStart = isEnd ), quindi una posizione aperta a fine in-sample prosegue nell'out-of-sample e un indicatore con lookback L vede fino a L barre di in-sample — la misura "fuori campione" non lo è del tutto. La piattaforma possedeva già lo strumento giusto ( PurgedTimeSeriesCv ) ma lo usava solo nel percorso ML. Il test più importante è : con embargo 0 il comportamento resta bit-identico a prima — nessuno sweep esistente cambia risultato per l'introduzione del campo.

`DefaultZero_KeepsHistoricalContiguousBehaviour`, `Embargo_TrimsExactlyThatManyBarsFromEachOosWindow`, `EmbargoConsumingTheWholeOos_SkipsTheWindowInsteadOfMeasuringNoise`, `NegativeEmbargo_IsRejectedByValidation`

## `PipelineCostsTests.cs` — 4 test, 61 righe

> P0-4: i backtest della pipeline devono usare i costi reali del venue (Bitget), INCLUSO il funding dei perpetual — che prima restava a 0 (default di BacktestConfiguration) mentre fee e slippage erano già applicati. PipelineCosts centralizza lettura + applicazione dei tre costi.

`FromConfig_EmptyConfig_UsesVenueDefaults_IncludingFunding`, `FromConfig_ReadsOverrides`, `ApplyTo_SetsAllThreeCostsOnBacktestConfig`, `ParameterDefinitions_ExposeTheThreeCostKnobs`

## `PortfolioOptimizerSelectionTests.cs` — 4 test, 145 righe

> [2.8 PRD-RISANAMENTO, chiude C-05] L'allocatore dei pesi dell'ensemble è selezionabile per nome (parametro di stage portfolioOptimizer ) invece che HRP cablato come tipo concreto. Le proprietà che contano: 1. REGRESSIONE: default e "HRP" esplicito producono pesi identici (il comportamento storico non cambia per chi non tocca nulla); 2. la scelta CAMBIA davvero i pesi (MeanVariance ≠ HRP su gambe con profili diversi); 3. nome sconosciuto ⇒ HRP con dichiarazione nel log, mai un run rotto per un typo; 4. il Method della proposta dichiara l'optimizer REALE (prima era l'etichetta fissa "HRP").

`Default_And_ExplicitHrp_ProduceIdenticalWeights`, `MeanVariance_ProducesDifferentWeights_ThanHrp`, `ChosenOptimizer_IsDeclaredInProposalMethod`, `UnknownName_FallsBackToHrp_AndSaysSo`

## `RegimeLabelWindowTests.cs` — 4 test, 93 righe

> Regressione di un difetto SILENZIOSO trovato guardando un run reale della pipeline il 2026-07-25: la configurazione swing giornaliera riportava Regime: sconosciuto a ogni esecuzione, anche subito dopo aver riaddestrato il modello nello stesso run. La causa: la finestra di etichettatura è espressa in giorni ( labelLookbackDays , default 30) mentre il warmup dell'estrattore di feature è in barre (50, la finestra più lunga che usa). Su 1h trenta giorni fanno 720 barre e tutto funziona; su 1d ne fanno 30 , cioè sotto il warmup — l'estrattore restituiva zero feature, nessuna candela veniva etichettata, e il regime usciva "sconosciuto" senza che niente segnalasse il perché. È il tipo di guasto che non rompe niente e non compare nei log: produce un valore plausibile ("sconosciuto" è una risposta…

`EveryTimeframe_GetsEnoughBarsToClearTheWarmup`, `TheDailyCase_IsTheOneThatUsedToBreak`, `TheHourlyCase_IsUnchanged`, `ExtractorReallyReturnsNothingBelowWarmup`

## `RestingStopOrderTests.cs` — 4 test, 107 righe

> [P0-5] Costruzione delle richieste per gli ordini TRIGGER reduce-only "resting" (stop-market / take-profit-market) su Bitget e Binance. Verifica i parametri inviati SENZA rete (fake handler): è ciò che si può controllare in modo deterministico prima della verifica dal vivo su Demo/Testnet.

`Bitget_TriggerOrder_BuildsReduceOnlyPlanOrder`, `Binance_StopLoss_UsesStopMarketReduceOnlyMarkPrice`, `Binance_TakeProfit_UsesTakeProfitMarket`, `TriggerOrder_MissingTriggerPrice_FailsWithoutCallingExchange`

## `SafetyCheckerFuturesExposureTests.cs` — 4 test, 107 righe

> [D-02, Fase 1 PRD-RISANAMENTO] MaxTotalExposurePercent deve vincolare l'esposizione NOZIONALE aggregata anche sui Futures. Prima del fix lo stato di safety sommava il MARGINE delle posizioni aperte al NOZIONALE del nuovo ordine (unita' diverse): con leva 5x e MaxOpenPositions alzato, il capitale esposto raggiungeva il 100% contro un limite dichiarato del 50% senza che il check scattasse — coi default la coincidenza 10%×5=50% mascherava tutto. Qui si riproduce ESATTAMENTE lo scenario numerico dell'audit (docs/audit/20_DEEP_DIVE_CODE_ANALYSIS.md §3) e si verifica che ora il limite morda.

`ExposedNotional_UsesNotional_NotMargin`, `AuditScenario_TenthPosition_IsNowRejected`, `WithinDeclaredLimit_IsStillAllowed`, `SpotSemantics_Unchanged`

## `SentimentMetricSyncServiceTests.cs` — 4 test, 159 righe

> Test di : inserimento, dedupe fra sync sovrapposte, isolamento per fonte (una che lancia non fa fallire le altre), backfill una tantum delle fonti backfillable, e popolamento del registro di salute.

`SyncAllAsync_InsertsSamples_AndDeduplicatesOverlappingRuns`, `SyncAllAsync_OneSourceThrows_OthersStillSync_AndHealthTracksBoth`, `SyncAllAsync_BackfillableSource_FetchesFullHistoryOnlyWhenTableIsEmpty`, `SyncAllAsync_SameTimestampDifferentMetricOrSymbol_AreDistinctPoints`

## `SentimentScorerComparisonServiceTests.cs` — 4 test, 172 righe

> Test di (harness A/B/C): stesse notizie, stesse candele, stesso giudice ( ) per ogni scorer; LLM interamente ripiegato ⇒ riga dichiarata non disponibile (mai un confronto che non confronta); ONNX senza modello ⇒ riga dichiarata; disaccordi calcolati solo dove esistono due punteggi veri.

`KeywordOnly_ProducesEvaluationOnSameJudge`, `LlmAvailable_SecondEntry_AndDisagreementsWhereScoresDiffer`, `LlmEntirelyFallenBack_DeclaredUnavailable_NotADisguisedDuplicate`, `OnnxWithoutModel_DeclaredUnavailable`

## `StackedReturnPredictorTests.cs` — 4 test, 113 righe

> Test dello (rif. docs/archive/ROADMAP-QLIB.md §1.8 ): ogni modalità di stacking addestra e predice su un dataset apprendibile, e il round-trip Save/Load riproduce le stesse predizioni. Essendo un IReturnPredictor , si comporta come gli altri modelli (nessun consumatore va toccato).

`EveryMode_FitsAndPredictsLearnableTarget`, `SaveLoad_RoundTrip_ReproducesPredictions`, `FeatureImportance_RanksTheStrongestFeatureFirst`, `SingleBase_IsValid`

## `SymbolCatalogTests.cs` — 4 test, 155 righe

> [E-04, Fase 2 PRD-RISANAMENTO] Il catalogo simboli condiviso: la POLITICA (unione di TrackedSeries e simboli storici in OhlcvData, ordinata) dichiarata e verificata in un punto solo, al posto delle sette copie implicite nelle pagine. La sfumatura che contava: una serie RIMOSSA dalla watchlist resta selezionabile (i suoi dati esistono), e una APPENA AGGIUNTA compare anche senza candele — nessuna delle due sarebbe sopravvissuta a una sostituzione ingenua con la sola TrackedSeries. Stessa politica per le COPPIE (simbolo, timeframe) di GetKnownSeriesAsync, con in più il vincolo che NON siano il prodotto cartesiano simboli × timeframe: quello mentirebbe sulle serie senza dati.

`Union_TrackedAndHistorical_Ordered`, `Cache_ServesWithoutRescan_UntilInvalidated`, `Series_UnionTrackedAndHistorical_NoCartesianProduct`, `Series_ShareTheCacheAndTheInvalidate_WithSymbols`

## `TelegramNotifierTests.cs` — 4 test, 89 righe

> Test del provider Telegram (Fase 4): payload corretto (chat_id + testo con icona di gravità), token SOLO dall'env (mai config), errori HTTP che diventano eccezioni (che il dispatcher contiene). Handler HTTP scriptato: nessuna chiamata reale.

`Send_PostsToBotApi_WithChatIdAndSeverityIcon`, `MissingToken_Throws_WithClearMessage`, `MissingChatId_Throws`, `HttpFailure_Throws_SoTheDispatcherLogsIt`

## `TradingEngineChampionTests.cs` — 4 test, 311 righe

> Test del Champion del registry come strategia di una lane (follow-up "Champion → TradingEngine"). Copre: (a) CONFINE DI SICUREZZA — una lane Live rifiuta il Champion con throw esplicito, mai un fallback silenzioso; (b) cache per-lane — il modello non si ricarica a ogni candela ma solo al cambio di Champion; (c) parità batch/stream — lo stesso SavedMlModel caricato da MlModelLoader dà lo stesso segnale su serie piena (backtest) e su buffer (streaming); (e) end-to-end — una lane Paper col Champion apre posizioni coerentemente coi segnali del predittore. La non-regressione delle lane a sole regole (d) è coperta dalla suite esistente (il ramo Champion scatta SOLO per StrategyName=="MlChampion").

`Champion_OnLiveLane_ThrowsExplicitly_NeverSilentFallback`, `Champion_Cache_ReloadsOnlyWhenModelChanges`, `Champion_BatchAndStreamLoader_ProduceSameSignal_OnSameSeries`, `Champion_PaperLane_OpensPosition_FromPredictorSignal`

## `TradingEngineSizingTests.cs` — 4 test, 191 righe

> Regressione H1 (audit 2026-07): con la size hard-coded all'8% e leva 5, il nozionale per posizione era il 40% del capitale — sopra MaxPositionSizePercent (10%) — quindi il SafetyChecker rifiutava OGNI ordine e la corsia futures non faceva mai trading, in silenzio. Il fix rende la size configurabile ( ) e valida la coerenza a : meglio un errore azionabile all'avvio che il silenzio degli ordini.

`Futures_DefaultSafety_Leverage5_StartFailsFastWithActionableMessage`, `Futures_RaisedLimits_StartsAndFirstOrderPassesSafetyChecker`, `Spot_SizeAboveMaxPositionSize_StartFailsFast`, `Spot_DefaultSafety_StartsNormally`

## `TradingWorkerClosedBarTests.cs` — 4 test, 106 righe

> [2026-08-06] Il worker deve alimentare il motore SOLO con barre chiuse. Il guasto, trovato dal proprietario : sulla corsia 3 uno short ETC/USDT con take profit a 6,3786 non si è chiuso, benché il minimo della barra 4h delle 08:00 fosse 6,31 . Causa: l'ingestione REST scrive anche l'ultima kline INCOMPLETA, il worker la consumava appena comparsa — quando il minimo era ancora sopra il target — e avanzava il cursore. Quando la barra chiudeva col minimo vero, ProcessCandleAsync la scartava come «già vista». Il tratto peggiore era che nessun indicatore lo mostrava : il battito diceva «ultima candela 16:00 · 0 barre indietro» in verde, mentre quella barra 4h chiudeva alle 20:00. Su 4h il punto cieco vale fino a quattro ore di prezzi.

`QuattroOre_AlleSedici48_LUltimaChiusaEQuellaDelleDodici`, `AppenaChiusa_LaBarraDiventaAlimentabile`, `TimeframeIgnoto_NienteDaAlimentare`, `AlimentandoLUltimaChiusa_IlBattitoDiceZeroBarreIndietro`

## `TrialsCountPropagationTests.cs` — 4 test, 108 righe

> [D-01, Fase 1 PRD-RISANAMENTO] Il gate DSR deve usare le combinazioni REALMENTE provate, non i soli sopravvissuti al Top-N. Prima nello stesso run convivevano tre conteggi che non si parlavano: PowerCheckStage ne assumeva 300, StrategyDiscoveryEngine misurava il numero vero (solo per la UI), e il gate usava validated.Count ≤ topN = 15 — con 3.000 combinazioni la soglia SR* applicata era la metà di quella dovuta (1,77σ contro 3,56σ, docs/audit/20_DEEP_DIVE_CODE_ANALYSIS.md §2).

`MoreExploredTrials_LowerDeflatedSharpe`, `UnknownExploration_PreservesHistoricalBehaviour`, `ExploredFewerThanCandidates_NeverLowersN`, `PipelineContext_AccumulatesAcrossStages`

## `AuditPipelineExperimentLoggingTests.cs` — 3 test, 250 righe

> Audit FASE 3.3 — ogni run di pipeline (successo O fallimento) deve comporre un ExperimentRun accanto al PipelineRun : parametri (universo, modalità, seed), metriche (stage/sopravvissuti) e stato finale coerente. È ciò che rende i run confrontabili in modo deterministico nella tabella di /experiments. Il wiring esiste in PipelineEngine.FinalizeRun ma nessun test lo verificava end-to-end.

`PipelineRun_ComposesExperimentRun_WithParamsMetricsAndMatchingStatus`, `CompletedRunWithSurvivor_LogsRichMetrics_BestProfileAndPanelPbo`, `TwoRuns_SameConfiguration_ProduceSameParametersHash`

## `AuditStressNestedExecutionTests.cs` — 3 test, 198 righe

> Audit FASE 2.3 (parte DB) — l'ExperimentTracker sotto concorrenza reale su Postgres: metriche loggate in parallelo sullo STESSO run non devono perdersi (il read-modify-write del JSON è la superficie a rischio), e run interi in parallelo devono completare tutti senza deadlock.

`HighFrequencyNestedLoop_AllAlgorithms_ParallelThreads_DeterministicAndExact`, `ConcurrentMetricLogging_OnSameRun_MustNotLoseAnyMetric`, `ParallelFullRuns_AllCompleteWithArtifacts_NoDeadlock`

## `ConfigurationBindingTests.cs` — 3 test, 83 righe

> Un refuso nel nome di una sezione di configurazione non rompe niente: lascia semplicemente la funzione spenta in silenzio . È il guasto peggiore per una manopola di sicurezza, perché l'operatore la vede scritta nel file e crede che sia attiva. Questi test legano le sezioni dal file d'esempio versionato , non da JSON inventato qui: così coprono anche il caso in cui il codice sia giusto e sia il file a essere sbagliato.

`CorrelatedExposureSection_BindsFromTheShippedExample`, `RegimeRoutingSection_BindsFromTheShippedExample_AndStaysInObservation`, `RegimeRoutingRules_NameOnlyStrategiesThatExist`

## `ConfigurationUiCoverageTests.cs` — 3 test, 201 righe

> LA REGOLA D'ORO DELLA PIATTAFORMA, resa verificabile: nessuna funzione backend può esistere senza essere controllabile dall'interfaccia web. L'audit backend↔frontend del 2026-07-29 ha trovato quattordici sezioni di configurazione che governavano funzioni vive — il feed real-time e il suo potere di chiudere posizioni, il limite di esposizione correlata, il router di regime, il watchdog delle corsie, il canale Telegram, il forward test del carry — e che si potevano toccare SOLO editando appsettings.json a mano. Nessuna era rotta: erano invisibili, che per una manopola di sicurezza è lo stesso guasto della ConfigurationBindingTests vista da un altro lato. Questo test impedisce che il buco si riapra. Scandisce i sorgenti alla ricerca delle sezioni lette dal codice e pretende che ognuna compai…

`EveryConfiguredSection_IsEitherExposedInTheUi_OrDeliberatelyNot`, `EveryClaimedOwnerPage_ActuallyNamesTheSection`, `TheInventoryHasNoStaleEntries`

## `EnsembleSimulationCacheTests.cs` — 3 test, 180 righe

> [F1 PRD Valore] La cache della simulazione in : prima di questa cache, il poll della pagina Ensemble rieseguiva due simulazioni complete (un backtest per gamba) ogni 15 secondi. Il contratto verificato qui è triplice: (1) a parità di candele e configurazione la simulazione NON si ripete; (2) una candela nuova invalida; (3) una modifica di configurazione invalida. Il motore è un fake che conta le chiamate: il valore dei numeri simulati lo verificano i test dell'EnsembleManager, qui conta SOLO quante volte si paga.

`RepeatedReads_SameCandlesAndConfig_SimulateOnlyOnce`, `NewCandle_InvalidatesCache`, `ConfigChange_InvalidatesCache`

## `ExcursionBracketTests.cs` — 3 test, 74 righe

> Verifica il calcolo automatico del bracket SL+TP data-driven (percentili di escursione avversa/ favorevole) aggiunto a . È la base del nuovo comportamento "calcola/proponi/applica automaticamente stop loss e take profit".

`SuggestTakeProfit_LongPercentile_ReflectsFavorableExcursion`, `SuggestBracket_Long_CombinesStopAndTargetPercentiles`, `SuggestBracket_Use99thPercentile_IsWiderThan95th`

## `ExecutionSquareRootImpactTests.cs` — 3 test, 69 righe

> E1 — modello di impatto di mercato √(partecipazione) (legge empirica di Almgren) al posto del solo lineare. Verifica la concavità: per piccole partecipazioni il √ costa PIÙ del lineare (per unità), e raddoppiando la partecipazione l'impatto √ cresce di ×√-ratio (concavo) mentre il lineare cresce proporzionalmente. Isola l'impatto azzerando lo spread e alzando il tetto.

`SquareRoot_ExceedsLinear_ForSmallParticipation`, `SquareRoot_IsConcave_QuadruplingParticipationDoublesImpact`, `SquareRoot_IsDefault`

## `FeatureImportanceTests.cs` — 3 test, 74 righe

> Test della permutation feature importance ( ): una feature davvero predittiva deve pesare più di una puramente casuale, per qualunque modello (lineare o ad alberi).

`Linear_InformativeFeature_RanksAboveNoise`, `RandomForest_InformativeFeature_RanksAboveNoise`, `ComputeFeatureImportance_BeforeFit_Throws`

## `HrpLinkageTests.cs` — 3 test, 82 righe

> E1 — l'HRP ora usa un linkage configurabile (default Average/UPGMA) invece del solo single-linkage dell'articolo originale, che soffre di "chaining". Verifica il default, la validità dei pesi e che la scelta del linkage sia effettivamente cablata (su una struttura a catena Single ≠ Average).

`Hrp_DefaultLinkage_IsAverage`, `Hrp_AverageLinkage_ProducesValidWeights`, `SingleAndAverageLinkage_DifferAsAlgorithms_OnPathologicalChain`

## `KlineExtendedFieldsTests.cs` — 3 test, 171 righe

> La regola di MERGE dell'ingestione sui campi estesi, su DB vero: un update senza campi estesi (fonte che non li espone, es. Bitget) non deve azzerare quelli già scritti.

`Binance_FullKline_ExtractsTheFourPreviouslyDiscardedFields`, `Binance_ShortPayload_LeavesExtendedFieldsNull_InsteadOfCrashing`, `UpdateWithoutExtendedFields_DoesNotEraseThePreviouslyCollectedOnes`

## `MlSavedModelIntegrationTests.cs` — 3 test, 235 righe

> Verifica il punto di aggancio che rende i modelli ML utilizzabili da Optimization/Discovery/ Ensemble: con StrategyName="Ml" deve risolvere la strategia caricando un dal DB (via "SavedModelId" nei parametri), esattamente come già fa per nome con le strategie a regole — nessun cambiamento richiesto a Optimization/Ensemble, che passano solo BacktestConfiguration .

`RunBacktestAsync_WithMlStrategyName_ResolvesSavedModel_AndCompletes`, `RunBacktestAsync_MlStrategy_MissingSavedModelId_Throws`, `RunBacktestAsync_MlStrategy_NonExistentSavedModelId_Throws`

## `MlStageMapperTests.cs` — 3 test, 36 righe

> Il proto ml.proto e l'enum di dominio ModelStage hanno numerazioni diverse (proto3 impone lo zero-value UNSPECIFIED): la mappatura deve reggere sui NOMI, non sugli ordinali. Questi test impediscono che un futuro riordino di uno dei due enum introduca un disallineamento silenzioso.

`ToProto_ThenFromProto_RoundTrips`, `FromProto_Unspecified_Throws`, `ToProto_MapsChampion_ToChampion`

## `NullTwinWickAsymmetryTests.cs` — 3 test, 116 righe

> [D-04, Fase 1 PRD-RISANAMENTO] Il gemello nullo conserva l'ASIMMETRIA degli stoppini. Prima li spartiva 50/50 sopra e sotto il corpo: geometria idealizzata che sposta la probabilita' di tocco di stop e target intra-barra — proprio cio' che i backtest usano per decidere le uscite, e proprio sull'orizzonte intraday di riferimento. Ora la quota sopra/sotto e' campionata dalla stessa barra sorgente (accoppiata come volume e ampiezza) e SPECCHIATA quando il segno del rendimento e' stato invertito.

`Twin_PreservesWickAsymmetry_InsteadOfHalving`, `Twin_IsStillDeterministic_PerSeed`, `Twin_HighLowEnvelope_StaysCoherent`

## `OptimizationComboKeyTests.cs` — 3 test, 74 righe

> Regressione: deve formattare i decimal in InvariantCulture. Bug reale scoperto integrando MlStrategy in Optimization (soglie Long/Short, non intere): sotto cultura it-IT (virgola come separatore decimale) una chiave come "LongThreshold=0,001,ShortThreshold=0,001" spezza il parsing dell'heatmap (che separa i parametri per virgola), mai emerso prima perché tutte le strategie a regole sweepano solo parametri interi (FastPeriod, SlowPeriod, ...).

`ComboKey_UnderItalianCulture_UsesInvariantDecimalSeparator`, `ComboKey_Parseable_RoundTripsEachParameter`, `ComboKey_OrdersParametersByNameOrdinal_IndependentOfCulture`

## `OptimizationSearchStrategyTests.cs` — 3 test, 163 righe

> Test dell'aggancio Bayesian a (follow-up "Bayesian in /optimization"). Verifica: (a) GridSearch default = comportamento storico (numero esatto di valutazioni, verdetto DSR popolato, trova l'ottimo); (b) ramo Bayesian deterministico a parità di seme; (c) Validation (Deflated Sharpe) popolato anche nel ramo Bayesian; (d) a parità di budget il ramo Bayesian non valuta più del grid equivalente.

`GridSearch_Default_TestsFullProduct_FindsOptimum_AndPopulatesValidation`, `Bayesian_IsDeterministic_ForSameSeed_AndPopulatesValidation`, `Bayesian_EqualBudget_DoesNotEvaluateMoreThanGrid`

## `OverfittingGateTests.cs` — 3 test, 91 righe

> P0-3: il gate anti-overfitting universale applicato in HoldoutValidation. Verifica i tre comportamenti-cardine su dati sintetici (nessun DB/backtest): rumore/edge debole ⇒ scartato via Deflated Sharpe; edge forte con pochi tentativi ⇒ sopravvive; pannello di rumore ⇒ PBO calcolato e tutti i candidati filtrati.

`Apply_FlatHoldout_RejectsOnLowDeflatedSharpe`, `Apply_StrongSteadyHoldoutFewTrials_Survives`, `Apply_LongNoisePanel_ComputesPbo_AndFiltersAll`

## `PairsVolFilterTests.cs` — 3 test, 109 righe

> [E1 roadmap profitto-intraday] Filtro di volatilità dello spread nel pairs backtest: salta gli ingressi quando la vol recente dello spread supera di un rapporto la vol di base — il regime in cui la mean-reversion diventa un blow-up. Questi test fissano: il calcolo causale del rapporto vol, e che accendere il filtro NON possa aprire più posizioni (al più le riduce).

`SpreadVolRatio_IsCausal_AndFiresOnTheVolRegimeTransition`, `VolFilter_On_NeverOpensMoreTradesThanOff`, `VolFilter_Disabled_IsBitIdenticalToNoFilter`

## `PipelineEngineConcurrencyTests.cs` — 3 test, 199 righe

> Regressione per un bug reale trovato durante il lavoro sulla schedulazione automatica del pipeline: persisteva un con Status="Running" PRIMA di controllare se un run era già in corso (il controllo viveva solo dentro LaunchBackground, chiamato DOPO il salvataggio). Con un solo utente che clicca a mano la race era quasi impossibile da osservare, ma lo scheduler introduce chiamate concorrenti reali (due config dovute nello stesso tick, o lo scheduler che corre con un clic manuale) — il secondo StartRunAsync concorrente creava una riga "Running" orfana per sempre, perché il suo lancio in background falliva ma la riga restava già salvata. Fix: la guardia "un run è già in corso" ora gira PRIMA di qualunque scrittura sul DB. Per rendere la race deterministica (non affidata ai tempi macchina) il …

`ConcurrentStartRunAsync_SecondCallThrows_WithoutPersistingOrphanedRun`, `RecoverOrphanedRuns_TurnsInheritedRunningRows_IntoResumablePaused`, `RecoverOrphanedRuns_WithNothingToRecover_IsANoOp`

## `RegimeModelSelectionTests.cs` — 3 test, 120 righe

> [2.7 PRD-RISANAMENTO] Il jump model dietro flag ( MarketRegime:Model ), nel rispetto del contratto C1 scritto in : il DEFAULT resta K-means finché la misura non decide, e il flag rende la misura possibile dall'app. Qui si verificano le proprietà della CUCITURA (il seam in RegimeDetector.TrainAsync), non l'algoritmo in sé — quello è già coperto da JumpModelTests: 1. parsing tollerante del flag (un typo in config non deve rompere il training); 2. compatibilità del formato persistito: i centroidi del fit (double) convertiti in float funzionano con l'inference nearest-centroid ESISTENTE (nessuna migrazione, stessa pipeline di assegnazione); 3. la proprietà per cui il flag esiste: sul percorso usato dal seam, il jump produce meno transizioni del K-means a parità di dati rumorosi.

`TrainingConfiguration_DefaultsToKMeans`, `JumpCentroids_ConvertedToFloat_WorkWithExistingNearestCentroidInference`, `OnSeamPath_Jump_ProducesFewerTransitions_ThanKMeans`

## `RemoteMarketDataSyncServiceTests.cs` — 3 test, 62 righe

> Test di (Fase 1 microservizi): il client HTTP verso il servizio Ingestion remoto, esercitato con un handler mock (nessuna rete reale).

`SyncSeriesAsync_PostsToSyncEndpoint_AndReturnsCandlesProcessed`, `SyncSeriesAsync_Throws_OnNonSuccessStatus`, `SyncAllEnabledAsync_Throws_BecauseSchedulingLivesInRemoteWorker`

## `SafetySectionPersistenceTests.cs` — 3 test, 121 righe

> Regressione del bug adiacente a H1: il vecchio SafetyConfigWriter riscriveva Trading:Safety con un elenco di 7 chiavi scritto a mano — ogni salvataggio dal pannello riportava SILENZIOSAMENTE ai default le proprietà dimenticate (MaxLeverageAllowed, MaintenanceMarginPercent, UseExchangeRestingStops). Il fix serializza l'INTERO oggetto: per costruzione una proprietà nuova non può più essere persa. [E1, 2026-07-31] SafetyConfigWriter non esiste più: il pannello passa da IEngineConfigStore, che scrive tramite — la stessa catena che questi test coprono, senza più l'adapter in mezzo. La proprietà difesa resta identica, e il test enumera le proprietà via reflection così non va aggiornato a mano quando se ne aggiungono.

`SaveSection_WritesEveryPublicProperty_AndPreservesSiblingSections`, `SaveSection_NonDefaultValues_SurviveTheRoundtrip`, `SaveSection_MissingTradingSection_CreatesIt`

## `SentimentSyncWorkerTests.cs` — 3 test, 156 righe

> Test del tick di : metriche + news + snapshot in cache, la cadenza delle news (secondo tick entro l'intervallo NON risincronizza), e la retention con l'esenzione della fonte FearGreed.

`Tick_SyncsMetricsAndNews_AndComputesSnapshotInCache`, `Tick_NewsCadence_SecondTickWithinIntervalSkipsNews_ForceNewsOverrides`, `Tick_Purge_RespectsCutoffs_AndExemptsHistoricalSeries`

## `SignalCatalogCacheTests.cs` — 3 test, 85 righe

> Regressione del bug trovato DAL VIVO la prima notte di Composite su una corsia: la cache del SignalCatalog era per ISTANZA della lista candele — corretta nei backtest (liste immutabili), ma il TradingEngine live riusa UN buffer che cresce/scorre e ri-inizializza la strategia a ogni candela. La matrice tornava stantia: più corta del buffer (IndexOutOfRange a ogni candela) o, con finestra rotolante a lunghezza fissa, della STESSA lunghezza con contenuto vecchio — segnali sbagliati in silenzio, il caso peggiore. La cache ora porta un'impronta (Count, primo, ultimo timestamp) e si rinnova quando il contenuto cambia.

`GrowingBuffer_SameListInstance_MatrixFollowsTheNewLength`, `RollingWindow_SameLengthDifferentContent_IsRecomputed_NotSilentlyStale`, `ImmutableList_BacktestPath_IsStillServedFromCache`

## `TradingEngineCredentialDecryptTests.cs` — 3 test, 243 righe

> Bug B2, lato motore: l'avvio Testnet/Live con credenziali cifrate da una master key DIVERSA deve fallire con un che spiega il rimedio (reinserire in /settings/exchanges), MAI con una AuthenticationTagMismatchException grezza — e se accanto alla riga indecifrabile ce n'è una decifrabile (credenziali reinserite), l'avvio deve riuscire usando quella. Coperto anche il fallback senza reader (vecchi harness), dove l'errore del converter EF va tradotto nello stesso messaggio.

`StartTestnet_UndecryptableCredential_FailsWithClearRemedy_NotRawCryptoException`, `StartTestnet_DecryptableRowNextToTheOldOne_StartsUsingTheGoodRow`, `StartTestnet_LegacyPathWithoutReader_TranslatesConverterFailureIntoTheSameClearError`

## `TradingEngineFuturesEquityTests.cs` — 3 test, 243 righe

> Regressione del bug CRITICO C1 (audit 2026-07): ComputeEquity applicava il modello di cassa dello SPOT (±qty·prezzo) anche ai FUTURES a margine isolato. Su uno short leveraged l'equity crollava del nozionale intero alla candela di APERTURA (es. leva 5, size 8%: −40% di equity istantaneo) → falso "Max drawdown superato" → emergency stop immediato con chiusura forzata di una posizione perfettamente sana. Il fix somma margine bloccato + PnL non realizzato, coerente con il modello di cassa di apertura/chiusura (margine+fee giù, margine+PnL su).

`ShortFlat_EquityIsCapitalMinusFee_NoInstantEmergencyStop`, `LongFlat_TotalPnlIsMinusFee_NotPlusNotional`, `ShortPriceDrops2Percent_EquityGainsUnrealizedPnl`

## `AddOhlcvIngestionTests.cs` — 2 test, 46 righe

> Smoke test del wiring DI di (Fase 1 microservizi), stesso stile di ObservabilityWiringTests: verifica che l'infrastruttura condivisa di ingestione si registri e si risolva senza dipendere da un DB reale.

`AddOhlcvIngestion_RegistersExchangeClientsAndFactory`, `AddOhlcvIngestion_RegistersIngestionService_WithoutSyncServiceOrWorker`

## `AuditStressIngestionTests.cs` — 2 test, 183 righe

> Audit FASE 2.1 — stress dell'ingestione OHLCV con un exchange FINTO deterministico (nessuna rete): N simboli in parallelo su Postgres reale (Testcontainers), migliaia di candele 1m per simbolo, con misure di tempo/throughput/allocazioni riportate nell'output del test. Verifica funzionale: conteggi esatti, idempotenza dell'upsert (re-ingestione = zero duplicati), nessuna ritenzione di memoria anomala a fine corsa.

`MassiveParallelIngestion_10Symbols_30DaysOf1m_CountsExact_NoLeak`, `Reingestion_SameRange_IsIdempotent_NoDuplicates`

## `BacktestEngineTests.cs` — 2 test, 126 righe

> Verifica end-to-end del motore di backtest su dati reali BTC/USDT 1h: completamento senza errori, coerenza equity/candele, e DETERMINISMO (stesso input -&gt; stesso output). Saltato se il DB non e' disponibile.

`EmaCross_OnRealData_Completes_And_IsDeterministic`, `EmptyRange_ReturnsInitialCapital_NoTrades`

## `BacktestFeeTests.cs` — 2 test, 131 righe

> Regressione: una commissione negativa (bug d'uso dalla pagina Backtest, Fee % = -1) non deve comportarsi come un rebate che paga a ogni fill gonfiando i rendimenti. Il Portfolio la clampa a &gt;= 0 — stessa difesa già in uso per leva e slippage — quindi una fee negativa deve produrre ESATTAMENTE il risultato di fee 0, mai uno superiore. Nato nella PR #34 (2026-07-20, mai mergiata) e riportato sul motore attuale il 2026-08-09, dopo aver verificato che il difetto era ancora vivo: _feeFrac veniva calcolato senza guardia.

`NegativeFee_DoesNotBoostReturnAboveZeroFee`, `PositiveFee_ReducesReturnVsZeroFee`

## `CointegrationOnRealDataTests.cs` — 2 test, 135 righe

> Verifica sui DATI REALI del passaggio della cointegrazione ai log-prezzi. Il caso che ha motivato il cambiamento è AAVE/XLM: sulla finestra di selezione 2024-01→2026-03 (4h) la vecchia specificazione sui prezzi grezzi la dichiarava cointegrata, ed è finita fra le otto candidate salvo poi risultare la peggiore (−14,14%, maxDD 15,1% — docs/REPORT-RICERCA-2026-07.md). Il risultato interessante della verifica è che a bocciarla NON è il filtro sull'elasticità: quella vale ~0,69 ed è dentro la banda di sanità. È l'ADF stesso a rifiutarla, una volta che gira sui log. In altre parole la stazionarietà dello spread era un artefatto della regressione in unità di prezzo fra due monete con scale di prezzo lontanissime — il rilievo "cointegrazione troppo liberale" dell'audit 2026-07, che sui log si chi…

`AaveXlm_TheSpuriousPair_IsRejectedUnderLogPrices`, `LogSpecification_IsStricterThanRawPrices_AcrossTheUniverse`

## `EnsembleManagerDecayTests.cs` — 2 test, 144 righe

> Test di integrazione di : carica la configurazione reale (JSON su Postgres) e i TradeRecords reali dal DB, verificando che il monitor riceva esattamente i dati giusti per ciascuna gamba.

`GetDecayReportsAsync_OneReportPerLeg_OnlyItsOwnTradesCounted`, `GetDecayReportsAsync_NoTrades_ReturnsReportsWithZeroCount`

## `ExchangeSettingsPageTests.cs` — 2 test, 133 righe

> Bug B2 (docs/TEST-UI-2026-07-18.md), lato pagina: /settings/exchanges andava in Internal Server Error (AuthenticationTagMismatchException nella materializzazione EF) se in tabella c'era UNA riga cifrata con una master key diversa. Ora la pagina carica via : la riga indecifrabile deve comparire col badge "reinserire le credenziali" (Test disabilitato), le altre righe restare pienamente usabili.

`UndecryptableRow_ShowsBadge_AndKeepsTheOtherRowsUsable`, `NoCredentials_RendersEmptyState`

## `HourOfDaySignalTests.cs` — 2 test, 66 righe

> [2.S roadmap macchina-ricerca] Il segnale "Ora UTC" nel catalogo: la stagionalità oraria che CyclicalAnalyzer misura da tempo diventa CACCIABILE dalla stessa combinatoria degli altri segnali (Composite/StrategyComposer), senza sottosistemi nuovi. Appeso come id 9: gli id 0-8 delle strategie Composite già salvate restano validi.

`Catalog_SignalIdsAreAppendOnly_AndTheTenthIsUtcHour`, `HourSignal_DependsOnlyOnItsOwnTimestamp_NoLookAheadPossible`

## `MigrationsEfVersionAlignmentTests.cs` — 2 test, 84 righe

> [Fase 5, 2026-08-11] Il guardiano nato da un primo avvio SENZA schema: il progetto delle migrazioni aveva Microsoft.EntityFrameworkCore.Design a 10.0.9 mentre l'app pubblicava EF 10.0.8 — la DLL delle migrazioni chiedeva Relational 10.0.9, il binder rifiutava (la versione trovata era più bassa della richiesta), OGNI classe Migration falliva il load, EF ingoiava l'eccezione e dichiarava «zero migrazioni» ⇒ «schema già allineato» su un database VUOTO. Rotto in silenzio sia nel container sia sull'host, mascherato solo dallo schema già migrato a mano. La regola è una sola e vive qui: le versioni della famiglia EF di app e progetto migrazioni devono combaciare. Chi fa un bump lo fa su ENTRAMBI i csproj, o la suite diventa rossa — che è esattamente il momento giusto per accorgersene.

`LaFamigliaEfDellApp_ViaggiaSuUnaVersioneSola`, `IlProgettoMigrazioni_UsaLaStessaVersioneEfDellApp`

## `MlDeterminismTests.cs` — 2 test, 209 righe

> Requisito centrale della Fase 2a (dual-read): l'inferenza del servizio ml remoto deve essere BYTE-IDENTICA a quella locale per lo stesso input. Qui la prova senza DB né rete: si addestra un predittore in memoria, lo si salva su bytes, e si confronta la predizione locale (via ) con quella del servizio remoto (chiamata diretta a ). Uguaglianza ESATTA, non a tolleranza. La copre tutti i ModelType, inclusi Attention/Stacked: prima del fix del bug in MlModelLoader (che li caricava come Linear) questi due casi fallivano l'assert su Name.

`LocalAndRemote_Predict_ByteIdentical`, `PredictSignal_WrongFeatureLength_FailsPrecondition`

## `NotificationHttpLoggingTests.cs` — 2 test, 87 righe

> Il token del bot Telegram sta nel PATH dell'URL (vincolo dell'API Telegram) e il logging di default di HttpClientFactory scrive l'URI completo a Information: questi test verificano che il client nominato "telegram-notifier" registrato da AddProcioneNotifications sia SENZA logger HTTP (RemoveAllLoggers), con un client di controllo che dimostra che la cattura funziona (il test non deve essere tautologico).

`TelegramNotifierClient_HasNoHttpLoggers_SoTheTokenNeverReachesTheLogs`, `ControlClient_DoesLogTheFullUrl_ProvingTheCaptureWorks`

## `ProtectiveExitMetricTests.cs` — 2 test, 265 righe

> [R1/R2] La metrica procione.trading.protective_exits è la PROVA del valore del feed real-time: confrontando source=tick con source=candle si vede quanto ritardo è stato tolto agli stop. Perché quel confronto significhi qualcosa, il conteggio deve registrare le uscite RIUSCITE, non i tentativi. La distinzione non è accademica. Una chiusura può fallire (rete incerta, rifiuto dell'exchange) lasciando la posizione aperta per il retry. Contando comunque, un ordine che continua a fallire registrerebbe a ogni valutazione — e sul percorso a tick, dove le valutazioni sono decine al secondo invece di una per candela, gonfierebbe di migliaia di conteggi proprio la metrica che serve a confrontare i due percorsi. La metrica direbbe "il tick funziona benissimo" esattamente quando il tick non sta riusce…

`SuccessfulTickExit_IsCountedOnce`, `FailedTickExit_IsNotCounted_EvenWhenRetriedManyTimes`

## `RegimeAutoKTests.cs` — 2 test, 109 righe

> R1.2 — robustezza del rilevamento regimi: auto-selezione di K per Silhouette (senza DB, solo ML.NET su matrice sintetica) e invariante anti-look-ahead del feature extractor (la feature alla candela i è identica sia sull'intera serie sia su una serie troncata dopo i).

`SelectBestK_PicksTrueClusterCount_OnWellSeparatedBlobs`, `ComputeFeatures_IsCausal_TruncationInvariant`

## `RestingBracketPersistenceTests.cs` — 2 test, 231 righe

> [M3] Persistenza degli id dei bracket "resting" ( / ): prima erano [NotMapped] — un riavvio perdeva i clientOrderId dei trigger REALI ancora armati sull'exchange, e la chiusura non poteva più cancellarli (ordini orfani reduce-only pronti a scattare su una posizione ormai chiusa). Vedi RestingStopOrderTests per la costruzione delle richieste lato client.

`BracketIds_PersistedOnPlacement_CancelUsesExactlyThoseIds`, `BracketIds_SurviveRestart`

## `SignalReversalThrottleTests.cs` — 2 test, 214 righe

> L'anti-spam n.6 del ( MinOrderIntervalSeconds ) deve frenare gli INGRESSI ravvicinati e nient'altro. PositionCloser segnava LastOrderUtc anche in chiusura, e siccome un'inversione di segnale chiude e riapre sullo STESSO timestamp di candela (vedi TradingEngine , casi Signal.Long / Signal.Short ), l'apertura opposta arrivava al controllo con elapsed = 0 e veniva rifiutata. Riguardava tutte e 12 le strategie del catalogo, ognuna delle quali può emettere un segnale opposto a quello in corso (osservato dal vivo: 430 ordini rifiutati su 500 — docs/REPORT-RICERCA-2026-07.md). I due test sono una coppia e vanno letti insieme: il primo verifica che l'inversione passi, il secondo che il freno sugli ingressi ravvicinati sia ancora lì. Da soli sarebbero entrambi soddisfatti da una regressione (rispe…

`SignalReversal_OpensOppositePosition_OnTheSameCandle`, `TwoEntriesOnTheSameCandle_SecondIsStillThrottled`

## `SymbolScanGuardTests.cs` — 2 test, 104 righe

> [E-04, Fase 2 PRD-RISANAMENTO] Il guardiano del catalogo simboli. La 2.1 aveva sostituito con ISymbolCatalog le scansioni OhlcvData…Select(Symbol)…Distinct() che ogni pagina rifaceva per conto proprio — una scansione solo-indice su ~12M righe per ~30 stringhe — ma QUATTRO copie erano sfuggite (FeatureSelection, e Backtest/Optimization/MlLab attraverso i loro page service, che il censimento per pagine non aveva contato), scoperte alla verifica browser del 2026-08-10 dai ~5 s di apertura di /feature-selection e /backtest. Questo test impedisce che il buco si riapra, come per i pannelli: scandisce i sorgenti e pretende che ogni scansione diretta o non esista, o sia iscritta nell'inventario qui sotto con la ragione per cui il catalogo non le basta.

`NoDirectSymbolScan_OutsideTheDeclaredAllowList`, `TheAllowListHasNoStaleEntries`

## `DormantFleetPromotionTests.cs` — 1 test, 84 righe

> [AF0] La flotta a 8 con cinque corsie DORMIENTI (registrate, mai avviate) attraversa la valutazione di promozione senza eccezioni e senza azioni: una corsia mai partita non ha metriche, e "nessuna metrica" deve restare "nessuna decisione", non un crash né — peggio — una promozione costruita su zeri.

`EightLaneFleet_FiveDormant_NoExceptions_NoActionsOnDormantLanes`

## `FeatureDriftMonitorTests.cs` — 1 test, 121 righe

> Test del : dato un modello i cui fattori sono calcolati su una finestra di training a BASSA volatilità (reference, letta dal DB) e su candele recenti ad ALTA volatilità (current), il fattore di volatilità realizzata deve risultare in drift. Verifica l'integrazione reale (ricostruzione fattori dal FactorsJson + detector).

`Evaluate_DetectsVolatilityDrift_BetweenTrainingAndRecent`

## `IndicatorsOnRealDataTests.cs` — 1 test, 94 righe

> Test richiesto dallo spec: calcola gli indicatori su dati reali BTC/USDT 1h presenti nel DB Postgres reale (procionemgr) e verifica invarianti strutturali. Se il DB o i dati non sono disponibili, il test viene saltato (non fallisce).

`Indicators_On_Real_BtcUsdt_RespectInvariants`

## `MlGrpcRoundTripTests.cs` — 1 test, 110 righe

> Prova che il valore attraversa la pipeline gRPC e la (de)serializzazione protobuf senza perdita: la predizione ricevuta sul wire è ESATTAMENTE uguale a quella calcolata in locale. Senza DB: l'IModelRegistry è sostituito con uno fake, la connection string è fittizia (Npgsql non si connette a startup e il registry fake non tocca il DB). ATTENZIONE — cosa questo test NON copre: WebApplicationFactory usa il TestServer in-memory, che NON passa da Kestrel. Il trasporto reale (h2c) qui non è esercitato, quindi questo test resta verde anche se gli endpoint Kestrel sono configurati male. È esattamente così che è sfuggito il bug HTTP_1_1_REQUIRED (0xd) corretto in ProcioneMGR.Ml/Program.cs: gli endpoint erano lasciati al default Http1AndHttp2 e in chiaro (senza ALPN) NESSUNA chiamata gRPC passava i…

`PredictSignal_OverRealGrpc_MatchesLocalExactly`

## `MlStrategySequenceTests.cs` — 1 test, 82 righe

> Verifica l'innesto dei modelli SEQUENZIALI (attention) nel backtest tramite : la strategia riconosce l' e costruisce la finestra degli ultimi T timestep a inferenza (nessun buffer stateful). Test di non-regressione del cablaggio: warm-up → Hold, nessuna eccezione lungo la serie, segnali deterministici. Assunzione (come per gli altri modelli): dopo il warm-up i fattori sono completi in modo contiguo, così la finestra costruita per indice di candela coincide col layout di training.

`MlStrategy_WithAttention_RunsAcrossSeries_WarmupIsHold`

## `ProviderCompatibilityTests.cs` — 1 test, 121 righe

> Verifica che i tipi "sensibili al provider" sopravvivano a un round-trip persistenza→reload SENZA perdita di informazione: blob binari (modelli ML), decimal ad alta precisione (prezzi crypto) e stringhe JSON. Gira su un database PostgreSQL effimero (Testcontainers), l'unico provider supportato.

`Postgres_PreservesBlobDecimalAndJson`

## `RealtimeSharedSymbolLanesTests.cs` — 1 test, 215 righe

> Regressione dell'incidente del 2026-08-09 (pod procionemgr-trading), stavolta con la catena VERA: due corsie persistite a DB sullo stesso simbolo facevano fallire il refresh delle sottoscrizioni di con «ArgumentException: An item with the same key has already been added. Key: DOTUSDT» — il log prometteva «ritento» ma il set non convergeva mai, e nessuna delle due corsie riceveva i tick. Qui si monta il worker reale su un Postgres reale (Testcontainers) con due in esecuzione sulla stessa coppia, e si verifica la proprietà che l'incidente aveva negato: il refresh converge senza errori ed ENTRAMBE le corsie ricevono lo stesso tick.

`TwoRunningLanesOnTheSameSymbol_BothReceiveTheTick_AndTheRefreshConverges`

## `StrategyDiscoveryDefaultsTests.cs` — 1 test, 43 righe

> Invariante di integrazione: OGNI strategia registrata in deve avere una griglia di default in , con nomi parametro esistenti nelle ParameterDefinitions della strategia. Senza questo, una nuova strategia appare selezionabile in Discovery ma lo sweep non produce nulla (bug reale trovato in revisione: DonchianBreakout e PriceSmaCross erano scoperti).

`EveryFactoryStrategy_HasDefaultRanges_WithValidParameterNames`

## `NewStrategiesTests.cs` — 0 test, 94 righe

> Verifica che le nuove strategie (MACD Trend, Bollinger Mean Reversion, Momentum) — e quelle esistenti — generino trade su dati reali BTC/USDT 1h. Saltato se il DB non è disponibile.
