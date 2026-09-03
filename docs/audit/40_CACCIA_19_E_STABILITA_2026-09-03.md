# La caccia 19, e la cosa più grande che c'era sotto

> **Data:** 2026-09-03 · Nasce dalla richiesta del proprietario: «*ok ora affrontiamo la config 19*».
>
> Il quadro che avevo dato il 2026-09-02 era **sbagliato nella metà che conta**, e correggerlo ha
> fatto emergere un difetto che riguarda tutte e quattro le cacce.

---

## 1. Quello che avevo detto, e quello che è vero

Avevo scritto: *«la config 19 consuma il 62 % del budget e ha schierato zero gambe»*. La prima metà
regge come proiezione, la seconda è **fuorviante**.

### Il costo, misurato

Finestra di 30 giorni, durate wall-clock da `PipelineRuns`:

| cfg | run | ore in finestra | giorni attivi | **ore/mese al ritmo** | quota |
|---|---|---|---|---|---|
| **19** (5m) | 18 | 15,31 | 12,9 | **35,7** | ~52 % |
| 18 (1h) | 61 | 20,89 | 29,8 | 21,1 | ~31 % |
| 20 (15m) | 16 | 2,82 | 12,9 | 6,6 | ~10 % |
| 17 (4h) | 60 | 4,89 | 29,4 | 5,0 | ~7 % |

Il «62 %» era una proiezione mensile; il numero onesto è **~52 % del budget proiettato**, oppure
**33 % delle ore davvero consumate negli ultimi 30 giorni** — la 19 è attiva solo da 13 giorni.

### La qualità, misurata — ed è il ribaltamento

Confronto sul **solo motore corrente** (dal 2026-08-23, quando il walk-forward è stato sostituito):

| cfg | righe | chiavi | Sharpe mediano | p90 | trade mediani | % ≥ 0,5 |
|---|---|---|---|---|---|---|
| **19** | 288 | 30 | **+0,215** | **1,794** | **21** | **37,8 %** |
| 20 | 483 | 67 | −0,103 | 1,426 | 14 | 31,7 % |
| 17 | 2 273 | 284 | −0,394 | 1,247 | 11 | 25,4 % |
| 18 | 1 442 | 124 | −0,419 | 1,386 | 17 | 23,2 % |

**La config 19 è la migliore su ogni dimensione**: unico Sharpe mediano positivo, p90 più alto, più
trade in holdout, e la quota più alta di candidati sopra la soglia.

E arriva più lontano di tutte nella catena dei cancelli:

| cfg | chiavi | arrivate al gate DSR | **tasso** | DSR massimo |
|---|---|---|---|---|
| **19** | 30 | 11 | **36,7 %** | 0,516 |
| 18 | 124 | 13 | 10,5 % | 0,718 |
| 20 | 67 | 7 | 10,4 % | 0,451 |
| 17 | 284 | 22 | 7,7 % | **0,817** |

Un candidato della 19 ha **quattro volte più probabilità** di superare Sharpe *e* conteggio trade e
arrivare all'ultimo cancello. Quello che le manca è il **tetto sul DSR**: 0,52 contro lo 0,82 della
17, su una soglia di 0,95 che *nessuna configurazione ha mai raggiunto* (0 righe su 711).

> **Perché la resa in «chiavi grigie» la faceva sembrare sterile.** La fascia grigia raccoglie chi è
> bocciato *solo* per finestra corta o pochi trade. I candidati della 19 hanno **21 trade mediani**,
> quindi non ci finiscono: vengono bocciati altrove, o passerebbero. Misurare una caccia col numero
> di suoi scarti di un certo tipo premia chi produce scarti di quel tipo.

---

## 2. La decisione del proprietario: rallentare, non spegnere

**La cadenza era sproporzionata alla finestra.** Misurato: **14,1 ore mediane** fra un run e il
successivo, per muovere una finestra di holdout di **112 giorni**. Fra un giro e l'altro entrano
**288 candele nuove su circa 32.000** — lo **0,5 %**. Quarantaquattro minuti di calcolo per mezzo
punto percentuale di dati nuovi.

E il pomello era **uno solo per tutte**: `VettingCampaign.BackoffHours` più `Campaign:RearmHours`,
mentre la mediana per run va da **3,7 minuti** (cfg 17) a **43,8** (cfg 19) — **dodici volte**.
Alzare il pomello globale per rallentare la 19 rallentava anche la 17, che costa un dodicesimo.

