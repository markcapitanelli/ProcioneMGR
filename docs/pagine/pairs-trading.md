# Pairs Trading — `/pairs-trading`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/PairsTrading.razor`](../../ProcioneMGR/Components/Pages/PairsTrading.razor) (~450 righe) |
| **Route** | `/pairs-trading` |
| **Sezione navigazione** | Strumenti Avanzati |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

**Statistical arbitrage**: invece di scommettere sulla direzione di un asset, si scommette
sulla **relazione** fra due asset che normalmente si muovono insieme. Quando il loro spread
(differenza pesata dall'hedge ratio) si allarga più del solito, si compra il relativamente
economico e si shorta il relativamente caro — posizione **dollar-neutral** che guadagna al
rientro dello spread, indipendentemente dal mercato.

Prerequisito verificato dalla pagina: i due asset devono essere **cointegrati**
(test Engle-Granger / ADF). Se "Cointegrate? = No", la coppia in quel periodo è rischiosa:
lo spread potrebbe divergere per sempre.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 24–57 | Concetto, cointegrazione/ADF, hedge ratio, ricalibrazione, z-score, soglie |
| Configurazione | 59–158 | Exchange, Symbol Y / Symbol X, timeframe, periodo; **doppia `DataAvailability`** (una per gamba, righe 100–105) |
| `AdvancedPanel` | 108–143 | Lookback ristima (90), ricalibra ogni (30), z-score lookback (20), soglia entrata \|z\|≥2.0, uscita \|z\|≤0.5, capitale, size per gamba, fee |
| KPI cointegrazione | 160–167 | Cointegrate? / ADF stat / hedge ratio full-sample |
| Risultato backtest | 169–232 | KPI, equity curve, **grafico z-score rolling walk-forward**, trade list (lato "Long Y/Short X" o inverso, prezzi delle due gambe, hedge ratio all'entrata) |

## Come funziona (flusso del codice)

### `RunAsync` (righe 318–434)
1. Validazioni: symbol diversi, intervallo valido, dati sufficienti per **entrambe** le
   gambe (minimo `lookback + zLookback + 10` candele).
2. **Allineamento**: `PairsCandleAligner.Align` tiene solo i timestamp comuni alle due serie.
3. **Screening di cointegrazione** full-sample (righe 359–367):
   `EngleGrangerCointegrationTest().Test(closeY, closeX)` → informativo, dice se la coppia
   ha senso nel periodo.
4. **Backtest** (`Engine.RunBacktest`): l'hedge ratio è **rolling e walk-forward** —
   ristimato ogni `RecalibrationInterval` barre usando solo le ultime `LookbackWindow`
   barre passate (mai il futuro). Lo z-score dello spread è calcolato su `ZScoreLookback`
   barre; entrata quando |z| supera la soglia, uscita al rientro.
5. **Visualizzazione**: equity dalla curva dell'engine; lo z-score è **ricalcolato con lo
   stesso `RollingPairsSpreadAnalyzer`** dell'engine, solo per il grafico (nessuna doppia
   verità).

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IPairsBacktestEngine` / `PairsBacktestEngine` | Il backtest dollar-neutral con ricalibrazione rolling | [`Services/PairsTrading/PairsBacktestEngine.cs`](../../ProcioneMGR/Services/PairsTrading/PairsBacktestEngine.cs) |
| `PairsCandleAligner` | Allineamento per timestamp delle due serie | [`Services/PairsTrading/PairsCandleAligner.cs`](../../ProcioneMGR/Services/PairsTrading/PairsCandleAligner.cs) |
| `RollingPairsSpreadAnalyzer` | Hedge ratio rolling + z-score walk-forward | [`Services/PairsTrading/RollingPairsSpreadAnalyzer.cs`](../../ProcioneMGR/Services/PairsTrading/RollingPairsSpreadAnalyzer.cs) |
| `EngleGrangerCointegrationTest` | Test ADF sulla relazione | [`Services/TimeSeries/EngleGrangerCointegrationTest.cs`](../../ProcioneMGR/Services/TimeSeries/EngleGrangerCointegrationTest.cs) |
| `OlsRegression` (via test/analyzer) | Stima dell'hedge ratio | [`Services/TimeSeries/OlsRegression.cs`](../../ProcioneMGR/Services/TimeSeries/OlsRegression.cs) |

## Dati letti / scritti

- **Legge**: `OhlcvData` per entrambi i symbol.
- **Scrive**: `UserPageConfigs` (preset). Il backtest è puramente analitico.

## Collegamenti con le altre pagine

- [Watchlist](watchlist.md) — servono dati per **due** serie sullo stesso timeframe.
- [Volatilità](volatility.md) / [Regimes](regimes.md) — analisi complementari di contesto.

## Note di design

- La ricalibrazione walk-forward dell'hedge ratio evita il look-ahead bias del ratio
  full-sample (usato solo come screening informativo).
- Dall'audit algoritmico 2026-07: il test di cointegrazione è considerato **troppo
  liberale** e manca uno **stop di divergenza** sulla posizione — due dei punti 🔴 da
  chiudere prima del capitale reale su questa strategia.
