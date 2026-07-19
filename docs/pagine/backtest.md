# Backtest — `/backtest`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Backtest.razor`](../../ProcioneMGR/Components/Pages/Backtest.razor) (~670 righe) |
| **Route** | `/backtest` (accetta query string di handoff, vedi sotto) |
| **Sezione navigazione** | Ricerca & Sviluppo |
| **Accesso** | `[Authorize]`; il bottone di handoff verso Optimization è solo Manager/Admin |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Simula una strategia sui dati storici: la piattaforma rilegge le candele una a una come se
il tempo scorresse davvero, applica le regole della strategia scelta e simula compravendite
con un capitale virtuale. È il **primo passo del workflow di ricerca** (dopo i dati): tutte
le protezioni (SL/TP/trailing), gli attriti (fee, slippage, funding) e la leva sono
configurabili, con default prudenti.

La pagina non fa solo il backtest: sul risultato offre **analisi del rischio di secondo
livello** — performance report sui trade, Monte Carlo, Performance Control, criterio di
Kelly e consulente leva.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 23–74 | Glossario completo (timeframe, position size, win rate, drawdown, equity, trade list…) |
| ConfigPresets | 79–81 | Preset nominati + ripristino automatico dell'ultima configurazione (`PageKey="backtest"`); l'auto-restore è disattivato se si arriva con handoff o `?load=` |
| Form serie | 82–119 | Exchange, symbol (datalist), timeframe, intervallo Da/A |
| `DataAvailability` | 122–124 | Validazione preventiva: la pagina sa quali dati esistono **prima** del Run e disabilita il bottone se non ce ne sono |
| Strategia + parametri | 128–154 | Select dalle `StrategyFactory.Prototypes`; gli input parametro (`_paramInputs`) si rigenerano al cambio strategia con i default del prototipo |
| `AdvancedPanel` | 158–195 | Fee, SL/TP/trailing (0=off), bottone **Suggerisci SL/TP**, leva (1–125), slippage, funding — con riassunto leggibile a pannello chiuso (`AdvancedSummary`, righe 527–530) |
| Azioni | 197–213 | Run/Stop, Salva come strategia (con nome), link a Le mie Strategie |
| KPI risultato | 224–237 | Final Capital, Total Return, Win Rate, Max Drawdown, Trades, ⚠ Liquidazioni (se leva) |
| Handoff Optimization | 241–248 | "Ottimizza questa strategia →" apre Optimization precompilata (solo Manager/Admin) |
| Performance report | 250–294 | Net Profit, Profit Factor, Average Trade, Reward/Risk, Kestner Ratio, drawdown in valuta, delay tra picchi, profitti annui, **Kelly criterion** (se ≥10 trade decisi) |
| Analisi del rischio | 296–389 | Monte Carlo (ricombinazioni + rumore), **consulente leva** (bootstrap con P(rovina) e liquidazioni per scenario), Performance Control (equity-curve trading) |
| Equity curve | 392–400 | `OhlcvChart` in modalità solo-indicatori (`ShowCandles="false"`) |
| Trade list | 402–439 | Prime 500 operazioni con direzione, prezzi, quantità, PnL |

## Come funziona (flusso del codice)

### Architettura: pagina sottile, service grosso
Quasi tutta la logica sta in **`BacktestPageService`** (iniettato come `Svc`): la pagina
mantiene solo lo stato del form e delega. Il contratto è lo snapshot immutabile
`BacktestConfigSnapshot` (righe 492–497) che fotografa l'intero form; `ApplySnapshot`
(righe 500–518) fa il percorso inverso. Risultati e serie (Result, TradeReport, McResult,
PcResult, Kelly, LeverageAdvice, EquitySeries) vivono nel service e la pagina li legge come
proprietà.

