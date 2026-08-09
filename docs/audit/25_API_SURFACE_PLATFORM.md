# 25 — SUPERFICIE API: piattaforma, automazioni e UI

> Estrazione **esaustiva e meccanica** della superficie API dal sorgente: nessun campione,
> nessuna parafrasi. Ogni tipo e ogni membro pubblico (o di interfaccia) con la firma reale
> e il doc-comment che il codice gli associa.

Pipeline autonoma, layer AI, sentiment, dati, monitoraggio, notifiche, osservabilità, registry, esperimenti, configurazione, UI e strumenti CLI.

| | |
|---|---:|
| File coperti | 153 |
| Tipi | 356 |
| Membri (metodi, proprietà, costruttori, costanti) | 1435 |

**Legenda:** 🔌 interface · 📦 class · 🧾 record · 🔢 enum · ▫️ struct · `m` metodo · `p` proprietà · `c` costruttore · `k` costante

---

# `Services/Pipeline/`

## `ProcioneMGR/Services/Pipeline/AutoBracket.cs`

### 📦 `AutoBracket`

> Bracket SL/TP data-driven per (symbol, timeframe), estratto da PipelineApplier perché ora ha DUE consumatori (l'applica della raccomandazione e lo schieramento dei candidati grigi) e la regola della piattaforma è una sola implementazione, nessuna deriva. Primario (R1.5): MAE/MFE sull'orizzonte di detenzione condizionato al regime di volatilità corrente ( ), media dei bracket long/short per un livello simmetrico. Fallback: escursioni a barra singola (95° percentile) quando il campionamento sull'orizzonte è troppo rado. (0,0) se i dati non bastano: chi schiera decide se procedere senza protezioni o fermarsi.

## `ProcioneMGR/Services/Pipeline/CampaignEntities.cs`

### 📦 `VettingCampaign`

> Campagna di vaglio (Fase 1, PRD Autonomia Operativa §4): un elenco ORDINATO di configurazioni di caccia ( ) che il ruota da solo — "0 sopravvissuti" non è più un punto morto ma un input per la mossa successiva. La campagna decide COSA fare dopo un run; il motore pipeline resta intoccato (si aggiunge SOPRA, mai DENTRO). SAFETY: doppio gate — la campagna agisce solo se Campaign:Enabled (globale, default OFF) E (per campagna) sono veri. L'applica passa dalla STESSA catena della ri-applica automatica (supervisore con veto + isteresi); le corsie si avviano al massimo in Paper (Testnet nel planner è nel backlog §8 del PRD, Live MAI per costruzione).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `string Name` | — |
| `p` | `string CreatedBy` | Id dell'IdentityUser che ha creato la campagna (usato come userId dei run avviati). |
| `p` | `bool Enabled` | Gate per-campagna (oltre a quello globale Campaign:Enabled ). |
| `p` | `string Status` | Vedi : "Rotating" \| "Observing" \| "WaitingForTrigger". |
| `p` | `string ConfigStatesJson` | JSON: List&lt; &gt; — la rotazione ordinata con lo stato per config. |
| `p` | `int BackoffHours` | Backoff: la stessa config non si ripete prima di N ore (un wake del trigger lo bypassa). |
| `p` | `bool AutoStartPaperLanes` | Se true, dopo un'applica riuscita il planner AVVIA in Paper le corsie appena configurate (solo quelle ferme: una corsia già in esecuzione — o in quarantena — non viene mai toccata). |
| `p` | `Guid? PendingRunId` | Run avviato dalla campagna e non ancora valutato (slot singolo per campagna). |
| `p` | `int ObservedLanes` | Corsie configurate dall'ultima applica riuscita (lo "stato ATTESO di flotta" per il riallineamento post-riavvio, Fase 3-C3): in osservazione, le corsie 0..N-1 dovrebbero essere in esecuzione. 0 = nessuna applica ancora … |
| `p` | `string? PendingWakeReason` | Motivo del "wake" chiesto da un trigger contestuale (Fase 2) e non ancora consumato: il prossimo run parte subito (backoff bypassato) con trigger "Event". |
| `p` | `string? LastOutcome` | Ultima decisione presa dal planner, leggibile (per UI e notifiche). |
| `p` | `DateTime? LastActionAtUtc` | — |
| `p` | `DateTime CreatedAtUtc` | — |
| `p` | `DateTime UpdatedAtUtc` | — |

### 📦 `CampaignStatus`

> Stati di una campagna. Stringhe (non enum) per lo stesso motivo di PipelineRun.Status.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string Rotating` | Sta ruotando le config di caccia in cerca di sopravvissuti. |
| `k` | `string Observing` | Ensemble schierato: rotazione ferma, osservazione (decay monitor / promozioni). |
| `k` | `string WaitingForTrigger` | Rotazione esaurita senza sopravvissuti: in attesa di un trigger contestuale (Fase 2). |

### 📦 `CampaignConfigState`

> Stato per-config dentro (ordine = ordine di rotazione).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int ConfigurationId` | — |
| `p` | `Guid? LastRunId` | — |
| `p` | `DateTime? LastRunAtUtc` | — |
| `p` | `string? LastOutcome` | "NoSurvivors" \| "Applied" \| "NotApplied" \| "Failed" (null = mai eseguita in questo ciclo). |
| `p` | `int Attempts` | — |

## `ProcioneMGR/Services/Pipeline/CampaignOptions.cs`

### 📦 `CampaignOptions`

> Opzioni del Campaign Planner (Fase 1, PRD Autonomia Operativa §4), sezione Campaign .

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Gate GLOBALE del planner. DEFAULT false (è IL cambio di natura da strumento ad agente: l'attivazione è una decisione esplicita dell'operatore, come da PRD §4). Hot-reload. |
| `p` | `int TickSeconds` | Cadenza del tick del worker (letta all'avvio; cambiarla richiede riavvio). |

## `ProcioneMGR/Services/Pipeline/CampaignPageService.cs`

### 📦 `CampaignPageService` `(`

> Orchestrazione di Components/Pages/Campaign.razor (stesso pattern P1-5 delle altre pagine: la logica sta qui, testabile senza Blazor; il componente fa solo rendering). Registrato Scoped (un'istanza per circuito utente).

| | Firma | Descrizione |
|---|---|---|
| `p` | `List&lt;VettingCampaign&gt; Campaigns` | — |
| `p` | `List&lt;PipelineConfiguration&gt; Configs` | — |
| `p` | `string? Message` | — |
| `p` | `bool IsError` | — |
| `p` | `bool GloballyEnabled` | Gate globale (Campaign:Enabled): se spento, il planner non agisce qualunque cosa dica la campagna. |
| `m` | `Task RefreshAsync()` | — |
| `m` | `string ConfigName(int configurationId)` | — |
| `m` | `List&lt;CampaignConfigState&gt; StatesOf(VettingCampaign campaign)` | — |
| `m` | `Task CreateAsync(string name, IReadOnlyList&lt;int&gt; configurationIds, int backoffHours, bool autoStartPaper, string? userId)` | — |
| `m` | `Task SetEnabledAsync(int campaignId, bool enabled)` | — |
| `m` | `Task WakeAsync(int campaignId, string? userId)` | Riporta in rotazione una campagna in attesa/osservazione (gesto esplicito dell'operatore, bypassa il backoff). |
| `m` | `Task DeleteAsync(int campaignId)` | — |
| `m` | `Task TickNowAsync()` | Tick manuale del planner (utile per non aspettare il prossimo giro del worker). |

## `ProcioneMGR/Services/Pipeline/CampaignPlanner.cs`

### 🔌 `ICampaignPlanner`

> Il Campaign Planner (Fase 1, PRD Autonomia Operativa §4): la politica di reazione agli esiti dei run. La pipeline conclude onestamente "0 sopravvissuti" e SI FERMA; nella sessione di esercizio 2026-07-18 la mossa successiva l'ha decisa ogni volta l'operatore. Questo servizio prende ESATTAMENTE quelle decisioni, sopra il motore (mai dentro): - 0 sopravvissuti → prossima config della rotazione, con backoff (la stessa config non si ripete prima di N ore; un "wake" di un trigger contestuale bypassa il backoff); - sopravvissuti &gt; 0 → STESSA catena della ri-applica automatica (supervisore con veto + isteresi + via ); se schierato: rotazione ferma, stato "Observing", corsie avviate in Paper (solo quelle ferme, mai Live); se NON schierato (veto/isteresi): la caccia continua — scostamento deliberato dal PRD §4, che fermava la rotazione su qualunque sopravvissuto: fermarsi senza aver schierato…

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task TickAsync(CancellationToken ct = default)` | Un giro di decisioni su tutte le campagne abilitate. Chiamato dal worker; pubblico per test. |
| `m` | `Task&lt;int&gt; WakeAsync(string reason, CancellationToken ct = default)` | Un trigger contestuale (Fase 2) chiede di anticipare la prossima esecuzione: le campagne in rotazione o in attesa tornano eleggibili SUBITO (backoff bypassato) e il prossimo run parte con trigger "Event". Le campagne in… |

### 📦 `CampaignPlanner` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task TickAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;int&gt; WakeAsync(string reason, CancellationToken ct = default)` | — |
| `m` | `List&lt;CampaignConfigState&gt; ParseConfigStates(string json)` | — |
| `m` | `string SerializeConfigStates(List&lt;CampaignConfigState&gt; states)` | — |

## `ProcioneMGR/Services/Pipeline/CampaignPlannerWorker.cs`

### 📦 `CampaignPlannerWorker` `(`

> Worker del Campaign Planner (Fase 1, PRD Autonomia): loop sottile sul col pattern PeriodicTimer degli altri worker. Il gate Campaign:Enabled è dentro (hot-reload): col default OFF questo worker gira a vuoto e la piattaforma si comporta esattamente come prima.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

## `ProcioneMGR/Services/Pipeline/IPipelineEngine.cs`

### 🔌 `IPipelineEngine`

> Orchestrates pipeline runs: validates the stage DAG, executes the enabled stages in order in the background, checkpoints the context to the DB after every stage, and exposes a live status for the UI. One run at a time (the underlying engines are heavy).

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;Guid&gt; StartRunAsync(int configurationId, string trigger = "Manual", string? userId = null, CancellationToken ct = default)` | Starts a new run of a saved configuration. Throws if a run is already in progress. |
| `m` | `Task&lt;Guid&gt; ResumeRunAsync(Guid runId, string? userId = null, CancellationToken ct = default)` | Resumes a Paused/Failed/Cancelled run from its last checkpoint: already-completed stages are skipped, the rest re-execute against the restored context. |
| `m` | `void RequestPause(Guid runId)` | Requests a graceful pause: the run stops at the NEXT stage boundary (checkpoint intact). |
| `m` | `void Cancel(Guid runId)` | Cancels the in-progress run (checkpoint of completed stages is preserved). |
| `m` | `PipelineLiveStatus? GetLiveStatus()` | Live status of the in-progress run, or null when idle. |
| `m` | `List&lt;string&gt; ValidateConfiguration(IReadOnlyList&lt;StageConfig&gt; stages)` | Validates a configuration's stage list against the DAG rules (dependencies satisfied by enabled stages ordered earlier). Returns the list of problems (empty = valid). |
| `m` | `Task&lt;int&gt; RecoverOrphanedRunsAsync(CancellationToken ct = default)` | Recovers runs orphaned by a process restart: rows still marked "Running" on the DB when no run can possibly be executing (the live slot is in-memory only, so after a restart any "Running" row is a leftover of the previo… |

### 🔌 `IPipelineStageCatalog`

> Catalog of all available stages (prototypes for the UI, factory for the engine).

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;IPipelineStage&gt; Prototypes` | Prototype instances (read Name/DisplayName/ParameterDefinitions — do not execute). |
| `m` | `IPipelineStage Create(IServiceProvider scopedProvider, string name)` | Resolves a fresh stage instance by technical name from the given scope. |
| `m` | `List&lt;StageConfig&gt; DefaultStages()` | Default stage list for a brand-new configuration (all stages, default order/params). |

## `ProcioneMGR/Services/Pipeline/IPipelineStage.cs`

### 🔌 `IPipelineStage`

> A composable phase of the autonomous pipeline. Stages are thin orchestrators over the existing platform services (they never re-implement them): each one reads its inputs from the , calls the underlying services, and writes its output back into the context for the following stages. Contract: - stateless: any per-run state lives in the context, so stages can be transient; - deterministic: same context + config + data → same output (seeded randomness only); - no look-ahead: date ranges from must be respected — the holdout range is verdict-only and may be read exclusively by validation stages.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | Technical name, stable across versions (used in StageConfig.Type and dependencies). |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | One-line description shown in the UI editor. |
| `p` | `int DefaultOrder` | Default position in a new configuration. |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | Dependency groups: each group requires at least one of its stages to be enabled and ordered before this one. Checked by the engine before the run starts (DAG validation). |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | Editable parameters (defaults + hints) for the UI gear editor. |
| `m` | `string? ValidateInput(PipelineContext ctx)` | Runtime prerequisite check against the actual context (e.g. "no candidates to validate"). Returns null when OK, otherwise a human-readable error. |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | Executes the stage, writing its output into the context. |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | Builds the textual + metric summary from the output this stage wrote into the context. Called by the engine right after completes. |

## `ProcioneMGR/Services/Pipeline/PipelineApplier.cs`

### 🔌 `IPipelineApplier`

> Deploys a onto the isolated trading lanes (0..LaneCount-1), with the exact validated per-leg parameters (from BestStopVariant ) plus a data-driven SL/TP bracket. Extracted verbatim from Pipeline.razor so the SAME apply path is used by both the manual "Applica al Trading" button and the automatic re-apply loop in — one implementation, no drift. SAFETY: this only writes ensemble CONFIGURATION (per-lane ); it never starts trading, never opens a position, never switches to Live. Starting a lane is always a separate, explicit action from /trading (Paper), and real execution stays behind SafetyChecker + manual confirmation.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LaneCount` | Number of lanes the automatic distribution spreads legs across. Deliberately the HISTORICAL footprint (3), NOT the physical fleet size ( ): growing the fleet (AF0) must not silently widen what a scheduled re-apply overw… |
| `m` | `Task&lt;ApplyResult&gt; ApplyRecommendationAsync(PipelineRecommendation recommendation, CancellationToken ct = default)` | Distributes the recommendation's legs across the lanes. Returns a report (lanes used, overflow, message). |
| `m` | `Task&lt;ApplyResult&gt; ApplyRunAsync(Guid runId, CancellationToken ct = default)` | Loads a completed run's recommendation from the DB and applies it. Throws if the run/recommendation is missing. |
| `m` | `Task&lt;EnsembleSummary&gt; GetCurrentEnsembleSummaryAsync(CancellationToken ct = default)` | Snapshot of the ensemble currently deployed across all lanes (for comparison against a candidate). |
| `m` | `EnsembleSummary SummarizeRecommendation(PipelineRecommendation recommendation)` | Compact, comparable snapshot of a recommendation (the candidate ensemble). |

### 📦 `ApplyResult`

> Outcome of an apply operation (for the UI message + the scheduler audit log).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LanesUsed` | — |
| `p` | `int Overflow` | — |
| `p` | `List&lt;string&gt; Deployed` | — |
| `p` | `string Message` | — |

### 📦 `PipelineApplier` `(`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LaneCount` | — |
| `m` | `Task&lt;ApplyResult&gt; ApplyRunAsync(Guid runId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;ApplyResult&gt; ApplyRecommendationAsync(PipelineRecommendation recommendation, CancellationToken ct = default)` | — |
| `m` | `Task&lt;EnsembleSummary&gt; GetCurrentEnsembleSummaryAsync(CancellationToken ct = default)` | — |
| `m` | `EnsembleSummary SummarizeRecommendation(PipelineRecommendation recommendation)` | — |

## `ProcioneMGR/Services/Pipeline/PipelineCandleCache.cs`

### 📦 `PipelineCandleCache` `(IDbContextFactory&lt;ApplicationDbContext&gt; dbFactory) : IPipelineCandleCache`

> Per-run candle cache: every (symbol, timeframe, from, to) window is loaded from the DB at most once. Instantiated fresh for each run by the engine — the cache lifetime IS the run lifetime, so a resumed run rereads current DB data (which is what we want: candles are the source of truth, not part of the checkpoint).

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyList&lt;OhlcvData&gt;&gt; GetAsync(string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct)` | — |

## `ProcioneMGR/Services/Pipeline/PipelineDagValidator.cs`

### 📦 `PipelineDagValidator`

> Pure DAG validation of a stage list: every enabled stage's dependency groups must be satisfied by at least one enabled stage ordered BEFORE it. Static and side-effect free so it is directly unit-testable (the engine and the UI both delegate here).

## `ProcioneMGR/Services/Pipeline/PipelineEngine.cs`

### 📦 `PipelineEngine` `(`

> Singleton run orchestrator. One run at a time (the underlying discovery/backtest engines are CPU-heavy); executes in the background on a dedicated scope, checkpoints the context to the DB after every stage, supports graceful pause (at stage boundaries), cancellation, and resume-from-checkpoint. Live progress is polled by the UI (2s timer, same pattern as /trading — Blazor Server already streams the UI over SignalR, a dedicated hub would add moving parts without adding capability).

| | Firma | Descrizione |
|---|---|---|
| `m` | `PipelineLiveStatus? GetLiveStatus()` | — |
| `m` | `List&lt;string&gt; ValidateConfiguration(IReadOnlyList&lt;StageConfig&gt; stages)` | — |
| `m` | `Task&lt;Guid&gt; StartRunAsync(int configurationId, string trigger = "Manual", string? userId = null, CancellationToken ct = default)` | — |
| `m` | `Task&lt;Guid&gt; ResumeRunAsync(Guid runId, string? userId = null, CancellationToken ct = default)` | — |
| `m` | `Task&lt;int&gt; RecoverOrphanedRunsAsync(CancellationToken ct = default)` | — |
| `m` | `void RequestPause(Guid runId)` | — |
| `m` | `void Cancel(Guid runId)` | — |

## `ProcioneMGR/Services/Pipeline/PipelineEntities.cs`

### 📦 `PipelineConfiguration`

> A saved, reusable pipeline configuration ("recipe"): universe, date ranges, and the ordered list of stages with their parameters. JSON columns keep the schema stable while stages and parameters evolve (same pattern as EnsembleState / SavedStrategy.ParametersJson).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `string Name` | — |
| `p` | `string Description` | — |
| `p` | `string CreatedBy` | Id of the IdentityUser that owns the configuration. |
| `p` | `DateTime CreatedAt` | — |
| `p` | `DateTime UpdatedAt` | — |
| `p` | `string ExchangeName` | Exchange the whole pipeline reads data from. |
| `p` | `string UniverseJson` | JSON: List&lt;SeriesSpec&gt;. |
| `p` | `string DateRangesJson` | JSON: PipelineDateRanges. |
| `p` | `string StagesJson` | JSON: List&lt;StageConfig&gt;. |
| `p` | `decimal InitialCapital` | — |
| `p` | `int Seed` | Seed for deterministic runs. |
| `p` | `string ExecutionMode` | "Paper" \| "Live" \| "Disabled". Live never bypasses SafetyChecker / manual confirms. |
| `p` | `string? Schedule` | Standard 5-field cron expression (e.g. "0 3 * * *" = every day at 03:00 UTC), evaluated by . Null/empty = no automatic schedule. |
| `p` | `bool ScheduleEnabled` | Master on/off switch for automatic scheduling, independent of whether is set — lets the user pause automation without losing the expression. |
| `p` | `DateTime? NextRunAt` | Next due UTC timestamp per , maintained by . Null means "due now" (never scheduled yet, or schedule just changed) — the worker computes a real |

### 📦 `PipelineRun`

> One execution of a configuration. The context snapshot is the checkpoint: it is rewritten after every completed stage, so a Failed/Cancelled/Paused run can resume from the last completed stage instead of starting over.

| | Firma | Descrizione |
|---|---|---|
| `p` | `Guid Id` | — |
| `p` | `int ConfigurationId` | — |
| `p` | `DateTime StartedAt` | — |
| `p` | `DateTime? CompletedAt` | — |
| `p` | `string Status` | "Running" \| "Completed" \| "Failed" \| "Cancelled" \| "Paused". |
| `p` | `string Trigger` | "Manual" \| "Scheduled" \| "Campaign" (rotazione del Campaign Planner, Fase 1) \| "Event" (trigger contestuale, Fase 2). |
| `p` | `string ContextSnapshotJson` | JSON: the serializable part of PipelineContext (checkpoint, updated per stage). |
| `p` | `string StageSummariesJson` | JSON: List&lt;StageSummary&gt; (denormalized copy for fast history queries). |
| `p` | `string Conclusion` | Executive conclusion produced by the RecommendationStage. |
| `p` | `string RecommendationJson` | JSON: PipelineRecommendation. |
| `p` | `string? ErrorLog` | — |

### 📦 `PipelineArtifact`

> Large per-stage artifacts (equity curves, trade lists, importances) kept OUT of the run row so the history table stays fast to query.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `Guid RunId` | — |
| `p` | `string StageName` | — |
| `p` | `string Kind` | "EquityCurve" \| "TradeList" \| "FeatureImportance" \| "RegimeProfile" \| ... |
| `p` | `string PayloadJson` | JSON payload (shape depends on Kind). |
| `p` | `DateTime CreatedAt` | — |

## `ProcioneMGR/Services/Pipeline/PipelineModels.cs`

### 📦 `SeriesSpec`

> One (symbol, timeframe) entry of the pipeline universe.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |

### 📦 `PipelineDateRanges`

> Date ranges of a pipeline run. Selection = where every decision is allowed to look; Holdout = verdict-only, never used for any choice (same discipline as the strategy-hunt campaigns: the holdout exists to catch overfitting, so nothing may peek at it).

| | Firma | Descrizione |
|---|---|---|
| `p` | `DateTime SelectionFrom` | — |
| `p` | `DateTime SelectionTo` | — |
| `p` | `DateTime HoldoutFrom` | — |
| `p` | `DateTime HoldoutTo` | — |

### 📦 `StageConfig`

> Per-stage configuration inside a pipeline configuration (JSON column).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Type` | Technical stage name, matches . |
| `p` | `int Order` | — |
| `p` | `bool Enabled` | — |
| `p` | `Dictionary&lt;string, string&gt; Parameters` | Stage-specific parameters as strings (invariant culture); typed access via . Kept as strings so the JSON round-trips losslessly and the UI can edit any parameter generically. |

### 📦 `StageConfigExtensions`

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal GetDecimal(this StageConfig cfg, string key, decimal fallback)` | — |
| `m` | `int GetInt(this StageConfig cfg, string key, int fallback)` | — |
| `m` | `bool GetBool(this StageConfig cfg, string key, bool fallback)` | — |
| `m` | `string GetString(this StageConfig cfg, string key, string fallback)` | — |
| `m` | `List&lt;string&gt; GetList(this StageConfig cfg, string key)` | Comma-separated list parameter ("A,B,C"); empty string = empty list. |

### 🧾 `StageParameterDefinition` `(string Key, string Label, string DefaultValue, string Hint);`

> Definition of a stage parameter, for the generic gear-icon editor in the UI.

### ▫️ `PipelineCosts` `(decimal SlippagePercent, decimal FeePercent, decimal FundingRatePercentPer8h)`

> Costi di trading applicati ai backtest della pipeline, letti una volta dallo e replicati su OGNI di valutazione dei candidati. I default rispecchiano il venue reale (Bitget): fee taker (conservativa) + slippage realistico + funding dei perpetual. Il funding in particolare era assente (default 0 in BacktestConfiguration): senza, una strategia che tiene posizioni attraverso le finestre di funding appare più redditizia di quanto sarà live. La validazione gira a leva 1, ma il rapporto funding/PnL è leva-invariante: valida quindi correttamente l'edge al netto del funding.

| | Firma | Descrizione |
|---|---|---|
| `k` | `decimal DefaultSlippagePercent` | — |
| `k` | `decimal DefaultFeePercent` | — |
| `k` | `decimal DefaultFundingRatePercentPer8h` | — |
| `m` | `PipelineCosts FromConfig(StageConfig config)` | — |
| `m` | `BacktestConfiguration ApplyTo(BacktestConfiguration cfg)` | Applica i costi a una configurazione di backtest (in-place) e la restituisce, per l'uso fluido. |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | Parametri UI condivisi dei costi, da innestare nelle ParameterDefinitions di ogni stage. |

### 🧾 `StageDependency` `(IReadOnlyList&lt;string&gt; AnyOf)`

> A dependency group: the stage requires AT LEAST ONE of the listed stages to be enabled and ordered before it (e.g. HoldoutValidation needs StrategyDiscovery OR MlModelTraining).

| | Firma | Descrizione |
|---|---|---|
| `m` | `StageDependency On(params string[] stages)` | — |

### 🔌 `IPipelineCandleCache`

> Lazy candle loader shared by all stages of a run. Candles live in the DB and are NOT part of the checkpoint snapshot (they would dwarf it); a resumed run reloads them on demand.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyList&lt;OhlcvData&gt;&gt; GetAsync(string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct)` | — |

### 📦 `PipelineContext`

> Transient state of a pipeline run. Everything except and is JSON-serializable: the engine snapshots it to the DB after every completed stage (checkpoint), so a run can resume from the last completed stage.

| | Firma | Descrizione |
|---|---|---|
| `p` | `Guid RunId` | — |
| `p` | `string ExchangeName` | — |
| `p` | `List&lt;SeriesSpec&gt; Universe` | — |
| `p` | `PipelineDateRanges Ranges` | — |
| `p` | `decimal InitialCapital` | — |
| `p` | `int Seed` | Seed of the whole run: same seed + config + data = same output. |
| `p` | `string? UserId` | Owner of the run (used e.g. to persist SavedMlModel rows, which require a user FK). |
| `p` | `string ExecutionMode` | "Paper" \| "Live" \| "Disabled" — from the configuration; consumed by ExecutionPlanStage. |
| `p` | `DataIngestionOutput? DataStatus` | — |
| `p` | `PowerCheckOutput? Power` | — |
| `p` | `AltDataOutput? AltData` | — |
| `p` | `FeatureSelectionOutput? Features` | — |
| `p` | `RegimeOutput? Regimes` | — |
| `p` | `VolatilityOutput? Volatility` | — |
| `p` | `PairsOutput? Pairs` | — |
| `p` | `MlTrainingOutput? MlTraining` | — |
| `p` | `List&lt;DiscoveryCandidate&gt; Candidates` | — |
| `p` | `List&lt;ValidatedCandidate&gt; Validated` | — |
| `p` | `EnsembleProposal? Ensemble` | — |
| `p` | `RiskAssessment? Risk` | — |
| `p` | `NewsImpactOutput? NewsImpact` | — |
| `p` | `PipelineRecommendation? Recommendation` | — |
| `p` | `ExecutionPlan? Plan` | — |
| `p` | `List&lt;StageSummary&gt; StageSummaries` | — |
| `p` | `IPipelineCandleCache Candles` | — |
| `p` | `Action&lt;string&gt;? Log` | — |
| `m` | `void LogLine(string message)` | — |
| `p` | `SeriesSpec PrimarySeries` | First series of the universe: the "primary" one for single-series stages (regime, vol, news impact). |

### 📦 `SeriesDataStatus`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `int CandleCount` | — |
| `p` | `DateTime? FirstUtc` | — |
| `p` | `DateTime? LastUtc` | — |
| `p` | `bool CoversSelection` | — |
| `p` | `bool CoversHoldout` | — |

### 📦 `PowerSeriesEntry`

> [F4] Potenza statistica del run, per serie: quale Sharpe può SUPERARE i gate qui.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `int HoldoutObservations` | Osservazioni attese nell'holdout (le T su cui il DSR giudica). |
| `p` | `double NullBenchmarkSharpe` | SR* per-periodo: fin dove arriva il puro caso con N tentativi (E[max] sotto il nullo). |
| `p` | `double MinDetectableAnnualizedSharpe` | Sharpe ANNUALIZZATO minimo perché un candidato possa passare il gate su questa serie. |

### 📦 `PowerCheckOutput`

> [F4] Esito del power check MinTRL (Bailey-LdP): l'aritmetica del run, detta PRIMA dei backtest.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int TrialsAssumed` | — |
| `p` | `double Confidence` | — |
| `p` | `List&lt;PowerSeriesEntry&gt; Series` | — |
| `p` | `double WorstMinDetectableAnnualizedSharpe` | Il caso peggiore fra le serie: il numero da guardare. |
| `p` | `bool Underpowered` | True se il check ritiene il run sotto potenza rispetto al tetto plausibile configurato. |

### 📦 `DataIngestionOutput`

| | Firma | Descrizione |
|---|---|---|
| `p` | `List&lt;SeriesDataStatus&gt; Series` | — |
| `p` | `long CandlesIngested` | — |

### 📦 `AltDataOutput`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int InsertedCount` | — |
| `p` | `int NewsLast24h` | — |
| `p` | `double AvgSentimentLast24h` | — |
| `p` | `ProcioneMGR.Services.Sentiment.SentimentSnapshot? Snapshot` | Snapshot composite del market mood (Sentiment 2.0): per-mercato e per-simbolo, con z-score e flag contrarian. Nullable per compatibilità: i checkpoint dei run vecchi non ce l'hanno, e uno snapshot assente non deve mai f… |

### 📦 `FactorIcSummary`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string FactorName` | — |
| `p` | `string DisplayName` | — |
| `p` | `double InformationCoefficient` | — |
| `p` | `double RollingIcMean` | — |
| `p` | `double InformationRatio` | — |
| `p` | `int Observations` | — |
| `p` | `double IcTStatistic` | t-statistic dell'IC con SE Newey-West (robusta all'overlap dei forward-return). \|t\| ≳ 2 ≈ significativo. |
| `p` | `bool Selected` | — |

### 📦 `FeatureSelectionOutput`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `int ForwardHorizon` | — |
| `p` | `List&lt;FactorIcSummary&gt; Factors` | — |
| `p` | `List&lt;string&gt; SelectedFactorNames` | Names of the top-K factors kept as ML features. |

### 📦 `RegimeOutput`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int CurrentRegimeId` | — |
| `p` | `string CurrentRegimeLabel` | — |
| `p` | `double SilhouetteScore` | — |
| `p` | `bool TrainedNewModel` | — |
| `p` | `List&lt;RegimeProfile&gt; Profiles` | — |

### 📦 `VolatilityOutput`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Symbol` | — |
| `p` | `double Omega` | — |
| `p` | `double Alpha` | — |
| `p` | `double Beta` | — |
| `p` | `double Persistence` | — |
| `p` | `double CurrentVolatility` | Current per-period conditional volatility (stddev, not variance). |
| `p` | `double LongRunVolatility` | Long-run per-period volatility implied by the model. |
| `p` | `double ForecastVolatility24` | Forecast per-period volatility 24 steps ahead. |
| `p` | `string Level` | "Bassa" / "Media" / "Alta" vs the long-run level (thresholds from pipeline rules). |
| `p` | `double? TailDegreesOfFreedom` | Gradi di libertà ν stimati con innovazioni Student-t (null se il fit di coda non è disponibile). ν basso = code grasse. Rif. audit 2026-07 §4. |
| `p` | `double ForecastTailMove99` | Mossa avversa all'1% (VaR di coda) prevista a orizzonte, consapevole delle code grasse (quantile Student-t su σ previsto). Come frazione di prezzo, sempre ≥ del corrispettivo gaussiano. Serve da distanza di stop prudent… |
| `p` | `string ForecastSource` | [C3] Chi ha prodotto la previsione che classifica il Level: "har-log-rv" (log-HAR sulla varianza realizzata dai 5m — gate C3 passato 24/24 sul set di conferma) oppure "garch" (fallback quando i 5m non bastano, e comport… |

### 📦 `PairScreenResult`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string SymbolY` | — |
| `p` | `string SymbolX` | — |
| `p` | `string Timeframe` | — |
| `p` | `double AdfStatistic` | — |
| `p` | `bool IsCointegrated` | Verdetto puramente statistico dell'ADF sullo spread. |
| `p` | `double HedgeRatio` | Elasticità log-log fra le due gambe (β ≈ 1 = si muovono in proporzione). |
| `p` | `bool IsHedgeRatioPlausible` | Elasticità dentro la banda di sanità economica (vedi EngleGrangerCointegrationTest ). |
| `p` | `int AlignedCandles` | — |
| `p` | `bool IsTradeable` | Operabile solo se regge sia la statistica sia la plausibilità dell'elasticità. |

### 📦 `PairsOutput`

| | Firma | Descrizione |
|---|---|---|
| `p` | `List&lt;PairScreenResult&gt; Pairs` | — |
| `p` | `int CointegratedCount` | Quante coppie superano l'ADF (metrica statistica, storicamente riportata). |
| `p` | `int TradeableCount` | Quante ne superano ANCHE la banda di elasticità: è questo il numero operativo. |

### 📦 `MlTrainingOutput`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ModelType` | — |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `int TrainRows` | — |
| `p` | `int TestRows` | — |
| `p` | `int CvFolds` | — |
| `p` | `double TestCorrelation` | Pearson correlation between prediction and target on the temporal test split. |
| `p` | `List&lt;FeatureImportanceDto&gt; FeatureImportances` | — |
| `p` | `int? SavedMlModelId` | Id of the persisted SavedMlModel (null if persistence was disabled or training failed the quality bar). |

### 🧾 `FeatureImportanceDto` `(string FeatureName, double Importance);`

### 📦 `ValidatedCandidate`

> A discovery candidate enriched with the holdout verdict and robustness metrics.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string StrategyName` | — |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `Dictionary&lt;string, decimal&gt; Parameters` | — |
| `p` | `decimal WalkForwardOosSharpe` | — |
| `p` | `decimal SelectionSharpe` | — |
| `p` | `decimal SelectionReturn` | — |
| `p` | `decimal SelectionMaxDrawdown` | — |
| `p` | `int SelectionTrades` | — |
| `p` | `decimal HoldoutSharpe` | — |
| `p` | `decimal HoldoutReturn` | — |
| `p` | `decimal HoldoutMaxDrawdown` | — |
| `p` | `int HoldoutTrades` | — |
| `p` | `decimal HoldoutProfitFactor` | — |
| `p` | `bool Survived` | — |
| `p` | `string? RejectReason` | — |
| `p` | `double? DeflatedSharpe` | Deflated Sharpe del candidato (probabilità che l'edge holdout sia reale dopo N tentativi). null = non calcolabile. |
| `p` | `double? PanelPbo` | Probability of Backtest Overfitting del PANNELLO di candidati (comune a tutti). null = non calcolabile. |
| `p` | `double? PermutationPValue` | [T1.5] P- |
| `p` | `double? NullTwinPercentile` | [A1] Percentile (0-100) occupato dallo Sharpe holdout nella distribuzione dei gemelli nulli ( ), scritto da NullTwinValidationStage sui finalisti. null = non giudicato (stage non abilitato, candidato fuori dal tetto, o … |
| `p` | `decimal MonteCarloRiskFactor95` | — |
| `p` | `decimal MonteCarloDrawdown95` | — |
| `p` | `decimal KellyFraction` | — |
| `p` | `decimal EmpiricalKelly` | Kelly EMPIRICO sui rendimenti dei trade (distribuzione osservata, senza ipotesi di normalità): cattura le code grasse e di norma è ≤ del Kelly binario. Vedi . |
| `p` | `decimal HalfKelly` | Metà del MINIMO tra Kelly binario ed empirico: sizing prudente e robusto alle code grasse. |
| `p` | `string BestStopVariant` | — |
| `p` | `string Key` | Identity key for dictionary lookups (EnsembleAssembly/RiskSizing) and log/UI display. See . |

### 📦 `PipelineCandidateKey`

> Shared identity-key builder for pipeline candidates/legs. Classic strategies from StrategyDiscoveryStage produce at most ONE confirmed parameter set per (strategy,symbol,timeframe), so the short form is already unique there — but CreativeDiscoveryStage can confirm MULTIPLE distinct specs of the SAME meta-strategy (e.g. two different "Composite" rules) on the SAME pair, which collided under the old short key (a real bug caught live: `ToDictionary` throwing "same key already added", then a SECOND bug where a call site rebuilt the short key inline instead of reusing this method, silently failing every lookup). A short deterministic parameter fingerprint is appended whenever parameters exist, so every distinct spec gets its own key — used by BOTH and so the two always agree, instead of each computing its own string.

| | Firma | Descrizione |
|---|---|---|
| `m` | `string Build(string strategyName, string symbol, string timeframe, Dictionary&lt;string, decimal&gt; parameters)` | — |

### 📦 `ProposedLeg`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string StrategyName` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `Dictionary&lt;string, decimal&gt; Parameters` | — |
| `p` | `decimal WeightPercent` | — |
| `p` | `decimal SizingPercent` | — |
| `p` | `string BestStopVariant` | Stop variant validated in the walk-forward robustness probe ("base" \| "SLx" \| "TRAILx" — see ). Carried here so the live wiring (Pipeline.razor's ApplyRecommendationAsync) can translate it into the EnsembleStrategy's … |
| `p` | `decimal HoldoutSharpe` | Holdout metrics of the originating (verdict-only, never used for any selection decision — carried here purely so Pipeline.razor's ApplyRecommendationAsync can populate EnsembleStrategy.Expected* for the decay monitor). |
| `p` | `decimal HoldoutProfitFactor` | — |
| `p` | `decimal HoldoutMaxDrawdown` | — |
| `p` | `int HoldoutTrades` | Holdout trade count of the originating candidate — carried as the effective sample size behind the leg's Sharpe so the auto-reapply comparator can test a swap's statistical significance ( ). Verdict-only, never a select… |
| `p` | `string Key` | Same identity key as the originating — use this for lookups, never rebuild it inline. |

### 📦 `EnsembleProposal`

| | Firma | Descrizione |
|---|---|---|
| `p` | `List&lt;ProposedLeg&gt; Legs` | — |
| `p` | `string Method` | — |
| `p` | `string? Note` | — |

### 📦 `RiskAssessment`

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal AverageHalfKelly` | — |
| `p` | `decimal AverageRiskFactor95` | — |
| `p` | `decimal ShutdownDrawdownPercent` | System shutdown guard: stop everything if drawdown exceeds this % (MC-derived). |
| `p` | `decimal SuggestedStopLossPercent` | — |
| `p` | `decimal VolatilitySizingFactor` | Multiplier applied to sizing because of the volatility level (1 = no adjustment). |
| `p` | `List&lt;string&gt; Notes` | — |

### 📦 `CategoryImpactDto`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Category` | — |
| `p` | `int Observations` | — |
| `p` | `double AvgReturn24hPercent` | — |

### 📦 `NewsImpactOutput`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ReferenceSymbol` | — |
| `p` | `List&lt;CategoryImpactDto&gt; ByCategory` | — |
| `p` | `List&lt;string&gt; Alerts` | — |

### 📦 `RecommendationRiskLimits`

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal HalfKellyPercent` | — |
| `p` | `decimal RiskFactor95` | — |
| `p` | `decimal ShutdownDrawdownPercent` | — |
| `p` | `decimal StopLossPercent` | — |

### 📦 `PipelineRecommendation`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string RegimeLabel` | — |
| `p` | `string VolatilityLabel` | — |
| `p` | `string SentimentLabel` | — |
| `p` | `double? SentimentComposite` | Composite di market mood [-1,+1] (Sentiment 2.0); null nei run senza snapshot (compat). |
| `p` | `double? FearGreedValue` | Fear & Greed Index 0-100 al momento del run; null senza snapshot. |
| `p` | `List&lt;string&gt; SentimentExtremes` | Flag contrarian del mood (estremi F&G, funding/posizionamento a \|z\|≥soglia). |
| `p` | `int CandidatesEvaluated` | — |
| `p` | `int Survivors` | — |
| `p` | `string BestCandidate` | — |
| `p` | `List&lt;ProposedLeg&gt; EnsembleLegs` | — |
| `p` | `RecommendationRiskLimits RiskLimits` | — |
| `p` | `List&lt;string&gt; Alerts` | — |
| `p` | `List&lt;string&gt; SuggestedActions` | — |
| `p` | `string FullText` | The rendered template (the "Conclusion" persisted on the run). |

### 📦 `PlannedAction`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Description` | — |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `string StrategyName` | — |
| `p` | `decimal SizingPercent` | — |

### 📦 `ExecutionPlan`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Mode` | "Paper" \| "Live" \| "Disabled" — mirrors the configuration's ExecutionMode. |
| `p` | `List&lt;PlannedAction&gt; Actions` | — |
| `p` | `List&lt;string&gt; Notes` | — |

### 🔢 `StageStatus`

### 📦 `StageSummary`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string StageName` | — |
| `p` | `string DisplayName` | — |
| `p` | `int Order` | — |
| `p` | `StageStatus Status` | — |
| `p` | `DateTime StartedUtc` | — |
| `p` | `TimeSpan Duration` | — |
| `p` | `string Text` | — |
| `p` | `Dictionary&lt;string, decimal&gt; Metrics` | — |
| `p` | `string? Error` | — |

### 📦 `PipelineLiveStatus`

> Live view of the run in progress, polled by the UI (same pattern as /trading).

| | Firma | Descrizione |
|---|---|---|
| `p` | `Guid RunId` | — |
| `p` | `int ConfigurationId` | — |
| `p` | `string ConfigurationName` | — |
| `p` | `DateTime StartedUtc` | — |
| `p` | `string? CurrentStage` | — |
| `p` | `List&lt;StageSummary&gt; Stages` | — |
| `p` | `List&lt;string&gt; RecentLog` | — |
| `p` | `bool PauseRequested` | — |

## `ProcioneMGR/Services/Pipeline/PipelinePageService.cs`

### 🧾 `PipelineActionResult` `(string Message, bool IsError)`

> Esito di un'azione con messaggio per l'operatore.

| | Firma | Descrizione |
|---|---|---|
| `m` | `PipelineActionResult Ok(string message)` | — |
| `m` | `PipelineActionResult Error(string message)` | — |

### 📦 `PipelineConfigDraft`

> Bozza dell'editor di configurazione: la config in modifica (copia di lavoro, mai la riga tracciata) + universo/date/fasi deserializzati per il binding del form.

| | Firma | Descrizione |
|---|---|---|
| `p` | `PipelineConfiguration Config` | — |
| `p` | `List&lt;SeriesSpec&gt; Universe` | — |
| `p` | `PipelineDateRanges Ranges` | — |
| `p` | `List&lt;StageConfig&gt; Stages` | — |

### 🧾 `PipelineSaveResult` `(string Message, bool IsError, IReadOnlyList&lt;string&gt; Problems)`

> Esito del salvataggio config: eventuale lista di problemi delle fasi da mostrare nell'editor.

| | Firma | Descrizione |
|---|---|---|
| `m` | `PipelineSaveResult Ok(string message)` | — |
| `m` | `PipelineSaveResult Error(string message, IReadOnlyList&lt;string&gt;? problems = null)` | — |

### 📦 `PipelinePageService` `(`

> Orchestrazione estratta da Components/Pages/Pipeline.razor (P1-5, PRD-CONSOLIDAMENTO- ARCHITETTURA.md §3.3): caricamento di configurazioni/storico/raccomandazioni, CRUD delle configurazioni con la catena di validazione (nome, universo, date selection/holdout mai sovrapposte, problemi delle fasi dal motore), costruzione delle bozze dell'editor (nuova, o copia di lavoro con merge delle fasi aggiunte alla piattaforma dopo il salvataggio), controllo dei run (start/resume/pause/cancel), dettaglio run con confronto col precedente e decisione della ri-applica automatica, applicazione della raccomandazione ed export markdown — tutta la logica che prima viveva nel blocco @code del componente senza test indipendenti da Blazor. Il componente resta responsabile solo di ciò che è Blazor: binding della bozza, PollingTimer del tick, messaggi, badge di stato. Registrato Scoped: in Blazor Server uno sco…

| | Firma | Descrizione |
|---|---|---|
| `p` | `List&lt;PipelineConfiguration&gt; Configs` | — |
| `p` | `List&lt;PipelineRun&gt; Runs` | — |
| `p` | `PipelineLiveStatus? Live` | — |
| `p` | `PipelineRecommendation? LastRecommendation` | — |
| `p` | `PipelineRun? LastRecommendationRun` | — |
| `p` | `PipelineRun? SelectedRun` | — |
| `p` | `List&lt;StageSummary&gt; SelectedSummaries` | — |
| `p` | `List&lt;StageSummary&gt; PreviousRunSummaries` | — |
| `p` | `PipelineRecommendation? SelectedRecommendation` | — |
| `p` | `AutoReapplyDecisionArtifact? SelectedDecision` | — |
| `m` | `Task ReloadAsync(CancellationToken ct = default)` | — |
| `m` | `bool RefreshLive()` | Aggiorna lo stato live dal motore (tick di polling). True quando un run APPENA finito (running → null): il chiamante deve ricaricare lo storico. |
| `m` | `PipelineConfigDraft BuildNewConfigDraft()` | — |
| `m` | `PipelineConfigDraft BuildEditDraft(PipelineConfiguration config)` | Copia di lavoro per l'editor (mai la riga tracciata). Le fasi aggiunte alla piattaforma DOPO il salvataggio della config vengono proposte disabilitate (nessun cambio di comportamento), così le config esistenti possono a… |
| `m` | `void MoveStage(List&lt;StageConfig&gt; stages, int index, int delta)` | Scambia due fasi adiacenti e rinumera Order 1..N. |
| `m` | `Task&lt;PipelineSaveResult&gt; SaveConfigAsync(PipelineConfigDraft draft, string? userId, CancellationToken ct = default)` | Valida e salva la bozza. La catena di validazione è identica all'originale: nome obbligatorio, almeno una serie (le righe a symbol vuoto vengono rimosse dalla bozza), range di date validi, holdout MAI sovrapposto alla s… |
| `m` | `Task&lt;PipelineActionResult&gt; CloneConfigAsync(PipelineConfiguration config, string? userId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;PipelineActionResult&gt; DeleteConfigAsync(int id, CancellationToken ct = default)` | — |
| `m` | `Task&lt;PipelineActionResult&gt; StartRunAsync(int configId, string? userId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;PipelineActionResult&gt; ResumeRunAsync(Guid runId, string? userId, CancellationToken ct = default)` | — |
| `m` | `void PauseLiveRun()` | — |
| `m` | `void CancelLiveRun()` | — |
| `m` | `Task SelectRunAsync(PipelineRun run, CancellationToken ct = default)` | Seleziona un run: deserializza le fasi e la raccomandazione, individua il run COMPLETATO precedente della stessa config per il confronto, e carica (best-effort) la decisione della ri-applica automatica col giudizio del … |
| `m` | `void CloseSelectedRun()` | — |
| `m` | `IEnumerable&lt;(string Label, decimal Prev, decimal Curr)&gt; CompareRuns()` | Metriche comuni fra il run selezionato e il precedente, chiave "Fase: Metrica". |
| `m` | `Task&lt;PipelineActionResult?&gt; ApplyRecommendationAsync(PipelineRecommendation? recommendation, CancellationToken ct = default)` | Distribuisce l'ensemble sulle corsie isolate delegando a — la STESSA logica usata dalla ri-applica automatica dello scheduler (una sola implementazione, nessuna divergenza). Scrive solo la configurazione: nessun trading… |
| `m` | `string ExportHref(PipelineRun? run)` | Report markdown del run come data-URI scaricabile (nessun endpoint server necessario). |
| `m` | `string UniverseSummary(PipelineConfiguration config)` | Riassunto compatto dell'universo di una config ("BTC/USDT 1h, ETH/USDT 4h +2"). |

## `ProcioneMGR/Services/Pipeline/PipelineRules.cs`

### 📦 `PipelineRuleSet`

> Deterministic decision rules used by the RecommendationStage (NO LLM: every conclusion is backed by verifiable numbers). Loaded from Config/pipeline_rules.json under the content root so the user can tune thresholds without touching code; falls back to the built-in defaults when the file is missing or malformed.

| | Firma | Descrizione |
|---|---|---|
| `p` | `double VolatilityHighThresholdRatio` | — |
| `p` | `double VolatilityLowThresholdRatio` | — |
| `p` | `decimal HighVolSizingReductionPercent` | Sizing reduction (in %) applied when volatility is classified "Alta". |
| `p` | `double SentimentPositiveThreshold` | — |
| `p` | `double SentimentNegativeThreshold` | — |
| `p` | `List&lt;string&gt; MeanReversionStrategies` | Strategy names favoured when the regime label contains "Sideways"/"Choppy" (mean-reversion). |
| `p` | `List&lt;string&gt; TrendStrategies` | Strategy names favoured when the regime label contains "Trend" (trend-following). |
| `p` | `decimal RegimeMatchWeightMultiplier` | Weight multiplier applied to legs whose family matches the current regime. |
| `p` | `decimal MinHoldoutSharpe` | — |
| `p` | `int MinHoldoutTrades` | — |
| `p` | `decimal MaxMonteCarloRiskFactor95` | — |
| `p` | `decimal KellyFraction` | Fraction of full Kelly to use (0.5 = half-Kelly, the standard prudent choice). |
| `p` | `decimal MaxSizingPercent` | Hard cap on per-leg sizing regardless of Kelly (safety net for small samples). |
| `p` | `int MaxLegs` | Maximum number of ensemble legs recommended. |
| `p` | `List&lt;string&gt; AlertNewsCategories` | News categories whose recent presence generates an alert. |

### 🔌 `IPipelineRulesProvider`

| | Firma | Descrizione |
|---|---|---|
| `m` | `PipelineRuleSet GetRules()` | Current rule set (re-read from disk on every call — a run reads it once at RecommendationStage time). |
| `p` | `string RulesFilePath` | Absolute path of the rules file (for the UI to point the user at). |

### 📦 `PipelineRulesProvider` `(IWebHostEnvironment env, ILogger&lt;PipelineRulesProvider&gt; logger) : IPipelineRulesPr…`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string RulesFilePath` | — |
| `m` | `PipelineRuleSet GetRules()` | — |

## `ProcioneMGR/Services/Pipeline/PipelineSchedulerWorker.cs`

### 📦 `AutoReapplyOptions`

> Opzioni della ri-applica automatica dell'ensemble (sezione di config AutoReapply ).

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Interruttore globale della ri-applica automatica. DEFAULT false (safety): finché non lo abiliti esplicitamente, lo scheduler lancia i run ma NON schiera mai da solo un ensemble — l'utente applica a mano da /pipeline, co… |
| `p` | `int LookbackDays` | Quanti giorni indietro guardare per i run completati non ancora valutati. |
| `p` | `int MaxPerTick` | Massimo numero di run valutati per tick (limita il fan-out). |

### 📦 `AutoReapplyArtifactKinds`

> Kind dell'artifact che registra la decisione di ri-applica automatica di un run.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string Decision` | — |

### 📦 `AutoResumeArtifactKinds`

> Kind degli artifact dell'auto-resume (Fase 3-C1, PRD Autonomia): marker idempotenti per-run.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string Attempt` | Un tentativo di ripresa automatica (il conteggio = numero di questi artifact). |
| `k` | `string GaveUp` | Tentativi esauriti: notificato e mai più toccato (resta all'operatore). |

### 📦 `PipelineSchedulerWorker` `(`

> Worker schedulato: (1) valuta periodicamente le con attivo e lancia quelle dovute; (2) RI-APPLICA automaticamente l'ensemble migliore trovato dai run completati (se AutoReapply:Enabled ). SAFETY non negoziabile: un run schedulato non esegue MAI in Live — viene saltato (non declassato silenziosamente) se la config è in Live. La ri-applica automatica scrive SOLO la configurazione dell'ensemble sulle corsie (mai avvia trading, mai passa in Live, mai tocca SafetyChecker); l'apertura reale resta dietro conferma manuale in /trading. La sostituzione avviene solo se SIA il confronto oggettivo ( ) SIA il supervisore AI ( , che può solo porre un veto) sono d'accordo. Il motore ( ) è a slot singolo: niente lock per-config qui, la concorrenza dei run è già gestita da PipelineEngine . La catena valuta-e-applica (supervisore → confronto con isteresi → applier, con gate di atomicità sulle corsie) vive…

| | Firma | Descrizione |
|---|---|---|
| `k` | `int MaxAutoResumeAttempts` | Fase 3-C1: tentativi di auto-resume per run prima di arrendersi e notificare. Più di 1 (scostamento documentato dal PRD, che diceva "1 tentativo"): un run interrotto DUE volte da riavvii innocenti merita più di un tenta… |
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |
| `m` | `Task TickAsync(CancellationToken ct)` | Un tick completo: valuta le config schedulate e poi processa i run completati per la ri-applica. Pubblico per test. |
| `m` | `Task AutoResumePausedRunsAsync(CancellationToken ct)` | Riprende i run "Paused" con trigger AUTOMATICO (Scheduled/Event/Campaign): tipicamente gli orfani di un riavvio, che marca Paused e che prima restavano lì finché un umano non premeva Riprendi (evidenza della sessione 20… |
| `m` | `Task ProcessCompletedRunsAsync(CancellationToken ct)` | Trova i run schedulati COMPLETATI di recente senza una decisione di ri-applica registrata e li valuta uno per uno (confronto oggettivo + supervisore AI). Pubblico per test. |
| `m` | `Task EvaluateAndMaybeApplyAsync(Guid runId, CancellationToken ct)` | Valuta un singolo run e, se giustificato, ne schiera l'ensemble (delega a ). |
| `m` | `bool IsDue(PipelineConfiguration config, DateTime nowUtc)` | Vero se il prossimo run è dovuto: mai calcolato (null) o nel passato. Pura, testabile in isolamento. |
| `m` | `DateTime? ComputeNextRun(string schedule, DateTime fromUtc)` | Prossima occorrenza UTC per un'espressione cron standard a 5 campi, o null se non valida. Pura, testabile in isolamento. |

### 📦 `AutoReapplyDecisionArtifact`

> Payload dell'artifact di decisione della ri-applica automatica (persistito come JSON).

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Applied` | — |
| `p` | `string Message` | — |
| `p` | `EnsembleComparison? Comparison` | — |
| `p` | `SupervisorJudgment? Judgment` | — |
| `p` | `DateTime DecidedAtUtc` | — |

## `ProcioneMGR/Services/Pipeline/PipelineStageCatalog.cs`

### 📦 `PipelineStageCatalog` `: IPipelineStageCatalog`

> Catalog of the available pipeline stages. Stage classes are resolved via DI (they depend on platform services); the catalog holds the TYPE list, materializes prototypes once for metadata reads (Name/DisplayName/ParameterDefinitions/Dependencies are plain constants on every stage, safe to read after the construction scope is gone), and creates fresh per-run instances inside the engine's scope.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;Type&gt; StageTypes` | The full stage roster, in default execution order. |
| `c` | `PipelineStageCatalog(IServiceScopeFactory scopeFactory)` | — |
| `p` | `IReadOnlyList&lt;IPipelineStage&gt; Prototypes` | — |
| `m` | `IPipelineStage Create(IServiceProvider scopedProvider, string name)` | — |
| `m` | `List&lt;StageConfig&gt; DefaultStages()` | — |

## `ProcioneMGR/Services/Pipeline/RegimeChangeDetector.cs`

### 📦 `RegimeTriggerOptions`

> Opzioni del trigger contestuale (Fase 2, PRD Autonomia §5), sezione RegimeTrigger .

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Default ON: il trigger è additivo e parla SOLO col planner, che ha già il suo gate ( Campaign:Enabled default OFF) — senza campagne abilitate non succede nulla. |
| `p` | `int CheckIntervalMinutes` | Cadenza del check (letta all'avvio del worker). |
| `p` | `int CooldownHours` | Cooldown tra due wake (PRD: default 6h): il regime non "cambia" ogni mezz'ora. |
| `p` | `double VolBandMultiple` | Banda di volatilità: scatta se la realized esce da [forecast/k, forecast×k] rispetto al forecast GARCH dell'ultimo run (PRD: es. realized &gt; 1,5× forecast — l'espansione attesa su SOL; la compressione oltre banda è a … |

### 📦 `RegimeTriggerCheck`

> Esito di un check del trigger (con i valori osservati, per log/notifica/test).

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Triggered` | — |
| `p` | `string Reason` | — |
| `p` | `int? BaselineRegimeId` | — |
| `p` | `int? CurrentRegimeId` | — |
| `p` | `double? BaselineForecastVolatility` | — |
| `p` | `double? RealizedVolatility` | — |
| `p` | `Guid BaselineRunId` | — |

### 🔌 `IRegimeChangeDetector`

> Rileva un cambio di contesto rispetto all'ULTIMO run completato delle campagne abilitate (Fase 2, PRD Autonomia §5): la caccia gira alle 03:00, ma il regime cambia quando cambia. Riusa SOLO calcoli esistenti: cluster K-means corrente (IMarketFeatureExtractor + IRegimeDetector, stesso percorso dell'EnsembleManager) contro il CurrentRegimeId persistito nel checkpoint del run; volatilità realizzata (stddev dei log-rendimenti recenti, per-periodo) contro il forecast GARCH a 24 passi dello stesso run.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;RegimeTriggerCheck?&gt; CheckAsync(CancellationToken ct = default)` | Null quando manca la base di confronto (nessun run di campagna completato, niente dati/modello). |

### 📦 `RegimeChangeDetector` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;RegimeTriggerCheck?&gt; CheckAsync(CancellationToken ct = default)` | — |
| `m` | `double? ComputeRealizedVolatility(IReadOnlyList&lt;decimal&gt; prices, int window)` | Stddev per-periodo dei log-rendimenti sulle ultime osservazioni. Pura. |

## `ProcioneMGR/Services/Pipeline/RegimeChangeTriggerWorker.cs`

### 📦 `RegimeChangeTriggerWorker` `(`

> Worker del trigger contestuale (Fase 2, PRD Autonomia §5). Il trigger NON lancia mai run direttamente: CHIEDE al di anticipare la prossima esecuzione ( — backoff bypassato, run marcato "Event" ⚡), con cooldown (default 6h) e nel pieno rispetto dello slot singolo del motore (già garantito da StartRunAsync che rifiuta se occupato). Gate a monte: senza Campaign:Enabled il check non parte nemmeno — il trigger esiste solo per servire il planner.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |
| `m` | `Task TickAsync(CancellationToken ct)` | Un check completo (detector → cooldown → wake del planner → notifica). Pubblico per test. |

## `ProcioneMGR/Services/Pipeline/RunApplyEvaluator.cs`

### 🔌 `IRunApplyEvaluator`

> Valutazione "vale la pena schierare l'ensemble di questo run?" + eventuale applica, estratta VERBATIM da (Fase 1 del PRD Autonomia): la stessa identica catena — supervisore AI (solo veto) → confronto oggettivo con isteresi ( ) → — è ora usata sia dalla ri-applica automatica dello scheduler sia dal : una sola implementazione, nessuna deriva tra i due percorsi automatici. La decisione resta registrata come idempotente ( ), qualunque sia il chiamante.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;RunApplyOutcome&gt; EvaluateAndMaybeApplyAsync(Guid runId, CancellationToken ct = default)` | Valuta un run completato e, se giustificato, ne schiera l'ensemble. Idempotente per run. |

### 📦 `RunApplyOutcome`

> Esito della valutazione di un run (per lo scheduler, il planner e i loro log/test).

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool HadCandidate` | false quando il run non esiste o non ha un ensemble applicabile (0 sopravvissuti). |
| `p` | `bool Applied` | — |
| `p` | `bool Vetoed` | true quando il MOTIVO della mancata applica è il veto del supervisore AI. |
| `p` | `int LanesUsed` | Corsie configurate dall'applica (0 se non applicato) — al planner serve per l'avvio Paper. |
| `p` | `string Message` | — |

### 📦 `RunApplyEvaluator` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;RunApplyOutcome&gt; EvaluateAndMaybeApplyAsync(Guid runId, CancellationToken ct = default)` | — |
| `m` | `PipelineRecommendation? DeserializeRecommendation(string? json)` | — |

## `ProcioneMGR/Services/Pipeline/Stages/AnalysisStages.cs`

### 📦 `FeatureEngineeringStage` `(`

> Stage 3 — evaluates the alpha-factor library (Information Coefficient) on the primary series over the SELECTION range only, and selects the top-K factors as ML features.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

### 📦 `RegimeAnalysisStage` `(`

> Stage 4 — labels the current market regime with the active K-means model (training one on the selection range only when none exists, or when retrain=true).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `int MinLabelDaysForTests(string timeframe)` | Giorni minimi perché la finestra di etichettatura contenga abbastanza BARRE da superare il warmup dell'estrattore di feature (50 barre) con un margine utile allo smoothing dei regimi. Senza questo, ogni timeframe più lu… |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

### 📦 `VolatilityRegimeStage` `(IGarchModel garch) : IPipelineStage`

> Stage 5 — classifies the volatility level of the primary series. [C3] Il PREVISORE che decide il Level è il log-HAR sulla varianza realizzata giornaliera dai 5m quando i 5m bastano (gate C3: QLIKE OOS migliore del vincitore GARCH/EWMA su 6/6 simboli di sviluppo e 24/24 di conferma a 1g), altrimenti GARCH(1,1) come sempre. Il GARCH viene comunque fittato: persistenza, parametri e soprattutto le CODE Student-t (VaR 1%) restano sue — il gate C3 riguarda la previsione di σ, non i quantili di coda.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

### 📦 `PairsScreeningStage` `(ICointegrationTest cointegration) : IPipelineStage`

> Stage 6 — screens every same-timeframe symbol pair of the universe for cointegration (Engle-Granger) over the selection range.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

## `ProcioneMGR/Services/Pipeline/Stages/CreativeDiscoveryStage.cs`

### 📦 `CreativeDiscoveryStage` `(`

> Stage 8-bis — CREATIVE discovery: instead of sweeping parameters of known strategies, the GENERATES brand-new strategy specs (composite signal rules, event triggers, regime maps), screens them on the selection range and confirms the best per series with a fixed-parameter walk-forward. Confirmed candidates are injected into exactly like classic discovery output, so the holdout gauntlet (validation → robustness → ensemble) treats them identically — the composer proposes, the backtests dispose.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

## `ProcioneMGR/Services/Pipeline/Stages/DataStages.cs`

### 📦 `DataIngestionStage` `(`

> Stage 1 — verifies OHLCV coverage for the whole universe over [SelectionFrom, HoldoutTo] and (optionally) ingests only the MISSING head/tail deltas via the existing idempotent ingestion service. Never re-downloads what the DB already has.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

### 📦 `AltDataSyncStage` `(`

> Stage 2 — syncs the alternative-data sources (news RSS, retail sentiment) and summarizes the last 24h.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

## `ProcioneMGR/Services/Pipeline/Stages/DecisionStages.cs`

### 📦 `EnsembleAssemblyStage` `(`

> Stage 11 — assembles the final survivors into a weighted ensemble proposal. Weights come from HRP on the legs' selection-range equity

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

### 📦 `RiskSizingStage` `(IPipelineRulesProvider rulesProvider) : IPipelineStage`

> Stage 12 — turns the robustness numbers into operating risk limits: half-Kelly sizing per leg (volatility-adjusted), the Monte Carlo shutdown guard, and the system stop level.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

### 📦 `NewsImpactCheckStage` `(`

> Stage 13 — historical news impact on the reference symbol + alerts for recent high-impact categories.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

### 📦 `RecommendationStage` `(IPipelineRulesProvider rulesProvider) : IPipelineStage`

> Stage 14 — the deterministic "brain": renders the final conclusion from the numbers the previous stages produced, applying the rules of pipeline_rules.json. No LLM: every claim in the output traces back to a verifiable metric in the context.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

### 📦 `ExecutionPlanStage` `: IPipelineStage`

> Stage 15 — turns the recommendation into a concrete (paper-first) action plan. It NEVER starts trading by itself: the plan is applied by the user from the UI ("Applica al Trading"), and Live execution always goes through SafetyChecker + per-order manual confirmation in /trading — the pipeline cannot bypass either.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

## `ProcioneMGR/Services/Pipeline/Stages/ModelStages.cs`

### 📦 `MlModelTrainingStage` `(`

> Stage 7 — trains a return predictor on the SELECTION range (temporal train/test split with a purge gap of forwardHorizon rows at the boundary, so no test label overlaps the training window), persists it as a SavedMlModel and registers it as an "Ml" strategy candidate for the holdout validation.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

### 📦 `StrategyDiscoveryStage` `(IStrategyDiscovery discovery) : IPipelineStage`

> Stage 8 — systematic walk-forward strategy discovery over the whole universe, restricted to the SELECTION range. Applies the noise gates of the strategy-hunt reports (minimum OOS Sharpe AND minimum OOS trades — a Sharpe 3 with 2 trades is noise, not edge).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

### 📦 `HoldoutValidationStage` `(IBacktestEngine backtest, IDbContextFactory&lt;ApplicationDbContext&gt; dbFactory) : IPi…`

> Stage 9 — the verdict: every candidate is backtested on the HOLDOUT range (never seen by any prior decision), with slippage. Survivors must clear the Sharpe/trade-count gates.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `decimal ProfitFactor(IReadOnlyList&lt;BacktestTrade&gt; trades)` | Pubblico per testabilità diretta (stesso trattamento di OptimizationEngine.ComboKey). |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

### 📦 `RobustnessProbeStage` `(`

> Stage 10 — robustness probe on the survivors: stop-loss variants (chosen on SELECTION data only), seeded Monte Carlo of the trade sequence (shutdown guard level), Kelly sizing.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `void ApplyVariant(BacktestConfiguration cfg, string variant)` | Applica un variant a una configurazione di backtest. Un variant può COMBINARE più componenti separate da "_" (es. "SL2_TP4" = stop 2% + take profit 4%). Token riconosciuti: SLx (stop x%), TRAILx (trailing x%), TPx (take… |
| `m` | `List&lt;string&gt; EnsureTakeProfitVariants(List&lt;string&gt; variants)` | Autonomia: garantisce che la prova di robustezza valuti SEMPRE anche il take profit e alcune combinazioni SL+TP, anche per configurazioni salvate prima di questa feature (che elencano solo varianti di stop). Se la lista… |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

### 📦 `OverfittingGate`

> Gate anti-overfitting universale (P0-3) applicato dall' sull'intero batch di candidati dopo l'holdout. Puro e testabile in isolamento (nessun DB/backtest): muta i flag / / dei candidati passati. Riusa la libreria di rigore ( , ) — stesso pattern di OptimizationEngine e ModelRegistry.

### ▫️ `Result` `(int Survivors, double? PanelPbo);`

## `ProcioneMGR/Services/Pipeline/Stages/NullTwinValidationStage.cs`

### 📦 `NullTwinValidationStage` `(INullTwinJudge judge) : IPipelineStage`

> Stage 10 (opt-in) — il giudice del gemello nullo sui SOPRAVVISSUTI all'holdout: ogni finalista viene ribattezzato su N mercati nulli ( : stessa volatilità, zero struttura direzionale) e sopravvive solo se il suo Sharpe holdout supera il quantile richiesto della distribuzione nulla. È il terzo giudice indipendente dopo Sharpe/trade e DSR/PBO — quello che nei tool CLI ha smascherato il falso positivo SEI/USDT — reso organo della pipeline con la POLICY UNIFICATA di (200 gemelli, 99°), mai più due giudici con rigore diverso. Fail-safe dichiarato: un candidato NON giudicabile (holdout troppo corto, gemelli falliti in massa) resta sopravvissuto e viene detto a voce alta — un giudice che non può giudicare non boccia al buio, coerente con la regola del CorrelatedExposureGuard.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

## `ProcioneMGR/Services/Pipeline/Stages/PowerCheckStage.cs`

### 📦 `PowerCheckStage` `: IPipelineStage`

> [F4 PRD Valore] Check di potenza MinTRL (Bailey-López de Prado): PRIMA di spendere i backtest, dichiara quale Sharpe annualizzato può superare i gate su QUESTA finestra con QUESTI tentativi. Perché esiste: su 4 mesi di holdout un candidato con Sharpe ~1 non può passare il DSR per aritmetica — la piattaforma lo ha scoperto empiricamente («0 sopravvissuti» dopo ore di CPU, dieci volte). Questo stage rende quel numero un OUTPUT del run, in testa: se nessun candidato plausibile può farcela, lo dice subito, e con enforce=true blocca il run con la spiegazione invece di lasciarlo bruciare calcolo. Le soglie dei gate NON vengono toccate: il check informa (e al limite ferma), mai ammorbidisce. Deterministico e puro: nessun accesso a dati, solo l'aritmetica su finestre e conteggi già noti al contesto. Il numero di tentativi è un parametro dichiarato (default conservativo), perché il conteggio VER…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `string Description` | — |
| `p` | `int DefaultOrder` | — |
| `p` | `IReadOnlyList&lt;StageDependency&gt; Dependencies` | — |
| `p` | `IReadOnlyList&lt;StageParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `string? ValidateInput(PipelineContext ctx)` | — |
| `m` | `Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)` | — |
| `m` | `StageSummary Summarize(PipelineContext ctx)` | — |

# `Services/Llm/`

## `ProcioneMGR/Services/Llm/AiKeyStore.cs`

### 📦 `AiProviders`

> Nomi canonici dei provider AI. Stringhe (non enum): un provider nuovo non deve toccare lo schema.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string Anthropic` | — |
| `k` | `string Nvidia` | — |
| `k` | `string Gemini` | — |
| `k` | `string Groq` | — |
| `k` | `string HuggingFace` | — |
| `p` | `IReadOnlyList&lt;string&gt; Known` | I provider noti alla UI di configurazione, nell'ordine di presentazione. Anthropic in coda dal 2026-08-02 (scelta del proprietario: credito esaurito, si sfruttano le altre — resta disponibile per quando/se il credito to… |
| `m` | `string EnvVarFor(string provider)` | Variabile d'ambiente di fallback per il provider ("ANTHROPIC_API_KEY", "NVIDIA_API_KEY", "GEMINI_API_KEY", "GROQ_API_KEY", "HUGGINGFACE_API_KEY"). |

### 🔢 `AiKeySource` `{ None, Environment, Database }`

> Da dove viene la chiave che il provider userebbe ORA (per la UI: mai il valore, solo la fonte).

### 🔌 `IAiKeyStore`

> Fonte unica delle chiavi API dei provider AI: prima la riga cifrata a database (inserita da /admin/ai-supervisor), poi la variabile d'ambiente come fallback — così il comportamento storico (solo env) resta valido per chi non tocca il pannello.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;string?&gt; GetKeyAsync(string provider, CancellationToken ct = default)` | Chiave effettiva per il provider (DB → env), o null se assente. Carica la cache al primo uso. |
| `m` | `string? GetCachedKey(string provider)` | Lettura sincrona per gli IsConfigured: cache (se già caricata) → env. Mai I/O. |
| `m` | `AiKeySource GetCachedSource(string provider)` | Fonte corrente della chiave, per la UI. |
| `m` | `Task SetKeyAsync(string provider, string apiKey, CancellationToken ct = default)` | Inserisce o sostituisce la chiave del provider (cifrata a riposo) e aggiorna la cache. |
| `m` | `Task RemoveKeyAsync(string provider, CancellationToken ct = default)` | Rimuove la chiave a database (l'eventuale env torna a valere). |
| `m` | `Task ReloadAsync(CancellationToken ct = default)` | Ricarica la cache dal database (usata dalla UI e dal worker all'avvio). |

### 📦 `AiKeyStore` `(`

> Implementazione con cache in memoria (ConcurrentDictionary) caricata pigramente: i percorsi sincroni (IsConfigured dei client) non fanno mai I/O. Se una riga non si decifra (master key cambiata), il caricamento lo dice a voce alta e si prosegue col solo fallback env — la lezione B2: mai un errore crypto silenzioso che sembra "chiave assente".

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;string?&gt; GetKeyAsync(string provider, CancellationToken ct = default)` | — |
| `m` | `string? GetCachedKey(string provider)` | — |
| `m` | `AiKeySource GetCachedSource(string provider)` | — |
| `m` | `Task SetKeyAsync(string provider, string apiKey, CancellationToken ct = default)` | — |
| `m` | `Task RemoveKeyAsync(string provider, CancellationToken ct = default)` | — |
| `m` | `Task ReloadAsync(CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Llm/AnthropicLlmClient.cs`

### 📦 `LlmOptions`

> Opzioni del layer AI. Le API key NON sono qui: vivono cifrate a database (AiCredentials, gestite da /admin/ai-supervisor) con fallback alle variabili d'ambiente ( ANTHROPIC_API_KEY , NVIDIA_API_KEY ) — vedi .

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | — |
| `p` | `string Provider` | Provider attivo del layer AI (una voce di ). Default Nvidia dal 2026-08-02 (Anthropic retrocessa: credito esaurito). Hot-reload: l'instradamento avviene A OGNI chiamata (DelegatingLlmClient), cambiare provider dal panne… |
| `p` | `bool FailoverEnabled` | [Failover 2026-08-02] Se la chiamata al provider attivo fallisce (qualunque errore che non sia una cancellazione), il DelegatingLlmClient prova DA SOLO i provider di questa lista, nell'ordine, saltando quelli senza chia… |
| `p` | `List&lt;string&gt; FailoverProviders` | Catena di failover, nell'ordine di tentativo; VUOTA = catena di default ( ). Il default sta in una costante e NON qui: il binder di configurazione APPENDE gli elementi dell'array alla lista già inizializzata invece di s… |
| `p` | `IReadOnlyList&lt;string&gt; DefaultFailoverChain` | La catena di default quando è vuota. |
| `m` | `IReadOnlyList&lt;string&gt; EffectiveFailoverChain()` | La catena EFFETTIVA (configurata, o default se vuota), deduplicata preservando l'ordine. |
| `p` | `string Model` | — |
| `p` | `string NvidiaModel` | Modello per il provider Nvidia (namespace/modello del catalogo build.nvidia.com). |
| `p` | `string NvidiaBaseUrl` | Endpoint OpenAI-compatible del provider Nvidia. Parametrico DI PROPOSITO: qualunque piattaforma esponga lo stesso contratto (OpenRouter, endpoint self-hosted, …) potrà entrare cambiando URL e chiave, senza un client nuo… |
| `p` | `string GeminiModel` | Modello per Google Gemini (layer OpenAI-compatible di Generative Language API). Id CANONICO col prefisso "models/" come lo restituisce l'elenco dell'API (verificato dal vivo 2026-08-02); il 2.5 è ritirato per le chiavi … |
| `p` | `string GeminiBaseUrl` | — |
| `p` | `string GroqModel` | Modello per Groq (inferenza a bassa latenza su modelli aperti). |
| `p` | `string GroqBaseUrl` | — |
| `p` | `string HuggingFaceModel` | Modello per il router HuggingFace (org/nome del catalogo; il router sceglie il backend). |
| `p` | `string HuggingFaceBaseUrl` | — |
| `p` | `int MaxTokens` | — |
| `p` | `int PollIntervalMinutes` | — |
| `p` | `int RequestTimeoutSeconds` | Timeout COMPLESSIVO della chiamata, tutti i tentativi di failover compresi (il SDK da solo aspetterebbe fino a 10 minuti). |
| `p` | `int PerProviderTimeoutSeconds` | [2026-08-05] Budget di tempo del SINGOLO provider dentro la catena di failover. Scaduto questo, il provider è considerato appeso e si passa al prossimo anello. Perché esiste : senza, un provider che si appende — il modo… |
| `p` | `int BreakerFailureThreshold` | Errori transitori consecutivi dopo i quali il breaker sospende le chiamate. |
| `p` | `int BreakerCooldownMinutes` | Minuti tra i probe automatici a breaker aperto (il ripristino è autonomo). |
| `p` | `bool NotifyDecisions` | Notifica (Info) quando un'advisory riuscita contiene decisioni per l'utente. Default off. |
| `p` | `bool ComparisonEnabled` | [Fase C] Secondo parere: dopo ogni advisory riuscita, chiede la STESSA analisi anche al provider di confronto e la salva accanto (artifact separato, mai al posto). Default off: raddoppia il costo per run, e va scelto ap… |
| `p` | `string ComparisonProvider` | Provider del secondo parere (una voce di ). Default Groq (attivo default = Nvidia; due pareri dallo stesso provider non confrontano niente e si saltano da soli). |
| `p` | `bool ExplainRejections` | [G6] Spiegazione in prosa dei candidati BOCCIATI, prodotta dal worker dopo l'advisory. Default off: è una chiamata in più per run, e va scelta apposta. Spento NON significa niente spiegazione: il riassunto DETERMINISTIC… |
| `p` | `int ExplainRejectionsTopN` | [G6] Quanti candidati bocciati riportare per esteso (i conteggi per causa coprono sempre tutti). |

### 📦 `AnthropicLlmClient` `: ILlmClient, IModelCatalogProvider`

> Implementazione di sull'SDK ufficiale Anthropic (pacchetto Anthropic ). Usa il modello configurato (default claude-opus-4-8 ) con adaptive thinking. La API key è letta esclusivamente dalla variabile d'ambiente ANTHROPIC_API_KEY — mai da appsettings — e se manca il client è semplicemente "non configurato" (l'app parte lo stesso).

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsConfigured` | — |
| `p` | `string Model` | — |
| `m` | `Task&lt;string&gt; CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)` | — |
| `m` | `Task&lt;IReadOnlyList&lt;string&gt;&gt; ListModelsAsync(CancellationToken ct)` | GET api.anthropic.com/v1/models (dialetto proprio: header x-api-key + anthropic-version , non Bearer). HTTP nudo invece dell'SDK: è una GET con due header, e il contratto d'errore resta quello leggibile dal pannello. |

## `ProcioneMGR/Services/Llm/Committee/AiCommittee.cs`

### 📦 `CommitteeOptions`

> [AF3] Opzioni del comitato, sezione Committee . Default SPENTO. parte VUOTA per la stessa lezione di : il binder di configurazione APPENDE gli elementi di un array alla lista già inizializzata — con un default popolato la lista raddoppierebbe a ogni salvataggio dal pannello.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | — |
| `p` | `List&lt;string&gt; Providers` | Provider votanti; vuota = . Vota solo chi ha la chiave. |
| `p` | `IReadOnlyList&lt;string&gt; DefaultProviders` | — |
| `m` | `IReadOnlyList&lt;string&gt; EffectiveProviders()` | — |
| `p` | `int TimeoutSeconds` | Timeout del SINGOLO voto (i free tier sono lenti; i voti corrono in parallelo). |
| `p` | `int MinValidVotes` | Voti validi minimi perché la maggioranza valga; sotto, decide il default deterministico. |

### 🧾 `CommitteeOption` `(string Id, string Label);`

> Un'opzione del menù. Le AI possono scegliere SOLO fra queste.

### 🧾 `CommitteeQuestion` `(`

> La domanda: contesto + menù chiuso + il default deterministico che vale quando il comitato non produce una maggioranza valida. Il default non è un ripiego: è la regola che il codice avrebbe applicato comunque — il comitato può solo scegliere DENTRO il recinto.

### 🧾 `CommitteeVote` `(string Provider, string? OptionId, double? Confidence, string Reason, bool Valid);`

> Il voto di un provider. falso = astensione (errore, timeout, scelta fuori menù).

### 🧾 `CommitteeVerdict` `(string ChosenOptionId, bool ByQuorum, IReadOnlyList&lt;CommitteeVote&gt; Votes);`

> Il verdetto: SEMPRE un'opzione del menù. falso = ha deciso il default.

### 🔌 `IAiCommittee`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;CommitteeVerdict&gt; AskAsync(CommitteeQuestion question, CancellationToken ct = default)` | — |

### 📦 `AiCommittee` `(`

> [AF3] Il comitato a SCELTA VINCOLATA: i provider configurati votano TUTTI in parallelo (via — semantica opposta al failover, dove risponde uno solo) su un menù chiuso preparato dal codice. Contratto JSON severo; una scelta fuori menù, un errore, un timeout sono ASTENSIONI, mai errori che si propagano. Maggioranza semplice fra i validi; parità o quorum mancato ⇒ il default deterministico della domanda. Guardrail (in ordine di importanza): - il verdetto è validato di nuovo QUI contro il menù (difesa in profondità anti prompt injection: il contesto contiene dati di mercato, che sono testo non fidato); - il budget (AF1) si controlla PRIMA di ogni giro di voti: il comitato moltiplica le chiamate ed è il primo candidato al cost runaway; - nessun breaker condiviso col resto del layer: un'ecatombe del comitato produce un verdetto di default, MAI la sospensione di advisory/veto/sentiment.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;CommitteeVerdict&gt; AskAsync(CommitteeQuestion question, CancellationToken ct = default)` | — |
| `m` | `CommitteeVote Parse(string provider, string raw, CommitteeQuestion question)` | Parse severo del contratto. Tollera SOLO il rumore di forma noto (recinzioni markdown, testo attorno all'oggetto); tutto il resto — scelta fuori menù compresa — è astensione. |

## `ProcioneMGR/Services/Llm/ILlmClient.cs`

### 🔌 `ILlmClient`

> Astrazione minimale su un LLM testuale. Esiste per un solo motivo: isolare l'SDK Anthropic dietro un'interfaccia, così è testabile con un fake e nessun test tocca la rete. Nessuna capacità oltre "prompt → testo": il layer AI è advisory puro.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsConfigured` | True se il client ha le credenziali per operare (env ANTHROPIC_API_KEY presente). |
| `p` | `string Model` | Modello configurato (per tracciabilità nell'advisory). |
| `m` | `Task&lt;string&gt; CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)` | Esegue una singola completion e restituisce il testo concatenato dei blocchi di risposta. |

## `ProcioneMGR/Services/Llm/LlmCallGuard.cs`

### 🔢 `LlmCallOutcome`

> Esito di una chiamata Claude passata dal guard.

### 📦 `LlmCallResult`

> Risultato di .

| | Firma | Descrizione |
|---|---|---|
| `p` | `LlmCallOutcome Outcome` | — |
| `p` | `string? Text` | Testo della risposta, solo per . |
| `p` | `Exception? Error` | — |
| `p` | `string Cause` | Causa leggibile ("credito API", "rate-limit", "server", "rete", "timeout", ...). |

### 🧾 `LlmGuardStatus` `(`

> Fotografia dello stato del breaker per la UI (/admin/ai-supervisor).

### 🔌 `ILlmCallGuard`

> Chokepoint di OGNI chiamata Claude (path advisory e path veto). Un problema dell'API — credito esaurito, chiave revocata, rate-limit, guasto — non deve né bloccare la piattaforma né bruciare chiamate a vuoto né passare sotto silenzio: il guard classifica l'errore con le eccezioni tipizzate del SDK, apre un circuit breaker dopo N errori transitori consecutivi, riprova da solo con un probe periodico (half-open) e avvisa l'operatore UNA volta per transizione (Warning all'apertura con la causa, Info al ripristino). Stato solo in-memory: dopo un riavvio il breaker si riapre da sé dopo pochi errori a buon mercato.

| | Firma | Descrizione |
|---|---|---|
| `m` | `LlmGuardStatus GetStatus()` | — |

### 📦 `LlmCallGuard` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `LlmGuardStatus GetStatus()` | — |
| `m` | `(bool Retryable, string Cause) Classify(Exception ex)` | Classifica un'eccezione della chiamata Claude: transitoria (ritentabile, muove il breaker) o permanente. Pubblico e statico per i test. L'ordine conta: prima i tipi specifici. |
| `m` | `bool TryParseCompatHttpStatus(string message, out int status)` | "GROQ HTTP 429: {json}" → estrae il codice fra " HTTP " e i due punti, qualunque sia il provider. |

## `ProcioneMGR/Services/Llm/LlmClientResolver.cs`

### 🔌 `ILlmClientResolver`

> Risolve un per NOME di provider — serve al secondo parere (Fase C), che deve parlare con un provider SPECIFICO e non con quello attivo del . Interfaccia minima al posto della DI keyed: un fake nei test è una lambda, e un provider nuovo è una riga qui accanto a quella in .

| | Firma | Descrizione |
|---|---|---|
| `m` | `ILlmClient? Resolve(string provider)` | Il client del provider richiesto, o null se il nome non è noto. |

### 📦 `LlmClientResolver` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ILlmClient? Resolve(string provider)` | — |

## `ProcioneMGR/Services/Llm/LlmSupervisorWorker.cs`

### 📦 `LlmSupervisorWorker` `(`

> Worker che collega il layer AI al ciclo di ricerca SENZA accoppiarlo al motore: sonda periodicamente i completati privi di advisory AI e li fa supervisionare da . Decoupling deliberato — il PipelineEngine non conosce il layer AI, e questo worker non conosce trading/esecuzione: legge run e scrive artifact advisory, nient'altro (confine di sicurezza research→esecuzione, come per ). Inattivo per default: sia Llm:Enabled sia la presenza di ANTHROPIC_API_KEY sono valutati a OGNI tick (modello ExecutionWorker) — il worker NON muore mai: se la chiave manca logga una volta e resta in attesa, così toggle e chiave prendono effetto senza riavvio.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |
| `m` | `Task TickAsync(CancellationToken ct, bool forceProbe = false)` | Un tick: trova i run completati di recente senza advisory e li supervisiona. Pubblico per test e per la UI; ignora il cooldown del breaker ("Riprova adesso"). |

## `ProcioneMGR/Services/Llm/LlmUsage.cs`

### 📦 `LlmCallContext`

> [AF1] Etichetta di percorso della chiamata LLM in corso, propagata per contesto asincrono: la conosce solo il (che la riceve come parametro), ma a consumarla è il CLIENT che serve la risposta — e fra i due c'è il failover del , quindi passarla per parametro significherebbe cambiare la firma di e ogni fake dei test. Un AsyncLocal attraversa la catena senza toccare nessuna firma.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string? CurrentPath` | Il path corrente ("advisory" \| "veto" \| "sentiment" \| …), o null fuori dal guard. |
| `m` | `IDisposable Enter(string path)` | — |

### 📦 `Scope` `(string? previous) : IDisposable`

| | Firma | Descrizione |
|---|---|---|
| `m` | `void Dispose()` | — |

### ▫️ `LlmUsageEvent` `(`

> Un consumo dichiarato dal provider nella risposta. è quello del client che ha SERVITO la chiamata (ogni client concreto dichiara se stesso): col failover può non essere il provider attivo, e attribuire i token a quello sbagliato renderebbe il pannello una rassicurazione invece che una misura.

### 🧾 `LlmBudgetVerdict` `(bool Exhausted, string Reason)`

> Esito del controllo di budget prima di una chiamata.

| | Firma | Descrizione |
|---|---|---|
| `p` | `LlmBudgetVerdict Allowed` | — |

### 🧾 `LlmUsageSnapshot` `(`

> Consumo aggregato per il pannello.

### 🧾 `LlmUsageRow` `(string Provider, string Model, string Path, int Calls, long PromptTokens, long Completio…`

### 📦 `LlmBudgetOptions`

> [AF1] Opzioni di consumo e budget del layer AI, sezione Llm:Budget . TUTTO spento per default (invariante di piattaforma): senza non si scrive una riga e non si applica alcun tetto — comportamento bit-identico a prima della fase. I limiti a 0 significano "nessun tetto". Il budget è il freno al cost runaway: coi free tier di oggi para i loop impazziti, con un domani a pagamento para la bolletta.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool TrackingEnabled` | — |
| `p` | `int DailyCallLimit` | Tetto di CHIAMATE al giorno (0 = nessuno). Conta ogni chiamata servita, di ogni path. |
| `p` | `int DailyTokenLimit` | Tetto di token (prompt+completion) al giorno (0 = nessuno). |
| `p` | `int MonthlyTokenLimit` | Tetto di token nel mese solare UTC (0 = nessuno). |

### 🔌 `ILlmUsageSink`

> [AF1] Chi raccoglie i consumi e risponde sul budget. Interfaccia stretta di proposito: non lancia MAI (un contatore non deve poter rompere una chiamata riuscita) e è una lettura pura in memoria (il guard la chiama prima di ogni chiamata, non può costare un giro di DB).

| | Firma | Descrizione |
|---|---|---|
| `m` | `void Record(LlmUsageEvent e)` | — |
| `m` | `LlmBudgetVerdict CheckBudget()` | — |
| `m` | `bool TryMarkExhaustionNotified()` | Vero SOLO alla prima chiamata dopo l'esaurimento del budget (una notifica per transizione; il flag si riarma quando il budget torna disponibile, tipicamente a mezzanotte UTC). |
| `m` | `LlmUsageSnapshot GetSnapshot()` | — |

### 📦 `LlmUsageTracker` `(`

> Implementazione: aggregati in memoria + persistenza periodica su (una riga per giorno/provider/modello/path, upsert dal ). Al riavvio i totali di oggi e del mese si RICARICANO dal database (prima del primo flush): un budget giornaliero che si azzera riavviando il processo non è un budget, è un girotondo.

| | Firma | Descrizione |
|---|---|---|
| `m` | `void Record(LlmUsageEvent e)` | — |
| `m` | `LlmBudgetVerdict CheckBudget()` | — |
| `m` | `bool TryMarkExhaustionNotified()` | — |
| `m` | `LlmUsageSnapshot GetSnapshot()` | — |
| `m` | `Task LoadPersistedTotalsAsync(CancellationToken ct = default)` | Carica i totali persistiti di oggi e del mese. Chiamata dal flush worker all'avvio e dopo ogni rollover di giorno: resta una lettura in memoria, e fra l'avvio del processo e il primo caricamento il budget sottoconta (di… |
| `m` | `Task FlushAsync(CancellationToken ct = default)` | Scrive i delta accumulati (upsert per riga-giorno) e li sposta nei persistiti. |

### 📦 `LlmUsageFlushWorker` `(`

> [AF1] Flush periodico dei consumi + ricarica dei totali persistiti all'avvio e al cambio di giorno. Cadenza corta (1 minuto) perché il volume è minuscolo (aggregati, non eventi) e un crash non deve perdere più di un minuto di conteggio.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

## `ProcioneMGR/Services/Llm/ModelAutoSelector.cs`

### 📦 `ModelAutoSelector`

> Sceglie in automatico un modello di CHAT sensato dall'elenco che l'API restituisce per la chiave (richiesta del proprietario 2026-08-02: «i modelli vengano scaricati in automatico e uno venga scelto in automatico»). Funzione PURA: niente rete, niente stato — l'elenco arriva da e la scelta è riproducibile e testabile. Strategia: (1) si scartano gli id palesemente non-chat (embedding, tts, immagini, audio, video, robotica…) — un catalogo Gemini reale ne è pieno; (2) si prova una lista di preferenze ORDINATE per provider (dal modello di lavoro consigliato in giù); (3) a parità di preferenza vince l'id ordinalmente più alto, che nelle famiglie versionate coincide con la versione più recente (gemini-3.6 &gt; gemini-2.0); (4) se niente combacia, il primo id sopravvissuto al filtro — MAI null se l'elenco non è vuoto: un pilota automatico che si arrende non è un pilota automatico.

| | Firma | Descrizione |
|---|---|---|
| `m` | `string? Pick(string provider, IReadOnlyList&lt;string&gt; models)` | Il modello scelto per il provider, o null solo se l'elenco è vuoto/tutto non-chat. |

## `ProcioneMGR/Services/Llm/Narration/DigestNarrator.cs`

### 🔌 `IDigestNarrator`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;string?&gt; NarrateAsync(DigestData data, CancellationToken ct = default)` | Un paragrafo in italiano che riassume la giornata della flotta. null = nessuna narrazione (AI spenta, senza chiave, breaker aperto, budget esaurito, errore): il digest esce ESATTAMENTE come uscirebbe senza questa funzio… |

### 📦 `DigestNarrator` `(`

> [G9] La narrativa di sintesi in cima al digest giornaliero. Additiva per costruzione : il digest strutturato resta quello di prima, riga per riga. Questa aggiunge un paragrafo SOPRA, e la sua assenza non è un guasto — non si notifica, non si ritenta, non si dichiara. Il dead-man's-switch del digest (se non arriva, la piattaforma è muta) non deve dipendere da un provider AI. Il vincolo che conta : il paragrafo non deve contraddire i numeri che stanno sotto. Non si può verificare a macchina in generale, ma si può togliere l'occasione: il prompt riceve le stesse righe che finiranno nel messaggio, e chiede esplicitamente di non introdurre numeri che non ci sono. Il testo esce SOPRA i dati, non al loro posto, così il lettore ha sempre la fonte accanto alla sintesi.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string GuardPath` | Etichetta del path per metriche, breaker e budget (AF1). |
| `k` | `int MaxChars` | Tetto di lunghezza: un digest si legge sul telefono appena svegli. |
| `m` | `Task&lt;string?&gt; NarrateAsync(DigestData data, CancellationToken ct = default)` | — |
| `m` | `string BuildPrompt(DigestData data)` | Il prompt: le STESSE righe che il lettore troverà sotto. Puro e ispezionabile dai test. |
| `m` | `string Clean(string? raw)` | Ripulisce la risposta: via il markdown accidentale, una riga sola, e taglio duro alla lunghezza massima. Un modello prolisso non deve poter allungare un messaggio che si legge sul telefono. Pubblico per i test. |

## `ProcioneMGR/Services/Llm/Narration/PostMortemAnalyzer.cs`

### 📦 `PostMortemCauses`

> [G4] Il MENÙ CHIUSO delle cause di un'operazione andata male. L'AI può scegliere solo qui dentro: fuori menù, JSON rotto, timeout o assenza ⇒ , esattamente come il comitato AF3 ricade sul suo default.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string AdverseRegime` | Il mercato era in un regime diverso da quello in cui la strategia è stata validata. |
| `k` | `string TightStop` | Lo stop è stato colpito e poi il prezzo è tornato a favore: protezione troppo stretta. |
| `k` | `string DegradedSignal` | Le entrate non hanno più il margine che avevano in holdout. |
| `k` | `string CostsDominate` | Il lordo era positivo, i costi l'hanno mangiato. CALCOLABILE: non serve l'AI. |
| `k` | `string NormalNoise` | Dentro la variabilità attesa: non c'è niente da spiegare. |
| `k` | `string Liquidation` | Chiusura forzata dall'exchange. CALCOLABILE: non serve l'AI. |
| `k` | `string Inconclusive` | Il default deterministico: nessuno ha saputo dire di più. |
| `p` | `IReadOnlyList&lt;string&gt; AiSelectable` | Le voci che l'AI può scegliere (le calcolabili restano al codice). |
| `p` | `IReadOnlyList&lt;string&gt; All` | Tutte le voci ammesse in . |
| `m` | `bool IsValid(string? cause)` | — |
| `m` | `string Label(string cause)` | — |

### 🧾 `TradeFacts` `(`

> I fatti oggettivi di un'operazione: tutto da TradeRecord , niente di interpretato.

### 📦 `PostMortemAnalyzer`

> [G4] La parte DETERMINISTICA del post-mortem: ricava i fatti da un trade e, dove la causa è aritmetica, la stabilisce senza interpellare nessuna AI. È lo stesso principio di G6: ciò che il codice sa calcolare, lo calcola il codice. L'AI serve solo dove serve davvero un'interpretazione — e anche lì sceglie dentro un menù.

| | Firma | Descrizione |
|---|---|---|
| `m` | `TradeFacts Extract(TradeRecord trade, decimal feePercent)` | Estrae i fatti. Il lordo si stima aggiungendo al netto il costo di andata e ritorno ( per gamba): serve solo a distinguere «il segnale era buono ma i costi l'hanno mangiato» da «il segnale era sbagliato», e la stima è d… |
| `m` | `string? DeterministicCause(TradeFacts facts)` | La causa che il CODICE sa stabilire da solo, o null se serve un'interpretazione. Quando restituisce una causa, l'AI non viene interpellata affatto: è aritmetica, e pagare un LLM per confermarla sarebbe spreco (oltre che… |

## `ProcioneMGR/Services/Llm/Narration/PostMortemService.cs`

### 📦 `PostMortemOptions`

> [G4] Opzioni del post-mortem, sezione PostMortem . Default SPENTO.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Accende la scrittura dei post-mortem. Spento: nessuna riga, nessuna chiamata AI. |
| `p` | `decimal LossThresholdPercent` | Perdita percentuale oltre la quale un trade merita un post-mortem (valore POSITIVO: 1.0 = perdite oltre l'1%). Sotto soglia si tace: non ogni perdita è una lezione. Interazione da conoscere : la causa deterministica «co… |
| `p` | `bool UseAi` | Chiede anche la prosa e la classificazione all'AI. Spento = solo le cause calcolabili dal codice. |
| `p` | `int MaxPerRun` | Quanti post-mortem al massimo per giro, per non trasformare un arretrato in una bolletta. |
| `p` | `int CommitteeContextCount` | Quanti post-mortem recenti passare al comitato come contesto (0 = non passarne). |

### 🔌 `IPostMortemService`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;int&gt; AnalyzeRecentAsync(CancellationToken ct = default)` | Analizza i trade in perdita non ancora analizzati. Restituisce quanti post-mortem ha scritto. Idempotente per trade (indice unico su TradeRecordId ). |
| `m` | `Task&lt;string&gt; BuildCommitteeContextAsync(int laneId, CancellationToken ct = default)` | Il contesto per il comitato su una corsia: il conteggio delle cause recenti, in una riga. Stringa VUOTA se non c'è nulla — mai una frase che finge di sapere. |
| `m` | `Task&lt;IReadOnlyList&lt;TradePostMortem&gt;&gt; GetRecentAsync(int limit, CancellationToken ct = default)` | Gli ultimi post-mortem, per la pagina. |

### 📦 `PostMortemService` `(`

> [G4] Scrive il post-mortem delle operazioni chiuse in perdita. L'ordine dei fattori è il punto : prima i fatti (da TradeRecord ), poi la causa che il CODICE sa calcolare da solo; solo se resta un dubbio si interpella l'AI, e anche allora sceglie dentro un menù chiuso. Se l'AI non c'è, non risponde o esce dal menù, la causa è Inconcludente — un default deterministico, mai un'invenzione. Confine : questo servizio scrive righe di testo e una classificazione. Non ha fra le dipendenze nulla che possa aprire, chiudere o dimensionare una posizione.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string GuardPath` | Etichetta del path per metriche, breaker e budget (AF1). |
| `m` | `Task&lt;int&gt; AnalyzeRecentAsync(CancellationToken ct = default)` | — |
| `m` | `string BuildPrompt(TradeFacts f)` | Il prompt: solo fatti, e il menù delle risposte ammesse. Puro, ispezionabile dai test. |
| `m` | `(string? Cause, string Text) ParseVerdict(string raw)` | Interpreta il verdetto e RIVALIDA la causa contro il menù: una voce inventata vale come nessuna risposta (stessa disciplina del comitato AF3). Pubblico per i test. |
| `m` | `Task&lt;string&gt; BuildCommitteeContextAsync(int laneId, CancellationToken ct = default)` | — |
| `m` | `string Summarize(IReadOnlyList&lt;string&gt; causes)` | Il conteggio delle cause in una riga. Puro e testabile: è il TESTO che finisce nel prompt del comitato, e va guardato senza database. |
| `m` | `Task&lt;IReadOnlyList&lt;TradePostMortem&gt;&gt; GetRecentAsync(int limit, CancellationToken ct = default)` | — |

### 📦 `VerdictDto`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string? Cause` | — |
| `p` | `string? Text` | — |

## `ProcioneMGR/Services/Llm/Narration/PostMortemWorker.cs`

### 📦 `PostMortemWorker` `(`

> [G4] Il worker che scrive i post-mortem. Tick lento (l'analisi di un trade chiuso non ha fretta), spento per default, e mai bloccante: un errore si logga e si riprova al giro dopo. Vive nel guscio, come il resto del layer AI: legge trade chiusi e scrive righe di testo, non tocca il motore.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

## `ProcioneMGR/Services/Llm/Narration/RejectionDigest.cs`

### 📦 `RejectionCauses`

> [G6] Le classi di bocciatura di un candidato, ricavate dal che il motore scrive. Sono ETICHETTE per raggruppare, non un giudizio nuovo: il verdetto resta quello del gate che ha respinto.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string SharpeHoldout` | — |
| `k` | `string ContoTrade` | — |
| `k` | `string DeflatedSharpe` | — |
| `k` | `string PanelPbo` | — |
| `k` | `string Permutation` | — |
| `k` | `string NullTwin` | — |
| `k` | `string MonteCarlo` | — |
| `k` | `string BacktestFailed` | — |
| `k` | `string NoTrades` | — |
| `k` | `string Undeclared` | Bocciato senza motivo dichiarato: si dice, non si inventa una causa. |
| `k` | `string Other` | Motivo presente ma non riconosciuto dal classificatore. Esiste perché il classificatore legge stringhe scritte altrove: se il motore cambia un messaggio, i candidati finiscono qui e la UI lo dice — invece di essere sile… |
| `m` | `string Label(string cause)` | Etichetta leggibile in italiano, per la UI e per il prompt. |

### 🧾 `RejectionGroup` `(string Cause, string Label, int Count);`

> Quanti candidati sono caduti su una data causa.

### 🧾 `RejectedCandidateFacts` `(`

> Un candidato bocciato con i suoi numeri VERI. Nessun campo derivato dall'AI: questi valori vengono dal verdetto del motore e sono ciò contro cui una prosa sbagliata si smaschera da sola.

### 🧾 `RunRejectionDigest` `(`

> [G6] Il ritratto DETERMINISTICO delle bocciature di un run: quanti candidati, quanti sopravvissuti, per quale causa sono caduti gli altri, e i migliori fra i bocciati coi loro numeri. Il punto di questa classe : è calcolata in C# da dati già presenti, costa zero e NON richiede l'AI. La spiegazione in prosa ( ) si appoggia a questo, non lo sostituisce — così la funzione ha valore anche col layer AI spento, e la prosa viene sempre mostrata ACCANTO ai numeri veri: se l'AI scrive un numero sbagliato, si vede.

| | Firma | Descrizione |
|---|---|---|
| `p` | `RunRejectionDigest Empty` | — |
| `p` | `bool HasContent` | True se c'è qualcosa da raccontare (almeno un bocciato). |

### 📦 `RejectionDigestBuilder`

> [G6] Costruttore puro del . Nessuna dipendenza, nessun I/O.

| | Firma | Descrizione |
|---|---|---|
| `k` | `int DefaultTopN` | Quanti candidati bocciati riportare per esteso. Oltre, si contano soltanto. |
| `m` | `string Classify(string? rejectReason)` | Classifica un nella sua causa. Legge PREFISSI delle stringhe scritte da ModelStages e NullTwinValidationStage . È un accoppiamento che va dichiarato invece che nascosto: se un messaggio del motore cambia, il candidato f… |
| `m` | `RunRejectionDigest Build(IReadOnlyList&lt;ValidatedCandidate&gt;? candidates, int topN = DefaultTopN)` | Costruisce il ritratto. limita SOLO quanti bocciati vengono riportati per esteso: i conteggi per causa coprono sempre tutti. L'ordine dei «migliori fra i bocciati» è lo Sharpe holdout decrescente, e si chiama così per o… |

## `ProcioneMGR/Services/Llm/Narration/RejectionExplainService.cs`

### 🔌 `IRejectionExplainService`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;RunRejectionDigest&gt; GetDigestAsync(Guid runId, CancellationToken ct = default)` | Il ritratto DETERMINISTICO delle bocciature di un run, calcolato dall'artifact ValidatedCandidates . Non chiama l'AI, non costa nulla, funziona sempre — se il run non ha verdetti leggibili. |
| `m` | `Task&lt;RejectionNarration?&gt; GetNarrationAsync(Guid runId, CancellationToken ct = default)` | La narrazione già prodotta per il run, se c'è. Nessuna chiamata all'AI. |
| `m` | `Task&lt;RejectionNarration?&gt; ExplainRunAsync(Guid runId, bool force = false, CancellationToken ct = default)` | Produce (e persiste) la narrazione del run. Idempotente: se esiste già non richiama l'AI, a meno di . Restituisce la narrazione, oppure null se non è stato possibile produrla — nel qual caso il digest deterministico res… |
| `m` | `Task&lt;IReadOnlyList&lt;RunRejectionSummary&gt;&gt; GetRecentAsync(int limit, CancellationToken ct = default)` | I run completati più recenti che hanno verdetti leggibili, col loro digest deterministico e la narrazione se già prodotta. Serve alla pagina: il digest si vede ANCHE col layer AI spento, ed è il motivo per cui questa li… |

### 🧾 `RunRejectionSummary` `(`

> Un run con le sue bocciature: numeri sempre, prosa se c'è.

### 📦 `RejectionExplainService` `(`

> [G6] Collega il digest deterministico, il narratore AI e la persistenza. Confine: legge verdetti di candidati GIÀ respinti e scrive un artifact di testo. Non tocca corsie, ordini, parametri o soglie — non ha nemmeno i servizi per farlo fra le dipendenze.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;RunRejectionDigest&gt; GetDigestAsync(Guid runId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;RejectionNarration?&gt; GetNarrationAsync(Guid runId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;RejectionNarration?&gt; ExplainRunAsync(Guid runId, bool force = false, CancellationToken ct = default)` | — |
| `m` | `Task&lt;IReadOnlyList&lt;RunRejectionSummary&gt;&gt; GetRecentAsync(int limit, CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Llm/Narration/RejectionNarrator.cs`

### 🧾 `RejectionNote` `(string Key, string Text);`

> Una nota in prosa riferita a UN candidato bocciato, identificato dalla sua chiave.

### 📦 `RejectionNarration`

> [G6] La spiegazione in prosa delle bocciature di un run. Additiva per costruzione: il resta la fonte dei numeri, questa aggiunge solo parole.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Summary` | — |
| `p` | `List&lt;RejectionNote&gt; Notes` | — |
| `p` | `string ModelUsed` | Il modello che ha DAVVERO risposto (col failover può non essere quello attivo). |
| `p` | `DateTime CreatedAtUtc` | — |
| `p` | `int DiscardedNotes` | Quante note l'AI ha prodotto riferite a candidati INESISTENTI (scartate). Vedi . |

### 🔌 `IRejectionNarrator`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;RejectionNarration?&gt; NarrateAsync(RunRejectionDigest digest, CancellationToken ct = default)` | Chiede al provider attivo di raccontare le bocciature del digest. null = nessuna narrazione (AI spenta, senza chiave, breaker aperto, budget esaurito, errore, o digest vuoto): il chiamante mostra comunque il digest dete… |

### 📦 `RejectionNarrator` `(`

> [G6] Trasforma i numeri di un in poche righe di italiano piano. Perché è sicuro per costruzione : i candidati di cui parla sono GIÀ stati respinti dai gate e non sono mai stati schierati; non esiste percorso di codice per cui questo testo torni a toccare una decisione. La sicurezza sta QUI — nell'assenza di un percorso di ritorno — non nella pulizia dell'input. Sull'input, per onestà : il prompt è quasi tutto numeri calcolati dal motore, ma non è testo "sterile": i nomi dei simboli vengono in ultima analisi dagli exchange, e RejectReason può contenere il messaggio di un'eccezione ( "Backtest fallito: {ex.Message}" ). Dare per scontato che nulla di ostile possa entrare sarebbe una rassicurazione, non una garanzia. La difesa vera è sull'OUTPUT , e regge anche se l'input fosse avvelenato: le note tornano indicizzate per CHIAVE e ogni chiave che non è fra quelle inviate viene scartata (cont…

| | Firma | Descrizione |
|---|---|---|
| `k` | `string GuardPath` | Etichetta del path per metriche, breaker e budget (AF1). |
| `m` | `Task&lt;RejectionNarration?&gt; NarrateAsync(RunRejectionDigest digest, CancellationToken ct = default)` | — |
| `m` | `string BuildPrompt(RunRejectionDigest digest)` | Costruisce il prompt dai SOLI numeri del digest. Puro e deterministico: è la parte che i test possono ispezionare senza rete. |
| `m` | `RejectionNarration Parse(string raw, IReadOnlySet&lt;string&gt; allowedKeys)` | Interpreta la risposta e SCARTA ogni nota la cui chiave non sia fra quelle inviate: un candidato inventato non deve poter comparire in pagina accanto a quelli veri. Pubblico per i test. |

### 📦 `NarrationDto`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string? Summary` | — |
| `p` | `List&lt;NoteDto&gt;? Notes` | — |

### 📦 `NoteDto`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string? Key` | — |
| `p` | `string? Text` | — |

## `ProcioneMGR/Services/Llm/NvidiaLlmClient.cs`

### 🔌 `IModelCatalogProvider`

> Chi sa elencare i modelli disponibili PER LA CHIAVE configurata. Interfaccia separata da di proposito: aggiungerlo lì costringerebbe ogni fake dei test a implementarlo, e non tutti i provider ce l'hanno. Il caso che l'ha resa necessaria (2026-08-02): Google ha ritirato gemini-2.5-flash per le chiavi nuove e perfino l'alias "-latest" puntava al modello morto — l'unico elenco affidabile è quello che l'API restituisce ALLA TUA chiave.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyList&lt;string&gt;&gt; ListModelsAsync(CancellationToken ct)` | Gli id dei modelli disponibili per la chiave corrente, ordinati. Errori col contratto "&lt;PROVIDER&gt; HTTP &lt;code&gt;:". |

### 📦 `OpenAiCompatibleLlmClient` `(`

> Base di OGNI provider che parla il dialetto OpenAI-compatible ( POST {base}/chat/completions , Bearer, tre campi JSON). Nata come NvidiaLlmClient ed elevata a base quando il principio §1.2 del PRD («un provider nuovo = URL+chiave, zero client nuovi») è passato dalla promessa alla prova: NVIDIA, Google Gemini (layer compat), Groq e il router HuggingFace differiscono SOLO per nome, base URL e modello — una sottoclasse a testa, cinque righe l'una. Nessun SDK: un HttpClient nudo è meno fragile di quattro dipendenze. La chiave viene da (DB cifrato → env del provider). Timeout e retry NON vivono qui: la disciplina è del , identica per ogni provider — un breaker per il layer, non uno per client.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string HttpClientName` | Nome del client HTTP registrato in Program.cs (timeout largo: i modelli con reasoning sono lenti). Condiviso da tutti i provider compat. |
| `p` | `string ProviderName` | Nome canonico del provider (una voce di ). |
| `m` | `(string BaseUrl, string Model) Endpoint(LlmOptions options)` | Base URL e modello del provider, letti a OGNI chiamata (hot-reload). |
| `p` | `bool IsConfigured` | — |
| `p` | `string Model` | — |
| `m` | `Task&lt;string&gt; CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)` | — |
| `m` | `Task&lt;IReadOnlyList&lt;string&gt;&gt; ListModelsAsync(CancellationToken ct)` | GET {base}/models del dialetto OpenAI-compatible: gli id dei modelli disponibili per la chiave corrente. Stessa autenticazione e stesso contratto d'errore delle chiamate di completamento. |

### 📦 `NvidiaLlmClient` `(`

> NVIDIA build.nvidia.com ( integrate.api.nvidia.com/v1 , Bearer nvapi-… ).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ProviderName` | — |
| `m` | `(string BaseUrl, string Model) Endpoint(LlmOptions options)` | — |

### 📦 `GeminiLlmClient` `(`

> Google Gemini via layer OpenAI-compatible ( generativelanguage.googleapis.com/v1beta/openai ).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ProviderName` | — |
| `m` | `(string BaseUrl, string Model) Endpoint(LlmOptions options)` | — |

### 📦 `GroqLlmClient` `(`

> Groq ( api.groq.com/openai/v1 ): inferenza a bassissima latenza su modelli aperti.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ProviderName` | — |
| `m` | `(string BaseUrl, string Model) Endpoint(LlmOptions options)` | — |

### 📦 `HuggingFaceLlmClient` `(`

> Router di inferenza HuggingFace ( router.huggingface.co/v1 ): molti modelli aperti dietro un endpoint solo.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ProviderName` | — |
| `m` | `(string BaseUrl, string Model) Endpoint(LlmOptions options)` | — |

### 🔌 `ILlmCompletionInfo`

> Espone quale modello ha DAVVERO servito l'ultima risposta (col failover può non essere quello attivo). Interfaccia separata: i fake dei test non devono implementarla.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string? LastCompletionModel` | — |

### 📦 `DelegatingLlmClient` `(`

> L' registrato: instrada OGNI chiamata al provider scelto in (hot-reload: cambiare provider dal pannello ha effetto alla chiamata successiva, senza riavvio). Tutto ciò che consuma ILlmClient — supervisore, guard, worker, pannello — resta ignaro di quale provider stia parlando: è il punto dell'astrazione. [Failover 2026-08-02] Se la chiamata al provider attivo fallisce (qualunque errore che non sia una cancellazione), prova DA SOLO i provider di , nell'ordine, saltando chi non ha chiave e chi è già stato tentato — con più AI configurate, un 503 del free tier non ferma advisory né sentiment. Ogni salto è dichiarato nel log; il modello che ha davvero risposto è in . Il breaker del guard, a valle, scatta solo se falliscono TUTTI: coerente col suo contratto — è il breaker del layer, e il layer ora è la federazione. Senza (vecchi harness) la catena non è risolvibile e resta il comportamento st…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string? LastCompletionModel` | — |
| `p` | `bool IsConfigured` | — |
| `p` | `string Model` | — |
| `m` | `Task&lt;string&gt; CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)` | — |

## `ProcioneMGR/Services/Llm/PipelineSupervisor.cs`

### 📦 `LlmArtifactKinds`

> Kind dell'artifact che memorizza l'advisory AI di un run.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string Advisory` | — |
| `k` | `string AdvisoryComparison` | [Fase C] Il SECONDO parere (provider di confronto) di un run. Kind DISTINTO di proposito: worker, pannello e test filtrano su per contare/riprendere i run — un secondo artifact con lo stesso Kind li farebbe sbagliare tu… |
| `k` | `string RejectionExplanation` | [G6] La spiegazione in prosa dei candidati BOCCIATI di un run ( ). Kind proprio per la stessa ragione del secondo parere: l'anti-join del worker e i conteggi del pannello guardano , e un artifact in più con quel Kind li… |

### 🔌 `IPipelineSupervisor`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;bool&gt; SuperviseRunAsync(Guid runId, CancellationToken ct, bool forceProbe = false)` | Analizza un run completato: legge la sua PipelineRecommendation , chiede un parere all'LLM e persiste un come PipelineArtifact. Idempotente per run (non riscrive se un advisory esiste già). Un errore TRANSITORIO (credit… |
| `m` | `Task&lt;int&gt; DeleteErrorAdvisoriesAsync(DateTime since, CancellationToken ct)` | Elimina gli advisory in errore dei run completati da in poi, così il worker li rianalizza (l'idempotenza per-run altrimenti li blocca per sempre). Azione manuale-only dalla UI: gli errori più vecchi della finestra resta… |

### 📦 `PipelineSupervisor` `(`

> Layer AI di supervisione del ciclo di ricerca. CONFINE DI SICUREZZA NON NEGOZIABILE: questo servizio è solo advisory . Legge i risultati di un run e produce un parere testuale + suggerimenti sui parametri di caccia; NON avvia trading, NON passa mai in Live, NON tocca SafetyChecker né l'apertura di posizioni. Per costruzione non riceve in DI alcun servizio di esecuzione/trading: può solo leggere PipelineRun e scrivere un artifact di tipo advisory.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;bool&gt; SuperviseRunAsync(Guid runId, CancellationToken ct, bool forceProbe = false)` | — |
| `m` | `Task&lt;int&gt; DeleteErrorAdvisoriesAsync(DateTime since, CancellationToken ct)` | — |
| `m` | `SupervisorAdvisory ParseAdvisory(string raw)` | Estrae e deserializza l'oggetto JSON dalla risposta del modello, con tolleranza a testo attorno. |

### 📦 `AdvisoryDto`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string? Summary` | — |
| `p` | `string? Confidence` | — |
| `p` | `List&lt;string&gt;? DecisionsForUser` | — |
| `p` | `List&lt;SuggestionDto&gt;? ParameterSuggestions` | — |

### 📦 `SuggestionDto`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string? Parameter` | — |
| `p` | `string? CurrentOrObserved` | — |
| `p` | `string? Suggested` | — |
| `p` | `string? Rationale` | — |

## `ProcioneMGR/Services/Llm/SupervisorAdvisory.cs`

### 📦 `SupervisorAdvisory`

> Esito della supervisione AI di un run del pipeline: un parere LEGGIBILE per l'utente, più suggerimenti sui parametri di caccia e le decisioni che richiedono conferma umana. È solo advisory: non contiene azioni eseguibili, non avvia trading, non tocca SafetyChecker. Persistito come PipelineArtifact (Kind="LlmAdvisory") — nessuna nuova tabella.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Summary` | Riepilogo esecutivo in italiano, pronto da mostrare all'utente. |
| `p` | `List&lt;ParameterSuggestion&gt; ParameterSuggestions` | Aggiustamenti proposti ai parametri di caccia (proposte, non modifiche applicate). |
| `p` | `List&lt;string&gt; DecisionsForUser` | Decisioni che l'AI segnala come da confermare esplicitamente dall'utente. |
| `p` | `string Confidence` | "bassa" \| "media" \| "alta". |
| `p` | `string ModelUsed` | Modello usato (tracciabilità). |
| `p` | `bool IsError` | True se l'advisory è il risultato di un errore (LLM non raggiungibile, parsing fallito…). |
| `p` | `DateTime CreatedAtUtc` | — |

### 📦 `ParameterSuggestion`

> Un singolo suggerimento di aggiustamento parametro (proposta, mai applicata in automatico).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Parameter` | — |
| `p` | `string CurrentOrObserved` | — |
| `p` | `string Suggested` | — |
| `p` | `string Rationale` | — |

# `Services/Agents/`

## `ProcioneMGR/Services/Agents/ClaudeSupervisorAgent.cs`

### 📦 `ClaudeSupervisorAgent` `(`

> Optional Claude-backed supervisor. It reuses the existing (Anthropic SDK, key from ANTHROPIC_API_KEY only) through the shared (timeout, circuit breaker, error classification — so the veto path both consults and feeds the same breaker as the advisory path) and degrades gracefully: if the key is missing, the breaker is open, the call times out, or anything throws/parses wrong, it

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Provider` | — |
| `m` | `SupervisorJudgment Parse(string raw)` | Parses the model's JSON judgment, tolerant of surrounding text. Public for unit testing. |

### 📦 `JudgmentDto`

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool? ApproveReplacement` | — |
| `p` | `string? Summary` | — |
| `p` | `List&lt;string&gt;? Suggestions` | — |
| `p` | `List&lt;string&gt;? Concerns` | — |
| `p` | `string? Reasoning` | — |

## `ProcioneMGR/Services/Agents/DelegatingSupervisorAgent.cs`

### 📦 `DelegatingSupervisorAgent` `(`

> L' registrato in DI: sceglie Logging/Claude PER CHIAMATA da PipelineSupervisor:Provider (hot-reload). Prima la scelta avveniva una volta sola al boot in Program.cs: cambiare provider richiedeva un riavvio e la UI non poteva esporlo.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Provider` | — |

## `ProcioneMGR/Services/Agents/IPipelineSupervisorAgent.cs`

### 🔌 `IPipelineSupervisorAgent`

> Qualitative AI supervisor of the continuous re-apply loop. Given a completed pipeline run and the current vs candidate ensemble, it produces a readable judgment plus a VETO signal ( ) that the scheduler ANDs with the objective verdict. SAFETY (non-negotiable): the agent can only ever VETO a replacement the metrics already approved — it can never FORCE one, never start trading, never switch to Live, never touch SafetyChecker. It receives no execution/trading service in DI. When it fails or is not configured it

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Provider` | Provider name for UI/telemetry ("Logging" \| "Claude"). |

### 📦 `SupervisorJudgment`

> The supervisor's verdict on a run + proposed ensemble swap. Advisory + a metrics-deferring veto flag only.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool ApproveReplacement` | False = VETO the replacement even if the metrics approve it. True = no objection (the metrics decide). Default true so failures/absence never block a metrically-justified swap. |
| `p` | `string Summary` | Readable executive summary (Italian), shown in the UI. |
| `p` | `IReadOnlyList&lt;string&gt; Suggestions` | Adjustment suggestions (proposals, never auto-applied). |
| `p` | `IReadOnlyList&lt;string&gt; Concerns` | Concerns/risks the agent flags. |
| `p` | `string Reasoning` | Internal reasoning (debug, expandable in UI). |
| `p` | `string Provider` | Provider that produced this judgment ("Logging" \| "Claude"). |
| `p` | `DateTime AnalyzedAt` | — |

### 📦 `SupervisorAgentOptions`

> Options for the supervisor agent (bound from the PipelineSupervisor config section).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Provider` | "Logging" (default, no AI) or "Claude" (uses the existing ILlmClient / ANTHROPIC_API_KEY). |
| `p` | `int TimeoutSeconds` | Hard timeout for a single Claude analysis; on timeout the agent falls back to "approve" (defer to metrics). |

## `ProcioneMGR/Services/Agents/LoggingSupervisorAgent.cs`

### 📦 `LoggingSupervisorAgent` `(ILogger&lt;LoggingSupervisorAgent&gt; logger) : IPipelineSupervisorAgent`

> Default supervisor: no AI. It logs the run and always approves the replacement, delegating the entire decision to the objective . This is the fallback when the user has not configured a Claude API key — the platform is fully operational without any AI layer.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Provider` | — |

# `Services/Sentiment/`

## `ProcioneMGR/Services/Sentiment/DelegatingSentimentScorer.cs`

### 📦 `SentimentScorerProviders`

> Nomi canonici degli scorer di sentiment. Stringhe (non enum): un provider nuovo non tocca lo schema di config.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string Keyword` | — |
| `k` | `string Llm` | — |
| `k` | `string Onnx` | — |
| `p` | `IReadOnlyList&lt;string&gt; Known` | Gli scorer noti alla UI di configurazione, nell'ordine di presentazione. |

### 📦 `DelegatingSentimentScorer` `(`

> L' registrato: instrada OGNI chiamata sullo scorer scelto in (hot-reload: cambiare scorer dal pannello ha effetto alla notizia successiva, senza riavvio). Stesso pattern di DelegatingLlmClient : i consumatori (AltDataSyncService) restano ignari di quale scorer stia lavorando. Default = comportamento storico, zero costi: passare all'LLM è una scelta esplicita dell'operatore (è il consenso al costo per chiamata).

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;decimal&gt; ScoreAsync(string title, string? summary, CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Sentiment/HashingTextVectorizer.cs`

### 📦 `HashingTextVectorizer`

> Vettorizzatore testuale a feature hashing, PURO e DETERMINISTICO: minuscole → token alfanumerici (≥2 caratteri) → unigrammi + bigrammi → hash FNV-1a a 32 bit modulo la dimensione → conteggi, normalizzati L2. È il punto architetturale del pilota ONNX (PRD-ONNX-SENTIMENT-PILOT): la parte testuale resta codice C# CONDIVISO fra addestramento e inferenza — il modello ONNX riceve solo il vettore numerico. Questo elimina per costruzione il rischio di parità del tokenizer (un tokenizer subword sbagliato produce punteggi plausibili ma errati, peggio di un crash) e il rischio di copertura degli operatori nell'export ML.NET→ONNX (le trasformazioni testuali di ML.NET non sono esportabili; un ingresso già-vettoriale sì). MAI usare string.GetHashCode() qui: è randomizzato per processo, e il modello addestrato in un processo darebbe risposte diverse in un altro. FNV-1a è fisso per sempre.

| | Firma | Descrizione |
|---|---|---|
| `k` | `int Dimension` | Dimensione del vettore (2^15): abbastanza larga da tenere basse le collisioni su un vocabolario di notizie, abbastanza piccola da addestrare in secondi. |
| `m` | `float[] Vectorize(string title, string? summary)` | Vettorizza titolo+sommario in un vettore denso L2-normalizzato di dimensione . |
| `m` | `List&lt;string&gt; Tokenize(string text)` | Token alfanumerici in minuscolo, lunghezza ≥ 2 (i singoli caratteri sono rumore). |
| `m` | `uint Fnv1a(string token)` | FNV-1a a 32 bit sul token UTF-8: stabile fra processi, piattaforme e versioni. |

## `ProcioneMGR/Services/Sentiment/ISentimentScorer.cs`

### 🔌 `ISentimentScorer`

> Assegna un punteggio di sentiment a un testo. Interfaccia pensata per essere intercambiabile: (lessicale, testabile senza alcuna chiave API), (LLM via provider attivo del layer AI) e (inferenza locale) — stesso contratto, i consumatori restano ignari (stesso principio di IReturnPredictor / IPortfolioOptimizer ). Il contratto è asincrono perché un'implementazione può fare I/O di rete (LLM). Chi implementa NON deve mai lasciar propagare un fallimento del canale: un errore va assorbito ripiegando su un punteggio calcolabile localmente (vedi ) — il chiamante ( AltDataSyncService ) tratta comunque un'eccezione come "salta l'elemento e ritenta al prossimo giro", mai come fallimento dell'intera sync.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;decimal&gt; ScoreAsync(string title, string? summary, CancellationToken ct = default)` | Punteggio in [-1, +1]: negativo = notizia ribassista, positivo = rialzista, 0 = neutra/non determinabile. |

## `ProcioneMGR/Services/Sentiment/KeywordSentimentScorer.cs`

### 📦 `KeywordSentimentScorer` `: ISentimentScorer`

> Sentiment lessicale: conta parole positive/negative (word-boundary) nel testo e restituisce (positive-negative)/(positive+negative). Semplicistico ma reale e testabile SENZA alcuna chiave API — è il fallback sempre disponibile del layer sentiment: gli scorer che dipendono da un canale esterno ( ) ripiegano qui quando il canale manca.

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal Score(string title, string? summary)` | Percorso sincrono, senza I/O per costruzione: resta pubblico perché è il fallback che gli altri scorer invocano inline (e il riferimento indipendente nei loro test). |
| `m` | `Task&lt;decimal&gt; ScoreAsync(string title, string? summary, CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Sentiment/LlmSentimentScorer.cs`

### 📦 `LlmSentimentScorer` `(`

> basato sul layer LLM multi-provider (PRD-AI-MULTIPROVIDER Fase B): chiede al provider ATTIVO ( = DelegatingLlmClient) un punteggio in [-1,+1] per titolo+sommario. Ogni chiamata passa dal condiviso (path "sentiment", metriche separate): se il provider è giù il breaker del layer sospende anche questo percorso — coerente col principio "il breaker è del layer, non del provider". Mai un'eccezione verso il chiamante : qualunque esito non-Ok (chiave assente, breaker aperto, errore, risposta non interpretabile) ripiega in silenzio operoso sul — il punteggio arriva comunque, e il log dice da dove. La sync delle notizie non deve MAI fermarsi per un problema del canale AI. Costo dichiarato (principio §1.4 del PRD): il percorso vivo scora solo le notizie NUOVE del giro di sync (≈ decine di titoli l'ora, prompt corti); il replay storico del pannello di confronto usa (N titoli in UNA chiamata) propri…

| | Firma | Descrizione |
|---|---|---|
| `k` | `string GuardPath` | Etichetta metrica del guard: separa i conteggi dal path advisory/veto. |
| `k` | `int BatchSize` | Notizie per chiamata nel percorso batch: abbastanza da tagliare i costi di un ordine di grandezza, abbastanza poche da non degradare la qualità del giudizio. |
| `m` | `Task&lt;decimal&gt; ScoreAsync(string title, string? summary, CancellationToken ct = default)` | — |
| `m` | `bool TryParseScore(string raw, out decimal score)` | Estrae il primo numero decimale dalla risposta (tollera testo attorno e la virgola come separatore — i modelli rispondono in italiano) e lo blocca in [-1,+1]. |
| `m` | `IReadOnlyList&lt;decimal&gt;? TryParseScoreArray(string raw, int expectedCount)` | Estrae dall'output l'array JSON di numeri e pretende ESATTAMENTE la lunghezza attesa: un array più corto o più lungo è disallineato (non si sa più quale punteggio è di chi) e vale come fallimento del batch — mai un'asse… |

## `ProcioneMGR/Services/Sentiment/Metrics/BinanceFuturesSentimentClient.cs`

### 📦 `BinanceFuturesSentimentClient` `(`

> Dati pubblici di posizionamento dai futures USDS-M di Binance — API senza chiave (limite IP 1000 req/5min; questa fonte ne usa ~5 per simbolo ogni tick da 30 min): global long/short account ratio, top-trader long/short position ratio, taker buy/sell volume ratio, open interest, funding rate. È POSIZIONAMENTO REALE sul venue dove la piattaforma trada: la fonte con il riscontro più solido della ricerca 2026-07 — funding e positioning estremi sono segnali contrarian di squeeze/reversal documentati. ATTENZIONE STORICO: Binance conserva SOLO gli ultimi 30 giorni di queste serie — i buchi sono irrecuperabili. Per questo il worker di raccolta è default ON e ogni fetch prende limit=48 punti orari (recupera fino a 48h di downtime; oltre, il buco resta e i baseline lo tollerano). FALLBACK GEO designato (non implementato): gli endpoint sono market data pubblico e l'Italia non è bloccata (il repo c…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `Task&lt;IReadOnlyList&lt;SentimentMetricSample&gt;&gt; FetchLatestAsync(CancellationToken ct)` | — |
| `m` | `string ToBaseTicker(string market)` | "BTCUSDT" → "BTC": ticker base, compatibile con SymbolsJson delle news e l'OHLCV. |
| `m` | `IReadOnlyList&lt;SentimentMetricSample&gt; ParseRatioSeries(string json, string ratioField, string metric, string baseSymbol)` | Parsing puro delle serie "ratio" (globalLongShortAccountRatio / topLongShortPositionRatio / takerlongshortRatio): array di oggetti con il campo ratio come stringa + timestamp in ms. Un elemento malformato si salta, non … |
| `m` | `IReadOnlyList&lt;SentimentMetricSample&gt; ParseOpenInterestSeries(string json, string baseSymbol)` | Parsing puro di openInterestHist: due metriche per punto (contratti e valore USDT). |
| `m` | `IReadOnlyList&lt;SentimentMetricSample&gt; ParseFundingRates(string json, string baseSymbol)` | Parsing puro di /fapi/v1/fundingRate: funding in PERCENTO (×100, convenzione piattaforma). |

## `ProcioneMGR/Services/Sentiment/Metrics/FearGreedClient.cs`

### 📦 `FearGreedClient` `(IHttpClientFactory httpClientFactory) : IBackfillableMetricSource`

> Fear & Greed Index di alternative.me — API pubblica gratuita SENZA chiave (https://api.alternative.me/fng/), un punto al giorno 0 (extreme fear) - 100 (extreme greed). Fonte scelta dalla ricerca 2026-07: l'indice NON predice i ritorni giornalieri (reagisce ai prezzi), ma gli ESTREMI (≤20 / ≥80) hanno valore contrarian documentato su orizzonti multi-settimana — per questo alimenta i flag Extremes del composite, non un segnale diretto. Termini d'uso: attribuzione obbligatoria (link ad alternative.me, presente in /sentiment). Lo storico completo è scaricabile con limit=0 (~2500 punti): backfill una tantum via , poi limit=7 per tick.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `Task&lt;IReadOnlyList&lt;SentimentMetricSample&gt;&gt; FetchLatestAsync(CancellationToken ct)` | — |
| `m` | `Task&lt;IReadOnlyList&lt;SentimentMetricSample&gt;&gt; FetchFullHistoryAsync(CancellationToken ct)` | — |
| `m` | `IReadOnlyList&lt;SentimentMetricSample&gt; ParseFng(string json)` | Parsing puro della risposta /fng/ — testabile senza rete (fixture reale nei test). |

## `ProcioneMGR/Services/Sentiment/Metrics/ISentimentMetricSource.cs`

### 🧾 `SentimentMetricSample` `(DateTime TimestampUtc, string Metric, string Symbol, decimal Value);`

> Un punto di metrica prodotto da una fonte, pronto per SentimentMetricPoints . Timestamp del punto (dalla fonte), UTC. Nome della metrica (costanti in SentimentMetrics ). Ticker base ("BTC"); stringa vuota = mercato intero. Valore (convenzioni in SentimentMetricPoint : funding in percento ×100).

### 🔌 `ISentimentMetricSource`

> Una fonte di serie numeriche di market mood (Sentiment 2.0) — l'equivalente "denso" di IAltDataSource , che resta per gli eventi testuali (notizie). Stesso principio additivo: una nuova fonte è una nuova implementazione registrata, senza toccare schema né orchestrazione.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | Nome della fonte (colonna Source, costanti in SentimentMetricSources ). |
| `m` | `Task&lt;IReadOnlyList&lt;SentimentMetricSample&gt;&gt; FetchLatestAsync(CancellationToken ct)` | Gli ultimi punti disponibili (finestra corta: la dedupe mangia le sovrapposizioni). |

### 🔌 `IBackfillableMetricSource` `: ISentimentMetricSource`

> Fonte che sa fornire anche l'INTERO storico: usata dal sync service una sola volta, quando la tabella non ha ancora righe per quella fonte (es. Fear & Greed: ~2500 punti giornalieri).

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyList&lt;SentimentMetricSample&gt;&gt; FetchFullHistoryAsync(CancellationToken ct)` | — |

## `ProcioneMGR/Services/Sentiment/Metrics/SentimentMetricSyncService.cs`

### 🔌 `ISentimentMetricSyncService`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;int&gt; SyncAllAsync(CancellationToken ct)` | Interroga tutte le fonti di metriche e salva i punti nuovi. Restituisce quanti ne ha inseriti. |

### 📦 `SentimentMetricSyncService` `(`

> Orchestratore delle fonti di metriche sentiment — specchia AltDataSyncService : fetch paralleli, una fonte che fallisce viene saltata con warning + health rossa (mai far fallire il batch), dedupe applicativa sulla chiave (Source, Metric, Symbol, TimestampUtc) con l'indice unico come backstop. Le fonti (Fear & Greed) fanno il backfill dell'INTERO storico la prima volta (zero righe per quella Source), poi solo gli ultimi punti.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;int&gt; SyncAllAsync(CancellationToken ct)` | — |

## `ProcioneMGR/Services/Sentiment/OnnxSentimentPilotService.cs`

### 🧾 `OnnxPilotTrainResult` `(`

> Esito dell'addestramento del pilota ONNX, con le misure che il pannello dichiara.

### 📦 `OnnxSentimentPilotService` `(`

> Addestra il pilota ONNX del sentiment (PRD-ONNX-SENTIMENT-PILOT, Livello 1) con una filiera 100% C#: notizie testuali già in archivio → etichette deboli dal lessico ( ) → vettori → regressione lineare ML.NET (SDCA) → export ONNX ( ConvertToOnnx ) → verifica di PARITÀ fra le predizioni ML.NET e l'inferenza ONNX Runtime attraverso lo scorer REALE. Onestà dichiarata : il Livello 1 è una DISTILLAZIONE del lessico — il suo scopo è provare la filiera di inferenza locale (export, caricamento, parità, integrazione col gate IC), non battere il lessico in segnale. La generalizzazione oltre le 25 parole (n-grammi co-occorrenti) è possibile ma va MISURATA nel pannello di confronto, mai presunta. Il Livello 2 (modello pre-addestrato esterno) resta gated dall'esito di questo pilota. Se la parità fallisce oltre la tolleranza il modello esportato viene ELIMINATO: un modello che inferisce diverso da com…

| | Firma | Descrizione |
|---|---|---|
| `k` | `double ParityTolerance` | Oltre questa differenza assoluta fra ML.NET e ONNX Runtime la parità è fallita. |

### 📦 `PilotRow`

| | Firma | Descrizione |
|---|---|---|
| `p` | `float[] Features` | — |
| `p` | `float Label` | — |

### 📦 `PilotPrediction`

| | Firma | Descrizione |
|---|---|---|
| `p` | `float Score` | — |
| `m` | `Task&lt;OnnxPilotTrainResult&gt; TrainAsync(CancellationToken ct)` | — |

## `ProcioneMGR/Services/Sentiment/OnnxSentimentScorer.cs`

### 📦 `OnnxSentimentScorer` `(`

> a inferenza LOCALE via ONNX Runtime (PRD-ONNX-SENTIMENT-PILOT, Livello 1): carica il modello .onnx del pilota (addestrato in ML.NET dentro l'app, esportato con ConvertToOnnx — filiera 100% C#, zero Python) e lo esegue in-process sulla CPU. Nessuna chiave API, nessun costo per chiamata, nessun rate limit: è il contraltare locale dello scorer LLM, dietro lo stesso contratto. La parte testuale (tokenizzazione + hashing) NON sta nel modello: è , lo stesso codice usato in addestramento — la parità train/inference è garantita per costruzione, non da un vocabolario da tenere allineato. Se il file del modello manca (mai addestrato, o percorso cambiato) lo scorer NON è un errore: ripiega sul lessico e lo dice nel log una volta per percorso. Il modello si addestra dal pannello in /sentiment (OnnxSentimentPilotService), che al termine chiama .

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsAvailable` | True se un modello è caricato e pronto a inferire (per la UI). |
| `p` | `string? LastLoadError` | PERCHÉ l'ultimo caricamento è fallito (null se mai fallito o poi riuscito). Un badge "non disponibile" senza causa è un controllo che non sa dire di no: la UI e il pilota lo mostrano. |
| `p` | `string ResolvedModelPath` | Percorso assoluto del file modello secondo la configurazione corrente. |
| `m` | `Task&lt;decimal&gt; ScoreAsync(string title, string? summary, CancellationToken ct = default)` | — |
| `m` | `void Reload()` | Ricarica il modello dal disco (dopo un nuovo addestramento, o un cambio percorso). |
| `m` | `void Dispose()` | — |

## `ProcioneMGR/Services/Sentiment/SentimentAlphaFactor.cs`

### 🧾 `ScoredNewsItem` `(DateTime PublishedUtc, decimal SentimentScore, IReadOnlyList&lt;string&gt; Symbols);`

> Una notizia già classificata/scorata, pronta per essere allineata alle candele.

### 📦 `SentimentAlphaFactor` `(IReadOnlyList&lt;ScoredNewsItem&gt; news, string? symbolFilter = null) : IAlphaFactor`

> Fattore alpha da sentiment (cap. 14, con LLM al posto di LDA/lessici tradizionali — qui il fallback lessicale finché non c'è una chiave LLM): media rolling del sentiment delle notizie pubblicate nelle ultime LookbackHours ore prima di ogni candela. DEVIAZIONE FLAGGATA (stesso trattamento di MlStrategy per IStrategy ): non è nello switch di perché richiede le notizie già scorate come dipendenza esterna (non rappresentabile come parametri decimali di default) — si costruisce direttamente passando le notizie, ma implementa comunque per restare compatibile con FactorEvaluator / DatasetBuilder / MlStrategy senza modifiche.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; p)` | — |

## `ProcioneMGR/Services/Sentiment/SentimentCompositeCalculator.cs`

### 📦 `SentimentCompositeCalculator`

> Calcolo PURO dello dai punti metrici e dai punteggi news: niente DB, niente clock implicito — completamente testabile. Regole: - z-score = (ultimo − media baseline) / σ baseline; con meno di osservazioni o σ=0 lo z è null (meglio nessun numero che un numero rumoroso); - il contributo di uno z al composite è z/2 clampato in [-1,+1] (z=±2, la soglia "estremo" di default, satura il contributo); - i pesi sono RINORMALIZZATI sui soli componenti disponibili: una fonte giù non distorce il composite, lo restringe; - l'open interest è contesto di ampiezza (flag), MAI parte del composite: dice "quanto è grossa la scommessa", non in che direzione.

| | Firma | Descrizione |
|---|---|---|
| `k` | `int MinBaselineObservations` | Sotto questa numerosità del baseline gli z-score non si calcolano. |
| `m` | `string FearGreedLabel(double value)` | Etichetta alternative.me-style dal valore 0-100. |
| `m` | `double? ZScore(IReadOnlyList&lt;SentimentMetricPoint&gt; series)` | z dell'ULTIMO punto vs l'intera serie nella finestra; null se serie corta o piatta. |

## `ProcioneMGR/Services/Sentiment/SentimentFeatureFactor.cs`

### 📦 `SentimentFeatureFactor` `(ISentimentNewsProvider newsProvider) : IAlphaFactor`

> Il fattore "Sentiment" come feature ML di FABBRICA (Sentiment 2.0, opt-in): risolve da solo la dipendenza dalle notizie via e deriva il filtro simbolo dalle candele ("BTC/USDT" → "BTC"), poi delega alla logica rolling anti-look-ahead di (che resta la classe usata direttamente da /sentiment). Caveat FactorCache: la chiave di cache è nome+parametri+impronta candele, quindi una serie sentiment aggiornata tra due chiamate identiche può restare stantia al massimo fino alla candela successiva — staleness ≤ 1 barra, innocua per training e inferenza.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `p` | `string DisplayName` | — |
| `p` | `FactorCategory Category` | — |
| `p` | `IReadOnlyList&lt;FactorParameterDefinition&gt; ParameterDefinitions` | — |
| `m` | `IReadOnlyList&lt;decimal?&gt; Compute(IReadOnlyList&lt;OhlcvData&gt; candles, IReadOnlyDictionary&lt;string, decimal&gt; p)` | — |

## `ProcioneMGR/Services/Sentiment/SentimentNewsProvider.cs`

### 🔌 `ISentimentNewsProvider`

> Fornisce alle feature ML lo snapshot in-memory delle notizie scorate (l'input che richiede come dipendenza esterna). Singleton con snapshot volatile: Compute dei fattori resta sincrono e senza I/O; il refresh avviene dopo ogni sync delle news (worker, stage pipeline, bottone UI — tutti passano da AltDataSyncService).

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;ScoredNewsItem&gt; Snapshot` | — |
| `m` | `Task RefreshAsync(CancellationToken ct)` | Ricarica lo snapshot dal DB (finestra = retention news). |

### 📦 `SentimentNewsProvider` `(`

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;ScoredNewsItem&gt; Snapshot` | — |
| `m` | `Task RefreshAsync(CancellationToken ct)` | — |

## `ProcioneMGR/Services/Sentiment/SentimentOptions.cs`

### 📦 `SentimentOptions`

> Opzioni di Sentiment 2.0 (sezione Sentiment ): raccolta delle serie di market mood (Fear & Greed + derivati Binance, API pubbliche senza chiave), composite con z-score e retention. Hot-reload via IOptionsMonitor (editabile da /admin/autonomy); gli INTERVALLI del worker si leggono al boot (PeriodicTimer) e richiedono riavvio.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Worker di raccolta. Default ON: sole GET pubbliche a cadenza modesta, e le serie Binance esistono solo per 30 giorni — i buchi sono irrecuperabili. |
| `p` | `int MetricsIntervalMinutes` | Cadenza del fetch delle metriche (minuti). Richiede riavvio. |
| `p` | `int NewsIntervalMinutes` | Cadenza del sync delle notizie RSS/calendario/retail (minuti). Richiede riavvio. |
| `p` | `List&lt;string&gt; Symbols` | Mercati Binance USDS-M osservati (formato exchange, es. BTCUSDT). |
| `p` | `int NewsRetentionDays` | Retention delle notizie (AltDataPoints), giorni. |
| `p` | `int MetricRetentionDays` | Retention delle serie metriche, giorni (la fonte FearGreed è ESENTE: è il baseline lungo, ~2500 righe totali). |
| `p` | `int BaselineDays` | Finestra del baseline per gli z-score, giorni. |
| `p` | `double ExtremeZScore` | \|z\| oltre cui una metrica è "estrema" (flag contrarian). |
| `p` | `int FearGreedExtremeLow` | Fear & Greed ≤ questa soglia = extreme fear (flag contrarian). |
| `p` | `int FearGreedExtremeHigh` | Fear & Greed ≥ questa soglia = extreme greed (flag contrarian). |
| `p` | `double WeightNews` | — |
| `p` | `double WeightFearGreed` | — |
| `p` | `double WeightFunding` | — |
| `p` | `double WeightLongShort` | — |
| `p` | `double WeightTaker` | — |
| `p` | `bool EnableMlFeature` | Opt-in: rende il fattore "Sentiment" disponibile come feature ML (AlphaFactorFactory). Default OFF: il sentiment entra nei modelli solo per scelta esplicita dell'operatore. |
| `p` | `string ScorerProvider` | Scorer delle notizie: "Keyword" (default, lessicale, zero costi), "Llm" (provider AI attivo del layer multi-provider — sceglierlo è il consenso esplicito al costo per chiamata) o "Onnx" (inferenza locale del pilota). Ho… |
| `p` | `string OnnxModelPath` | Percorso del modello ONNX del pilota sentiment (relativo al content root se non assoluto). Il file NON sta nel repository (è un artefatto addestrato, cartella gitignored): si genera dal pannello in /sentiment. |

## `ProcioneMGR/Services/Sentiment/SentimentScorerComparisonService.cs`

### 🧾 `ScorerComparisonRequest` `(`

> Richiesta di confronto scorer (dal pannello in /sentiment).

### 🧾 `ScorerComparisonEntry` `(`

> Una riga del confronto: lo scorer, se era davvero disponibile, e le sue metriche IC.

### 🧾 `ScorerDisagreement` `(`

> Una notizia su cui gli scorer sono in disaccordo (per capire COME differiscono, non solo di quanto).

### 🧾 `ScorerComparisonResult` `(`

> Esito complessivo del confronto.

### 📦 `SentimentScorerComparisonService` `(`

> Confronto A/B/C fra gli scorer di sentiment (Keyword / Llm / Onnx) sul giudice che la piattaforma usa per OGNI fattore: si rigiocano le notizie storiche (AltDataPoints) attraverso ciascuno scorer, si costruisce un per scorer e si misura l'IC con lo STESSO (Spearman, t-stat Newey-West, IR, quantili) sulle STESSE candele — nessuna infrastruttura di gate nuova, e i verdetti sono confrontabili per costruzione. Offline puro: non tocca i punteggi salvati né il percorso di sync. Il replay LLM usa (N titoli per chiamata) col tetto MaxItems : il costo del confronto è dichiarato e limitato PRIMA di partire, non scoperto dalla bolletta.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;ScorerComparisonResult&gt; CompareAsync(ScorerComparisonRequest request, CancellationToken ct)` | — |

## `ProcioneMGR/Services/Sentiment/SentimentSnapshot.cs`

### 📦 `SentimentSnapshot`

> Fotografia del "market mood" (Sentiment 2.0): composite per-mercato e per-simbolo con z-score vs baseline rolling e flag contrarian agli estremi. POCO con setter pubblici DI PROPOSITO: viaggia dentro AltDataOutput nel checkpoint serializzato del PipelineContext. Semantica: è il MOOD DELLA FOLLA in [-1,+1] (positivo = folla bullish); la lettura CONTRARIAN vive nei flag — un mood estremo è un rischio di squeeze/svolta, non un invito a seguirlo.

| | Firma | Descrizione |
|---|---|---|
| `p` | `DateTime ComputedAtUtc` | — |
| `p` | `double? NewsScore24h` | Media 24h del punteggio news di TUTTE le fonti testuali (null se nessuna notizia scorata). |
| `p` | `double? FearGreedValue` | — |
| `p` | `string? FearGreedLabel` | — |
| `p` | `double? FearGreedDelta7d` | Variazione del Fear & Greed rispetto a ~7 giorni fa (null senza storico). |
| `p` | `double CompositeScore` | Mood della folla a livello mercato, [-1,+1]. |
| `p` | `List&lt;string&gt; Extremes` | Flag contrarian (testo leggibile) a livello mercato + per simbolo. |
| `p` | `List&lt;SymbolSentiment&gt; Symbols` | — |

### 📦 `SymbolSentiment`

> Mood per singolo simbolo (ticker base, es. "BTC").

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Symbol` | — |
| `p` | `double? NewsScore24h` | — |
| `p` | `double? FundingPercent` | Ultimo funding in percento (convenzione piattaforma) e relativo z-score. |
| `p` | `double? FundingZ` | — |
| `p` | `double? GlobalLongShortRatio` | — |
| `p` | `double? GlobalLongShortZ` | — |
| `p` | `double? TopTraderLongShortZ` | — |
| `p` | `double? TakerZ` | — |
| `p` | `double? OiChange24hPercent` | Variazione % dell'open interest (valore USDT) nelle ultime ~24h — contesto di ampiezza, MAI nel composite. |
| `p` | `double Composite` | Mood della folla sul simbolo, [-1,+1]. |
| `p` | `List&lt;string&gt; Extremes` | — |

## `ProcioneMGR/Services/Sentiment/SentimentSnapshotService.cs`

### 📦 `SentimentSnapshotCache`

> Ultimo snapshot calcolato, per UI/prompt/pipeline senza ricomputo. Singleton.

| | Firma | Descrizione |
|---|---|---|
| `p` | `SentimentSnapshot? Current` | — |
| `m` | `void Set(SentimentSnapshot snapshot)` | — |

### 🔌 `ISentimentSnapshotService`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;SentimentSnapshot?&gt; ComputeAsync(CancellationToken ct)` | Carica metriche (finestra baseline) e news (24h) dal DB, calcola lo snapshot col calcolatore puro e aggiorna la cache. Null se non c'è alcun dato (mai un finto "neutro"). |

### 📦 `SentimentSnapshotService` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;SentimentSnapshot?&gt; ComputeAsync(CancellationToken ct)` | — |

## `ProcioneMGR/Services/Sentiment/SentimentSourceHealthRegistry.cs`

### 🧾 `SourceHealth` `(`

> Fotografia della salute di una fonte dati (news o metriche) per la UI.

### 📦 `SentimentSourceHealthRegistry`

> Registro in-memory della salute delle fonti di sentiment (RSS, calendario, retail, Fear & Greed, derivati Binance): ultima sync riuscita, quanti elementi, ultimo errore. Una fonte che fallisce viene già SALTATA senza far fallire il batch — questo registro rende il fallimento VISIBILE (/sentiment) invece che sepolto nei log. Process-local di proposito: dopo un riavvio è vuoto fino al primo tick, e va benissimo così (niente tabella per uno stato diagnostico).

| | Firma | Descrizione |
|---|---|---|
| `m` | `void ReportSuccess(string name, int count)` | — |
| `m` | `void ReportError(string name, string message)` | — |
| `m` | `IReadOnlyList&lt;SourceHealth&gt; Snapshot()` | — |

## `ProcioneMGR/Services/Sentiment/SentimentSyncWorker.cs`

### 📦 `SentimentSyncWorker` `(`

> Worker di Sentiment 2.0: raccoglie le serie di market mood (ogni tick), sincronizza le notizie (a cadenza più lenta — supera il vecchio "solo on-demand" di /sentiment), ricalcola lo snapshot composite e applica la retention. Default ON (a differenza delle automazioni decisionali): sole GET pubbliche keyless a cadenza modesta, e le serie derivate di Binance esistono SOLO per 30 giorni — un worker spento significa buchi irrecuperabili nei baseline degli z-score. Enabled è per-tick (hot da /admin/autonomy); gli intervalli sono letti al boot. I fallimenti delle fonti restano log + salute in UI: niente Telegram (non azionabili).

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |
| `m` | `Task&lt;(int Metrics, int News)&gt; TickAsync(CancellationToken ct, bool forceNews = false)` | Un tick completo: metriche sempre, news solo se è passato l'intervallo dedicato, snapshot, retention. Pubblico per i test e per "Esegui ora" dalla UI (che forza anche le news). |

# `Services/AltData/`

## `ProcioneMGR/Services/AltData/AltDataSyncService.cs`

### 🔌 `IAltDataSyncService`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;int&gt; SyncAllAsync(CancellationToken ct)` | Interroga tutte le fonti registrate, classifica/scora le notizie nuove e le salva. Restituisce quante ne ha inserite. |

### 📦 `AltDataSyncService` `(`

> Implementazione di . Deduplica per Source+Url (o Source+Title se una fonte non fornisce un link), tollera fonti temporaneamente irraggiungibili (le salta con un warning, non fa fallire l'intera sync — stesso spirito resiliente di MarketDataSyncService per l'OHLCV).

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;int&gt; SyncAllAsync(CancellationToken ct)` | — |

## `ProcioneMGR/Services/AltData/ForexFactoryIngestor.cs`

### 📦 `ForexFactoryIngestor` `(IHttpClientFactory httpClientFactory) : IAltDataSource`

> Ingestor del calendario economico di ForexFactory (Fase D.2). ForexFactory non ha un feed RSS pubblico ( /rss risponde 403) — verificato dal vivo che /calendar è invece HTML server-renderizzato con uno User-Agent da browser realistico (senza, il sito risponde comunque con la pagina ma non è mai stato verificato un blocco Cloudflare attivo in questo scraping: la pagina contiene le righe evento reali, non una challenge page). LIMITAZIONE DOCUMENTATA: i valori "Actual/Forecast/Previous" NON sono presenti nell'HTML statico — verificato dal vivo, tutte le celle calendar__actual risultano vuote nella risposta server: ForexFactory li popola via JavaScript/AJAX dopo il caricamento pagina. Riprodurli richiederebbe un browser headless (fuori scope, dipendenza pesante e fragile) o l'endpoint AJAX interno del sito (non documentato, più a rischio di rottura silenziosa di uno scraping HTML già di per…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `Task&lt;IReadOnlyList&lt;RawNewsItem&gt;&gt; FetchLatestAsync(CancellationToken ct)` | — |
| `m` | `IReadOnlyList&lt;RawNewsItem&gt; ParseCalendar(string html)` | Parsing puro da HTML già scaricato — separato da per essere testabile senza rete (stesso pattern di RssNewsSource.ParseFeed ). |

## `ProcioneMGR/Services/AltData/IAltDataSource.cs`

### 🧾 `RawNewsItem` `(`

> Una notizia/evento grezzo, prima di classificazione/sentiment (li applica AltDataSyncService ). Le fonti testuali (RSS) lasciano CategoryOverride / SentimentScoreOverride / SymbolsOverride a null e si affidano alla classificazione automatica ( / ISentimentScorer ). Le fonti strutturali (es. ForexFactoryIngestor per il calendario economico, RetailSentimentIngestor per i dati numerici di posizionamento retail) valorizzano gli override perché il dato non è testo libero da classificare: la categoria è nota per costruzione e il punteggio di sentiment (se applicabile) è calcolato direttamente dal dato numerico, non da un lessico.

### 🔌 `IAltDataSource`

> Fonte di dati alternativi (cap. 3): stesso spirito di IExchangeClient — un'astrazione per fonte, così aggiungerne una nuova (social, on-chain) è "nuova classe + un case", non un cambiamento strutturale.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | Nome tecnico della fonte, es. "CoinDesk". |
| `m` | `Task&lt;IReadOnlyList&lt;RawNewsItem&gt;&gt; FetchLatestAsync(CancellationToken ct)` | — |

## `ProcioneMGR/Services/AltData/NewsImpactAnalyzer.cs`

### 🧾 `ImpactStats` `(int Observations, double AvgReturn1h, double AvgReturn4h, double AvgReturn24h);`

> Ritorno medio del simbolo di riferimento sui tre orizzonti, per un gruppo di notizie/eventi.

### 🧾 `CategoryImpact` `(string Category, ImpactStats Stats);`

### 🧾 `SourceImpact` `(string Source, ImpactStats Stats);`

### 🧾 `RetailSentimentAgreement` `(`

> Confronto incrociato fra due fonti indipendenti di sentiment retail sullo STESSO simbolo nella STESSA ora: quando concordano (entrambe fortemente long o fortemente short), il ritorno medio del simbolo di riferimento differisce da quando divergono?

### 🧾 `NewsImpactReport` `(`

### 🔌 `INewsImpactAnalyzer`

| | Firma | Descrizione |
|---|---|---|
| `m` | `NewsImpactReport Analyze(string referenceSymbol, IReadOnlyList&lt;AltDataPoint&gt; news, IReadOnlyList&lt;OhlcvData&gt; referenceCandles)` | Misura il movimento di prezzo del simbolo di riferimento nelle finestre [t,t+1h], [t,t+4h], [t,t+24h] a partire dal timestamp di ciascuna notizia/evento, e aggrega per categoria e per fonte. deve essere ordinato cronolo… |

### 📦 `NewsImpactAnalyzer` `: INewsImpactAnalyzer`

> DECISIONE ARCHITETTURALE (Fase D.2): la piattaforma ingerisce OHLCV solo per crypto (Binance/Bitget) — non esiste uno storico prezzi per le coppie forex (EURUSD ecc.) di cui parlano le fonti macro/calendario/sentiment retail. Misurare l'impatto "sul proprio strumento" richiederebbe OHLCV forex, fuori scope. Si misura quindi l'impatto di OGNI notizia/evento (qualunque sia lo strumento nominale di cui parla) sul movimento di un SIMBOLO CRYPTO DI RIFERIMENTO scelto dall'utente (es. BTC/USDT) — una domanda empirica legittima e ben nota in letteratura ("risk-on/risk-off": le decisioni Fed/ECB e il sentiment macro muovono anche gli asset di rischio come le crypto, non solo il proprio strumento diretto). Se in futuro la piattaforma ingerisse anche OHLCV forex, lo stesso analyzer funzionerebbe passando quella serie come referenceCandles — nessun cambiamento di codice necessario.

| | Firma | Descrizione |
|---|---|---|
| `m` | `NewsImpactReport Analyze(string referenceSymbol, IReadOnlyList&lt;AltDataPoint&gt; news, IReadOnlyList&lt;OhlcvData&gt; referenceCandles)` | — |

## `ProcioneMGR/Services/AltData/NewsImpactClassifier.cs`

### 🔢 `NewsCategory`

> Categoria di impatto di una notizia/evento. Regulatory/Security/Institutional/CentralBanks/ Macro sono derivate per keyword dal testo (vedi ). EconomicCalendar e RetailSentiment sono invece categorie STRUTTURALI: non derivano da classificazione testuale ma sono assegnate direttamente dal rispettivo ingestor ( ForexFactoryIngestor , RetailSentimentIngestor ) tramite RawNewsItem.CategoryOverride , perché la natura del dato è diversa da una notizia testuale (un evento datato con impatto atteso; un numero di posizionamento retail).

### 📦 `NewsImpactClassifier`

> Classificazione per parola chiave (word-boundary, non semplice substring — "ban" non deve far scattare un falso positivo su "banana", "sol" su "absolute"): filtro leggero PRIMA di un'eventuale chiamata LLM di sentiment, per concentrare il costo/rumore sulle notizie che la letteratura conferma muovere davvero il mercato.

| | Firma | Descrizione |
|---|---|---|
| `m` | `NewsCategory Classify(string title, string? summary)` | Punteggio per numero di keyword trovate per categoria (non "prima categoria che matcha"): una singola parola ambigua non deve sovrastare un segnale più specifico con più riscontri (es. "BlackRock ... ETF inflows" è due … |
| `m` | `IReadOnlyList&lt;string&gt; DetectSymbols(string title, string? summary)` | — |

## `ProcioneMGR/Services/AltData/RetailSentimentIngestor.cs`

### 📦 `RetailSentimentIngestor` `(string sourceName, string brokerKey, IHttpClientFactory httpClientFactory) : IAltDataSou…`

> Ingestor del posizionamento retail (long % vs short %) per coppia — un CONTRARIAN indicator: il retail è storicamente sul lato sbagliato ai punti di svolta. A differenza delle notizie testuali, qui non c'è testo da classificare/scorare col lessico: e il punteggio di sentiment sono valorizzati direttamente da un dato numerico ( RawNewsItem.CategoryOverride / SentimentScoreOverride ). DEVIAZIONE FLAGGATA rispetto al piano originale (due siti separati): - forexclientsentiment.com è dietro una challenge Cloudflare attiva (verificato dal vivo: risposta 403, pagina "Just a moment" di ~5KB) — non scrapabile senza un browser headless, fuori scope. - fxssi.com/tools/current-ratio stesso: la pagina HTML statica NON contiene i valori (widget client-side, nessun dato embeddato server-side) — verificato dal vivo scaricando la pagina reale (1MB di HTML/CSS/JS, zero percentuali di long/short nel marku…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `Task&lt;IReadOnlyList&lt;RawNewsItem&gt;&gt; FetchLatestAsync(CancellationToken ct)` | — |
| `m` | `IReadOnlyList&lt;RawNewsItem&gt; ParseRatios(IReadOnlyDictionary&lt;string, string&gt; ratios)` | Parsing puro dal dizionario simbolo→percentuale già deserializzato — testabile senza rete. |

### 🧾 `FxssiApiResponse` `(`

## `ProcioneMGR/Services/AltData/RssNewsSource.cs`

### 📦 `NewsFeeds`

> Fonti RSS note: gratuite, senza chiave API, senza rate limit pratico — a differenza di CryptoPanic (piano gratuito chiuso ad aprile 2026) o di provider a pagamento come CryptoCompare. Editorialmente affidabili: CoinDesk/Cointelegraph/The Block/Decrypt sono le fonti più citate negli studi di event-study sull'impatto di notizie regolatorie/ETF sui mercati crypto. FXStreet (Fase D.2, forex/macro) è anch'essa un normale feed RSS 2.0 — verificato dal vivo (200, text/xml , item validi). DELIBERATO: niente "FxStreetRssIngestor" dedicato, solo due voci in più qui — già gestisce qualunque feed RSS/Atom, e la distinzione Macro/CentralBanks è fatta dal classificatore per KEYWORD sul contenuto (stesso principio delle notizie crypto), non da una classe per fonte. Una classe wrapper che non aggiunge comportamento sarebbe duplicazione, non riuso. "FXStreet-CentralBanks" è il feed di categoria dedicato…

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyDictionary&lt;string, string&gt; KnownFeeds` | — |

### 📦 `RssNewsSource` `(string name, string feedUrl, IHttpClientFactory httpClientFactory) : IAltDataSource`

> Implementazione di per un singolo feed RSS/Atom.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `Task&lt;IReadOnlyList&lt;RawNewsItem&gt;&gt; FetchLatestAsync(CancellationToken ct)` | — |
| `m` | `IReadOnlyList&lt;RawNewsItem&gt; ParseFeed(SyndicationFeed feed)` | Estrazione pura dal feed già parsato — separata da per essere testabile senza rete. |

# `Services/Monitoring/`

## `ProcioneMGR/Services/Monitoring/Drift/DriftMath.cs`

### 📦 `DriftMath`

> Utilità numeriche condivise dai detector di drift. Tutto in double (test statistici).

| | Firma | Descrizione |
|---|---|---|
| `m` | `double[] ToDoubles(IReadOnlyList&lt;decimal&gt; values)` | — |
| `m` | `(double Mean, double Std) MeanStd(double[] values)` | — |
| `m` | `double QuantileSorted(double[] sortedAsc, double q)` | Quantile (interpolazione lineare stile NumPy) su un array GIÀ ordinato crescente. |
| `m` | `double KolmogorovQ(double lambda)` | Q di Kolmogorov: coda della distribuzione KS. Q(λ)=2·Σ(-1)^(k-1)·e^(-2k²λ²). Restituisce il p- |

## `ProcioneMGR/Services/Monitoring/Drift/DriftModels.cs`

### 🔢 `DriftSeverity`

> Gravità del drift rilevato su una feature. None &lt; Warning &lt; Alert.

### 🧾 `DriftResult` `(`

> Esito di UN test di drift su una feature (una distribuzione di riferimento vs una corrente). è la statistica del test (PSI, D di KS, statistica di Page-Hinkley); è valorizzato solo dove il test ne produce uno (KS).

### 📦 `DriftThresholds`

> Soglie dei test di drift. Default coerenti con la prassi (PSI &gt;0.2 warning, &gt;0.25 alert; KS p&lt;0.05 warning, p&lt;0.01 alert). Page-Hinkley lavora su z-score (standardizzati sulla distribuzione di riferimento) così le sue soglie sono indipendenti dalla scala della feature.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int PsiBins` | — |
| `p` | `double PsiWarning` | — |
| `p` | `double PsiAlert` | — |
| `p` | `double KsPValueWarning` | — |
| `p` | `double KsPValueAlert` | — |
| `p` | `double PageHinkleyDelta` | Tolleranza (in deviazioni standard di riferimento) prima che Page-Hinkley accumuli: assorbe il rumore di uno stream stazionario così solo uno spostamento PERSISTENTE della media supera le soglie. Le soglie sono tarate p… |
| `p` | `double PageHinkleyWarning` | — |
| `p` | `double PageHinkleyAlert` | — |
| `p` | `int MinObservations` | Numero minimo di osservazioni valide (per lato) sotto cui il test non è affidabile. |

### 📦 `FactorDriftReport`

> Report di drift per UNA feature di un modello: distribuzione di training (reference) vs finestra recente (current), con l'esito di ciascun detector. è la gravità massima.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string FeatureName` | — |
| `p` | `int ReferenceCount` | — |
| `p` | `int CurrentCount` | — |
| `p` | `IReadOnlyList&lt;DriftResult&gt; Results` | — |
| `p` | `DriftSeverity Overall` | — |

### 📦 `DriftCheckResult`

> ENTITÀ EF (tabella DriftCheckResults ): esito PERSISTITO di un check di drift su un modello, una riga per modello per tick del — anche quando è tutto pulito, così l'assenza di righe si distingue da "il worker non sta girando". Prima di questa tabella gli esiti vivevano solo nei log: la UI (/admin/autonomy) non poteva mostrare né l'ultimo esito né lo storico. Prune automatico oltre i 90 giorni nel worker.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `DateTime CheckedAtUtc` | Quando è stato eseguito il check (UTC). |
| `p` | `int ModelId` | Id del SavedMlModel valutato. NON è FK: la riga sopravvive alla cancellazione del modello. |
| `p` | `string ModelName` | Nome del modello, denormalizzato per leggibilità storica. |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `int TotalFeatures` | Feature totali valutate; 0 = check saltato (es. candele recenti insufficienti). |
| `p` | `int DriftingFeatures` | Feature con drift (Warning o Alert). |
| `p` | `int AlertFeatures` | Feature in Alert (sottoinsieme di ). |
| `p` | `DriftSeverity Overall` | Gravità complessiva del check (max tra le feature). |
| `p` | `string? TopFeaturesJson` | Top-5 feature in drift, JSON [{"name","severity","detector","score"}] — abbastanza per la tabella in UI senza persistire l'intero report per-feature. |
| `p` | `bool ChampionRetired` | True se QUESTO check ha fatto ritirare un Champion (ciclo chiuso del registry). |

## `ProcioneMGR/Services/Monitoring/Drift/FeatureDriftMonitor.cs`

### 📦 `FeatureDriftMonitor` `: IFeatureDriftMonitor`

> Implementazione di . Ricostruisce i fattori del modello dal suo FactorsJson (stesso round-trip di SavedMlModel in /ml), calcola le serie sul periodo di training (reference, letto dal DB) e sulle candele recenti (current), e passa i due campioni a ogni detector. Il calcolo dei fattori rispetta l'invariante anti-look-ahead di (nessuna feature legge dati futuri).

## `ProcioneMGR/Services/Monitoring/Drift/FeatureDriftWorker.cs`

### 📦 `DriftMonitorOptions`

> Opzioni del (sezione config "Drift"). Default safe-off.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Master switch. Default false: il worker si spegne subito, il drift resta valutabile on-demand dalla UI. |
| `p` | `int IntervalHours` | Cadenza di valutazione automatica (ore). |
| `p` | `int RecentCandles` | Quante candele recenti usare come campione "corrente". |
| `p` | `bool RetireChampionOnAlert` | Ciclo chiuso (Fase 2): quando un modello Champion va in drift, ritiralo dal registry e accoda un retrain. Default true (il worker è comunque opt-in). Il retrain NON è automatico — si marca soltanto la richiesta per l'op… |
| `p` | `int MinAlertsToRetire` | Numero minimo di feature in Alert per far scattare il ritiro del Champion. |

### 📦 `FeatureDriftWorker` `(`

> Valuta periodicamente (opt-in) il drift delle feature di ogni e logga warning/alert. AFFIANCA il StrategyDecayMonitor : è un segnale anticipatore sugli input, non una decisione di trading — non apre/chiude nulla, scrive solo log (rif. ROADMAP-QLIB §1.5). Default spento ( =false), come le altre automazioni.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |
| `k` | `int ResultRetentionDays` | Righe più vecchie di così vengono eliminate a ogni tick (lo storico utile è "di recente"). |
| `m` | `Task TickAsync(CancellationToken ct)` | Un tick: valuta il drift di ogni modello salvato e logga gli scostamenti. Pubblico per test e per "Esegui ora" da /admin/autonomy. |
| `m` | `string? BuildTopFeaturesJson(IReadOnlyList&lt;FactorDriftReport&gt; drifting)` | Top-5 feature in drift come JSON compatto per la UI: [{"name","severity","detector","score"}]. |

## `ProcioneMGR/Services/Monitoring/Drift/IFeatureDriftDetector.cs`

### 🔌 `IFeatureDriftDetector`

> Rileva il drift statistico di una feature: quanto la distribuzione dei valori CORRENTI si è spostata rispetto a quella di RIFERIMENTO (tipicamente: finestra di training del modello). È un segnale anticipatore che AFFIANCA — non sostituisce — lo : quest'ultimo misura il PnL realizzato (il giudice finale), il drift misura se gli INPUT del modello sono cambiati prima ancora che il PnL ne risenta (rif. docs/archive/ROADMAP-QLIB.md §1.5 ). Puro/stateless → registrabile come Singleton.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | Nome tecnico del test: "Psi" \| "Ks" \| "PageHinkley". |
| `m` | `DriftResult Detect(IReadOnlyList&lt;decimal&gt; reference, IReadOnlyList&lt;decimal&gt; current, DriftThresholds thresholds)` | Confronta i valori di riferimento con quelli correnti. Restituisce sempre un (con e un dettaglio esplicativo quando i dati sono insufficienti), mai un'eccezione. |

## `ProcioneMGR/Services/Monitoring/Drift/IFeatureDriftMonitor.cs`

### 🔌 `IFeatureDriftMonitor`

> Valuta il drift di TUTTE le feature di un : per ciascun fattore usato dal modello confronta la distribuzione nella finestra di training (reference) con quella nelle candele recenti (current), applicando ogni . Rif. docs/archive/ROADMAP-QLIB.md §1.5 .

## `ProcioneMGR/Services/Monitoring/Drift/KsDriftDetector.cs`

### 📦 `KsDriftDetector` `: IFeatureDriftDetector`

> Test di Kolmogorov-Smirnov a due campioni : la statistica D è la massima distanza fra le due funzioni di ripartizione empiriche (reference vs current). Il p-

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `DriftResult Detect(IReadOnlyList&lt;decimal&gt; reference, IReadOnlyList&lt;decimal&gt; current, DriftThresholds thresholds)` | — |

## `ProcioneMGR/Services/Monitoring/Drift/PageHinkleyDetector.cs`

### 📦 `PageHinkleyDetector` `: IFeatureDriftDetector`

> Test di Page-Hinkley : change-point online su uno STREAM (non due campioni statici). A differenza di PSI/KS, che confrontano due distribuzioni globali, qui si scorre la serie corrente nell'ordine temporale e si accumula la deviazione persistente della media rispetto al riferimento — utile per cogliere uno spostamento GRADUALE del regime. I valori correnti sono standardizzati (z-score) sulla media/deviazione di riferimento, così la statistica e le soglie sono indipendenti dalla scala della feature. Si valutano entrambe le direzioni (aumento/diminuzione della media) e si tiene la più forte.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `DriftResult Detect(IReadOnlyList&lt;decimal&gt; reference, IReadOnlyList&lt;decimal&gt; current, DriftThresholds thresholds)` | — |

## `ProcioneMGR/Services/Monitoring/Drift/PsiDriftDetector.cs`

### 📦 `PsiDriftDetector` `: IFeatureDriftDetector`

> Population Stability Index : quanto la distribuzione corrente si è spostata fra i bin definiti dai quantili della distribuzione di riferimento. PSI = Σ (a−e)·ln(a/e), con e/a = frazione attesa (reference) / effettiva (current) per bin. Convenzione: &lt;0.1 stabile, 0.1–0.25 spostamento moderato, &gt;0.25 significativo.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `DriftResult Detect(IReadOnlyList&lt;decimal&gt; reference, IReadOnlyList&lt;decimal&gt; current, DriftThresholds thresholds)` | — |

## `ProcioneMGR/Services/Monitoring/StrategyDecayMonitor.cs`

### 🔌 `IStrategyDecayMonitor`

> Confronta la performance REALIZZATA (trade chiusi dal vivo, Paper/Testnet/Live) di una gamba dell'ensemble con quella ATTESA dal backtest/holdout che l'ha validata — "l'edge è morto?" come segnale misurabile invece che intuizione. Puro/deterministico: nessuna dipendenza da DB o orologio all'interno del calcolo (i trade e l'istante di analisi sono passati dal chiamante), per restare testabile in isolamento con dati sintetici.

| | Firma | Descrizione |
|---|---|---|
| `m` | `DecayReport Analyze(EnsembleStrategy strategy, IReadOnlyList&lt;TradeRecord&gt; allClosedTrades, string timeframe, DecayMonitorOptions? options = nul…` | Analizza una gamba dato l'intero storico dei suoi trade chiusi (di qualunque strategia dell'ensemble contenga anche altre gambe — il filtro per è fatto internamente, così il chiamante può passare l'intera tabella TradeR… |

### 📦 `DecayMonitorOptions`

> Soglie del monitor di decadimento. Stessa finestra funge da minimo di trade richiesti e da ampiezza del rolling.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int WindowTradeCount` | Quante delle ultime operazioni chiuse considerare (e minimo richiesto prima di poter valutare). |
| `p` | `decimal AlertThresholdRatio` | Sotto questa frazione di RealizedSharpe/ExpectedSharpe scatta l'alert (default 50%). |

### 📦 `DecayReport`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string StrategyId` | — |
| `p` | `string StrategyName` | — |
| `p` | `string DisplayName` | — |
| `p` | `decimal? ExpectedSharpe` | — |
| `p` | `decimal? RealizedSharpe` | [M5] Sharpe realizzato su base PER-CANDELA (bucket del timeframe, bucket senza trade = 0), annualizzato come lo Sharpe holdout: è il numero CONFRONTABILE con . |
| `p` | `decimal? RealizedTradeSharpe` | Sharpe realizzato "a trade" (annualizzato con sqrt(trade/anno) stimati dalla cadenza del campione) — il valore storico del monitor, conservato come INFORMATIVO: non è sulla stessa base dell'atteso e non partecipa più al… |
| `p` | `decimal? SharpeDelta` | RealizedSharpe - ExpectedSharpe. |
| `p` | `decimal? SharpeRatio` | RealizedSharpe / ExpectedSharpe (1 = in linea, &lt;0.5 = alert di default). Null se ExpectedSharpe non è positivo (il rapporto non è interpretabile). |
| `p` | `decimal? ExpectedProfitFactor` | — |
| `p` | `decimal? RealizedProfitFactor` | — |
| `p` | `int TradeCount` | — |
| `p` | `bool IsAlert` | — |
| `p` | `string StatusMessage` | Messaggio sempre valorizzato: spiega l'esito anche quando non scatta un alert (es. "trade insufficienti"). |
| `p` | `DateTime AnalyzedAtUtc` | — |

### 📦 `StrategyDecayMonitor` `: IStrategyDecayMonitor`

| | Firma | Descrizione |
|---|---|---|
| `m` | `DecayReport Analyze(EnsembleStrategy strategy, IReadOnlyList&lt;TradeRecord&gt; allClosedTrades, string timeframe, DecayMonitorOptions? options = nul…` | — |

# `Services/Ingestion/`

## `ProcioneMGR/Services/Ingestion/BarBuilder.cs`

### 🧾 `AggregatedBar` `(`

> Barra aggregata a soglia (volume o controvalore) costruita da candele temporali di base.

### 📦 `BarBuilder`

> Costruzione di barre non temporali (Jansen ML4T, cap. 2): le barre a tempo fisso campionano il mercato in modo disomogeneo (poche informazioni di notte, troppe nei momenti concitati). Aggregare per VOLUME costante ("volume bars") o CONTROVALORE costante ("dollar bars") produce serie con proprieta' statistiche piu' vicine alla normalita' (meno eteroschedasticita'), migliori come input per i modelli ML. Qui l'aggregazione parte dalle candele temporali di base gia' in piattaforma (non dai tick, che non ingestiamo): la granularita' minima della soglia e' quindi quella della candela sorgente — usare la serie base piu' fine disponibile (es. 1m/5m).

| | Firma | Descrizione |
|---|---|---|
| `m` | `IReadOnlyList&lt;AggregatedBar&gt; BuildVolumeBars(IReadOnlyList&lt;OhlcvData&gt; candles, decimal volumePerBar)` | Barre a volume costante: chiude la barra quando il volume cumulato raggiunge la soglia. |
| `m` | `IReadOnlyList&lt;AggregatedBar&gt; BuildDollarBars(IReadOnlyList&lt;OhlcvData&gt; candles, decimal dollarPerBar)` | Barre a controvalore costante: soglia sul cumulato di (prezzo tipico x volume). |
| `m` | `decimal SuggestVolumeThreshold(IReadOnlyList&lt;OhlcvData&gt; candles, int targetBarCount)` | Soglia di volume che produce circa barre sull'intera serie (l'equivalente del "trades_per_min" del libro: volume totale / barre desiderate). |
| `m` | `decimal SuggestDollarThreshold(IReadOnlyList&lt;OhlcvData&gt; candles, int targetBarCount)` | Soglia di controvalore che produce circa barre. |

## `ProcioneMGR/Services/Ingestion/IMarketDataSyncService.cs`

### 🔌 `IMarketDataSyncService`

> Sincronizza le serie della watchlist: calcola il delta (dall'ultima candela salvata fino a "ora") e lo ingerisce riusando . Usato sia dal worker schedulato sia dal pulsante "Sync now" della UI.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;int&gt; SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default)` | Sincronizza una singola serie tracciata. Ritorna le candele processate. |
| `m` | `Task SyncAllEnabledAsync(CancellationToken ct = default)` | Sincronizza tutte le serie abilitate (resiliente: un errore non blocca le altre). |

## `ProcioneMGR/Services/Ingestion/IOhlcvIngestionService.cs`

### ▫️ `IngestionProgress` `(long Ingested, long Estimated, string Symbol, string Timeframe)`

> Avanzamento dell'ingestione, riportato alla UI/log.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Percent` | — |

### ▫️ `IngestionResult` `(long CandlesProcessed, bool Cancelled);`

> Esito sintetico di un'operazione di ingestione.

### 🔌 `IOhlcvIngestionService`

> Scarica e persiste dati OHLCV storici da un exchange, gestendo paginazione, rate-limit e upsert idempotente sulla tabella OHLCV.

## `ProcioneMGR/Services/Ingestion/IngestionServiceCollectionExtensions.cs`

### 📦 `IngestionServiceCollectionExtensions`

> Infrastruttura di ingestione OHLCV condivisa: client exchange (sorgente dei dati) + . Estratta da Program.cs per essere riusata verbatim dal servizio standalone ProcioneMGR.Ingestion (Fase 1 microservizi). NON registra nè il worker schedulato ( ): quella è la parte che il feature toggle MarketData:UseRemoteIngestion commuta tra implementazione locale e remota, quindi resta responsabilità dell'host. I client exchange e invece servono sempre (trading, pipeline, dashboard li usano a prescindere dal toggle).

| | Firma | Descrizione |
|---|---|---|
| `m` | `IServiceCollection AddExchangeClients(this IServiceCollection services)` | Solo i client exchange (Binance/Bitget + ), senza . Estratto da per ProcioneMGR.Trading (Fase 2b), che deve firmare le chiamate Testnet/Live ma non ingerisce candele: trascinare l'ingestione nel suo host sarebbe una dip… |
| `m` | `IServiceCollection AddOhlcvIngestion(this IServiceCollection services)` | — |

## `ProcioneMGR/Services/Ingestion/MarketDataSyncService.cs`

### 📦 `MarketDataSyncService` `(`

> Implementazione della sincronizzazione incrementale delle serie tracciate.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;int&gt; SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default)` | — |
| `m` | `Task SyncAllEnabledAsync(CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Ingestion/MarketDataSyncWorker.cs`

### 📦 `MarketDataSyncWorker` `(`

> Worker schedulato: a intervalli regolari sincronizza tutte le serie abilitate della watchlist. Gira nel processo dell'app come . Configurazione (sezione "MarketData" in appsettings.json): - Enabled : true/false per accendere/spegnere il worker (default true) - SyncIntervalMinutes : intervallo tra i cicli (default 5) - DefaultBackfillDays : finestra di backfill alla prima sync di una serie (default 7) Usa perche' i servizi di dominio sono scoped mentre il worker e' singleton.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

## `ProcioneMGR/Services/Ingestion/OhlcvIngestionService.cs`

### 📦 `OhlcvIngestionService` `(`

> Implementazione dell'ingestione storica OHLCV. Strategia: - itera richiedendo all'exchange blocchi di candele (max consentito dal client), avanzando il cursore since finche' non si copre l'intervallo [from, to]; - rispetta i rate-limit con un tra una richiesta e l'altra; - persiste con UPSERT idempotente (vedi ): nessuna candela duplicata grazie all'indice univoco (Symbol, Timeframe, TimestampUtc). Usa perche' il loop e' a lunga durata: si crea un DbContext fresco e a vita breve per ogni batch, evitando di tenere aperto un context per tutta l'operazione.

## `ProcioneMGR/Services/Ingestion/RemoteMarketDataSyncService.cs`

### 📦 `RemoteMarketDataSyncService` `(`

> Implementazione di che delega la sincronizzazione al microservizio remoto ProcioneMGR.Ingestion via HTTP (Fase 1 microservizi). Attiva nel monolite solo con MarketData:UseRemoteIngestion=true . Trasparente per i consumer (es. il pulsante "Sync now" in Watchlist.razor), che iniettano sempre l'interfaccia.

### 🧾 `SyncResponse` `(int CandlesProcessed);`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;int&gt; SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default)` | — |
| `m` | `Task SyncAllEnabledAsync(CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Ingestion/SeriesFreshness.cs`

### 📦 `SeriesFreshness`

> [B2] Regola UNICA per dire se una serie è aggiornata o FERMA. Nasce da una cecità del gate B2, trovata il 2026-07-28 guardando il database invece dei documenti. Il gate chiedeva «7 giorni senza buchi nelle candele», ma nessuno dei due strumenti che dovevano misurarlo sapeva vedere una serie che ha smesso di avanzare: - lo stato di sync scriveva OK: N candele guardando quante righe erano state processate. Su una serie ferma il cursore incrementale ri-chiede l'ultima candela nota, l'exchange la restituisce, e l'upsert la riscrive: OK: 1 candele a ogni giro, per sempre. MKR/USDT lo diceva da dieci mesi; - l'audit di copertura misurava le candele presenti sull'intervallo [prima, ultima] della serie STESSA. Una serie che si ferma ha copertura 100% del proprio passato: per costruzione non poteva accorgersene. Qui la freschezza si misura contro ADESSO, che è l'unico riferimento che non si spos…

| | Firma | Descrizione |
|---|---|---|
| `k` | `int DefaultToleranceBars` | Quante barre di ritardo si tollerano prima di chiamare ferma una serie. Tre, non zero: l'exchange pubblica con un ritardo suo, il ciclo di sync gira ogni 5 minuti e la barra in formazione non è un buco. Sotto questa sog… |
| `m` | `DateTime? LastClosedBarOpenUtc(string timeframe, DateTime nowUtc)` | [2026-08-06] L'istante di APERTURA dell'ultima barra che ha già CHIUSO. null se il timeframe non è riconosciuto. Sta qui, accanto a , perché è la stessa nozione vista dall'altro lato: là serve a misurare il ritardo, qui… |
| `m` | `int? BarsBehind(string timeframe, DateTime? lastCandleUtc, DateTime nowUtc)` | Quante barre CHIUSE mancano all'appello. null se il timeframe non è riconosciuto o la serie è vuota: due casi che NON sono "aggiornata" e non devono poter essere scambiati per tale da un confronto numerico. Il riferimen… |

## `ProcioneMGR/Services/Ingestion/SeriesFreshnessWatchWorker.cs`

### 📦 `SeriesFreshnessWatchWorker` `(`

> [E7] Guardia di freschezza delle serie: applica la regola UNICA di a tutte le serie abilitate della watchlist e NOTIFICA la TRANSIZIONE a ferma — una volta per serie, non una per giro. Perché esiste: B2.a ha costruito la regola, ma il suo esito viveva in un LogWarning del pod di ingestion e nel tool CLI coverage — MKR/USDT è stata ferma DIECI MESI con `/watchlist` che diceva «Abilitata». La lezione di D2.a: di un guasto ci si deve accorgere senza doverci pensare , non aprendo i log giusti al momento giusto. Vive nel GUSCIO e legge solo il database, quindi funziona identico con l'ingestion locale o remota — è di proposito indipendente da dove giri il sync, perché è il sync l'imputato che deve sorvegliare. Nessuna azione automatica: disabilitare una serie resta una scelta umana (un BREAK può essere temporaneo, decisione B2.a).

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |
| `m` | `Task&lt;IReadOnlyList&lt;string&gt;&gt; TickAsync(CancellationToken ct)` | Un giro di controllo. Pubblico per i test. Restituisce le serie appena DIVENTATE ferme in questo giro (quelle per cui è partita la notifica): il chiamante di test può così distinguere «ferma e già nota» da «ferma e appe… |

# `Services/Notifications/`

## `ProcioneMGR/Services/Notifications/DailyDigest.cs`

### 📦 `DigestOptions`

> [AF5.4] Il digest giornaliero, sezione Notifications:Digest . Default SPENTO. L'ora è quella LOCALE della macchina (il PC del proprietario): il digest serve a un umano che si sveglia, non a un cron UTC.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | — |
| `p` | `int Hour` | — |
| `p` | `int Minute` | — |
| `p` | `bool NarrativeEnabled` | [G9] Un paragrafo di sintesi in italiano, scritto dal provider AI attivo, SOPRA i dati strutturati. Default off. Additivo per costruzione: se l'AI è spenta, senza chiave, in breaker o fuori budget, il digest esce identi… |

### 📦 `DigestSchedule`

> Decisione pura di scheduling: il digest parte quando l'orario del giorno è passato E oggi non è ancora stato mandato. Separata dal worker per essere testabile con orologi finti.

| | Firma | Descrizione |
|---|---|---|
| `m` | `bool IsDue(DateTime nowLocal, int hour, int minute, DateOnly? lastSentDate)` | — |

### 🧾 `DigestData` `(`

> Il materiale del digest, già raccolto: il compositore è puro e non tocca servizi.

### 📦 `DailyDigestComposer`

> Compone il testo. La chiusura è la parte più importante: dichiara che l'ASSENZA del digest è essa stessa l'allarme — il dead-man's-switch percepibile da un umano senza infrastruttura.

| | Firma | Descrizione |
|---|---|---|
| `m` | `string Compose(DigestData data, DateTime nowLocal, string? narrative = null)` | [G9]: paragrafo di sintesi opzionale, inserito SOPRA i dati e mai al loro posto — il lettore ha sempre la fonte accanto alla sintesi. null o vuoto ⇒ messaggio identico a prima, carattere per carattere. |

### 📦 `DailyDigestWorker` `(`

> [AF5.4] Il worker: ogni minuto controlla se il digest è dovuto; quando lo è, raccoglie ogni sezione IN PROPRIO try/catch (meglio un digest con meno sezioni che nessun digest) e lo manda dal canale normale. Vive nel SOLO monolite. L'anti-doppione è in memoria: dopo un riavvio a cavallo dell'ora configurata un secondo invio è possibile e accettato (il rate-limit del dispatcher lo assorbe; un doppione all'anno batte una tabella in più).

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

## `ProcioneMGR/Services/Notifications/INotifier.cs`

### 🔢 `NotificationSeverity`

> Gravità di una notifica (mappa su livello di log e icona del messaggio).

### 🔌 `INotifier`

> Canale di notifica verso l'operatore (Fase 4, PRD Autonomia Operativa §7): il contrario dell'autonomia cieca — un modo affidabile di CHIAMARE l'umano quando serve. Un solo metodo, nessun bus: progetto solo-operatore. L'implementazione registrata è (gate + rate-limit + scelta provider); i producer NON devono mai fallire per colpa di una notifica — il dispatcher non propaga eccezioni.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)` | — |

### 🔌 `INotificationProvider`

> Provider concreto di recapito (Logging, Telegram, …), selezionato da .

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | Nome con cui il provider si seleziona in config (case-insensitive). |
| `m` | `Task SendAsync(NotificationSeverity severity, string title, string body, CancellationToken ct)` | Recapita il messaggio. Può lanciare: è il dispatcher a contenere l'errore. |

### 📦 `NotificationOptions`

> Opzioni del canale di notifica, sezione Notifications . Default OFF.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Default false: nessuna notifica finché l'operatore non abilita esplicitamente. |
| `p` | `string Provider` | "Logging" (default) \| "Telegram". |
| `p` | `string ChatId` | Chat id Telegram di destinazione (il token del bot NON va in config: env TELEGRAM_BOT_TOKEN). |
| `p` | `int MaxPerHour` | Rate-limit: massimo di messaggi recapitati per ora (finestra scorrevole); l'eccesso viene coalizzato. |

## `ProcioneMGR/Services/Notifications/LoggingNotifier.cs`

### 📦 `LoggingNotifier` `(ILogger&lt;LoggingNotifier&gt; logger) : INotificationProvider`

> Provider di default: le notifiche finiscono nel log strutturato (nessuna dipendenza esterna). Utile anche come "prova generale" del canale prima di configurare Telegram.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `Task SendAsync(NotificationSeverity severity, string title, string body, CancellationToken ct)` | — |

## `ProcioneMGR/Services/Notifications/NotificationDispatcher.cs`

### 🔢 `NotificationOutcome`

> Esito di un tentativo di recapito. Serve alla diagnostica, non ai producer.

### 🧾 `NotificationResult` `(NotificationOutcome Outcome, string? Detail = null)`

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsDelivered` | — |

### 🧾 `NotificationChannelStatus` `(`

> [E5] La spia di guasto del canale: ultimo recapito riuscito, ultimo fallimento col motivo, e quanti fallimenti si sono accumulati dall'ultimo recapito. Esiste perché NotifyAsync assorbe l'esito per contratto (giusto per i producer) e quindi un canale rotto falliva SOLO nei log — è ciò che ha tenuto Telegram muto per due giorni senza che nessuno potesse accorgersene. Un canale rotto non può auto-denunciarsi via notifica per definizione: serve una superficie che si legge, non un messaggio che non partirà. Ultimo recapito accettato dal provider (null = mai da questo avvio). Ultimo fallimento di recapito o provider sconosciuto (null = mai da questo avvio). Motivo leggibile dell'ultimo fallimento. Fallimenti consecutivi dall'ultimo recapito riuscito: &gt; 0 = il canale sta perdendo messaggi ADESSO.

### 📦 `NotificationDispatcher` `(`

> L' registrato in DI: gate ( Notifications:Enabled , default OFF, hot-reload), rate-limit a finestra scorrevole con coalescing (i messaggi soppressi vengono conteggiati e riportati nel primo messaggio successivo, mai persi in silenzio) e selezione del provider per nome. NON propaga MAI eccezioni al producer: una notifica fallita non deve far fallire un watchdog o un planner (si degrada a log d'errore). Quel «non propaga mai» è giusto per i producer e sbagliato per chi vuole SAPERE se il canale funziona: il 2026-07-29 il pulsante «Invia notifica di prova» di /admin/autonomy dichiarava successo mentre il recapito falliva per TELEGRAM_BOT_TOKEN assente — una verifica che dice la cosa rassicurante indipendentemente dalla realtà è peggio di nessuna verifica. Da qui : stesso identico percorso (gate, rate-limit, provider), ma l'esito torna al chiamante invece di finire solo nel log.

| | Firma | Descrizione |
|---|---|---|
| `p` | `NotificationChannelStatus ChannelStatus` | [E5] Stato corrente del canale, per la UI: si legge senza inviare nulla. |
| `m` | `Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)` | Contratto invariato verso i producer: nessuna eccezione, nessun esito da controllare. Il risultato lo assorbe il log, esattamente come prima. |

## `ProcioneMGR/Services/Notifications/NotificationServiceCollectionExtensions.cs`

### 📦 `NotificationServiceCollectionExtensions`

> Composizione DI del canale di notifica (Fase 4, PRD Autonomia): condivisa dagli host che hanno producer (monolite; ProcioneMGR.Trading per il watchdog in modalità remota). TryAdd ovunque: i test possono sostituire il notifier registrando prima il proprio fake.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IServiceCollection AddProcioneNotifications(this IServiceCollection services, IConfiguration configuration)` | — |

## `ProcioneMGR/Services/Notifications/TelegramNotifier.cs`

### 📦 `TelegramNotifier` `(`

> Provider Telegram (PRD Autonomia §7: pragmatico per un solo operatore — gratuito, push su mobile). Il token del bot NON sta mai in config/repo: SOLO dalla variabile d'ambiente (stesso patto di ANTHROPIC_API_KEY per il layer AI). La chat di destinazione ( ) non è un segreto e sta in config.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string TokenEnvVar` | — |
| `k` | `string HttpClientName` | Nome del client HTTP nominato (i test lo intercettano con un handler scriptato). |
| `p` | `string Name` | — |
| `m` | `Task SendAsync(NotificationSeverity severity, string title, string body, CancellationToken ct)` | — |

# `Services/Observability/`

## `ProcioneMGR/Services/Observability/MetricsCollector.cs`

### 📦 `MetricsCollector` `: IHostedService, IDisposable`

> Collettore IN-PROCESSO dei contatori di : un del BCL che accumula i totali (per strumento + tag) e un riassunto degli istogrammi, così la dashboard può mostrarli SENZA un backend OpenTelemetry (che resta l'export opzionale/spento). I totali sono "dalla partenza del processo": si azzerano a un riavvio. Zero dipendenze esterne, thread-safe. [Fase 1] Gli istogrammi seguiti sono passati da uno a tre (slippage dei job a fette, shortfall degli ordini di corsia, latenza dell'ordine), quindi l'accumulo è stato generalizzato in invece di restare replicato campo per campo.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string ExecutionSlippageInstrument` | Slippage degli ordini eseguiti a fette (TWAP/VWAP/Iceberg). |
| `k` | `string TradingSlippageInstrument` | [Fase 1] Shortfall degli ordini di corsia — la stragrande maggioranza degli ordini. |
| `k` | `string OrderLatencyInstrument` | [Fase 1] Latenza invio→risposta dell'ordine. |
| `m` | `Task StartAsync(CancellationToken cancellationToken)` | — |
| `m` | `Task StopAsync(CancellationToken cancellationToken)` | — |
| `m` | `MetricsSnapshot Snapshot()` | — |
| `m` | `void Dispose()` | — |

### 📦 `HistogramAccumulator`

> Accumulo di un istogramma: totali esatti (conteggio/somma/min/max) più una finestra scorrevole degli ultimi campioni. I percentili si calcolano su quella finestra e NON su tutta la sessione: tenere ogni campione per un processo che gira per giorni non è accettabile, e per la domanda che questi percentili devono servire — "quanto è andata male la coda di recente" — la finestra è anche la risposta più utile. La distinzione è dichiarata anche in UI, per non far leggere come "P99 di sempre" un numero che è "P99 degli ultimi campioni".

| | Firma | Descrizione |
|---|---|---|
| `m` | `void Add(double value)` | — |
| `m` | `HistogramSummary Summarize()` | — |

### 🧾 `HistogramSummary` `(`

> Riassunto di un istogramma. / / sono calcolati sulla finestra dei campioni recenti (vedi HistogramAccumulator ).

| | Firma | Descrizione |
|---|---|---|
| `p` | `HistogramSummary Empty` | — |

### 🧾 `MetricsSnapshot` `(`

> Fotografia immutabile dei contatori accumulati, per la dashboard.

| | Firma | Descrizione |
|---|---|---|
| `m` | `HistogramSummary Histogram(string instrument)` | Riassunto di un istogramma seguito; vuoto se lo strumento non ha mai registrato. |
| `p` | `long SlippageCount` | Slippage dei job a fette — scorciatoie storiche usate dalla dashboard. |
| `p` | `double SlippageMean` | — |
| `p` | `double SlippageMin` | — |
| `p` | `double SlippageMax` | — |
| `p` | `IReadOnlyList&lt;(DateTime T, double V)&gt; SlippageRecent` | — |
| `m` | `long Total(string instrument)` | Totale di uno strumento (somma su tutte le combinazioni di tag). |
| `m` | `IReadOnlyList&lt;(string Value, long Count)&gt; GroupByTag(string instrument, string tagKey)` | Ripartizione di uno strumento per il valore di un tag (es. "status", "side", "action"). |

## `ProcioneMGR/Services/Observability/ObservabilityExtensions.cs`

### 📦 `ObservabilityExtensions`

> Wiring dell'export OpenTelemetry opt-in (flag Observability:Enabled , default OFF). Estratto da Program.cs per essere testabile in isolamento. Con il flag OFF non registra nulla (costo zero); con il flag ON esporta metriche del meter e log applicativi via OTLP verso il collector locale (infra/observability/docker-compose.yml). L'exporter OTLP è fire-and-forget con retry in background: nessun collector in ascolto non causa errori né rallenta l'app.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IServiceCollection AddProcioneObservability(this IServiceCollection services, IConfiguration configuration)` | — |

## `ProcioneMGR/Services/Observability/ProcioneMetrics.cs`

### 📦 `ProcioneMetrics` `: IDisposable`

> Punto unico di strumentazione (Fase 5): un del BCL con i contatori/istogrammi degli eventi che contano per un sistema autonomo 24/7 — promozioni di corsia, drift, ritiri di modelli, run di pipeline, trade, esecuzioni. È basato SOLO su System.Diagnostics.Metrics (nessuna dipendenza esterna): l'export (OpenTelemetry/OTLP) è un layer opzionale sopra, wired in Program.cs e spento di default. Senza un listener/exporter, registrare una metrica costa quasi nulla, quindi la strumentazione resta sempre attiva senza penalità.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string MeterName` | Nome del meter: OpenTelemetry.AddMeter(MeterName) lo aggancia per l'export. |
| `c` | `ProcioneMetrics()` | — |
| `m` | `void RecordLanePromotion(int laneId, string newMode)` | — |
| `m` | `void RecordDriftAlerts(string symbol, string timeframe, int alertCount)` | — |
| `m` | `void RecordModelRetired(string symbol, string timeframe)` | — |
| `m` | `void RecordPipelineRun(string status)` | — |
| `m` | `void RecordTradeExecuted(string mode, string side, string action = "Open")` | — |
| `m` | `void RecordExecutionJob(string algorithm, string status)` | — |
| `m` | `void RecordExecutionSlippage(double bps, string algorithm)` | — |
| `m` | `void RecordMlComparison(string outcome)` | Esito di un confronto dual-read: match \| mismatch \| timeout \| error. |
| `m` | `void RecordLlmCall(string path, string result)` | Chiamata Claude: path advisory\|veto, result ok\|error\|skipped_breaker\|skipped_unconfigured. |
| `m` | `void RecordLlmAdvisory(bool isError)` | — |
| `m` | `void RecordLlmVeto()` | — |
| `m` | `void RecordSentimentSync(string source, string esito)` | Sync di una fonte sentiment: esito ok \| error. |
| `m` | `void RecordRealtimeReconnect(string exchange)` | — |
| `m` | `void RecordProtectiveExit(string source, string reason)` | [R1] Metrica-prova della fase: confrontando source=tick con source=candle si vede quante uscite protettive sono state colte dal feed real-time invece che alla chiusura della candela — cioè quanto ritardo è stato effetti… |
| `m` | `void RecordTradingSlippage(double bps, string mode, string action)` | [Fase 1] Shortfall di un SINGOLO ordine, positivo = costo. Si affianca a senza sovrapporvisi, perché le due misurano cose diverse: là lo scarto dell'INTERO piano a fette dal suo prezzo di arrivo a t0, qui la qualità di … |
| `m` | `void RecordOrderLatency(double milliseconds, string exchange, string market, string outcome)` | [Fase 1] Latenza invio→risposta di un ordine. Da leggere ai percentili alti (P95/P99): è sulla coda, non sulla media, che si decide se un ritardo è costato un fill. |
| `m` | `void Dispose()` | — |

# `Services/Registry/`

## `ProcioneMGR/Services/Registry/ModelRegistry.cs`

### 📦 `ModelRegistryOptions`

> Opzioni del registry (sezione config "Registry").

| | Firma | Descrizione |
|---|---|---|
| `p` | `double MinChampionDeflatedSharpe` | Deflated Sharpe minimo perché un modello possa diventare Champion, anche se non c'è un Champion in carica da battere. Default 0: non blocca il primo Champion, ma il gate "batti l'incumbent" resta sempre attivo. Alzabile… |

### 🧾 `PromotionOutcome` `(bool Promoted, string Reason, int? DemotedChampionId = null);`

> Esito di un tentativo di promozione a Champion.

### 🔌 `IModelRegistry`

> Governo del ciclo di vita dei modelli ML (Fase 2, rif. docs/REPORT-ANALISI-RICOSTRUZIONE). Fa rispettare due invarianti: (1) un solo Champion per (Symbol, Timeframe) ; (2) un Challenger può diventare Champion solo se il suo Deflated Sharpe (Fase 1) è ≥ di quello del Champion in carica — un modello meno difendibile non sostituisce mai uno più difendibile. NON tocca mai il trading Live: sposta solo di stadio i record. Additivo: lavora sui campi di ciclo di vita di , senza tabelle nuove.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;SavedMlModel?&gt; GetChampionAsync(string symbol, string timeframe, CancellationToken ct = default)` | Il Champion attivo per (symbol, timeframe), o null se non esiste. |
| `m` | `Task&lt;IReadOnlyList&lt;SavedMlModel&gt;&gt; ListGroupAsync(string symbol, string timeframe, CancellationToken ct = default)` | Tutti i modelli di un gruppo (symbol, timeframe), per la UI del registry. |
| `m` | `Task PromoteToChallengerAsync(int modelId, CancellationToken ct = default)` | Porta un modello Staging → Challenger (in valutazione). No-op se già oltre. |
| `m` | `Task&lt;PromotionOutcome&gt; TryPromoteToChampionAsync(int modelId, CancellationToken ct = default)` | Prova a promuovere il modello a Champion applicando il gate DSR e l'invariante di unicità. Se supera, l'eventuale Champion in carica viene ritirato. Idempotente: promuovere l'attuale Champion è un successo no-op. |
| `m` | `Task RetireAsync(int modelId, string reason, bool requestRetrain, CancellationToken ct = default)` | Ritira un modello con un motivo; opzionalmente marca "retrain accodato" (nessun retrain automatico). |

### 📦 `ModelRegistry` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;SavedMlModel?&gt; GetChampionAsync(string symbol, string timeframe, CancellationToken ct = default)` | — |
| `m` | `Task&lt;IReadOnlyList&lt;SavedMlModel&gt;&gt; ListGroupAsync(string symbol, string timeframe, CancellationToken ct = default)` | — |
| `m` | `Task PromoteToChallengerAsync(int modelId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;PromotionOutcome&gt; TryPromoteToChampionAsync(int modelId, CancellationToken ct = default)` | — |
| `m` | `Task RetireAsync(int modelId, string reason, bool requestRetrain, CancellationToken ct = default)` | — |

# `Services/Experiments/`

## `ProcioneMGR/Services/Experiments/ExperimentEntities.cs`

### 📦 `ExperimentRun`

> Un run sperimentale : la registrazione osservabile e confrontabile di UN'esecuzione di ricerca (backtest, sweep di ottimizzazione, training ML, campagna di discovery, pipeline...). Generalizza il tracking che finora esisteva SOLO per il Pipeline a 15 stadi ( PipelineRun / PipelineArtifact ): stesso pattern a colonne JSON (schema stabile mentre parametri/metriche evolvono), ma disaccoppiato da un singolo consumatore. Non sostituisce PipelineRun (il cui checkpoint per-stadio è un bisogno diverso): il Pipeline può SCRIVERE in aggiunta un di kind "Pipeline" per comparire nella stessa tabella comparativa degli altri (comporre, non sostituire). Rif. docs/archive/ROADMAP-QLIB.md §1.3 .

| | Firma | Descrizione |
|---|---|---|
| `p` | `Guid Id` | — |
| `p` | `string Kind` | "Backtest" \| "Optimization" \| "MlTraining" \| "Discovery" \| "Pipeline" \| "AlphaMining". |
| `p` | `string Name` | Etichetta leggibile scelta dal chiamante (es. "LightGBM · BTCUSDT · 50 fattori"). |
| `p` | `string Status` | "Running" \| "Completed" \| "Failed". |
| `p` | `string CreatedBy` | Id dell'utente che ha avviato il run (vuoto per run automatici/di sistema). |
| `p` | `string? Symbol` | Symbol principale del run, denormalizzato per il filtro della UI (nullable). |
| `p` | `string? Timeframe` | Timeframe principale del run, denormalizzato per il filtro della UI (nullable). |
| `p` | `DateTime StartedAt` | — |
| `p` | `DateTime? CompletedAt` | — |
| `p` | `string ParametersJson` | JSON dei parametri/configurazione del run (shape libera decisa dal chiamante). |
| `p` | `string ParametersHash` | Hash SHA-256 (hex) di : versioning "git-like" leggero per riconoscere run con configurazione identica. NON è un content-addressable store completo (scelta dichiarata: complessità non giustificata qui, vedi ROADMAP-QLIB … |
| `p` | `string MetricsJson` | JSON: dizionario nome→valore (decimal) delle metriche finali del run. |
| `p` | `string? ErrorLog` | — |

### 📦 `ExperimentArtifact`

> Artefatto voluminoso associato a un run (equity curve, lista trade, importanze feature, ...), tenuto FUORI dalla riga del run così la tabella storica resta veloce da interrogare — stesso principio di PipelineArtifact .

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `Guid RunId` | — |
| `p` | `string KindTag` | Etichetta del tipo di artefatto ("EquityCurve" \| "FeatureImportance" \| ...). |
| `p` | `string PayloadJson` | — |
| `p` | `DateTime CreatedAt` | — |

## `ProcioneMGR/Services/Experiments/ExperimentTracker.cs`

### 📦 `ExperimentTracker` `: IExperimentTracker`

> Implementazione di su EF Core. Usa (context a vita breve per operazione) così è sicuro come Singleton e utilizzabile da servizi/worker a lunga durata e da componenti Blazor. Disciplina anti-regressione: ogni metodo apre e chiude il proprio context; nessuno stato condiviso. Il tracker NON lancia mai eccezioni verso i calcoli che lo ospitano quando è usato tramite gli helper "best-effort" (vedi ).

| | Firma | Descrizione |
|---|---|---|
| `c` | `ExperimentTracker(IDbContextFactory&lt;ApplicationDbContext&gt; dbFactory)` | — |
| `m` | `Task LogMetricsAsync(Guid runId, IReadOnlyDictionary&lt;string, decimal&gt; metrics, CancellationToken ct = default)` | — |
| `m` | `Task LogArtifactAsync(Guid runId, string kindTag, object payload, CancellationToken ct = default)` | — |
| `m` | `Task CompleteAsync(Guid runId, string status, string? errorLog = null, CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Experiments/ExperimentTrackerExtensions.cs`

### 📦 `ExperimentTrackerExtensions`

> Helper "best-effort" per il tracking: incapsulano le chiamate al in un try/catch così un problema di logging (DB occupato, transitorio) NON fa mai cadere il calcolo osservato. Gli engine restano la fonte di verità; il tracker è un osservatore sacrificabile. Un run non aperto è rappresentato da , che gli altri helper trattano come no-op.

## `ProcioneMGR/Services/Experiments/IExperimentTracker.cs`

### 🔌 `IExperimentTracker`

> Logger sperimentale generalizzato (un piccolo MLflow interno). Ogni tipo di esecuzione di ricerca apre un run all'inizio, ne registra le metriche/artefatti, e lo chiude a fine — così backtest, sweep, training e discovery finiscono nella STESSA tabella comparabile, invece di vivere solo nella UI del momento e poi perdersi. Rif. docs/archive/ROADMAP-QLIB.md §1.3 . Additivo per costruzione: non modifica il comportamento degli engine, aggiunge solo osservabilità. Idempotente rispetto agli errori del chiamante: un fallimento del logging non deve mai far cadere il calcolo che lo ospita (gli engine sono la fonte di verità, il tracker è un osservatore).

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task LogMetricsAsync(Guid runId, IReadOnlyDictionary&lt;string, decimal&gt; metrics, CancellationToken ct = default)` | Registra/aggiorna le metriche finali del run (merge nel dizionario esistente). |
| `m` | `Task LogArtifactAsync(Guid runId, string kindTag, object payload, CancellationToken ct = default)` | Allega un artefatto voluminoso (equity curve, importanze, ...) al run. |
| `m` | `Task CompleteAsync(Guid runId, string status, string? errorLog = null, CancellationToken ct = default)` | Chiude il run con lo stato finale ("Completed" \| "Failed") ed eventuale log d'errore. |

# `Services/Health/`

## `ProcioneMGR/Services/Health/HostHeartbeats.cs`

### 📦 `HeartbeatOptions`

> [AF5.1] Configurazione dell'heartbeat incrociato. Default SPENTO: a config vuota nessun host scrive né sorveglia, comportamento identico a prima della fase (invariante di piattaforma). Sezione Heartbeat , hot-reload via IOptionsMonitor.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | — |
| `p` | `int WriteSeconds` | Cadenza di scrittura del proprio battito. |
| `p` | `int StaleMinutes` | Dopo quanti minuti senza battito l'ALTRO host è dichiarato muto. Molto maggiore del periodo di scrittura (10× di default): un tick perso per rumore di rete non deve allarmare nessuno. |

### 🔢 `HeartbeatHealth`

> Stato di salute dell'altro host, come lo vede il monitor.

### 📦 `HeartbeatMonitorLogic`

> Decisione pura del monitor, separata dal worker per essere testabile con un orologio finto.

| | Firma | Descrizione |
|---|---|---|
| `m` | `HeartbeatHealth Evaluate(DateTime? lastSeenUtc, DateTime nowUtc, TimeSpan staleAfter)` | — |

### 🧾 `HeartbeatNotice` `(NotificationSeverity Severity, string Title);`

> Cosa notificare a fronte di un'osservazione (null = niente).

### 📦 `HeartbeatTransitionTracker` `(string otherRole)`

> Traduce la sequenza di osservazioni in notifiche UNA-PER-TRANSIZIONE, mai a raffica: Warning quando l'altro host diventa muto (anche se lo è già alla prima osservazione), Info quando torna. Unknown non produce mai nulla, in nessuna direzione: prima di dichiarare un guasto bisogna aver visto — ora o in passato — un battito, oppure la sua assenza prolungata su una riga che esiste.

| | Firma | Descrizione |
|---|---|---|
| `m` | `HeartbeatNotice? Observe(HeartbeatHealth current)` | — |

### 📦 `HostHeartbeatWorker` `(`

> Scrive il battito del PROPRIO host (upsert sulla riga col proprio ruolo, mai su quella altrui). Registrato in entrambi gli host da AddTradingLanes; a feature spenta dorme e basta.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

### 📦 `HeartbeatMonitorWorker` `(`

> Sorveglia la riga dell'ALTRO host. Vive: nel motore sempre (il guscio è riavviabile per definizione, ma un guscio muto da ore significa niente advisory, niente pipeline, niente occhi); nel guscio solo col trading remoto (in-process non esiste un "engine" separato da sorvegliare). Le notifiche passano dal dispatcher normale — rate-limit e coalescing compresi.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

# `Services/Config/`

## `ProcioneMGR/Services/Config/AdminConfigRules.cs`

### 📦 `AdminConfigRules`

> Validazione LATO SERVER dei pannelli di configurazione admin (/admin/autonomy, /admin/protections, i parametri di /execution). Perché serve, visto che i campi hanno già min= nell'HTML: l'attributo min di un &lt;input type="number"&gt; vincola la validazione di un FORM, non il binding di Blazor — con @bind il valore digitato arriva al modello comunque, e da lì dritto in appsettings.json . Prima di questa classe si poteva salvare Llm:MaxTokens=0 (ogni chiamata all'API rifiutata), Drift:IntervalHours=0 (il PeriodicTimer del worker lancia all'avvio e la funzione muore in silenzio fino al riavvio successivo), o soglie di ingresso/uscita invertite che fanno aprire e chiudere il carry alla stessa valutazione. Il contratto è volutamente semplice: un solo punto di ingresso ( ), null = configurazione accettabile, altrimenti il messaggio da mostrare all'operatore. Nessuna correzione silenziosa dei…

| | Firma | Descrizione |
|---|---|---|
| `m` | `string? Validate(object options)` | Valida le opzioni di una sezione. Restituisce null se sono accettabili, altrimenti il messaggio d'errore. I tipi non riconosciuti passano: questa classe non è un gate obbligatorio per ogni sezione esistente, è il posto … |

## `ProcioneMGR/Services/Config/AppConfigWriter.cs`

### 🔌 `IAppConfigWriter`

> Persiste una sezione di configurazione in appsettings.json . Il provider JSON dell'host ha reloadOnChange=true , quindi chi legge via IOptionsMonitor&lt;T&gt; (o IConfiguration live) vede i nuovi valori entro ~1s senza riavvio — è lo stesso meccanismo del pannello sicurezza di /trading, generalizzato per /admin/autonomy. Niente tabella DB, niente provider custom: il file resta l'unica fonte di verità della configurazione.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task SaveSectionAsync&lt;T&gt;(string sectionPath, T options, CancellationToken ct = default)` | Serializza e lo scrive alla sezione (segmenti separati da : , es. "Trading:Safety" ; i nodi mancanti vengono creati). |

### 📦 `AppConfigWriter` `(IHostEnvironment env, ILogger&lt;AppConfigWriter&gt; logger) : IAppConfigWriter`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task SaveSectionAsync&lt;T&gt;(string sectionPath, T options, CancellationToken ct = default)` | — |

# `Services/Admin/`

## `ProcioneMGR/Services/Admin/DatabaseBackupHelper.cs`

### 🧾 `PgConnectionInfo` `(string Host, int Port, string Database, string Username, string? Password);`

> Parametri di connessione a un database PostgreSQL, estratti dalla connection string.

### 🧾 `IntegrityResult` `(bool Ok, string Message);`

> Esito di una verifica del backup (archivio leggibile ⇒ integro). true se pg_restore --list ha letto correttamente l'archivio.

### 🧾 `BackupResult` `(bool Success, string BackupPath, long SizeBytes, IntegrityResult Integrity, string? Erro…`

> Esito di un backup completo.

### 🧾 `BackupInfo` `(string FileName, string FullPath, DateTime CreatedUtc, long SizeBytes);`

> Metadati di un file di backup già presente.

### 📦 `DatabaseBackupHelper`

> Helper (puro, senza stato) per il backup/ripristino di un database PostgreSQL tramite gli strumenti nativi pg_dump / pg_restore , che devono essere nel PATH. Il formato è il custom archive ( -Fc ): compresso, ripristinabile selettivamente e verificabile con pg_restore --list senza toccare il database. La password non viene mai passata sulla command line: è iniettata nell'ambiente del processo figlio via PGPASSWORD . A differenza del vecchio backup SQLite (copia di file + WAL checkpoint), qui il dump è già uno snapshot transazionalmente consistente prodotto dal server: nessun bisogno di fermare l'app.

| | Firma | Descrizione |
|---|---|---|
| `m` | `BackupResult Backup(PgConnectionInfo conn, string backupDir)` | Backup completo e verificato: (1) pg_dump -Fc in con timestamp, (2) pg_restore --list sulla copia per confermarne la leggibilità. Se la verifica fallisce, il file viene eliminato e il risultato è Success=false . |
| `m` | `IntegrityResult IntegrityCheck(string backupPath)` | Verifica che un archivio di backup sia leggibile via pg_restore --list . |
| `m` | `IReadOnlyList&lt;BackupInfo&gt; ListBackups(string backupDir)` | Elenca i backup (*.dump) in , più recenti prima. |
| `m` | `void Restore(PgConnectionInfo conn, string backupPath)` | Ripristina un backup nel database di destinazione con pg_restore --clean --if-exists (droppa gli oggetti esistenti prima di ricrearli). Verifica prima la leggibilità dell'archivio. Operazione distruttiva: sovrascrive lo… |

## `ProcioneMGR/Services/Admin/DatabaseBackupService.cs`

### 📦 `DatabaseBackupService`

> Wrapper iniettabile attorno a per l'uso dalla UI (pagina /admin/backup ). Risolve i parametri di connessione PostgreSQL dalla connection string PostgresConnection e la cartella backup/ relativa alla content root, così il chiamante non deve conoscerli. Il backup usa gli strumenti nativi pg_dump / pg_restore (devono essere nel PATH): vedi e docs/POSTGRES_MIGRATION.md.

| | Firma | Descrizione |
|---|---|---|
| `c` | `DatabaseBackupService(IConfiguration configuration, IHostEnvironment env)` | — |
| `p` | `string TargetDatabase` | Nome del database di destinazione (per la UI). |
| `p` | `string BackupDirectory` | Cartella dove vivono i backup. |
| `m` | `BackupResult CreateBackup()` | Crea un backup verificato del DB attivo. Vedi . |
| `m` | `IReadOnlyList&lt;BackupInfo&gt; ListBackups()` | Elenca i backup esistenti, più recenti prima. |
| `m` | `IntegrityResult VerifyBackup(string backupPath)` | Verifica la leggibilità di un file di backup ( pg_restore --list ). |
| `m` | `void Restore(string backupPath)` | Ripristina un backup nel DB attivo ( pg_restore --clean --if-exists ). |

# `Services/Preferences/`

## `ProcioneMGR/Services/Preferences/PageConfigStore.cs`

### 🔌 `IPageConfigStore`

> Persistenza delle configurazioni di pagina per utente: preset con nome e "ultima configurazione usata" (nome vuoto, riscritta a ogni Run). Il JSON è opaco: lo schema lo definisce la pagina, il servizio si limita a upsert/lettura/lista/cancellazione.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task SaveAsync(string userId, string pageKey, string name, string configJson, CancellationToken ct = default)` | — |
| `m` | `Task&lt;string?&gt; LoadAsync(string userId, string pageKey, string name, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;string&gt;&gt; ListNamesAsync(string userId, string pageKey, CancellationToken ct = default)` | Solo i preset con nome (l'ultima configurazione usata è esclusa), in ordine alfabetico. |
| `m` | `Task DeleteAsync(string userId, string pageKey, string name, CancellationToken ct = default)` | — |

### 📦 `PageConfigStore` `(IDbContextFactory&lt;ApplicationDbContext&gt; dbFactory) : IPageConfigStore`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task SaveAsync(string userId, string pageKey, string name, string configJson, CancellationToken ct = default)` | — |
| `m` | `Task&lt;string?&gt; LoadAsync(string userId, string pageKey, string name, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;string&gt;&gt; ListNamesAsync(string userId, string pageKey, CancellationToken ct = default)` | — |
| `m` | `Task DeleteAsync(string userId, string pageKey, string name, CancellationToken ct = default)` | — |

# `Data/`

## `ProcioneMGR/Data/AiCredential.cs`

### 📦 `AiCredential`

> Chiave API di un provider AI, cifrata a riposo (AES-256-GCM via converter — stesso pattern di ). Una riga per provider, a livello di PIATTAFORMA e non per-utente: il layer AI (supervisione, e gli usi futuri) è un servizio della piattaforma, come i worker che lo eseguono. La variabile d'ambiente resta il fallback per chi non vuole la chiave a database (vedi AiKeyStore : DB prima, env poi).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `string Provider` | "Anthropic" \| "Nvidia" \| domani altri: stringa e non enum, un provider nuovo non deve richiedere una migrazione. |
| `p` | `string ApiKey` | Cifrata a riposo dal converter. |
| `p` | `DateTime UpdatedAtUtc` | — |

## `ProcioneMGR/Data/AltDataPoint.cs`

### 📦 `AltDataPoint`

> Un elemento di dato alternativo (cap. 3): oggi solo notizie via RSS, pensata per essere generica (stesso spirito di TrackedSeries per l'OHLCV) così da poter accogliere in futuro altre fonti (social, on-chain) senza cambiare schema.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `DateTime TimestampUtc` | Data di pubblicazione (dalla fonte), UTC. |
| `p` | `string Source` | "CoinDesk" \| "Cointelegraph" \| "TheBlock" \| "Decrypt" \| ... |
| `p` | `string Title` | — |
| `p` | `string? Summary` | — |
| `p` | `string? Url` | — |
| `p` | `string Category` | "Regulatory" \| "Security" \| "Institutional" \| "Other" — da NewsImpactClassifier . |
| `p` | `string SymbolsJson` | Simboli rilevanti individuati nel testo (JSON array di stringhe, es. ["BTC","ETH"]). |
| `p` | `decimal? SentimentScore` | Punteggio di sentiment in [-1,+1], null finché non calcolato da un ISentimentScorer . |
| `p` | `string DedupeKey` | Chiave univoca per evitare duplicati fra sync successive dello stesso feed (Source+Url). |

## `ProcioneMGR/Data/AppRoles.cs`

### 📦 `AppRoles`

> Ruoli applicativi di ProcioneMGR. Centralizzati come costanti per evitare "magic strings" sparse tra registrazione, seeding e attributi [Authorize].

| | Firma | Descrizione |
|---|---|---|
| `k` | `string Admin` | — |
| `k` | `string Manager` | — |
| `k` | `string User` | — |
| `p` | `string[] All` | Tutti i ruoli, usati dal seeder all'avvio. |

## `ProcioneMGR/Data/ApplicationDbContext.cs`

### 📦 `ApplicationDbContext` `: IdentityDbContext&lt;ApplicationUser&gt;`

| | Firma | Descrizione |
|---|---|---|
| `c` | `ApplicationDbContext(DbContextOptions&lt;ApplicationDbContext&gt; options, IEncryptionService encryption)` | — |
| `p` | `DbSet&lt;OhlcvData&gt; OhlcvData` | Dati di mercato time-series. Tabella ad alto volume, separata da Identity. |
| `p` | `DbSet&lt;ExchangeCredential&gt; ExchangeCredentials` | Credenziali API degli exchange, cifrate a riposo. |
| `p` | `DbSet&lt;ExchangeCredentialCiphertext&gt; ExchangeCredentialCiphertexts` | Stessa tabella di ma col CIPHERTEXT grezzo (nessun converter): per i percorsi che decifrano riga per riga in memoria e devono sopravvivere a una riga cifrata con una master key diversa. Sola lettura (keyless). |
| `p` | `DbSet&lt;AiCredential&gt; AiCredentials` | Chiavi API dei provider AI (Anthropic, Nvidia, …), cifrate a riposo. Una riga per provider. |
| `p` | `DbSet&lt;TrackedSeries&gt; TrackedSeries` | Watchlist globale: serie mantenute aggiornate dal worker in background. |
| `p` | `DbSet&lt;SavedStrategy&gt; SavedStrategies` | Configurazioni di strategia salvate per-utente. |
| `p` | `DbSet&lt;EnsembleState&gt; EnsembleStates` | Stato dell'ensemble (riga singola, JSON). |
| `p` | `DbSet&lt;EnsembleRebalanceHistory&gt; EnsembleRebalanceHistory` | Storico rebalancing dell'ensemble. |
| `p` | `DbSet&lt;ProcioneMGR.Services.Regime.RegimeModel&gt; RegimeModels` | Modelli di market regime addestrati (K-means). |
| `p` | `DbSet&lt;SavedMlModel&gt; SavedMlModels` | Modelli di previsione dei rendimenti (Lineare/RF/LightGBM) salvati per-utente. |
| `p` | `DbSet&lt;SavedFactor&gt; SavedFactors` | Fattori alpha "minati" (formulaic alpha mining) salvati per-utente. |
| `p` | `DbSet&lt;AltDataPoint&gt; AltDataPoints` | Dati alternativi (notizie RSS con categoria/sentiment). |
| `p` | `DbSet&lt;SentimentMetricPoint&gt; SentimentMetricPoints` | Serie numeriche di market mood (Fear & Greed, long/short, taker, OI, funding) — Sentiment 2.0. |
| `p` | `DbSet&lt;UserPageConfig&gt; UserPageConfigs` | Configurazioni di pagina per-utente: preset con nome + ultima configurazione usata. |
| `p` | `DbSet&lt;ProcioneMGR.Services.Trading.Order&gt; Orders` | — |
| `p` | `DbSet&lt;ProcioneMGR.Services.Trading.OpenPosition&gt; OpenPositions` | — |
| `p` | `DbSet&lt;ProcioneMGR.Services.Trading.TradeRecord&gt; TradeRecords` | — |
| `p` | `DbSet&lt;ProcioneMGR.Services.Trading.TradingEngineState&gt; TradingEngineStates` | — |
| `p` | `DbSet&lt;ProcioneMGR.Services.Trading.TradingAuditLog&gt; TradingAuditLogs` | — |
| `p` | `DbSet&lt;ProcioneMGR.Services.Trading.LaneQuarantine&gt; LaneQuarantines` | Corsie in quarantena per violazione di invarianti contabili (Fase 0-A3, PRD Autonomia). |
| `p` | `DbSet&lt;ProcioneMGR.Services.Trading.ExecutionJob&gt; ExecutionJobs` | Piani di esecuzione live "a fette" (TWAP/VWAP/Iceberg) in corso/storici, per corsia. |
| `p` | `DbSet&lt;ProcioneMGR.Services.Pipeline.PipelineConfiguration&gt; PipelineConfigurations` | — |
| `p` | `DbSet&lt;ProcioneMGR.Services.Pipeline.PipelineRun&gt; PipelineRuns` | — |
| `p` | `DbSet&lt;ProcioneMGR.Services.Pipeline.PipelineArtifact&gt; PipelineArtifacts` | — |
| `p` | `DbSet&lt;ProcioneMGR.Services.Pipeline.VettingCampaign&gt; VettingCampaigns` | Campagne di vaglio del Campaign Planner (Fase 1, PRD Autonomia). |
| `p` | `DbSet&lt;ProcioneMGR.Services.Experiments.ExperimentRun&gt; ExperimentRuns` | — |
| `p` | `DbSet&lt;ProcioneMGR.Services.Experiments.ExperimentArtifact&gt; ExperimentArtifacts` | — |
| `p` | `DbSet&lt;ProcioneMGR.Services.Monitoring.Drift.DriftCheckResult&gt; DriftCheckResults` | Esiti dei check di drift feature (uno per modello per tick del FeatureDriftWorker). |
| `p` | `DbSet&lt;ProcioneMGR.Services.Alpha.FactorIcWindow&gt; FactorIcWindows` | [D2] Storia dell'IC per fattore: una riga per finestra, scritta dal FactorDriftWorker. |
| `p` | `DbSet&lt;ProcioneMGR.Services.Trading.ProtectiveExitShadow&gt; ProtectiveExitShadows` | [B3] Confronti d'ombra fra il tick e la candela sulle uscite protettive. |
| `p` | `DbSet&lt;HostHeartbeat&gt; HostHeartbeats` | [AF5.1] Battiti di vita degli host (una riga per processo: "shell" / "engine"). |
| `p` | `DbSet&lt;LlmUsageRecord&gt; LlmUsageRecords` | [AF1] Consumo LLM aggregato per giorno/provider/modello/percorso. |
| `p` | `DbSet&lt;OrchestratorDecision&gt; OrchestratorDecisions` | [AF2] Journal delle decisioni dell'orchestratore di flotta. |
| `p` | `DbSet&lt;TradePostMortem&gt; TradePostMortems` | [G4] Post-mortem delle operazioni chiuse in perdita: testo e classificazione, mai un parametro. |
| `m` | `void OnModelCreating(ModelBuilder builder)` | — |

## `ProcioneMGR/Data/ApplicationUser.cs`

### 📦 `ApplicationUser` `: IdentityUser`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string? DisplayName` | Nome visualizzato opzionale per la UI. |
| `p` | `DateTime CreatedAtUtc` | Momento di creazione dell'account (UTC). |

## `ProcioneMGR/Data/DatabaseMigrator.cs`

### 📦 `DatabaseMigrationOptions`

> Opzioni della migrazione automatica, sezione Database .

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool AutoMigrate` | Applica le migrazioni pendenti all'avvio. Default TRUE: fino al 2026-08-05 lo schema si applicava solo a mano ( dotnet ef database update ) e una migrazione dimenticata si manifestava come un errore runtime a metà giorn… |
| `p` | `int LockTimeoutSeconds` | Secondi di attesa per il lock: oltre, si rinuncia e si dichiara (un altro host sta migrando). |

### 🧾 `MigrationOutcome` `(`

> Esito della migrazione, per chi vuole raccontarlo (log, pannello, test). elenca le migrazioni applicate DA QUESTA chiamata.

### 📦 `DatabaseMigrator`

> [2026-08-05] Applica le migrazioni pendenti all'avvio. Il problema che risolve : l'app NON referenzia ProcioneMGR.Migrations.Postgres (sarebbe un ciclo di progetti — quello referenzia l'app per il tipo del DbContext), quindi non c'era migrate-on-startup e lo schema si applicava a mano. Funziona finché qualcuno si ricorda. La sera del 2026-08-05 non me ne sono ricordato io: la tabella nuova non c'era e me ne sono accorto solo interrogando il database. Come lo risolve senza creare il ciclo : EF risolve l'assembly delle migrazioni per NOME ( MigrationsAssembly("ProcioneMGR.Migrations.Postgres") ), quindi basta che la DLL sia accanto all'eseguibile — ci pensa un target di copia nel progetto delle migrazioni. Se la DLL NON c'è (host satelliti, che non la ricevono), non si finge nulla: si dichiara a log che le migrazioni non sono applicabili da qui e si prosegue, esattamente come prima. Perch…

| | Firma | Descrizione |
|---|---|---|
| `k` | `string MigrationsAssemblyName` | Nome dell'assembly che ospita le migrazioni (deve combaciare con MigrationsAssembly(...) ). |
| `m` | `void EnsureMigrationsAssemblyResolvable(ILogger? logger = null)` | Insegna al runtime a trovare l'assembly delle migrazioni accanto all'eseguibile. Perché non basta copiarci la DLL : un'app .NET framework-dependent risolve gli assembly dal proprio deps.json , non dai file che trova nel… |

## `ProcioneMGR/Data/DatabaseServiceCollectionExtensions.cs`

### 📦 `DatabaseServiceCollectionExtensions`

> Registrazione condivisa del DbContextFactory Postgres per il monolite e gli host satellite (ProcioneMGR.Ingestion oggi; trading/ml nelle fasi successive): unica fonte per le opzioni Npgsql e per il MigrationsAssembly, così gli host non divergono su resilienza/timeout. La connection string viene risolta SUBITO (fail-fast a startup, non alla prima creazione del context) e la lambda delle opzioni cattura solo la stringa, non l'intero builder.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IServiceCollection AddProcioneDatabase(this IServiceCollection services, IConfiguration configuration)` | — |

## `ProcioneMGR/Data/DbInitializer.cs`

### 📦 `DbInitializer`

> Inizializzazione all'avvio: garantisce l'esistenza dei ruoli applicativi (Admin / Manager / User). Lo schema del database si applica come passo separato (migrate-on-deploy, vedi InitializeAsync). La logica "primo utente = Admin" vive invece nel flusso di registrazione.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task InitializeAsync(IServiceProvider services)` | — |

## `ProcioneMGR/Data/EnsembleState.cs`

### 📦 `EnsembleState`

> Stato persistito dell'ensemble (configurazione + ultimo status), riga singola. I payload sono serializzati in JSON per non vincolare lo schema a strutture in evoluzione.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `int LaneId` | Corsia di trading isolata (0 = corsia di default, esistente prima del supporto multi-coppia). |
| `p` | `string ConfigurationJson` | JSON di EnsembleConfiguration. |
| `p` | `string StatusJson` | JSON dell'ultimo EnsembleStatus calcolato. |
| `p` | `DateTime LastUpdatedUtc` | — |

### 📦 `EnsembleRebalanceHistory`

> Storico dei rebalancing dell'ensemble.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `int LaneId` | Corsia di trading isolata (0 = corsia di default). |
| `p` | `DateTime Timestamp` | — |
| `p` | `string AllocationsJson` | JSON di List&lt;RebalanceAllocation&gt;. |
| `p` | `string Reason` | — |

## `ProcioneMGR/Data/ExchangeCredential.cs`

### 🔢 `ExchangeName`

> Exchange supportati. Valori espliciti per stabilita' della serializzazione.

### 📦 `ExchangeCredential`

> Credenziali API di un exchange, appartenenti a un singolo utente. SICUREZZA: , e sono cifrati a riposo via EncryptedStringConverter (AES-256-GCM) configurato nel . Sul DB non compaiono mai in chiaro.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `string UserId` | FK verso AspNetUsers (IdentityUser). |
| `p` | `ApplicationUser? User` | — |
| `p` | `ExchangeName ExchangeName` | — |
| `p` | `string Label` | Etichetta leggibile scelta dall'utente, es. "Binance Main". |
| `p` | `string ApiKey` | Cifrata a riposo. |
| `p` | `string ApiSecret` | Cifrato a riposo. |
| `p` | `string? Passphrase` | Cifrata a riposo. Obbligatoria per Bitget, nulla/assente altrove. |
| `p` | `bool IsTestnet` | — |
| `p` | `DateTime CreatedAt` | — |
| `m` | `string? ValidateBusinessRules()` | Regola di dominio: Bitget richiede la passphrase. Restituisce l'eventuale messaggio d'errore (null = valido). Usata sia dalla UI sia dal layer exchange. |
| `p` | `string MaskedApiKey` | ApiKey mascherata per la UI (mai esporre il secret). |
| `m` | `string Mask(string value)` | — |

## `ProcioneMGR/Data/ExchangeCredentialCiphertext.cs`

### 📦 `ExchangeCredentialCiphertext`

> Proiezione KEYLESS di sola lettura sulla tabella ExchangeCredentials che espone il CIPHERTEXT così com'è sul DB (nessun EncryptedStringConverter). Serve ai percorsi che devono sopravvivere a una riga cifrata con una master key diversa da quella del processo corrente (bug B2, docs/TEST-UI-2026-07-18.md): col converter la decifratura avviene DENTRO la materializzazione EF, quindi una sola riga indecifrabile (AuthenticationTagMismatchException) abbatteva l'intera query — e con essa la pagina /settings/exchanges o l'avvio Testnet/Live. Qui il ciphertext arriva intatto e la decifratura è per-riga, in memoria: vedi . Mappata con ToView sulla tabella esistente: nessuna tabella nuova, nessuna migrazione (le entità ToView sono escluse dal DDL di EnsureCreated/Migrations).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `string UserId` | — |
| `p` | `ExchangeName ExchangeName` | — |
| `p` | `string Label` | — |
| `p` | `string ApiKey` | Base64 del payload AES-GCM, NON decifrato. |
| `p` | `string ApiSecret` | Base64 del payload AES-GCM, NON decifrato. |
| `p` | `string? Passphrase` | Base64 del payload AES-GCM, NON decifrato. Null dove non usata (Binance). |
| `p` | `bool IsTestnet` | — |
| `p` | `DateTime CreatedAt` | — |

## `ProcioneMGR/Data/HostHeartbeat.cs`

### 📦 `HostHeartbeat`

> [AF5.1] Battito di vita di un host, una riga per processo. Il guscio scrive la SUA riga, il motore la SUA: ogni scrittore ha esattamente una riga, quindi la regola "ogni scrittore ha esattamente un host" vale a grana di riga — nessuna contesa, nessun lock. Il punto: se muore il motore, il guscio se ne accorge dagli errori gRPC ma nessuno lo DICE; se muore il guscio, il motore continua a tradare senza occhi e nessuno se ne accorge affatto. Ogni host legge la riga ALTRUI e dichiara la stantiezza (vedi HeartbeatMonitorWorker). Il caso "muoiono entrambi" non è coperto da qui per costruzione: per quello esiste il watchdog esterno (scripts/watchdog.ps1) e l'assenza del digest giornaliero.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string ShellRole` | — |
| `k` | `string EngineRole` | — |
| `p` | `string Host` | Chiave: il ruolo dell'host ("shell" \| "engine"). |
| `p` | `DateTime LastUtc` | Ultimo battito (UTC). La riga non si cancella mai: si aggiorna. |
| `p` | `string Version` | Versione informativa dell'assembly che batte, per la diagnostica dei deploy. |

## `ProcioneMGR/Data/LlmUsageRecord.cs`

### 📦 `LlmUsageRecord`

> [AF1] Consumo LLM aggregato per giorno/provider/modello/percorso. AGGREGATO e non a eventi di proposito: alla scala reale (decine di chiamate l'ora nei giorni pieni) una riga per chiamata sarebbe rumore da amministrare; una riga per combinazione al giorno resta leggibile per anni. Scritto solo dal LlmUsageFlushWorker del guscio (l'unico host col layer AI).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `DateTime DayUtc` | Giorno UTC (mezzanotte) a cui il consumo appartiene. |
| `p` | `string Provider` | Provider che ha SERVITO le chiamate (minuscolo, una voce di AiProviders.Known). |
| `p` | `string Model` | — |
| `p` | `string Path` | Percorso del guard ("advisory" \| "veto" \| "sentiment" \| "committee" \| "direct"). |
| `p` | `int Calls` | — |
| `p` | `long PromptTokens` | — |
| `p` | `long CompletionTokens` | — |

## `ProcioneMGR/Data/ModelStage.cs`

### 🔢 `ModelStage`

> Stadio del ciclo di vita di un modello nel registry (Fase 2). Progressione tipica: Staging → Challenger → Champion , con uscita a Retired . Vincolo di dominio: un solo per (Symbol, Timeframe). Persistito come stringa (come gli altri enum del dominio), così il valore è leggibile a DB e stabile rispetto ai riordini dell'enum.

## `ProcioneMGR/Data/OhlcvData.cs`

### 📦 `OhlcvData`

> Una candela OHLCV (Open/High/Low/Close/Volume) di mercato. Questa tabella e' progettata per ospitare ENORMI volumi time-series (storico di mercato), in netto contrasto con le poche righe delle tabelle Identity. Per questo motivo: - prezzi in (precisione esatta, niente errori float); - volume in (gestisce sia asset interi che frazionari/crypto); - timestamp in UTC ( ) per coerenza globale; - indice composto Univoco (Symbol, Timeframe, TimestampUtc) configurato via Fluent API nel per query time-series veloci e per impedire candele duplicate.

| | Firma | Descrizione |
|---|---|---|
| `p` | `long Id` | Chiave surrogata. long perche' la tabella crescera' oltre i limiti di int. |
| `p` | `string Symbol` | Strumento di mercato, es. "BTCUSDT", "AAPL". |
| `p` | `string Timeframe` | Intervallo della candela, es. "1m", "5m", "1h", "1d". |
| `p` | `DateTime TimestampUtc` | Apertura della candela in UTC (Unix epoch normalizzato a DateTime UTC). |
| `p` | `decimal Open` | — |
| `p` | `decimal High` | — |
| `p` | `decimal Low` | — |
| `p` | `decimal Close` | — |
| `p` | `decimal Volume` | Volume scambiato nel periodo. |
| `p` | `decimal? QuoteVolume` | Controvalore scambiato (quote asset, es. USDT). Binance k[7], Bitget k[6]. Null = non raccolto. |
| `p` | `long? TradeCount` | Numero di trade nella candela (Binance k[8]). Abilita dimensione media del trade e trade-bars. Null = non raccolto. |
| `p` | `decimal? TakerBuyVolume` | Volume base comprato da TAKER (Binance k[9]): l'order flow aggressivo — chi attraversa lo spread. L'imbalance TakerBuyVolume/Volume è la feature order-flow di T3.8b. Null = non raccolto. |
| `p` | `decimal? TakerBuyQuoteVolume` | Controvalore comprato da taker (Binance k[10]). Null = non raccolto. |

## `ProcioneMGR/Data/OrchestratorDecision.cs`

### 📦 `OrchestratorDecision`

> [AF2] Il journal della Queen Bee: UNA riga per decisione che porta informazione (assegnazione, ritiro, proposta di fascia grigia, blocco motivato). Persistito perché l'autonomia senza tracciabilità è un racconto: il pannello in /admin/autonomy mostra ESATTAMENTE ciò che l'orchestratore ha deciso, quando, con che motivo e con quale esito — dry-run compreso.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `DateTime AtUtc` | — |
| `p` | `string Kind` | "Assign" \| "Retire" \| "ProposeGrey" \| "Blocked". |
| `p` | `int? LaneId` | — |
| `p` | `Guid? RunId` | — |
| `p` | `string Source` | Chi ha scelto: "rules" (il default deterministico) \| "committee" (AF3) \| "default" (comitato fallito → default). |
| `p` | `string Reason` | — |
| `p` | `string VotesJson` | [AF3] I voti del comitato, uno per provider (JSON). "[]" quando il comitato non è stato interpellato. |
| `p` | `bool Applied` | True se l'azione è stata ESEGUITA (false in dry-run o su errore). |
| `p` | `bool DryRun` | True se la decisione è stata presa col dry-run acceso (solo journal, mai azione). |
| `p` | `string? Error` | — |

## `ProcioneMGR/Data/SavedFactor.cs`

### 📦 `SavedFactor`

> Un fattore alpha "minato" (formulaic alpha mining, rif. docs/archive/ROADMAP-QLIB.md §1.7 ) salvato per riuso: l'espressione serializzata + la diagnostica IC su selezione e holdout. L'espressione si ricostruisce in un IAlphaFactor (via AlphaExpressionFactor / IAlphaFactorFactory.Create con nome "expr:…"), quindi è riusabile ovunque come qualunque altro fattore.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `string UserId` | FK verso AspNetUsers. |
| `p` | `ApplicationUser? User` | — |
| `p` | `string Name` | Etichetta scelta dall'utente. |
| `p` | `string Expression` | Espressione alpha serializzata (S-expression), es. Div(Sub($Close,Mean($Close,5)),Std($Close,20)) . |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `int ForwardHorizon` | — |
| `p` | `double SelectionIc` | IC (Spearman) sul periodo di selezione dove il fattore è stato scelto. |
| `p` | `double? HoldoutIc` | IC sull'holdout mai visto: il verdetto onesto (null se non verificato). |
| `p` | `int Observations` | — |
| `p` | `int Size` | Numero di nodi dell'albero (complessità). |
| `p` | `DateTime CreatedAtUtc` | — |

## `ProcioneMGR/Data/SavedMlModel.cs`

### 📦 `SavedMlModel`

> Modello di previsione dei rendimenti ( IReturnPredictor ) addestrato e salvato da un utente in /ml, per riuso senza dover riaddestrare. A differenza di RegimeModel (che salva solo i parametri numerici del K-means e reimplementa l'inferenza a mano), qui salviamo il modello ML.NET GIÀ SERIALIZZATO (lo stesso blob prodotto da IReturnPredictor.Save ): per Random Forest/LightGBM (decine di alberi) reimplementare l'inferenza a mano sarebbe complesso e rischioso, mentre il round-trip Save/Load è già testato per tutti i modelli.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `string UserId` | FK verso AspNetUsers. |
| `p` | `ApplicationUser? User` | — |
| `p` | `string Name` | Nome scelto dall'utente, es. "RF momentum BTC 1h". |
| `p` | `string ModelType` | "Linear" \| "RandomForest" \| "GradientBoosting" — usato per ricreare l'istanza giusta al Load. |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `DateTime TrainingDataFrom` | — |
| `p` | `DateTime TrainingDataTo` | — |
| `p` | `int ForwardHorizon` | — |
| `p` | `string TargetKind` | [1.V fase 2] Cosa predice il modello: "ForwardReturn" \| "ForwardAbsReturn" \| "ForwardRealizedVol". Persistito perché la semantica della predizione È il contratto: un modello di volatilità non può alimentare segnali lo… |
| `p` | `bool IsDirectional` | True se la predizione è un rendimento atteso e può alimentare segnali long/short (MlStrategy, Champion). I modelli di rischio (vol) sono consumabili SOLO da sizing/ vol-targeting. Non mappato da EF (sola lettura). |
| `p` | `string FactorsJson` | JSON: List&lt;SavedFactorSpecDto&gt; — nome fattore + parametri, per ricreare i FactorSpec al Load. |
| `p` | `byte[] ModelBytes` | Il modello ML.NET serializzato (stesso formato prodotto da IReturnPredictor.Save). |
| `p` | `int TrainRowCount` | — |
| `p` | `double TrainCorrelation` | — |
| `p` | `DateTime CreatedAtUtc` | — |
| `p` | `ModelStage Stage` | Stadio nel registry. Default (candidato appena salvato). |
| `p` | `int Version` | Generazione del modello per (Symbol, Timeframe): informativa, assegnata dal registry. |
| `p` | `Guid? ExperimentRunId` | Lineage: il run di experiment tracking che ha prodotto/valutato questo modello (se noto). |
| `p` | `double? DeflatedSharpe` | Deflated Sharpe (Fase 1) associato al modello: è il gate di promozione a Champion. null se non ancora misurato ⇒ non promuovibile a Champion (nessuna promozione "alla cieca"). |
| `p` | `DateTime? PromotedAtUtc` | Quando è diventato Champion l'ultima volta (null se non lo è mai stato). |
| `p` | `DateTime? RetiredAtUtc` | Quando è stato ritirato (null se non ritirato). |
| `p` | `string? RetiredReason` | Motivo del ritiro (es. "superato da versione con DSR migliore", "drift: 3 feature in alert"). |
| `p` | `DateTime? RetrainRequestedAtUtc` | Marcatore "retrain accodato": valorizzato quando il ciclo drift chiede un riaddestramento. La piattaforma NON riaddestra da sola (scelta di sicurezza): è un segnale per l'operatore/UI. |

## `ProcioneMGR/Data/SavedStrategy.cs`

### 📦 `SavedStrategy`

> Configurazione di strategia salvata da un utente, riutilizzabile in /backtest. I parametri sono serializzati in JSON (Dictionary&lt;string, decimal&gt;).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `string UserId` | FK verso AspNetUsers. |
| `p` | `ApplicationUser? User` | — |
| `p` | `string Name` | Nome scelto dall'utente, es. "Il mio EMA veloce". |
| `p` | `string StrategyName` | Nome tecnico della strategia, es. "EmaCross". |
| `p` | `string ParametersJson` | Parametri serializzati: JSON di Dictionary&lt;string, decimal&gt;. |
| `p` | `DateTime CreatedAt` | — |
| `p` | `bool IsOptimized` | True se la configurazione proviene da un'ottimizzazione walk-forward (Fase 5). |
| `p` | `DateTime? OptimizationDate` | — |
| `p` | `decimal? OptimizationSharpe` | Sharpe out-of-sample medio dell'ottimizzazione che ha prodotto questi parametri. |

## `ProcioneMGR/Data/SentimentMetricPoint.cs`

### 📦 `SentimentMetricPoint`

> Un punto di una serie numerica di "market mood" (Sentiment 2.0): Fear & Greed, long/short ratio, taker buy/sell, open interest, funding. Tabella slim separata da (che è event-shaped: Title/Url/DedupeKey) perché queste sono serie DENSE per-metrica/per-simbolo su cui si calcolano baseline rolling e z-score. La dedupe è l'indice unico composito (Source, Metric, Symbol, TimestampUtc) + un pre-filtro applicativo nel sync service.

| | Firma | Descrizione |
|---|---|---|
| `p` | `long Id` | — |
| `p` | `DateTime TimestampUtc` | Timestamp del punto (dalla fonte), UTC. |
| `p` | `string Source` | "FearGreed" \| "BinanceFutures" \| ... (vedi ). |
| `p` | `string Metric` | Nome della metrica (vedi ). |
| `p` | `string Symbol` | Ticker base ("BTC", "ETH"); stringa VUOTA = mercato intero (es. Fear & Greed). Non-nullable di proposito: in Postgres i NULL sono distinti negli indici unici e la dedupe sui punti market-wide smetterebbe di funzionare. |
| `p` | `decimal Value` | Valore della metrica. Convenzioni: Fear & Greed 0-100; ratio così come arrivano dalla fonte; funding in PERCENTO (×100, convenzione della piattaforma). |

### 📦 `SentimentMetricSources`

> Nomi delle fonti di metriche sentiment (colonna Source).

| | Firma | Descrizione |
|---|---|---|
| `k` | `string FearGreed` | — |
| `k` | `string BinanceFutures` | — |
| `k` | `string BinanceLiquidations` | [F4] Liquidazioni dal WebSocket futures Binance (!forceOrder@arr), aggregate per ora. |

### 📦 `SentimentMetrics`

> Nomi delle metriche sentiment (colonna Metric).

| | Firma | Descrizione |
|---|---|---|
| `k` | `string FearGreedIndex` | Fear & Greed Index di alternative.me, 0 (extreme fear) - 100 (extreme greed). |
| `k` | `string GlobalLongShortRatio` | Rapporto account long/short di tutti gli account (Binance globalLongShortAccountRatio). |
| `k` | `string TopTraderLongShortRatio` | Rapporto POSIZIONI long/short dei top trader (Binance topLongShortPositionRatio). |
| `k` | `string TakerBuySellRatio` | Rapporto volume taker buy/sell (Binance takerlongshortRatio). |
| `k` | `string OpenInterest` | Open interest in contratti (Binance openInterestHist.sumOpenInterest). |
| `k` | `string OpenInterestValue` | Open interest in USDT (Binance openInterestHist.sumOpenInterestValue). |
| `k` | `string FundingRate` | Funding rate in percento (×100), come il resto della piattaforma. |
| `k` | `string LongLiquidationNotional` | Nozionale (USDT) dei LONG liquidati nell'ora. |
| `k` | `string ShortLiquidationNotional` | Nozionale (USDT) degli SHORT liquidati nell'ora. |
| `k` | `string LongLiquidationCount` | Numero di ordini di liquidazione LONG nell'ora. |
| `k` | `string ShortLiquidationCount` | Numero di ordini di liquidazione SHORT nell'ora. |

## `ProcioneMGR/Data/TrackedSeries.cs`

### 📦 `TrackedSeries`

> Una serie di mercato (Exchange + Symbol + Timeframe) che il sistema mantiene aggiornata automaticamente in background. E' una watchlist GLOBALE: i dati OHLCV non sono per-utente, quindi nemmeno la lista delle serie tracciate lo e'.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `ExchangeName Exchange` | — |
| `p` | `string Symbol` | Simbolo canonico "BASE/QUOTE", es. "BTC/USDT". |
| `p` | `string Timeframe` | Timeframe canonico, es. "1h". |
| `p` | `bool Enabled` | Se false, il worker la salta. |
| `p` | `DateTime? LastSyncUtc` | Ultima sincronizzazione riuscita (UTC), null se mai sincronizzata. |
| `p` | `string? LastSyncStatus` | Esito sintetico dell'ultima sincronizzazione (per la UI). |
| `p` | `DateTime CreatedAt` | — |

## `ProcioneMGR/Data/TradePostMortem.cs`

### 📦 `TradePostMortem`

> [G4] L'analisi a posteriori di UN'operazione chiusa in perdita (o del ritiro di una corsia). Tabella propria e non PipelineArtifact : quelli sono agganciati a un RunId che un trade non ha. E non il journal della flotta: un post-mortem non è una decisione dell'orchestratore, e piegare quello schema si sarebbe pagato dopo. Confine : questa riga è testo e una classificazione. Non entra in nessun percorso di esecuzione; l'unico consumatore oltre la pagina è il Context della domanda al comitato AF3 — che resta a menù chiuso, con quorum e default deterministico.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `DateTime CreatedAtUtc` | — |
| `p` | `int LaneId` | — |
| `p` | `int TradeRecordId` | Il trade analizzato ( TradeRecords.Id ). Indice unico: un trade, un post-mortem. |
| `p` | `string Symbol` | — |
| `p` | `string StrategyId` | — |
| `p` | `decimal PnlPercent` | Perdita percentuale del trade (negativa), copiata qui per poter interrogare senza join. |
| `p` | `string Cause` | Una voce di — MAI testo libero. È il solo campo che viaggia verso il comitato. |
| `p` | `string Source` | Chi ha scelto la causa: "rules" = calcolata dal codice (aritmetica, nessuna AI interpellata) \| "ai" = scelta dall'AI dentro il menù \| "default" = AI non disponibile o fuori menù. |
| `p` | `string FactsJson` | I fatti oggettivi su cui si è ragionato (JSON), per poter rileggere il verdetto fra un mese. |
| `p` | `string Narrative` | La prosa dell'AI. Vuota quando l'AI non ha risposto: la causa deterministica resta comunque. |
| `p` | `string ModelUsed` | Il modello che ha davvero risposto, vuoto se non è stata interpellata nessuna AI. |

## `ProcioneMGR/Data/UserPageConfig.cs`

### 📦 `UserPageConfig`

> Configurazione completa di una pagina (form di Backtest, Optimization, ...) salvata per utente: preset con nome oppure "ultima configurazione usata" (Name vuoto, aggiornata a ogni Run). Il contenuto è un JSON opaco definito dalla pagina stessa (ogni pagina ha il suo DTO).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `string UserId` | FK verso AspNetUsers. |
| `p` | `ApplicationUser? User` | — |
| `p` | `string PageKey` | Chiave stabile della pagina, es. "backtest", "optimization". |
| `p` | `string Name` | Nome del preset scelto dall'utente; stringa vuota = ultima configurazione usata. |
| `p` | `string ConfigJson` | Configurazione serializzata (JSON opaco, schema a carico della pagina). |
| `p` | `DateTime UpdatedAtUtc` | — |

# `Components/`

## `ProcioneMGR/Components/Account/IdentityComponentsEndpointRouteBuilderExtensions.cs`

### 📦 `IdentityComponentsEndpointRouteBuilderExtensions`

| | Firma | Descrizione |
|---|---|---|
| `m` | `IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)` | — |

## `ProcioneMGR/Components/Account/IdentityNoOpEmailSender.cs`

### 📦 `IdentityNoOpEmailSender` `: IEmailSender&lt;ApplicationUser&gt;`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)` | — |
| `m` | `Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)` | — |
| `m` | `Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)` | — |

## `ProcioneMGR/Components/Account/IdentityRedirectManager.cs`

### 📦 `IdentityRedirectManager` `(NavigationManager navigationManager)`

| | Firma | Descrizione |
|---|---|---|
| `k` | `string StatusCookieName` | — |
| `m` | `void RedirectTo(string? uri)` | — |
| `m` | `void RedirectTo(string uri, Dictionary&lt;string, object?&gt; queryParameters)` | — |
| `m` | `void RedirectToWithStatus(string uri, string message, HttpContext context)` | — |
| `m` | `void RedirectToCurrentPage()` | — |
| `m` | `void RedirectToCurrentPageWithStatus(string message, HttpContext context)` | — |
| `m` | `void RedirectToInvalidUser(UserManager&lt;ApplicationUser&gt; userManager, HttpContext context)` | — |

## `ProcioneMGR/Components/Account/IdentityRevalidatingAuthenticationStateProvider.cs`

### 📦 `IdentityRevalidatingAuthenticationStateProvider` `(`

| | Firma | Descrizione |
|---|---|---|
| `p` | `TimeSpan RevalidationInterval` | — |

## `ProcioneMGR/Components/Account/PasskeyInputModel.cs`

### 📦 `PasskeyInputModel`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string? CredentialJson` | — |
| `p` | `string? Error` | — |

## `ProcioneMGR/Components/Account/PasskeyOperation.cs`

### 🔢 `PasskeyOperation`

## `ProcioneMGR/Components/Layout/NavModel.cs`

### 🧾 `NavItem` `(`

> Una singola voce di menu. URL relativo, senza slash iniziale (es. "market/watchlist"). Stringa vuota = Home. Etichetta mostrata in italiano. Classe Bootstrap Icons (es. "bi-house-door-fill"). Frase breve usata come tooltip in sidebar e come testo secondario nella ricerca globale (Ctrl+K). Ruoli abilitati a vedere la voce. null = qualsiasi utente autenticato. Rispecchia 1:1 il gating AuthorizeView della vecchia NavMenu. true = match esatto della route (usato solo per Home).

### 🧾 `NavSection` `(`

> Un blocco della sidebar (Overview / Dati / Ricerca / Trading / Avanzati / Configurazione). Chiave stabile usata per persistere lo stato aperto/chiuso in localStorage. Titolo del blocco. Colore CSS del pallino (tono pastello sul tema scuro). true = il blocco può essere collassato dall'utente. Voci contenute.

### 📦 `NavModel`

> Modello di navigazione centralizzato: unica fonte di verità condivisa da NavMenu (sidebar), Breadcrumb (percorso contestuale) e CommandPalette (ricerca globale Ctrl+K). Organizzazione per workflow utente: Overview → Dati → Ricerca & Sviluppo → Trading → Strumenti Avanzati → Configurazione. Href e ruoli invariati.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;NavSection&gt; Sections` | Blocchi operativi. Account è a parte (renderizzato da NavMenu). |
| `p` | `IEnumerable&lt;string&gt; CollapsibleKeys` | Chiavi dei soli blocchi collassabili (usate per lo stato persistente). |
| `m` | `bool IsVisible(NavItem item, ClaimsPrincipal user)` | True se la voce è visibile all'utente indicato (stessa semantica del vecchio gating AuthorizeView). Condiviso fra NavMenu e CommandPalette. |
| `m` | `(string Section, string Page)? Resolve(string relativePath)` | Restituisce il breadcrumb (sezione, pagina) per una route relativa, oppure null se la route non è mappata (es. Home o pagine Account/Identity). |
| `m` | `string? SectionKeyOf(string relativePath)` | Chiave del blocco che contiene la route, o null . |

## `ProcioneMGR/Components/Shared/PollingTimer.cs`

### 📦 `PollingTimer` `: IAsyncDisposable, IDisposable`

> Timer di polling per il refresh periodico delle pagine Blazor Server. Sostituisce il pattern fragile new System.Threading.Timer(async _ =&gt; await ..., ...) , la cui lambda è async void : un'eccezione sfuggita al corpo non ha nessuno che la osservi e termina il PROCESSO (non solo il circuito). Qui il loop su avvolge OGNI tick in try/catch — un tick che lancia (es. DB irraggiungibile, circuito in chiusura) viene loggato e il polling prosegue al tick successivo. La protezione diventa struttura, non convenzione di pagina. Uso: creare in OnInitialized / OnAfterRender(firstRender) passando () =&gt; InvokeAsync(RefreshAsync) (il marshaling sul contesto del renderer resta a carico della pagina), e disporre in Dispose / DisposeAsync . Supporta entrambe le dispose così le pagine @implements IDisposable non devono cambiare contratto.

| | Firma | Descrizione |
|---|---|---|
| `c` | `PollingTimer(TimeSpan interval, Func&lt;Task&gt; onTickAsync, ILogger? logger = null)` | — |
| `m` | `void Dispose()` | Cleanup sincrono per le pagine @implements IDisposable : cancella e ferma il timer. Il loop osserva la cancellazione ed esce da sé — non lo si attende qui per non bloccare il thread del circuito. Un eventuale tick già i… |
| `m` | `ValueTask DisposeAsync()` | Cleanup asincrono per le pagine @implements IAsyncDisposable : come ma ATTENDE la fine del loop prima di rilasciare il . |

# `ProcioneMGR.Contracts/`

## `ProcioneMGR.Contracts/Grpc/SharedSecretClientInterceptor.cs`

### 📦 `SharedSecretClientInterceptor` `(string sharedSecret) : Interceptor`

> Interceptor client-side che aggiunge a ogni chiamata gRPC uscente un header con un segreto condiviso — controparte di SharedSecretAuthInterceptor lato server in ProcioneMGR.Trading . Vive qui (libreria condivisa referenziata da entrambi gli host) perché sia il monolite (client, quando Trading:UseRemoteTrading=true ) sia i test del servizio standalone devono poterlo costruire con lo stesso nome header/segreto. Non è un sostituto di mTLS: è un secondo fattore oltre alla NetworkPolicy K8s, a costo quasi zero, per il caso in cui il confine di rete da solo si riveli insufficiente (es. un `kubectl port-forward` che lo scavalca, documentato in infra/k8s/README.md).

| | Firma | Descrizione |
|---|---|---|
| `k` | `string HeaderName` | Nome dell'header sul filo — condiviso testualmente col controllo lato server. |

# `ProcioneMGR.Ingestion/`

## `ProcioneMGR.Ingestion/NoOpEncryptionService.cs`

### 📦 `NoOpEncryptionService` `: IEncryptionService`

> che lancia sempre eccezione. Serve solo a soddisfare la dipendenza del costruttore di ApplicationDbContext (l'EncryptedStringConverter è applicato alle colonne credenziali degli exchange, che il path di ingestione OHLCV non tocca MAI — i dati di mercato Bitget/Binance sono endpoint pubblici non firmati). Deliberatamente NON un passthrough silenzioso ( Encrypt(x) =&gt; x ): questo è un servizio long-running con un endpoint HTTP, non un tool CLI usa-e-getta. Se in futuro qualcuno aggiungesse per errore una query su ExchangeCredentials in questo processo, un passthrough scriverebbe credenziali IN CHIARO su colonne che tutto il resto del sistema tratta come cifrate — fallimento silenzioso e pericoloso. Lanciare trasforma quello scenario in un crash immediato. Conseguenza: a questo host non va distribuita NESSUNA master key.

| | Firma | Descrizione |
|---|---|---|
| `m` | `string Encrypt(string plaintext)` | — |
| `m` | `string Decrypt(string ciphertext)` | — |

## `ProcioneMGR.Ingestion/Program.cs`

### 📦 `Program` `;`
