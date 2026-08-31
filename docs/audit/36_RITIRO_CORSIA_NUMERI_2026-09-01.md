# Quando ritirare una corsia — i numeri per decidere (K16, K17, K19, K20)

> **Data:** 2026-09-01 · **Filone K, Fase 2** · Riferimento: `docs/PRD-AUTONOMIA-PIENA-2026-08.md`
>
> Questo documento **non decide**. Prepara i numeri per una decisione di politica: *quando la
> piattaforma smette di dare capitale a un'ipotesi*. Ogni cifra qui dentro è stata misurata e poi
> **attaccata** da un avversario indipendente incaricato di demolirla. Dove l'attacco ha vinto, il
> numero originale è cancellato e resta il corretto. Dove ha perso, è scritto che regge.
>
> Metodo: 4 agenti di misura + 4 avversari, `wf_0e9a7e15-007`. Le tre affermazioni portanti sono
> state poi ri-verificate a mano contro il database vivo.

---

## 0. Il numero che decide tutto, e che nessuno aveva

Tutti e quattro gli avversari, indipendentemente, hanno chiesto **lo stesso numero mancante**:
*quanto vive un'identità di corsia prima di essere sostituita.*

Serve perché **ogni** criterio di ritiro (inedia, Sharpe, danno, ritmo) è ancorato al primo
avvistamento dell'identità, e `LaneObservationLedger.AccumulateAsync` **azzera** `FirstSeenUtc` e
`ObservedSeconds` a ogni cambio. `FleetLaneObservations` tiene **una riga per corsia, senza storia**.
Se una soglia chiede più giorni di quanti l'identità mediamente ne viva, la soglia non è severa:
è **spenta**.

L'ho ricostruito dal journal degli assegnamenti incrociato con `EnsembleStates.LastUpdatedUtc`.

| episodio | da | a | giorni di calendario |
|---|---|---|---|
| corsia 5 · Composite DOT/USDT 1h | 03/08 14:56 | 13/08 18:29 | **10,15** |
| corsia 3 · RsiOversold ETC/USDT 4h | 05/08 20:29 | 31/08 16:07 | **25,82** |
| corsia 4 · GridMeanReversion XRP/USDT 4h | 03/08 14:54 | 31/08 20:22 | **28,23** |
| corsia 6 · Composite LTC/USDT 15m | 03/08 14:57 | 31/08 21:12 | **28,26** |
| corsia 7 · Bollinger STX/USDT 4h | 05/08 20:30 | *ancora viva* | 26,09 (censurato) |
| corsia 5 · Grid UNI/USDT 4h | 13/08 18:29 | *ancora viva* | 18,18 (censurato) |

**Mediana su 4 episodi chiusi: 27,0 giorni di calendario.** Minimo 10,15.

Ma il criterio non conta calendario, conta **osservazione accreditata**, e il duty misurato dal
ledger è **89,8%** (corsia 5) e **86,0%** (corsia 7) — chiamiamolo 88%. Quindi:

**Mediana di vita di un'identità ≈ 23,8 giorni OSSERVATI. Minimo ≈ 8,9.**

Ora si legge tutto il resto. Contro questo metro, i cancelli in vigore e quelli proposti:

| cancello | episodi chiusi che lo avrebbero raggiunto |
|---|---|
| inedia `StarvationMinDays = 10` (in vigore) | **3 su 4** |
| Sharpe `RetireMinWeeks = 3` → 21 gg osservati | **3 su 4** (la metà tempo; la metà trade mai, § 3) |
| inedia a **27** giorni (opzione 1) | **0 su 4** |
| inedia a **41** giorni (opzione 2) | **0 su 4** |

> **Conseguenza che cambia la discussione.** Le due opzioni che rendono l'inedia statisticamente
> rispettabile la **spengono**: nessuna delle identità realmente vissute in questa flotta sarebbe
> mai arrivata al cancello. È esattamente il difetto che J8 era stato scritto per riparare, e lo
> ricrea da un'altra porta.
>
> Nota onesta sul numero: 4 episodi, di cui **2 con la fine non registrata a journal** (corsie 4 e
> 6, ricostruite da `EnsembleStates.LastUpdatedUtc`). È un limite inferiore grezzo, non una
> distribuzione. **Ma è il numero che decide, e oggi non esiste una tabella che lo conservi.**

