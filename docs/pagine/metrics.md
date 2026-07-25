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

### [Fase 2026-07-25] Costo di esecuzione: assunto vs pagato

Il pannello in evidenza in cima ai grafici confronta due numeri che prima non erano mai stati
messi uno accanto all'altro:

- **assunto in selezione** — `PipelineCosts.DefaultSlippagePercent` (0,05% per fill = 5 bps), cioè
  il costo che la caccia alle strategie dà per scontato quando decide se una strategia è
  profittevole;
- **pagato davvero** — mediana, media e coda (P95/P99) dell'implementation shortfall misurato sugli
  ordini di corsia reali (`procione.trading.slippage_bps`).

Se il pagato supera stabilmente l'assunto, le strategie promosse sono state scelte con un vantaggio
che non esiste: il verdetto colorato dice esattamente questo. È **asimmetrico di proposito** — un
costo minore dell'assunto è una buona notizia (selezione prudente), solo il maggiore è un allarme.

La misura esiste **solo su Testnet/Live**: in Paper il prezzo eseguito coincide per costruzione con
quello di riferimento, quindi uno shortfall Paper sarebbe zero per definizione e diluirebbe le
statistiche. Un pannello vuoto su una corsia Paper è il comportamento corretto, non un guasto.

### [Fase 2026-07-25] Latenza degli ordini

`procione.trading.order_latency_ms` a P50/P95/P99 più la serie temporale. Misura invio→risposta
dell'exchange, **inclusa l'attesa del rate-limiter interno**: è il ritardo che la strategia subisce
davvero, e una coda interna lo produce esattamente come lo produce la rete. Va letta ai percentili
alti — è sulla coda che un ritardo costa un fill. I percentili sono calcolati sugli **ultimi
campioni** della sessione (finestra scorrevole), non su tutta la sua storia.

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
