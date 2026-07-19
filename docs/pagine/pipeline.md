# Pipeline — `/pipeline`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Pipeline.razor`](../../ProcioneMGR/Components/Pages/Pipeline.razor) (~705 righe) |
| **Route** | `/pipeline` |
| **Sezione navigazione** | Strumenti Avanzati |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer`, implementa `IDisposable` (polling 2s) |

## A cosa serve

Il pipeline **automatizza l'intero flusso di ricerca**: scarica i dati, valuta i fattori,
identifica il regime, cerca strategie in walk-forward, le valida su un **holdout mai
visto**, ne misura la robustezza (Monte Carlo), le combina in un ensemble pesato e produce
una **raccomandazione operativa** con limiti di rischio.

Principi cardine (dal `GuidaPanel`, righe 29–73):
- ***Le regole propongono, i backtest dispongono*** — ogni conclusione è supportata da numeri;
  le soglie decisionali sono in `Config/pipeline_rules.json` (deterministiche, niente LLM
  nel percorso decisionale).
- **Selection vs Holdout** — tutte le decisioni usano SOLO il periodo di selezione;
  l'holdout è il verdetto finale.
- **"Applica al Trading" è sempre manuale** — il pipeline non opera mai da solo.
- **Un run schedulato non esegue MAI in Live** — se la config è Live viene saltato con
  avviso, non declassato di nascosto.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| Run in corso | 81–108 | Badge per fase (colore = stato), log recente in tempo reale, Pausa (al prossimo checkpoint) / Annulla |
| Configurazioni salvate | 111–148 | Le "ricette": nome, universo, modalità (Paper/Live/Disabled), stato schedulazione (con prossima esecuzione), azioni Esegui/Modifica/Clona/Elimina |
| Editor config | 151–316 | Nome/descrizione/exchange/capitale/seed/modalità; **schedulazione cron** (5 campi UTC, anteprima della prossima esecuzione, warning se Live); date Selection/Holdout; universo (serie symbol+TF); **fasi ordinabili** con ↑↓, checkbox attiva, ingranaggio per i parametri, dipendenze mostrate; problemi di validazione |
| Ultima raccomandazione | 319–339 | Badge regime/volatilità/**sentiment**/sopravvissuti, testo completo, **Applica al Trading**, Esporta report (markdown) |
| Storico run | 342–384 | Filtro per trigger (Manuale ✋ / Schedulato 🕒 / Campagna 🎯 / Event ⚡), durata, stato, Dettaglio, **↻ Riprendi** per run Paused/Failed/Cancelled |
| Dettaglio run | 387–495 | Riepilogo esecutivo, errore, **giudizio del supervisore AI & decisione di ri-applica** (veto/nessuna obiezione, suggerimenti, preoccupazioni, ragionamento), timeline delle fasi, **confronto col run precedente** (Δ metriche) |

## Come funziona (flusso del codice)

### Architettura (righe 498–521)
Stato applicativo in **`PipelinePageService`** con alias read-only; la bozza dell'editor
(`PipelineConfigDraft`) resta nel componente perché è stato di form. Polling da 2s:
`Svc.RefreshLive()` segue il run in corso e, quando finisce, ricarica tutto.

### Configurazioni e fasi
Le fasi disponibili vengono da `IPipelineStageCatalog.Prototypes` (nome, descrizione,
dipendenze, parametri con default). Il salvataggio valida il DAG delle dipendenze
(`PipelineDagValidator`) e riporta i problemi nell'editor. Le fasi concrete stanno in
[`Services/Pipeline/Stages/`](../../ProcioneMGR/Services/Pipeline/Stages): dati (sync/ingestione),
analisi (fattori, regime, volatilità, **sentiment**), ricerca (walk-forward, **scoperta
creativa** — generazione deterministica di strategie composite/event/regime col seed della
config), modelli ML, decisione (holdout, Monte Carlo, ensemble, raccomandazione).

### Esecuzione, pausa, ripresa
`StartRunAsync` avvia il run nel `PipelineEngine` (motore **a slot singolo**: un solo run
alla volta; il bottone Esegui è disabilitato se ce n'è uno in corso). La pausa si applica al
prossimo checkpoint di fase; un run Paused/Failed/Cancelled **riprende dalla fase successiva
all'ultima completata** (verificato dal vivo nel PRD Autonomia, requisito C1: una caccia
interrotta si è auto-ripresa e completata da sola).

### Schedulazione (righe 189–210, 650–682)
Cron standard a 5 campi in UTC, interpretato da `PipelineSchedulerWorker.ComputeNextRun`
(stessa funzione usata per l'anteprima nell'editor: nessuna divergenza tra preview e
runtime). Se un run è già in corso, quello schedulato riparte al giro successivo (max 5
minuti) invece di saltare. Config in Live = run schedulato saltato con avviso.

### Raccomandazione e ri-applica
La raccomandazione finale include regime, volatilità, sentiment, sopravvissuti
all'holdout, gambe ensemble proposte con SL/TP e limiti di rischio. Due strade:
- **Manuale**: "Applica al Trading" (`PipelineApplier` via service) precompila l'ensemble
  della corsia in [/ensemble](ensemble.md) — l'avvio resta all'utente.
- **Automatica** (se abilitata in [Autonomia](admin-autonomy.md)): l'`EnsembleComparator`
  con isteresi decide se sostituire l'ensemble corrente; il **supervisore AI** (Logging o
  Claude) ha potere di **solo veto**. La decisione (`AutoReapplyDecisionArtifact`) è
  mostrata nel dettaglio run con il verdetto, i suggerimenti e il ragionamento; se il
  provider è `Logging`, la UI spiega come attivare Claude (`PipelineSupervisor:Provider` +
  `ANTHROPIC_API_KEY`).

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `PipelinePageService` | Stato pagina, CRUD config, controllo run, apply/export | [`Services/Pipeline/PipelinePageService.cs`](../../ProcioneMGR/Services/Pipeline/PipelinePageService.cs) |
| `IPipelineEngine` / `PipelineEngine` | Il motore a fasi con checkpoint/resume | [`Services/Pipeline/PipelineEngine.cs`](../../ProcioneMGR/Services/Pipeline/PipelineEngine.cs) |
| `IPipelineStageCatalog` + Stages | Catalogo e implementazioni delle fasi | [`Services/Pipeline/PipelineStageCatalog.cs`](../../ProcioneMGR/Services/Pipeline/PipelineStageCatalog.cs) · [`Stages/`](../../ProcioneMGR/Services/Pipeline/Stages) |
| `IPipelineRulesProvider` | Le soglie decisionali da `Config/pipeline_rules.json` | [`Services/Pipeline/PipelineRules.cs`](../../ProcioneMGR/Services/Pipeline/PipelineRules.cs) |
| `PipelineDagValidator` | Validazione dipendenze tra fasi | [`Services/Pipeline/PipelineDagValidator.cs`](../../ProcioneMGR/Services/Pipeline/PipelineDagValidator.cs) |
| `PipelineSchedulerWorker` | Schedulazione cron in background | [`Services/Pipeline/PipelineSchedulerWorker.cs`](../../ProcioneMGR/Services/Pipeline/PipelineSchedulerWorker.cs) |
| `RegimeChangeTriggerWorker` | Trigger "Event": run al cambio regime | [`Services/Pipeline/RegimeChangeTriggerWorker.cs`](../../ProcioneMGR/Services/Pipeline/RegimeChangeTriggerWorker.cs) |
| `PipelineApplier` | Trasforma la raccomandazione in configurazione ensemble | [`Services/Pipeline/PipelineApplier.cs`](../../ProcioneMGR/Services/Pipeline/PipelineApplier.cs) |
| `RunApplyEvaluator` / `EnsembleComparator` | Decisione di ri-applica automatica con isteresi | [`Services/Pipeline/RunApplyEvaluator.cs`](../../ProcioneMGR/Services/Pipeline/RunApplyEvaluator.cs) · [`Services/Ensemble/EnsembleComparator.cs`](../../ProcioneMGR/Services/Ensemble/EnsembleComparator.cs) |
| `IPipelineSupervisorAgent` | Veto-only: Logging o Claude | [`Services/Agents/`](../../ProcioneMGR/Services/Agents) |
| `CreativeDiscoveryStage` | La fase di scoperta creativa | [`Services/Pipeline/Stages/CreativeDiscoveryStage.cs`](../../ProcioneMGR/Services/Pipeline/Stages/CreativeDiscoveryStage.cs) |

## Dati letti / scritti

- **Legge**: `PipelineConfigurations`, `PipelineRuns`, `PipelineArtifacts` (raccomandazioni,
  decisioni, giudizi supervisore).
- **Scrive**: configurazioni (CRUD), avvio/pausa/annulla/riprendi run, applicazione
  raccomandazione → configurazione ensemble della corsia.

## Collegamenti con le altre pagine

- [Ensemble](ensemble.md) — destinazione di "Applica al Trading".
- [Campagne](campaign.md) — il planner che decide QUALE config lanciare dopo un run (trigger 🎯).
- [Autonomia](admin-autonomy.md) — gli interruttori di auto-reapply e supervisore.
- [Supervisione AI](admin-ai-supervisor.md) — l'advisory layer che legge i run completati.
- [Sentiment](sentiment.md) — lo snapshot di mood che entra nel run e nella raccomandazione.

## Note di design

- Il determinismo è un requisito: stesse regole + stesso seed → stessa conclusione; l'AI può
  solo porre veto, mai decidere in positivo.
- Il confronto col run precedente (Δ metriche) rende visibile il decadimento tra cacce
  successive senza aprire due dettagli.
- L'export report è un markdown scaricabile: il run è documentabile fuori dalla piattaforma.
