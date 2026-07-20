# Ricerca di edge, luglio 2026 — cinque angoli, un controllo, nessuna opportunità

Data: 2026-07-20 · Universo: 45 coppie, 12,14M candele, timeframe 1m→1d
Costi applicati ovunque: fee 0,1%/lato + slippage 0,05%/fill ⇒ **round-turn 0,30%**

**Esito in una riga:** su questo universo e questi dati non è emerso alcun edge che sopravviva
ai costi e alla correzione per test multiplo — e l'esperimento di controllo dimostra che è una
proprietà del mercato, non un difetto degli strumenti. **Ma la frontiera dei costi mostra che
almeno un candidato fallisce per l'esecuzione, non per il segnale**: il pareggio cade fra 0,120% e
0,200% di round-turn, cioè in un intervallo raggiungibile.

Questo documento esiste perché un risultato negativo ben misurato vale quanto uno positivo:
serve a non rifare la stessa strada fra sei mesi.

---

## Perché fidarsi di questi negativi: il controllo positivo

Cinque ricerche a vuoto ammettono due spiegazioni molto diverse — non c'è edge, oppure il
rilevatore è rotto — e finché non si distinguono, nessuna conclusione ha valore.

Si è quindi costruita una serie **sintetica con un edge piantato**: processo a ritorno verso la
media, oscillazione tipica ±2,6% attorno al livello, cioè quasi dieci volte il round-turn. Poi la
si è data alla **stessa** pipeline, con gli stessi costi e lo stesso walk-forward.

| Strategia | Sharpe OOS | Sharpe IS | Trade | DSR |
|---|---|---|---|---|
| BollingerMeanReversion | 6,82 | 6,64 | 21 | **1,00** |
| RsiOversold | 6,31 | 5,50 | 20 | **1,00** |
| Stochastic | 5,89 | 5,22 | 18 | **1,00** |
| VwapReversion | 3,03 | 2,19 | 37 | 0,96 |
| RegimeConditional | 1,90 | 2,04 | 16 | 0,20 |
| EventTrigger | 0,09 | −0,32 | 2 | 0,23 |

Tre cose, non una:

1. **La pipeline trova l'edge**, con Sharpe enormi.
2. **Trova la famiglia giusta**: le prime tre sono tutte mean-reversion, cioè esattamente il tipo di
   edge piantato; le direzionali restano in fondo. Non è fortuna.
3. **Il Deflated Sharpe sa dire di sì.** Era la preoccupazione più seria: un gate che rifiuta sempre
   è inutile quanto uno che accetta sempre. Qui dà 1,00 sui tre veri contro 0,69–0,81 sui migliori
   candidati reali. **Discrimina.**

Riproducibile: `dotnet run --project tools/PlatformExpand -- control` (seme fisso, la serie
sintetica viene rimossa dal DB in un blocco `finally`).

---

## I cinque angoli

### 1. Caccia ampia — 1d/4h/1h, 45 coppie

**445.280 combinazioni.** 180 candidati grezzi → 6 oltre i gate anti-rumore (Sharpe OOS ≥ 0,5,
almeno 20 operazioni) → **0 significativi al Deflated Sharpe**.

Migliore: Supertrend DOGE/USDT 1h — OOS 1,61, IS 1,49, 31 trade, **DSR 0,81** (soglia 0,95).

### 2. Caccia con finestre diverse — 30 coppie, incluso 15m

Ha prodotto Sharpe apparentemente molto migliori: 3,28, 3,00, 2,98. Ma con **2, 1 e 8 operazioni**.
Filtrando per numero di trade il quadro collassa in modo monotòno:

| soglia trade | candidati | miglior Sharpe OOS |
|---|---|---|
| ≥ 1 | 155 | 3,28 |
| ≥ 5 | 50 | 2,98 |
| ≥ 10 | 33 | 1,64 |
| ≥ 20 | 13 | **1,55** |

Gli Sharpe spettacolari vivono **tutti** nella zona 1–4 operazioni. È la firma del rumore, ed è la
ragione per cui il gate sul numero minimo di trade non è una formalità.

