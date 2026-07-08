# Report caccia alle strategie — 2 luglio 2026

> Campagna sistematica di ricerca strategie su dati nuovi, condotta con il metodo completo
> della piattaforma: ingestione → discovery walk-forward → validazione con varianti stop →
> **verdetto su holdout mai visto** → probe di robustezza cross-asset → salvataggio dei soli
> sopravvissuti. Nessun denaro reale coinvolto.

## 1. Dati

Ingestione da Binance (klines pubbliche): **10 coppie × 3 timeframe (1h/4h/1d), dal
2023-07-01 a oggi = 340.200 candele** — BTC, ETH, SOL, BNB, XRP, DOGE, ADA, LINK, AVAX,
LTC (tutte /USDT). Le 30 serie sono state registrate in **watchlist** (Enabled): il worker
di sync le terrà aggiornate automaticamente ogni 5 minuti quando l'app è in esecuzione.

## 2. Metodo (anti-overfitting by design)

- **Periodo di selezione**: 2023-07-01 → 2026-03-01. Tutte le scelte (parametri, varianti
  stop) sono state fatte SOLO qui.
- **Holdout**: 2026-03-01 → 2026-07-02, mai toccato da nessuna decisione. Solo verdetto.
- **Discovery**: 10 coppie × 3 TF × 7 strategie = 210 job walk-forward (IS 12 mesi / OOS 3),
  28.440 combinazioni testate, selezione su Sharpe in-sample, ranking su Sharpe OOS.
- **Filtro statistico**: OOS Sharpe > 0.3, ≥ 12 trade OOS, ≥ 3 finestre — molti candidati
  apparentemente ottimi (Sharpe OOS > 2) avevano 1-3 trade: rumore, scartati.
- **Validazione dei 15 finalisti**: 4 varianti di stop (base / SL3% / TSL5% / SL3+TSL5)
  scelte sul periodo di selezione; Monte Carlo evoluta (500 ricombinazioni) e Kelly sui
  trade di selezione; verdetto finale sull'holdout.
- **Probe cross-asset**: le configurazioni sopravvissute, identiche, su tutte le 10 coppie.

## 3. Risultato onesto: 2 sopravvissuti su 15

L'holdout (primavera 2026, mercato laterale/ribassista) ha **bocciato 13 candidati su 15**
— quasi tutti trend-following che in selezione mostravano Sharpe 0.5-1.5. Questo è il
comportamento atteso da una validazione seria: la maggior parte di ciò che luccica sul
passato non regge sul futuro.

### 🥇 PriceSmaCross DOGE/USDT 4h — Period=100, AllowShort=1, Stop loss 3%
*Salvata in “Le mie strategie” come `PriceSmaCross DOGE 4h [SL3]`.*

| Fase | Sharpe | Return | PF | Max DD | Trades |
|---|---|---|---|---|---|
| Walk-forward OOS (6 finestre) | 1.75 | — | — | — | 12 |
| Selezione (32 mesi) | 1.36 | +108% | 2.16 | 11.1% | 211 |
| **Holdout (4 mesi mai visti)** | **1.23** | **+4.7%** | **1.81** | **4.2%** | **31** |

- **Unico candidato positivo in TUTTE le fasi**, con coerenza IS≈OOS in discovery
  (1.98 vs 1.75) — il segno distintivo di un sistema non sovra-adattato.
- Kestner 0.46 (curva regolare), Monte Carlo RiskFactor95 = **1.95×** → livello di guardia:
  spegnere se un futuro drawdown supera ~2× quello storico di selezione.
- **Kelly 11.4% → usare l'half-Kelly ≈ 5-6% del capitale per trade.**
- Probe cross-asset: la logica (trend 100-periodi con short e stop) è positiva su **8/10
  coppie nel periodo di selezione** — non è un artefatto di DOGE — ma nell'holdout ostile
  al trend sopravvive solo su DOGE: aspettarsi che soffra nei mercati laterali.

### 🥈 BollingerMeanReversion BNB/USDT 4h — Period=14, StdDev=2.5, senza stop
*Salvata come `BollingerMeanReversion BNB 4h [base]`. Riserva complementare, evidenza più debole.*

| Fase | Sharpe | Return | PF | Max DD | Trades |
|---|---|---|---|---|---|
| Walk-forward OOS | 1.17 | — | — | — | 13 |
| Selezione | -0.01 | +4.6% | 1.12 | 8.2% | 151 |
| **Holdout** | **0.37** | **+1.2%** | **1.59** | **2.5%** | **19** |

- Profilo "mai negativo" ma rendimento modesto; in selezione è piatta.
- Probe: la mean reversion è **negativa su 8/10 coppie in selezione** (mercato in trend) e
  **positiva su 7/10 nell'holdout** (mercato laterale) — è l'immagine speculare del
  trend-following. Il suo valore è di **copertura di regime**, non di motore di profitto.

## 4. La lezione strutturale (più preziosa delle singole strategie)

Il probe dimostra che **trend-following e mean-reversion si alternano con il regime di
mercato**: chi vinceva nella selezione perdeva nell'holdout e viceversa. La risposta
giusta non è cercare "la strategia perfetta", ma:

1. **Ensemble regime-aware** (già in piattaforma): combinare le due strategie salvate con
   la pesatura per regime attiva — il K-means dei regimi decide quando pesare il trend e
   quando la mean reversion.
2. **Performance Control** (cap. 8 Trombetta, in piattaforma): finestra 10 trade, soglia 0
   — spegne automaticamente la gamba che il regime sta punendo.