---

## 1. Che cosa è stato demolito (non usare più questi numeri)

Prima delle opzioni, il rogo. Sette affermazioni sono cadute, e cinque di loro erano *a favore*
dell'agire in fretta.

### 1.1 · 66 righe di replay in `TradeRecords` — verificato di persona

```
righe | PositionId distinti | entità logiche distinte
  367 |                 367 |                     301
```

`COUNT(DISTINCT PositionId)` **non può** rilevarlo: il `PositionId` è generato a ogni run, quindi
tre esecuzioni dello stesso backtest danno tre righe con tre id diversi e identici
`(LaneId, StrategyId, Symbol, Side, OpenedAtUtc, ClosedAtUtc, EntryPrice, ExitPrice, Quantity, Pnl)`.
Il controllo che doveva escludere il gonfiaggio era cieco **per costruzione** — la firma del filone E.

Cosa cade con esso: **ogni numero per gamba di K17**. Il drawdown peggiore di sempre passa da
3,62% a **2,87%**, la perdita cumulata peggiore da −3,28% a **−2,54%**, e le soglie che
«sparavano una volta» sparano **zero** volte.

> Chiave d'entità corretta: `(LaneId, StrategyId, Symbol, Side, OpenedAtUtc, ClosedAtUtc)`.
> 7 gambe su 21 sono contaminate; la peggiore (corsia 0, `a9e108e7`) ha 30 righe di troppo su 76.

### 1.2 · Le corsie 4 e 6 sono **la stessa ipotesi** — verificato di persona

```
4 | GridMeanReversion | DOGE/USDT | 15m | expectedSharpe 1,8754417491 | f 3,80 | {Direction 1, EntryRungs 1, StepPercent 2, AnchorPeriod 20}
6 | GridMeanReversion | DOGE/USDT | 15m | expectedSharpe 1,8754417491 | f 3,80 | {Direction 1, EntryRungs 1, StepPercent 2, AnchorPeriod 20}
```

Stesso `expectedTradesSource`: *«holdout del run e0cec50f: 14 trade su 3,68 mesi»*. Differiscono
**solo** per l'hash dello `strategyId`. Sono **una** ipotesi che occupa **due** corsie e due
dotazioni di capitale, e che sbaglierà due volte insieme.

**Le corsie di flotta con ipotesi distinte sono 4, non 5.** Ogni conteggio che le tratta come due
prove indipendenti è gonfiato: i falsi allarmi, le date di inedia, la diversificazione apparente.

### 1.3 · Il falso allarme calcolato una-tantum, mentre la regola gira ogni 15 minuti

`FleetOrchestratorWorker` applica l'isteresi **a ogni tick** (`TickMinutes = 15`) e rivaluta la
corsia finché resta in corsa. Una corsia sana non è esposta al criterio *un giorno*: è esposta
**per sempre**, mentre la soglia sale linearmente e i suoi trade no.

| regola | falso allarme «al primo sguardo» | falso allarme **cumulato a 180 gg** |
|---|---|---|
| `MinDays = 10` (in vigore) | 28,7 – 31,7% | **31,7 – 34,4%** |
| `MinDays = 27` (opz. 1) | 3,4 – 4,5% | **8,2 – 9,2%** |
| `MinDays = 41` (opz. 2) | 0,7 – 3,7% | **6,1 – 6,5%** |
| quantile di Poisson al 5% (opz. 3) | ≤ 5% «per costruzione» | **17,1 – 23,4%** |

Il vantaggio del giorno 41 su cui poggiava l'opzione 2 **svanisce**: 4,12% cumulato, identico al 42.

### 1.4 · L'opzione 3 (quantile di Poisson) non ha la garanzia che rivendica

