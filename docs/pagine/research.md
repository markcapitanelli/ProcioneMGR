# Archivio candidati — `/research`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Research.razor`](../../ProcioneMGR/Components/Pages/Research.razor) (~300 righe) |
| **Route** | `/research` |
| **Sezione navigazione** | Ricerca & Sviluppo |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Rende **leggibile la caccia**: ogni run della pipeline archivia da luglio tutti i candidati
provati (artifact `ValidatedCandidates`, 24 metriche + motivo di scarto), ma ogni run era un blob
JSON isolato — nessuna domanda trasversale possibile (Filone R, 2026-08-06). Questa pagina
risponde a: *quali coppie hanno prodotto candidati vicini alla soglia? quale famiglia rende?
perché si scarta?* — con la **fascia grigia in evidenza** (bocciati per sola finestra corta o DSR
in [0,80–0,95), Sharpe holdout positivo: candidabili al forward test Paper, non promossi).

## Struttura della pagina

| Blocco | Contenuto |
|---|---|
| GuidaPanel | cos'è l'archivio, cos'è la fascia grigia, cosa fanno Rianalizza/Componi |
| KPI | candidati totali, run, promossi, grigi (con quota "per finestra corta"), scartati nel merito, periodo |
| Filtri | coppia, timeframe, verdetto (Promosso/Grigia/Scartato), famiglia, ricerca testo; bottone «Ricostruisci indice» |
| Resa per famiglia | provati / promossi / grigi / tasso per `StrategyName` |
| Motivi di scarto | categorie classificate (finestra corta, DSR, Sharpe, PBO, gemello, permutation) con Sharpe holdout medio |
| Tabella candidati | primi 200 per data run e Sharpe holdout; righe grigie evidenziate (`table-warning`); azioni per riga |

## Come funziona (flusso del codice)

- **Indice a righe (R2)**: la tabella `ResearchCandidates` è **derivata** dagli artifact
  (`ResearchCandidateIndexer`): incrementale a ogni apertura della pagina (i run già indicizzati
  non si ritoccano — indice unico `(RunId, CandidateKey)` come contratto di idempotenza),
  ricostruibile da zero col bottone. Un payload illeggibile esclude quel run con un log, mai
  l'intero giro. `IsGrey` è la cache del **giudice unico** `GreyZone.IsGrey` (lo stesso di
  FleetStateReader/GreyDeployer), riallineata a ogni rebuild.
- **Rianalizza**: handoff in query string verso [Optimization](optimization.md)
  (`symbol`+`timeframe`+`strategy`+`parameters`), nessuna scrittura.
- **Componi →**: porta in [Ensemble](ensemble.md), dove i candidati grigi della stessa
  coppia/timeframe della corsia compaiono come quinta fonte gamba.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `ResearchPageService` | Aggregati, filtri, handoff | [`Services/Research/ResearchPageService.cs`](../../ProcioneMGR/Services/Research/ResearchPageService.cs) |
| `IResearchCandidateIndexer` / `ResearchCandidateIndexer` | Costruzione/mantenimento dell'indice derivato | [`Services/Research/ResearchCandidateIndex.cs`](../../ProcioneMGR/Services/Research/ResearchCandidateIndex.cs) |
| `GreyZone` | IL giudice della fascia grigia (unico, condiviso con Fleet) | [`Services/Pipeline/GreyZone.cs`](../../ProcioneMGR/Services/Pipeline/GreyZone.cs) |

## Dati letti / scritti

- **Legge**: `ResearchCandidates` (indice), `PipelineArtifacts`+`PipelineRuns` (solo in indicizzazione).
- **Scrive**: solo l'indice derivato (mai i dati d'origine, mai il percorso di trading).

## Collegamenti con le altre pagine

- [Pipeline](pipeline.md) — la fonte dei run; [Optimization](optimization.md) — destinazione di
  «Rianalizza»; [Ensemble](ensemble.md) — destinazione di «Componi» (fonte gamba «Da fascia grigia»);
  [/admin/autonomy](admin-autonomy.md) — il click umano F5 del GreyDeployer usa la stessa definizione di grigio.

## Note di design

- La fascia grigia NON è una promozione: ovunque compaia porta il badge e la spiegazione. È la
  classe di difetto «controlli che rassicurano» (Filone E) applicata al contrario — qui si evita
  che un quasi-promosso si travesta da promosso.
- Nessuna nuova sezione di configurazione: la pagina non legge `IConfiguration`
  (`ConfigurationUiCoverageTests` resta intatto) e non scandisce simboli da `OhlcvData`
  (`SymbolScanGuardTests` idem).
