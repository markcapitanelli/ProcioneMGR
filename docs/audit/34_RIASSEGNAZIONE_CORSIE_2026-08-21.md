# La riassegnazione che non si può fare — 2026-08-21

> Il proprietario ha chiesto di aggiornare le corsie con i nuovi investimenti: togliere le vecchie
> coppie, mettere le nuove strategie. Ho fatto il lavoro. **Il risultato è che non c'è niente da
> mettere**, e le ragioni valgono più di una lista di gambe.

Metodo: censimento del database in sola lettura su cinque assi (archivio candidati, cacce recenti,
otto corsie, disponibilità dati, correlazioni), poi **tre proposte di allocazione indipendenti** da
angoli diversi — cadenza intraday, diversificazione, forza dell'evidenza — ciascuna passata sotto
**tre scettici** con l'incarico di demolirla, e una sintesi finale che doveva risolvere ogni difetto
marcato fatale o scartare la gamba.

---

## 1. L'imbuto

| passo | criterio | superstiti |
|---|---|---|
| 0 | candidati **distinti** in archivio (13.893 righe) | **738** |
| 1 | la terza finestra deve **esistere** (walk-forward ≠ selezione arrotondata) | — |
| 2 | tre finestre positive, ≥60 trade di selezione, ≥18 di holdout | **16** |
| 3 | cancello di costo ≥2× al denominatore della **barra di esecuzione** | **2** |
| 4 | provenienza risolvibile e non respinta da un gate | **1** |
| 5 | tenuta sui **25 giorni che nessuna caccia ha mai visto** | **0** |

È il **dodicesimo no**, ed è più informativo degli undici precedenti: non dice «lo Sharpe non è
significativo», dice che **tre degli strumenti con cui si misurava non misuravano**.

---

## 2. I tre strumenti rotti

### a. Il walk-forward non è un walk-forward

Su 13.893 righe, **9.665 (69,6%)** hanno `WalkForwardOosSharpe = round(SelectionSharpe, 2)`. Per
configurazione: **cfg 18 → 2.944 su 2.944, il 100%**; cfg 9 96,4%; cfg 16 97,8%; cfg 17 59,0%; cfg
19 solo 13,3%.

Significa che per due terzi dell'archivio **la terza finestra non esiste**: è la prima, arrotondata.
E la caccia 1h — quella su cui poggiava la proposta più conservatrice — non ha *mai* un fuori
campione oltre l'holdout. La «tripla più coerente dell'archivio» era `sel 0,639 / hold 0,675 /
wf 0,64`, dove `0,64 = round(0,639)`.

### b. Il PBO è uno scalare di run, non una proprietà del candidato

`SELECT count(DISTINCT "PanelPbo") per RunId` → **0 run su 162** hanno più di un valore. Il PBO
0,079 non è una proprietà delle gambe XLM: è condiviso da tutti i 64 candidati di quel run,
**compresi quelli con holdout −7,78**. Qualunque classifica costruita sul PBO è vuota.

### c. Manca il benchmark banale

Nessuna delle tre proposte confrontava la gamba con «tieni la stessa direzione e non fare niente»
sulla sua stessa finestra. Fatto il confronto (2026-03-01 → 07-27, annualizzato):

| simbolo | passivo | gamba | direzione | eccesso |
|---|---|---|---|---|
| DOT 1h | −2,54 | EventTrigger 1,175 | **short** | **−1,37** |
| ATOM 1h | −1,14 | EventTrigger 0,824 | short | −0,32 |
| FIL 4h | −0,88 | EventTrigger 0,675 | short | −0,21 |
| AVAX 5m | −1,47 | GridMR 1,078 | short | −0,39 |
| DOGE 15m | −1,97 | GridMR 1,875 | short | −0,10 |
| ADA 1h | −1,86 | EmaCross 1,242 | bidirez. | −0,62 |
| XLM 1h | +0,38 | RegimeCond 1,083 | long/fermo | +0,70 |
| ETC 4h | −0,97 | RsiOversold 1,655 | bidirez. | +0,69 |
| UNI 4h | +0,03 | GridMR 1,187 | both | **+1,16** |