«FA ≤ 5% per costruzione» vale per **un** test. Qui i test sono ~96 al giorno per sempre, e il
quantile parla più spesso della regola a frazione: **17,1 – 23,4% cumulato**, cioè **peggio**
dell'opzione 2. La virtù rivendicata («si autolimita con onestà») è quella che si rompe per prima.
Sopravvive solo con budget d'errore speso una volta sola, o con alpha-spending sequenziale.

### 1.5 · `f` non è una costante nota a tre decimali

È una stima da **14–18 trade** di holdout (errore relativo 1/√n ≈ **26,7%** a 14 trade), e per le
corsie 4 e 6 è **la stessa stima usata due volte**. Con la predittiva corretta (NegBin) il falso
allarme sale ancora: a 41 giorni per f=3,80 diventa **6,03%** invece di 3,66%.

> **Nessuna delle quattro opzioni porta il falso allarme sotto il 5% sulle corsie lente**, finché
> `f` è stimata da 14–18 trade.

### 1.6 · Gli arrivi non sono di Poisson: sono a grappolo

Coefficiente di variazione degli intervalli fra aperture e fattore di Fano sui conteggi (Poisson ⇒
entrambi 1): **7 serie su 8 nettamente sovradisperse** — DOGE CV 2,50 Fano 2,94 · XRP CV 3,74 Fano
3,01 · ETC 2,14/1,85 · DOT 2,07/2,89 · LTC 2,50/2,81 · STX 1,48/1,87 · UNI 1,68/1,52 · AAVE
0,75/0,98.

Quindi P(zero trade nella finestra) è **maggiore** del valore di Poisson: **tutte le cifre di falso
allarme di questo documento sono un pavimento, non una stima.** Esempio misurato: su UNI/USDT una
secca da 12 giorni è già avvenuta (1 intervallo su 18 ≈ 5,6% empirico contro lo 0,17% di Poisson).

### 1.7 · «L'atteso è sottostimato di 2,8×–14×» — cancellato

I ritmi realizzati erano calcolati su finestre che **finiscono prima che l'identità attuale
cominci**: i 27 trade DOGE della corsia 4 chiudono entro il 27/08, l'identità nasce il 31/08 21:11.
Sono i ritmi delle ipotesi **precedenti** sullo stesso simbolo. Non è evidenza che l'atteso sia
sbagliato: è evidenza che i simboli sono attivi.

Era **l'unica** cosa che spingeva i falsi allarmi verso il basso. Sequenzialità, stima di `f` e
sovradispersione li spingono tutti e tre verso l'alto.

### 1.8 · Non succede niente il 4-5 settembre

- **Corsia 7:** `IsRunning = false`, e il ciclo di ritiro itera su `.Where(l => l.IsRunning)`.
  Non accredita tempo e **non viene nemmeno esaminata**. Mai, finché resta ferma.
- **Corsia 5:** 1 trade dall'ancora contro soglia 0,243 → `1 < 0,243` è **falso**. Non è affamata.
  Lo diventa al giorno di osservazione 41,13, cioè **~10/10** al suo duty misurato.
- Le corsie a rischio sono **3, 4 e 6**, intorno al **10-12/09**, e **solo se restano mute** — che
  per la corsia 4 su DOGE ha probabilità ≈ 3·10⁻⁸ (λ a 10 giorni = 17,4; zero secche ≥10 gg su 26
  intervalli osservati).

**Il cancello che si apre non è il verdetto che scatta.**

---

## 2. K16 — l'inedia

### Lo stato di fatto

Con `StarvationFraction = 0,20` e `f` fra 3,5 e 3,8 trade/mese, a 10 giorni l'atteso vale 1,15-1,25
trade e la soglia **0,23-0,25**. La soglia è **sotto 1**: la regola degenera in «condanna se e solo
se zero trade». Non è severa, è **rotta per aritmetica**.