3. **Position sizing**: half-Kelly ≈ 5% per trade sul trend, meno sulla mean reversion.
4. **Livello di guardia Monte Carlo**: stop definitivo del sistema se il drawdown supera
   ~2× il massimo storico di selezione.

## 5. Prossimi passi consigliati (in ordine)

1. **Paper trading** delle due strategie salvate per 1-3 mesi (il worker tiene i dati
   aggiornati; la pagina Trading è già pronta) — è l'"incubation period" del metodo.
2. Comporre un **Ensemble** con le due strategie + regime-aware weighting e confrontare
   l'equity combinata con le singole.
3. Ripetere la **discovery ogni 3 mesi** sui dati nuovi (walk-forward vivo): le
   configurazioni vanno ri-validate, non tenute per fede. Lo strumento usato per questa
   campagna è conservato in `tools/StrategyHunter/` (app FERMA durante l'esecuzione):
   `dotnet run -- ingest` → `-- discover` → `-- validate` → `-- probe` → `-- save`
   (aggiornare prima le date di selezione/holdout in `Program.cs`).
4. Non alzare il position size oltre l'half-Kelly finché il paper trading non conferma le
   metriche di selezione (degrado accettabile: -30% su PF e Average Trade, regola Trombetta).

## 6. Caccia v2 — leva, 15 minuti e 8 coppie nuove (stesso giorno)

Su richiesta (budget < 1000€, uso intensivo della leva) la piattaforma è stata estesa:

- **Motore di backtest a margine**: `Leverage`, **liquidazione intrabar** (margine di
  mantenimento 0.5%), **funding** dei perpetual pro-rata, **slippage** sfavorevole su ogni
  fill. A leva 1 la contabilità coincide al centesimo con lo spot (360/360 test).
- **LeverageAdvisor**: bootstrap dei trade con pavimento di liquidazione → per ogni leva
  {1,2,3,5,10,20}: crescita mediana, P(-50% dal picco), P(rovina), quota liquidazioni, e
  leva consigliata (la più alta con P(-50%) ≤ 10%). Visibile nella pagina Backtest.
- **Dati v2**: +8 coppie (NEAR, TRX, UNI, ATOM, FIL, SHIB, PEPE, DOT) e **15m** per tutte
  e 18 (da 2025-01) = 1.218.335 nuove candele, tutte in watchlist auto-sync.
- **Caccia v2**: 62.568 combinazioni (WF 12/3/3 sui TF classici, 6/2/2 sul 15m),
  validazione con **slippage 0.05% su ogni fill**, advisor di leva sui finalisti.

**Verdetto holdout v2: 13 bocciati su 15.** Sopravvissuti:

### 🥇 RsiOversold DOT/USDT 15m — Period=14, Oversold=20, Overbought=65, senza stop
*Salvata come `RsiOversold DOT 15m [base]`. **Leva consigliata: 3×.***

| Fase | Sharpe | Return | PF | Max DD | Trades |
|---|---|---|---|---|---|
| Walk-forward OOS (15m, 6/2/2) | 1.82 | — | — | — | 21 |
| Selezione (14 mesi, slippage incluso) | 1.12 | +29.8% | 1.52 | 14.9% | 125 |
| **Holdout (4 mesi mai visti)** | **4.05** | **+21.7%** | **5.76** | **4.7%** | **35** |

- Mean reversion di breve periodo: compra RSI<20, esce a RSI>65 — la famiglia giusta per
  il regime laterale attuale (conferma della lezione §4). Kelly 23.6%, MC RiskFactor95 1.85×.
- Attenzione onesta: l'holdout eccezionale coincide con un regime FAVOREVOLE alla mean
  reversion; l'aspettativa realistica è la metrica di selezione (Sharpe ~1.1), non il 4.05.

### 🥈 PriceSmaCross DOGE 4h — riconfermata anche con slippage (holdout Sharpe 1.13, PF 1.72)

## 7. Guida operativa per ~1000€ con leva (dal LeverageAdvisor)

1. **Leva 3×, non di più**, su entrambe le strategie salvate: oltre, la probabilità di
   dimezzarsi supera il 10% e la crescita mediana NON aumenta (oltre il picco di Kelly più
   leva = più rischio E meno rendimento).
2. **Margine per trade: 20% del capitale** (≈200€) → esposizione ≈600€ per posizione.
   Mai più di 2 posizioni contemporanee.
3. **Stop loss sempre**: a 3× la liquidazione è lontana (~33%), ma lo stop del sistema
   (o il 3% della variante SL3) limita la perdita per trade a ~6% del capitale.
4. Le due strategie sono **complementari per regime** (trend 4h + mean reversion 15m):
   insieme in Ensemble regime-aware coprono entrambe le fasi di mercato.
5. Il **funding** dei perpetual (~0.01%/8h) pesa poco sul 15m (holding brevi) e
   moderatamente sul 4h: nel backtest si può stimare col campo dedicato.
6. Con 1000€ le commissioni minime e gli arrotondamenti di quantità pesano di più che nei
   backtest: partire in **paper trading** e confrontare i fill reali con quelli teorici.

## 8. Onestà finale

- 4 mesi di holdout positivo su una coppia NON sono una garanzia: sono la migliore evidenza
  disponibile oggi, ottenuta senza barare.
- Il +4.7% in 4 mesi dell'holdout di PriceSmaCross DOGE, annualizzato ~15%, con DD 4.2%,
  è un profilo realistico — diffidare di chi promette di più.
- 13 strategie su 15 sono state bocciate: è il costo (e il valore) del metodo.