**Sei gambe su nove non battono una posizione costante nella stessa direzione.** E quattro su sette
di una proposta erano **short-only su corsie Spot**, dove lo short esiste solo in Paper — senza che
la proposta se ne accorgesse: descriveva una griglia `Direction:1` (vende sulla forza) come «solo
long, compra sulla debolezza».

---

## 3. Il test che ha ucciso l'ultimo superstite

Le finestre di holdout delle cacce 17 e 18 finivano il **2026-07-27**. Cosa è successo dopo, su
barre 4h, dal 27/07 a oggi:

| BTC | CRV | SOL | XRP | ADA | DOGE | ATOM | ALGO | AVAX | DOT | ETC | XLM | FIL | UNI |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| +18,0% | +51,1% | +19,1% | +26,8% | +30,0% | +15,0% | +12,4% | +11,9% | +12,9% | +8,9% | +5,8% | +5,6% | +4,0% | +0,4% |

**Quattordici simboli su quattordici positivi.** L'intero menu è stato selezionato su un mercato che
scendeva, e da 25 giorni il mercato sale. Schierare una qualunque delle gambe short-biased significa
schierare **il regime precedente**.

I tre oggetti sopravvissuti ai filtri, riprodotti con un'implementazione indipendente (conteggio
trade coincidente: 21/21, 17/18, 12/13):

- **RsiOversold ETC/USDT 4h** (corsia 3) — mediana di detenzione dell'oggetto validato **200 ore
  (8,3 giorni)**, massimo 55 giorni: fuori mandato di per sé. Dal 26/07 è **short con una sola
  posizione mai chiusa**, per 26 giorni, dentro un rialzo del 5,8%. Sembra swing breve dal vivo
  (44h) solo perché l'applier aggiunge un bracket che l'oggetto validato non aveva
  (`BestStopVariant = 'base'`): **l'oggetto misurato e l'oggetto schierato non sono lo stesso**.
- **GridMeanReversion UNI/USDT 4h** (corsia 5) — sulla carta il miglior grigio dell'archivio.
  Poi la macchina a stati: i 18 trade di holdout esistono **solo perché la pipeline taglia la serie
  esattamente a `HoldoutFrom`**. Partendo da gennaio la griglia apre un long il 18/01 e non lo
  chiude più: **zero trade in tutta la finestra**. Il conteggio non è una proprietà della gamba, è
  una proprietà della data d'inizio del backtest. Ed è la stessa classe di difetto corretta il 20
  agosto — qui strutturale per la famiglia.
- **BollingerMeanReversion INJ/USDT 4h** — l'unico oggetto **stabile**: 21 trade identici partendo
  da gennaio o da marzo, mediana 40 ore, massimo 100 ore per costruzione, cadenza 4,3/mese, batte il
  passivo di +0,61 su un +61%. Poi i 25 giorni non visti: **−8,58% totale, −2,14% per trade**,
  contro +2,32% in holdout. Il segno si è invertito. E comunque `SelectionSharpe = 0,063 su 97
  trade`: il campione 4,6 volte più grande dice zero.

---

## 4. Che cosa ho fatto

| | |
|---|---|
| **Migrazione** `SessionActiveStrategies` | applicata (`ALTER TABLE "TradingEngineStates" ADD "ActiveStrategiesJson" text`), confermata all'avvio: *«Schema già allineato: nessuna migrazione pendente (28 note)»* |
| **Immagine del core** | costruita e importata nel nodo kind come `local-a422f7f8`, **verificata con crictl**; `newTag` bumpato nella kustomization con la motivazione |
| **Otto corsie** | **tutte ferme**, zero posizioni aperte |
| **Caccia 15m (cfg 20)** | `includeGreyZone = true` — le mancava l'unica chiave che la 19 aveva, ed è il motivo per cui aveva trovato 3 grigi su 2 simboli e proposto **zero** gambe |
| **Finestre scongelate (cfg 17 e 18)** | holdout portato da 2026-07-27 a **2026-08-21**, larghezza invariata (148 giorni), selezione spostata di pari passo. La colonna «Finestra» ora dice *«fino al 21/08 (oggi)»* |
| **Caccia 1h rilanciata** | run `240272c8` sui 25 giorni vergini |
| **`GreyZone.cs`** | rettificata la giustificazione del pavimento (vedi §5) |