| f (trade/mese) | corsia | soglia a 10 gg | falso allarme al primo sguardo |
|---|---|---|---|
| 3,50 | 7 (ferma) | 0,2308 | **31,67%** |
| 3,70 | 5 | 0,2431 | **29,65%** |
| 3,80 | 4 e 6 *(stessa ipotesi)* | 0,2496 | **28,69%** |
| 11,11 | 3 | 3,6503 | 2,60% |

Quasi **una corsia sana su tre** condannata, al primo sguardo. Cumulato a 180 giorni con `f`
stimata: **31,7 – 34,4%**.

**La potenza contro una corsia a zero è 100% a qualunque giorno**, per costruzione. Il criterio non
sbaglia mai il caso che interessa: paga solo in **latenza**. Contro una corsia moribonda ma non
morta (20% del ritmo) la potenza sta fra 38% e 71%, e **non è monotona nei giorni**.

### La non-monotonia, che è una trappola vera

Il falso allarme **risale** quando `ceil(0,2·λ)` scavalca un intero, al giorno `d* = 5·30,4375/f`:
**44** per f=3,50, **42** per f=3,70, **41** per f=3,80, **13,7** per f=11,11.

A f=3,70: 41 gg → 0,68%, **42 gg → 3,70%**. A f=3,80: **41 gg → 3,66%**, 42 gg → 3,30%.
Un giorno di differenza vale un fattore 5. **Nessun `MinDays` fisso è un buon `MinDays` per tutte
le frequenze insieme** — ed è il difetto che ha ucciso l'opzione «41 e non 42».

### Le opzioni, con il costo onesto

| | regola | FA cumulato 180 gg | identità che arrivano al cancello (§ 0) | stato |
|---|---|---|---|---|
| **0** | `MinDays = 10` (oggi) | 31,7 – 34,4% | **3 su 4** | in vigore |
| **1** | `MinDays = 27` | 8,2 – 9,2% | **0 su 4** | in piedi, ma spegne |
| **2** | `MinDays = 41` | 6,1 – 6,5% | **0 su 4** | ⛔ demolita |
| **3** | quantile Poisson 5% a ogni tick | 17,1 – 23,4% | — | ⛔ demolita |
| **4** | `MinDays = ceil(3,0 · 30,4375 / f)` | come 1, ma **senza salti** | dipende da `f` | **struttura corretta** |

L'opzione **4** è l'unica strutturalmente sana: vincola **λ**, non i giorni, quindi elimina il salto
di `ceil` come sorgente di sorprese. Dà 25 gg a f=3,65 e **8 gg** a f=11,11 — cioè si adatta.

Ma va decisa con due paletti:
- **pavimento assoluto**, perché una `f` alta la renderebbe fulminea;
- **tetto assoluto**, perché nella coda della fascia grigia c'è f ≈ 0,67/mese, e lì la regola
  chiederebbe **136 giorni** prima di poter parlare. Se quel tetto non è accettabile, la domanda
  vera non è come tarare l'inedia: è **se quelle gambe vadano schierate**.

### Il costo dell'attesa non è un argomento

−1,81 €/corsia-giorno **non è distinguibile da zero**: media per trade −2,583 con sd 36,922 su
n=138 ⇒ t = −0,82, p ≈ 0,41, intervallo al 95% ≈ **[−6,1 ; +2,5]**. Il *segno* è ignoto. Ed è
fragile: **metà della perdita totale sta in 3 trade su 138**.

Il costo di coda esiste (`Blocked`: *«16 candidati grigi schierabili e 1 corsie libere, ma il tetto
grigio è saturo: 4 corsie grigie in corsa su un massimo di 3»*) ma **non è funzione di
`StarvationMinDays`** — vedi § 5.

---

## 3. K19 — il ritiro per Sharpe

### Il difetto contabile, verificato di persona

