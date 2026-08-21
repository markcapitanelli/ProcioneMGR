# La notte intraday — 2026-08-21

Richiesta del proprietario, la sera del 20 agosto: *«sfrutta la piattaforma al massimo delle sue
capacità e trova modi e strategie nuove da applicare, per investimenti possibilmente intraday o
comunque abbastanza rapidi … sei libero di fermare e caricare nuovi dati e investimenti su tutte le
corsie che ritieni necessarie … lasciami una traccia scritta»*.

> **La premessa, detta prima di qualunque risultato.** Non posso promettere profitto, e non l'ho
> cercato con una scommessa. La piattaforma ha già detto **dieci volte** di no al direzionale-tecnico,
> e negli ultimi 30 giorni ha validato **11.496 candidati con zero sopravvissuti**. Cercare *di più*
> non è la leva: ogni combinazione in più alza SR\* e schiaccia il Deflated Sharpe di tutte le altre.
> Quello che ho fatto stanotte è **cercare dove non si era mai cercato** e **rimettere in funzione il
> giudice**, che è l'unica cosa che questa piattaforma abbia mai riconosciuto come decisiva: il
> forward test in Paper.

---

## 1. La diagnosi, prima di toccare qualsiasi cosa

### Il giudice era a digiuno

| corsia | serie | tf | strategia | trade totali | ultimo trade |
|---|---|---|---|---|---|
| 0 | *non configurata* | — | — | 159 (storici, altri simboli) | ferma |
| 1 | DOT/USDT | 15m | RsiOversold | 21 | 2026-07-27 |
| 2 | ADA/USDT | 4h | Supertrend + Composite | 9 | 2026-08-10 |
| 3 | ETC/USDT | 4h | RsiOversold | 5 | 2026-08-06 |
| 4 | XRP/USDT | 4h | GridMeanReversion | 1 | 2026-08-06 |
| 5 | UNI/USDT | 4h | GridMeanReversion | 5 | 2026-08-14 |
| 6 | LTC/USDT | 15m | Composite | 6 | 2026-07-20 |
| 7 | STX/USDT | 4h | BollingerMeanReversion | 3 | 2026-07-25 |

**In sette giorni l'intera flotta ha chiuso UN trade.** Sei corsie su otto sono su 4h, cioè swing e
non intraday. Nessuna posizione aperta: le corsie non sono bloccate, semplicemente non generano
segnali. Un forward test che produce un trade a settimana non può giudicare niente in tempi umani —
e il forward test è l'unico giudice che questa piattaforma si fidi di usare.

### La ricerca non ha mai guardato dove il proprietario vuole operare

| timeframe | candidati in archivio | ultimo run |
|---|---|---|
| 4h | 8.291 | 2026-08-20 |
| 1h | 4.529 | 2026-08-20 |
| 1d | 832 | 2026-07-27 |
| **15m** | **117** | **2026-07-27** |
| **5m** | **37** | **2026-07-26** |
| **30m** | **8** | **2026-07-27** |

Su 13.814 candidati archiviati, **162 sono intraday**, e l'ultima caccia intraday è di un mese fa. La
piattaforma ha passato agosto a cercare su 1h e 4h. Non è che l'intraday sia stato provato e bocciato:
**non è stato provato**.

E non per mancanza di dati — l'infrastruttura c'era ed era ferma:

- **5m: 30 simboli, 5,16 M candele, 20 mesi di storia, ultima candela a 2 ore fa.** Watchlist: 30
  serie, tutte abilitate.
- **15m: 45 simboli, 2,54 M candele, fresche.**
- 30m: **ferme dal 26 luglio** — quelle 5 serie non sono nemmeno in watchlist. È un buco da chiudere.

### Una trappola evitata

L'archivio contiene **4 sopravvissuti a 5m** (RsiOversold su LINK e FIL, 65 e 77 trade nell'holdout,
walk-forward OOS 2,03 e 1,86). Sembrano la risposta pronta. **Non lo sono**: sono del **3 luglio**,
cioè *prima* della correzione D-01 che ha portato il conteggio dei tentativi dal minimo alle migliaia
e dimezzato SR\*. Rivalutati il 18 luglio, gli stessi candidati danno **DSR 0,518 e 0,325** — sotto
anche il pavimento nuovo della fascia grigia. Col gate di oggi **non sopravviverebbero**.

Il loro unico pregio reale è la **cadenza**: 65-77 trade in un holdout. È quello che li rende
*giudicabili in settimane invece che in anni* — che è un'altra cosa dall'essere buoni.

---

## 2. Che cosa ho fatto

### a. Due cacce intraday nuove, con i gate di oggi

Configurazioni **19 (5m)** e **20 (15m)**, entrambe in Paper:

- **Universo ristretto a 10 majors liquidi** (BTC, ETH, SOL, BNB, XRP, DOGE, ADA, AVAX, LINK, LTC) e
  non ai 34 delle cacce 1h/4h. È deliberato e discende dalla misura di ieri: SR\* cresce con la
  dimensione della ricerca, e su 34 serie a 1h il DSR non supera **0,773**. Meno superficie, più
  possibilità che un edge vero passi il gate. Sull'intraday la liquidità è anche l'unica difesa
  contro i costi.