### Perché le corsie sono state fermate, e perché era obbligatorio

`ActiveStrategiesJson` è NULL su **8 corsie su 8**, comprese quelle che risultavano in esecuzione: il
binario che gira è ancora quello vecchio, che quella colonna non la scrive. Al riavvio con l'immagine
nuova, il ripiego dichiarato di `RestoreActiveLegsAsync` avrebbe **risvegliato cinque corsie con
gambe selezionate sul regime precedente, quattro delle quali short**, dentro un mercato che sale da
25 giorni.

**Non fare nulla non era neutro.** Fermare prima, promuovere dopo.

---

## 5. Una rettifica su una mia decisione di ieri

Il 2026-08-20 ho abbassato il pavimento DSR della fascia grigia da 0,80 a 0,70, scrivendo che «a
0,70 entrano 49 candidati al mese, 24 dei quali finirebbero schierati».

**Quelle 49 sono righe, non candidati.** Le righe sono ~19 volte i candidati perché ogni caccia
notturna ri-registra la stessa griglia. I `CandidateKey` distinti mai finiti in banda [0,70; 0,95)
in tutta la storia dell'archivio sono **sei**, e due soli ne producono 42 (una per notte). Il «24
schierati» non è derivabile da sei distinti.

Peggio: quei due erano **usciti dalla banda undici giorni prima** che il pavimento scendesse a
prenderli, e non per un cambio di prestazione — il 2026-08-09 è entrata la correzione del conteggio
tentativi e il loro DSR è sceso di −0,089 esatti con Sharpe e trade **invariati**. Da allora il DSR
massimo mai osservato è **0,659**.

**L'abbassamento non ha ammesso nessuno.** Il valore resta 0,70 — a entrambe le altezze la porta è
chiusa, e 0,70 è difendibile per conto proprio — ma il commento ora dice la verità, e aggiunge la
regola che ne discende: chi vuole aprire quella porta deve agire sul **numeratore** (meno
combinazioni per run, o un edge più forte), non sulla soglia.

---

## 6. Corsia per corsia

| corsia | stato | perché | cosa deve accadere perché si riempia |
|---|---|---|---|
| **0** | ferma, residuo AAVE/USDT **1d** | dentro l'impronta auto-apply (`AutoApplyLaneFootprint = 3`): non va riempita a mano finché `AutoReapply` è acceso | un candidato che superi l'imbuto intero |
| **1** | ferma | `expectedSharpe 4,054` è un `OptimizationSharpe` **in campione** del 02/07, non un holdout. DOT è sceso del 48,8% nella finestra di selezione | idem |
| **2** | ferma | Supertrend: sel 0,134 su 254 trade contro hold 3,195 su 17 (**24×**), ed è il 3,195 scritto come atteso. Composite: 6 trade, mai aperta una posizione dal 14/08 | idem |
| **3** | ferma | mediana di detenzione **200 ore**, short da 26 giorni dentro un rialzo, e l'oggetto schierato ha stop che il validato non aveva | un 4h con mediana **misurata** sotto 72 ore che superi il passo 5 |
| **4** | ferma | `ProfitFactor 999` è la sentinella «zero trade in perdita», non una misura. `sel 0,091 su 19 trade`. L'unico trade reale è rimasto aperto 376 ore | idem |
| **5** | ferma | dipendenza dal percorso (vedi §3). Resta la **sonda di fedeltà**: al riavvio col binario nuovo il replay dice se la correzione dello specchio è viva | una strategia non dipendente dal percorso |
| **6** | ferma | LTC 15m è l'unica serie che abbia mai prodotto una mediana realizzata davvero intraday (**1,13 h**) | il posto naturale per il primo candidato **15m** che superi l'imbuto — ora che la cfg 20 ha `includeGreyZone` |
| **7** | ferma | STX è il simbolo più sottile dell'universo: round-trip alla barra **2,471%** contro un lordo di 2,048%/trade, copertura **0,83×** | il primo candidato che superi tutti e cinque i passi |