```
corsia | identità                | gg osservati | trade DALL'ANCORA | trade sull'identità
   3   | AAVE/USDT|4h|7cfa69bf   |    0,151     |         0         |          7
   4   | DOGE/USDT|15m|380ad9f9  |    0,058     |         0         |         27
   5   | UNI/USDT|4h|ad1a3638    |    5,772     |         1         |         19
   6   | DOGE/USDT|15m|4c911e3b  |    0,047     |         0         |          1
   7   | STX/USDT|4h|0b4ebc23    |    5,513     |         0         |         12
```

**65 trade già chiusi sulle identità esattamente schierate ora sono fuori dalla finestra di
giudizio.** La corsia 4 ne ha 27 e il criterio ne vede 0. Tutta la flotta ha prodotto **un solo
trade** dalle ancore.

### Ma recuperarli non basta

Il cancello non è solo sui trade: `enoughHistory = TotalTrades ≥ 20 **AND** Observation ≥ 21 gg`.
Nessuna corsia supera **5,77 giorni osservati** su 21 richiesti. Recuperare i 65 trade sistema il
numeratore e lascia intatto il denominatore, che è il vincolo che morde.

**La prima corsia giudicabile per merito è la 5, verso il 17/09.** Non la 4, e non oggi.

E il commento J8 registra che la **massima finestra continua mai raggiunta in tutta la vita della
flotta** è stata **20g 3h contro i 21 richiesti**. Il criterio per Sharpe non ha mai potuto
esprimersi — non perché sia severo, ma perché il suo cancello è appena sopra la vita di un'identità.

### La taratura, se e quando si arriva a parlarne

Falso allarme al vero Sharpe = 0 → **50% a ogni N**, per costruzione. Serve una soglia negativa.

| soglia (ritira se Sharpe osservato <) | N=10 | N=20 | N=30 |
|---|---|---|---|
| 0,00 | fa 50,0% / pot 99,5% | fa 50,0% | fa 50,0% |
| **−0,50** | **fa 5,7% / pot 90,2%** | fa 1,3% / pot 96,6% | fa 0,3% |
| −0,75 | fa 0,9% / pot 74,1% | fa 0,0% / pot 81,9% | — |

Aspettare più trade **non è gratis**: sulle corsie realmente positive costa dal 5 al 19% di
ritiro-sbagliato in più — S=+0,185 (2·ADA) → 34,1% a N=5, 20,6% a N=20; S=+0,401 (3·AAVE) → 19,4%
a N=5, 4,2% a N=20.

**«PnL con banda» non è un'alternativa: è lo stesso test.** `t = Sharpe × √N`, identità algebrica.
L'unica differenza reale è la **banda**, cioè la zona di astensione.

### Il tranello dell'unità

`Statistics.PeriodsPerYear`: 4h → 2190, 15m → 35040. `√2190 = 46,8` contro `√35040 = 187,2`, **un
fattore 4,0**. Lo **stesso** `RetireSharpeThreshold` significa una cosa **quattro volte diversa**
sulle corsie 4 e 6 (15m) rispetto alle corsie 3, 5, 7 (4h). Una soglia unica non è una soglia unica.

### Il campione, dichiarato bene

**17** storie (corsia, simbolo); **15** con n≥3, **13** con n≥5, **12** con n≥10, **6** con n≥20;
**368** trade chiusi. Gli effetti sono piccoli **tranne una storia**: corsia 5 · DOT, n=3,
S/trade = **−3,15**, t = −5,45, PnL −76,49 $. L'affermazione «S = −0,5 per trade non esiste in
questa flotta» era vera solo perché quella riga era stata omessa.

---

## 4. K17 — il criterio di danno

**Non è tarabile oggi, e la ragione è misurata.**

1. **La scala proposta è fuori range.** Sul dato deduplicato il drawdown massimo di sempre è
   **2,87%** (corsia 7 · STX) e la perdita cumulata minima **−2,54%**. Soglie X = 5%/8% e
   Y = 5%/10% non sparano **mai**. Soglia utile solo sotto ~2,9%.

