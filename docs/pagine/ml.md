# ML Lab — `/ml`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/MlLab.razor`](../../ProcioneMGR/Components/Pages/MlLab.razor) (~645 righe) |
| **Route** | `/ml` |
| **Sezione navigazione** | Ricerca & Sviluppo |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Addestra un modello di **machine learning** a prevedere il rendimento futuro a partire dai
fattori alpha (momentum, RSI, MACD, volatilità…), con una **disciplina anti-imbroglio**
esplicita: il modello si allena SOLO sul periodo Train e viene testato SOLO sul periodo
Test successivo mai visto. Il flusso è numerato nella UI: **1. Dati e fattori →
2. Addestra → 3. Esegui backtest (out-of-sample)**, più il salvataggio del modello per il
riuso a valle.

Modelli disponibili (select alle righe 108–117):

| Modello | Note |
|---|---|
| Lineare (SDCA) | Semplice, veloce, spiegabile |
| Random Forest | Non lineare, ML.NET |
| Gradient Boosting | LightGBM |
| Rete neurale (MLP) | C# puro |
| **Stacking** | Ensemble di modelli base con 3 modalità di combinazione: media semplice, peso ∝ 1/RMSE, **meta-learner ridge su predizioni out-of-fold (purged CV)** — pesi senza leakage |
| **Attention / Transformer** | Self-attention su una finestra di T candele, C# puro (niente TorchSharp); parametri finestra ed embedding |

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 27–63 | Spiegazione di train/test, orizzonte, soglie, feature importance |
| Card "1. Dati e fattori" | 65–237 | Serie + modello + pannelli condizionali (Stacking righe 124–152, Attention 154–172) + selezione fattori |
| Fattori alpha | 176–187 | Checkbox dai prototipi di `IAlphaFactorFactory` (default: Momentum, RSI, MACD) |
| Fattori minati | 189–205 | I fattori salvati da [Alpha Mining](alpha-mining.md), con tooltip dell'espressione |
| `AdvancedPanel` | 207–218 | % Train (10–90) e orizzonte forward |
| Modelli salvati | 239–275 | Tabella con azioni: **Ottimizza →** (handoff a Optimization), Carica, Elimina |
| Modello addestrato | 277–352 | KPI train, **feature importance** (permutation: calo R² se il fattore viene confuso), soglie Long/Short, parametri backtest, salvataggio |
| Risultato backtest | 354–428 | KPI, **tearsheet** (Sharpe, Sortino, Calmar, Omega, VaR/CVaR 95%, durata max DD, esposizione), equity OOS, trade list |

## Come funziona (flusso del codice)

### Architettura
Pagina sottile su **`MlLabService`** (`Svc`): stato del form fotografato in
`MlConfigSnapshot` (righe 481–485, include fattori selezionati, configurazione
stacking/attention e parametri di backtest); risultati esposti dal service
(`SavedModels`, `SavedFactors`, `HasTrainedModel`, `FeatureImportance`, `Result`,
`Tearsheet`, `EquitySeries`).

### Addestra — `TrainAsync` (righe 534–562)
`Svc.TrainAsync(Snapshot(), userId)`: il service carica le candele, costruisce il dataset
(fattori → feature, rendimento forward → label) via `DatasetBuilder`, fa lo split
train/test temporale (il test è sempre la parte **più recente**), addestra il predictor
scelto dal `ReturnPredictorCatalog` e calcola correlazione in-sample + permutation
importance. Il training viene tracciato anche in [Esperimenti](experiments.md) via
`IExperimentTracker`.

### Backtest OOS — `BacktestAsync` (righe 564–583)
Simula le operazioni **solo sul periodo Test**: il modello predice il rendimento, e si apre
Long/Short solo se la predizione supera la soglia corrispondente. Produce anche il
tearsheet completo. È questo il risultato che conta, non la correlazione in-sample.

