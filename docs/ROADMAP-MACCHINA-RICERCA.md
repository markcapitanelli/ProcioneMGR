# ProcioneMGR — Roadmap "Macchina di Ricerca": spremere i dati già in casa

*2026-07-20. Terza roadmap di metodo dopo ML4T e QLIB. Ogni claim "esiste/manca" è stato verificato
leggendo il codice (file citati), non per analogia. I numeri dell'inventario vengono dall'audit
`coverage` eseguito oggi (T0.0, riproducibile).*

---

## 0. Premessa: perché "servono dati di un altro mercato" era una conclusione prematura

Dopo la ricerca del 2026-07-20 (445.280 combinazioni → 0 candidati significativi al Deflated
Sharpe → 0/6 sopravvissuti all'holdout; dosaggio della volatilità regime-dipendente) la conclusione
era stata: *per decidere serve un mercato diverso*. Quella frase resta vera per **una** domanda
specifica — se il dosaggio della volatilità sia una proprietà universale o un artefatto di questo
ciclo — ma il proprietario ha contestato la generalizzazione, e il censimento del codice gli dà
ragione su tutta la linea:

- la piattaforma **raccoglie già** dati preziosi che il motore di ricerca **non consuma** (funding
  storico, open interest, long/short ratio — raccolti per il sentiment e mai usati come feature o
  costo);
- l'ingestione **scarta** a ogni download campi che Binance regala in ogni kline (volume taker,
  numero di trade, controvalore);
- metà delle tecniche statistiche "mancanti" **esistono già nel codice** ma non sono collegate dove
  servono (purged CV e CPCV esistono, il walk-forward dell'ottimizzatore non li usa);
- due strumenti d'analisi già scritti — `NewsImpactAnalyzer` e `CyclicalAnalyzer` — sono fermi allo
  stadio di visualizzazione, mai promossi a componenti del processo di ricerca.

**Questa roadmap quindi collega più che inventare.** È il suo pregio: quasi ogni item estende
un'interfaccia esistente invece di aggiungere un sottosistema.

---

## 1. Le quattro proposte del proprietario, punto per punto

### 1.a "Investimenti in negativo oltre che in positivo" (short)

**Già supportato, ovunque**: `Signal.Short` nel contratto delle strategie, `AllowShort` nei
parametri (Supertrend, PriceSmaCross, Donchian…), futures con leva su Binance/Bitget, guard
anti-short-su-spot-reale nel motore. Le cacce (`hunt`) includono gli short da sempre.

**Il gap vero è più sottile: il funding è FIRMATO e il backtest lo ignora.** Sui perpetual, quando
il funding è positivo (condizione tipica dei mercati rialzisti) i long lo *pagano* e gli short lo
*incassano*. Il motore di backtest addebita invece una costante (`FundingRatePercentPer8h`, default
0,01%/8h — `BacktestEngine.cs:182`) **a qualunque posizione**, penalizzando sistematicamente
proprio gli short che il proprietario vuole esplorare. Il funding storico è già raccolto in
`SentimentMetricPoints` (Metric=`FundingRate`) ma non arriva mai al motore. → **Item T0.2.**

### 1.b "Analisi di periodi specifici con episodi specifici"

**Gap reale, ma non greenfield.** Esiste già `Services/AltData/NewsImpactAnalyzer.cs`: misura il
rendimento medio a 1h/4h/24h dal timestamp di ogni notizia/evento (inclusi gli eventi del
calendario economico ForexFactory, già ingeriti da `ForexFactoryIngestor`). Quello che manca è il
**rigore**: nessun confronto con una baseline (abnormal return), nessuna finestra pre-evento,
nessun test placebo. E manca del tutto il rilevatore di **eventi di mercato** (crash, spike di
volatilità) che funzionerebbe su tutta la profondità OHLCV, non solo sui 20 giorni di storia
alt-data. → **Item T2.7.**

L'audit dice cosa è fattibile: tutti e 10 gli episodi nominabili dal 2020 (COVID, LUNA, FTX, SVB,
ETF, halving, yen carry-trade…) sono coperti a 1d; dal 2024 anche a 1h. Gli event-study intraday
fini (5m/15m) sono possibili solo dall'inizio 2025.

### 1.c "Variazioni di parametri (EMA 20/50 → 25/55 → …)"