2. **Il controllo sul rumore boccia tutto, in entrambe le versioni.** Il primo nullo (fermarsi a un
   trade a caso) bocciava 11 regole su 11, ma era viziato: non conservava l'eleggibilità, e il 72%
   dello svantaggio era artefatto. Il nullo **corretto** — permutare l'ordine dei trade *dentro* la
   gamba, che conserva esattamente il PnL finale — su 3.000 permutazioni dà: **nessuna regola è
   distinguibile dal caso, in nessuna delle due direzioni.**

3. **La regola «meglio calibrata» cambia segno una volta tolti i replay.** `cum ≤ −1,5% con n≥5`
   passava per +140,0 $ netti con 2 VP / 1 FP; deduplicata dà **−19,5 $** e 2 VP / 2 FP / 1
   indifferente. Il suo unico grande vero positivo diventa indifferente, e compare un falso
   positivo nuovo da −76,7 $. Nessuna delle tre soglie di `cum` ha netto positivo: −1,5% → −19,5 $ ·
   −2% → −21,9 $ · −3% → zero spari. p = 0,335 al test di permutazione.

4. **Il caso XRP è un aneddoto con N=1.** L'aritmetica centrale regge (sparo alla 3ª perdita
   consecutiva al 6° trade, **187 $ risparmiati su 10.000**), ma il denominatore che lo rendeva
   spettacolare — «32× l'atteso» — **non esiste**: la gamba XRP non ha righe in `EnsembleStates`, e
   il 3,8 con cui era costruito è l'atteso di **un'altra strategia, su un altro simbolo, su un altro
   timeframe** (griglia DOGE a 15m contro griglia XRP a 4h). Il «89% del danno» è in realtà il
   risparmio su una finestra di 34,5 giorni, non 19.

5. **Il segnale discriminante non è il PnL, è il ritmo** — ma non è misurabile sull'identità
   attuale: la corsia 6 ha **1 solo trade** sul simbolo corrente, anteriore al deploy.

**Buona notizia collaterale:** i `Pnl` sono al **netto** delle commissioni sui due lati
(`PositionCloser.cs:194,199,349`), e lo slippage è modellato sui prezzi. Non c'è motivo di abbassare
le soglie per i costi. Unica eccezione: **1 riga** scritta da `ProtectiveExitDiagnosticsService`
senza fee, più **5 righe** con `ClosedAtUtc` riscritto da J21 — queste ultime alterano l'**ordine**
dei trade su cui si contano le perdite consecutive.

**Prerequisito non negoziabile prima di accendere qualunque soglia di danno:** il lettore ha bisogno
di **tre** filtri, non uno — `Mode='Paper'`, `StrategyId` nelle gambe ancorate, e deduplicazione per
`(StrategyId, OpenedAtUtc, ClosedAtUtc)`. Senza il primo, la corsia 2 risulta a **−18.187%**.

---

## 5. K20 — l'isteresi che non sopravvive al riavvio

**È un problema teorico all'attuale taratura, e l'asse su cui era misurato era quello sbagliato.**

La condanna è **monotona**: l'osservazione cresce e i trade dall'ancora no, quindi una corsia votata
al ritiro al tick *k* lo è anche a *k+1* e a tutti i successivi — finché non fa un trade, e se lo fa
il criterio ha smesso di mordere **perché doveva**. Un riavvio **non perde** il ritiro: costa
un'**attesa** fino alla prima finestra di uptime abbastanza lunga.

| `RetireConfirmTicks` | ritardo atteso da riavvio |
|---|---|
| N = 2 (in vigore) | **0,2 minuti** |
| N = 3 | 2,5 minuti |
| N = 4 | **9,0 minuti** |
| N = 6 | 105 minuti |

Contro un cancello di **10 giorni**. L'opzione «alzare a N=4» non manca un ritiro su tre: lo ritarda
di **nove minuti**.

**Quindi il valore di K20 non è il tasso di conferma: è l'osservabilità.** Rendere *leggibile* che
una condanna era a metà strada. È fattibile e verificato: `OrchestratorDecisions.Kind` è
`varchar(32)` e `'RetirePending'` ci sta; `LaneId` e `RunId` sono nullable (la riga `Blocked` li ha
entrambi vuoti); il journal ha 117 righe in 28,4 giorni, quindi scrivendo solo sui **cambi di serie**
il rumore è governabile.

