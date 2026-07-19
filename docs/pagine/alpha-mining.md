# Alpha Mining — `/alpha-mining`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/AlphaMining.razor`](../../ProcioneMGR/Components/Pages/AlphaMining.razor) (~375 righe) |
| **Route** | `/alpha-mining` |
| **Sezione navigazione** | Strumenti Avanzati |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Cerca **NUOVE formule**, non combinazioni di fattori esistenti: un algoritmo **genetico**
evolve alberi di espressione su prezzo/volume massimizzando l'**Information Coefficient**
sul periodo di selezione, con penalità di complessità contro l'overfitting
(rif. ROADMAP-QLIB §1.7).

Disciplina non negoziabile (dal `GuidaPanel`): le formule si scelgono SOLO sulla selezione,
ma il **verdetto è sull'holdout mai visto**. Una formula con IC alto in selezione ma
nullo/opposto sull'holdout è overfit. In più la pagina calcola il **PBO** (Probability of
Backtest Overfitting, via CSCV) sulla scelta della formula migliore.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 22–34 | Metodo e disciplina selection/holdout |
| Form | 36–112 | Symbol, timeframe, periodi Selezione e Holdout separati, `DataAvailability` |
| `AdvancedPanel` | 76–99 | Iperparametri del miner: orizzonte, popolazione (default 150), generazioni (12), profondità max (5), seed (42) |
| Banner PBO | 116–125 | Verde se PBO < 50% ("la scelta regge fuori campione"), giallo altrimenti ("fidati solo delle sopravvissute") |
| Formule trovate | 126–168 | Espressione, IC selezione, IC holdout, numero nodi, esito **Sopravvissuto** / Overfit-debole, bottone Salva |
| Fattori salvati | 170–192 | Gli ultimi 50 fattori salvati dell'utente con i loro IC |

## Come funziona (flusso del codice)

### Mining — `RunAsync` (righe 278–340)
1. Carica separatamente candele di **selezione** (minimo 120) e di **holdout**.
2. Costruisce `MiningConfig` con clamp difensivi (popolazione 20–1000, generazioni 1–100,
   profondità 2–8) e `TopN = 20`.
3. `Miner.Mine(selection, config)` in `Task.Run` — l'evoluzione genetica è CPU-bound.
4. Per ogni formula trovata, se l'holdout ha ≥60 candele: riparsa l'espressione
   (`AlphaExpressionParser.Parse`) e calcola l'**IC sull'holdout** (`Miner.EvaluateIc`).
5. `Miner.ComputeSelectionPbo` sulle stesse candele di selezione: quanto la scelta della
   formula migliore è guidata dall'overfitting (CSCV — Combinatorially Symmetric Cross
   Validation).
6. Registra il run negli Esperimenti (formule, sopravvissute, best IC, PBO).

### Criterio di sopravvivenza — `Survived` (righe 232–233)
`|IC holdout| > 0.02` **e stesso segno** dell'IC di selezione: la formula deve informare
fuori campione nella stessa direzione.

### Salvataggio — `SaveAsync` (righe 342–367)
Persiste in `SavedFactors` (nome auto `mined_<timestamp>`, espressione, symbol/TF,
orizzonte, IC selezione/holdout, osservazioni, dimensione). I fattori salvati compaiono in
[ML Lab](ml.md) nella sezione "Fattori minati" come feature selezionabili.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `GeneticAlphaMiner` | Evoluzione genetica, valutazione IC, PBO di selezione | [`Services/AlphaMining/GeneticAlphaMiner.cs`](../../ProcioneMGR/Services/AlphaMining/GeneticAlphaMiner.cs) |
| `AlphaExpressionParser` / `AlphaNode` | Parsing/AST delle espressioni evolute | [`Services/AlphaMining/AlphaExpressionParser.cs`](../../ProcioneMGR/Services/AlphaMining/AlphaExpressionParser.cs) |
| `AlphaExpressionFactor` | Adapter: espressione salvata → fattore usabile come feature ML | [`Services/AlphaMining/AlphaExpressionFactor.cs`](../../ProcioneMGR/Services/AlphaMining/AlphaExpressionFactor.cs) |
| `CombinatorialPurgedCv` / PBO | La validazione CSCV dietro `ComputeSelectionPbo` | [`Services/Validation/CombinatorialPurgedCv.cs`](../../ProcioneMGR/Services/Validation/CombinatorialPurgedCv.cs) · [`BacktestOverfitting.cs`](../../ProcioneMGR/Services/Validation/BacktestOverfitting.cs) |
| `IExperimentTracker` | Registrazione del run | [`Services/Experiments/ExperimentTracker.cs`](../../ProcioneMGR/Services/Experiments/ExperimentTracker.cs) |

## Dati letti / scritti

- **Legge**: `OhlcvData` (selezione + holdout), `SavedFactors` (i propri).
- **Scrive**: `SavedFactors`, `ExperimentRuns`, `UserPageConfigs`.

## Collegamenti con le altre pagine

- [ML Lab](ml.md) — i fattori salvati diventano feature selezionabili ("Fattori minati").
- [Feature Selection](feature-selection.md) — il vaglio IC dei fattori, minati inclusi.
- [Esperimenti](experiments.md) — storico dei run di mining.

## Note di design

- Selezione e holdout hanno **date esplicitamente separate** nel form: la separazione non è
  un dettaglio implementativo ma parte dell'interfaccia.
- Deterministico a parità di seed: le campagne sono riproducibili.
- La soglia di sopravvivenza (0.02) è volutamente severa insieme al requisito di segno
  concorde: meglio pochi fattori robusti che tanti rumorosi.