**Già coperto — ed esaurito.** È esattamente ciò che fanno Grid search e Bayesian in
`/optimization`, la caccia `hunt` (445.280 combinazioni su 46 coppie × 13 strategie × 3 timeframe)
e il minatore genetico (`GeneticAlphaMiner`). L'esito è il punto: **0 combinazioni significative al
Deflated Sharpe, 0/6 sopravvissute all'holdout**. La macchina per "provare tante varianti" esiste
ed è stata usata a fondo; produrne altre sugli stessi dati con lo stesso tipo di domanda produce
solo overfitting più raffinato. Questa roadmap serve a cambiare **tipo di domanda**, non a fare più
domande dello stesso tipo.

### 1.d "Anticipare movimenti dai volumi"

**Gap reale, con un colpevole preciso.** Il parsing delle klines Binance
(`BinanceClient.cs:44-55`, verificato riga per riga) legge solo `k[0..5]` (tempo + OHLCV) e
**scarta**: `k[7]` controvalore (quote volume), `k[8]` **numero di trade**, `k[9]` **volume taker
buy base**, `k[10]` volume taker buy quote. Il volume taker è l'order flow aggregato — chi
attraversa lo spread, cioè la componente "aggressiva" degli scambi — e arriva gratis in ogni
download da anni. Paradosso: la piattaforma raccoglie il `TakerBuySellRatio` da un endpoint futures
separato (retention 30 giorni), mentre la stessa informazione, senza limiti di retention, viene
buttata al parsing spot. → **Item T0.3, poi T3.**

---

## 2. Inventario dei dati già in casa (audit T0.0, 2026-07-20)

Riproducibile: `dotnet run --project tools/PlatformExpand -- coverage`

| Dataset | Copertura | Dove vive | Chi lo usa oggi | Valore non estratto |
|---|---|---|---|---|
| OHLCV 1d | 46 simboli, dal **2020-01** | `OhlcvData` | tutto | — |
| OHLCV 4h | 46 simboli, dal 2020-07 (grosso dal 2022) | `OhlcvData` | tutto | — |
| OHLCV 1h | 46 simboli, dal 2020-07 (grosso dal 2023) | `OhlcvData` | tutto | — |
| OHLCV 5m/15m | 30-45 simboli, dal 2025-01 | `OhlcvData` | poco (costi li rendono cari) | stagionalità intraday |
| OHLCV 1m | 6 simboli, ~525k candele/simbolo | `OhlcvData` | analisi costi R2 | stagionalità, event-study fini |
| **Densità: 219 serie, 12,15M candele, NESSUN buco** (tutte ≥99%) | | | | |
| Fear & Greed | **dal 2018-02, 3.088 punti** | `SentimentMetricPoints` | z-score sentiment | feature di regime con 8,5 anni di storia |
| Funding rate | ~~17 giorni~~ → **dal 2019-09, ~7.400 eventi/simbolo su 6 simboli** (backfill T0.2 eseguito) | `SentimentMetricPoints` | backtest via `FundingHistoryProvider` | ✅ collegato |
| Open interest, long/short, taker ratio | **3 giorni**, 2 simboli | `SentimentMetricPoints` | z-score sentiment | feature — ma serve accumulare storia |
| Notizie + calendario economico | dal 2026-01/07, 3.876 eventi | `AltDataPoint` | NewsImpactAnalyzer (medie semplici) | event-study rigoroso (T2.7) |
| Volume taker, n. trade, quote volume | **SCARTATI al parsing** | — | nessuno | order flow storico completo (T0.3) |
| Coppie non-USDT (ETH/BTC…) | ingeribili OGGI, zero modifiche | — | nessuno | mercati relativi interni (T4) |

Due conseguenze immediate dell'audit: (1) le metriche futures hanno 3-17 giorni di storia — vanno
messe in accumulo **adesso** perché fra sei mesi valgano qualcosa; (2) la storia intraday profonda
esiste solo da inizio 2025: gli event-study fini si fanno sugli episodi recenti, quelli storici su
1d/4h.

---

## 3. Le lezioni metodologiche incorporate

Tre lezioni pagate di recente, che questa roadmap tratta come vincoli:

1. **Randomizzare su asset correlati dentro una finestra fabbrica significatività finta.** I 400
   panieri casuali estratti dallo stesso periodo hanno prodotto una t di 141 priva di senso: erano
   un esperimento solo ripetuto 400 volte. L'unica randomizzazione valida è lungo la dimensione in
   cui i dati sono indipendenti: **il tempo** (finestre disgiunte, block bootstrap). → T1.5.
2. **Lo Sharpe di selezione non è una previsione.** I sei migliori su 445.280 avevano selezione
   1,28–1,61 e holdout −0,79…−4,75. Ogni item di questa roadmap dichiara *quanta informazione nuova
   estrae*, mai quanto renderà.