### Il numero di disponibilità va corretto

Le sonde del watchdog davano **99,09%**, ma **non sono una fonte indipendente**: girano dentro lo
stesso supervisore e sono cieche proprio quando la piattaforma è giù. Misurato: **1.986 sonde su
2.409 attese = 423 mancanti, il 17,6%**, con 20 buchi > 12′ per 35,0 ore non sondate su 200,8.

**La fonte primaria di disponibilità è il ledger: 89,8%**, ed è l'unica delle tre che non sia cieca
durante gli outage. Le due si riconciliano esattamente sul deficit della corsia 5 (56.293 s).
`MaxCreditPerGap = 45 minuti` fa sì che sia un **limite inferiore**.

Le date del cancello vanno quindi pubblicate **in due colonne** — «a duty 100%» e «al duty misurato
della corsia» — altrimenti l'orologio dell'inedia sembra un calendario e non lo è.
(Corsia 5 al giorno 10: **05/09 15:31**, non 04:09. Corsia 7: **06/09 04:04**, non 05/09 09:44.)

Nota minore ma da non ripetere: i log sono in ora **locale** (CEST), il database in **UTC**.
Mescolarli sposta tutto di due ore.

---

## 6. La coda bloccata — e non c'entra il ritiro

```
Id 117 · 2026-08-31 21:28:15 · Blocked · rules
«16 candidati grigi schierabili e 1 corsie libere, ma il tetto grigio e' saturo:
 4 corsie grigie in corsa su un massimo di 3»
```

**Una corsia libera c'è già.** Il vincolo che morde è `MaxGreyLanes = 3`. E la quarta «grigia» è la
**corsia 5**, contata tale solo perché il suo `sourceVerdict` è **vuoto** e l'ignoto conta come
grigio (che è il verso prudente, e va tenuto).

Verificato:

```
3 | MacdTrend              | Grey  | holdout del run 13a1f834: 54 trade su 4,86 mesi
4 | GridMeanReversion      | Grey  | holdout del run e0cec50f: 14 trade su 3,68 mesi
5 | GridMeanReversion      |       | [J9, ricostruito] holdout del run 5f6e1001: 18 trade su 4,86 mesi
6 | GridMeanReversion      | Grey  | holdout del run e0cec50f: 14 trade su 3,68 mesi
7 | BollingerMeanReversion |       | [J9, ricostruito] holdout del run a060b59f: 17 trade su 4,86 mesi
```

Il backfill K13 ha etichettato 3, 4 e 6; per la 5 e la 7 **non ha trovato il candidato e non ha
inventato l'etichetta** — che è il comportamento voluto.

> **Il costo di coda è reale ma non è funzione di `StarvationMinDays`.** Un ritiro libera **uno** dei
> 16, non la coda. Va trattato come intervento separato: o si ritrova la provenienza delle corsie 5
> e 7, o si alza `MaxGreyLanes`. Ed è **la cosa più economica sul tavolo**.
>
> Il «16» è comunque da riverificare: la fascia grigia degli ultimi 14 giorni è **5.811 righe per
> 578 chiavi distinte**, e `ResearchCandidates` nel suo complesso ha **18.267 righe per 1.028
> chiavi** (17,8×, coerente col ~19× storico — il rapporto **non** è raddoppiato).

---

## 7. Le decisioni aperte (nessuna presa qui)

