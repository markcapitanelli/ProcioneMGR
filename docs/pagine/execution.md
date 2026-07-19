# Execution Lab — `/execution`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/ExecutionLab.razor`](../../ProcioneMGR/Components/Pages/ExecutionLab.razor) (~220 righe) |
| **Route** | `/execution` |
| **Sezione navigazione** | Trading |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Misura **quanto costa davvero eseguire un ordine**: su size significative, distribuire
l'ordine nel tempo (**TWAP**, **VWAP**, **Iceberg**, **Adaptive**) riduce l'impatto di
mercato rispetto all'esecuzione immediata. La pagina simula tutti gli algoritmi sulle
candele fini recenti e li confronta per **implementation shortfall** (bps) — misurare invece
di assumere (rif. ROADMAP-QLIB §1.2).

Caveat dichiarato nel `GuidaPanel` (righe 31–35): il modello di impatto è semplificato
(lineare nella quota di volume assorbita) e va calibrato in Paper — i valori assoluti sono
indicativi, **il confronto relativo fra algoritmi è il punto**. Ogni confronto viene
registrato negli [Esperimenti](experiments.md).

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 24–36 | Scopo e limiti del modello |
| Form | 38–81 | Symbol, timeframe di esecuzione (5m/15m/1h), lato Buy/Sell, quantità, finestra in candele; `ConfigPresets` silenzioso (`PageKey="execution"`) |
| Tabella confronto | 83–122 | Per algoritmo, ordinato dal migliore: ordini figli, prezzo medio, shortfall (bps), delta vs Immediate; il migliore ha 🏆 e riga verde |

## Come funziona (flusso del codice)

### `CompareAsync` (righe 165–220)
1. Valida quantità e clampa la finestra (2–2000 candele).
2. Carica le **ultime N candele** del symbol/timeframe e le rimette in ordine cronologico.
3. Il **prezzo di arrivo** è l'open della prima candela della finestra: da lì si misura lo
   shortfall.
4. Costruisce l'`ExecutionIntent` (symbol, side, quantità, arrivo) e per **ogni** algoritmo
   della factory: `algo.BuildPlan(intent, candles, ExecParams)` →
   `Simulator.Simulate(plan, ...)` (righe 200–204). Stesso identico contratto usato dal
   trading reale: i piani simulati qui sono quelli che l'`ExecutionWorker` eseguirebbe su
   Testnet/Live.
5. Registra il run negli Esperimenti con le metriche `{Algoritmo}_bps` (righe 206–213,
   API `Safe*` che non fallisce mai il flusso principale).

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IExecutionAlgorithmFactory` | Tutti gli algoritmi disponibili (Immediate, TWAP, VWAP, Iceberg, Adaptive) | [`Services/Execution/ExecutionAlgorithmFactory.cs`](../../ProcioneMGR/Services/Execution/ExecutionAlgorithmFactory.cs) |
| Algoritmi concreti | Piani a fette; l'Adaptive usa la forma chiusa Almgren-Chriss | [`Services/Execution/ExecutionAlgorithms.cs`](../../ProcioneMGR/Services/Execution/ExecutionAlgorithms.cs) |
| `IExecutionSimulator` | Simula i fill del piano sulle candele con modello di impatto | [`Services/Execution/ExecutionSimulator.cs`](../../ProcioneMGR/Services/Execution/ExecutionSimulator.cs) |
| `ExecutionParameters` | Parametri del modello (partecipazione, impatto) da configurazione | [`Services/Execution/ExecutionModels.cs`](../../ProcioneMGR/Services/Execution/ExecutionModels.cs) |
| `IExperimentTracker` | Registrazione del confronto | [`Services/Experiments/ExperimentTracker.cs`](../../ProcioneMGR/Services/Experiments/ExperimentTracker.cs) |
| `ExecutionWorker` (contesto) | Chi esegue davvero i piani su Testnet/Live (default-off, solo aperture) | [`Services/Trading/ExecutionWorker.cs`](../../ProcioneMGR/Services/Trading/ExecutionWorker.cs) |

## Dati letti / scritti

- **Legge**: `OhlcvData` (finestra recente).
- **Scrive**: `ExperimentRuns` (un run "Execution" per confronto), `UserPageConfigs`.

## Collegamenti con le altre pagine

- [Ensemble](ensemble.md) — è lì che si sceglie l'algoritmo di esecuzione per gamba
  (colonna "Esecuzione"); questo lab serve a decidere **quale** scegliere.
- [Trading](trading.md) / [Metriche](metrics.md) — lo slippage reale dei job eseguiti è
  tracciato nelle metriche runtime.
- [Esperimenti](experiments.md) — storico dei confronti.

## Note di design

- Il confronto usa la stessa factory/simulatore del runtime: nessuna divergenza tra ciò che
  si misura qui e ciò che si esegue in produzione.
- L'esecuzione a fette nel motore reale è deliberatamente limitata: solo Testnet/Live, solo
  aperture, default-off (scelta della roadmap QLIB §1.2 LIVE).
