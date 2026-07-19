# Discovery — `/discovery`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Discovery.razor`](../../ProcioneMGR/Components/Pages/Discovery.razor) (~420 righe) |
| **Route** | `/discovery` |
| **Sezione navigazione** | Strumenti Avanzati |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

"**Non so quale strategia usare, trovamela tu**": mentre Optimization ottimizza UNA strategia
su UNA coppia, Discovery esplora **ogni combinazione strategia × coppia × timeframe**
selezionata, ciascuna con la stessa disciplina walk-forward, e produce una classifica delle
combinazioni più promettenti ordinata per **Sharpe out-of-sample** con verdetto **DSR**
(Deflated Sharpe) per ciascuna.

### Modalità creativa
Oltre allo sweep delle strategie note, il sistema **genera strategie mai codificate prima**:
combinazioni di segnali elementari ("RSI basso E volume alto E trend a favore"), trigger su
eventi discreti (picchi di volatilità, shock di prezzo con uscita a tempo) e meta-strategie
che cambiano comportamento col regime. Ogni spec generata passa dagli **stessi filtri
onesti** delle altre (screening sul periodo + conferma walk-forward a parametri fissi) e le
confermate si fondono in classifica con le classiche.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 24–59 | Scopo, colonna OOS Sharpe, modalità creativa |
| Universo di ricerca | 61–100 | Checkbox per coppie (tutte quelle con dati), timeframe e strategie; default: tutte le coppie × 1h × tutte le strategie |
| Periodo + creativa | 102–122 | Da/A, checkbox modalità creativa con budget di spec (10–2000, default 200) |
| `AdvancedPanel` | 125–140 | Finestre walk-forward IS/OOS/Step (mesi) |
| Run + progress | 142–160 | Il bottone mostra il **numero di job** (coppie × TF × strategie); progress con best OOS corrente e Stop |
| Classifica | 168–212 | Top 20+: OOS Sharpe con cella colorata rosso→verde, badge **DSR** (verde se ≥95%), IS Sharpe, return, MaxDD, trade, parametri, **💾 Salva** per riga |

## Come funziona (flusso del codice)

### Universo e default (righe 240–254)
Le coppie/timeframe proposti derivano da ciò che esiste in `OhlcvData` (query `Distinct` su
Symbol/Timeframe). I preset applicati filtrano le voci non più esistenti; una selezione che
si svuoterebbe del tutto lascia i default (commento alle righe 279–280).

### Run — `RunAsync` (righe 297–386)
1. Costruisce `StrategyDiscoveryConfiguration` (universo + walk-forward + TopN 20) e chiama
   `IStrategyDiscovery.DiscoverAsync(cfg, progress, token)` con progress per job.
2. **Modalità creativa** (righe 327–352): per ogni coppia×TF,
   `IStrategyComposer.ComposeAndScreenAsync` genera le spec (seed fisso 42, budget
   clampato), le screena e conferma in walk-forward; le confermate si aggiungono ai
   candidati e la classifica viene riordinata per OOS Sharpe.
3. **Experiment tracking** best-effort (righe 353–378): un run "Discovery" per campagna con
   metriche Candidates/JobsRun/CreativeCandidates/BestOosSharpe.

### Salvataggio — `SaveAsync` (righe 390–406)
Salva la combinazione in `SavedStrategies` con `IsOptimized = true` e lo Sharpe OOS come
`OptimizationSharpe`: comparirà in [Le mie Strategie](strategies.md) e nell'elenco
"Aggiungi salvata" dell'[Ensemble](ensemble.md).

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IStrategyDiscovery` / `StrategyDiscoveryEngine` | Lo sweep walk-forward su tutte le combinazioni | [`Services/Discovery/StrategyDiscoveryEngine.cs`](../../ProcioneMGR/Services/Discovery/StrategyDiscoveryEngine.cs) |
| `IStrategyComposer` / `StrategyComposer` | Generazione creativa (composite/event/regime) + screening | [`Services/Discovery/StrategyComposer.cs`](../../ProcioneMGR/Services/Discovery/StrategyComposer.cs) |
| `SignalCatalog` / `CompositeSignalStrategy` / `EventTriggerStrategy` / `RegimeConditionalStrategy` | I mattoni delle strategie generate | [`Services/Backtesting/`](../../ProcioneMGR/Services/Backtesting) |
| `DeflatedSharpeRatio` (via engine) | Il verdetto DSR per candidato | [`Services/Validation/DeflatedSharpeRatio.cs`](../../ProcioneMGR/Services/Validation/DeflatedSharpeRatio.cs) |
| `IExperimentTracker` | Registrazione della campagna | [`Services/Experiments/ExperimentTracker.cs`](../../ProcioneMGR/Services/Experiments/ExperimentTracker.cs) |
| `IStrategyFactory` | Prototipi per l'universo delle strategie classiche | [`Services/Backtesting/StrategyFactory.cs`](../../ProcioneMGR/Services/Backtesting/StrategyFactory.cs) |

## Dati letti / scritti

- **Legge**: `OhlcvData` (universo e dati dei job).
- **Scrive**: `SavedStrategies` (salvataggi), `ExperimentRuns` (campagna), `UserPageConfigs`.

## Collegamenti con le altre pagine

- [Optimization](optimization.md) — il "fratello mirato": Discovery per esplorare, poi
  Optimization per rifinire i parametri della combinazione scelta.
- [Le mie Strategie](strategies.md) / [Ensemble](ensemble.md) — destinazione dei salvataggi.
- [Pipeline](pipeline.md) — usa la Discovery (anche creativa) come stage automatizzato.

## Note di design

- Il DSR è calcolato **per candidato** tenendo conto del numero di combinazioni provate:
  con migliaia di job, uno Sharpe OOS alto da solo non basta.
- La modalità creativa è deterministica a parità di seed (42) e passa dagli stessi gate
  delle strategie classiche: la creatività non compra sconti sul rigore.