### La flotta di ieri, misurata

Sui rendimenti sovrapposti a 48 ore — l'orizzonte a cui le gambe tengono davvero — su 52 settimane:
**ρ medio fra le 5 corsie 0,759**, sette coppie su dieci sopra la soglia di ridondanza 0,70 del
codice stesso, **N_eff = 1,24 scommesse indipendenti su 5**, ρ medio con BTC **0,753**. La migliore
delle tre proposte arrivava a ~1,4 su 6: comprava 0,15 di scommessa al prezzo di riscrivere cinque
corsie.

---

## 7. Due cose che nessuno stava guardando

**«5m» non vuol dire intraday.** Misurata la durata mediana con implementazione indipendente:
`RsiOversold` su barre da 5 minuti tiene la posizione **20-44 ore**. È un flip sempre-a-mercato — la
cadenza si compra con la **struttura dell'uscita**, non col timeframe. E il pool grigio intero ha una
cadenza di **1,3-4,6 trade/mese**.

**Il 30m non esiste.** Zero righe in watchlist; le 5 serie residue (BTC, ETH, LINK, LTC, SOL) sono
morte il 2026-07-26, **637 ore fa**. Una proposta a 30m oggi non è eseguibile, e nulla nel sistema lo
dice.

**Il costo è sottostimato da 5× a 40×.** `DefaultSlippagePercent = 0,05` per gamba, contro il
round-trip misurato al denominatore della barra col clip vero: UNI 4h 0,610% · DOGE 15m 1,525% · ETC
4h 1,623% · **ADA 5m 3,259% · AVAX 5m 4,321%**. Le due corsie 5m che la proposta «cadenza» metteva in
testa hanno un costo 3-4 volte il margine lordo per trade.

---

## 8. Aperto

- [ ] **`kubectl apply -k infra/k8s/trading`** — l'immagine è pronta e importata, il pin è
      committato, manca solo l'apply. Poi il controllo che conta:
      `SELECT "LaneId", length("ActiveStrategiesJson") FROM "TradingEngineStates"` deve smettere di
      restituire NULL su una corsia avviata.
- [ ] **La sonda di fedeltà sulla corsia 5.** Dopo il rollout, riavviarla in Paper senza cambiare
      nulla e leggere il replay. Previsione dall'implementazione di riferimento: **5 trade chiusi**
      (01/08 +5,0% in 4h · 03/08 +2,53% in 20h · 12/08 +2,08% in 12h · 12/08 +2,11% in 16h ·
      19/08 +2,57% in 164h) più una short aperta dal 20/08. **4-6 chiusure a quei prezzi ⇒ la
      correzione dello specchio è viva. 0-1 e nessuna chiusura ⇒ non è atterrata.** In entrambi i
      casi si impara la cosa che serve.
- [ ] **Riparare `WalkForwardOosSharpe`**: finché il 69,6% dell'archivio ha due finestre spacciate
      per tre, ogni classifica per «coerenza» è un artefatto.
- [ ] **Benchmark passivo come gate**, non come commento: nessun candidato entra in fascia grigia se
      non batte il buy&hold nella sua direzione prevalente. Sull'archivio di oggi toglie sei gambe
      su nove.
