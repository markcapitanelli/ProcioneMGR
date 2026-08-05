# 03 — Mappa del codice

Riferimenti verificati per navigare il progetto senza aprirlo a caso. Conteggi al 2026-08-04.

---

## Cartelle principali

| Percorso | File | Cosa contiene |
|---|---|---|
| `ProcioneMGR/Services/` | 384 `.cs` | tutto il dominio, in 38 sottocartelle |
| `ProcioneMGR/Components/` | 89 `.razor` + 8 `.cs` | UI |
| `ProcioneMGR/Data/` | 21 `.cs` | entità EF Core + `DbContext` |
| `ProcioneMGR.Tests/` | 259 `.cs` | suite di test |
| `ProcioneMGR.Migrations.Postgres/` | 40 `.cs` | migrazioni EF |
| `tools/` | 5 progetti | CLI operative |

Distribuzione di `Services/` (le 12 cartelle maggiori):

```
Trading 60 │ ML 37 │ Pipeline 28 │ Backtesting 24 │ Sentiment 21 │ Alpha 14
Exchanges 12 │ Llm 12 │ Regime 12 │ Monitoring 10 │ Ingestion 10 │ Validation 10
```

---

## `Services/Trading/` — 60 file, il modulo più grande

Il cuore operativo, e l'unico posto dove il codice può muovere soldi.

**Motore ed esecuzione**
- `TradingEngine.cs` (87,8 KB — **il file più grande del progetto**) — orchestratore per corsia
- `TradingWorker.cs` — loop periodico per corsia
- `RemoteTradingEngineClient.cs` — implementazione di `ITradingEngine` che parla gRPC al core caldo
- `ExecutionWorker.cs`, `ExecutionJobModels.cs` — piani di esecuzione a fette (TWAP/VWAP/Iceberg/Adaptive)

**Sicurezza (le barriere verso il denaro reale)**
- `SafetyChecker.cs` — **statico e puro**, invocato dentro il motore: non è iniettabile, quindi non
  è sostituibile né aggirabile da DI
- `SafetyConfiguration.cs` — limiti per corsia
- `PromotionEvaluator.cs` — decide la modalità suggerita; **non restituisce mai `Live`**
- `PromotionWorker.cs` — agisce solo su transizioni Paper↔Testnet
- `LanePromoter.cs` — solleva eccezione se gli si chiede di passare una corsia a Live
- `LaneQuarantineStore.cs`, `LaneInvariantChecker.cs`, `LaneInvariantWatchdog.cs`,
  `LaneInvariantOptions.cs` — quarantena per violazione di invarianti contabili
- `LaneSafetyMonitor.cs`, `LaneExecutionLease.cs` — lease per corsia (un solo scrittore)

**Sottocartella `Internal/` (13 file) — la meccanica fine**
`PositionOpener`, `PositionCloser`, `BracketOrderManager`, `ProtectiveExitEvaluator`,
`OrderReconciler`, `FuturesPositionReconciler`, `FillSanityCheck` (il fix del bug dei fill
patologici), `VolatilityScaler`, `ExecutionSlicePlanner`, `ExecutionQuality`, `SignalOrderBuilder`,
`AutoStopApplier`, `TradingPersistence`.

**CQRS** — `Commands/` (7: Start/StopLane, ClosePosition, ConfirmOrder, RejectOrder, EmergencyStop,
SetStopLossTakeProfit), `Queries/` (5: LaneStatus, OpenPositions, OrderHistory, PendingOrders,
Performance), `Behaviors/LoggingBehavior.cs`.

**Diagnostica** — `ProtectiveExitLagAnalyzer.cs`, `ProtectiveExitDiagnosticsService.cs`,
`ProtectiveExitShadow.cs`: misurano se uscire al tocco batte uscire a barra chiusa. Il risultato
misurato (uscire al tocco è **peggio**, 24/24 configurazioni) è il motivo per cui
`DriveProtectiveExits` resta `false`.