Due candidati compaiono in entrambe le cacce con parametri e conteggio trade identici. **Attenzione
a non sopravvalutarlo**: le due cacce coprono lo stesso periodo (2024-01 → 2026-03) e differiscono
solo nella suddivisione walk-forward. Dimostrano robustezza allo split, *non* conferma
out-of-sample.

### 3. Holdout — il test che ha chiuso la questione

Finestra 2026-03-01 → 2026-07-20, **mai usata in selezione**.

| | selezione | holdout | buy & hold |
|---|---|---|---|
| Supertrend DOGE 1h | Sharpe **+1,61** | Sharpe **−2,37**, netto −45,27% | −23,69% |
| DonchianBreakout DOGE 1h | Sharpe +1,41 | Sharpe **−3,21**, netto −50,32% | −23,69% |

Entrambi **peggio del non fare nulla**. Il Deflated Sharpe aveva ragione: 0,69–0,81 significava
"non dimostrato", e i dati vergini lo confermano.

Costi nell'holdout: 24,25% e 25,65% su 85 e 102 operazioni in quattro mesi e mezzo.

### 4. Caccia su timeframe lenti — 1d/4h, storia più profonda

R2 aveva misurato lì il cost drag più basso. **182.160 combinazioni**, 3 candidati oltre i gate,
0 significativi al DSR, **0 sopravvissuti all'holdout**.

Il caso più istruttivo dell'intera ricerca:

> **PriceSmaCross DOGE/USDT 4h — holdout: lordo +7,74%, costi 15,31%, netto −7,57%**

Il segnale **funzionava davvero**. Le commissioni l'hanno trasformato in una perdita, con
**0,3 operazioni al giorno** — non un turnover forsennato. Se l'edge lordo è dell'8%, bastano
poche operazioni per azzerarlo. È la tesi di R2 su un caso in cui il segnale c'era.

Nota: entrambi i candidati **battono il buy-and-hold** (−7,6% contro −25,3%; −12,0% contro
−54,1%) ma perdono comunque. Perdere meno del mercato non è un edge tradabile.

### 5. Pairs trading — angolo market-neutral

Motivato, non casuale: nell'holdout ogni candidato perdeva mentre DOGE faceva −25% e BCH −54%.
Perdere in un mercato che crolla non distingue una strategia rotta da una strategia lunga.

20 simboli, 4h, cointegrazione stimata **solo** in selezione e backtest **solo** su holdout.

- **17/190 coppie cointegrate (9%)** — coerente con una significatività del 5% più qualche
  relazione genuina. Su questo universo il test **non** risulta troppo liberale, contrariamente al
  timore dell'audit 2026-07.
- 1/8 sopravvive: ETC/ICP, +2,20% netto, 28 trade, maxDD 2,1%. **Non schierato**: è il migliore di
  8, a loro volta i primi di 17 su 190. Il numero effettivo di tentativi è alto e +2,20% ci sta
  comodamente dentro il rumore.

**Il risultato strutturale è più interessante del singolo sopravvissuto:**

| | drawdown massimo (holdout) |
|---|---|
| Strategie direzionali | 25% – 55% |
| Coppie market-neutral | **2,1% – 5,2%** |

Un ordine di grandezza. La proprietà market-neutral non è teorica: si misura. Sette coppie su otto
perdono comunque dopo i costi, quindi non c'è un edge da schierare — ma il **profilo di rischio** è
una categoria di cosa diversa dal rendimento, e questo dato vale per il futuro.

---

## Il filo conduttore: i costi, non i segnali

Il turnover è la variabile che decide, e lo si vede su tre livelli indipendenti:

1. **Strutturale** (R2): a 1m servono ~8,9 candele tipiche catturate per pareggiare i costi, contro
   1,1 a 1h.
2. **Aggregato** (R2): cost drag 3,4% a 1h → 8,9% a 15m → 24,2% a 5m → 76,9% a 1m, sulla stessa
   finestra.
3. **Su un caso reale** (angolo 4): un lordo di +7,74% diventa −7,57% netto per 43 operazioni.

Non serve un turnover forsennato per essere distrutti dalle commissioni: serve solo che l'edge
lordo sia piccolo, e sui dati reali lo è sempre.

---

## Due difetti trovati — entrambi corretti (2026-07-20)