### Avvio — `OnInitializedAsync` (righe 559–578)
1. Recupera lo userId per preset e salvataggi.
2. **Handoff da Optimization**: se la query string contiene
   `exchange/symbol/timeframe/strategy/from/to/parameters`, `Svc.ApplyHandoff` la converte
   in uno snapshot (valori assenti o malformati lasciano i default: "il link è una comodità,
   mai un requisito").
3. Carica i simboli noti; se c'è `?load={id}` carica una strategia salvata
   (`Svc.LoadSavedStrategyAsync`) e ne ripristina nome+parametri.

### Run — `RunAsync` (righe 599–628)
Salva la configurazione come "ultima usata", poi `Svc.RunAsync(Snapshot(), token)` con
`CancellationTokenSource` per lo Stop. Il service carica le candele, costruisce la strategia
via `StrategyFactory`, esegue il `BacktestEngine` e prepara risultato + report + equity.

### Suggerisci SL/TP — `SuggestBracketAsync` (righe 537–550)
Chiama `Svc.SuggestBracketAsync(Snapshot())` che usa `ExcursionAnalyzer.SuggestBracket`:
SL e TP calcolati dai **percentili 95° di escursione avversa/favorevole** della serie
selezionata (approccio data-driven, stesso metodo della pagina Analisi Serie).

### Analisi post-risultato
- `RunMonteCarlo(shuffles, noise)` — ricombina l'ordine dei trade (con rumore opzionale) e
  stima il drawdown al 95° percentile: il "livello di guardia" oltre cui spegnere il sistema.
- `RunPerformanceControl(window, threshold)` — equity-curve trading: sospende i trade quando
  la performance recente scende sotto soglia e confronta originale vs controllato.
- **Kelly** e **consulente leva** sono calcolati dal service assieme al report: frazione
  ottima e half-Kelly consigliato; per la leva un bootstrap sui trade con P(-50%), P(rovina)
  e tasso di liquidazione per scenario, con evidenziata la leva massima sostenibile.

### Salvataggio strategia — `SaveAsync` (righe 636–642)
`Svc.SaveStrategyAsync(nome, strategia, parametri, userId)` persiste in `SavedStrategies`:
la strategia diventa riusabile in [Le mie Strategie](strategies.md), [Ensemble](ensemble.md)
e nel trading.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `BacktestPageService` | Tutta la logica: run, report, MC/PC, Kelly, leva, preset, handoff | [`Services/Backtesting/BacktestPageService.cs`](../../ProcioneMGR/Services/Backtesting/BacktestPageService.cs) |
| `IStrategyFactory` | Prototipi strategia (nome, display name, parametri con default) | [`Services/Backtesting/StrategyFactory.cs`](../../ProcioneMGR/Services/Backtesting/StrategyFactory.cs) |
| `IBacktestEngine` (via service) | Il motore di simulazione candela-per-candela | [`Services/Backtesting/BacktestEngine.cs`](../../ProcioneMGR/Services/Backtesting/BacktestEngine.cs) |
| Strategie concrete | EmaCross, RSI, Bollinger, MACD, Donchian, Supertrend, Momentum, Stochastic, VWAP, PriceSma, CompositeSignal, EventTrigger, RegimeConditional, Ml… | [`Services/Backtesting/`](../../ProcioneMGR/Services/Backtesting) |
| `TradeStatistics` (via service) | Performance report (PF, Kestner, drawdown in valuta, delay picchi…) | [`Services/Optimization/TradeStatistics.cs`](../../ProcioneMGR/Services/Optimization/TradeStatistics.cs) |
| `MonteCarloAnalyzer` (via service) | Ricombinazioni equity e DD 95° percentile | [`Services/Risk/MonteCarloAnalyzer.cs`](../../ProcioneMGR/Services/Risk/MonteCarloAnalyzer.cs) |
| `KellyCalculator` (via service) | Frazione di Kelly e half-Kelly | [`Services/Risk/KellyCalculator.cs`](../../ProcioneMGR/Services/Risk/KellyCalculator.cs) |
| `LeverageAdvisor` (via service) | Scenari di leva con P(rovina) e liquidazioni | [`Services/Risk/LeverageAdvisor.cs`](../../ProcioneMGR/Services/Risk/LeverageAdvisor.cs) |
| `PerformanceControlService` (via service) | Equity-curve trading | [`Services/Risk/PerformanceControlService.cs`](../../ProcioneMGR/Services/Risk/PerformanceControlService.cs) |
| `ExcursionAnalyzer` (via service) | Suggerimento SL/TP dai percentili di escursione | [`Services/Analysis/ExcursionAnalyzer.cs`](../../ProcioneMGR/Services/Analysis/ExcursionAnalyzer.cs) |
| `ConfigPresets` / `DataAvailability` / `AdvancedPanel` / `Stat` | Componenti condivisi (preset, gating dati, pannello avanzate, KPI tile) | [`Components/Shared/`](../../ProcioneMGR/Components/Shared) |

## Dati letti / scritti

- **Legge**: `OhlcvData` (candele del run), `SavedStrategies` (con `?load=`), `UserPageConfigs` (preset).
- **Scrive**: `SavedStrategies` (salvataggio), `UserPageConfigs` (ultima configurazione).

## Collegamenti con le altre pagine

- **In ingresso**: [Optimization](optimization.md) ("Backtest →" sulle righe della Top 10
  arriva qui con la query string), [Le mie Strategie](strategies.md) (`?load={id}`).
- **In uscita**: "Ottimizza questa strategia →" verso [Optimization](optimization.md)
  precompilata (`BacktestPageService.OptimizationHandoffUrl`).
- [Analisi Serie](market-analysis.md) — stessa logica percentile per lo stop suggerito.

## Note di design

- Il backtest simula SL/TP **intrabar** (usando high/low della candela), mentre il motore
  live valuta a chiusura barra: asimmetria nota, censita nell'audit algoritmico 2026-07.
- La card "⚠ LIQUIDAZIONI" appare solo con leva >1 quando la simulazione registra
  liquidazioni: segnale che la configurazione è troppo aggressiva.
- Il pattern snapshot/service rende la configurazione serializzabile una volta sola per
  preset, handoff URL e run: nessuna duplicazione della forma dei parametri.
