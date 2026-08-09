# 26 — CONFIGURAZIONE E SCHEMA DATI (completo)

> Ogni opzione di configurazione con il suo **default reale letto dal codice**, ogni entità
> persistita con **tutti** i suoi campi, ogni contratto gRPC. Estrazione meccanica.

| | |
|---|---:|
| Classi di configurazione | 44 |
| Opzioni totali | 359 |
| Tabelle (`DbSet`) | 34 |
| Campi di entità | 398 |
| File `.proto` | 5 |
| RPC gRPC | 12 |

---

## 1. Opzioni di configurazione

Il **default** è il valore inizializzato nella classe; se vuoto, il tipo usa il proprio
default (`false` per bool, `0` per numerici, `null` per riferimenti).

### `AutoReapplyOptions`

<sub>`ProcioneMGR/Services/Pipeline/PipelineSchedulerWorker.cs`</sub>

> Opzioni della ri-applica automatica dell'ensemble (sezione di config AutoReapply ).

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | Interruttore globale della ri-applica automatica. DEFAULT false (safety): finché non lo abiliti esplicitamente, lo scheduler lancia i run ma NON schiera mai da solo un ensemble — l'utente applica a mano da /pipeline, come prima. |
| `LookbackDays` | `int` | `7` | Quanti giorni indietro guardare per i run completati non ancora valutati. |
| `MaxPerTick` | `int` | `3` | Massimo numero di run valutati per tick (limita il fan-out). |

### `BacktestConfiguration`

