# Report — Espansione dati + stress test completo della piattaforma

Obiettivo (richiesta utente): espandere al massimo i dati (storici, strategici, di analisi),
sfruttare e testare a fondo la piattaforma, cercare bug d'uso quotidiano e perfezionarla, PRIMA
di procedere alla migrazione (a PostgreSQL). "Accumulare più dati possibili."

## 1. Espansione dati storici

Prima: **18 coppie × 5 timeframe = 90 serie, 3,78M candele**.
Dopo: **30 coppie × 5 timeframe = 150 serie, ~7,45M candele** (DB 478 MB → **889 MB**).

Interventi (via nuovo harness `tools/PlatformExpand`, fase `ingest`, ~70 min, 6,03M candele
processate, 0 errori):

- **12 nuove coppie liquide** su Binance: OP, ARB, APT, SUI, INJ, TIA, SEI, AAVE, GRT, ALGO,
  ICP, HBAR — ciascuna su 1d/4h/1h/15m/5m.
- **Timeframe 5m per TUTTE le 30 coppie** (~158k candele l'una): è il timeframe chiave per
  l'intraday a leva richiesto dall'utente. Ha anche colmato un buco preesistente sulle 18 coppie
  originali (il loro 5m partiva dal 2025-05 nonostante il 15m dal 2025-01).
- **Storia più profonda** sulle coppie esistenti: 1d dal 2020-01 (prima dal 2023-07) e 4h dal
  2022-01 — più diversità di regime per walk-forward/holdout più robusti.
- Tutte le nuove serie registrate in watchlist (77 nuove `TrackedSeries`), quindi il
  `MarketDataSyncWorker` le mantiene aggiornate da solo.

Nota operativa: l'ingest è write-heavy e SQLite è a scrittore singolo, quindi l'app è stata
fermata per la durata (~70 min, finestra di manutenzione). Al riavvio la sessione Paper reale è
**ripartita intatta sulla corsia 0** (running=True, posizione aperta e 143 trade storici
preservati) — verificato nei log e in `/trading`.

## 2. Bug d'uso quotidiano trovato e corretto

**Watchlist: query N+1 sul conteggio candele** (`Components/Pages/Watchlist.razor`, `LoadAsync`).
La pagina eseguiva **una `CountAsync` per ogni serie tracciata** (ora ~110, diverse su serie 5m
da ~158k candele) a ogni caricamento — un pattern N+1 che l'espansione dati peggiorava
direttamente. Sostituito con **una sola query aggregata** `GROUP BY Symbol, Timeframe`:

```csharp
var counts = await db.OhlcvData
    .GroupBy(c => new { c.Symbol, c.Timeframe })
    .Select(g => new { g.Key.Symbol, g.Key.Timeframe, Count = g.Count() })
    .ToDictionaryAsync(x => (x.Symbol, x.Timeframe), x => x.Count);
```

Conteggi mostrati identici; ~110 round-trip → 1. Misurata dopo il fix: la GROUP BY gira in **5ms**
sull'intera tabella (7,45M righe) grazie all'indice composto `(Symbol, Timeframe, TimestampUtc)`.

## 3. Investigato ma NON un bug (misurato prima di toccare)

Sospetto iniziale: 8 pagine di analisi eseguono `SELECT DISTINCT Symbol[,Timeframe] FROM OhlcvData`
a ogni caricamento, ora su ~7,45M righe → possibile collo di bottiglia. **Misurato dai log EF: la
query gira in <0,5ms** — SQLite la soddisfa con uno *skip-scan* sull'indice composto (colonne di
testa), non un full scan. Nessuna modifica necessaria: le 8 pagine restano invariate. (Disciplina
misura-prima-di-cambiare: evitato di "ottimizzare" 8 pagine funzionanti senza motivo.)

Confermato inoltre che **tutte le pagine popolano i selettori di coppia dinamicamente** da
`OhlcvData` → le 12 nuove coppie compaiono ovunque **senza modifiche al codice** (verificato in
browser: ARB/USDT, INJ/USDT, SUI/USDT presenti in Backtest/Volatilità/Pairs/Regimi/Analisi).

## 4. Stress test end-to-end sui NUOVI dati (browser, app dal vivo)

