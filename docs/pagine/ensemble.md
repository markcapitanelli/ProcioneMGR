# Ensemble — `/ensemble`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Ensemble.razor`](../../ProcioneMGR/Components/Pages/Ensemble.razor) (~750 righe) |
| **Route** | `/ensemble` (ancore `#decay-monitor`, `#execution-jobs`, `#drift-monitor`) |
| **Sezione navigazione** | Ricerca & Sviluppo |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer`, implementa `IDisposable` (polling 15s) |

## A cosa serve

Combina **più strategie in un unico portafoglio** su una corsia di trading, dividendo il
capitale in base a chi sta rendendo meglio: a ogni rebalance il capitale viene riallocato
secondo il **rolling Sharpe** recente, con limiti Min/Max % che impediscono sia l'esclusione
totale di una strategia sia la concentrazione eccessiva.

La pagina è anche il **cruscotto di salute** della corsia: monitor di decadimento
(realizzato vs atteso), monitor di drift dei fattori ML, piani di esecuzione a fette e
storico dei ribilanciamenti.

### Il concetto di corsia (lane)
Selettore in cima (righe 37–46): ogni corsia (`TradingLanes.Count`, 3 corsie fisse) è un
ensemble/trading **indipendente e isolato** — stesso database, dati separati. Tutte le
operazioni della pagina sono per-corsia.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| Selettore corsia | 37–46 | Cambio corsia → ricarica tutto |
| GuidaPanel | 48–112 | Spiegazione completa, inclusa la distinzione cruciale Rolling Sharpe (ri-simulazione) vs Monitor decadimento (trade reali) |
| Toast flottante | 114–124 | Esito azioni in position:fixed (i bottoni sono in fondo pagina, l'esito resta visibile) |
| Configurazione | 132–351 | Exchange/symbol/timeframe/capitale, rebalance/rolling days, Min/Max %, **Futures+leva** (con avviso margine isolato e warning MiCA per Binance), tabella strategie, aggiunta gambe, azioni |
| Tabella strategie | 209–253 | Per gamba: parametri, attiva, alloc %, **SL/TP/Trailing %**, **Sharpe/PF/MaxDD attesi** (alimentano il decay monitor), **algoritmo di esecuzione** (Immediato/TWAP/VWAP/Iceberg/Adaptive + finestra minuti), rimozione |
| Aggiunta gambe | 255–335 | Da predefinite, da salvate (badge "Optimized" con Sharpe), da **modelli ML compatibili** (solo stesso symbol/timeframe), e il **Champion del registry** (banner dedicato: si auto-aggiorna, "Solo Paper/Testnet — mai Live, rifiutato dal motore per costruzione") |
| Azioni | 337–349 | Save, Rebalance Now, Enable/Disable Ensemble, Aggiorna status |
| Status live | 353–388 | KPI (capitale, PnL, last/next rebalance) + tabella strategie live ordinata per rolling Sharpe (migliore evidenziata) |
| Monitor decadimento | 390–450 | Card per gamba: Sharpe atteso vs realizzato sui **trade realmente eseguiti**, delta, % dell'atteso, badge Alert/In attesa/In linea, filtro "solo alert" |
| Piani di esecuzione | 452–485 | Job TWAP/VWAP/Iceberg recenti della corsia con stato e prezzo medio |
| Drift fattori | 487–554 | Per un modello ML scelto: distribuzione training vs ultime N candele con tre detector — **PSI, KS (p-value), Page-Hinkley** — e severità per fattore |
| Performance | 556–562 | Equity totale + per strategia (`OhlcvChart` solo indicatori) |
| Rebalancing history | 564–584 | Ogni evento con motivo e transizioni di allocazione per gamba |

## Come funziona (flusso del codice)

### Architettura (righe 588–600)
Lo stato applicativo vive in **`EnsemblePageService`** (refactoring P1-5): la pagina espone
alias read-only (`_config => Svc.Config` ecc.) così il markup con `@bind` resta valido.
Le azioni sono one-liner che delegano al service (`SaveAsync`, `StartEnsembleAsync`,
`RebalanceNowAsync`, `AddPredefined`, `AddFromSaved`, `AddFromMlModel`, `AddChampion`…).

### Ciclo di vita
`OnInitializedAsync` carica i cataloghi (strategie salvate, modelli ML), poi `LoadLaneAsync`
in modo **granulare** — un errore su un pannello non impedisce agli altri di popolarsi
(config+champion, status, decay, execution jobs come chiamate separate). Un `PollingTimer`
da 15 secondi tiene aggiornato lo status; `Dispose` lo ferma.

### Rebalance
Il ricalcolo periodico in background è del `EnsembleRebalanceWorker`; "Rebalance Now" forza
il ricalcolo immediato via `IEnsembleManager`. Ogni evento finisce nella history con il
motivo e le transizioni `prev% → new% (Sharpe)`.

### Decadimento vs drift — i due monitor
- **Decay monitor**: `StrategyDecayMonitor` (via `EnsembleManager.GetDecayReportsAsync`)
  confronta lo Sharpe atteso dichiarato sulla gamba (dal backtest/holdout, compilato a mano
  o dalla Pipeline) con lo Sharpe dei trade **chiusi realmente** dal Trading. Serve una
  finestra minima di trade (default 20). È lo stesso report che alimenta l'alert in Home.
- **Drift monitor**: `FeatureDriftMonitor` confronta la **distribuzione degli input** del
  modello (finestra di training vs candele recenti) con PSI / Kolmogorov-Smirnov /
  Page-Hinkley. La didascalia è esplicita: un drift non è un allarme di PnL, è un avviso
  anticipatore che gli input sono cambiati.

### Champion del registry (righe 305–335)
Se il [Registry](registry.md) ha un Champion per symbol+timeframe della corsia, si può
aggiungere come gamba con soglie Long/Short: la gamba usa la **sentinella
`TradingEngine.ChampionStrategyName`**, quindi segue automaticamente le promozioni future
del registry. Vincolo di sicurezza scolpito nella UI e nel motore: mai in Live.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `EnsemblePageService` | Stato pagina + tutte le azioni | [`Services/Ensemble/EnsemblePageService.cs`](../../ProcioneMGR/Services/Ensemble/EnsemblePageService.cs) |
| `IEnsembleManager` / `EnsembleManager` | Config/status/performance, rebalance, decay reports | [`Services/Ensemble/EnsembleManager.cs`](../../ProcioneMGR/Services/Ensemble/EnsembleManager.cs) |
| `EnsembleAllocator` | Calcolo allocazioni da rolling Sharpe con vincoli Min/Max | [`Services/Ensemble/EnsembleAllocator.cs`](../../ProcioneMGR/Services/Ensemble/EnsembleAllocator.cs) |
| `EnsembleRebalanceWorker` | Ribilanciamento periodico in background | [`Services/Ensemble/EnsembleRebalanceWorker.cs`](../../ProcioneMGR/Services/Ensemble/EnsembleRebalanceWorker.cs) |
| `StrategyDecayMonitor` | Realizzato vs atteso sui trade chiusi | [`Services/Monitoring/StrategyDecayMonitor.cs`](../../ProcioneMGR/Services/Monitoring/StrategyDecayMonitor.cs) |
| `FeatureDriftMonitor` + detector | PSI / KS / Page-Hinkley sui fattori del modello | [`Services/Monitoring/Drift/`](../../ProcioneMGR/Services/Monitoring/Drift) |
| `TradingLanes` | Numero e semantica delle corsie | [`Services/Trading/TradingLanes.cs`](../../ProcioneMGR/Services/Trading/TradingLanes.cs) |
| `ModelRegistry` (via service) | Champion corrente per symbol/timeframe | [`Services/Registry/ModelRegistry.cs`](../../ProcioneMGR/Services/Registry/ModelRegistry.cs) |
| `IStrategyFactory` | Prototipi per l'aggiunta di gambe predefinite | [`Services/Backtesting/StrategyFactory.cs`](../../ProcioneMGR/Services/Backtesting/StrategyFactory.cs) |

## Dati letti / scritti

- **Legge**: configurazione/stato ensemble per corsia, `SavedStrategies`, `MlModels`,
  `ExecutionJobs`, trade chiusi (per il decay), candele (per il drift).
- **Scrive**: configurazione ensemble della corsia (Save), eventi di rebalance,
  enable/disable del ribilanciamento automatico.

## Collegamenti con le altre pagine

- **In ingresso**: [Optimization](optimization.md) ("Aggiungila all'Ensemble →"),
  [Pipeline](pipeline.md) ("Applica al Trading" precompila gambe con SL/TP e attese),
  [Registry](registry.md) (Champion), [ML Lab](ml.md) (modelli).
- **In uscita**: [Trading](trading.md) — l'ensemble della corsia è ciò che il motore esegue;
  gli SL/TP/trailing per gamba vengono applicati automaticamente all'apertura posizioni.

## Note di design

- La distinzione "ri-simulazione vs trade reali" è spiegata due volte (GuidaPanel e note):
  è l'errore di lettura più probabile per l'utente.
- Il filtro dei modelli ML per symbol/timeframe è motivato nel commento (righe 277–280):
  i fattori sono simbolo-specifici per costruzione.
- L'avviso MiCA su Binance Futures (righe 190–198) riflette il vincolo normativo reale
  dal 2026-07-01 per i residenti SEE: per la leva si usa Bitget.