**Configurazione del motore** — `EngineConfigStore.cs`, `EngineConfigService.cs`,
`EngineConfigSections.cs`: da quando il motore è remoto, la configurazione si legge e si scrive via
gRPC, non più su file (`ISafetyConfigWriter` è stato rimosso —
[Program.cs:147](../../ProcioneMGR/Program.cs#L147)).

---

## `Services/Validation/` — 10 file, il gate anti-overfitting

Piccolo ma decisivo: è ciò che rende il progetto una macchina di misura invece che un generatore di
illusioni.

| File | Tecnica |
|---|---|
| `DeflatedSharpeRatio.cs` | Sharpe corretto per numero di trial (Bailey–López de Prado) |
| `BacktestOverfitting.cs` | Probability of Backtest Overfitting |
| `CombinatorialPurgedCv.cs` | molti percorsi OOS dallo stesso storico |
| `PermutationTest.cs` | significatività non parametrica |
| `NullTwinGenerator.cs` + `NullTwinJudge.cs` | "gemello sintetico": un edge finto per validare gli strumenti |
| `GatePowerAnalyzer.cs` | potenza statistica del gate |
| `MinTrackRecord.cs` | track record minimo necessario |
| `EffectiveTrials.cs` | numero effettivo di trial indipendenti |
| `SelectionValidator.cs` | validatore della selezione |

`PurgedTimeSeriesCv.cs` sta in `Services/ML/` insieme al resto della cross-validation.

---

## `Services/ML/` — 37 file

**Predittori** (interfaccia comune `IReturnPredictor`): `LinearReturnPredictor`,
`RandomForestReturnPredictor`, `GradientBoostingReturnPredictor` (LightGBM), `MlpReturnPredictor`
(rete C# pura), `AttentionReturnPredictor`, `StackedReturnPredictor`, con base condivisa
`RegressionPredictorBase` e catalogo `ReturnPredictorCatalog`.

**Dataset e feature**: `DatasetBuilder`, `IcFeatureSelector` (selezione per Information Coefficient),
`SequenceWindowing`, `MlTargetKind`.

**Etichettatura** `Labeling/`: `TripleBarrierLabeler`, `MetaLabeler`, `MetaModelTrainer`,
`MetaLabelingAnalysisService` — la catena completa segnali → etichette → meta-modello.

**Interpretabilità** `Shap/`: `TreeShapExplainer`, `ShapTree`, `MlNetTreeExtractor`, `ShapAnalysis`.

**Fattori di rischio**: `RiskFactorPca`, `HierarchicalClustering` (base di HRP).

---

## `Services/Backtesting/` — 24 file

`BacktestEngine.cs` (33,7 KB) + `IStrategy` con **14 strategie** concrete: Bollinger mean reversion,
composite signal, Donchian breakout, EMA cross, event trigger, grid mean reversion, MACD trend, ML,
momentum, price/SMA cross, regime-conditional, RSI oversold, stochastic, supertrend, VWAP reversion.
Creazione via `StrategyFactory` + `SignalCatalog`.

---

## `Services/Pipeline/` — 28 file

Motore di automazione end-to-end. `PipelineEngine` esegue stage risolti da `PipelineStageCatalog`,
con `PipelineDagValidator` a controllare il grafo. Gli stage stanno in `Stages/`: `DataStages`,
`AnalysisStages`, `ModelStages`, `DecisionStages`, `CreativeDiscoveryStage`,
`NullTwinValidationStage`, `PowerCheckStage`.

Automazione: `PipelineSchedulerWorker` (Cronos), `CampaignPlanner` + `CampaignPlannerWorker`
(politica di reazione agli esiti), `RegimeChangeTriggerWorker` (wake su cambio regime),
`RunApplyEvaluator` + `PipelineApplier` (catena "valuta e applica", condivisa con la ri-applica
automatica: **una sola implementazione**, non due).

---

## `Services/Llm/` — 12 file

- `ILlmClient` con `AnthropicLlmClient` e `NvidiaLlmClient`; `LlmClientResolver` sceglie il provider
  **per chiamata** (hot-reload), non al boot.
- `AiKeyStore` — chiavi cifrate su DB (`AiCredentials`), non da file.
- `LlmCallGuard` — breaker/limitatore.
- `ModelAutoSelector` — sceglie il modello disponibile.
- `PipelineSupervisor` + `LlmSupervisorWorker` — il supervisore **advisory/veto-only**.
- `Committee/AiCommittee.cs` — comitato multi-modello, default OFF.

**Confine di sicurezza esplicito** ([Program.cs:456-457](../../ProcioneMGR/Program.cs#L456)): questi
servizi leggono i run e scrivono un advisory; **non avviano trading, non passano in Live, non toccano
`SafetyChecker`** — nessun servizio di esecuzione è iniettato qui.

---

## `Services/Security/` — 6 file

- `AesGcmEncryptionService` — AES-256-GCM per i segreti a riposo
- `EncryptedStringConverter` — value converter EF: cifra prima che il dato tocchi il DB
- `ExchangeCredentialReader` — decifratura per-riga (fix B2)
- `MasterKeyProbe` — rileva la master key placeholder di sviluppo
- `DataProtectionSetup` — keyring persistito (serve dentro i container)

---

## `Services/Exchanges/` — 12 file

`IExchangeClient` implementato da `BinanceClient` (32,3 KB) e `BitgetClient` (41,7 KB), creati da
`ExchangeClientFactory`. `ExchangeRateLimitHandler` gestisce i rate limit, `ExchangeClock` la deriva
di orologio, `Timeframes` la normalizzazione dei timeframe.

---

## `Services/Fleet/` — 5 file

Orchestratore di flotta (Queen Bee): `FleetOrchestrator` (core deterministico puro),
`FleetStateReader` (sola lettura), `FleetOrchestratorWorker` (journal su `OrchestratorDecision`),
`GreyDeployer` (fascia grigia). Default OFF. Vincolo dichiarato: **non tocca mai** le corsie 0–2,
le corsie Live/Testnet, le quarantene o le campagne
([Program.cs:524-526](../../ProcioneMGR/Program.cs#L524)).

---

## Componenti UI principali

Le pagine più grandi — dove si concentra la complessità di presentazione:

| Pagina | KB |
|---|---|
| `Pages/Admin/Autonomy.razor` | **107,6** |
| `Pages/Trading.razor` | 68,9 |
| `Pages/Sentiment.razor` | 55,0 |
| `Pages/MlLab.razor` | 50,3 |
| `Pages/Admin/AiSupervisor.razor` | 47,7 |
| `Pages/Backtest.razor` | 43,5 |
| `Pages/Ensemble.razor` | 42,8 |

Sei di queste hanno l'orchestrazione estratta in un *page service* testabile (`TradingPageService`,
`MlLabService`, `BacktestPageService`, `OptimizationPageService`, `PipelinePageService`,
`EnsemblePageService`) — refactor P1-5, registrato scoped in
[Program.cs:543-571](../../ProcioneMGR/Program.cs#L543). `Autonomy.razor` e `Sentiment.razor`, le
altre due grandi, **non** hanno un page service equivalente.

---

## Utility trasversali

| File | Ruolo |
|---|---|
| `Services/Indicators/TechnicalIndicatorsService.cs` | indicatori tecnici (20,5 KB) |
| `Services/Trading/DecimalValueMapper.cs`, `TradingContractMapper.cs` | mapping fra modelli di dominio e messaggi gRPC |
| `Services/TimeSeries/` (8 file) | operazioni su serie |
| `Services/Config/` (2 file) | writer generalizzato di sezioni appsettings |
| `Services/Preferences/` (1) | preset di pagina per utente |

---

## Relazioni fra moduli

Il flusso di dipendenza dominante è **a strati, in una direzione sola**:

```
Components/*.razor
      ↓ (page service o servizio di dominio)
Services/<dominio>
      ↓
Services/Exchanges  +  Data/ApplicationDbContext
      ↓
PostgreSQL / API esterne
```

Punti di incrocio degni di nota:

- **`Data/ApplicationDbContext` dipende da `Services/Security/IEncryptionService`** — il DbContext
  riceve il servizio di cifratura nel costruttore per alimentare l'`EncryptedStringConverter`. È una
  dipendenza dal basso verso l'alto rispetto alla stratificazione consueta, ed è deliberata; obbliga
  a iniettare `IEncryptionService` anche a design-time (`DesignTimeDbContextFactory`), altrimenti
  `dotnet ef` non parte.
- **`Services/Pipeline` → `Services/Backtesting`, `ML`, `Validation`, `Trading`** — la pipeline è il
  consumatore trasversale che tira dentro quasi tutto.
- **`Services/Trading` ↔ `ProcioneMGR.Contracts`** — quando `UseRemoteTrading=true`,
  `RemoteTradingEngineClient` sostituisce `TradingEngine` dietro la stessa interfaccia
  `ITradingEngine`. La UI non sa quale delle due sta usando.
- **`AddTradingLanes` è condivisa verbatim** fra l'app Blazor e l'host `ProcioneMGR.Trading`
  ([Program.cs:407](../../ProcioneMGR/Program.cs#L407)): stessa composizione, due processi.

## Dipendenze circolari

**Non ne ho trovate a livello di progetto.** Il grafo delle reference è aciclico:
`Ingestion`/`Ml`/`Trading` → `Contracts`; l'app principale → `Contracts` + `Migrations.Postgres`.

A livello di namespace non ho eseguito un'analisi automatica: **DA VERIFICARE** con uno strumento
dedicato (es. NDepend o `dotnet-depends`) se si vuole una risposta rigorosa dentro `Services/`.

## Criticità strutturali

1. **`TradingEngine.cs` a 87,8 KB** è un single point of complexity. È mitigato dalla sottocartella
   `Internal/` che ne ha estratto 13 collaboratori, ma il file resta il doppio del secondo classificato.
2. **`Program.cs` a 674 righe** concentra tutta la composizione. Molto commentato — i commenti
   spiegano il *perché*, non il *cosa*, il che è la scelta giusta — ma è un file che va letto per
   intero per capire cosa gira.
3. **`Autonomy.razor` a 107,6 KB** è la pagina più grande e non ha page service: la logica di
   orchestrazione vive nel markup.