Esercitati 13 pagine / 5+ motori sui dati nuovi, **zero errori** (console, network e log server
tutti puliti per l'intera sessione):

| Motore / Pagina        | Test sui dati nuovi                        | Esito |
|------------------------|--------------------------------------------|-------|
| Backtest               | ARB/USDT **5m**, 158.268 candele           | OK — completato, nessun errore |
| Volatilità (GARCH)     | INJ/USDT 1h, 4.332 rendimenti              | OK — persistenza 0,955, vol. LP 108% |
| Pairs Trading (coint.) | ARB/USDT vs OP/USDT, 4.333 candele allineate | OK — ADF −1,79, hedge 0,47, 165 trade |
| Regimi (K-means)       | ARB/USDT 1h                                | OK — Silhouette 0,433, 4 profili |
| Analisi serie          | SUI/USDT 1d, 731 candele                   | OK — gap/lap + stop-loss percentili |
| Sentiment / ML Lab / Optimization / Pipeline | render + selettori | OK — 200, nuove coppie presenti |
| Trading / Ensemble     | selettore corsia + sessione Paper corsia 0 | OK — ripartita intatta dopo riavvio |

## 5. Stress test dei motori di ricerca (fase `discover`, read-only, in parallelo all'app)

Discovery su universo espanso (30 coppie), che gira **read-only** in contemporanea all'app dal
vivo (coesistenza garantita da WAL — nessun conflitto):

- **Swing 1h/4h** (30 coppie, walk-forward 8/2/2 dal 2024-01): 118.800 combinazioni testate, 80
  candidati.
- **Intraday 15m** (30 coppie, walk-forward compresso 4/1/1 dal 2025-01): 390 job.
- **Totale: 160 candidati in 21,8 min**, miglior Sharpe out-of-sample 3,31 (RsiOversold ALGO 15m).

**Risultato onesto (importante).** La classifica per Sharpe OOS grezzo è **dominata da rumore a
basso numero di trade**: i primi ~20 candidati hanno Sharpe 2,5–3,3 ma **1–8 trade** nella
finestra walk-forward — Sharpe alto su 1-2 trade è fortuna, non edge. Applicando un filtro di
significatività (**≥10 trade**) e testando sul **holdout mai visto (2026-03→07)**, la maggior
parte va **negativa** (es. EmaCross AVAX 1h −4,87; DonchianBreakout DOGE 1h −3,16; MacdTrend DOGE
4h −4,42). Sopravvivono in pochi, ma reali:

| Strategia          | Symbol    | TF  | Holdout Sharpe | Holdout Ret | Trade |
|--------------------|-----------|-----|----------------|-------------|-------|
| RsiOversold        | DOT/USDT  | 15m | **+4,05**      | **+21,7%**  | 35    |
| Stochastic         | FIL/USDT  | 4h  | **+2,68**      | +17,6%      | 19    |
| DonchianBreakout   | SEI/USDT  | 1h  | **+1,77**      | +7,7%       | 36    |

Questo è **la disciplina di validazione che funziona**: la piattaforma espone l'overfitting
invece di nasconderlo (tasso di sopravvivenza basso = firma di una validazione corretta). Lezione
pratica confermata: non fidarsi mai dello Sharpe grezzo di discovery senza il doppio filtro
**numero-di-trade + holdout**. I candidati NON vengono auto-salvati (coerente con questa
disciplina): sono un artefatto da validare prima. Il 5m completo su 30 coppie è escluso
(richiederebbe ore); i dati 5m sono ora disponibili per cacce mirate via UI/pipeline. Classifica
completa in `tools/PlatformExpand/bin/.../expand-discovery.json`.

## 6. Verifica

- **Build**: 0 errori. **Suite test completa: 464/464 verdi** (incluso il fix Watchlist; nessuna
  regressione).
- Nuovo strumento: `tools/PlatformExpand` (fasi `stats` | `ingest` | `discover`), riutilizzabile
  per future espansioni/inventari.

## 7. Note per la migrazione (contesto per il prossimo passo)

- Il DB è passato a **~889 MB** (7,45M candele) e crescerà ancora con l'uso intraday quotidiano.
  Questo **rafforza concretamente** la raccomandazione di migrare a PostgreSQL (§2.6/§4.7 del
  piano): oggi SQLite regge (query chiave misurate <5ms grazie agli indici), ma la combinazione
  trading live 24/7 + cacce periodiche + ingest continuo su un file da ~1 GB è esattamente lo
  scenario in cui il limite a scrittore singolo di SQLite diventa un collo di bottiglia reale.
- Prima di migrare conviene un **checkpoint WAL** e un backup del file `app.db` (~889 MB).