- **Finestra fissata PRIMA di guardare i risultati**: selezione 2025-01-01 → 2026-04-30, holdout
  2026-04-30 → **2026-08-20**. Le altre configurazioni hanno l'holdout fermo a fine luglio.
- **Walk-forward adattato**: 9 mesi in-sample + 3 OOS, passo 3, embargo 60 barre (5 ore su 5m). Le
  cacce 1h usano 18+6, ma la selezione qui copre 16 mesi: con 18 di in-sample **non ci sarebbe stata
  nemmeno una finestra**. Con la configurazione clonata così com'era, la caccia sarebbe girata a
  vuoto.
- **Orizzonte del forecast di volatilità da 14 a 288 passi**: 14 barre da 5 minuti sono 70 minuti,
  una scala su cui «previsione di volatilità» non significa nulla. 288 passi è una giornata.

### b. Una fragilità trovata dal primo run, e corretta

Il primo tentativo è **fallito al secondo stage su diciassette**, con l'ingestione dei prezzi già
completata: `23505 duplicate key … IX_AltDataPoints_DedupeKey` dentro il sync delle notizie. Due sync
sovrapposte che ingeriscono lo stesso elemento — capita quando si lancia una caccia a mano mentre il
worker periodico sta già sincronizzando. Raro (**1 run su 170**) e proprio per questo insidioso.

Lo stage aveva **metà della dottrina applicata**: lo snapshot del mood era già protetto, con un
commento che dice *«non deve mai far fallire lo stage»*; la sync no. È il rovescio della regola 4 —
fail-closed sulla sicurezza, **fail-open sulla diagnostica**. Un run che perde le notizie di oggi vale
ancora; un run che non parte non vale niente.

Corretto, con cinque test (fra cui: il fallimento dev'essere **scritto** e non inghiottito, e la
cancellazione dev'essere ancora una cancellazione). Per far ripartire la caccia stanotte, senza
aspettare il deploy della correzione, ho usato la manopola già documentata `sync=false` — *«usa solo
le notizie già presenti nel database»* — e infatti il run ha comunque letto **837 notizie** e un mood
composite di +0,23.

### c. Una conferma di ieri, sui dati veri

La caccia 5m ha scritto: *«BTC/USDT: volatilità Media [har-log-rv] (attuale 0,179%, forecast
0,137%)»*. Sono valori **per candela da 5 minuti**. Col difetto A1 corretto ieri, lo stesso stage
avrebbe scritto ~2,3%, cioè il valore giornaliero, e il trigger contestuale avrebbe visto una
«compressione» inesistente. La correzione regge anche sul timeframe più corto.

---

## 3. Che cosa NON ho fatto, e perché

- **Non ho aumentato la ricerca.** Sarebbe stato il gesto più facile e il meno onesto: più
  combinazioni significa SR\* più alto e DSR più basso per tutti, cioè meno probabilità che qualcosa
  passi. La piattaforma lo ha già pagato una volta (445k combinazioni → 0 significative).
- **Non ho toccato le corsie 4 e 5.** Girano `GridMeanReversion`, che fino a ieri era rotta dal
  vivo (lo specchio della posizione azzerato a ogni candela). Da ieri è corretta: quelle due corsie
  sono l'**unico test pulito della correzione**, e spegnerle avrebbe buttato via l'esperimento nel
  momento in cui comincia a valere.
- **Non ho promosso niente verso Testnet o Live.** Tutto resta Paper. Regola 3.

---

## 4. Cose trovate strada facendo, da non perdere

- **Il PnL della corsia 2 (−227.328%) è un artefatto**: un solo trade **SUI/USDT** del 9 luglio,
  entrato a 0,7694 e uscito a 1748,18 con `EmergencyStop`. È il bug dei fill patologici, **già
  corretto il 18 luglio**. Non è un guasto vivo, ma sporca ogni aggregato che somma `PnlPercent` per
  corsia — e le corsie sono state riassegnate fra simboli diversi, quindi quegli aggregati mescolano
  serie. Vale la pena escluderlo o segmentare per simbolo dove il numero viene mostrato.
- **Le configurazioni 8, 10, 12 e 13 sono a timeframe misti** (1h+4h, 15m+5m) e da ieri la guardia A2
  le fa **saltare** allo stage di validazione holdout. È il comportamento voluto — il PBO non può
  confrontare barre di granularità diversa — ma significa che quelle quattro configurazioni oggi non
  producono nulla. Vanno divise per timeframe o archiviate.
- **Il 30m è fermo dal 26 luglio** perché quelle serie non sono in watchlist.
- **Tutte le configurazioni preesistenti hanno l'holdout fermo al 20-27 luglio**: un mese di dati
  freschi che nessuna caccia sta usando.