- [x] **`GreyDeployer` risolve sulla `CandidateKey`** — **FATTO il 2026-08-22.** Due rettifiche alla
      riga che avevo scritto qui: (a) le «119 terne ambigue» sono **12 distinte** ricomparse in 119
      run-istanze, perché la caccia notturna ritrova ogni notte la stessa griglia — è lo stesso
      errore di conteggio righe-vs-distinti già rettificato su `GreyZone.DsrFloor`; (b) **nessuno
      schieramento sbagliato è mai avvenuto**: dei 6 click umani, il solo su terna ambigua aveva le
      due gambe con Sharpe identico. Il valore della correzione è **prospettico**, e il caso vivo è
      `b49a4c8c`, dove la riga **preselezionata** (Composite XLM/USDT 4h, Sharpe 1,29 su 8 trade)
      avrebbe schierato l'altra specifica della stessa terna (0,53 su 3 trade) senza alcun errore
      dell'operatore. Corretti anche il `FirstOrDefaultAsync` su `(RunId, Kind)` — che non è una
      chiave e ha già duplicati per altri `Kind` — e la proposta di `FleetStateReader`, che nominava
      ancora la terna mentre due righe sotto passava già `Identity: best.Key`.
- [ ] **Cancello di costo al denominatore della barra** dentro la pipeline.
- [ ] **`ResearchCandidates` va indicizzata dal run**, non all'apertura di `/research`: quattro run
      di oggi hanno zero righe pur avendo tutti gli artefatti.
- [ ] **Una sonda «core stantio»**: revisione del pod contro `HEAD`, visibile in `/trading`. Le
      immagini erano indietro di 4 (trading), 5 (ingestion) e **11 giorni (ml)**.

---

## 9. Le due cacce sui 25 giorni vergini — l'esito

Scongelate le finestre, entrambe le cacce sono state rilanciate lo stesso pomeriggio.

| caccia | run | candidati valutati | **sopravvissuti** | migliore |
|---|---|---|---|---|
| **1h** universo largo | `240272c8` | 64 | **0** | `RegimeConditional` XLM/USDT — Sharpe 1,35 su **42 trade**, PF 1,84, DSR 0,653 |
| **4h** universo largo | `b49a4c8c` | 141 | **0** | `RegimeConditional` NEAR/USDT — Sharpe 1,90 su **60 trade**, PF 1,67, DSR 0,367 |

**Zero sopravvissuti anche sui dati che nessuno aveva mai visto**, e nessun candidato raggiunge
nemmeno il pavimento grigio di 0,70. È il tredicesimo no — ma questa volta misurato su un mercato
che sale, non su quello che scendeva.

Vale la pena guardare i due migliori: sono entrambi `RegimeConditional`, ed è la sola famiglia che
qui compaia con un campione serio (42 e 60 trade contro i 2-19 di quasi tutti gli altri). XLM/USDT
1h regge: era già il miglior candidato dell'archivio col DSR vero (holdout 1,08 su 48 trade prima,
1,35 su 42 adesso), è **long/fermo** e non short, e batte il passivo di +0,70 nella sua finestra.
Resta bocciato nel merito — DSR 0,653 contro un pavimento 0,70 — ma è l'unico oggetto che sopravvive
al cambio di regime senza cambiare segno.

### La conferma del difetto n.1 sui dati nuovi

Sul run 1h appena eseguito: **64 righe su 64** hanno `WalkForwardOosSharpe = round(SelectionSharpe, 2)`.
Sul 4h: **82 su 141 (58%)**, che combacia col 59,0% storico della cfg 17.

Non è un residuo di archivio: **il difetto è vivo adesso**. E l'asimmetria dice anche dove guardare —
le righe col walk-forward *vero* sono tutte delle famiglie `GridMeanReversion` / `DonchianBreakout` /
`RsiOversold` / `Supertrend` (es. `GridMeanReversion ALGO/USDT`: wf 4,98 contro sel 0,181), mentre
`RegimeConditional`, `Composite` ed `EventTrigger` hanno sempre il campo copiato. È il primo indizio
concreto su quale ramo del codice non lo calcola.
