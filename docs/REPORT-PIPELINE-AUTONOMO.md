# Report — Autonomous Research & Strategy Pipeline (2026-07-02)

Implementazione completa del pipeline autonomo richiesto: un orchestratore end-to-end che
concatena tutti i moduli esistenti della piattaforma (ingestione → fattori → regime →
discovery → holdout → robustezza → ensemble → risk → raccomandazione → piano), con UI
dedicata `/pipeline`, persistenza su DB, checkpoint/resume, e regole decisionali
deterministiche esterne al codice.

**Stato: funzionante e verificato** — build 0 errori, **396/396 test xUnit** (371 preesistenti
+ 25 nuovi, zero regressioni), run end-to-end reale eseguito dal browser su BTC/USDT +
ETH/USDT 1h (15 fasi in 9 secondi, esito onesto: 0 sopravvissuti all'holdout → "NON operare").

---

## 1. File creati

### Servizi (`ProcioneMGR/Services/Pipeline/`)
| File | Contenuto |
|---|---|
| `PipelineModels.cs` | `PipelineContext` (stato serializzabile del run), `StageConfig` + getter tipizzati, DTO di output di ogni fase, `StageSummary`, `PipelineLiveStatus`, `SeriesSpec`, `PipelineDateRanges` |
| `IPipelineStage.cs` | Contratto delle fasi: Name/Dependencies/ParameterDefinitions/ValidateInput/ExecuteAsync/Summarize |
| `IPipelineEngine.cs` | Contratto engine (Start/Resume/Pause/Cancel/LiveStatus/Validate) + `IPipelineStageCatalog` |
| `PipelineEntities.cs` | Entità EF: `PipelineConfiguration`, `PipelineRun`, `PipelineArtifact` |
| `PipelineEngine.cs` | Esecutore singleton: background run, checkpoint per fase, pause/cancel/resume, audit |
| `PipelineStageCatalog.cs` | Catalogo dei 15 stage (prototipi per la UI, factory DI per l'engine, default) |
| `PipelineDagValidator.cs` | Validazione pura del DAG delle dipendenze (testabile direttamente) |
| `PipelineCandleCache.cs` | Cache per-run delle candele (una lettura DB per finestra) |
| `PipelineRules.cs` | `PipelineRuleSet` + provider che legge/materializza `Config/pipeline_rules.json` |
| `Stages/DataStages.cs` | Fase 1 DataIngestion (solo delta mancanti), fase 2 AltDataSync |
| `Stages/AnalysisStages.cs` | Fasi 3-6: FeatureEngineering (IC top-K), RegimeAnalysis, VolatilityRegime (GARCH), PairsScreening |
| `Stages/ModelStages.cs` | Fasi 7-10: MlModelTraining (split temporale con purge), StrategyDiscovery (gate anti-rumore), HoldoutValidation (verdetto), RobustnessProbe (MC + stop + Kelly) |
| `Stages/DecisionStages.cs` | Fasi 11-15: EnsembleAssembly (HRP + bias regime), RiskSizing, NewsImpactCheck, Recommendation, ExecutionPlan |

### UI, dati, test
- `Components/Pages/Pipeline.razor` — le 4 sezioni richieste + GuidaPanel
- `Data/Migrations/20260702175926_AddAutonomousPipeline` — 3 tabelle nuove (applicata)
- `ProcioneMGR.Tests/PipelineTests.cs` — 25 test (dettaglio in §4)
- `Config/pipeline_rules.json` — generato automaticamente al primo uso con i default

### File modificati (minimi)
- `Data/ApplicationDbContext.cs` — 3 DbSet + Fluent API (pattern esistente)
- `Program.cs` — 3 registrazioni DI (rules provider, catalog, engine)
- `Components/Layout/NavMenu.razor` — link "Pipeline" nella sezione Amministrazione
- `Services/Pipeline/Stages/ModelStages.cs`: `ProfitFactor`/`ApplyVariant` resi `public` per i test (stesso trattamento di `OptimizationEngine.ComboKey`)

**Nessun modulo esistente è stato modificato**: il pipeline è un orchestratore puro.

---

## 2. Decisioni architetturali

1. **Checkpoint senza candele.** Il `PipelineContext` è serializzato su `PipelineRun.ContextSnapshotJson`
   dopo OGNI fase, ma le candele NON ne fanno parte (dominerebbero lo snapshot): vivono nel DB
   (fonte di verità) e un run ripreso le ricarica on-demand tramite `PipelineCandleCache`.
   Conseguenza: resume robusto e snapshot piccoli.

2. **ValidateInput fallito = fase SALTATA, non run fallito.** Se una fase non ha i prerequisiti
   (es. 0 candidati da validare), viene marcata `Skipped` col motivo e il run continua: la
   raccomandazione finale resta onesta ("0 sopravvissuti → NON operare"). Solo un'eccezione
   dentro `ExecuteAsync` fa fallire il run. Verificato live: con 0 sopravvissuti le fasi 10-12
   sono state saltate correttamente e la 14 ha prodotto la conclusione giusta.

3. **Disciplina selection/holdout ereditata dal Report Caccia.** Ogni scelta (fattori, parametri,
   variante di stop, pesi) usa SOLO il range di selezione; l'holdout è verdetto-only. Il
   salvataggio della config rifiuta holdout sovrapposti alla selezione. Il training ML usa uno
   split temporale con **purge gap** di `forwardHorizon` righe al confine train/test (la
   versione essenziale dell'embargo di `PurgedTimeSeriesCv`).

4. **Il modello ML è un candidato come gli altri.** `MlModelTrainingStage` salva il predictor
   come `SavedMlModel` (riuso totale del pattern ML Lab) e lo registra come candidato
   `StrategyName="Ml"` SOLO se la correlazione sul test temporale supera una soglia; poi
   `HoldoutValidationStage` lo giudica con gli stessi gate delle strategie a regole.

5. **Pesi ensemble: HRP sui rendimenti giornalieri delle gambe** (riuso di
   `HierarchicalRiskParityOptimizer`), con fallback equal-weight se <2 gambe o storico
   allineato insufficiente; poi bias di regime dalle regole (moltiplicatore sulle famiglie
   mean-reversion/trend a seconda dell'etichetta corrente) e rinormalizzazione.

6. **Progresso via polling, non hub SignalR dedicato** (deviazione dichiarata, vedi §3).

7. **Un run alla volta** (engine singleton con lock): i motori sottostanti sono CPU-heavy e
   il vincolo rende banale il live-status; il tentativo di secondo run fallisce con errore chiaro.

8. **Safety invariata.** `ExecutionPlanStage` produce solo un PIANO. "Applica al Trading"
   precompila `EnsembleConfiguration` (l'utente conferma e avvia da /ensemble e /trading);
   in Live restano SafetyChecker + conferma manuale per-ordine. Il pipeline non piazza mai
   ordini da solo, in nessuna modalità.

---

## 3. Deviazioni dal prompt (con motivazione)

| Richiesta | Cosa ho fatto | Perché |
|---|---|---|
| Log in tempo reale "via SignalR" | Polling a 2s del live-status del singleton (ring buffer 60 righe) | È lo "stesso pattern già usato altrove" citato dal prompt: /trading, /ensemble ecc. fanno così; Blazor Server streama già la UI su SignalR, un hub dedicato aggiunge parti mobili senza capacità nuove |
| Fasi drag-and-drop | Riordino con pulsanti ↑/↓ | Drag-and-drop in Blazor senza librerie JS terze è fragile; ↑/↓ è accessibile e testabile. Nessuna perdita funzionale |
| Export "PDF/Markdown" | Solo Markdown (data-URL, download immediato) | Il PDF richiede una dipendenza nativa nuova (es. QuestPDF); Markdown copre l'uso reale (condivisione/archiviazione). Estendibile |
| `Schedule` cron-like attivo | Campo persistito ma nessuno scheduler in esecuzione | Avviare run pesanti in autonomia senza che l'utente l'abbia mai visto girare è prematuro; il campo c'è, un worker analogo a `MarketDataSyncWorker` è il passo naturale quando servirà |
| Pesatura "EnsembleManager" per le gambe | `HierarchicalRiskParityOptimizer` direttamente | `EnsembleManager` pesa strategie sulla STESSA coppia con simulazione storica propria; le gambe del pipeline possono essere su coppie diverse → HRP sui rendimenti delle equity per-gamba è lo strumento giusto già esistente |
| `PipelineArtifact.Payload byte[]` | `PayloadJson string` | Tutti gli artefatti reali sono JSON; il blob binario non ha un caso d'uso oggi |
| "Applica al Trading" con gambe multi-coppia | Applica la prima coppia + gambe corrispondenti, segnala le altre | `EnsembleConfiguration` è single-symbol per design (vincolo esistente della piattaforma, non del pipeline); il messaggio dice esplicitamente quante gambe restano da configurare a mano |

---

## 4. Test (25 nuovi, tutti verdi; 396/396 totali)

- **`StageConfigExtensionsTests`** — parsing invariant-culture dei parametri con fallback.
- **`PipelineDagValidatorTests`** (6) — catena valida, dipendenza mancante, gruppo any-of
  soddisfatto da una sola alternativa, dipendenza ordinata DOPO (non basta che esista),
  stage sconosciuto, nessuno stage abilitato.
- **`PipelineHelperTests`** — varianti stop (SL3/SL5/TRAIL5/base) e profit factor.
- **`PipelineContextSnapshotTests`** — il checkpoint JSON round-trippa senza perdita.
- **`RecommendationStageTests`** (3) — template con i numeri giusti, "NON operare" con 0
  sopravvissuti, determinismo (stesso contesto → stesso testo).
- **`RiskSizingStageTests`** (2) — Kelly frazionario con cap, riduzione sizing in alta volatilità.
- **`ExecutionPlanStageTests`** (2) — Live non auto-esegue mai e avvisa su SafetyChecker; Paper genera le azioni.
- **`PipelineAnalysisStagesTests`** (4) — su dati sintetici seedati (no DB/rete):
  FeatureEngineering valuta tutti i fattori ed è deterministico; GARCH classifica; PairsScreening
  testa ogni coppia una volta e due random walk indipendenti risultano NON cointegrati;
  ValidateInput richiede 2 simboli.

## 5. Verifica browser (run reale)

Config "Smoke test BTC+ETH 1h" creata dalla UI (universo 2 serie, selezione 2024-07→2026-03,
holdout 2026-03→2026-07, sync di rete disattivate perché i dati sono già nel DB), salvata con
validazione DAG, eseguita: **15 fasi in 9 secondi**, tutte le sezioni verificate (storico,
dettaglio con timeline fasi, card raccomandazione con badge, export markdown, "Applica al
Trading" correttamente disabilitato con 0 gambe). Zero errori server e console (solo i noti
timeout WebAuthn della pagina di login). Risultati del run reale:
- 8 fattori valutati, selezionati MeanReversion/RsiFactor/Momentum/DistanceFromMa;
- regime corrente **Sideways** (modello attivo riusato, silhouette 0.402);
- GARCH: vol Media (0.466%→0.533%, persistenza 0.975);
- BTC/ETH non cointegrati nel periodo (ADF -2.19) — coerente col test storico di /pairs-trading;
- RF su 10.906 righe: correlazione test **-0.0013** → salvato ma NON candidato (sotto soglia) — il gate anti-illusione funziona;
- Discovery: 3 candidati oltre i gate (miglior OOS Sharpe 2.14) ma **0 sopravvissuti all'holdout** — coerente con le campagne precedenti su BTC/ETH 1h;
- Raccomandazione finale: "NON operare", con 5 alert news CentralBanks (impatto storico +2.84% a 24h su 37 osservazioni).

Il run di smoke test e la sua configurazione sono stati lasciati nel DB come esempio funzionante.

## 6. Prossimi passi consigliati

1. **Scheduler**: worker `PipelineScheduleWorker` (pattern `MarketDataSyncWorker`) che interpreta il campo `Schedule` — la persistenza c'è già.
2. **Universo più ampio nel primo run reale**: 18 coppie × 15m/1h/4h come nelle campagne manuali; il pipeline è lo stesso harness, ora ripetibile con un click.
3. **Confronto run automatico**: la sezione "Confronto col run precedente" c'è; si può aggiungere un alert automatico se i sopravvissuti calano tra run consecutivi (degrado del sistema).
4. **PDF export** se serve la condivisione formale (QuestPDF dietro lo stesso pulsante).
5. **Leva nel pipeline**: aggiungere `LeverageAdvisor` come fase 12-bis per suggerire la leva per gamba (il servizio esiste già, è un wrapper sottile).