**1. `PositionCloser` bloccava il rientro inverso. CORRETTO.** Le righe 168 e 307 impostavano
`state.LastOrderUtc`, che è lo stesso timestamp usato dal `SafetyChecker` per l'anti-spam **sugli
ingressi**. Su un'inversione di segnale: si chiude → `LastOrderUtc = T` → si tenta l'ingresso
opposto sulla stessa candela → `elapsed = 0` → **rifiutato**. Toccava **tutte e 12 le strategie**
del catalogo (tutte possono emettere `Signal.Short`). Con le soglie globali il ritardo era di una
candela; con un profilo di rischio R3 di ore. Osservato dal vivo: 430 ordini rifiutati su 500.

Le due righe sono state rimosse: le chiusure non passano dal `SafetyChecker` (gira solo in
`ExecuteOpenAsync` e nello slicing), quindi quel timestamp serviva soltanto a frenare l'apertura
successiva. Copertura in `SignalReversalThrottleTests`, due test in coppia — uno verifica che
l'inversione passi, il gemello che il freno sugli ingressi ravvicinati sia ancora attivo — e si è
verificato che rimettendo le righe il primo fallisce e il secondo no. Nessuna migrazione.

**2. Cointegrazione sui prezzi grezzi. CORRETTA passando ai log-prezzi.** AAVE/XLM era stata
accettata con **β = 575,29**, ed era **la peggiore delle otto** candidate (−14,14%, maxDD 15,1%,
tre volte le altre).

La correzione inizialmente ipotizzata — un tetto su |β| — era la diagnosi sbagliata: β sui prezzi
grezzi ha le unità di "prezzo di Y per prezzo di X", quindi 575 misura soprattutto che AAVE vale
~1000× XLM. Un tetto su |β| avrebbe bocciato coppie sane fra monete di prezzo diverso e lasciato
passare quelle rotte fra monete di prezzo simile. La regressione ora gira sui **log**, dove β è
un'elasticità adimensionale con valore di riferimento 1 per qualunque coppia. Non è solo più
comodo: `log Y − β·log X` stazionario equivale a `Y/X^β` costante, e per β = 1 quel portafoglio è
esattamente quello a controvalore uguale sulle due gambe — cioè quello che `PairsBacktestEngine`
apre davvero. Prima il segnale sorvegliava una combinazione β-pesata mentre l'esecuzione ne apriva
un'altra.

**Il risultato della verifica sui dati reali è però diverso da quello atteso**, e vale la pena
registrarlo (`CointegrationOnRealDataTests`, finestra 2024-01→2026-03, 4h, 4740 candele):

| | AAVE/XLM |
|---|---|
| elasticità log β | **0,687** — *dentro* la banda di sanità [0,5–2,0] |
| ADF sui log | **−2,981** contro CV MacKinnon −3,337 (11 lag) |
| esito | **non cointegrata** → non operabile |

A bocciarla non è il filtro sull'elasticità: è l'**ADF stesso**, una volta che gira sui log. La
stazionarietà dello spread era un artefatto della regressione in unità di prezzo. La banda di
plausibilità resta come rete di sicurezza, ma su questi dati non vincola nessuna coppia.

L'effetto è ampio e va guardato con sospetto quanto il difetto che corregge: sulle **91 coppie**
dell'universo a 4h, sui log ne risulta cointegrata **1 sola**. Al 5% di significatività il puro
caso ne darebbe ~4–5, quindi la nuova specificazione è se mai *conservativa*. Chiude il rilievo
"cointegrazione troppo liberale" dell'audit 2026-07 — ma conferma anche, dall'ennesimo angolo, che
sui dati veri non c'è un edge di pairs da schierare.

---

## Cosa NON vale la pena rifare

- Altre cacce a strategia singola variando finestre sugli stessi dati: quattro tentativi, quattro
  esiti negativi, e il controllo dice che non è colpa degli strumenti.
- Timeframe sotto i 15m: R2 ha già misurato che i costi li rendono inoperabili.
- Fidarsi di uno Sharpe alto senza guardare il numero di operazioni.

## La frontiera dei costi: due modi di fallire, non uno

