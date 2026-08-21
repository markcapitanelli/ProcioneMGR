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

---

## 5. Il risultato delle due cacce

Entrambe **completate**, entrambe con **zero sopravvissuti**. È l'undicesimo e il dodicesimo no.

| caccia | durata | candidati validati | sopravvissuti | N tentativi effettivi |
|---|---|---|---|---|
| **5m**, 10 majors | 44 min | 15 | **0** | 3.744 (7.020 combinazioni) |
| **15m**, 10 majors | 11 min | 27 | **0** | 3.640 (7.020 combinazioni) |

### Che cosa è caduto, e come — è qui l'informazione

I dieci candidati 5m con holdout positivo, ordinati per Sharpe holdout:

| strategia | serie | Sharpe hold. | trade | PF | walk-forward OOS | DSR | perché è caduto |
|---|---|---|---|---|---|---|---|
| Composite | ADA | **2,65** | 16 | 2,68 | 0,40 | — | solo 16 trade (< 20) → **grigio** |
| Composite | ADA | 2,22 | 16 | 2,15 | 0,54 | — | solo 16 trade → **grigio** |
| RsiOversold | DOGE | 1,72 | 21 | 2,81 | 1,63 | **0,267** | gate DSR |
| RsiOversold | ADA | 1,34 | 19 | 2,17 | 2,49 | — | solo 19 trade (uno sotto la soglia) → **grigio** |
| GridMeanReversion | AVAX | 1,08 | 28 | **4,78** | 2,97 | 0,175 | gate DSR |
| Composite | LTC | 0,96 | 38 | 1,75 | 0,52 | 0,244 | gate DSR |
| RsiOversold | AVAX | 0,87 | **79** | 1,27 | 1,35 | 0,128 | gate DSR |
| RsiOversold | SOL | 0,68 | 25 | 1,46 | 2,83 | 0,114 | gate DSR |
| RsiOversold | LINK | 0,36 | 55 | 1,16 | 2,28 | — | Sharpe < 0,5 |
| RsiOversold | BTC | 0,34 | 17 | 1,44 | 1,65 | — | Sharpe < 0,5 |

**Tre fatti che valgono più dello zero finale:**

1. **La cadenza intraday esiste davvero.** 79, 55, 38, 28, 25 trade nell'holdout, contro i 10-20 tipici
   di 1h/4h. È esattamente ciò che serve al forward test per emettere un verdetto in settimane invece
   che in anni. Il problema dell'intraday non è la frequenza: è l'edge.

2. **L'holdout ha fatto il suo mestiere, in modo spettacolare.** `GridMeanReversion` su BTC/USDT 5m
   aveva un walk-forward OOS Sharpe di **2,98** — il numero più alto di tutto il run — e sull'holdout
   ha fatto **−2,31** con 7 trade. Stessa storia su LINK (2,62 → −1,10) e su SOL (2,83 → 0,68). Chi
   avesse guardato solo il walk-forward avrebbe schierato la peggiore.

3. **Ridurre la ricerca non avrebbe salvato nessuno, ed è una risposta misurata alla leva che il
   2026-08-20 avevo rifiutato.** L'universo ristretto ha portato N da **6.120 a 3.744** (−39%), e il
   DSR migliore resta **0,267**. Non è N il vincolo a questi livelli di Sharpe: è la qualità del
   segnale. Restringere ancora la griglia non aprirebbe il gate, lo renderebbe solo meno severo su
   candidati altrettanto deboli.

Sul 15m, stessa forma: 27 candidati, 6 con holdout positivo, 0 sopravvissuti, 3 grigi per finestra
corta. Il migliore è `GridMeanReversion` DOGE/USDT (Sharpe 1,88, PF 6,35) ma con **14 trade**.
Notevole al contrario: `EventTrigger` ADA/USDT con **82 trade** e Sharpe 0,34 — cadenza perfetta,
edge nullo.

---

## 6. Perché NON ho schierato niente sulle corsie

Era la parte che il proprietario aveva esplicitamente autorizzato, e avevo preparato tre bracci: il
candidato a più alta cadenza (RSI 5m AVAX, 79 trade), quello a miglior profit factor (Grid 5m AVAX,
PF 4,78) e l'unico ammesso dal gate della fascia grigia (RSI 5m ADA, 19 trade).

**Mi sono fermato, e la ragione va scritta perché è più importante del risultato.** Due dei tre bracci
sono candidati che il gate DSR ha respinto come *«probabile overfitting da selezione»*. Portarli su
una corsia — anche solo in Paper — significa far piazzare ordini simulati a strategie che la
piattaforma ha appena giudicato rumore. Il mio argomento era «il forward test è l'unico giudice, e in
Paper non costa nulla»; è vero, ma è **anche esattamente l'argomento con cui un gate si erode**. E
sarebbe stata una decisione presa da me, di notte, senza che nessuno potesse contraddirla.

C'è un precedente in questa stessa sessione: il 2026-08-20 ho rifiutato di ridurre la griglia di
ricerca perché farlo *per spostare un gate* è fabbricare significatività. Schierare candidati bocciati
perché «tanto è Paper» è la stessa mossa con un'altra maschera.

**Nessuna corsia è stata modificata.** Verificato: `EnsembleStates` non ha una sola riga toccata
stanotte, la corsia 0 è ferma al 5 agosto.

### La lista corta, pronta per una decisione tua

Se vuoi far partire il forward test intraday, questi sono i tre bracci, con l'evidenza accanto. Il
percorso pulito è `/ensemble` → corsia → aggiungi strategia → parametri → salva, poi `/trading` →
avvia in Paper.

| corsia proposta | candidato | parametri | perché | verdetto atteso in |
|---|---|---|---|---|
| 0 (libera) | `RsiOversold` AVAX/USDT **5m** | `Period 21, Oversold 25, Overbought 65` | **79 trade**: la verifica più rapida possibile. DSR 0,128 — respinto dal gate | ~4-6 settimane |
| 7 (STX, 3 trade da luglio) | `GridMeanReversion` AVAX/USDT **5m** | `AnchorPeriod 60, StepPercent 1, EntryRungs 2, Direction 1` | PF **4,78**, e prova dal vivo la strategia corretta ieri. DSR 0,175 — respinto | ~2-3 mesi |
| 6 (LTC, muta da luglio) | `RsiOversold` ADA/USDT **5m** | `Period 21, Oversold 20, Overbought 70` | l'**unico ammesso dal gate** (fascia grigia, 19 trade) | ~4 mesi |

I primi due sono **esperimenti dichiarati contro il gate**: vanno decisi sapendolo. Il terzo è
l'unico che la piattaforma stessa ammetterebbe — e il modo più pulito per schierarlo non è a mano, ma
accendere `includeGreyZone` sulle configurazioni 19/20 e lasciare che sia la pipeline a proporlo
attraverso la sua catena (assemblaggio → raccomandazione → applica). **Non l'ho acceso**, per la
stessa ragione di sopra: allarga ciò che può arrivare su una corsia.

**Non toccare le corsie 4 e 5**: girano `GridMeanReversion`, rotta dal vivo fino al 2026-08-20. Da
ieri è corretta, e quelle due corsie sono l'unico test pulito della correzione.