<sub>`ProcioneMGR/Services/Backtesting/BacktestModels.cs`</sub>

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `ExchangeName` | `string` | `string.Empty` | — |
| `Symbol` | `string` | `string.Empty` | — |
| `Timeframe` | `string` | `string.Empty` | — |
| `From` | `DateTime` | `—` | — |
| `To` | `DateTime` | `—` | — |
| `InitialCapital` | `decimal` | `10000m` | — |
| `PositionSizePercent` | `decimal` | `10m` | % del capitale corrente impegnata per ogni trade. |
| `FeePercent` | `decimal` | `0.1m` | Commissione per lato (entry/exit), in percentuale del notional. Default 0.1%. |
| `StrategyName` | `string` | `string.Empty` | — |
| `StrategyParameters` | `Dictionary&lt;string, decimal&gt;` | `new()` | — |
| `StopLossPercent` | `decimal` | `—` | Stop loss in % dal prezzo di ingresso (0 = disattivo). Overlay a livello di MOTORE (McAllen: "lo stop loss E' parte del trade"): controllato su high/low di ogni candela PRIMA del segnale di strategia, eseguito al livello di stop. La strategia non viene notifi… |
| `TakeProfitPercent` | `decimal` | `—` | Take profit in % dal prezzo di ingresso (0 = disattivo). |
| `TrailingStopPercent` | `decimal` | `—` | Trailing stop in % dal miglior prezzo raggiunto dall'ingresso (0 = disattivo). Sale con il prezzo e non scende mai: preserva i guadagni (McAllen cap. 17). |
| `TrailingAtrMultiple` | `decimal` | `—` | [Fase 5a] Trailing "chandelier": distanza dal miglior prezzo pari a questo multiplo dell'ATR invece che a una percentuale fissa (0 = disattivo). Quando è &gt; 0 sostituisce , non vi si somma — due trailing attivi insieme sarebbero semplicemente il più stretto… |
| `TrailingAtrPeriod` | `int` | `14` | Periodo dell'ATR usato dal trailing chandelier (default 14, Wilder). |
| `Leverage` | `decimal` | `1m` | Leva finanziaria (futures/margin). Con leva L, e' la quota di capitale usata come MARGINE e il nozionale e' margine x L. A 1 (default) il comportamento coincide esattamente con lo spot attuale. Con L &gt; 1 il motore modella anche la LIQUIDAZIONE intrabar (ve… |
| `MaintenanceMarginPercent` | `decimal` | `0.5m` | Margine di mantenimento in % del nozionale (default 0.5%, tipico dei perpetual su coppie liquide). La posizione viene liquidata quando margine + PnL non realizzato scende a questo livello: si perde quasi tutto il margine, come nella realta'. |
| `FundingRatePercentPer8h` | `decimal` | `—` | Funding rate dei perpetual in % del nozionale per periodo di 8 ore (0 = disattivo; 0.01 e' il valore "neutro" storico). Addebitato pro-rata a ogni candela con posizione aperta: a leva alta su holding lunghi pesa piu' delle commissioni. |
| `SlippagePercent` | `decimal` | `—` | Slippage in % applicato SFAVOREVOLMENTE a ogni eseguito (entry, exit, stop, target, liquidazione). 0 = fill teorici (default, comportamento invariato). |
| `EntryExecution` | `EntryExecutionStyle` | `EntryExecutionStyle.Taker` | Come viene eseguito l'INGRESSO. è il default e lascia il comportamento invariato. Le uscite restano sempre taker: uno stop protettivo è un ordine a mercato per natura — non lo si può appoggiare passivamente al book e sperare. |
| `MakerOffsetPercent` | `decimal` | `0.05m` | Quanto passivo si mette il limite, in % sotto (long) o sopra (short) la close del segnale. Più è passivo, meglio si compra QUANDO si viene riempiti — e meno spesso si viene riempiti. |
| `MakerMaxWaitBars` | `int` | `3` | Per quante candele il limite resta appoggiato prima di scadere. |
| `MakerFeePercent` | `decimal` | `0.02m` | Commissione per lato di un eseguito MAKER, in % del nozionale (tipicamente &lt; ). |
| `MakerQueuePenetrationPercent` | `decimal` | `—` | [F-queue, roadmap profitto-intraday] PROXY DI CODA. Il limite si considera riempito solo se il prezzo PENETRA oltre il livello di questa % (long: Low ≤ limite·(1−q); short: High ≥ limite·(1+q)), non se lo SFIORA soltanto. Modella deterministicamente la posizi… |
| `MakerFallbackToTaker` | `bool` | `—` | Alla scadenza del limite non riempito: true = si attraversa lo spread e si entra comunque a mercato (taker), false = il segnale si perde. Sono due strategie diverse, non due sfumature della stessa: la prima paga il taker proprio sui casi in cui il prezzo è sc… |
| `VolatilityTargeting` | `VolatilityTargetingOptions` | `new()` | Dosaggio della posizione sulla volatilità (spento di default: comportamento invariato). |
| `FundingHistory` | `List&lt;FundingRatePoint&gt;?` | `—` | [T0.2] Serie STORICA dei funding rate (percento per 8h, FIRMATA). Null o vuota = si usa la costante come sempre. Quando presente, il motore applica il rate dell'ultimo evento ≤ timestamp della candela, rispettando il LATO: con funding positivo il long paga e … |

### `BayesianOptions`

<sub>`ProcioneMGR/Services/Optimization/Bayesian/BayesianOptimizationEngine.cs`</sub>

> Iperparametri del surrogato Gaussian Process e dell'acquisizione.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `LengthScale` | `double` | `0.2` | Lengthscale del kernel RBF sullo spazio normalizzato [0,1]^d. |
| `SignalVariance` | `double` | `1.0` | Varianza del segnale (ampiezza a priori delle funzioni). |
| `NoiseVariance` | `double` | `1e-6` | Rumore/regolarizzazione sulla diagonale (stabilità numerica + osservazioni rumorose). |
| `ExplorationXi` | `double` | `0.01` | Parametro di esplorazione ξ dell'Expected Improvement (più alto ⇒ più esplorativo). |
| `AcquisitionSamples` | `int` | `512` | Quanti candidati casuali campionare per massimizzare l'acquisizione a ogni passo. |
| `OptimizeHyperparameters` | `bool` | `true` | Se true (default), e vengono STIMATI dai dati massimizzando la log-verosimiglianza marginale del GP a ogni passo, invece di restare fissi (fissi ⇒ il surrogato non si adatta e la ricerca degenera verso il casuale). I valori nelle proprietà fungono da fallback… |
| `MinPointsForHyperparameterFit` | `int` | `4` | Numero minimo di osservazioni per stimare gli iperparametri via marginal-likelihood. |
| `Seed` | `int` | `42` | Seme di base: la ricerca è deterministica a parità di seme e di storia. |

### `CampaignOptions`

<sub>`ProcioneMGR/Services/Pipeline/CampaignOptions.cs`</sub>

> Opzioni del Campaign Planner (Fase 1, PRD Autonomia Operativa §4), sezione Campaign .

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | Gate GLOBALE del planner. DEFAULT false (è IL cambio di natura da strumento ad agente: l'attivazione è una decisione esplicita dell'operatore, come da PRD §4). Hot-reload. |
| `TickSeconds` | `int` | `60` | Cadenza del tick del worker (letta all'avvio; cambiarla richiede riavvio). |

### `CarryConfiguration`

<sub>`ProcioneMGR/Services/Carry/CarryModels.cs`</sub>

> [E3 roadmap profitto-intraday] Configurazione del carry delta-neutro (long spot + short perp sullo stesso simbolo). L'edge è il FUNDING incassato dallo short quando è positivo — un flusso, non una previsione. Delta-neutro: la componente direzionale del prezzo si elide fra le due gambe.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `InitialCapital` | `decimal` | `10_000m` | Capitale iniziale (unità di conto, es. USDT). |
| `PositionSizePercent` | `decimal` | `50m` | % del capitale impegnata come nozionale PER GAMBA (le due gambe hanno lo stesso nozionale). |
| `EnterAnnualFundingPercent` | `decimal` | `5m` | Si ENTRA quando il funding annualizzato medio (finestra ) supera questa soglia (%). |
| `ExitAnnualFundingPercent` | `decimal` | `—` | Si ESCE quando il funding annualizzato medio scende sotto questa soglia (%). Deve essere &lt; enter (isteresi). |
| `TrailingFundingEvents` | `int` | `9` | Eventi di funding su cui mediare per la decisione (8h l'uno: 9 ≈ 3 giorni). Smussa gli spike singoli. |
| `FundingEventsPerDay` | `int` | `3` | Eventi di funding al giorno dell'exchange (Binance/Bitget: 3, ogni 8h). |
| `SpotFeePercent` | `decimal` | `0.1m` | Commissione per lato della gamba SPOT (% del nozionale). |
| `PerpFeePercent` | `decimal` | `0.05m` | Commissione per lato della gamba PERP (% del nozionale, tipicamente &lt; spot). |
| `SlippagePercent` | `decimal` | `0.03m` | Slippage sfavorevole per gamba (%), in entrata e in uscita. |

### `CarryOptions`

<sub>`ProcioneMGR/Services/Carry/CarryWorker.cs`</sub>

> Configurazione del forward-test del carry (sezione "Carry").

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | Default OFF: il carry è un edge nuovo in forward test, si accende deliberatamente. Anche acceso, di default gira in PAPER (nessun ordine reale) — vedi . |
| `Mode` | `string` | `"Paper"` | "Paper" (default, simulazione) o "Testnet". Live NON è un valore accettato: il parsing lo rifiuta e resta Paper. Il carry non può mai operare con denaro reale. |
| `Symbols` | `List&lt;string&gt;` | `["BTC", "ETH", "SOL", "BNB", "XRP", "DO…` | Simboli (ticker base) da sorvegliare per il carry. |
| `EvaluationMinutes` | `int` | `60` | Minuti fra due valutazioni (il funding cambia ogni 8h: un'ora è ampiamente sufficiente). |
| `EnterAnnualFundingPercent` | `decimal` | `5m` | — |
| `ExitAnnualFundingPercent` | `decimal` | `—` | — |
| `TrailingFundingEvents` | `int` | `9` | — |
| `PositionSizePercent` | `decimal` | `50m` | — |

### `CommitteeOptions`

<sub>`ProcioneMGR/Services/Llm/Committee/AiCommittee.cs`</sub>

> [AF3] Opzioni del comitato, sezione Committee . Default SPENTO. parte VUOTA per la stessa lezione di : il binder di configurazione APPENDE gli elementi di un array alla lista già inizializzata — con un default popolato la lista raddoppierebbe a ogni salvataggio dal pannello.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | — |
| `Providers` | `List&lt;string&gt;` | `[]` | Provider votanti; vuota = . Vota solo chi ha la chiave. |
| `DefaultProviders` | `IReadOnlyList&lt;string&gt;` | `—` | — |
| `TimeoutSeconds` | `int` | `30` | Timeout del SINGOLO voto (i free tier sono lenti; i voti corrono in parallelo). |
| `MinValidVotes` | `int` | `2` | Voti validi minimi perché la maggioranza valga; sotto, decide il default deterministico. |

### `ComposerConfiguration`

<sub>`ProcioneMGR/Services/Discovery/StrategyComposer.cs`</sub>

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `MaxCandidates` | `int` | `200` | — |
| `Seed` | `int` | `42` | — |
| `EnableComposite` | `bool` | `true` | — |
| `EnableEvent` | `bool` | `true` | — |
| `EnableRegime` | `bool` | `true` | — |
| `SignalPool` | `List&lt;int&gt;` | `new()` | Signal ids allowed in composite specs (empty = the whole catalog). |

### `ComposerScreeningConfiguration`

<sub>`ProcioneMGR/Services/Discovery/StrategyComposer.cs`</sub>

> Screening + fixed-parameter walk-forward settings (mirrors the hunt gates).

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `ExchangeName` | `string` | `"Binance"` | — |
| `Symbol` | `string` | `string.Empty` | — |
| `Timeframe` | `string` | `string.Empty` | — |
| `From` | `DateTime` | `—` | — |
| `To` | `DateTime` | `—` | — |
| `InitialCapital` | `decimal` | `10_000m` | — |
| `SlippagePercent` | `decimal` | `0.05m` | — |
| `FeePercent` | `decimal` | `0.1m` | Commissione per lato (%) — allineata ai default di PipelineCosts (Bitget, conservativa). |
| `FundingRatePercentPer8h` | `decimal` | `0.01m` | Funding dei perpetual (%/8h) — allineato ai default di PipelineCosts; era assente (0). |
| `MinScreenSharpe` | `decimal` | `0.3m` | Selection-range gates before the walk-forward confirmation. |
| `MinTrades` | `int` | `12` | — |
| `ConfirmTopN` | `int` | `5` | How many screened specs per series get the walk-forward confirmation. |
| `OosWindowMonths` | `int` | `2` | Fixed-parameter walk-forward: evaluate on rolling OOS windows of this many months. |
| `MinOosSharpe` | `decimal` | `0.3m` | — |

### `CorrelatedExposureOptions`

<sub>`ProcioneMGR/Services/Risk/CorrelatedExposureGuard.cs`</sub>

> Opzioni del limite di esposizione correlata. Default SPENTO.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | Interruttore generale. Default FALSE: la funzione va prima calibrata sui dati delle corsie realmente attive, perché una soglia scelta male non protegge — paralizza. |
| `MaxCorrelatedExposurePercent` | `decimal` | `50m` | Tetto dell'esposizione correlata netta, in % del capitale aggregato delle corsie nella stessa modalità. Il default coincide col tetto di esposizione totale per singola corsia ( ): un insieme di posizioni che si muovono all'unisono non deve poter superare il l… |
| `MinCorrelationToCount` | `double` | `0.5d` | Sotto questa correlazione (in valore assoluto) una posizione è trattata come indipendente e non contribuisce. Serve a non accumulare rumore da decine di correlazioni spurie piccole. |
| `Timeframe` | `string` | `"1h"` | Timeframe delle barre su cui si stima la correlazione. |
| `LookbackBars` | `int` | `720` | Numero di barre della finestra di stima (720 barre 1h ≈ 30 giorni). |
| `MinOverlappingBars` | `int` | `100` | Barre in comune minime perché una correlazione sia considerata stimabile. |
| `CacheTtl` | `TimeSpan` | `TimeSpan.FromHours(6)` | Validità della correlazione in cache: oltre, si ricalcola. |

### `CpcvConfiguration`

<sub>`ProcioneMGR/Services/Optimization/OptimizationModels.cs`</sub>

> [T1.6 roadmap macchina-ricerca] Configurazione della validazione CPCV per il percorso strategie: invece di UN solo percorso out-of-sample (walk-forward + holdout), C(gruppi, gruppiTest) combinazioni di gruppi contigui → una DISTRIBUZIONE di Sharpe fuori campione per candidato.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Groups` | `int` | `8` | Gruppi temporali contigui in cui dividere la serie. |
| `TestGroups` | `int` | `2` | Gruppi usati come test in ogni combinazione: C(Groups, TestGroups) percorsi. |
| `PurgeBars` | `int` | `—` | Barre rimosse dal train PRIMA di ogni gruppo di test (stessa semantica di CombinatorialPurgedCv). |
| `EmbargoBars` | `int` | `—` | Barre rimosse dal train DOPO ogni gruppo di test. |

### `DatabaseMigrationOptions`

<sub>`ProcioneMGR/Data/DatabaseMigrator.cs`</sub>

> Opzioni della migrazione automatica, sezione Database .

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `AutoMigrate` | `bool` | `true` | Applica le migrazioni pendenti all'avvio. Default TRUE: fino al 2026-08-05 lo schema si applicava solo a mano ( dotnet ef database update ) e una migrazione dimenticata si manifestava come un errore runtime a metà giornata — «relation … does not exist» — inve… |
| `LockTimeoutSeconds` | `int` | `120` | Secondi di attesa per il lock: oltre, si rinuncia e si dichiara (un altro host sta migrando). |

### `DecayMonitorOptions`

<sub>`ProcioneMGR/Services/Monitoring/StrategyDecayMonitor.cs`</sub>

> Soglie del monitor di decadimento. Stessa finestra funge da minimo di trade richiesti e da ampiezza del rolling.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `WindowTradeCount` | `int` | `20` | Quante delle ultime operazioni chiuse considerare (e minimo richiesto prima di poter valutare). |
| `AlertThresholdRatio` | `decimal` | `0.5m` | Sotto questa frazione di RealizedSharpe/ExpectedSharpe scatta l'alert (default 50%). |

### `DigestOptions`

<sub>`ProcioneMGR/Services/Notifications/DailyDigest.cs`</sub>

> [AF5.4] Il digest giornaliero, sezione Notifications:Digest . Default SPENTO. L'ora è quella LOCALE della macchina (il PC del proprietario): il digest serve a un umano che si sveglia, non a un cron UTC.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | — |
| `Hour` | `int` | `7` | — |
| `Minute` | `int` | `30` | — |
| `NarrativeEnabled` | `bool` | `—` | [G9] Un paragrafo di sintesi in italiano, scritto dal provider AI attivo, SOPRA i dati strutturati. Default off. Additivo per costruzione: se l'AI è spenta, senza chiave, in breaker o fuori budget, il digest esce identico a come uscirebbe senza questa opzione… |

### `DriftMonitorOptions`

<sub>`ProcioneMGR/Services/Monitoring/Drift/FeatureDriftWorker.cs`</sub>

> Opzioni del (sezione config "Drift"). Default safe-off.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | Master switch. Default false: il worker si spegne subito, il drift resta valutabile on-demand dalla UI. |
| `IntervalHours` | `int` | `6` | Cadenza di valutazione automatica (ore). |
| `RecentCandles` | `int` | `200` | Quante candele recenti usare come campione "corrente". |
| `RetireChampionOnAlert` | `bool` | `true` | Ciclo chiuso (Fase 2): quando un modello Champion va in drift, ritiralo dal registry e accoda un retrain. Default true (il worker è comunque opt-in). Il retrain NON è automatico — si marca soltanto la richiesta per l'operatore. Nessun impatto sul trading Live. |
| `MinAlertsToRetire` | `int` | `1` | Numero minimo di feature in Alert per far scattare il ritiro del Champion. |

### `EnsembleComparatorOptions`

<sub>`ProcioneMGR/Services/Ensemble/EnsembleComparator.cs`</sub>

> Tunable thresholds for (bound from the EnsembleComparator config section).

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `MinSharpeImprovementPercent` | `decimal` | `10m` | Minimum weighted-Sharpe improvement (percent of the incumbent) required to replace — the hysteresis band. |
| `MinRiskFactorImprovementPercent` | `decimal` | `15m` | Minimum Monte-Carlo RiskFactor95 improvement (percent, lower is better) that can justify a swap on its own when Sharpe is not worse. |
| `MinLegs` | `int` | `2` | A candidate with fewer surviving legs than this is rejected outright (too thin to deploy). |
| `MinDistinctSymbols` | `int` | `2` | A candidate covering fewer distinct symbols than this is rejected outright (not diversified enough). |
| `MinSharpeSignificanceZ` | `decimal` | `1.0m` | Minimo z-score di significatività statistica del vantaggio di Sharpe del candidato sull'incumbent, oltre alla soglia percentuale di isteresi. Un miglioramento percentuale grande su un campione piccolo è rumore: pretendere che sia anche significativo evita di … |

### `EnsembleConfiguration`

<sub>`ProcioneMGR/Services/Ensemble/EnsembleModels.cs`</sub>

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `ExchangeName` | `string` | `"Binance"` | — |
| `Symbol` | `string` | `"BTC/USDT"` | — |
| `Timeframe` | `string` | `"1h"` | — |
| `TotalCapital` | `decimal` | `10000m` | — |
| `RebalanceIntervalDays` | `int` | `7` | — |
| `SharpeRollingDays` | `int` | `30` | — |
| `MinAllocationPercent` | `decimal` | `5m` | — |
| `MaxAllocationPercent` | `decimal` | `40m` | — |
| `SharpeShrinkage` | `decimal` | `0.5m` | Intensità dello shrinkage degli Sharpe verso l'equipeso prima dell'allocazione (0..1). Le stime di Sharpe sono rumorose: 0 = puro Sharpe-weighting (comportamento storico Fase 6), 1 = equipeso puro. Default 0.5 (metà fiducia agli scarti dalla media): su dati O… |
| `MinSharpeObservations` | `int` | `20` | Numero minimo di osservazioni (punti di equity) perché lo Sharpe di una gamba sia ritenuto affidabile; sotto la soglia la gamba è portata all'equipeso. 0 = disattivo. |
| `Strategies` | `List&lt;EnsembleStrategy&gt;` | `new()` | — |
| `IsEnabled` | `bool` | `—` | — |
| `RiskProfileName` | `string?` | `—` | [R3] Nome del profilo di rischio della Modalità Semplice (vedi Services.Risk.RiskProfiles ). Quando è valorizzato, le soglie di sicurezza EFFETTIVE della corsia sono quelle del profilo sovrapposte a quelle globali ( ). null o vuoto = la corsia NON usa la Moda… |
| `IsFutures` | `bool` | `—` | True per operare su Futures perpetui a leva invece che Spot. Campo primitivo (non l'enum MarketType di Services.Trading) per evitare una dipendenza incrociata Ensemble→Trading, dato che Trading già dipende da Ensemble (IEnsembleManager). |
| `Leverage` | `int` | `1` | Leva richiesta se IsFutures=true (ignorata per lo Spot). Va sotto SafetyConfiguration.MaxLeverageAllowed. |
| `RegimeAwareWeighting` | `bool` | `—` | Se true la pesatura è "regime-aware": peso = 0.6·Sharpe rolling (norm) + 0.4·perf nel regime corrente (norm). Se false usa solo lo Sharpe rolling (comportamento Fase 6). |
| `ExpectedRiskFactor95` | `decimal` | `—` | RiskFactor95 Monte-Carlo aggregato dell'ensemble al momento del deploy (dalla PipelineRecommendation.RiskLimits ), memorizzato qui perché il confronto "corrente vs candidato" del ciclo di ri-applica automatica ( ) possa valutare anche il rischio dell'ensemble… |

### `FactorCacheOptions`

<sub>`ProcioneMGR/Services/Alpha/FactorCache.cs`</sub>

> Opzioni della cache dei fattori (sezione config "FactorCache").

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `MaxEntries` | `int` | `512` | Numero massimo di serie memorizzate; oltre, si sfrattano le più vecchie (FIFO). Default 512. |

### `FleetOptions`

<sub>`ProcioneMGR/Services/Fleet/FleetModels.cs`</sub>

> [AF2] Opzioni dell'orchestratore di flotta (sezione Fleet ). Default: SPENTO, e anche da acceso parte in DryRun (solo journal, zero azioni) — l'ordine degli incrementi è parte del contratto: prima si osserva il journal per giorni, poi si toglie il dry-run apposta.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | — |
| `DryRun` | `bool` | `true` | Finché è true (default), l'orchestratore DECIDE e SCRIVE il journal ma non esegue nulla. |
| `TickMinutes` | `int` | `15` | Cadenza del tick in minuti. |
| `RetireSharpeThreshold` | `decimal` | `—` | Sharpe realizzato sotto cui un forward test è un perdente da ritirare. |
| `RetireMinWeeks` | `int` | `3` | Settimane minime di osservazione prima che un ritiro sia un giudizio e non rumore. |
| `RetireMinTrades` | `int` | `20` | Trade minimi prima che un ritiro sia un giudizio e non rumore. |
| `RetireConfirmTicks` | `int` | `2` | Tick CONSECUTIVI in cui il verdetto di ritiro deve ripetersi prima di agire (isteresi: uno Sharpe che oscilla attorno alla soglia non deve produrre stop a raffica). |
| `MaxAssignmentsPerTick` | `int` | `1` | Assegnazioni massime per tick (prudenza: una alla volta, il tick dopo si rivaluta). |
| `MinTradesPerMonth` | `decimal` | `1m` | Trade/mese minimi dichiarati (derivati dall'holdout) perché un candidato entri in coda. Preferenza del proprietario: intraday/swing breve — un candidato che non dichiara la sua frequenza non entra affatto. |
| `CandidateMaxAgeDays` | `int` | `14` | Età massima (giorni) di un run perché sia ancora un candidato fresco. |
| `MaxLanesWithoutExposureGuard` | `int` | `3` | Oltre questo numero di corsie ATTIVE, l'orchestratore rifiuta nuove assegnazioni se il limite di esposizione correlata ( Trading:CorrelatedExposure ) è spento: una flotta larga senza guardia trasversale è concentrazione di rischio non misurata. |
| `CarrySilenceAlertHours` | `int` | `24` | Notifica se il worker del carry è abilitato ma non decide da più di queste ore. |
| `UseCommittee` | `bool` | `—` | [AF3] Consulta il comitato AI sui PAREGGI (più candidati idonei della stessa assegnazione). Default false; richiede anche Committee:Enabled . Il comitato sceglie SOLO dentro il menù che il core ha già validato: una risposta invalida ricade sul default determi… |

### `HeartbeatOptions`

<sub>`ProcioneMGR/Services/Health/HostHeartbeats.cs`</sub>

> [AF5.1] Configurazione dell'heartbeat incrociato. Default SPENTO: a config vuota nessun host scrive né sorveglia, comportamento identico a prima della fase (invariante di piattaforma). Sezione Heartbeat , hot-reload via IOptionsMonitor.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | — |
| `WriteSeconds` | `int` | `60` | Cadenza di scrittura del proprio battito. |
| `StaleMinutes` | `int` | `10` | Dopo quanti minuti senza battito l'ALTRO host è dichiarato muto. Molto maggiore del periodo di scrittura (10× di default): un tick perso per rumore di rete non deve allarmare nessuno. |

### `LaneInvariantOptions`

<sub>`ProcioneMGR/Services/Trading/LaneInvariantOptions.cs`</sub>

> Soglie del watchdog di invarianti contabili per corsia (Fase 0-A3, PRD Autonomia Operativa), sezione Trading:LaneInvariants . Le soglie sono LASCHE apposta: il watchdog non duplica il pre-ordine (che resta il freno fine), è un tripwire per stati contabili ASSURDI che nessun percorso legittimo può produrre — come il caso reale della corsia 2 (PnL -1,8M su capitale 10k con leva 2). Hot-reload via IOptionsMonitor.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Default ON: è un freno di sicurezza, spegnerlo è la scelta che va motivata. |
| `CheckIntervalSeconds` | `int` | `60` | Cadenza del check (letta all'avvio del worker; cambiarla richiede riavvio, come PromotionWorker). |
| `AvailableCapitalTolerance` | `decimal` | `1m` | ε in valuta: AvailableCapital sotto -ε è una violazione (mai negativo oltre l'arrotondamento). |
| `MaxAbsPnlCapitalMultiple` | `decimal` | `2m` | k: \|PnL totale (realizzato + non realizzato)\| oltre k × TotalCapital × Leverage è una violazione. |
| `MaxExposureCapitalMultiple` | `decimal` | `2m` | Nozionale aperto complessivo oltre questo multiplo di TotalCapital × Leverage è una violazione. |

### `LiquidationsOptions`

<sub>`ProcioneMGR/Services/MarketData/LiquidationSyncWorker.cs`</sub>

> Configurazione dell'accumulo liquidazioni (sezione "Liquidations").

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Default ON: stream pubblico keyless in sola lettura, e il dato NON è ricostruibile a posteriori — ogni giorno spento è un giorno di storia perso (stessa logica dell'accumulo OI/long-short del Sentiment 2.0). Spegnibile per gli ambienti dove non serve. |
| `FlushMinutes` | `int` | `5` | Minuti fra due flush su DB. |
| `StaleSeconds` | `int` | `900` | Secondi senza messaggi oltre i quali il canale si considera guasto. 900s (15 min), NON 120: trovato girando dal vivo la prima notte — !forceOrder@arr è un feed di EVENTI sparsi (le liquidazioni di TUTTO il listino), e in mercato calmo i vuoti di 120s sono NOR… |
| `BlockedRetryMinutes` | `int` | `60` | [2026-07-24] Minuti di pausa quando l'endpoint futures risulta bloccato (connesso ma muto — vedi il ramo endpointLikelyBlocked). Lungo di proposito: evita il churn di riconnessione a vuoto quando i dati non arriveranno mai da questa postazione. Testabile picc… |

### `LiveExecutionOptions`

<sub>`ProcioneMGR/Services/Trading/LiveExecutionOptions.cs`</sub>

> Opzioni dell'esecuzione live "a fette" (TWAP/VWAP/Iceberg su Testnet/Live). Sezione config Trading:LiveExecution . Letta via (hot-reload): è un interruttore di sicurezza, deve poter essere spento senza riavviare l'app. Default safe-off, come ogni automazione della piattaforma.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | Master switch. Default false: nessun piano di esecuzione viene mai creato o avanzato. |
| `DefaultWindowMinutes` | `int` | `5` | Finestra di esecuzione di default (minuti) se la strategia non ne specifica una propria. |
| `WorkerTickSeconds` | `int` | `15` | Cadenza del worker che avanza le fette dovute. |
| `AbandonGraceMinutes` | `int` | `5` | Grazia oltre la finestra prima di dichiarare abbandonate le fette non piazzabili. |

### `LlmBudgetOptions`

<sub>`ProcioneMGR/Services/Llm/LlmUsage.cs`</sub>

> [AF1] Opzioni di consumo e budget del layer AI, sezione Llm:Budget . TUTTO spento per default (invariante di piattaforma): senza non si scrive una riga e non si applica alcun tetto — comportamento bit-identico a prima della fase. I limiti a 0 significano "nessun tetto". Il budget è il freno al cost runaway: coi free tier di oggi para i loop impazziti, con un domani a pagamento para la bolletta.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `TrackingEnabled` | `bool` | `—` | — |
| `DailyCallLimit` | `int` | `—` | Tetto di CHIAMATE al giorno (0 = nessuno). Conta ogni chiamata servita, di ogni path. |
| `DailyTokenLimit` | `int` | `—` | Tetto di token (prompt+completion) al giorno (0 = nessuno). |
| `MonthlyTokenLimit` | `int` | `—` | Tetto di token nel mese solare UTC (0 = nessuno). |

### `LlmOptions`

<sub>`ProcioneMGR/Services/Llm/AnthropicLlmClient.cs`</sub>

> Opzioni del layer AI. Le API key NON sono qui: vivono cifrate a database (AiCredentials, gestite da /admin/ai-supervisor) con fallback alle variabili d'ambiente ( ANTHROPIC_API_KEY , NVIDIA_API_KEY ) — vedi .

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | — |
| `Provider` | `string` | `AiProviders.Nvidia` | Provider attivo del layer AI (una voce di ). Default Nvidia dal 2026-08-02 (Anthropic retrocessa: credito esaurito). Hot-reload: l'instradamento avviene A OGNI chiamata (DelegatingLlmClient), cambiare provider dal pannello non richiede riavvio. |
| `FailoverEnabled` | `bool` | `true` | [Failover 2026-08-02] Se la chiamata al provider attivo fallisce (qualunque errore che non sia una cancellazione), il DelegatingLlmClient prova DA SOLO i provider di questa lista, nell'ordine, saltando quelli senza chiave e il provider già tentato. Default on… |
| `FailoverProviders` | `List&lt;string&gt;` | `[]` | Catena di failover, nell'ordine di tentativo; VUOTA = catena di default ( ). Il default sta in una costante e NON qui: il binder di configurazione APPENDE gli elementi dell'array alla lista già inizializzata invece di sostituirla — con un default popolato la … |
| `DefaultFailoverChain` | `IReadOnlyList&lt;string&gt;` | `—` | La catena di default quando è vuota. |
| `Model` | `string` | `"claude-opus-4-8"` | — |
| `NvidiaModel` | `string` | `"meta/llama-3.3-70b-instruct"` | Modello per il provider Nvidia (namespace/modello del catalogo build.nvidia.com). |
| `NvidiaBaseUrl` | `string` | `"https://integrate.api.nvidia.com/v1"` | Endpoint OpenAI-compatible del provider Nvidia. Parametrico DI PROPOSITO: qualunque piattaforma esponga lo stesso contratto (OpenRouter, endpoint self-hosted, …) potrà entrare cambiando URL e chiave, senza un client nuovo. |
| `GeminiModel` | `string` | `"models/gemini-3.6-flash"` | Modello per Google Gemini (layer OpenAI-compatible di Generative Language API). Id CANONICO col prefisso "models/" come lo restituisce l'elenco dell'API (verificato dal vivo 2026-08-02); il 2.5 è ritirato per le chiavi nuove — usare «Scarica modelli» nel pann… |
| `GeminiBaseUrl` | `string` | `"https://generativelanguage.googleapis.…` | — |
| `GroqModel` | `string` | `"llama-3.3-70b-versatile"` | Modello per Groq (inferenza a bassa latenza su modelli aperti). |
| `GroqBaseUrl` | `string` | `"https://api.groq.com/openai/v1"` | — |
| `HuggingFaceModel` | `string` | `"meta-llama/Llama-3.3-70B-Instruct"` | Modello per il router HuggingFace (org/nome del catalogo; il router sceglie il backend). |
| `HuggingFaceBaseUrl` | `string` | `"https://router.huggingface.co/v1"` | — |
| `MaxTokens` | `int` | `4096` | — |
| `PollIntervalMinutes` | `int` | `5` | — |
| `RequestTimeoutSeconds` | `int` | `60` | Timeout COMPLESSIVO della chiamata, tutti i tentativi di failover compresi (il SDK da solo aspetterebbe fino a 10 minuti). |
| `PerProviderTimeoutSeconds` | `int` | `25` | [2026-08-05] Budget di tempo del SINGOLO provider dentro la catena di failover. Scaduto questo, il provider è considerato appeso e si passa al prossimo anello. Perché esiste : senza, un provider che si appende — il modo più comune in cui un provider gratuito … |
| `BreakerFailureThreshold` | `int` | `3` | Errori transitori consecutivi dopo i quali il breaker sospende le chiamate. |
| `BreakerCooldownMinutes` | `int` | `30` | Minuti tra i probe automatici a breaker aperto (il ripristino è autonomo). |
| `NotifyDecisions` | `bool` | `—` | Notifica (Info) quando un'advisory riuscita contiene decisioni per l'utente. Default off. |
| `ComparisonEnabled` | `bool` | `—` | [Fase C] Secondo parere: dopo ogni advisory riuscita, chiede la STESSA analisi anche al provider di confronto e la salva accanto (artifact separato, mai al posto). Default off: raddoppia il costo per run, e va scelto apposta. |
| `ComparisonProvider` | `string` | `AiProviders.Groq` | Provider del secondo parere (una voce di ). Default Groq (attivo default = Nvidia; due pareri dallo stesso provider non confrontano niente e si saltano da soli). |
| `ExplainRejections` | `bool` | `—` | [G6] Spiegazione in prosa dei candidati BOCCIATI, prodotta dal worker dopo l'advisory. Default off: è una chiamata in più per run, e va scelta apposta. Spento NON significa niente spiegazione: il riassunto DETERMINISTICO delle bocciature (quanti candidati per… |
| `ExplainRejectionsTopN` | `int` | `Narration.RejectionDigestBuilder.Defaul…` | [G6] Quanti candidati bocciati riportare per esteso (i conteggi per causa coprono sempre tutti). |

### `MlComparisonOptions`

<sub>`ProcioneMGR/Services/ML/MlComparisonOptions.cs`</sub>

> Opzioni del dual-read ML (Fase 2a, sezione config "Ml"). Il confronto col servizio remoto è puramente OSSERVATIVO: non influenza mai una decisione di trading.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | Accende il confronto (hot-reload via IOptionsMonitor). deve comunque essere valorizzato a startup perché il client gRPC venga registrato (cambiarlo richiede riavvio). |
| `RemoteUrl` | `string?` | `—` | Indirizzo del servizio procionemgr-ml. Vuoto → client non registrato, confronto spento. |
| `TimeoutMs` | `int` | `300` | Deadline della chiamata gRPC di confronto (ms). Stretto: è solo osservabilità. |

### `ModelRegistryOptions`

<sub>`ProcioneMGR/Services/Registry/ModelRegistry.cs`</sub>

> Opzioni del registry (sezione config "Registry").

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `MinChampionDeflatedSharpe` | `double` | `—` | Deflated Sharpe minimo perché un modello possa diventare Champion, anche se non c'è un Champion in carica da battere. Default 0: non blocca il primo Champion, ma il gate "batti l'incumbent" resta sempre attivo. Alzabile (es. 0.95) per pretendere significativi… |

### `NotificationOptions`

<sub>`ProcioneMGR/Services/Notifications/INotifier.cs`</sub>

> Opzioni del canale di notifica, sezione Notifications . Default OFF.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | Default false: nessuna notifica finché l'operatore non abilita esplicitamente. |
| `Provider` | `string` | `"Logging"` | "Logging" (default) \| "Telegram". |
| `ChatId` | `string` | `string.Empty` | Chat id Telegram di destinazione (il token del bot NON va in config: env TELEGRAM_BOT_TOKEN). |
| `MaxPerHour` | `int` | `20` | Rate-limit: massimo di messaggi recapitati per ora (finestra scorrevole); l'eccesso viene coalizzato. |

### `OptimizationConfiguration`

<sub>`ProcioneMGR/Services/Optimization/OptimizationModels.cs`</sub>

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `ExchangeName` | `string` | `string.Empty` | — |
| `Symbol` | `string` | `string.Empty` | — |
| `Timeframe` | `string` | `string.Empty` | — |
| `From` | `DateTime` | `—` | — |
| `To` | `DateTime` | `—` | — |
| `InitialCapital` | `decimal` | `10000m` | — |
| `CommissionPercent` | `decimal` | `0.1m` | — |
| `SlippagePercent` | `decimal` | `Pipeline.PipelineCosts.DefaultSlippageP…` | [R2] Attrito sfavorevole applicato a OGNI fill, in % del prezzo. Prima non esisteva su questo modello e BuildBacktestConfig non lo impostava: l'intera SELEZIONE dei parametri (e, a cascata, quella dei candidati di Discovery) girava a sole commissioni, mentre … |
| `PositionSizePercent` | `decimal` | `100m` | % del capitale impegnata per trade durante l'ottimizzazione. |
| `StrategyName` | `string` | `string.Empty` | — |
| `ParameterRanges` | `List&lt;ParameterRange&gt;` | `new()` | — |
| `WalkForward` | `WalkForwardConfiguration` | `new()` | — |
| `SelectionMetric` | `OptimizationSelectionMetric` | `OptimizationSelectionMetric.InSampleSha…` | Come selezionare i parametri della finestra. Default = in-sample (corretto). |
| `SearchStrategy` | `SearchStrategy` | `SearchStrategy.GridSearch` | Strategia di ricerca. Default = GridSearch (comportamento storico bit-identico). |
| `BayesianIterations` | `int` | `40` | Ramo Bayesian: passi guidati (Expected Improvement) DOPO l'esplorazione iniziale, per finestra. |
| `BayesianInitialRandom` | `int` | `8` | Ramo Bayesian: punti iniziali casuali (esplorazione), per finestra. |
| `BayesianSeed` | `int` | `42` | Ramo Bayesian: seme — la ricerca è deterministica a parità di seme e di storia. |

### `PairsBacktestConfiguration`

<sub>`ProcioneMGR/Services/PairsTrading/PairsBacktestModels.cs`</sub>

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `SymbolY` | `string` | `string.Empty` | — |
| `SymbolX` | `string` | `string.Empty` | — |
| `InitialCapital` | `decimal` | `10_000m` | — |
| `PositionSizePercent` | `decimal` | `10m` | % del capitale corrente impegnata per GAMBA (dollar-neutral: stesso notional su Y e X). |
| `FeePercent` | `decimal` | `0.1m` | Commissione per lato, in percentuale del notional di ciascuna gamba. |
| `LookbackWindow` | `int` | `90` | Ampiezza della finestra (barre) usata per ristimare l'hedge ratio ad ogni ricalibrazione. |
| `RecalibrationInterval` | `int` | `30` | Ogni quante barre ristimare l'hedge ratio (walk-forward, mai barre future). |
| `ZScoreLookback` | `int` | `20` | Finestra per lo z-score rolling causale dello spread. |
| `EntryZScore` | `decimal` | `2.0m` | \|z\| oltre questa soglia apre la posizione (spread anomalo). |
| `ExitZScore` | `decimal` | `0.5m` | \|z\| sotto questa soglia chiude la posizione (spread rientrato). |
| `StopZScore` | `decimal` | `3.5m` | STOP DI DIVERGENZA: \|z\| AVVERSO oltre questa soglia forza l'uscita in perdita (il classico blow-up del pairs — lo spread può divergere all'infinito). Deve essere &gt; . 0 = disattivo (sconsigliato con denaro vero). Default 3.5. |
| `MaxHoldBars` | `int` | `—` | Stop temporale: chiude la posizione dopo questo numero di barre se non è ancora rientrata (0 = disattivo). |
| `SlippagePercent` | `decimal` | `—` | Slippage sfavorevole (%) applicato al fill di OGNI gamba, in entrata e in uscita (0 = fill teorici). |
| `MaxSpreadVolRatio` | `decimal` | `—` | [E1] FILTRO DI VOLATILITÀ dello spread. Salta l'apertura di una nuova posizione quando la volatilità RECENTE dello spread (finestra ) supera di questo rapporto la sua volatilità di BASE (finestra ): è il regime in cui la relazione si sta rompendo e la mean-re… |
| `SpreadVolBaselineWindow` | `int` | `120` | Finestra di base della volatilità dello spread per il filtro (vedi ). |
| `HedgeRatioEstimator` | `PairsHedgeRatioEstimator` | `PairsHedgeRatioEstimator.Kalman` | [C2] Estimatore dell'hedge ratio. Default per esito del gate C2 MISURATO (2026-07-26, fase `pairs 1d` di PlatformExpand, holdout 2026-03-01→oggi sulle 5 coppie operabili in selezione): spread OOS più stazionario in 5/5 (mediana ΔADF −0,98, stabile con δ da 1e… |
| `KalmanDelta` | `double` | `KalmanPairsSpreadAnalyzer.DefaultDelta` | [C2] δ del filtro di Kalman (rumore di stato, adimensionale). Vedi . |

### `PipelineConfiguration`

<sub>`ProcioneMGR/Services/Pipeline/PipelineEntities.cs`</sub>

> A saved, reusable pipeline configuration ("recipe"): universe, date ranges, and the ordered list of stages with their parameters. JSON columns keep the schema stable while stages and parameters evolve (same pattern as EnsembleState / SavedStrategy.ParametersJson).

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Id` | `int` | `—` | — |
| `Name` | `string` | `string.Empty` | — |
| `Description` | `string` | `string.Empty` | — |
| `CreatedBy` | `string` | `string.Empty` | Id of the IdentityUser that owns the configuration. |
| `CreatedAt` | `DateTime` | `—` | — |
| `UpdatedAt` | `DateTime` | `—` | — |
| `ExchangeName` | `string` | `"Binance"` | Exchange the whole pipeline reads data from. |
| `UniverseJson` | `string` | `"[]"` | JSON: List&lt;SeriesSpec&gt;. |
| `DateRangesJson` | `string` | `"{}"` | JSON: PipelineDateRanges. |
| `StagesJson` | `string` | `"[]"` | JSON: List&lt;StageConfig&gt;. |
| `InitialCapital` | `decimal` | `10_000m` | — |
| `Seed` | `int` | `42` | Seed for deterministic runs. |
| `ExecutionMode` | `string` | `"Paper"` | "Paper" \| "Live" \| "Disabled". Live never bypasses SafetyChecker / manual confirms. |
| `Schedule` | `string?` | `—` | Standard 5-field cron expression (e.g. "0 3 * * *" = every day at 03:00 UTC), evaluated by . Null/empty = no automatic schedule. |
| `ScheduleEnabled` | `bool` | `—` | Master on/off switch for automatic scheduling, independent of whether is set — lets the user pause automation without losing the expression. |
| `NextRunAt` | `DateTime?` | `—` | Next due UTC timestamp per , maintained by . Null means "due now" (never scheduled yet, or schedule just changed) — the worker computes a real |

### `PostMortemOptions`

<sub>`ProcioneMGR/Services/Llm/Narration/PostMortemService.cs`</sub>

> [G4] Opzioni del post-mortem, sezione PostMortem . Default SPENTO.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | Accende la scrittura dei post-mortem. Spento: nessuna riga, nessuna chiamata AI. |
| `LossThresholdPercent` | `decimal` | `1.0m` | Perdita percentuale oltre la quale un trade merita un post-mortem (valore POSITIVO: 1.0 = perdite oltre l'1%). Sotto soglia si tace: non ogni perdita è una lezione. Interazione da conoscere : la causa deterministica «costi che hanno mangiato il lordo» può sca… |
| `UseAi` | `bool` | `—` | Chiede anche la prosa e la classificazione all'AI. Spento = solo le cause calcolabili dal codice. |
| `MaxPerRun` | `int` | `5` | Quanti post-mortem al massimo per giro, per non trasformare un arretrato in una bolletta. |
| `CommitteeContextCount` | `int` | `5` | Quanti post-mortem recenti passare al comitato come contesto (0 = non passarne). |

### `PromotionEvaluatorOptions`

<sub>`ProcioneMGR/Services/Trading/PromotionEvaluator.cs`</sub>

> Soglie della promozione/retrocessione automatica delle corsie (sezione di config PromotionEvaluator ).

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `MinSharpeRealized` | `decimal` | `0.8m` | — |
| `MinTradeCount` | `int` | `30` | — |
| `MaxDrawdownPercent` | `decimal` | `15m` | — |
| `MinObservationWeeks` | `int` | `3` | — |
| `MinWinRate` | `decimal` | `0.45m` | — |
| `AutoPromoteToTestnet` | `bool` | `true` | Se true il PromotionWorker promuove davvero (Paper→Testnet); se false valuta soltanto (la UI mostra "pronto"). |
| `NotifyOnPromotion` | `bool` | `true` | Scrive una voce di audit visibile all'utente a ogni promozione/retrocessione. |
| `HardMaxDrawdownPercent` | `decimal` | `20m` | Blocco assoluto: una corsia con drawdown oltre questa soglia non viene MAI promossa, anche se il resto è ottimo. |
| `AutoDemoteToPaper` | `bool` | `true` | — |
| `DemoteSharpeThreshold` | `decimal` | `0.5m` | — |
| `DemoteMinWeeks` | `int` | `2` | — |
| `AutoDemoteLiveToTestnet` | `bool` | `—` | Se true, una corsia LIVE degradata viene retrocessa a Testnet (mai a Paper diretto). Default false. |
| `DemoteLiveDryRun` | `bool` | `true` | Finché è true (default), la retrocessione Live si ANNUNCIA soltanto (WouldDemoteLive + reason DRY-RUN), senza agire. |
| `DemoteLiveSharpeThreshold` | `decimal` | `—` | Sharpe realizzato sotto cui la corsia Live è considerata degradata. |
| `DemoteLiveMaxDrawdownPercent` | `decimal` | `15m` | Drawdown oltre cui la corsia Live è considerata degradata, a prescindere dallo Sharpe. |
| `DemoteLiveMinWeeks` | `int` | `1` | Storia minima (settimane) prima che il degrado di una Live sia un giudizio e non rumore. |
| `DemoteLiveMinTrades` | `int` | `10` | Trade minimi prima che il degrado di una Live sia un giudizio e non rumore. |
| `EvaluationIntervalHours` | `int` | `6` | Ogni quante ore il PromotionWorker rivaluta le corsie. |

### `ProtectiveExitShadowOptions`

<sub>`ProcioneMGR/Services/Trading/ProtectiveExitShadow.cs`</sub>

> Opzioni della sentinella.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Spegnibile: la sentinella osserva e basta, ma osservare a ogni tick ha un costo e chi non la vuole deve poterla togliere senza toccare il codice. |
| `AlertAboveBps` | `double` | `200d` | Sopra questo costo in punti base si allerta, sul SINGOLO evento. 200 bps (2%) non è una stima: è la soglia oltre la quale un caso solo vale più di una media, perché a quel punto non si sta più misurando l'effetto dell'ombra sullo stop ma un salto di prezzo de… |

### `RealtimeFeedOptions`

**Sezione appsettings:** `MarketData:Realtime`

<sub>`ProcioneMGR/Services/MarketData/RealtimeMarketDataModels.cs`</sub>

> Configurazione del feed real-time, sezione MarketData:Realtime di appsettings.json. I default sono pensati per essere INERTI: a feature spenta il comportamento della piattaforma è identico a prima del feed.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `SectionName` | `string` | `"MarketData:Realtime"` | — |
| `Enabled` | `bool` | `—` | Interruttore generale. DEFAULT FALSE: il feed è additivo rispetto alla sincronizzazione REST già esistente, quindi spegnerlo riporta esattamente al comportamento a sole candele. |
| `DriveProtectiveExits` | `bool` | `true` | Se true i tick alimentano le uscite protettive del motore. Separato da apposta: permette di tenere il feed acceso in sola OSSERVAZIONE (log e metriche, nessuna decisione) per convincersi che i prezzi siano sani prima di dargli potere di chiudere posizioni. St… |
| `SubscriptionRefreshSeconds` | `int` | `30` | Ogni quanto rileggere le corsie per aggiornare l'insieme delle sottoscrizioni. |
| `StaleAfterSeconds` | `int` | `60` | Silenzio oltre il quale il feed è considerato STALE. Non blocca nulla (la sincronizzazione REST resta comunque attiva e indipendente), ma smette di essere considerato una fonte viva e genera un allarme: non si opera mai credendo di avere prezzi aggiornati qua… |
| `ReconnectInitialDelayMs` | `int` | `1_000` | Attesa iniziale prima di un tentativo di riconnessione. |
| `ReconnectMaxDelayMs` | `int` | `60_000` | Tetto dell'attesa di riconnessione (backoff esponenziale con jitter). |
| `MaxSpreadPercent` | `decimal` | `2m` | Vedi : oltre questo spread il tick è scartato. |

### `RegimeRoutingOptions`

<sub>`ProcioneMGR/Services/Regime/LaneRegimeRouter.cs`</sub>

> Opzioni del router di regime. Default SPENTO.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | Interruttore generale. Default FALSE: prima di dare a un modello K-means il potere di spegnere una strategia dal vivo, quel potere va guadagnato in validazione. |
| `DriveDecisions` | `bool` | `—` | Separa l' osservare dal decidere , come già fa il feed real-time con DriveProtectiveExits . Default FALSE: acceso ma non decidente, il router classifica il regime a ogni candela e ne registra i cambi, senza impedire nulla. Non è prudenza generica, è la rispos… |
| `AllowUnmappedRegimes` | `bool` | `true` | Politica per i regimi senza regola. Default TRUE (permissivo): un regime nuovo — o un modello riaddestrato con più cluster di quanti ne conosca la configurazione — non deve zittire la corsia di soppiatto. Il caso "non so" e il caso "so che qui non si opera" s… |
| `MinCandles` | `int` | `60` | Candele minime in memoria perché la classificazione sia tentata. |
| `ModelCheckTtl` | `TimeSpan` | `TimeSpan.FromMinutes(5)` | Per quanto tempo si riusa l'esito della verifica "esiste un modello attivo per questa serie" senza rinterrogare il database. Il compromesso è dichiarato: attivare un modello nuovo da /regimes impiega fino a questo tempo a farsi sentire sul router, in cambio d… |
| `Rules` | `List&lt;RegimeRoutingRule&gt;` | `[]` | — |

### `RegimeTriggerOptions`

<sub>`ProcioneMGR/Services/Pipeline/RegimeChangeDetector.cs`</sub>

> Opzioni del trigger contestuale (Fase 2, PRD Autonomia §5), sezione RegimeTrigger .

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Default ON: il trigger è additivo e parla SOLO col planner, che ha già il suo gate ( Campaign:Enabled default OFF) — senza campagne abilitate non succede nulla. |
| `CheckIntervalMinutes` | `int` | `30` | Cadenza del check (letta all'avvio del worker). |
| `CooldownHours` | `int` | `6` | Cooldown tra due wake (PRD: default 6h): il regime non "cambia" ogni mezz'ora. |
| `VolBandMultiple` | `double` | `1.5` | Banda di volatilità: scatta se la realized esce da [forecast/k, forecast×k] rispetto al forecast GARCH dell'ultimo run (PRD: es. realized &gt; 1,5× forecast — l'espansione attesa su SOL; la compressione oltre banda è a sua volta un cambio di contesto). |

### `SafetyConfiguration`

<sub>`ProcioneMGR/Services/Trading/SafetyConfiguration.cs`</sub>

> Limiti di sicurezza del trading. Bindato da appsettings.json sezione "Trading:Safety". I default sono CONSERVATIVI: in caso di config mancante il sistema resta prudente.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `MaxPositionSizePercent` | `decimal` | `10m` | Max % del capitale totale per una singola posizione. |
| `PositionSizePercent` | `decimal` | `8m` | % del capitale impiegata per ogni apertura: NOZIONALE investito per lo Spot (leva implicita 1x), MARGINE isolato per i Futures — il nozionale reale è margine × leva, stessa semantica del motore di backtest. Coerenza richiesta (validata a StartAsync): Position… |
| `MaxTotalExposurePercent` | `decimal` | `50m` | Max % del capitale totale impegnata complessivamente in posizioni aperte. |
| `MaxDailyLossPercent` | `decimal` | `5m` | Stop trading se la perdita giornaliera supera questa % del capitale. |
| `MaxDrawdownPercent` | `decimal` | `20m` | Stop trading se il drawdown supera questa %. |
| `MaxOpenPositions` | `int` | `5` | Numero massimo di posizioni aperte contemporaneamente. |
| `MinOrderIntervalSeconds` | `int` | `10` | Intervallo minimo (secondi) tra un ordine e il successivo (anti-spam). |
| `RequireManualConfirmationForLive` | `bool` | `true` | Se true, ogni ordine in modalità Live richiede conferma manuale dell'operatore. |
| `VolatilityTargetingEnabled` | `bool` | `—` | Se true, viene moltiplicato per un fattore che punta a una volatilità costante: meno capitale esposto quando il mercato si agita, di più quando si calma. È l'unico risultato di ricerca sopravvissuto al controllo a esposizione media costante. Default FALSE. |
| `TargetAnnualVolatilityPercent` | `decimal` | `30m` | Volatilità annualizzata a cui puntare (%). Sotto questo valore si espone di più, sopra di meno. |
| `VolatilityLookbackBars` | `int` | `30` | Barre usate per stimare la volatilità realizzata. 30 è il valore validato dalla ricerca. |
| `MinExposureMultiplier` | `decimal` | `0.25m` | Pavimento del moltiplicatore: sotto questo non si scende, per non annullare del tutto l'operatività. |
| `MaxExposureMultiplier` | `decimal` | `1.0m` | Tetto del moltiplicatore. Default 1,0 di proposito: così il dosaggio può solo RIDURRE la dimensione rispetto a , mai aumentarla, e accendere la funzione non può violare né . Alzarlo sopra 1,0 toglie questa garanzia. |
| `MaxLeverageAllowed` | `int` | `5` | Leva massima consentita per il trading Futures (default CONSERVATIVO: con un capitale piccolo la leva alta è attraente ma la crescita del rischio non è lineare — vedi , che tipicamente sconsiglia oltre 3-5x anche per sistemi con un edge reale). L'utente può a… |
| `MaintenanceMarginPercent` | `decimal` | `0.5m` | Margine di mantenimento in % del nozionale, usato per la STIMA locale del prezzo di liquidazione ( ) quando l'exchange non la riporta ancora (es. subito dopo il fill, o in modalità Paper). Stessa convenzione e stesso default del motore di backtest ( BacktestC… |
| `UseExchangeRestingStops` | `bool` | `—` | [P0-5] Se true, all'apertura di una posizione FUTURES in Testnet/Live il motore piazza sull'exchange ordini TRIGGER reduce-only (stop-market e take-profit-market) come protezione "resting": restano validi sull'exchange anche se il processo va giù o perde conn… |
| `MaxFillPriceDeviationPercent` | `decimal` | `20m` | [B1] Banda massima (± % dal prezzo corrente di mercato) entro cui il prezzo di fill riportato dall'exchange è considerato plausibile. Fuori banda (o ≤ 0) il fill è SOSPETTO e non viene mai adottato: vedi e il bug B1 in docs/TEST-UI-2026-07-18.md (testnet che … |
| `MaxFillQuantityDeviationPercent` | `decimal` | `5m` | [B1] Tolleranza massima (± % dalla quantità RICHIESTA) entro cui la quantità di fill riportata dall'exchange è considerata plausibile. Fuori tolleranza (es. quantità cumulative 100x dal testnet, bug B1) il fill è SOSPETTO e non viene mai adottato. NB delibera… |
| `FeePercent` | `decimal` | `0.1m` | [P2-8] Fee dell'exchange in % del nozionale, applicata sia in apertura sia in chiusura. Prima era una costante fissa in TradingEngine (stesso valore di default, 0.1%), scollegata dal fee reale e dal parametro equivalente e già configurabile del backtest ( Bac… |

### `SentimentOptions`

<sub>`ProcioneMGR/Services/Sentiment/SentimentOptions.cs`</sub>

> Opzioni di Sentiment 2.0 (sezione Sentiment ): raccolta delle serie di market mood (Fear & Greed + derivati Binance, API pubbliche senza chiave), composite con z-score e retention. Hot-reload via IOptionsMonitor (editabile da /admin/autonomy); gli INTERVALLI del worker si leggono al boot (PeriodicTimer) e richiedono riavvio.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `true` | Worker di raccolta. Default ON: sole GET pubbliche a cadenza modesta, e le serie Binance esistono solo per 30 giorni — i buchi sono irrecuperabili. |
| `MetricsIntervalMinutes` | `int` | `30` | Cadenza del fetch delle metriche (minuti). Richiede riavvio. |
| `NewsIntervalMinutes` | `int` | `60` | Cadenza del sync delle notizie RSS/calendario/retail (minuti). Richiede riavvio. |
| `Symbols` | `List&lt;string&gt;` | `["BTCUSDT", "ETHUSDT"]` | Mercati Binance USDS-M osservati (formato exchange, es. BTCUSDT). |
| `NewsRetentionDays` | `int` | `180` | Retention delle notizie (AltDataPoints), giorni. |
| `MetricRetentionDays` | `int` | `400` | Retention delle serie metriche, giorni (la fonte FearGreed è ESENTE: è il baseline lungo, ~2500 righe totali). |
| `BaselineDays` | `int` | `30` | Finestra del baseline per gli z-score, giorni. |
| `ExtremeZScore` | `double` | `2.0` | \|z\| oltre cui una metrica è "estrema" (flag contrarian). |
| `FearGreedExtremeLow` | `int` | `20` | Fear & Greed ≤ questa soglia = extreme fear (flag contrarian). |
| `FearGreedExtremeHigh` | `int` | `80` | Fear & Greed ≥ questa soglia = extreme greed (flag contrarian). |
| `WeightNews` | `double` | `0.20` | — |
| `WeightFearGreed` | `double` | `0.25` | — |
| `WeightFunding` | `double` | `0.20` | — |
| `WeightLongShort` | `double` | `0.20` | — |
| `WeightTaker` | `double` | `0.15` | — |
| `EnableMlFeature` | `bool` | `—` | Opt-in: rende il fattore "Sentiment" disponibile come feature ML (AlphaFactorFactory). Default OFF: il sentiment entra nei modelli solo per scelta esplicita dell'operatore. |
| `ScorerProvider` | `string` | `SentimentScorerProviders.Keyword` | Scorer delle notizie: "Keyword" (default, lessicale, zero costi), "Llm" (provider AI attivo del layer multi-provider — sceglierlo è il consenso esplicito al costo per chiamata) o "Onnx" (inferenza locale del pilota). Hot-reload via DelegatingSentimentScorer; … |
| `OnnxModelPath` | `string` | `Path.Combine("models", "sentiment-pilot…` | Percorso del modello ONNX del pilota sentiment (relativo al content root se non assoluto). Il file NON sta nel repository (è un artefatto addestrato, cartella gitignored): si genera dal pannello in /sentiment. |

### `StrategyDiscoveryConfiguration`

<sub>`ProcioneMGR/Services/Discovery/StrategyDiscoveryModels.cs`</sub>

> Configurazione della ricerca di strategie: spazza un universo di (strategia × coppia × timeframe) e, per ciascuna, ottimizza i parametri in walk-forward.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `ExchangeName` | `string` | `"Binance"` | — |
| `Symbols` | `List&lt;string&gt;` | `new()` | — |
| `Timeframes` | `List&lt;string&gt;` | `new()` | — |
| `Strategies` | `List&lt;string&gt;` | `new()` | Nomi strategia da provare (vuoto = tutte quelle disponibili). |
| `From` | `DateTime` | `—` | — |
| `To` | `DateTime` | `—` | — |
| `InitialCapital` | `decimal` | `10000m` | — |
| `CommissionPercent` | `decimal` | `0.1m` | — |
| `SlippagePercent` | `decimal` | `Pipeline.PipelineCosts.DefaultSlippageP…` | [R2] Attrito per fill propagato all'ottimizzatore. Vedi per il motivo per cui il default è onesto e non zero. |
| `WalkForward` | `WalkForwardConfiguration` | `new()` | — |
| `TopN` | `int` | `20` | Quante candidate restituire (ordinate per Sharpe out-of-sample). |

### `SupervisorAgentOptions`

<sub>`ProcioneMGR/Services/Agents/IPipelineSupervisorAgent.cs`</sub>

> Options for the supervisor agent (bound from the PipelineSupervisor config section).

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Provider` | `string` | `"Logging"` | "Logging" (default, no AI) or "Claude" (uses the existing ILlmClient / ANTHROPIC_API_KEY). |
| `TimeoutSeconds` | `int` | `30` | Hard timeout for a single Claude analysis; on timeout the agent falls back to "approve" (defer to metrics). |

### `TrainingConfiguration`

<sub>`ProcioneMGR/Services/Regime/RegimeModels.cs`</sub>

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `ExchangeName` | `string` | `"Binance"` | — |
| `Symbol` | `string` | `"BTC/USDT"` | — |
| `Timeframe` | `string` | `"1h"` | — |
| `From` | `DateTime` | `—` | — |
| `To` | `DateTime` | `—` | — |
| `NumberOfRegimes` | `int` | `4` | — |
| `MaxIterations` | `int` | `100` | — |
| `AutoSelectK` | `bool` | `—` | Se true, K non è fisso: si addestra il K-means per ogni K in [ .. ] e si sceglie quello col Silhouette Score migliore (auto-selezione di K). viene aggiornato al K scelto. Se false si usa così com'è (comportamento storico). |
| `MinRegimes` | `int` | `2` | Estremo inferiore del range di K per l'auto-selezione (min 2). Usato solo se . |
| `MaxRegimes` | `int` | `6` | Estremo superiore del range di K per l'auto-selezione. Usato solo se . |
| `IncludeVolumeFeature` | `bool` | `—` | [3.8a] Quinta feature di clustering: VolumeRatio (volume / media 20 periodi). Default OFF = comportamento storico bit-identico. ATTENZIONE dichiarata: accenderla CAMBIA le etichette dei regimi del modello riaddestrato — l'impatto sull'allocazione regime-aware… |
| `IncludeBreadthFeature` | `bool` | `—` | [3.8a/4.9] Sesta feature di clustering: breadth interna (% dei simboli /USDT sopra la propria SMA50 — "quanti partecipano al movimento"). Default OFF; stessa avvertenza del volume. Richiede dati multi-simbolo sullo stesso timeframe (il calcolo è di IMarketBre… |

### `VolatilityTargetingOptions`

<sub>`ProcioneMGR/Services/Backtesting/BacktestModels.cs`</sub>

> Dosaggio della posizione sulla volatilità realizzata, con la stessa semantica e gli stessi default del trading dal vivo ( SafetyConfiguration ): serve a poter MISURARE l'effetto sui propri dati prima di accenderlo. Spento di default = comportamento invariato.

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `Enabled` | `bool` | `—` | — |
| `TargetAnnualVolatilityPercent` | `decimal` | `30m` | — |
| `LookbackBars` | `int` | `30` | — |
| `MinExposureMultiplier` | `decimal` | `0.25m` | — |
| `MaxExposureMultiplier` | `decimal` | `1.0m` | 1,0 = il dosaggio può solo ridurre la size, mai aumentarla. Vedi VolatilityScaler . |

### `WalkForwardConfiguration`

<sub>`ProcioneMGR/Services/Optimization/OptimizationModels.cs`</sub>

| Opzione | Tipo | Default | Descrizione |
|---|---|---|---|
| `InSampleMonths` | `int` | `12` | — |
| `OutOfSampleMonths` | `int` | `3` | — |
| `StepMonths` | `int` | `3` | — |
| `EmbargoBars` | `int` | `—` | [T0.1 roadmap macchina-ricerca] Barre di cuscinetto SALTATE all'inizio di ogni finestra out-of-sample. Senza, IS e OOS sono contigui ( oosStart = isEnd ) e l'informazione filtra attraverso il confine: una posizione aperta a fine IS prosegue nell'OOS, un indic… |

---

## 2. Schema dati — 34 tabelle

| `DbSet` | Entità |
|---|---|
| `AiCredentials` | `AiCredential` |
| `AltDataPoints` | `AltDataPoint` |
| `DriftCheckResults` | `DriftCheckResult` |
| `EnsembleRebalanceHistory` | `EnsembleRebalanceHistory` |
| `EnsembleStates` | `EnsembleState` |
| `ExchangeCredentialCiphertexts` | `ExchangeCredentialCiphertext` |
| `ExchangeCredentials` | `ExchangeCredential` |
| `ExecutionJobs` | `ExecutionJob` |
| `ExperimentArtifacts` | `ExperimentArtifact` |
| `ExperimentRuns` | `ExperimentRun` |
| `FactorIcWindows` | `FactorIcWindow` |
| `HostHeartbeats` | `HostHeartbeat` |
| `LaneQuarantines` | `LaneQuarantine` |
| `LlmUsageRecords` | `LlmUsageRecord` |
| `OhlcvData` | `OhlcvData` |
| `OpenPositions` | `OpenPosition` |
| `OrchestratorDecisions` | `OrchestratorDecision` |
| `Orders` | `Order` |
| `PipelineArtifacts` | `PipelineArtifact` |
| `PipelineConfigurations` | `PipelineConfiguration` |
| `PipelineRuns` | `PipelineRun` |
| `ProtectiveExitShadows` | `ProtectiveExitShadow` |
| `RegimeModels` | `RegimeModel` |
| `SavedFactors` | `SavedFactor` |
| `SavedMlModels` | `SavedMlModel` |
| `SavedStrategies` | `SavedStrategy` |
| `SentimentMetricPoints` | `SentimentMetricPoint` |
| `TrackedSeries` | `TrackedSeries` |
| `TradePostMortems` | `TradePostMortem` |
| `TradeRecords` | `TradeRecord` |
| `TradingAuditLogs` | `TradingAuditLog` |
| `TradingEngineStates` | `TradingEngineState` |
| `UserPageConfigs` | `UserPageConfig` |
| `VettingCampaigns` | `VettingCampaign` |

### `AiCredential`

<sub>`ProcioneMGR/Data/AiCredential.cs`</sub>

> Chiave API di un provider AI, cifrata a riposo (AES-256-GCM via converter — stesso pattern di ). Una riga per provider, a livello di PIATTAFORMA e non per-utente: il layer AI (supervisione, e gli usi futuri) è un servizio della piattaforma, come i worker che lo eseguono. La variabile d'ambiente resta il fallback per chi non vuole la chiave a database (vedi AiKeyStore : DB prima, env poi).

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `Provider` | `string` | "Anthropic" \| "Nvidia" \| domani altri: stringa e non enum, un provider nuovo non deve richiedere una migrazione. |
| `ApiKey` | `string` | Cifrata a riposo dal converter. |
| `UpdatedAtUtc` | `DateTime` | — |

### `AltDataPoint`

<sub>`ProcioneMGR/Data/AltDataPoint.cs`</sub>

> Un elemento di dato alternativo (cap. 3): oggi solo notizie via RSS, pensata per essere generica (stesso spirito di TrackedSeries per l'OHLCV) così da poter accogliere in futuro altre fonti (social, on-chain) senza cambiare schema.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `TimestampUtc` | `DateTime` | Data di pubblicazione (dalla fonte), UTC. |
| `Source` | `string` | "CoinDesk" \| "Cointelegraph" \| "TheBlock" \| "Decrypt" \| ... |
| `Title` | `string` | — |
| `Summary` | `string?` | — |
| `Url` | `string?` | — |
| `Category` | `string` | "Regulatory" \| "Security" \| "Institutional" \| "Other" — da NewsImpactClassifier . |
| `SymbolsJson` | `string` | Simboli rilevanti individuati nel testo (JSON array di stringhe, es. ["BTC","ETH"]). |
| `SentimentScore` | `decimal?` | Punteggio di sentiment in [-1,+1], null finché non calcolato da un ISentimentScorer . |
| `DedupeKey` | `string` | Chiave univoca per evitare duplicati fra sync successive dello stesso feed (Source+Url). |

### `DriftCheckResult`

<sub>`ProcioneMGR/Services/Monitoring/Drift/DriftModels.cs`</sub>

> ENTITÀ EF (tabella DriftCheckResults ): esito PERSISTITO di un check di drift su un modello, una riga per modello per tick del — anche quando è tutto pulito, così l'assenza di righe si distingue da "il worker non sta girando". Prima di questa tabella gli esiti vivevano solo nei log: la UI (/admin/autonomy) non poteva mostrare né l'ultimo esito né lo storico. Prune automatico oltre i 90 giorni nel worker.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `CheckedAtUtc` | `DateTime` | Quando è stato eseguito il check (UTC). |
| `ModelId` | `int` | Id del SavedMlModel valutato. NON è FK: la riga sopravvive alla cancellazione del modello. |
| `ModelName` | `string` | Nome del modello, denormalizzato per leggibilità storica. |
| `Symbol` | `string` | — |
| `Timeframe` | `string` | — |
| `TotalFeatures` | `int` | Feature totali valutate; 0 = check saltato (es. candele recenti insufficienti). |
| `DriftingFeatures` | `int` | Feature con drift (Warning o Alert). |
| `AlertFeatures` | `int` | Feature in Alert (sottoinsieme di ). |
| `Overall` | `DriftSeverity` | Gravità complessiva del check (max tra le feature). |
| `TopFeaturesJson` | `string?` | Top-5 feature in drift, JSON [{"name","severity","detector","score"}] — abbastanza per la tabella in UI senza persistire l'intero report per-feature. |
| `ChampionRetired` | `bool` | True se QUESTO check ha fatto ritirare un Champion (ciclo chiuso del registry). |

### `EnsembleRebalanceHistory`

<sub>`ProcioneMGR/Data/EnsembleState.cs`</sub>

> Storico dei rebalancing dell'ensemble.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `LaneId` | `int` | Corsia di trading isolata (0 = corsia di default). |
| `Timestamp` | `DateTime` | — |
| `AllocationsJson` | `string` | JSON di List&lt;RebalanceAllocation&gt;. |
| `Reason` | `string` | — |

### `EnsembleState`

<sub>`ProcioneMGR/Data/EnsembleState.cs`</sub>

> Stato persistito dell'ensemble (configurazione + ultimo status), riga singola. I payload sono serializzati in JSON per non vincolare lo schema a strutture in evoluzione.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `LaneId` | `int` | Corsia di trading isolata (0 = corsia di default, esistente prima del supporto multi-coppia). |
| `ConfigurationJson` | `string` | JSON di EnsembleConfiguration. |
| `StatusJson` | `string` | JSON dell'ultimo EnsembleStatus calcolato. |
| `LastUpdatedUtc` | `DateTime` | — |

### `ExchangeCredential`

<sub>`ProcioneMGR/Data/ExchangeCredential.cs`</sub>

> Credenziali API di un exchange, appartenenti a un singolo utente. SICUREZZA: , e sono cifrati a riposo via EncryptedStringConverter (AES-256-GCM) configurato nel . Sul DB non compaiono mai in chiaro.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `UserId` | `string` | FK verso AspNetUsers (IdentityUser). |
| `User` | `ApplicationUser?` | — |
| `ExchangeName` | `ExchangeName` | — |
| `Label` | `string` | Etichetta leggibile scelta dall'utente, es. "Binance Main". |
| `ApiKey` | `string` | Cifrata a riposo. |
| `ApiSecret` | `string` | Cifrato a riposo. |
| `Passphrase` | `string?` | Cifrata a riposo. Obbligatoria per Bitget, nulla/assente altrove. |
| `IsTestnet` | `bool` | — |
| `CreatedAt` | `DateTime` | — |
| `MaskedApiKey` | `string` | ApiKey mascherata per la UI (mai esporre il secret). |

### `ExchangeCredentialCiphertext`

<sub>`ProcioneMGR/Data/ExchangeCredentialCiphertext.cs`</sub>

> Proiezione KEYLESS di sola lettura sulla tabella ExchangeCredentials che espone il CIPHERTEXT così com'è sul DB (nessun EncryptedStringConverter). Serve ai percorsi che devono sopravvivere a una riga cifrata con una master key diversa da quella del processo corrente (bug B2, docs/TEST-UI-2026-07-18.md): col converter la decifratura avviene DENTRO la materializzazione EF, quindi una sola riga indecifrabile (AuthenticationTagMismatchException) abbatteva l'intera query — e con essa la pagina /settings/exchanges o l'avvio Testnet/Live. Qui il ciphertext arriva intatto e la decifratura è per-riga, in memoria: vedi . Mappata con ToView sulla tabella esistente: nessuna tabella nuova, nessuna migra…

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `UserId` | `string` | — |
| `ExchangeName` | `ExchangeName` | — |
| `Label` | `string` | — |
| `ApiKey` | `string` | Base64 del payload AES-GCM, NON decifrato. |
| `ApiSecret` | `string` | Base64 del payload AES-GCM, NON decifrato. |
| `Passphrase` | `string?` | Base64 del payload AES-GCM, NON decifrato. Null dove non usata (Binance). |
| `IsTestnet` | `bool` | — |
| `CreatedAt` | `DateTime` | — |

### `ExecutionJob`

<sub>`ProcioneMGR/Services/Trading/ExecutionJobModels.cs`</sub>

> Un piano di esecuzione live di un'apertura di posizione, distribuita in fette nel tempo reale (TWAP/VWAP/Iceberg) su Testnet/Live (rif. docs/archive/ROADMAP-QLIB.md §1.2 ). Una riga per corsia, persistita così che un piano sopravviva a un riavvio del processo e sia ispezionabile in UI. Le fette vivono in (blob, stesso pattern di PipelineRun.StageSummariesJson ): poche fette per job, pochi job attivi per corsia — nessun vantaggio relazionale a questo volume. INVARIANTE: solo le APERTURE guidate da segnale diventano un ExecutionJob; ogni chiusura resta sempre immediata. Un job viene creato SOLO dopo che la prima fetta ha effettivamente creato la posizione, così è sempre valido per i job Runni…

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `Guid` | — |
| `LaneId` | `int` | Corsia di trading isolata (0 = corsia di default). |
| `StrategyId` | `string` | — |
| `PositionId` | `string` | PositionId della posizione aperta/accresciuta da questo piano (chiave di correlazione). |
| `Symbol` | `string` | — |
| `MarketType` | `MarketType` | — |
| `Side` | `OrderSide` | — |
| `TotalQuantity` | `decimal` | Quantità totale prevista dal piano. |
| `FilledQuantity` | `decimal` | Quantità effettivamente riempita finora (somma dei fill delle fette). |
| `EntryPriceWeightedAvg` | `decimal` | Prezzo medio ponderato di ingresso della posizione dopo i fill accumulati. |
| `Algorithm` | `string` | "Twap" \| "Vwap" \| "Iceberg" (mai "Immediate": quello non genera un job). |
| `WindowSeconds` | `int` | Ampiezza della finestra di esecuzione, in secondi. |
| `Status` | `string` | "Running" \| "Completed" \| "Cancelled" \| "Failed". |
| `CreatedAtUtc` | `DateTime` | — |
| `CompletedAtUtc` | `DateTime?` | — |
| `FailureReason` | `string?` | — |
| `ArrivalPrice` | `decimal` | Prezzo di arrivo/decisione (t0) del piano, per il calcolo dell'implementation shortfall alla chiusura del job. NON persistito (solo osservabilità, in-memory per la durata del job): un job che sopravvive a un riavvio lo ricarica a 0 e la metrica di slippage vi… |
| `SlicesJson` | `string` | JSON: List&lt;ExecutionJobSlice&gt; (le fette del piano con il loro stato). |

### `ExperimentArtifact`

<sub>`ProcioneMGR/Services/Experiments/ExperimentEntities.cs`</sub>

> Artefatto voluminoso associato a un run (equity curve, lista trade, importanze feature, ...), tenuto FUORI dalla riga del run così la tabella storica resta veloce da interrogare — stesso principio di PipelineArtifact .

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `RunId` | `Guid` | — |
| `KindTag` | `string` | Etichetta del tipo di artefatto ("EquityCurve" \| "FeatureImportance" \| ...). |
| `PayloadJson` | `string` | — |
| `CreatedAt` | `DateTime` | — |

### `ExperimentRun`

<sub>`ProcioneMGR/Services/Experiments/ExperimentEntities.cs`</sub>

> Un run sperimentale : la registrazione osservabile e confrontabile di UN'esecuzione di ricerca (backtest, sweep di ottimizzazione, training ML, campagna di discovery, pipeline...). Generalizza il tracking che finora esisteva SOLO per il Pipeline a 15 stadi ( PipelineRun / PipelineArtifact ): stesso pattern a colonne JSON (schema stabile mentre parametri/metriche evolvono), ma disaccoppiato da un singolo consumatore. Non sostituisce PipelineRun (il cui checkpoint per-stadio è un bisogno diverso): il Pipeline può SCRIVERE in aggiunta un di kind "Pipeline" per comparire nella stessa tabella comparativa degli altri (comporre, non sostituire). Rif. docs/archive/ROADMAP-QLIB.md §1.3 .

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `Guid` | — |
| `Kind` | `string` | "Backtest" \| "Optimization" \| "MlTraining" \| "Discovery" \| "Pipeline" \| "AlphaMining". |
| `Name` | `string` | Etichetta leggibile scelta dal chiamante (es. "LightGBM · BTCUSDT · 50 fattori"). |
| `Status` | `string` | "Running" \| "Completed" \| "Failed". |
| `CreatedBy` | `string` | Id dell'utente che ha avviato il run (vuoto per run automatici/di sistema). |
| `Symbol` | `string?` | Symbol principale del run, denormalizzato per il filtro della UI (nullable). |
| `Timeframe` | `string?` | Timeframe principale del run, denormalizzato per il filtro della UI (nullable). |
| `StartedAt` | `DateTime` | — |
| `CompletedAt` | `DateTime?` | — |
| `ParametersJson` | `string` | JSON dei parametri/configurazione del run (shape libera decisa dal chiamante). |
| `ParametersHash` | `string` | Hash SHA-256 (hex) di : versioning "git-like" leggero per riconoscere run con configurazione identica. NON è un content-addressable store completo (scelta dichiarata: complessità non giustificata qui, vedi ROADMAP-QLIB §1.3). |
| `MetricsJson` | `string` | JSON: dizionario nome→valore (decimal) delle metriche finali del run. |
| `ErrorLog` | `string?` | — |

### `FactorIcWindow`

<sub>`ProcioneMGR/Services/Alpha/FactorIcHistory.cs`</sub>

> ENTITÀ EF (tabella FactorIcWindows ): l'IC di UN fattore su UNA finestra di UNA serie. La riga è l'osservazione elementare della deriva: la serie storica è l'insieme delle righe ordinate per . L'indice unico su (serie, fattore, orizzonte, ampiezza, fine finestra) rende la scrittura IDEMPOTENTE: il worker gira ogni 12 ore sulle stesse candele e ricalcola le stesse finestre — senza quel vincolo la tabella crescerebbe di un duplicato per giro, per sempre.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `Symbol` | `string` | Serie di appartenenza (es. "BTC/USDT"). |
| `Timeframe` | `string` | Timeframe della serie (es. "1h"). |
| `FactorName` | `string` | Nome del fattore, come lo espone IAlphaFactor.Name . |
| `ForwardHorizon` | `int` | Orizzonte del rendimento forward su cui l'IC è stato misurato (in barre). |
| `WindowSize` | `int` | Ampiezza della finestra in osservazioni. Fa parte della chiave logica perché un IC su 500 osservazioni e uno su 2000 sono misure DIVERSE: il pavimento di rumore è 1,96/√n, quindi mescolarle nella stessa serie confronterebbe numeri con soglie diverse. |
| `WindowStartUtc` | `DateTime` | — |
| `WindowEndUtc` | `DateTime` | — |
| `InformationCoefficient` | `double` | IC di Spearman sulla finestra. |
| `ComputedAtUtc` | `DateTime` | Quando questa riga è stata calcolata (UTC). Serve a distinguere una storia viva da una ferma. |

### `HostHeartbeat`

<sub>`ProcioneMGR/Data/HostHeartbeat.cs`</sub>

> [AF5.1] Battito di vita di un host, una riga per processo. Il guscio scrive la SUA riga, il motore la SUA: ogni scrittore ha esattamente una riga, quindi la regola "ogni scrittore ha esattamente un host" vale a grana di riga — nessuna contesa, nessun lock. Il punto: se muore il motore, il guscio se ne accorge dagli errori gRPC ma nessuno lo DICE; se muore il guscio, il motore continua a tradare senza occhi e nessuno se ne accorge affatto. Ogni host legge la riga ALTRUI e dichiara la stantiezza (vedi HeartbeatMonitorWorker). Il caso "muoiono entrambi" non è coperto da qui per costruzione: per quello esiste il watchdog esterno (scripts/watchdog.ps1) e l'assenza del digest giornaliero.

| Campo | Tipo | Descrizione |
|---|---|---|
| `ShellRole` | `string` | — |
| `EngineRole` | `string` | — |
| `Host` | `string` | Chiave: il ruolo dell'host ("shell" \| "engine"). |
| `LastUtc` | `DateTime` | Ultimo battito (UTC). La riga non si cancella mai: si aggiorna. |
| `Version` | `string` | Versione informativa dell'assembly che batte, per la diagnostica dei deploy. |

### `LaneQuarantine`

<sub>`ProcioneMGR/Services/Trading/TradingEntities.cs`</sub>

> Quarantena di una corsia (Fase 0-A3, PRD Autonomia Operativa): riga inserita dal quando un invariante contabile risulta violato (es. il caso reale della corsia 2: PnL -1,8M su capitale 10k da fill patologici). Finché la riga esiste, TradingEngine.StartAsync RIFIUTA di riavviare la corsia: un nuovo StartAsync azzererebbe capitale/PnL cancellando l'evidenza da esaminare. La rimozione è un'azione umana esplicita (/trading, solo Admin) dopo verifica. Tabella separata da proprio perché StartAsync RIGENERA quella riga da zero: un flag lì sopra non sopravvivrebbe.

| Campo | Tipo | Descrizione |
|---|---|---|
| `LaneId` | `int` | Chiave naturale: al più una quarantena attiva per corsia. |
| `CreatedAtUtc` | `DateTime` | — |
| `Reason` | `string` | Invarianti violati, leggibile per l'operatore (banner in /trading). |
| `DetailsJson` | `string` | JSON con i valori osservati al momento della violazione (stato contabile + soglie). |

### `LlmUsageRecord`

<sub>`ProcioneMGR/Data/LlmUsageRecord.cs`</sub>

> [AF1] Consumo LLM aggregato per giorno/provider/modello/percorso. AGGREGATO e non a eventi di proposito: alla scala reale (decine di chiamate l'ora nei giorni pieni) una riga per chiamata sarebbe rumore da amministrare; una riga per combinazione al giorno resta leggibile per anni. Scritto solo dal LlmUsageFlushWorker del guscio (l'unico host col layer AI).

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `DayUtc` | `DateTime` | Giorno UTC (mezzanotte) a cui il consumo appartiene. |
| `Provider` | `string` | Provider che ha SERVITO le chiamate (minuscolo, una voce di AiProviders.Known). |
| `Model` | `string` | — |
| `Path` | `string` | Percorso del guard ("advisory" \| "veto" \| "sentiment" \| "committee" \| "direct"). |
| `Calls` | `int` | — |
| `PromptTokens` | `long` | — |
| `CompletionTokens` | `long` | — |

### `OhlcvData`

<sub>`ProcioneMGR/Data/OhlcvData.cs`</sub>

> Una candela OHLCV (Open/High/Low/Close/Volume) di mercato. Questa tabella e' progettata per ospitare ENORMI volumi time-series (storico di mercato), in netto contrasto con le poche righe delle tabelle Identity. Per questo motivo: - prezzi in (precisione esatta, niente errori float); - volume in (gestisce sia asset interi che frazionari/crypto); - timestamp in UTC ( ) per coerenza globale; - indice composto Univoco (Symbol, Timeframe, TimestampUtc) configurato via Fluent API nel per query time-series veloci e per impedire candele duplicate.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `long` | Chiave surrogata. long perche' la tabella crescera' oltre i limiti di int. |
| `Symbol` | `string` | Strumento di mercato, es. "BTCUSDT", "AAPL". |
| `Timeframe` | `string` | Intervallo della candela, es. "1m", "5m", "1h", "1d". |
| `TimestampUtc` | `DateTime` | Apertura della candela in UTC (Unix epoch normalizzato a DateTime UTC). |
| `Open` | `decimal` | — |
| `High` | `decimal` | — |
| `Low` | `decimal` | — |
| `Close` | `decimal` | — |
| `Volume` | `decimal` | Volume scambiato nel periodo. |
| `QuoteVolume` | `decimal?` | Controvalore scambiato (quote asset, es. USDT). Binance k[7], Bitget k[6]. Null = non raccolto. |
| `TradeCount` | `long?` | Numero di trade nella candela (Binance k[8]). Abilita dimensione media del trade e trade-bars. Null = non raccolto. |
| `TakerBuyVolume` | `decimal?` | Volume base comprato da TAKER (Binance k[9]): l'order flow aggressivo — chi attraversa lo spread. L'imbalance TakerBuyVolume/Volume è la feature order-flow di T3.8b. Null = non raccolto. |
| `TakerBuyQuoteVolume` | `decimal?` | Controvalore comprato da taker (Binance k[10]). Null = non raccolto. |

### `OpenPosition`

<sub>`ProcioneMGR/Services/Trading/TradingModels.cs`</sub>

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `LaneId` | `int` | Corsia di trading isolata (0 = corsia di default). |
| `PositionId` | `string` | — |
| `StrategyId` | `string` | — |
| `Symbol` | `string` | — |
| `Side` | `OrderSide` | — |
| `EntryPrice` | `decimal` | — |
| `Quantity` | `decimal` | — |
| `StopLoss` | `decimal?` | — |
| `TakeProfit` | `decimal?` | — |
| `TrailingStopPercent` | `decimal?` | Trailing stop in %, applicato automaticamente dall'EnsembleStrategy o impostato a mano. Il livello effettivo si ricalcola ogni candela da (vedi TradingEngine.ProcessCandleAsync ), sullo stesso schema causale del motore di backtest (livello calcolato sul best … |
| `BestPriceSinceEntry` | `decimal?` | Massimo (long) / minimo (short) toccato dal prezzo dall'apertura, per il trailing stop. Null finché il trailing non è attivo. |
| `OpenedAtUtc` | `DateTime` | — |
| `CurrentPrice` | `decimal` | — |
| `UnrealizedPnl` | `decimal` | — |
| `UnrealizedPnlPercent` | `decimal` | — |
| `ExchangeOrderId` | `string?` | — |
| `OpenedInMode` | `TradingMode` | Modalità in cui la posizione è stata APERTA. Discriminatore anti-mescolamento (M2): al cambio di modalità della corsia (promozione/retrocessione), EnsureLoadedAsync carica solo le righe della modalità corrente e PURGA le altre — una posizione simulata Paper n… |
| `StopOrderId` | `string?` | [P0-5] Id (clientOrderId) degli ordini TRIGGER reduce-only piazzati sull'exchange come protezione "resting" (stop-market / take-profit-market), quando è attivo (default OFF). PERSISTITI (M3): dopo un riavvio la chiusura deve poter cancellare i trigger REALI a… |
| `TakeProfitOrderId` | `string?` | [P0-5] Vedi . |
| `Leverage` | `int` | Leva della posizione (1 per Spot). |
| `LiquidationPrice` | `decimal?` | Prezzo di liquidazione stimato/riportato dall'exchange (solo Futures, null per Spot). In Testnet/Live è la fonte di verità dell'exchange quando disponibile, altrimenti la stima locale via ; in Paper è sempre la stima locale. |
| `MarginBalance` | `decimal` | Margine isolato allocato alla posizione (= Quantity*EntryPrice per lo Spot). |

### `OrchestratorDecision`

<sub>`ProcioneMGR/Data/OrchestratorDecision.cs`</sub>

> [AF2] Il journal della Queen Bee: UNA riga per decisione che porta informazione (assegnazione, ritiro, proposta di fascia grigia, blocco motivato). Persistito perché l'autonomia senza tracciabilità è un racconto: il pannello in /admin/autonomy mostra ESATTAMENTE ciò che l'orchestratore ha deciso, quando, con che motivo e con quale esito — dry-run compreso.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `AtUtc` | `DateTime` | — |
| `Kind` | `string` | "Assign" \| "Retire" \| "ProposeGrey" \| "Blocked". |
| `LaneId` | `int?` | — |
| `RunId` | `Guid?` | — |
| `Source` | `string` | Chi ha scelto: "rules" (il default deterministico) \| "committee" (AF3) \| "default" (comitato fallito → default). |
| `Reason` | `string` | — |
| `VotesJson` | `string` | [AF3] I voti del comitato, uno per provider (JSON). "[]" quando il comitato non è stato interpellato. |
| `Applied` | `bool` | True se l'azione è stata ESEGUITA (false in dry-run o su errore). |
| `DryRun` | `bool` | True se la decisione è stata presa col dry-run acceso (solo journal, mai azione). |
| `Error` | `string?` | — |

### `Order`

<sub>`ProcioneMGR/Services/Trading/TradingModels.cs`</sub>

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `LaneId` | `int` | Corsia di trading isolata (0 = corsia di default). |
| `OrderId` | `string` | — |
| `ClientOrderId` | `string` | Client order id idempotente inviato all'exchange (newClientOrderId/clientOid). |
| `PositionId` | `string` | — |
| `StrategyId` | `string` | — |
| `Symbol` | `string` | — |
| `Side` | `OrderSide` | — |
| `Type` | `OrderType` | — |
| `Quantity` | `decimal` | — |
| `Price` | `decimal?` | Prezzo limite, oppure prezzo di riferimento stimato per i market order (per i safety check). |
| `Status` | `OrderStatus` | — |
| `FilledPrice` | `decimal?` | — |
| `FilledQuantity` | `decimal?` | — |
| `CreatedAtUtc` | `DateTime` | — |
| `FilledAtUtc` | `DateTime?` | — |
| `ExchangeOrderId` | `string?` | — |
| `ErrorMessage` | `string?` | — |
| `Mode` | `TradingMode` | — |
| `MarketType` | `MarketType` | — |
| `Leverage` | `int` | Leva usata per questo ordine (1 per Spot). |
| `ManuallyConfirmed` | `bool` | Conferma manuale dell'operatore (richiesta in Live se abilitata in SafetyConfiguration). |
| `ArrivalPrice` | `decimal?` | [Fase 1] Prezzo di riferimento fissato al momento della DECISIONE, prima di inviare l'ordine all'exchange: il denominatore dell'implementation shortfall. Serve un campo suo perché ha due significati (limite oppure riferimento) e in chiusura veniva sovrascritt… |
| `SubmitLatencyMs` | `int?` | [Fase 1] Millisecondi fra l'invio della richiesta all'exchange e la sua risposta. Include di proposito l'attesa del rate-limiter client-side: è il ritardo che la strategia subisce davvero, non solo quello di rete. |
| `Notional` | `decimal` | Notional stimato dell'ordine (Quantity × Price). |
| `ShortfallBps` | `decimal?` | [Fase 1] Implementation shortfall in punti base, segnato come COSTO (positivo = eseguito peggio del riferimento), stessa convenzione degli ExecutionJob e di ExecutionSimulator. Null quando l'esecuzione non è misurabile. |

### `PipelineArtifact`

<sub>`ProcioneMGR/Services/Pipeline/PipelineEntities.cs`</sub>

> Large per-stage artifacts (equity curves, trade lists, importances) kept OUT of the run row so the history table stays fast to query.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `RunId` | `Guid` | — |
| `StageName` | `string` | — |
| `Kind` | `string` | "EquityCurve" \| "TradeList" \| "FeatureImportance" \| "RegimeProfile" \| ... |
| `PayloadJson` | `string` | JSON payload (shape depends on Kind). |
| `CreatedAt` | `DateTime` | — |

### `PipelineConfiguration`

<sub>`ProcioneMGR/Services/Pipeline/PipelineEntities.cs`</sub>

> A saved, reusable pipeline configuration ("recipe"): universe, date ranges, and the ordered list of stages with their parameters. JSON columns keep the schema stable while stages and parameters evolve (same pattern as EnsembleState / SavedStrategy.ParametersJson).

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `Name` | `string` | — |
| `Description` | `string` | — |
| `CreatedBy` | `string` | Id of the IdentityUser that owns the configuration. |
| `CreatedAt` | `DateTime` | — |
| `UpdatedAt` | `DateTime` | — |
| `ExchangeName` | `string` | Exchange the whole pipeline reads data from. |
| `UniverseJson` | `string` | JSON: List&lt;SeriesSpec&gt;. |
| `DateRangesJson` | `string` | JSON: PipelineDateRanges. |
| `StagesJson` | `string` | JSON: List&lt;StageConfig&gt;. |
| `InitialCapital` | `decimal` | — |
| `Seed` | `int` | Seed for deterministic runs. |
| `ExecutionMode` | `string` | "Paper" \| "Live" \| "Disabled". Live never bypasses SafetyChecker / manual confirms. |
| `Schedule` | `string?` | Standard 5-field cron expression (e.g. "0 3 * * *" = every day at 03:00 UTC), evaluated by . Null/empty = no automatic schedule. |
| `ScheduleEnabled` | `bool` | Master on/off switch for automatic scheduling, independent of whether is set — lets the user pause automation without losing the expression. |
| `NextRunAt` | `DateTime?` | Next due UTC timestamp per , maintained by . Null means "due now" (never scheduled yet, or schedule just changed) — the worker computes a real |

### `PipelineRun`

<sub>`ProcioneMGR/Services/Pipeline/PipelineEntities.cs`</sub>

> One execution of a configuration. The context snapshot is the checkpoint: it is rewritten after every completed stage, so a Failed/Cancelled/Paused run can resume from the last completed stage instead of starting over.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `Guid` | — |
| `ConfigurationId` | `int` | — |
| `StartedAt` | `DateTime` | — |
| `CompletedAt` | `DateTime?` | — |
| `Status` | `string` | "Running" \| "Completed" \| "Failed" \| "Cancelled" \| "Paused". |
| `Trigger` | `string` | "Manual" \| "Scheduled" \| "Campaign" (rotazione del Campaign Planner, Fase 1) \| "Event" (trigger contestuale, Fase 2). |
| `ContextSnapshotJson` | `string` | JSON: the serializable part of PipelineContext (checkpoint, updated per stage). |
| `StageSummariesJson` | `string` | JSON: List&lt;StageSummary&gt; (denormalized copy for fast history queries). |
| `Conclusion` | `string` | Executive conclusion produced by the RecommendationStage. |
| `RecommendationJson` | `string` | JSON: PipelineRecommendation. |
| `ErrorLog` | `string?` | — |

### `ProtectiveExitShadow`

<sub>`ProcioneMGR/Services/Trading/ProtectiveExitShadow.cs`</sub>

> [B3, sentinella] Un confronto COMPLETATO fra il momento in cui il feed real-time avrebbe fatto scattare un'uscita protettiva e il momento in cui il percorso a candele l'ha fatta scattare davvero. Una riga per confronto, scritta solo quando entrambi i lati esistono. Non serve a produrre una media: su tre corsie che fanno una dozzina di trade al mese le osservazioni sono troppo poche perché una mediana significhi qualcosa, e quella domanda è già stata chiusa offline dal replay su migliaia di posizioni (REPORT-B3-EXITLAG-2026-07-28). Serve a vedere il caso SINGOLO che il replay non poteva vedere: un crollo con gap, dove aspettare la chiusura della barra non costa qualche punto base ma una cate…

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `LaneId` | `int` | — |
| `Symbol` | `string` | — |
| `Mode` | `TradingMode` | Modalità della corsia al momento del confronto (mai mescolare Paper e Testnet). |
| `PositionId` | `string` | — |
| `Side` | `OrderSide` | — |
| `EntryPrice` | `decimal` | — |
| `DetectedAtUtc` | `DateTime` | Quando il primo tick ha soddisfatto la condizione di uscita, e a che prezzo. |
| `DetectedPrice` | `decimal` | — |
| `DetectedReason` | `string` | Motivo che sarebbe scattato sul tick ("StopLoss", "TakeProfit", "Liquidation"). |
| `ShadowFillPrice` | `decimal` | Prezzo di riempimento che il tick avrebbe ottenuto, dallo stesso evaluator del motore. |
| `ActualExitAtUtc` | `DateTime` | Quando il percorso a candele ha chiuso davvero, a che prezzo e per quale motivo. |
| `ActualFillPrice` | `decimal` | — |
| `ActualReason` | `string` | — |
| `LeadSeconds` | `double` | Secondi di anticipo del feed sulla scoperta. |
| `DelayCostBps` | `double` | Costo del ritardo in punti base dell'ingresso, orientato sulla posizione: POSITIVO = il feed avrebbe fatto uscire meglio, negativo = aspettare la chiusura è convenuto. Stessa convenzione di , così i due numeri sono confrontabili. |
| `CreatedAtUtc` | `DateTime` | — |

### `RegimeModel`

<sub>`ProcioneMGR/Services/Regime/RegimeModels.cs`</sub>

> Modello di regime addestrato. È anche l'entità EF (persistita nel DB). I centroidi sono nello spazio NORMALIZZATO; per l'inference si standardizza la feature con e si assegna al centroide euclideo più vicino.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `ExchangeName` | `string` | — |
| `Symbol` | `string` | — |
| `Timeframe` | `string` | — |
| `TrainedAtUtc` | `DateTime` | — |
| `TrainingDataFrom` | `DateTime` | — |
| `TrainingDataTo` | `DateTime` | — |
| `NumberOfRegimes` | `int` | — |
| `CentroidsJson` | `string` | JSON: array K × 8 di centroidi normalizzati. |
| `FeatureScalingJson` | `string` | JSON: (mean/std per feature). |
| `RegimeProfilesJson` | `string` | JSON: List&lt;RegimeProfile&gt;. |
| `SilhouetteScore` | `double` | — |
| `IsActive` | `bool` | — |

### `SavedFactor`

<sub>`ProcioneMGR/Data/SavedFactor.cs`</sub>

> Un fattore alpha "minato" (formulaic alpha mining, rif. docs/archive/ROADMAP-QLIB.md §1.7 ) salvato per riuso: l'espressione serializzata + la diagnostica IC su selezione e holdout. L'espressione si ricostruisce in un IAlphaFactor (via AlphaExpressionFactor / IAlphaFactorFactory.Create con nome "expr:…"), quindi è riusabile ovunque come qualunque altro fattore.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `UserId` | `string` | FK verso AspNetUsers. |
| `User` | `ApplicationUser?` | — |
| `Name` | `string` | Etichetta scelta dall'utente. |
| `Expression` | `string` | Espressione alpha serializzata (S-expression), es. Div(Sub($Close,Mean($Close,5)),Std($Close,20)) . |
| `Symbol` | `string` | — |
| `Timeframe` | `string` | — |
| `ForwardHorizon` | `int` | — |
| `SelectionIc` | `double` | IC (Spearman) sul periodo di selezione dove il fattore è stato scelto. |
| `HoldoutIc` | `double?` | IC sull'holdout mai visto: il verdetto onesto (null se non verificato). |
| `Observations` | `int` | — |
| `Size` | `int` | Numero di nodi dell'albero (complessità). |
| `CreatedAtUtc` | `DateTime` | — |

### `SavedMlModel`

<sub>`ProcioneMGR/Data/SavedMlModel.cs`</sub>

> Modello di previsione dei rendimenti ( IReturnPredictor ) addestrato e salvato da un utente in /ml, per riuso senza dover riaddestrare. A differenza di RegimeModel (che salva solo i parametri numerici del K-means e reimplementa l'inferenza a mano), qui salviamo il modello ML.NET GIÀ SERIALIZZATO (lo stesso blob prodotto da IReturnPredictor.Save ): per Random Forest/LightGBM (decine di alberi) reimplementare l'inferenza a mano sarebbe complesso e rischioso, mentre il round-trip Save/Load è già testato per tutti i modelli.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `UserId` | `string` | FK verso AspNetUsers. |
| `User` | `ApplicationUser?` | — |
| `Name` | `string` | Nome scelto dall'utente, es. "RF momentum BTC 1h". |
| `ModelType` | `string` | "Linear" \| "RandomForest" \| "GradientBoosting" — usato per ricreare l'istanza giusta al Load. |
| `Symbol` | `string` | — |
| `Timeframe` | `string` | — |
| `TrainingDataFrom` | `DateTime` | — |
| `TrainingDataTo` | `DateTime` | — |
| `ForwardHorizon` | `int` | — |
| `TargetKind` | `string` | [1.V fase 2] Cosa predice il modello: "ForwardReturn" \| "ForwardAbsReturn" \| "ForwardRealizedVol". Persistito perché la semantica della predizione È il contratto: un modello di volatilità non può alimentare segnali long/short. Default retro-compatibile: tut… |
| `IsDirectional` | `bool` | True se la predizione è un rendimento atteso e può alimentare segnali long/short (MlStrategy, Champion). I modelli di rischio (vol) sono consumabili SOLO da sizing/ vol-targeting. Non mappato da EF (sola lettura). |
| `FactorsJson` | `string` | JSON: List&lt;SavedFactorSpecDto&gt; — nome fattore + parametri, per ricreare i FactorSpec al Load. |
| `ModelBytes` | `byte[]` | Il modello ML.NET serializzato (stesso formato prodotto da IReturnPredictor.Save). |
| `TrainRowCount` | `int` | — |
| `TrainCorrelation` | `double` | — |
| `CreatedAtUtc` | `DateTime` | — |
| `Stage` | `ModelStage` | Stadio nel registry. Default (candidato appena salvato). |
| `Version` | `int` | Generazione del modello per (Symbol, Timeframe): informativa, assegnata dal registry. |
| `ExperimentRunId` | `Guid?` | Lineage: il run di experiment tracking che ha prodotto/valutato questo modello (se noto). |
| `DeflatedSharpe` | `double?` | Deflated Sharpe (Fase 1) associato al modello: è il gate di promozione a Champion. null se non ancora misurato ⇒ non promuovibile a Champion (nessuna promozione "alla cieca"). |
| `PromotedAtUtc` | `DateTime?` | Quando è diventato Champion l'ultima volta (null se non lo è mai stato). |
| `RetiredAtUtc` | `DateTime?` | Quando è stato ritirato (null se non ritirato). |
| `RetiredReason` | `string?` | Motivo del ritiro (es. "superato da versione con DSR migliore", "drift: 3 feature in alert"). |
| `RetrainRequestedAtUtc` | `DateTime?` | Marcatore "retrain accodato": valorizzato quando il ciclo drift chiede un riaddestramento. La piattaforma NON riaddestra da sola (scelta di sicurezza): è un segnale per l'operatore/UI. |

### `SavedStrategy`

<sub>`ProcioneMGR/Data/SavedStrategy.cs`</sub>

> Configurazione di strategia salvata da un utente, riutilizzabile in /backtest. I parametri sono serializzati in JSON (Dictionary&lt;string, decimal&gt;).

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `UserId` | `string` | FK verso AspNetUsers. |
| `User` | `ApplicationUser?` | — |
| `Name` | `string` | Nome scelto dall'utente, es. "Il mio EMA veloce". |
| `StrategyName` | `string` | Nome tecnico della strategia, es. "EmaCross". |
| `ParametersJson` | `string` | Parametri serializzati: JSON di Dictionary&lt;string, decimal&gt;. |
| `CreatedAt` | `DateTime` | — |
| `IsOptimized` | `bool` | True se la configurazione proviene da un'ottimizzazione walk-forward (Fase 5). |
| `OptimizationDate` | `DateTime?` | — |
| `OptimizationSharpe` | `decimal?` | Sharpe out-of-sample medio dell'ottimizzazione che ha prodotto questi parametri. |

### `SentimentMetricPoint`

<sub>`ProcioneMGR/Data/SentimentMetricPoint.cs`</sub>

> Un punto di una serie numerica di "market mood" (Sentiment 2.0): Fear & Greed, long/short ratio, taker buy/sell, open interest, funding. Tabella slim separata da (che è event-shaped: Title/Url/DedupeKey) perché queste sono serie DENSE per-metrica/per-simbolo su cui si calcolano baseline rolling e z-score. La dedupe è l'indice unico composito (Source, Metric, Symbol, TimestampUtc) + un pre-filtro applicativo nel sync service.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `long` | — |
| `TimestampUtc` | `DateTime` | Timestamp del punto (dalla fonte), UTC. |
| `Source` | `string` | "FearGreed" \| "BinanceFutures" \| ... (vedi ). |
| `Metric` | `string` | Nome della metrica (vedi ). |
| `Symbol` | `string` | Ticker base ("BTC", "ETH"); stringa VUOTA = mercato intero (es. Fear & Greed). Non-nullable di proposito: in Postgres i NULL sono distinti negli indici unici e la dedupe sui punti market-wide smetterebbe di funzionare. |
| `Value` | `decimal` | Valore della metrica. Convenzioni: Fear & Greed 0-100; ratio così come arrivano dalla fonte; funding in PERCENTO (×100, convenzione della piattaforma). |

### `TrackedSeries`

<sub>`ProcioneMGR/Data/TrackedSeries.cs`</sub>

> Una serie di mercato (Exchange + Symbol + Timeframe) che il sistema mantiene aggiornata automaticamente in background. E' una watchlist GLOBALE: i dati OHLCV non sono per-utente, quindi nemmeno la lista delle serie tracciate lo e'.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `Exchange` | `ExchangeName` | — |
| `Symbol` | `string` | Simbolo canonico "BASE/QUOTE", es. "BTC/USDT". |
| `Timeframe` | `string` | Timeframe canonico, es. "1h". |
| `Enabled` | `bool` | Se false, il worker la salta. |
| `LastSyncUtc` | `DateTime?` | Ultima sincronizzazione riuscita (UTC), null se mai sincronizzata. |
| `LastSyncStatus` | `string?` | Esito sintetico dell'ultima sincronizzazione (per la UI). |
| `CreatedAt` | `DateTime` | — |

### `TradePostMortem`

<sub>`ProcioneMGR/Data/TradePostMortem.cs`</sub>

> [G4] L'analisi a posteriori di UN'operazione chiusa in perdita (o del ritiro di una corsia). Tabella propria e non PipelineArtifact : quelli sono agganciati a un RunId che un trade non ha. E non il journal della flotta: un post-mortem non è una decisione dell'orchestratore, e piegare quello schema si sarebbe pagato dopo. Confine : questa riga è testo e una classificazione. Non entra in nessun percorso di esecuzione; l'unico consumatore oltre la pagina è il Context della domanda al comitato AF3 — che resta a menù chiuso, con quorum e default deterministico.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `CreatedAtUtc` | `DateTime` | — |
| `LaneId` | `int` | — |
| `TradeRecordId` | `int` | Il trade analizzato ( TradeRecords.Id ). Indice unico: un trade, un post-mortem. |
| `Symbol` | `string` | — |
| `StrategyId` | `string` | — |
| `PnlPercent` | `decimal` | Perdita percentuale del trade (negativa), copiata qui per poter interrogare senza join. |
| `Cause` | `string` | Una voce di — MAI testo libero. È il solo campo che viaggia verso il comitato. |
| `Source` | `string` | Chi ha scelto la causa: "rules" = calcolata dal codice (aritmetica, nessuna AI interpellata) \| "ai" = scelta dall'AI dentro il menù \| "default" = AI non disponibile o fuori menù. |
| `FactsJson` | `string` | I fatti oggettivi su cui si è ragionato (JSON), per poter rileggere il verdetto fra un mese. |
| `Narrative` | `string` | La prosa dell'AI. Vuota quando l'AI non ha risposto: la causa deterministica resta comunque. |
| `ModelUsed` | `string` | Il modello che ha davvero risposto, vuoto se non è stata interpellata nessuna AI. |

### `TradeRecord`

<sub>`ProcioneMGR/Services/Trading/TradingModels.cs`</sub>

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `LaneId` | `int` | Corsia di trading isolata (0 = corsia di default). |
| `PositionId` | `string` | — |
| `StrategyId` | `string` | — |
| `Symbol` | `string` | — |
| `Side` | `OrderSide` | — |
| `EntryPrice` | `decimal` | — |
| `ExitPrice` | `decimal` | — |
| `Quantity` | `decimal` | — |
| `Pnl` | `decimal` | — |
| `PnlPercent` | `decimal` | — |
| `OpenedAtUtc` | `DateTime` | — |
| `ClosedAtUtc` | `DateTime` | — |
| `Duration` | `TimeSpan` | — |
| `ExitReason` | `string?` | — |
| `Mode` | `TradingMode` | — |
| `MarketType` | `MarketType` | — |
| `Leverage` | `int` | Leva usata per il trade (1 per Spot). |
| `WasLiquidated` | `bool` | True se la chiusura è stata una liquidazione (forzata o rilevata per riconciliazione). |

### `TradingAuditLog`

<sub>`ProcioneMGR/Services/Trading/TradingEntities.cs`</sub>

> Audit trail: ogni azione di trading (ordine, chiusura, emergency, start/stop) è loggata.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `LaneId` | `int` | Corsia di trading che ha generato questa voce di audit (0 = corsia di default). |
| `TimestampUtc` | `DateTime` | — |
| `Action` | `string` | "PlaceOrder", "OrderRejected", "ClosePosition", "EmergencyStop", "StartEngine", "StopEngine". |
| `Details` | `string` | JSON con i dettagli dell'azione. |
| `UserId` | `string?` | Utente che ha eseguito l'azione (null per il background worker). |
| `Mode` | `TradingMode` | — |

### `TradingEngineState`

<sub>`ProcioneMGR/Services/Trading/TradingEntities.cs`</sub>

> Stato persistito del trading engine (riga singola). Garantisce idempotenza: al restart il sistema ricostruisce lo stato (running/mode/capitale/emergency) dal DB.

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `LaneId` | `int` | Corsia di trading isolata (0 = corsia di default, esistente prima del supporto multi-coppia). Ogni corsia ha la propria istanza di TradingEngine/EnsembleManager, mai condivise. |
| `Mode` | `TradingMode` | — |
| `MarketType` | `MarketType` | — |
| `Leverage` | `int` | Leva della sessione (1 per Spot; impostata via SetLeverageAsync all'avvio per Futures). |
| `IsRunning` | `bool` | — |
| `ExchangeName` | `string` | — |
| `Symbol` | `string` | — |
| `Timeframe` | `string` | — |
| `TotalCapital` | `decimal` | — |
| `AvailableCapital` | `decimal` | — |
| `RealizedPnl` | `decimal` | — |
| `PeakEquity` | `decimal` | Equity massima raggiunta (per il calcolo del drawdown). |
| `MaxDrawdownPercent` | `decimal` | Massimo drawdown % osservato dalla StartAsync della sessione (persistito): prima viveva solo nella curva equity in-memory, quindi un riavvio lo azzerava — e il gate assoluto HardMaxDrawdownPercent di PromotionEvaluator poteva promuovere una corsia che aveva g… |
| `DailyPnl` | `decimal` | PnL realizzato nelle ultime 24h (rolling), per il safety check daily-loss. |
| `DailyAnchorUtc` | `DateTime` | — |
| `StartedAtUtc` | `DateTime?` | — |
| `LastOrderUtc` | `DateTime?` | — |
| `IsEmergencyStopped` | `bool` | — |
| `EmergencyStopReason` | `string?` | — |
| `UpdatedAtUtc` | `DateTime` | — |

### `UserPageConfig`

<sub>`ProcioneMGR/Data/UserPageConfig.cs`</sub>

> Configurazione completa di una pagina (form di Backtest, Optimization, ...) salvata per utente: preset con nome oppure "ultima configurazione usata" (Name vuoto, aggiornata a ogni Run). Il contenuto è un JSON opaco definito dalla pagina stessa (ogni pagina ha il suo DTO).

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `UserId` | `string` | FK verso AspNetUsers. |
| `User` | `ApplicationUser?` | — |
| `PageKey` | `string` | Chiave stabile della pagina, es. "backtest", "optimization". |
| `Name` | `string` | Nome del preset scelto dall'utente; stringa vuota = ultima configurazione usata. |
| `ConfigJson` | `string` | Configurazione serializzata (JSON opaco, schema a carico della pagina). |
| `UpdatedAtUtc` | `DateTime` | — |

### `VettingCampaign`

<sub>`ProcioneMGR/Services/Pipeline/CampaignEntities.cs`</sub>

> Campagna di vaglio (Fase 1, PRD Autonomia Operativa §4): un elenco ORDINATO di configurazioni di caccia ( ) che il ruota da solo — "0 sopravvissuti" non è più un punto morto ma un input per la mossa successiva. La campagna decide COSA fare dopo un run; il motore pipeline resta intoccato (si aggiunge SOPRA, mai DENTRO). SAFETY: doppio gate — la campagna agisce solo se Campaign:Enabled (globale, default OFF) E (per campagna) sono veri. L'applica passa dalla STESSA catena della ri-applica automatica (supervisore con veto + isteresi); le corsie si avviano al massimo in Paper (Testnet nel planner è nel backlog §8 del PRD, Live MAI per costruzione).

| Campo | Tipo | Descrizione |
|---|---|---|
| `Id` | `int` | — |
| `Name` | `string` | — |
| `CreatedBy` | `string` | Id dell'IdentityUser che ha creato la campagna (usato come userId dei run avviati). |
| `Enabled` | `bool` | Gate per-campagna (oltre a quello globale Campaign:Enabled ). |
| `Status` | `string` | Vedi : "Rotating" \| "Observing" \| "WaitingForTrigger". |
| `ConfigStatesJson` | `string` | JSON: List&lt; &gt; — la rotazione ordinata con lo stato per config. |
| `BackoffHours` | `int` | Backoff: la stessa config non si ripete prima di N ore (un wake del trigger lo bypassa). |
| `AutoStartPaperLanes` | `bool` | Se true, dopo un'applica riuscita il planner AVVIA in Paper le corsie appena configurate (solo quelle ferme: una corsia già in esecuzione — o in quarantena — non viene mai toccata). |
| `PendingRunId` | `Guid?` | Run avviato dalla campagna e non ancora valutato (slot singolo per campagna). |
| `ObservedLanes` | `int` | Corsie configurate dall'ultima applica riuscita (lo "stato ATTESO di flotta" per il riallineamento post-riavvio, Fase 3-C3): in osservazione, le corsie 0..N-1 dovrebbero essere in esecuzione. 0 = nessuna applica ancora avvenuta. |
| `PendingWakeReason` | `string?` | Motivo del "wake" chiesto da un trigger contestuale (Fase 2) e non ancora consumato: il prossimo run parte subito (backoff bypassato) con trigger "Event". |
| `LastOutcome` | `string?` | Ultima decisione presa dal planner, leggibile (per UI e notifiche). |
| `LastActionAtUtc` | `DateTime?` | — |
| `CreatedAtUtc` | `DateTime` | — |
| `UpdatedAtUtc` | `DateTime` | — |

---

## 3. Contratti gRPC

### `Protos/common.proto` — 21 righe

**Messaggi (2):** `DecimalValue`, `Instrument`

### `Protos/events.proto` — 33 righe

**Messaggi (2):** `MarketDataSyncedEvent`, `AlphaSignalReadyEvent`

### `Protos/ingestion.proto` — 23 righe

**Messaggi (3):** `ne`, `SyncRequest`, `SyncResult`

### `Protos/ml.proto` — 50 righe

| Servizio | RPC | Richiesta | Risposta |
|---|---|---|---|
| `InferenceService` | `PredictSignal` | `PredictSignalRequest` | `PredictSignalResponse` |

**Messaggi (2):** `PredictSignalRequest`, `PredictSignalResponse`

### `Protos/trading.proto` — 407 righe

| Servizio | RPC | Richiesta | Risposta |
|---|---|---|---|
| `TradingCommandService` | `GetLaneStatus` | `GetLaneStatusRequest` | `GetLaneStatusResponse` |
| `TradingCommandService` | `GetOpenPositions` | `GetOpenPositionsRequest` | `GetOpenPositionsResponse` |
| `TradingCommandService` | `GetPerformance` | `GetPerformanceRequest` | `GetPerformanceResponse` |
| `TradingCommandService` | `StartLane` | `StartLaneRequest` | `StartLaneResponse` |
| `TradingCommandService` | `StopLane` | `StopLaneRequest` | `StopLaneResponse` |
| `TradingCommandService` | `EmergencyStop` | `EmergencyStopRequest` | `EmergencyStopResponse` |
| `TradingCommandService` | `ClosePosition` | `ClosePositionRequest` | `ClosePositionResponse` |
| `TradingCommandService` | `CloseAllPositions` | `CloseAllPositionsRequest` | `CloseAllPositionsResponse` |
| `TradingCommandService` | `SetStopLossTakeProfit` | `SetStopLossTakeProfitRequest` | `SetStopLossTakeProfitResponse` |
| `TradingCommandService` | `ConfirmOrder` | `ConfirmOrderRequest` | `ConfirmOrderResponse` |
| `TradingCommandService` | `RejectOrder` | `RejectOrderRequest` | `RejectOrderResponse` |

**Messaggi (36):** `GetLaneStatusRequest`, `GetLaneStatusResponse`, `GetOpenPositionsRequest`, `GetOpenPositionsResponse`, `OpenPosition`, `ClosePositionRequest`, `ClosePositionResponse`, `CloseAllPositionsRequest`, `CloseAllPositionsResponse`, `SetStopLossTakeProfitRequest`, `e`, `SetStopLossTakeProfitResponse`, `StartLaneRequest`, `StartLaneResponse`, `StopLaneRequest`, `StopLaneResponse`, `EmergencyStopRequest`, `EmergencyStopResponse`, `ConfirmOrderRequest`, `ConfirmOrderResponse`, `RejectOrderRequest`, `RejectOrderResponse`, `GetPerformanceRequest`, `GetPerformanceResponse`, `EquityPoint`, `TradeRecord`, `tipato`, `GetEngineConfigRequest`, `GetEngineConfigResponse`, `EngineConfigSection`, `SetEngineConfigRequest`, `SetEngineConfigResponse`, `SendTestNotificationRequest`, `SendTestNotificationResponse`, `GetNotificationChannelStatusRequest`, `GetNotificationChannelStatusResponse`