| # | decisione | non è codice, è politica perché… |
|---|---|---|
| **D1** | **Conservare la storia delle identità** (tabella append-only degli episodi, con motivo di chiusura) | Senza, nessuna delle tarature qui sopra è verificabile a posteriori — e resta impossibile sapere se una soglia è raggiungibile prima di chiedersi se è giusta |
| **D2** | Come tarare l'inedia: restare a 10 accettando ~1/3 di falsi allarmi, salire a 27 sapendo che **spegne**, o passare a `MinDays` derivato da `f` con pavimento e tetto | Sceglie se la macchina preferisce **sbagliare per fretta** o **non decidere mai** |
| **D3** | Che fare della **coda a f ≈ 0,67/mese** (136 giorni prima di poter parlare) | Se il tetto è inaccettabile, la risposta non è tarare l'inedia: è **non schierare** quelle gambe |
| **D4** | Le corsie **4 e 6 sono la stessa ipotesi**: liberarne una, o tenerle come replica dichiarata | Cambia la diversificazione reale della flotta e libera capitale e una corsia per la coda |
| **D5** | **Sbloccare la coda subito** ritrovando la provenienza di 5 e 7, o alzando `MaxGreyLanes` da 3 | È un tetto di rischio, non un dettaglio: l'ignoto-conta-come-grigio va **tenuto** in ogni caso |
| **D6** | Le **66 righe di replay** in `TradeRecords`: deduplicare in lettura, o ripulire l'archivio | Toccare uno storico di trade è irreversibile; deduplicare in lettura è reversibile ma lascia la trappola |
| **D7** | `RetireSharpeThreshold` **per timeframe** (fattore 4,0 fra 15m e 4h), o una soglia denominata in Sharpe **per trade** | Una soglia unica oggi è quattro soglie diverse senza dirlo |
| **D8** | K20: N resta 2 (costo 0,2′) e si aggiunge `RetirePending` a journal per **vedere** le condanne a metà strada | Il beneficio è osservabilità, non tasso di conferma |

---

## 8. Che cosa regge, verificato riga per riga

1. Le chiavi delle gambe sono **camelCase** dentro `strategies` — con `legs` o in PascalCase: zero righe.
2. Trade dall'ancora **0 / 0 / 1 / 0 / 0**, riprodotti in due modi indipendenti.
3. La degenerazione a 10 giorni (soglie 0,2308 / 0,2431 / 0,2496, tutte sotto 1) e i falsi allarmi
   31,67 / 29,65 / 28,69 / 2,60% — riprodotti a tre decimali.
4. La non-monotonia da salto intero, con salto a `5·30,4375/f`.
5. Potenza contro una corsia a zero = **100%** a qualunque giorno ≥ `MinDays`.
6. `Kind = 'Retire'` **esiste** ed è scritto in due punti del worker: `OrchestratorDecisions` ha 109
   `ProposeGrey`, 7 `Assign`, 2 `Blocked` e **zero** `Retire`. **L'assenza è informativa, non un
   buco di journal.**
7. `FleetStateReader` chiama già `GetPerformanceAsync(from: firstSeen)` e usa solo `SharpeRatio` e
   `TotalTrades`; `TotalReturn` e `MaxDrawdown` **non** sono ancorati e infatti non vengono usati.
8. Le corsie 0-2 sono correttamente escluse dal ritiro di flotta.
9. La corsia 7 sfugge davvero al criterio finché resta ferma.
10. Il ritardo di 1,13 giorni del cancello dovuto al `MaxCreditPerGap` è corretto come conseguenza.

---

## 9. Trappole da ricordare (già pagate qui)

- **`COUNT(DISTINCT <id generato per run>)` non è un controllo di unicità.** È cieco per costruzione.
  L'entità va definita sui campi che la identificano, non sulla chiave surrogata.
- **Un falso allarme «al primo sguardo» non è il tasso di errore di un criterio che gira ogni 15
  minuti.** Va sempre dichiarato l'orizzonte.
- **Un ritmo realizzato misurato prima dell'ancora non dice nulla dell'ipotesi ancorata.** Dice solo
  che il simbolo è attivo.
- **Le sonde di un watchdog che gira dentro il processo sorvegliato non misurano la disponibilità di
  quel processo.** Non sono una seconda fonte.
- **Poisson su arrivi a grappolo sottostima le secche.** Sette serie su otto qui sono sovradisperse.
- **Log in ora locale, database in UTC.**