3. **Il gate esiste e funziona** (l'esperimento di controllo con edge piantato lo dimostra: DSR
   1,00 quando l'edge c'è). Ogni nuova idea passa da: edge piantato → DSR → holdout → replica su
   finestre temporali disgiunte. Nessuna eccezione, nemmeno per gli item di questa roadmap.

---

## 4. Gli item, verificati contro il codice

Formato: cosa c'è oggi / cosa manca / aggancio / effort (S/M/L) / come si valida.

### T0 — Collega l'esistente

#### T0.0 — Audit di copertura dati ✅ FATTO (2026-07-20)

Fase `coverage` in `tools/PlatformExpand` (read-only). I numeri sono la §2. Da rilanciare dopo ogni
ingestione importante.

#### T0.1 — Purge/embargo nel walk-forward dell'ottimizzatore — **S** ✅ FATTO (2026-07-20)

**Oggi**: `OptimizationEngine.GenerateWindows` (`:246-263`, verificato) produce finestre IS/OOS
**contigue**: `oosStart = isEnd`. Una strategia che tiene posizioni aperte a cavallo del confine, o
indicatori con lookback, fanno filtrare informazione dall'IS all'OOS. Intanto
`Services/ML/PurgedTimeSeriesCv.cs` (purge+embargo, López de Prado) esiste ed è usato **solo**
nello stacking ML: la piattaforma possiede lo strumento giusto e non lo usa dove conta di più.

**Fatto**: campo `EmbargoBars` in `WalkForwardConfiguration` (default 0 = comportamento invariato,
con test a guardia); l'OOS di ogni finestra perde le prime N barre, il report riflette l'inizio
EFFETTIVO; finestre degenerate dall'embargo vengono saltate con warning invece di misurare rumore
su due barre. Esposto in `/optimization` (campo "Embargo (barre)"), nei preset (compatibili
all'indietro) e nello stage Discovery della pipeline (`embargoBars`).

**Validazione fatta**: contiguità bit-identica a embargo 0; trimming esatto di N barre per
finestra; finestra degenerata saltata senza backtest eseguiti; embargo negativo rifiutato.
Resta da fare: il ri-run di uno sweep storico con/senza per QUANTIFICARE il leakage (item di
misura, non di codice).

#### T0.2 — Funding storico nel motore di backtest — **S/M** ✅ FATTO (2026-07-20)

**Era**: serie raccolta ma con 17 giorni di storia; il motore usava la costante
`config.FundingRatePercentPer8h` senza segno — chi era short *pagava* la costante invece di
*incassare* il funding positivo.

**Fatto**:
- `FundingRateLookup` (gradini: ultimo evento ≤ ts, fallback dichiarato alla costante prima del
  primo evento) + `BacktestConfiguration.FundingHistory`;
- il motore applica il rate **firmato per lato**: funding positivo → il long paga, lo short
  incassa. Vale anche per la costante (fix documentato: la vecchia semantica addebitava il funding
  anche a chi lo avrebbe ricevuto);
- `FundingHistoryProvider` (DI) legge la serie da `SentimentMetricPoints`;
- **backfill profondo eseguito** (fase `fundingbackfill`): la verifica sul campo ha dato molto più
  dei 30 giorni temuti — `/fapi/v1/fundingRate` serve l'INTERA storia: BTC dal **2019-09-10**,
  ~7.400 eventi/simbolo su 6 simboli (BTC, ETH, SOL, BNB, XRP, DOGE), ~42.000 punti.

**Validazione fatta**: simmetria esatta long/short a parità di serie; conto a mano dell'addebito
pro-rata; serie che parte a metà run (costante prima, storico dopo); default invariato a funding 0.
Resta da fare l'A/B costante-vs-storico su una strategia short del catalogo (dipende da una caccia
nuova).

#### T0.3 — Stop allo scarto dei campi klines — **M** ✅ FATTO, reingest incluso (2026-07-23)

**Era**: `OhlcvData` aveva solo OHLCV; il parsing Binance scartava `k[7..10]`; Bitget scartava il
quoteVolume.

**Fatto**: 4 colonne **nullable** su `OhlcvData` (`QuoteVolume`, `TradeCount`, `TakerBuyVolume`,
`TakerBuyQuoteVolume` — null distingue "non raccolto" da "zero"); record `Ohlcv` esteso con
opzionali in coda; parsing Binance `k[7..10]` con guardia sui payload corti; Bitget raccoglie il
quoteVolume; **regola di merge** nell'upsert (una fonte povera non azzera i campi della ricca);
migration `AddKlineExtendedFields` **applicata al DB reale**; fase `reingestx <tfs>`.

