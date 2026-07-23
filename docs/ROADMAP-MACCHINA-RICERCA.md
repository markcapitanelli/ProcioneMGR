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

#### T0.3 — Stop allo scarto dei campi klines — **M**

**Oggi**: `OhlcvData` ha solo OHLCV (verificato anche nello snapshot delle migration); il parsing
Binance scarta `k[7..10]`; Bitget scarta il quoteVolume.

**Aggancio**: 4 colonne **nullable** su `OhlcvData` (`QuoteVolume`, `TradeCount`, `TakerBuyVolume`,
`TakerBuyQuoteVolume`) + estensione del record `Ohlcv` + parsing + migration additiva + fase di
reingest idempotente (upsert sull'indice unico esistente). Nullable = le candele storiche non
ancora rifetchate restano valide.

**Validazione**: invarianti post-reingest (`TakerBuyVolume ≤ Volume`,
`QuoteVolume ≈ Volume × prezzo tipico`), % righe popolate per serie.

### T1 — Prima il giudice, poi il labeling

*(L'ordine è deliberato: 1.5 e 1.6 rafforzano il metro con cui giudicheremo 1.V e 1.4.)*

#### T1.5 — Block/stationary bootstrap + permutation test — **M**

**Oggi**: `MonteCarloAnalyzer` e `LeverageAdvisor` fanno solo resampling **iid** dei trade — che
distrugge l'autocorrelazione, cioè proprio la struttura che rende pericolosi i drawdown. Nessun
permutation test.

**Aggancio**: modalità *stationary block bootstrap* (Politis-Romano) in `MonteCarloAnalyzer`
(lunghezza media di blocco parametrica), riusata da `LeverageAdvisor`; nuovo
`Services/Validation/PermutationTest` (permutazione dei rendimenti lungo il tempo → distribuzione
nulla dello Sharpe) integrato come colonna aggiuntiva nell'`OverfittingGate`. È la codifica
**diretta** della lezione t=141: la randomizzazione valida è temporale.

**Validazione**: calibrazione — p-value uniforme su rumore bianco; p basso sull'edge piantato del
controllo.

#### T1.6 — CPCV esteso al percorso strategie — **M**

**Oggi**: `Services/Validation/CombinatorialPurgedCv.cs` esiste (C(N,k) split con purge/embargo,
AFML cap. 12) ma è usato solo nei gate ML. Il percorso strategie ha un solo train/test walk-forward
+ un solo holdout: **un** percorso out-of-sample per candidato.

**Aggancio**: generatore di finestre alternativo in `OptimizationEngine` che riusa
`CombinatorialPurgedCv` → *distribuzione* di Sharpe OOS multi-percorso per candidato + PBO via
`BacktestOverfitting` già esistente. Risponde esattamente alla richiesta del proprietario: più
out-of-sample **dagli stessi dati**.

**Validazione**: edge piantato sopravvive su tutti i percorsi; configurazione volutamente
overfittata → PBO alto.

#### T1.V — La volatilità come TARGET di predizione — **S/M** ⭐ *(rapporto valore/effort migliore del tier)*

**Oggi**: tutti i target ML sono rendimenti futuri (`DatasetBuilder`, label = forward return). 0
sopravvissuti su 445.280 dice che la **direzione** non è prevedibile su questi dati con questi
strumenti. Ma la mossa quant classica è: il **rischio** è prevedibile quasi sempre (la volatilità è
persistente — lo stesso fatto stilizzato dietro il GARCH già in piattaforma).

**Aggancio**: `TargetKind` in `DatasetBuilder` (`ForwardReturn` | `ForwardAbsReturn` |
`ForwardRealizedVol`); consumatori già pronti: `LeverageAdvisor` (input di rischio previsto),
vol-targeting del motore (già implementato, oggi alimentato dalla realizzata), regime detector.

**Validazione**: il forecast batte la baseline EWMA su QLIKE/MSE out-of-sample; test economico:
versione vol-targeted di una strategia esistente vs raw su holdout. *Onestà: migliora la gestione
del rischio, non promette rendimento — coerente con quanto già misurato sul dosaggio.*

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

#### T2.7 — Event-study rigoroso — **M**

**Oggi**: `NewsImpactAnalyzer` calcola medie post-evento semplici (1h/4h/24h) sugli eventi
alt-data. Manca: abnormal return vs baseline, finestra pre-evento (leakage/anticipazione), test
placebo, e soprattutto un rilevatore di **eventi di mercato** che funzioni su tutta la profondità
OHLCV (l'alt-data ha 20 giorni di storia, l'OHLCV sei anni).

**Aggancio**: estendere `NewsImpactAnalyzer` (abnormal return, pre-evento, placebo con date casuali
— riusa T1.5); nuovo `MarketEventDetector` (crash oltre soglia, vol spike, volume blowout) che
genera eventi dai prezzi stessi; "finestre nominate" → replay del `BacktestEngine` sulle finestre
degli episodi della §2 (tutti coperti a 1d, dal 2024 a 1h).

**Validazione**: placebo (date casuali → effetto nullo); controllo positivo sui giorni FOMC/CPI se
coperti; output = libreria di flag-evento usabili come filtri di strategia, che passano il gate
standard.

#### T2.S — Wiring della stagionalità — **S**

**Oggi**: `Services/Analysis/CyclicalAnalyzer.cs` calcola **già** bias orario, giorno-settimana e
stagionalità annuale (con robustezza percentuale-concordi) — ma è consumato solo dalla pagina
`/market-analysis`. Nessuna strategia può dire "opera solo nelle ore X".

**Aggancio**: filtro orario/sessione parametrico nelle strategie (via `SignalCatalog`/parametri
strategia), alimentato dai bias di `CyclicalAnalyzer`; i 525k candele/simbolo a 1m rendono la stima
oraria statisticamente affrontabile.

**Validazione**: gate standard con enfasi sulla replica su finestre disgiunte — i bias orari sono
notoriamente instabili, e il documento lo dice.

### T3 — Volumi e order flow

#### T3.8a — OBV/MFI/VWAP riusabili + volume nei regimi — **M** *(non dipende da T0.3)*

**Oggi**: Alpha158 ha già fattori volume (CORR, CORD, VMA, VSTD, WVMA, VSUMP/N/D) ma
OBV/MFI/VWAP non esistono come indicatori riusabili (il VWAP vive incastonato in una strategia);
il K-means dei regimi usa 4 feature e **esclude deliberatamente il volume**.

**Aggancio**: OBV/MFI/VWAP in `TechnicalIndicatorsService` + voci in `SignalCatalog` (li rende
"cacciabili" dal `StrategyComposer`); quinta feature (volume ratio) nel `RegimeDetector` — con nota
esplicita: cambia le etichette dei regimi, impatto su `RegimeConditionalStrategy` da misurare.

**Validazione**: IC incrementale via `FactorEvaluator` e **bassa correlazione** coi fattori volume
già esistenti (= informazione nuova, non duplicata); per i regimi: stabilità dei cluster e holdout
della strategia regime-aware.

#### T3.8b — Feature order-flow dai campi recuperati — **M** *(dipende da T0.3)*

**Aggancio**: nuovi `IAlphaFactor` — imbalance taker (`TakerBuyVolume/Volume`), dimensione media
del trade (`Volume/TradeCount`), e loro derivate rolling. Gate: stesso di 3.8a.

#### T3.8c — Barre informative → backtest — **L, OPZIONALE dichiarato**

`BarBuilder` (volume/dollar bars) esiste ma il motore assume spaziatura temporale regolare
(annualizzazione dello Sharpe, funding pro-rata via `candleHours`). Adattarlo è il pezzo più
costoso e rischioso del tier: dichiarato opzionale perché non blocchi i quick win.

### T4 — Mercati relativi interni — **S/M**

**Oggi**: nessuna coppia non-USDT tracciata, ma l'ingestione è già agnostica (verificato: nessun
hardcoding USDT nel percorso OHLCV). "Un mercato diverso" in miniatura è già a portata: i prezzi
**relativi** (ETH/BTC, alt/BTC) hanno dinamiche proprie, e la breadth interna (% dei simboli sopra
la propria MA50) è una feature di regime calcolabile con zero dati nuovi.

**Aggancio**: tracciare ETH/BTC e 3-4 alt/BTC (`TrackedSeries`, funziona oggi); estendere
`RollingPairsSpreadAnalyzer` (già sui log) ai ratio; breadth nel `RegimeDetector`. La
BTC-dominance vera richiederebbe market cap esterni: sostituita dalla breadth, 100% in casa.

**Validazione**: feature importance nel regime + gate standard per ogni strategia sui ratio.

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
| 0.1 Purge/embargo WF | T0 | S | — | delta OOS con/senza; edge piantato passa |
| 0.2 Funding storico | T0 | S/M | — | addebito firmato esatto; A/B short |
| 0.3 Campi klines | T0 | M | — | invarianti post-reingest |
| 1.5 Bootstrap+permutation | T1 | M | — | p uniforme su rumore, basso su edge |
| 1.6 CPCV strategie | T1 | M | 1.5 utile | distribuzione OOS; PBO su overfit noto |
| 1.V Vol come target | T1 | S/M | — | batte EWMA su QLIKE; test economico |
| 1.4 Triple-barrier+meta | T1 | L | 1.5, (weights) | edge asimmetrico recuperato; precision ↑ a DSR pari |
| 2.7 Event-study | T2 | M | 1.5 (placebo) | placebo nullo; controllo positivo FOMC |
| 2.S Stagionalità | T2 | S | — | replica su finestre disgiunte |
| 3.8a OBV/MFI/VWAP+regimi | T3 | M | — | IC incrementale, bassa correlazione |
| 3.8b Order-flow | T3 | M | **0.3** | idem |
| 3.8c Barre→backtest | T3 | L | 0.3 | opzionale dichiarato |
| 4.9 Mercati relativi | T4 | S/M | — | feature importance; gate standard |
| 5.10 Confidenza→Kelly | T5 | M | **modello oltre il gate** | crescita > sizing fisso in Paper |

**Percorso consigliato**: T0.1 → T0.2 → 1.5 → 1.V → T0.3 → 1.6 → 2.7 → 3.8a → … (T0.0 fatto;
3.8a e 2.S parallelizzabili in qualunque momento; 1.4 quando c'è spazio per un item L).

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

T0.1 + T0.2 (+ 1.5 se c'è tempo): un pomeriggio di lavoro, e da lì in poi ogni numero che la
piattaforma produce è più onesto di quello di ieri — che è l'unico modo sensato di "spremere i dati
già in casa".
