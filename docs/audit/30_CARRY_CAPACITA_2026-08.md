# Capacità e universo del carry — misura del 2026-08-20

**Item**: I16 ≡ F12. **Riproducibile con**: `dotnet run --project tools/PlatformExpand -- carrycapacity all`

---

## Il verdetto, in una riga

**Negli ultimi 365 giorni il carry non è profittevole su nessuno dei sei mercati, a nessuna soglia
che apra e a nessuna taglia.** La soglia in vigore (5%) non è il problema e spostarla non è la
soluzione: **il premio non c'è più.**

Sulla storia intera il carry funziona — ma a una soglia di **12-20%**, non a 5, e con una capacità
di **1-5 milioni per gamba** sui due mercati più profondi.

---

## 1. Il premio, e la sua compressione

Funding annualizzato medio per simbolo e per anno (42.644 eventi reali, 2019-09 → 2026-08):

| anno | BTC | ETH | SOL | BNB | XRP | DOGE |
|---|---|---|---|---|---|---|
| 2021 | 30,6 | 37,5 | 28,6 | 21,7 | 48,1 | 38,5 |
| 2022 | 4,2 | 0,8 | −35,6 | −12,6 | 0,0 | 3,4 |
| 2023 | 7,9 | 8,3 | 1,3 | −8,4 | 8,2 | 10,0 |
| 2024 | 11,9 | 13,0 | 13,6 | −3,3 | 14,2 | 14,0 |
| 2025 | 5,1 | 4,9 | 0,4 | −2,1 | 3,5 | 4,3 |
| **2026** | **2,3** | **1,2** | **−2,1** | **2,5** | **−1,2** | **2,1** |

Nel 2026 **nessuno dei sei supera il 3%**, due sono negativi. Il benchmark esterno citato nella
roadmap (basis 25% → <5% in due anni) è confermato sui nostri dati, e anzi superato.

**Quanto spesso il funding paga abbastanza per aprire** (frazione dei punti di decisione in cui la
media mobile a 9 eventi supera la soglia):

| anno | sopra 5% | sopra 10% | negativo |
|---|---|---|---|
| 2021 | 82,6% | 73,8% | 11,3% |
| 2024 | 70,4% | 46,5% | 9,3% |
| 2025 | 36,5% | 6,8% | 21,3% |
| **2026** | **19,9%** | **0,5%** | **35,9%** |

Nel 2026 il carry aprirebbe un quinto del tempo, quasi mai sopra il 10%, e **più di un terzo del
tempo si pagherebbe** invece di incassare.

---

## 2. Il numero che governa tutto: il pareggio

Un round trip paga quattro fill su due gambe: `2·(0,10+0,03) + 2·(0,05+0,03) = **0,420%**` del
nozionale (fee spot 0,10%, perp 0,05%, slippage base 0,03%).

| premio annualizzato | giorni in posizione solo per **pareggiare** |
|---|---|
| 3% | 51,1 |
| **5%** (soglia in vigore) | **30,7** |
| 8% | 19,2 |
| 12% | 12,8 |
| 20% | 7,7 |

**È qui che la soglia a 5% si rompe.** Serve un mese pieno in posizione per non perdere — e la durata
mediana degli episodi, misurata, sta fra 1 e 13,7 giorni su **cinque mercati su sei** (solo BTC
arriva a 29,2). Si paga il round trip
più volte di quante lo si ripaghi.

---

## 3. Universo: i sei mercati, ultimi 365 giorni

Misurato alla taglia minima (100.000 per gamba, impatto trascurabile) — quindi è il **caso migliore**
per l'edge, prima di qualunque considerazione di capacità.

| simbolo | ADV (90gg) | miglior soglia che apre | netto ann. | trade/mese | durata mediana |
|---|---|---|---|---|---|
| BTC | 1,06 mld | 5% | **−0,84%** | 0,50 | 29,2 gg |
| DOGE | 40,6 mln | 12% | **−1,23%** | 0,08 | 1,0 gg |
| XRP | 87,4 mln | 8% | **−2,23%** | 0,25 | 13,7 gg |
| ETH | 488 mln | 8% | **−3,21%** | 0,58 | 6,7 gg |
| SOL | 152 mln | 8% | **−4,69%** | 0,58 | 3,7 gg |
| BNB | 81,2 mln | 8% | **−7,12%** | 0,67 | 3,0 gg |

Alla soglia **in vigore** (5%) il conto è peggiore ovunque tranne BTC: ETH −5,44%, SOL −8,81%,
BNB −10,37%, XRP −10,07%, **DOGE −23,0%**.

