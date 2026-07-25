# Report E1 — Stat-arb cointegrazione 2.0 + F-queue (primo sviluppo ROADMAP-PROFITTO-INTRADAY)

*2026-07-24. Primi due item della roadmap sviluppati, integrati, testati e MISURATI sul database
reale. Esiti onesti sotto i numeri.*

---

## Cosa è stato costruito

**E1 — Cointegrazione 2.0 (market-neutral).** Il motore pairs aveva già il lookback DINAMICO
(`RollingPairsSpreadAnalyzer`, anti-look-ahead), z-score causale, stop di divergenza, slippage per
gamba. Aggiunti i due pezzi mancanti di E1:
- **Filtro di volatilità dello spread** (`MaxSpreadVolRatio`): salta gli ingressi quando la vol
  recente dello spread supera di un rapporto la vol di base — il regime in cui la mean-reversion
  diventa un blow-up. Rapporto causale, testato.
- **Fase `statarb`**: seleziona le coppie cointegrate su una finestra, sceglie le soglie z sulla
  selezione, le giudica sull'holdout mai visto, e passa i sopravvissuti dal **gemello sintetico**
  con le due gambe rese nulle indipendenti (co-movenza distrutta).

**F-queue — fill maker consapevole della coda.** `MakerQueuePenetrationPercent`: il fill richiede
che il prezzo PENETRI il livello, non che lo sfiori (proxy deterministico della posizione in coda).
Rende onesti i backtest ad alta frequenza. Tre test.

Suite: tutti verdi (E1 3 test, F-queue 3 test nuovi).

---

## Gli esiti, senza sconti

### E1 su 4h e 1h — zero coppie confermate

| Timeframe | Cointegrate (selezione) | Migliore holdout | Trade/coppia | Gemello sintetico |
|---|---|---|---|---|
| **4h** | 8/190 | ETC/FIL Sharpe **0,90** (+2,7%) | 42 | ETC/FIL 0,90 < P95 nullo 2,51 → **dentro il nullo** |
| **1h** | 9/190 | tutte NEGATIVE (migliore −1,71) | **77-90** | nessuna sopra 0,3 → 0 candidati |

**Zero coppie confermate** su entrambi i timeframe.

### La lezione, che è preziosa quanto un positivo

1. **"Più operazioni al giorno" è realizzato, e alla grande.** Le coppie stat-arb fanno **77-90
   trade** ciascuna a 1h, contro i 5-7 delle strategie direzionali. La domanda dell'utente — più
   operazioni — è tecnicamente soddisfatta.

2. **Ma quelle operazioni perdono denaro.** A 1h, con costi veri su DUE gambe per trade, il turnover
   domina il segnale: tutte e 9 le coppie negative sull'holdout. È la **lezione R2** (il costo
   dipende dal turnover) applicata al market-neutral: più operazioni senza edge = più perdite, non
   più guadagni. E F-queue, ora che c'è, renderebbe il conto ancora più severo (fill più difficili).

3. **A 4h il segnale è forse debolmente presente ma non distinguibile dal caso.** ETC/FIL ha
   holdout positivo (0,90) ma il gemello sintetico lo colloca dentro la distribuzione nulla: con un
   holdout di soli 4 mesi non si può dire che la sua cointegrazione batta due serie indipendenti.

### Onestà sui limiti di questo test (perché "negativo" non è "impossibile")

La letteratura che ottiene Sharpe 2+ sulla cointegrazione crypto usa condizioni diverse dalle
nostre, e vanno dette:
- **Holdout più lungo** (anni, non 4 mesi) — il nostro è troppo corto per un verdetto stat-arb
  confidente;
- **Frequenza più bassa** (spesso daily) — meno turnover, meno cost drag; ma daily = MENO
  operazioni, l'opposto di ciò che l'utente chiede;
- **Metodi copula** (dipendenza non-lineare) invece del solo z-score lineare;
- **Universi a volte meno liquidi** — dove l'inefficienza sopravvive.

Quindi il nostro negativo dice: *sul nostro universo (majors), con 4 mesi di holdout, con lo
z-score lineare e i costi veri, la cointegrazione semplice non produce un edge difendibile — e a
1h il turnover la affossa.* Non dice "lo stat-arb è morto".

---

## La tensione strutturale, ora in numeri

C'è una tensione che i dati continuano a confermare da ogni angolo:

> **Più operazioni al giorno ⟺ più costi pagati ⟺ serve un edge per-operazione più grande.**

- Le strategie direzionali: poche operazioni, nessun edge (4 negativi).
- Lo stat-arb a 1h: moltissime operazioni, edge per-operazione troppo piccolo per i costi.

La via d'uscita onesta a questa tensione **non è più operazioni della stessa qualità**, ma
operazioni di qualità DIVERSA:
- **Frequenza più bassa** dove l'edge stat-arb potrebbe sopravvivere ai costi (daily, con storia
  profonda — da testare);
- **Market making** (E4): l'UNICO modo di fare molte operazioni GUADAGNANDO invece di pagare i
  costi — incassi lo spread. Ma richiede i dati order book (D2) e il futuro server.
- **Meta-labeling** (M1): non più operazioni, ma le operazioni GIUSTE — un filtro che alza la
  precisione e sopprime i falsi positivi.

---

## Stato roadmap dopo questo sviluppo

| Item | Stato |
|---|---|
| E1 stat-arb cointegrazione 2.0 | ✅ COSTRUITO + MISURATO: 0 confermate su 4h/1h; "più operazioni" sì, ma perdono ai costi |
| F-queue fill maker in coda | ✅ COSTRUITO + testato (fondazione onestà 5m) |
| D1 order-flow intraday | da eseguire (reingest, pesante su API) |
| F-spread, F-impact | da costruire |
| E2 cross-sectional multi-fattore | il prossimo candidato (molte piccole posizioni ranked, combinatore regolarizzato) |
| E3 carry Bitget operativo | il candidato con base economica più solida (già misurato positivo) |
| M1 triple-barrier + meta-labeling | l'amplificatore, da applicare a un edge che passa il gate |

**Prossimo passo con più valore atteso onesto**: E3 (carry su Bitget) — è l'unico edge che abbiamo
MISURATO positivo (5-12%/anno), è un flusso non una previsione, e su Bitget è operabile. Oppure E2
(cross-sectional multi-fattore) per la strada "molte piccole posizioni". Lo stat-arb a bassa
frequenza (daily, storia profonda) è da ritestare quando serve un verdetto più confidente sulla
cointegrazione.