Rigirando i candidati sull'holdout a livelli di costo diversi si separa qualcosa che nei numeri
netti sembrava identico.

**PriceSmaCross DOGE/USDT 4h**

| scenario | round-turn | netto |
|---|---|---|
| taker Binance | 0,300% | −5,67% |
| taker Bitget | 0,220% | −2,37% |
| taker + slicing perfetto | 0,200% | −1,54% |
| taker Bitget + slicing perfetto | 0,120% | **+1,90%** |
| maker Binance +BNB | 0,085% | **+3,45%** |
| maker Bitget | 0,080% | **+3,67%** |
| costo zero (limite) | 0,000% | +7,28% |

→ il pareggio cade **fra 0,120% e 0,200%** di round-turn.

*(I valori si spostano di qualche decimo fra un'esecuzione e l'altra perché la finestra di holdout
arriva fino a "oggi" e quindi si allunga. Le soglie restano stabili.)*

**VwapReversion BCH/USDT 4h** → in perdita **anche a costo zero** (−6,14%).

Il primo ha un segnale reale che la struttura di costo taker distrugge; il secondo semplicemente
non funziona. La distinzione cambia cosa è sensato fare: sul primo ha senso lavorare
sull'esecuzione, sul secondo no.

> **Cautela.** Il maker non è gratis in senso pratico: un ordine limite può non essere eseguito, e
> una strategia che *insegue* il prezzo — una crossover lo fa — non può fare il maker per
> definizione. Il +4,31% assume che ogni limite venga riempito al prezzo maker, il che è
> ottimistico. Questi numeri dicono qual è il **requisito** di esecuzione e se cada in un intervallo
> raggiungibile — cade — non che sia gratuito ottenerlo.

Comando: `dotnet run --project tools/PlatformExpand -- costfrontier`

## Cosa avrebbe senso provare

- **Lavorare sull'esecuzione**, non sui segnali. È la leva misurata qui sopra, con un bersaglio
  numerico: portare il round-turn sotto lo **0,12%**. Due strade, e conviene sapere cosa può dare
  ciascuna:

  **Slicing** (TWAP/VWAP/Iceberg/Adaptive in `Services/Execution`, QLIB-5, oggi solo in apertura su
  Testnet/Live e default-off). Riduce l'**impatto di mercato**, cioè lo slippage — non la
  commissione. Il suo tetto teorico è slippage zero: su Binance porta a 0,200% (ancora in perdita),
  su Bitget a 0,120% (appena in pareggio, +1,90%). È un limite ideale che lo slicing avvicina ma non
  raggiunge, quindi da solo è al margine.

  **Ordini maker.** Dà il margine vero (0,080%, +3,67%), ma richiede una capacità che oggi **non
  esiste**: l'intero percorso live piazza esclusivamente ordini `MARKET` — `SignalOrderBuilder`,
  `PositionOpener`, `PositionCloser` — quindi è sempre taker per costruzione. I client exchange
  supportano già `LIMIT` (Binance e Bitget), quindi manca il percorso nel motore, non
  l'integrazione. Attenzione però: un ordine limite può non essere riempito, e una strategia che
  insegue il prezzo — una crossover lo fa — non può fare il maker senza cambiare natura. È lavoro
  di progettazione, non una configurazione da accendere.
- **Portafoglio di coppie**: i drawdown a 2–5% sono l'unico risultato strutturalmente favorevole
  emerso. Un paniere di coppie poco correlate merita una misura, anche se le singole non guadagnano.
- **Orizzonti più lunghi del giornaliero**, dove il rapporto fra ampiezza del movimento e costo
  è più favorevole di qualunque cosa testata qui.

## Riproducibilità

```
dotnet run --project tools/PlatformExpand -- hunt        # caccia ampia 1d/4h/1h
dotnet run --project tools/PlatformExpand -- hunt slow   # solo timeframe lenti
dotnet run --project tools/PlatformExpand -- holdout     # valida l'ultima caccia fuori campione
dotnet run --project tools/PlatformExpand -- pairs       # angolo market-neutral
dotnet run --project tools/PlatformExpand -- control     # l'esperimento di controllo
dotnet run --project tools/PlatformExpand -- costfrontier # a quale costo i candidati diventano profittevoli
```