**Reingest eseguito** su 1d/4h/1h: **1.700.270/1.700.270 candele popolate (100,0%)** su 138 serie,
invariante `TakerBuyVolume ≤ Volume` verificata su tutte. L'order flow storico completo (dal 2020
sul giornaliero) è ora nel database. 5m/15m/1m: lancio separato quando serve
(`reingestx "5m,15m"`).

### T1 — Prima il giudice, poi il labeling

*(L'ordine è deliberato: 1.5 e 1.6 rafforzano il metro con cui giudicheremo 1.V e 1.4.)*

#### T1.5 — Block/stationary bootstrap + permutation test — **M** ✅ FATTO (2026-07-23)

**Era**: `MonteCarloAnalyzer` e `LeverageAdvisor` facevano solo resampling **iid** dei trade — che
distrugge l'autocorrelazione, cioè proprio la struttura (serie di perdite consecutive) che produce
i drawdown profondi. Nessun permutation test.

**Fatto**:
- `MonteCarloSamplingMode.StationaryBlock` (Politis–Romano: blocchi geometrici con reinserimento e
  wrap-around, `MeanBlockLength` parametrico) accanto allo shuffle iid, che resta il default con
  test di invarianza; sul PnL "a strisce" il modo a blocchi vede code di drawdown peggiori — il
  motivo per cui esiste;
- `Services/Validation/PermutationTest`: p-value per lo Sharpe con capovolgimenti di segno a
  BLOCCHI lungo il tempo — la codifica diretta della lezione t=141 (l'unica randomizzazione onesta
  è temporale, e a blocchi per non distruggere l'autocorrelazione);
- `ValidatedCandidate.PermutationPValue` popolato dall'`OverfittingGate`: **informativo di default**
  (`maxPermutationPValue = 1.0`), diventa bloccante quando gli si passa una soglia — la stessa
  strada di rodaggio fatta dal DSR.

**Validazione fatta**: edge piantato → p &lt; 0,05; rumore simmetrico → p non estremo;
**calibrazione su 200 serie di rumore** (frazione con p&lt;0,10 nei limiti binomiali attorno al
10%); determinismo a parità di seme; sul gate, con soglia attiva il rumore muore e l'edge piantato
sopravvive.

#### T1.6 — CPCV esteso al percorso strategie — **M** ✅ FATTO, fase 2 inclusa (2026-07-23)

**Era**: `CombinatorialPurgedCv` esisteva ma era usato solo nei gate ML; il percorso strategie
aveva UN solo percorso out-of-sample per candidato (walk-forward + holdout).

**Fatto**: `OptimizationEngine.OptimizeCpcvAsync` — la serie è divisa in gruppi temporali contigui;
per ognuna delle C(gruppi, gruppiTest) combinazioni i parametri si scelgono sui gruppi di train
(media degli Sharpe sui gruppi INTERI: i gruppi mutilati dalle bande di purge/embargo vengono
scartati, conservativo) e si giudicano sui gruppi di test mai visti da quella scelta. Output:
**distribuzione** di Sharpe OOS (mediana, P05, P95, percorsi positivi), PBO sul pannello dei
candidati, e due metriche di stabilità — i parametri modali e la quota di percorsi che li sceglie
(`SelectionStability`): un candidato scelto dal 100% dei train è strutturale, uno scelto dal 30% è
figlio del periodo. Risponde esattamente alla richiesta del proprietario: più out-of-sample
**dagli stessi dati**.

**Validazione fatta**: edge piantato (picco a X=7 coerente sui gruppi) scelto su **tutti i 28
percorsi** C(8,2) con stabilità 100% e distribuzione OOS interamente positiva; con purge/embargo
larghi un gruppo intero i percorsi si risolvono comunque; deterministico; serie troppo corta →
errore esplicito invece di misurare rumore.

**Fase 2 ✅ (2026-07-23)**: esposto in `/optimization` — select "Validazione: Walk-Forward | CPCV"
(gruppi/gruppi-test/purge/embargo nel form, C(n,k) mostrato in tempo reale), card risultato con
distribuzione (mediana/P05/P95/percorsi positivi), PBO, parametri MODALI e stabilità della
selezione, tabella dei percorsi; "Salva parametri modali" persiste il candidato STABILE con lo
Sharpe OOS mediano (non il più fortunato); experiment tracking `OptimizationCpcv`; preset
retro-compatibili (i vecchi deserializzano a Walk-Forward). Bayesian+CPCV rifiutato con motivo
(il CPCV pre-calcola l'intera griglia). Test sull'orchestrazione in `OptimizationPageServiceTests`.

#### T1.V — La volatilità come TARGET di predizione — **S/M** ⭐ ✅ FATTO, fase 2 inclusa (2026-07-23)

**Oggi**: tutti i target ML erano rendimenti futuri. 0 sopravvissuti su 445.280 dice che la
**direzione** non è prevedibile su questi dati con questi strumenti; il **rischio** invece è
persistente (stesso fatto stilizzato dietro il GARCH già in piattaforma).

**Fatto (fase 1 — il target esiste e si misura nel Lab)**:
- `MlTargetKind` (`ForwardReturn` default invariato | `ForwardAbsReturn` | `ForwardRealizedVol`) +
  `ForwardTargets` (vol realizzata = deviazione standard dei rendimenti per-barra dentro
  l'orizzonte; orizzonte 1 rifiutato con errore chiaro invece di uno zero silenzioso);
- parametro opzionale su `IDatasetBuilder`/`DatasetBuilder`; select nel ML Lab;
- **GUARDIA DI SEMANTICA**: un modello con target non-rendimento **non si può salvare** — tutto ciò
  che consuma un `SavedMlModel` (MlStrategy, registry, Champion) interpreta la predizione come
  rendimento atteso e la confronterebbe con le soglie long/short (vol alta ≠ compra). Prima
  verifica di `SaveModelAsync`, indipendente dallo stato di addestramento, con test.

**Fase 2 ✅ (2026-07-23)** — la guardia si è SPOSTATA dal salvataggio al consumo:
- `SavedMlModel.TargetKind` persistito (migration `AddSavedMlModelTargetKind`, backfill
  "ForwardReturn" per i modelli storici) + `IsDirectional`;
- guardie sul consumo direzionale: `MlModelLoader.LoadAsync` (punto UNICO batch+stream) rifiuta i
  modelli non-rendimento con messaggio di semantica; `ModelRegistry` Gate 0 (mai Champion);
  Optimization ed Ensemble offrono solo modelli direzionali; il backtest del Lab su una sessione
  vol viene rifiutato indirizzando alla valutazione giusta;
- il consumo giusto: **"Valuta previsione di vol"** nel Lab — QLIKE/MSE out-of-sample contro
  baseline EWMA (RiskMetrics λ=0,94) e naive (vol passata), `VolForecastEvaluator` puro con test.
  Il verdetto onesto è scritto nella UI: finché il modello non batte l'EWMA, il vol-targeting resta
  sulla misura semplice — battere l'EWMA qui è la precondizione per instradare la predizione nel
  sizing. *Onestà: migliora la gestione del rischio, non promette rendimento.*

#### T1.4 — Triple-barrier labeling + meta-labeling — **L**

**Oggi**: label = rendimento a orizzonte fisso, che ignora il percorso (una label positiva può aver
attraversato un drawdown che nessuno stop avrebbe tollerato). Triple-barrier assente; meta-labeling
assente.

**Aggancio**: nuovo `Services/ML/Labeling/` (`ILabeler`): barriere profitto/stop/tempo, con le
soglie derivate dai percentili di escursione **già calcolati** da
`Services/Analysis/ExcursionAnalyzer.cs`. Meta-labeling: un classificatore che predice "il segnale
della strategia X sarà giusto?", costruito sul macchinario out-of-fold **purged** già esistente in
`StackedReturnPredictor`; consumo come decoratore di `IStrategy` (filtra i segnali sotto soglia di
probabilità). Clausola dichiarata: se entra il triple-barrier, i **sample weights** per label
sovrapposte (oggi in appendice) diventano quasi obbligatori.

**Validazione**: edge piantato con asimmetria di barriera nota viene recuperato; il meta-modello
migliora la precision a DSR pari o superiore sull'holdout.

### T2 — Episodi ed eventi

#### T2.7 — Event-study rigoroso — **M** ✅ FATTO (2026-07-23)

**Era**: `NewsImpactAnalyzer` calcolava medie post-evento semplici (1h/4h/24h) sugli eventi
alt-data — nessuna baseline, nessuna finestra pre-evento, nessun placebo, e nessun rilevatore di
eventi di MERCATO sull'OHLCV profondo.

**Fatto**:
- `Services/Analysis/EventStudy` (puro, deterministico a parità di seme): **abnormal return** vs
  baseline per-evento (finestra di stima separata da un gap), **finestra pre-evento** (una CAAR già
  positiva prima dell'evento = anticipazione o leakage del timestamp), **placebo temporale** —
  la stessa statistica su insiemi di date casuali (la lezione T1.5 codificata: la randomizzazione
  onesta è lungo il tempo), p-value con correzione add-one. Il placebo, non la t, è il verdetto;
- `Services/Analysis/MarketEventDetector` (causale: la barra giudicata non contribuisce mai alla
  propria soglia): Crash/Surge (|z| oltre k·σ rolling), VolSpike (σ breve / σ lunga su finestre
  disgiunte), VolumeBlowout (multiplo della mediana rolling), cooldown per tipo (un cluster = UN
  episodio) — funziona su tutti i sei anni di OHLCV;
- `INewsImpactAnalyzer.StudyRigorous(...)`: gli stessi eventi alt-data passano dallo studio
  rigoroso (filtra per categoria a monte);
- fase `eventstudy [symbol] [tf]` in PlatformExpand: rileva gli eventi, stampa CAAR pre/post, t,
  p placebo per tipo, e usa il **calendario economico** (ForexFactory) come controllo positivo
  quando presente in DB.

**Validazione fatta (criterio dichiarato)**: effetto piantato (+50bp × 11 barre su 25 eventi)
recuperato con p&lt;0,05 e CAAR pre ~0; su rumore puro con date casuali il placebo NON è
significativo; drift di baseline sottratto esattamente (la differenza vs medie semplici); eventi
troppo vicini ai bordi esclusi, non mutilati; detector causale per troncamento e dedup del cluster.
Aperto (misura, non codice): il run sul campo `eventstudy` per gli episodi nominati e il controllo
FOMC/CPI, e l'eventuale promozione dei flag-evento a filtri di strategia (passerebbero dal gate).

#### T2.S — Wiring della stagionalità — **S** ✅ FATTO (2026-07-23)

**Era**: `CyclicalAnalyzer` calcolava già bias orario/giorno-settimana/stagionalità ma solo per la
pagina `/market-analysis`: nessuna strategia poteva dire "opera solo nelle ore X".

**Fatto**: decimo segnale **"Ora UTC"** (id 9, scala 0-100 = hour/23·100) nel `SignalCatalog`.
La stagionalità oraria diventa così **cacciabile dalla stessa combinatoria degli altri segnali**:
il `StrategyComposer` può proporre "RSI < 20 AND OraUtc ∈ [X,Y]" e il Composite la esprime senza
sottosistemi nuovi. Appeso in coda: gli id 0-8 delle strategie salvate restano validi. Nessun
warm-up, anti-look-ahead per costruzione (il valore dipende solo dal timestamp della barra).

**Avvertenza incorporata nel codice**: i bias orari sono notoriamente instabili — le composizioni
che usano questo segnale vanno giudicate con enfasi sulla replica su finestre temporali disgiunte
(il gate T1.6/T1.5 esiste apposta).

### T3 — Volumi e order flow

#### T3.8a — OBV/MFI/VWAP riusabili + volume/breadth nei regimi — **M** ✅ FATTO (2026-07-23)

**Era**: OBV/MFI/VWAP non esistevano come indicatori riusabili (il VWAP viveva incastonato in una
strategia); il K-means dei regimi usava 4 feature ed escludeva deliberatamente il volume; la
breadth interna (aperto di T4) non esisteva.

**Fatto**:
- `TechnicalIndicatorsService`: `CalculateObvAsync` (cumulata firmata), `CalculateMfiAsync`
  (RSI pesato per volume, nativo 0-100, O(n) a finestra scorrevole), `CalculateRollingVwapAsync`
  (VWAP rolling senza ancora — quello di sessione UTC resta nel catalogo, id 5) — riusabili
  ovunque, con test contro conti a mano;
- `SignalCatalog`: id **10 "MFI"** (nativo) e **11 "OBV slope pct"** (variazione a 10 barre
  dell'OBV, percentile causale), APPESI in coda — gli id 0-9 delle Composite salvate restano
  validi; il VWAP non ha un id nuovo (la deviazione dal VWAP di sessione È già l'id 5, un
  duplicato violerebbe il criterio "informazione nuova"). Ora la combinatoria del
  `StrategyComposer` può proporre "RSI &lt; 20 AND MFI &lt; 20" (divergenza prezzo/flusso);
- `RegimeDetector`: quinta feature **VolumeRatio** e sesta **MarketBreadth** (frazione dei simboli
  /USDT sopra la propria SMA50 — nuovo `IMarketBreadthCalculator`, causale, warm-up dichiarato),
  entrambe **OPT-IN** (`TrainingConfiguration.IncludeVolumeFeature/IncludeBreadthFeature`, default
  OFF = etichette storiche bit-identiche). La scelta VIAGGIA COL MODELLO (`FeatureScaling.Names`
  persistito nel FeatureScalingJson): l'inference ricostruisce da sola il vettore giusto, i modelli
  pre-3.8a deserializzano alle 4 feature storiche. Checkbox in `/regimes` con l'avvertenza.

**Validazione fatta**: indicatori contro conti a mano (OBV cumulata, MFI estremi/warm-up/peso del
volume, VWAP rolling); segnali 10-11 causali per troncamento; vettore di clustering default
invariato e opt-in append-only; `FeatureScaling` legacy senza `Names` → 4 storiche. Aperto (misura):
IC incrementale dei segnali nuovi nelle composizioni e stabilità dei cluster col volume/breadth
accesi — si misura riaddestrando, il gate T1.5/T1.6 esiste apposta.

#### T3.8b — Feature order-flow dai campi recuperati — **M** ✅ FATTO (2026-07-23)

**Fatto**: due nuovi `IAlphaFactor` registrati in factory (prototipi + round-trip per nome):
- `TakerImbalance` — media rolling di (2·TakerBuyVolume/Volume − 1) ∈ [−1,+1]: la pressione
  aggressiva netta, chi paga lo spread pur di eseguire subito;
- `AvgTradeSize` — (Volume/TradeCount) relativo alla propria media rolling: trade grossi vs
  frammentazione.

Proprietà chiave testata: **null dove i campi estesi mancano** — un imbalance letto su uno zero
finto (candela non reingerita) sarebbe un artefatto della migrazione spacciato per segnale.

**Prima misura sull'IC** (fase `orderflow`, dati reingeriti, orizzonte ~1 giorno):

| | IC taker (1h, 26.749 barre) | IC taker (4h) | ρ con RelativeVolume |
|---|---|---|---|
| DOGE | **0,036** | 0,018 | ≈ 0 |
| SOL | 0,020 | 0,025 | ≈ 0 |
| BTC | 0,014 | 0,017 | ≈ 0 |
| ETH | 0,006 | **0,036** | ≈ 0 |

IC positivo su 5/6 simboli a 1h e 4h, con **correlazione ~zero** col fattore volume esistente:
informazione genuinamente nuova, non un rinominare. `AvgTradeSize` più rumoroso. **Non è un
verdetto**: la conferma spetta al gate (DSR/holdout/permutation) quando i fattori entreranno in un
modello o in una composizione.

#### T3.8c — Barre informative → backtest — **L, OPZIONALE dichiarato**

`BarBuilder` (volume/dollar bars) esiste ma il motore assume spaziatura temporale regolare
(annualizzazione dello Sharpe, funding pro-rata via `candleHours`). Adattarlo è il pezzo più
costoso e rischioso del tier: dichiarato opzionale perché non blocchi i quick win.

### T4 — Mercati relativi interni — **S/M** ✅ FATTO (dati) (2026-07-23)

**Fatto**: fase `relative` — **5 coppie /BTC tracciate** (ETH, SOL, BNB, XRP, DOGE × 1h/4h/1d,
15 serie in `TrackedSeries`: l'app le tiene aggiornate da sola) e **~217.000 candele** di storico
ingerite (1d/4h dal 2020, 1h dal 2023). Passando dal parser post-T0.3, hanno già i campi
order-flow. Sono serie di prima classe: backtest, pairs (già sui log), discovery e fattori le
vedono senza altro codice — "un mercato diverso" in miniatura, con dinamiche proprie rispetto
alle coppie USDT.

**Aperto di questo item — CHIUSO con 3.8a (2026-07-23)**: la breadth interna (% simboli sopra la
propria MA50) è ora la sesta feature opt-in del `RegimeDetector` (`IMarketBreadthCalculator`).

### T5 — Sizing dal modello — **M, CONDIZIONATO**

**Oggi**: `KellyCalculator` completo (binario/continuo/empirico/multi-asset, half-Kelly) ma
alimentato da statistiche realizzate, non dalla confidenza per-segnale.

**Aggancio**: il disaccordo tra i base-model dello `StackedReturnPredictor` come proxy di
incertezza per-segnale → moltiplicatore Kelly in `DecisionStages`.

**Precondizione esplicita**: un modello che superi il gate. Il sizing *moltiplica* un edge; senza
edge validato moltiplica zero. Finché la precondizione non si verifica, questo item resta in fondo.

---

## 5. Tabella riassuntiva

| Item | Tier | Effort | Dipende da | Criterio di validazione |
|---|---|---|---|---|
| 0.0 Audit dati | T0 | S | — | ✅ fatto: §2 |
| 0.1 Purge/embargo WF | T0 | S | — | ✅ fatto (delta OOS storico ancora da misurare) |
| 0.2 Funding storico | T0 | S/M | — | ✅ fatto, backfill dal 2019 (A/B short da misurare) |
| 0.3 Campi klines | T0 | M | — | ✅ fatto: reingest 1,7M candele al 100%, invarianti OK |
| 1.5 Bootstrap+permutation | T1 | M | — | ✅ fatto, calibrazione su 200 serie superata |
| 1.6 CPCV strategie | T1 | M | 1.5 utile | ✅ fatto + fase 2: CPCV in /optimization |
| 1.V Vol come target | T1 | S/M | — | ✅ fatto + fase 2: TargetKind persistito, QLIKE vs EWMA |
| 1.4 Triple-barrier+meta | T1 | L | 1.5, (weights) | edge asimmetrico recuperato; precision ↑ a DSR pari |
| 2.7 Event-study | T2 | M | 1.5 (placebo) | ✅ fatto: effetto piantato recuperato, placebo pulito |
| 2.S Stagionalità | T2 | S | — | ✅ fatto: segnale "Ora UTC" nel catalogo (id 9) |
| 3.8a OBV/MFI/VWAP+regimi | T3 | M | — | ✅ fatto: id 10-11, volume/breadth opt-in nei regimi |
| 3.8b Order-flow | T3 | M | **0.3** | ✅ fatto: IC>0 su 5/6 simboli, ρ≈0 con RelVolume |
| 3.8c Barre→backtest | T3 | L | 0.3 | opzionale dichiarato — RESTA rimandato (§7) |
| 4.9 Mercati relativi | T4 | S/M | — | ✅ fatto: 15 serie /BTC + breadth (chiusa con 3.8a) |
| 5.10 Confidenza→Kelly | T5 | M | **modello oltre il gate** | CONDIZIONATO — la precondizione non si è verificata |

**Stato 2026-07-23: la roadmap è CHIUSA.** Restano fuori, per scelta dichiarata e non per
dimenticanza: **1.4** (triple-barrier, l'unico item L "vero" rimasto — da riprendere quando c'è
spazio per un item lungo), **3.8c** (opzionale: il costo di adattare il motore a barre non
equispaziate resta superiore al beneficio) e **5.10** (condizionato a un modello oltre il gate:
il sizing moltiplica un edge, e senza edge validato moltiplica zero). Le misure "aperte" dei
singoli item (ri-run sweep con/senza embargo, A/B funding short, run `eventstudy` sul campo,
IC dei segnali 10-11, cluster con volume/breadth) sono lavoro di ESERCIZIO della macchina, non
di costruzione — appartengono alla prossima caccia, non a questa roadmap.

---

## 6. Principi trasversali

1. **Additività**: ogni item estende interfacce esistenti (`IAlphaFactor`, `IStrategy`,
   `SignalCatalog`, `OverfittingGate`, `DatasetBuilder`) — mai sottosistemi paralleli.
2. **Tracciamento**: ogni esperimento logga nell'`ExperimentTracker` esistente. Niente run orfani.
3. **Anti-look-ahead per costruzione**: il contratto `value[i] ← candles[0..i]` di `IAlphaFactor`
   vale per ogni feature nuova; le label triple-barrier si calcolano solo su dati futuri *rispetto
   al punto di decisione*, mai oltre l'orizzonte dichiarato.
4. **Randomizzazione solo temporale**: mai più panieri correlati dentro una finestra (§3).
5. **Il gate non si negozia**: edge piantato, DSR, holdout, finestre disgiunte — anche per gli
   item di questa roadmap, anche quando il risultato "sembra ovvio".

---

## 7. Appendice: rimandati con motivo

- **Fractional differentiation** (AFML cap. 5): valore reale ma marginale rispetto agli item sopra
  finché il labeling resta a orizzonte fisso; da rivalutare dopo 1.4.
- **Sample weights / uniqueness**: promossi da appendice a requisito **se e quando** entra il
  triple-barrier (label sovrapposte → pesi quasi obbligatori). Clausola già dichiarata in 1.4.
- **Barre informative nel motore** (3.8c): il costo di adattare annualizzazione e funding a barre
  non equispaziate supera il beneficio finché gli item T0-T2 non sono consumati.

## 8. Prossimo passo operativo

*(aggiornato 2026-07-23, a roadmap chiusa)*

Rilanciare una **caccia completa (hunt + holdout) col motore di oggi**: embargo attivo, funding
firmato, slippage onesto, segnale Ora-UTC e MFI/OBV nel pool del Composer, validazione CPCV e
permutation test sui candidati. È la stessa domanda del 2026-07-20, ma posta a una macchina che
nel frattempo è diventata onesta su ogni punto in cui si stava ingannando — e con più informazione
(order flow, eventi, breadth) nel pool. Per le idee NUOVE oltre questa roadmap, vedi
`docs/ROADMAP-FRONTIERE-PROFITTO.md`.