### Salva / Carica / Elimina modello (righe 585–626)
`SaveModelAsync` persiste il modello addestrato (tabella `MlModels`) per riusarlo senza
riaddestrare. `LoadSavedModelAsync` lo ricarica riallineando symbol/timeframe/tipo del form.
Il bottone **Ottimizza →** costruisce l'URL di handoff (`MlLabService.OptimizationHandoffUrl`)
verso [Optimization](optimization.md) con la strategia speciale "Modello ML": lì si
ottimizzano le soglie in walk-forward sui dati successivi al training.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `MlLabService` | Train, backtest OOS, tearsheet, salvataggio modelli, preset | [`Services/ML/MlLabService.cs`](../../ProcioneMGR/Services/ML/MlLabService.cs) |
| `IAlphaFactorFactory` | Prototipi dei fattori selezionabili | [`Services/Alpha/AlphaFactorFactory.cs`](../../ProcioneMGR/Services/Alpha/AlphaFactorFactory.cs) |
| `IDatasetBuilder` (via service) | Costruzione matrice feature/label con orizzonte forward | [`Services/ML/DatasetBuilder.cs`](../../ProcioneMGR/Services/ML/DatasetBuilder.cs) |
| `ReturnPredictorCatalog` | Mappa nome modello → predictor concreto; `BaseTypes` per lo stacking | [`Services/ML/ReturnPredictorCatalog.cs`](../../ProcioneMGR/Services/ML/ReturnPredictorCatalog.cs) |
| Predictor concreti | Linear (SDCA), RandomForest, GradientBoosting (LightGBM), MLP, Stacked, Attention | [`Services/ML/`](../../ProcioneMGR/Services/ML) |
| `PurgedTimeSeriesCv` | CV purgata per le predizioni out-of-fold dello stacking | [`Services/ML/PurgedTimeSeriesCv.cs`](../../ProcioneMGR/Services/ML/PurgedTimeSeriesCv.cs) |
| `MlStrategy` (via backtest) | La strategia che traduce predizioni + soglie in ordini | [`Services/Backtesting/MlStrategy.cs`](../../ProcioneMGR/Services/Backtesting/MlStrategy.cs) |
| `IExperimentTracker` (via service) | Tracciamento run in Esperimenti | [`Services/Experiments/ExperimentTracker.cs`](../../ProcioneMGR/Services/Experiments/ExperimentTracker.cs) |
| `AlphaExpressionFactor` | Valutazione dei fattori minati selezionati | [`Services/AlphaMining/AlphaExpressionFactor.cs`](../../ProcioneMGR/Services/AlphaMining/AlphaExpressionFactor.cs) |
| `ConfigPresets` / `DataAvailability` / `AdvancedPanel` / `Stat` | Componenti condivisi | [`Components/Shared/`](../../ProcioneMGR/Components/Shared) |

## Dati letti / scritti

- **Legge**: `OhlcvData`, `MlModels` (salvati), `SavedAlphaFactors` (fattori minati), `UserPageConfigs`.
- **Scrive**: `MlModels` (salvataggio/eliminazione), `ExperimentRuns` (tracking), `UserPageConfigs`.

## Collegamenti con le altre pagine

- [Feature Selection](feature-selection.md) — a monte: dice quali fattori valgono.
- [Alpha Mining](alpha-mining.md) — fornisce i "fattori minati" selezionabili qui.
- [Optimization](optimization.md) — "Ottimizza →" per le soglie del modello salvato.
- [Registry](registry.md) — i modelli salvati possono diventare Champion validati.
- [Esperimenti](experiments.md) — ogni training è tracciato lì.

## Note di design

- I modelli salvati sono legati a symbol+timeframe: l'Optimization mostra solo quelli
  compatibili con la serie corrente.
- Lo stacking "meta-learner ridge" usa out-of-fold con purged CV: i pesi dei modelli base
  sono stimati su predizioni mai viste in training (niente leakage).
- I modelli salvati da qui sono gli stessi caricati dal `MlModelLoader` condiviso usato dal
  TradingEngine (sentinella `MlChampion`), quindi la coerenza train/serve è centralizzata.
