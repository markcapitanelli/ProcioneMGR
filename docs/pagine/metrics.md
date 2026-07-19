# Metriche — `/metrics`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Metrics.razor`](../../ProcioneMGR/Components/Pages/Metrics.razor) (~210 righe) |
| **Route** | `/metrics` |
| **Sezione navigazione** | Dati & Monitoraggio |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer`, implementa `IAsyncDisposable` |

## A cosa serve

Dashboard di **osservabilità runtime**: legge i contatori interni emessi dal motore
(`ProcioneMetrics`) e li mostra dal vivo, **senza alcun servizio esterno** (niente
Prometheus/Grafana necessari — quelli sono lo stack LGTM opzionale dietro
`Observability:Enabled`). Punto importante spiegato nel `GuidaPanel`: i totali sono **dalla
partenza del processo** e si azzerano a ogni riavvio; uno zero significa "evento mai avvenuto
in questa sessione".

Metriche esposte:

| Tile | Contatore | Significato |
|---|---|---|
| Trade eseguiti | `procione.trades.executed` | Aperture/chiusure dal motore, taggate per lato (Buy/Sell) e azione (Open/Close) |
| Job esecuzione | `procione.execution.jobs` | Piani di esecuzione a fette (TWAP/VWAP/Iceberg) per esito |
| Promozioni corsia | `procione.lane.promotions` | Promozioni/retrocessioni del ciclo autonomo |
| Feature in drift | `procione.drift.alerts` | Alert del drift monitor |
| Modelli ritirati | `procione.models.retired` | Ritiri dal Model Registry |
| Run pipeline | `procione.pipeline.runs` | Esecuzioni della pipeline autonoma |

Più i grafici: trade per azione/lato, job per esito (con colori semantici), e la serie
temporale dello **slippage** dei job completati (implementation shortfall in bps, con
n/media/min/max nell'header).

## Come funziona (flusso del codice)

### Snapshot e polling (righe 115–127)
`OnInitialized` prende uno snapshot da `MetricsCollector.Snapshot()` e avvia un
`PollingTimer` da 5 secondi che invoca `Refresh` — la pagina si auto-aggiorna. `Refresh`
riprende lo snapshot, aggiorna il timestamp mostrato e marca `_renderPending`.

### Rendering grafici — `OnAfterRenderAsync` + `RenderChartsAsync` (righe 129–172)
Il flag `_renderPending` evita ridisegni inutili: i grafici Plotly vengono ridisegnati solo
dopo un refresh dati. `MetricsSnapshot` offre:
- `Total(name)` — totale di un contatore;
- `GroupByTag(name, tag)` — ripartizione per valore del tag (es. job per `status`);
- `SlippageRecent` / `SlippageCount/Mean/Min/Max` — le osservazioni recenti di slippage.

I colori dei job sono mappati per esito (righe 153–160): verde Completed, rosso Failed,
grigio Cancelled, blu Started.

### Ciclo di vita — `DisposeAsync` (righe 194–208)
Ferma il timer e smonta i tre grafici (`dispose` sul modulo charts.js), tollerando
`JSDisconnectedException` se il circuito è già chiuso.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `MetricsCollector` | Ascolta i Meter .NET e accumula i contatori; fornisce `Snapshot()` | [`Services/Observability/MetricsCollector.cs`](../../ProcioneMGR/Services/Observability/MetricsCollector.cs) |
| `ProcioneMetrics` | Definizione dei contatori emessi dal motore (nomi `procione.*`) | [`Services/Observability/ProcioneMetrics.cs`](../../ProcioneMGR/Services/Observability/ProcioneMetrics.cs) |
| `PollingTimer` | Timer di auto-refresh riusabile (5s) | [`Components/Shared/PollingTimer.cs`](../../ProcioneMGR/Components/Shared/PollingTimer.cs) |
| `wwwroot/js/charts.js` | Grafici `bar` e `timeseries` Plotly | [`wwwroot/js/charts.js`](../../ProcioneMGR/wwwroot/js/charts.js) |

Chi **emette** le metriche: `TradingEngine` (trade), `ExecutionWorker` (job e slippage),
`PromotionWorker`/`LanePromoter` (promozioni), `FeatureDriftWorker` (drift),
`ModelRegistry` (ritiri), `PipelineEngine` (run).

## Dati letti / scritti

- **Legge**: solo contatori in-memory del processo (nessuna query DB).
- **Scrive**: nulla.

## Collegamenti con le altre pagine

- [Trading](trading.md), [Execution Lab](execution.md), [Pipeline](pipeline.md),
  [Registry](registry.md), [Autonomia](admin-autonomy.md) — le pagine dove nascono gli
  eventi contati qui.

## Note di design

- Zero dipendenze esterne: utile anche in sviluppo locale senza stack di osservabilità.
- Il pattern snapshot+polling evita locking sui contatori live: la UI lavora sempre su una
  copia coerente.