Il confronto fra durata mediana e pareggio spiega tutto: BTC regge 29,2 giorni contro un pareggio di
30,7 — arriva quasi a coprire i costi e infatti perde poco. BNB chiude in 3 giorni contro 19,2 di
pareggio, e perde sette punti.

---

## 4. Capacità: la curva √-impatto

L'impatto segue la legge empirica di Almgren già in repo: `impatto = 0,1 · √(nozionale/ADV) · 100`.
Su BTC (ADV 1,06 mld) lo slippage per gamba passa da 0,127% a 100k fino a 3,098% a 100 milioni.

**Sulla storia intera** — l'unico periodo in cui l'edge esiste — il netto resta positivo fino a:

| simbolo | capacità per gamba | alla soglia |
|---|---|---|
| BTC | **5.000.000** | 12% |
| ETH | **5.000.000** | 12% |
| SOL | 1.000.000 | 20% |
| XRP | 1.000.000 | 20% |
| DOGE | 100.000 | 20% |
| BNB | *nessuna* (negativo ovunque) | — |

### E a che ritmo, dove funziona

La capacità di 5 milioni su BTC si ottiene alla soglia del 12%, che apre **14 volte in sette anni**:
**0,17 trade/mese**, durata mediana **44,3 giorni**, in posizione il 32,5% del tempo. Su ETH è
ancora più rado: 0,12 trade/mese, mediana 75 giorni.

È un dato da mettere accanto agli altri, non da nascondere: **dove il carry funziona, funziona a un
ritmo di due operazioni all'anno e con posizioni che durano mesi.** È l'opposto dell'orizzonte
intraday/swing breve che governa il resto della piattaforma. Non è un difetto della misura — è la
natura del carry — ma significa che questa classe non compete per lo stesso capitale né per le stesse
corsie delle altre, e va giudicata con un altro metro.

**Negli ultimi 365 giorni la capacità è zero su tutti e sei**: non c'è taglia abbastanza piccola da
rendere positivo un edge che non c'è.

---

## 5. Che cosa è misurato e che cosa è modellato

Non hanno lo stesso statuto e vanno letti diversamente.

- **Il premio è MISURATO**: sette anni di funding reale, 42.644 eventi, dalla stessa tabella che
  alimenta il worker.
- **La decisione è la STESSA dell'operatività**: l'analisi interroga `CarryBacktestEngine`, che usa
  `CarryDecider` — la funzione pura del worker. Non è una re-implementazione.
- **La capacità è MODELLATA**: il coefficiente d'impatto (0,1) è dichiarato illustrativo nel repo e
  **non è calibrato su fill veri**. La curva dice la *forma* del decadimento, non il livello esatto.
- **L'ADV è di oggi, il funding è storico.** Sulla riga «storia intera» la capacità è quindi misurata
  con la liquidità sbagliata (i mercati erano più sottili). È per questo che la riga da guardare per
  decidere adesso è quella degli **ultimi 365 giorni**.
- **Solo Binance.** Bitget — l'unico exchange a leva utilizzabile da IT/UE dopo la restrizione MiCA
  del 2026-07 — non ha storia di funding in questo database. Il premio potrebbe esservi diverso, e
  **non lo sappiamo**.

---

## 6. Cosa ne segue

Tre cose, in ordine di quanto sono sostenute dalla misura.

1. **La soglia a 5% è sbagliata, e lo era anche negli anni buoni.** Sulla storia intera l'ottimo è
   12-20% su tutti e sei; a 5% si lasciano da 1,5 a 14 punti di netto annualizzato secondo il
   simbolo. Il motivo è aritmetico: a 5% il pareggio è a 30,7 giorni e gli episodi non durano tanto.
2. **Oggi il carry non paga, e non è un problema di parametri.** Nessuna combinazione di soglia e
   taglia è positiva sull'ultimo anno. Alzare la soglia riduce la perdita perché fa aprire di meno,
   non perché trovi un premio migliore.
3. **La capacità, quando l'edge c'era, era di 1-5 milioni per gamba.** È un vincolo da conoscere
   prima di dimensionare, non dopo — ed è il motivo per cui questa misura andava fatta.

**Non si propone nulla di automatico.** Il carry Paper resta acceso: continua a produrre osservazioni
a costo zero, ed è la sorgente che dirà se e quando il premio torna. Questo documento è il
riferimento contro cui confrontarlo.
