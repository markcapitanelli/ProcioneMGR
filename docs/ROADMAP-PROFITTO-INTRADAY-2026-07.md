# ProcioneMGR — Roadmap "Profitto Intraday": cambiare classe di edge, non fare più tentativi

*2026-07-24. Quinta roadmap di metodo. Nasce da una domanda diretta dell'utente — "voglio di più,
molto di più; più operazioni al giorno, più guadagni; mi sembra ancora che manchi/sbagli qualcosa;
voglio il massimo dell'onestà" — e da due verifiche fatte oggi: la lettura del codice del motore di
fill/costo e della copertura dati, e una ricerca sulla letteratura quant crypto 2024-2026. Ogni
affermazione "esiste/manca" è verificata (file citati, query DB, fonti). Vincolo permanente: il PC
attuale non è il server, quindi la reggibilità a 5m NON è un criterio — conta che i calcoli siano
ONESTI, non che girino veloci qui.*

---

## 0. La diagnosi onesta: perché quattro cacce a zero non sono sfortuna

La piattaforma ha cercato un edge **quattro volte** con metodi indipendenti (445k combinazioni →
0 al DSR; 5 angoli di ricerca → tutti negativi; il gemello sintetico che conferma i negativi; la
caccia `huntedge` di oggi → 0 confermati). Ogni volta la conclusione è stata la stessa. **Non è
sfortuna: è il risultato atteso**, e capire *perché* è la chiave di tutto.