**K56**: `PipelineConfiguration.MinHoursBetweenRuns`, per-configurazione, `0` = nessun limite
proprio. Il pianificatore la rispetta nella rotazione; il wake da trigger di regime **non la
scavalca** (un regime nuovo è una ragione per svegliare la rotazione, non per pagare 44 minuti su
dati che non si sono mossi). Manopola in `/pipeline`, con accanto la traduzione in ore/mese
calcolata sulla durata **misurata** di quella caccia.

**Applicato: config 19 → 48 ore.** Costo atteso da **35,7 a ~11 ore al mese**, −69 %. La finestra si
muove dell'1,8 % fra un giro e l'altro invece dello 0,5 %.

---

## 3. Quello che c'era sotto, e vale più della prima decisione

Mentre misuravo la cadenza ho guardato **quanto cambia il risultato fra rimisurazioni della stessa
identica ipotesi** — stessa strategia, stesso simbolo, stesso timeframe, **stessi parametri**.

```
RsiOversold ADA/USDT 5m #079a89f7   17 misure   da 0,966 a 1,803   (mediana 1,413)
RsiOversold DOGE/USDT 5m #079a89f7  17 misure   da 0,844 a 1,856   (mediana 1,134)
```

E non è una cosa del 5m. **Il nullo lo dice, e smentisce il mio istinto:**

| cfg | chiavi rimisurate | misure mediane | **ampiezza mediana** | Sharpe mediano |
|---|---|---|---|---|
| 17 (4h) | 168 | 14 | **0,752** | −0,377 |
| 18 (1h) | 96 | 16 | 0,616 | −0,370 |
| **19 (5m)** | 24 | 13 | **0,534** | +0,269 |
| 20 (15m) | 36 | 13 | 0,398 | −0,218 |

La 19 ha la dispersione **seconda più bassa**, non la più alta. È la caccia a 4h la più rumorosa.

**Il cancello dello Sharpe holdout sta a 0,5.** Un ventaglio di 0,4-0,75 su una soglia di 0,5
significa che, per una fetta consistente delle ipotesi, **passare o non passare dipende da quale
notte si guarda**. E la fascia grigia ordina per Sharpe: propone, per costruzione, la notte in cui il
ventaglio era al massimo.

### Quanto pesa, misurato

Su **324 chiavi giudicabili** (≥5 rimisurazioni, motore corrente):

| criterio | chiavi che passano |
|---|---|
| soglia 0,5 sul **massimo** — come oggi | **111** |
| soglia 0,5 sulla **mediana** | **87** |
| **solo col massimo** | **24** (il **22 %** di ciò che oggi viene proposto) |

Ventiquattro ipotesi entrano nella coda della flotta **solo perché è esistita una notte fortunata**.
È la radice di K54 — la corsia 6 porta 1,875 contro una mediana di 0,479 — vista dove nasce invece
che a valle.

### K57 — il gate, con la soglia presa dai dati

Il rapporto **ampiezza / mediana** ha mediana osservata **0,57**. La soglia scelta è
`ampiezza ≤ mediana × 1,0`: sta intorno al **73° percentile** e taglia il quarto peggiore.

| soglia | chiavi superstiti (su 87) |
|---|---|
| × 0,5 | 35 — più della metà buttata |
| **× 1,0** | **64** |
| × 1,5 | 76 |
| × 2,0 | 79 — non cambierebbe quasi nulla |

**Relativa e non assoluta**: un ventaglio di 1,0 su una mediana di 0,6 è più largo del valore, su una
di 3,0 è ordinario. Una soglia assoluta punirebbe le ipotesi migliori per essere migliori.

**Le instabili non si cancellano, scendono in fondo.** La lista dei grigi è una lista che un umano
legge e da cui sceglie: toglierle nasconderebbe che esistono. E chi ha meno di cinque rimisurazioni
resta dov'era — **l'ignoranza non retrocede nessuno**.

> **Ed è informazione gratuita.** Nessun calcolo nuovo, nessun run in più: le misure sono già in
> `ResearchCandidates`, pagate col budget di caccia e finora buttate a ogni giro tenendo solo
> l'ultima riga.

---

## 4. Cosa resta aperto

1. **Il DSR non è mai stato raggiunto da nessuno**: 0 righe su 711, massimo storico 0,817 contro una
   soglia di 0,95. Finché è così, il forward test resta l'unico giudice — ed è il motivo per cui la
   fascia grigia esiste.
2. **La config 18** costa 21,1 ore/mese ed è la peggiore per qualità (Sharpe mediano −0,419, 23,2 %
   sopra soglia). Ora ha la sua manopola; la decisione è del proprietario.
3. **Il gate di stabilità è applicato alla lista dei grigi**, non ancora alla coda automatica della
   flotta. Estenderlo è un passo naturale, ma cambia il percorso autonomo e va guardato girare prima.
