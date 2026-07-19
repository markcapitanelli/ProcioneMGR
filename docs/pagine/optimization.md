# Optimization — `/optimization`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Optimization.razor`](../../ProcioneMGR/Components/Pages/Optimization.razor) (~585 righe) |
| **Route** | `/optimization` (accetta query string di handoff da Backtest e ML Lab) |
| **Sezione navigazione** | Ricerca & Sviluppo |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer`, implementa `IAsyncDisposable` |

## A cosa serve

Ottimizza i parametri di una strategia **senza illudersi**: prova le combinazioni (Grid
Search esaustiva o ricerca Bayesiana guidata) dentro un'analisi **walk-forward** — si
ottimizza su una finestra in-sample e si verifica SOLO sulla finestra successiva mai vista
(out-of-sample). Il numero che conta è lo **Sharpe out-of-sample**, e il verdetto finale è
il **Deflated Sharpe Ratio**, che corregge per il numero di combinazioni provate
(selection bias / test multiplo).

Caso speciale: la strategia "**Modello ML (salvato)**" usa un modello già addestrato in
[ML Lab](ml.md) — qui si ottimizzano solo le sue soglie di decisione, non il modello.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 25–77 | Spiegazione di overfitting, walk-forward, heatmap, Top 10, Save Best |
| ConfigPresets | 82–84 | Preset + ultima configurazione (`PageKey="optimization"`); auto-restore disattivato in caso di handoff |
| Form serie | 85–114 | Exchange, symbol, timeframe, periodo complessivo |
| `DataAvailability` | 116–118 | Gating dello Start sui dati disponibili |
| Strategia | 121–129 | Prototipi + voce speciale "Modello ML (salvato)" |
| Selettore modello ML | 131–153 | Visibile solo per strategia ML: modelli compatibili con symbol+timeframe correnti |
| Metodo di ricerca | 155–191 | Grid Search o **Bayesian** (iterazioni guidate, punti iniziali random, seme; badge con budget per finestra) |
| Range parametri | 193–220 | Min/Max/Step/Intero per parametro; per Bayesian i range sono i **confini dello spazio**, non un prodotto cartesiano; conteggio combinazioni totali |
| `AdvancedPanel` | 224–247 | Capitale, commissione, finestre walk-forward IS/OOS/Step in mesi |
| Start/Stop + progress | 249–272 | Progress bar con "best Sharpe finora" |
| Verdetto Deflated Sharpe | 278–291 | Verde = edge difendibile dopo correzione per test multiplo; giallo = probabile artefatto della ricerca |
| Top 10 | 292–336 | Combinazioni per Sharpe OOS medio, con handoff "Backtest →" per riga e footer **Save Best Configuration** (+ testimone "Aggiungila all'Ensemble →") |
| Heatmap | 338–344 | Solo con esattamente 2 parametri: mappa dello Sharpe OOS (zona buona continua = robusta, macchie isolate = fortuna) |
| Equity walk-forward | 346–354 | Capitale con i soli segmenti out-of-sample concatenati: la stima più onesta del "dal vivo" |
| Finestre walk-forward | 356–377 | Dettaglio per finestra: best params, Sharpe IS vs OOS (divario ampio = overfitting locale) |

## Come funziona (flusso del codice)

### Architettura
Come il Backtest, la pagina è sottile e delega a **`OptimizationPageService`**: snapshot
immutabile `OptimizationConfigSnapshot` (righe 431–436) per run/preset/handoff, risultati
esposti dal service (`Result`, `ResultConfig`, `EquitySeries`, `MlModels`, `KnownSymbols`).

### Handoff in ingresso (righe 384–391, 494–498)
Query string da Backtest ("Ottimizza questa strategia →") o da ML Lab ("Ottimizza" su un
modello): `Svc.ApplyHandoff` precompila simbolo/periodo/strategia, **ricentra i range sui
parametri del run di provenienza** e preseleziona il modello ML (`?model=`). Valori
malformati lasciano i default.

### Run — `StartAsync` (righe 514–531)
`Svc.RunAsync(Snapshot(), userId, progress, token)` con progress bar (`OptimizationProgress`:
combinazioni testate/totali + best Sharpe corrente). Al termine, se i parametri sono
esattamente 2, viene programmato il rendering della heatmap (`_heatmapPending` →
`OnAfterRenderAsync` → modulo JS `heatmap.js`).

### Metodo Bayesian (righe 164–191)
Gaussian Process + Expected Improvement: propone i punti da provare invece del prodotto
cartesiano. Deterministico a parità di seme. Lo Sharpe guida la ricerca; il **Deflated
Sharpe resta il verdetto finale su tutti i punti visitati** (la correzione per test multiplo
usa il numero di valutazioni effettive).

### Salvataggio — `SaveBestAsync` (righe 555–566)
`Svc.SaveBestAsync` persiste la combinazione migliore in `SavedStrategies` taggata come
"ottimizzata"; il flag `_savedOk` fa comparire il bottone di prosecuzione del workflow
"Aggiungila all'Ensemble →".

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `OptimizationPageService` | Run walk-forward, handoff, preset, heatmap, salvataggio | [`Services/Optimization/OptimizationPageService.cs`](../../ProcioneMGR/Services/Optimization/OptimizationPageService.cs) |
| `IOptimizationEngine` (via service) | Grid search + walk-forward engine | [`Services/Optimization/OptimizationEngine.cs`](../../ProcioneMGR/Services/Optimization/OptimizationEngine.cs) |
| `BayesianOptimizationEngine` | Ricerca guidata GP+EI (con kernel a Log Marginal Likelihood) | [`Services/Optimization/Bayesian/BayesianOptimizationEngine.cs`](../../ProcioneMGR/Services/Optimization/Bayesian/BayesianOptimizationEngine.cs) |
| `DeflatedSharpeRatio` | Correzione per selection bias sul numero di trial | [`Services/Validation/DeflatedSharpeRatio.cs`](../../ProcioneMGR/Services/Validation/DeflatedSharpeRatio.cs) |
| `IStrategyFactory` | Prototipi e default dei range | [`Services/Backtesting/StrategyFactory.cs`](../../ProcioneMGR/Services/Backtesting/StrategyFactory.cs) |
| `wwwroot/js/heatmap.js` | Rendering heatmap Plotly | [`wwwroot/js/heatmap.js`](../../ProcioneMGR/wwwroot/js/heatmap.js) |
| `ConfigPresets` / `DataAvailability` / `AdvancedPanel` | Componenti condivisi | [`Components/Shared/`](../../ProcioneMGR/Components/Shared) |

## Dati letti / scritti

- **Legge**: `OhlcvData`, `MlModels` (modelli salvati per la strategia ML), `UserPageConfigs`.
- **Scrive**: `SavedStrategies` (Save Best), `UserPageConfigs` (ultima configurazione).

## Collegamenti con le altre pagine

- **In ingresso**: [Backtest](backtest.md) e [ML Lab](ml.md) via query string.
- **In uscita**: "Backtest →" per riga della Top 10 (verifica puntuale di una combinazione),
  "Aggiungila all'Ensemble →" dopo il salvataggio verso [Ensemble](ensemble.md).

## Note di design

- Il gate anti-overfitting (Deflated Sharpe) è mostrato **sopra** la Top 10: prima il
  verdetto statistico, poi la classifica.
- La selezione della finestra avviene su in-sample e la metrica riportata è OOS: mai
  scegliere e valutare sugli stessi dati.
- La heatmap è deliberatamente limitata a 2 parametri: in più dimensioni una proiezione
  2D sarebbe fuorviante.