Tutte e quattro le cacce hanno cercato lo stesso TIPO di edge: **regole direzionali tecniche su
OHLCV di un singolo simbolo**. Questo è, in tutta la finanza crypto, l'angolo **più efficiente e
più arbitraggiato** — la letteratura 2024-2026 lo dice chiaramente: i mercati crypto sono maturati,
spread più stretti, più concorrenza istituzionale, e "il selvaggio 2017-2021" è finito
([Quantt 2026](https://www.quantt.co.uk/resources/crypto-quant-strategies-2026)). Cercare alpha
direzionale con RSI/MACD/Supertrend su BTC 1h è come cercare monete d'oro in una spiaggia già
setacciata da mille persone con metal detector migliori del tuo.

**Gli edge che sopravvivono nel 2025 sono di altra natura**, e la piattaforma li ha appena
sfiorati o non li ha toccati:

| Classe di edge | Evidenza 2024-25 | Stato in ProcioneMGR |
|---|---|---|
| **Market-neutral / stat-arb (cointegrazione)** | Sharpe **1,58-2,45**, BTC-ETH cointegrati, 37/90 coppie | motore pairs c'è, ma la caccia ha usato un ADF statico severissimo → **1 sola coppia** trovata (artefatto, non verità) |
| **Cross-sectional multi-fattore** | Sharpe > 1; combinare decine di indicatori via elastic net (CTREND) batte i costi | abbiamo Alpha158 + IC selection + stacking, ma la discovery sceglie strategie SINGOLE, non costruisce un portafoglio ranked |
| **Carry sul funding** | flusso strutturale, non previsione | **misurato** (F1.b: netto 5-12%/anno), mai operativo |
| **Microstruttura / order book (OBI, microprice, market making)** | il vero edge intraday; feature LOB stabili cross-asset | **impossibile oggi**: non abbiamo order book né tick |

**Le tre cose che "mancano o sbagliano", in ordine di importanza:**

1. **Abbiamo cercato nella classe sbagliata.** Il rimedio non è "più tentativi direzionali" ma
   **cambiare classe**: market-neutral, cross-sectional, carry. Sono anche, non a caso, le classi
   che danno **più operazioni al giorno** (molte piccole posizioni ranked, ribilanci frequenti) —
   esattamente ciò che l'utente chiede — ma con rischio direzionale ridotto.

2. **Il modello di fill/costo non è di grado intraday, quindi i calcoli a 5m MENTONO.** Verificato
   nel codice (`BacktestEngine.cs`): il modello maker esiste (limite appoggiato, fill se la candela
   tocca il prezzo), ma (a) assume **toccato = riempito**, ignorando la **posizione in coda** — nella
   realtà il prezzo attraversa il tuo limite senza riempirti; (b) si applica **solo agli ingressi**,
   non alle uscite; (c) il costo è una **percentuale piatta**, mentre a 5m il gioco È lo spread, che
   VARIA per minuto (F8 l'ha misurato: :00/:30 larghi, :29/:44/:58 stretti). A alta frequenza queste
   tre assunzioni sono la differenza fra profitto e rovina. **Questo è il "qualcosa che sbaglia" che
   l'utente sente**: un backtest a 5m oggi darebbe un numero, e quel numero non sarebbe affidabile —
   ottimista per un maker, pessimista per un taker, comunque non vero.

3. **I dati intraday sono incompleti.** Verificato con query DB: i campi order-flow (taker volume,
   n. trade) sono popolati al 100% su 1h/4h/1d ma **allo 0-0,6% su 1m/5m/15m** — proprio dove
   servirebbe l'alta frequenza, i fattori order-flow non hanno dati. E non esiste alcuna serie di
   order book o tick: solo barre OHLCV. Il materiale grezzo dell'edge intraday non è in casa.

**La conclusione onesta, senza giri di parole**: la piattaforma è una **macchina di ricerca
onesta eccellente** ma una **macchina di scoperta-edge mediocre**, perché è stata puntata sulla
classe di edge sbagliata con un realismo di fill/costo inadatto all'alta frequenza. Per ottenere
"molto di più" NON serve più potenza di calcolo né più tentativi dello stesso tipo: serve
**cambiare i tre punti sopra**. Questa roadmap fa esattamente questo, in tre tier di fondamenta
(dati, fill, metodo) e uno di edge — tutti fondati su evidenza, tutti col gate che non si negozia.

---

## 1. Tier D — DATI: rendere l'intraday onesto prima di crederci

### D1 — Reingest order-flow su 1m/5m/15m — **S** ⭐ (il quick win)

**Oggi**: `reingestx` fu eseguito solo su 1d/4h/1h. Su 1m/5m/15m i campi `TakerBuyVolume`,
`TradeCount`, `QuoteVolume` sono NULL (verificato: 0/3,15M su 1m, 30k/4,9M su 5m). Quindi i fattori
`TakerImbalance`/`AvgTradeSize` (3.8b, con IC>0 misurato su 1h/4h) su intraday **non producono
segnale** — restituiscono null per costruzione.

**Fatto da fare**: `reingestx "1m,5m,15m"` sui 6 simboli con 1m. Cheap, già scritto, sblocca
l'order flow proprio dove serve l'alta frequenza. Nessun rischio: la regola di merge non azzera
nulla.

**Validazione**: IC dei fattori order-flow su 5m/15m (fase `orderflow` estesa) — è informazione
nuova a intraday come lo era a 1h?

### D2 — Cattura order book + trade tape (accumulo, per il futuro server) — **M/L**

**Oggi**: nessun order book, nessun tick. È **il** gap dati dell'edge intraday. La letteratura
2024-25 sulla microstruttura crypto ([arXiv 2506.05764](https://arxiv.org/pdf/2506.05764),
[hftbacktest OBI](https://hftbacktest.readthedocs.io/en/latest/tutorials/Market%20Making%20with%20Alpha%20-%20Order%20Book%20Imbalance.html))
mostra che l'order book imbalance e il microprice sono predittivi a 1 secondo e le feature LOB sono
stabili cross-asset — ma richiedono i dati LOB, che non abbiamo.

**Fatto da fare**: un collector (stessa architettura di F4/liquidazioni) che accumula **snapshot L2
depth + trade tape** per 3-5 majors, in una tabella dedicata. Come le liquidazioni, il valore è
l'ACCUMULO: non ricostruibile a posteriori, va iniziato adesso. **NB vincolo EEA**: lo stream
futures Binance è bloccato da questa postazione (scoperto oggi con `streamdiag`) → usare lo **SPOT**
Binance (funziona) o **Bitget**. Sul futuro server, colocato e non bloccato, questo diventa la base
di E4 (market making).

**Validazione**: igiene del dato ora (coerenza depth/tape); il consumo (E4) quando c'è storia.
**Onestà**: è un investimento nel 2027, non un edge di domani. Ma senza, l'edge intraday "vero"
resta irraggiungibile.

### D3 — Funding allineato alle barre intraday — **S**

Il funding storico (2019+) è in casa e il backtest lo consuma su 1h/4h. Allineare il
`FundingRateLookup` alle barre 5m/15m (già a gradini, serve solo il passo) rende onesto il costo di
carry per le strategie intraday con posizioni overnight. Piccolo, abilita E1/E3 a intraday.

---

## 2. Tier F — FILL/COSTO: perché 5m diventi credibile

### F-queue — Modello di fill maker consapevole della coda — **M** ⭐

**Oggi** (`BacktestEngine.cs:318-354`): un limite maker si riempie se la candela **tocca** il
prezzo. È l'ottimismo classico del backtest maker: nella realtà, se sei in fondo alla coda al tuo
livello, il prezzo può toccarlo e ripartire senza eseguirti.

**Fatto da fare**: fill **probabilistico** — dato che la candela ha toccato il livello, la
probabilità di fill dipende dal volume scambiato a quel prezzo relativo alla tua size e da una
stima della coda (proxy: quanto la candela è penetrata OLTRE il limite — se solo sfiorato, fill
improbabile; se attraversato con volume, fill probabile). Più: estendere il maker anche alle
**USCITE** (oggi taker-at-close) e modellare i **fill parziali**. È il pezzo che decide se una
strategia ad alta frequenza è profittevole o no.

**Validazione**: su una strategia nota, il tasso di fill del modello deve scendere verso valori
realistici (non 100% dei touch); il PnL maker deve degradare verso quello taker man mano che la
size cresce (impatto di coda). Test di monotonia.

### F-spread — Spread e costo tempo-varianti dal profilo del minuto — **S/M**

**Oggi**: costo = percentuale piatta. **F8 ha già misurato** che range e volume variano
sistematicamente per minuto-dell'ora (:00/:30 agitati, :29/:44/:58 calmi). Lo spread proxy
(High-Low della barra, o dal tape quando D2 c'è) può diventare il costo EFFETTIVO del taker e
l'offset del maker per quel minuto. Così il backtest sa che essere maker in un minuto calmo rende, e
taker in un minuto volatile costa — cosa che la percentuale piatta nasconde.

**Validazione**: il costo medio implicito per il backtest deve riprodurre il profilo F8;
A/B costo-piatto vs costo-per-minuto su una strategia intraday.

### F-impact — √-impatto nel dimensionamento intraday — **S**

QLIB-5 ha già l'impatto √ (Almgren-Chriss); va **cablato nel path intraday** così che una size
grande su un minuto illiquido paghi l'impatto che pagherebbe davvero. Piccolo, chiude il cerchio dei
costi.

---

## 3. Tier E — le classi di EDGE che valgono (fondate su evidenza)

### E1 — Stat-arb cross-sectional / cointegrazione 2.0 — **M/L** ⭐⭐ (il candidato più forte)

**Ipotesi economica.** La cointegrazione fra majors crypto è **reale e documentata**: Sharpe
1,58-2,45, BTC-ETH e ETH-LTC cointegrati, 37/90 coppie
([IJSRA 2026](https://ijsra.net/sites/default/files/fulltext_pdf/IJSRA-2026-0283.pdf),
[Financial Innovation, copula](https://link.springer.com/article/10.1186/s40854-024-00702-7)). È
**market-neutral** (rischio di mercato eliso) → poche perdite direzionali, e per costruzione fa
**molte operazioni** (entra/esce ad ogni divergenza/convergenza dello spread): esattamente ciò che
l'utente chiede.

**Perché la nostra caccia ne trovò solo 1.** Verificato: la ricerca precedente usò un ADF **statico
e severissimo sui log**, e su 91 coppie 4h ne risultò cointegrata 1 sola. La letteratura che ottiene
Sharpe 2+ usa invece: **lookback DINAMICO** (la relazione cambia nel tempo), **filtro di
volatilità**, **trailing stop**, e **copule** (dipendenza non-lineare) invece della sola distanza.
Il nostro 1-su-91 non è "non c'è edge", è "abbiamo usato lo strumento più rigido possibile".

**Fatto da fare**: estendere `RollingPairsSpreadAnalyzer`/`EngleGrangerCointegrationTest` con
lookback rolling adattivo + filtro vol + stop, applicato al **pannello /BTC** (già ingerito, 4.9) e
alle majors; screening con la banda z-score e una versione copula-based. Il motore two-leg
(`PairsBacktestEngine`) c'è già.

**Validazione**: gate standard (edge piantato su spread sintetico cointegrato → recuperato;
holdout; gemello sintetico) + la lezione anti-t141 (randomizzare solo nel tempo). **Onestà**: la
cointegrazione è instabile (le coppie si de-cointegrano); serve il ri-test rolling, e va giudicata
sul PnL netto delle DUE gambe coi costi veri (Tier F), non sullo spread teorico.

### E2 — Portafoglio cross-sectional multi-fattore (stile CTREND) — **M/L** ⭐

**Ipotesi economica.** Un singolo indicatore è debole e arbitraggiato; **la combinazione
regolarizzata di decine di segnali** (elastic net) batte i costi e sopravvive
([Quantt](https://www.quantt.co.uk/resources/crypto-quant-strategies-2026): "CTREND combina decine
di indicatori via elastic net, batte le alternative dopo i costi"). E il **cross-section** —
comprare i forti relativi, vendere i deboli relativi su tutto l'universo — dà molte piccole
posizioni ranked = **più operazioni, diversificate, meno rischio idiosincratico**.

**Perché non l'abbiamo.** Abbiamo tutti i pezzi — Alpha158 (150+ fattori), i fattori order-flow, la
IC feature selection, lo `StackedReturnPredictor` — ma la Discovery sceglie **strategie singole** e
il ML predice **un simbolo alla volta**. Manca l'assemblatore: rank cross-section a ogni barra,
long top-k / short bottom-k, ribilancio, un solo portafoglio.

**Fatto da fare**: nuovo percorso "cross-sectional": (1) a ogni barra calcola i fattori su TUTTO
l'universo; (2) combinali in un punteggio (elastic net / IC-weighted, addestrato out-of-fold
purged); (3) rank, long top-k / short bottom-k a pesi ~market-neutral; (4) ribilancio con costi
veri. Riusa `FactorEvaluator`, `PurgedTimeSeriesCv`, il motore backtest per gamba.

**Validazione**: IC del punteggio combinato cross-section; il portafoglio long-short passa
holdout + gemello sintetico (il gemello va generato PRESERVANDO la cross-correlazione — nota di
metodo: qui il nullo è più delicato, va costruito con block bootstrap multivariato). **Onestà**: è
l'item metodologicamente più ricco; il rischio è l'overfitting del combinatore — il purged CV e il
gemello sono lì per questo.

### E3 — Carry sul funding operativo (Bitget) — **M**

**Ipotesi economica.** Già **misurata** (F1.b): long spot + short perp quando il funding
annualizzato è alto → netto 5-12%/anno sui 6 simboli, robusto alle soglie. È un **flusso**, non una
previsione — il candidato con la base economica più solida che abbiamo.

**Fatto da fare**: dalla misura all'operatività su **Bitget** (l'unico exchange usabile per
l'utente): strategia delta-neutra su Bitget spot + perp, con il funding Bitget vero, testata in
Paper→Testnet. Richiede: lettura funding Bitget, gestione due gambe, e il vincolo che Bitget demo
Futures ha equity 0 (va finanziato il wallet demo Futures, non solo Spot).

**Validazione**: A/B a funding zero (netto = −costi); backtest sul funding Bitget storico; forward
Paper. **Onestà**: il lordo è un limite superiore (ignora rischio di base e capacità); il netto
dipende dai costi delle due gambe — il tipo di domanda che Tier F rende onesta.

### E4 — Market making con alpha (OBI) — **L, CONDIZIONATO a D2**

**Ipotesi economica.** Il vero "molte operazioni al giorno con guadagno": **quotare bid/ask e
guadagnare lo spread**, inclinando le quote con l'order book imbalance
([hftbacktest](https://hftbacktest.readthedocs.io/en/latest/tutorials/Market%20Making%20with%20Alpha%20-%20Order%20Book%20Imbalance.html)).
Inverte la logica di R2 (i costi non li paghi, li INCASSI). È la risposta strutturale alla domanda
dell'utente.

**Precondizione dura**: richiede **D2** (order book + tape) e il **futuro server** (latenza, colocazione
logica). Senza order book, un backtest di market making è fantasia. Dichiarato in fondo per onestà:
è l'endgame, non l'inizio — ma è la direzione in cui "più operazioni + più guadagni" ha davvero
senso.

**Validazione**: simulazione su LOB reali (D2) con il modello di coda di F-queue; il PnL è
spread-catturato − selezione avversa − impatto. Il gate misura se l'alpha OBI aggiunge sopra il
market making neutro.

---

## 4. Tier M — METODO: lo strato di labeling che amplifica gli edge

### M1 — Triple-barrier + meta-labeling — **L** ⭐ (era 1.4, ora giustificato dall'evidenza)

**Ipotesi.** La letteratura crypto 2024-25 è esplicita: **information-driven bars + triple-barrier +
meta-labeling insieme MIGLIORANO le strategie**
([Financial Innovation 2025](https://link.springer.com/article/10.1186/s40854-025-00866-w),
[MDPI pair trading](https://www.mdpi.com/2227-7390/12/5/780),
[Hudson & Thames](https://hudsonthames.org/does-meta-labeling-add-to-signal-efficacy-triple-barrier-method/)).
- **Triple-barrier**: etichetta col percorso (quale barriera — profit/stop/tempo — si tocca prima),
  non col rendimento a orizzonte fisso. A intraday, dove lo stop conta su OGNI trade, è essenziale.
- **Meta-labeling**: un modello che decide **QUALI** segnali eseguire e **quanto grandi**,
  sopprimendo i falsi positivi. È **l'unico modo onesto di fare "più operazioni"**: non più segnali
  d'ingresso, ma un filtro/sizer che alza la precision senza sacrificare la recall.

**Fatto da fare**: `Services/ML/Labeling/` (triple-barrier con soglie dai percentili di escursione
di `ExcursionAnalyzer`); meta-modello out-of-fold purged su `StackedReturnPredictor`; consumo come
decoratore di `IStrategy` (filtra sotto soglia di probabilità) e come moltiplicatore di size. Con il
triple-barrier diventano quasi obbligatori i **sample weights** per label sovrapposte.

**Validazione**: edge piantato con asimmetria di barriera nota → recuperato; il meta-modello alza la
precision a DSR pari o migliore sull'holdout. **Onestà**: il meta-labeling **amplifica** un edge, non
lo crea — va applicato agli edge di Tier E che passano il gate, non alle regole OHLCV morte.

### M2 — Barre informative → backtest — **L, da rivalutare** (era 3.8c)

`BarBuilder` (volume/dollar bars) esiste ma il motore assume spaziatura temporale. La letteratura le
accoppia al triple-barrier; ora che l'intraday è l'obiettivo, il beneficio (barre a informazione
costante, meno rumore di microstruttura) potrebbe superare il costo di adattare annualizzazione e
funding. Rivalutare dopo M1.

---

## 5. Tabella riassuntiva e percorso onesto

| Item | Tier | Effort | Classe | Valore atteso (onesto) | Validazione |
|---|---|---|---|---|---|
| D1 order-flow intraday | Dati | S | quick win | sblocca i fattori order-flow a 5m/15m | IC intraday |
| D2 order book + tape | Dati | M/L | investimento | base dell'edge intraday vero (2027) | igiene dato ora |
| D3 funding intraday | Dati | S | onestà | costo carry corretto a intraday | — |
| F-queue fill maker in coda | Fill | M | **onestà 5m** | ✅ **COSTRUITO 2026-07-24**: penetrazione vs touch, testato (REPORT-E1-STATARB) | monotonia fill/size |
| F-spread per-minuto | Fill | S/M | onestà | costo reale = spread del minuto (F8) | A/B piatto vs per-minuto |
| F-impact √ intraday | Fill | S | onestà | size grande paga l'impatto vero | — |
| **E1 stat-arb cointegr. 2.0** | Edge | M/L | **market-neutral** | ✅ **COSTRUITO+MISURATO 2026-07-24**: filtro vol + fase statarb + gemello; 0 confermate su 4h/1h (77-90 trade/coppia = +operazioni SÌ ma perdono ai costi) → REPORT-E1-STATARB | gate + gemello + rolling |
| **E2 cross-sectional multi-fattore** | Edge | M/L | **cross-section** | +operazioni ranked, diversificate | IC combinato + purged CV |
| E3 carry funding operativo | Edge | M | carry | 5-12%/anno MISURATO | A/B funding 0 + forward |
| E4 market making OBI | Edge | L | microstruttura | l'endgame "molte op/giorno" | condizionato a D2 |
| **M1 triple-barrier + meta-label** | Metodo | L | amplificatore | alza precision; sizing onesto | edge piantato + precision↑ |
| M2 barre informative | Metodo | L | amplificatore | meno rumore intraday | dopo M1 |

**Percorso consigliato (in ordine di ritorno onesto):**

1. **Subito, cheap, sbloccante**: D1 (order-flow intraday) + F-spread + D3. Un pomeriggio, e i
   numeri intraday iniziano a essere onesti.
2. **La fondazione che rende credibile tutto il resto**: F-queue (il fill maker in coda) — senza,
   ogni backtest a 5m è una bugia in una delle due direzioni.
3. **Il primo edge vero da cacciare**: E1 (stat-arb cointegrazione 2.0) — market-neutral, più
   operazioni, e la letteratura dice che c'è. Poi E2 (cross-sectional multi-fattore).
4. **In parallelo, operativo**: E3 (carry su Bitget) — l'unico che abbiamo già misurato positivo.
5. **Il metodo che amplifica**: M1 (triple-barrier + meta-labeling) applicato agli edge di E1/E2/E3
   che passano il gate.
6. **L'investimento 2027**: D2 (order book) → E4 (market making) sul futuro server.

---

## 6. La verità, senza sconti

L'utente ha ragione: **manca qualcosa**, e ora è chiaro cosa. Non manca potenza di calcolo, non
mancano tentativi. Manca il cambio di **classe di edge** (da direzionale-tecnico a market-neutral /
cross-sectional / carry / microstruttura) e manca il **realismo di fill/costo** che rende onesti i
numeri ad alta frequenza. Le quattro cacce a zero non erano fallimenti: erano la prova, ripetuta,
che il pozzo che stavamo scavando è secco — e la conferma che il gate funziona, perché non ci ha mai
fatto schierare un'illusione.

"Più operazioni al giorno con più guadagni" è raggiungibile — ma per la strada del **cross-section
market-neutral** (molte piccole posizioni ranked, ribilanci frequenti) e, un giorno, del **market
making** (incassare lo spread invece di pagarlo), non per la strada di più segnali direzionali su
OHLCV. Questa roadmap è la mappa per scavare finalmente dove l'oro potrebbe esserci davvero — con
lo stesso gate spietato di sempre, perché è quel gate l'unica ragione per cui, il giorno che un
numero positivo comparirà, potremo credergli.

---

## 7. Fonti (ricerca 2026-07-24)

- Cosa funziona nel quant crypto 2025-26: [Quantt — Crypto Quant Strategies 2026](https://www.quantt.co.uk/resources/crypto-quant-strategies-2026);
  [1Token Crypto Quant Strategy Index (Oct 2025)](https://blog.1token.tech/crypto-quant-strategy-index-vii-oct-2025/);
  [QuantSeeker — Popular Investing Research 2025](https://www.quantseeker.com/p/popular-investing-research-in-2025)
- Stat-arb / cointegrazione: [IJSRA 2026 — Statistical Arbitrage Using Cointegration in Crypto](https://ijsra.net/sites/default/files/fulltext_pdf/IJSRA-2026-0283.pdf);
  [Financial Innovation — Copula-based trading of cointegrated crypto pairs](https://link.springer.com/article/10.1186/s40854-024-00702-7);
  [Palazzi 2025, Journal of Futures Markets](https://onlinelibrary.wiley.com/doi/full/10.1002/fut.70018)
- Microstruttura / order book: [Microstructural Dynamics in Crypto LOB (arXiv 2506.05764)](https://arxiv.org/pdf/2506.05764);
  [hftbacktest — Market Making with Alpha (OBI)](https://hftbacktest.readthedocs.io/en/latest/tutorials/Market%20Making%20with%20Alpha%20-%20Order%20Book%20Imbalance.html);
  [High-resolution microprice via Tsetlin Machines (arXiv 2411.13594)](https://arxiv.org/pdf/2411.13594);
  [Order Flow Imbalance signal — Markwick](https://dm13450.github.io/2022/02/02/Order-Flow-Imbalance.html)
- Triple-barrier / meta-labeling: [Financial Innovation 2025 — info-driven bars + triple-barrier + DL](https://link.springer.com/article/10.1186/s40854-025-00866-w);
  [MDPI — GA-driven triple-barrier per pair trading crypto](https://www.mdpi.com/2227-7390/12/5/780);
  [Hudson & Thames — Does meta-labeling add to signal efficacy?](https://hudsonthames.org/does-meta-labeling-add-to-signal-efficacy-triple-barrier-method/)

*Avvertenza d'uso: i numeri della letteratura (Sharpe 2+) sono su universi/periodi/costi degli
autori, spesso ottimistici; valgono come DIREZIONE, non come promessa. L'unica evidenza che conta è
quella che la piattaforma misurerà sui PROPRI dati col PROPRIO gate — inclusi Tier F (costi onesti)
e il gemello sintetico.*
