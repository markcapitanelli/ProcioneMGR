# Regimes — `/regimes`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Regimes.razor`](../../ProcioneMGR/Components/Pages/Regimes.razor) (~400 righe) |
| **Route** | `/regimes` |
| **Sezione navigazione** | Strumenti Avanzati |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Il mercato non si comporta sempre allo stesso modo: una strategia a media mobile ama le
tendenze, una a inversione ama i mercati laterali. Questa pagina raggruppa lo storico in
**regimi** — cluster di periodi con caratteristiche simili (volatilità, direzione, forza del
trend, RSI) — con **K-means** (ML non supervisionato). Non predice il futuro: dice *in che
tipo di mercato siamo stati, e quale strategia ha reso meglio in ciascuno*.

Il punto operativo è l'integrazione con l'ensemble: con **Regime-Aware Weighting** attivo,
il peso di ogni strategia diventa `0.6 · Sharpe rolling + 0.4 · performance nel regime
corrente`.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 25–60 | K, Train vs Activate, Silhouette, profili, matrice performance, timeline |
| Training | 62–119 | Serie + slider K (2–8) + **Train Model** (preview, non cambia nulla di operativo) + **Activate Model** (lo rende il modello usato dall'Ensemble) |
| Silhouette + profili | 121–158 | Score colorato (rosso <0.2, giallo <0.4, verde ≥0.4) con stato ATTIVO/preview; tabella profili: label suggerita, campioni, medie di volatilità/trend/RSI |
| Matrice strategia × regime | 160–188 | Sharpe medio di ogni strategia in ogni regime, celle colorate rosso→verde: la più verde per colonna è la strategia giusta per quel regime |
| Timeline | 190–209 | Striscia colorata proporzionale (flex-weight = campioni) del regime periodo per periodo, ultimi 6 mesi |
| Integrazione Ensemble | 212–233 | Switch Regime-Aware Weighting (salva subito con `@bind:after`) + regime corrente dell'ensemble |

## Come funziona (flusso del codice)

### Avvio (righe 299–318)
Carica simboli noti, la configurazione ensemble (per lo stato dello switch), e l'eventuale
**modello attivo** (`Detector.LoadLatestModelAsync`) con i suoi profili (deserializzati da
`RegimeProfilesJson`); se esiste, costruisce subito la timeline.

### Train — `TrainAsync` (righe 320–338)
`Detector.TrainAsync(TrainingConfiguration, activate: false)`: estrae le feature di mercato,
esegue K-means con K scelto e calcola il Silhouette Score. Il risultato è un **preview**:
serve "Activate" per renderlo operativo — separazione esplicita tra esplorazione e
attivazione.

### Activate — `ActivateAsync` (righe 340–354)
`Detector.ActivateModelAsync(model)`: il modello diventa quello usato dall'ensemble (e dal
`RegimeChangeTriggerWorker` della pipeline). Ricostruisce timeline e regime corrente.

### Timeline — `BuildTimelineAsync` (righe 356–381)
Estrae le feature degli ultimi 6 mesi (`IMarketFeatureExtractor`), le etichetta col modello
attivo (`LabelFeaturesAsync`) e comprime le sequenze di regime uguale in segmenti con peso
proporzionale alla durata.

### Regime-aware weighting (righe 390–397)
Lo switch scrive `RegimeAwareWeighting` nella configurazione dell'ensemble via
`IEnsembleManager.UpdateConfigurationAsync`; il regime corrente mostrato viene dallo status
dell'ensemble (symbol/timeframe devono combaciare col modello attivo).

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IRegimeDetector` / `RegimeDetector` | Training K-means, attivazione, labeling | [`Services/Regime/RegimeDetector.cs`](../../ProcioneMGR/Services/Regime/RegimeDetector.cs) |
| `IMarketFeatureExtractor` | Feature di mercato (volatilità, trend, RSI…) per training e timeline | [`Services/Regime/MarketFeatureExtractor.cs`](../../ProcioneMGR/Services/Regime/MarketFeatureExtractor.cs) |
| `IEnsembleManager` | Lettura/scrittura del flag regime-aware e regime corrente | [`Services/Ensemble/EnsembleManager.cs`](../../ProcioneMGR/Services/Ensemble/EnsembleManager.cs) |
| `RegimeRetrainingWorker` (indiretto) | Retraining periodico del modello attivo | [`Services/Regime/RegimeRetrainingWorker.cs`](../../ProcioneMGR/Services/Regime/RegimeRetrainingWorker.cs) |
| `RegimeAugmentation` (contesto) | One-hot del regime come feature ML opt-in | [`Services/Regime/RegimeAugmentation.cs`](../../ProcioneMGR/Services/Regime/RegimeAugmentation.cs) |

## Dati letti / scritti

- **Legge**: `OhlcvData` (via extractor), modelli regime salvati, configurazione/status ensemble.
- **Scrive**: modelli regime (train/activate), flag `RegimeAwareWeighting` dell'ensemble,
  `UserPageConfigs`.

## Collegamenti con le altre pagine

- [Ensemble](ensemble.md) — il consumatore del regime corrente (pesi regime-aware).
- [Pipeline](pipeline.md) — lo stage di regime detection e il trigger "Event" al cambio regime.
- [ML Lab](ml.md) — il regime one-hot come feature opzionale dei modelli.

## Note di design

- Train ≠ Activate: si può esplorare K e periodi senza toccare l'operatività.
- Il Silhouette a semaforo educa a non fidarsi di cluster mal separati.
- La strategia `RegimeConditionalStrategy` del backtesting usa questi stessi regimi per le
  meta-strategie condizionate.
