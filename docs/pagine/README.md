# Documentazione delle pagine — ProcioneMGR

Un file per ogni pagina della piattaforma: **a cosa serve, come è strutturata la UI, come
funziona il codice, quali servizi/classi coinvolge, quali dati legge e scrive, e come si
collega alle altre pagine**. I riferimenti a righe di codice sono relativi ai sorgenti al
momento dell'analisi (2026-07-19).

La suddivisione segue la navigazione della piattaforma (fonte unica:
[`Components/Layout/NavModel.cs`](../../ProcioneMGR/Components/Layout/NavModel.cs)).

## 🏠 Overview

| Pagina | Route | Doc |
|---|---|---|
| Home | `/` | [home.md](home.md) |
| Dashboard | `/dashboard` | [dashboard.md](dashboard.md) |

## 📊 Dati & Monitoraggio

| Pagina | Route | Doc |
|---|---|---|
| Watchlist | `/market/watchlist` | [watchlist.md](watchlist.md) |
| Analisi Serie | `/market-analysis` | [market-analysis.md](market-analysis.md) |
| Barre informative | `/market/bars` | [market-bars.md](market-bars.md) |
| Metriche | `/metrics` | [metrics.md](metrics.md) |

## 🔬 Ricerca & Sviluppo

| Pagina | Route | Doc |
|---|---|---|
| Backtest | `/backtest` | [backtest.md](backtest.md) |
| Optimization | `/optimization` | [optimization.md](optimization.md) |
| Feature Selection (IC) | `/feature-selection` | [feature-selection.md](feature-selection.md) |
| ML Lab | `/ml` | [ml.md](ml.md) |
| Ensemble | `/ensemble` | [ensemble.md](ensemble.md) |
| Portafoglio | `/portfolio` | [portfolio.md](portfolio.md) |
| Registry Modelli | `/registry` | [registry.md](registry.md) |
| Esperimenti | `/experiments` | [experiments.md](experiments.md) |

## 🚀 Trading

| Pagina | Route | Doc |
|---|---|---|
| Trading (control center) | `/trading` | [trading.md](trading.md) |
| Le mie Strategie | `/strategies` | [strategies.md](strategies.md) |
| Execution Lab | `/execution` | [execution.md](execution.md) |

## 🧠 Strumenti Avanzati

| Pagina | Route | Doc |
|---|---|---|
| Discovery | `/discovery` | [discovery.md](discovery.md) |
| Pipeline | `/pipeline` | [pipeline.md](pipeline.md) |
| Campagne | `/campaign` | [campaign.md](campaign.md) |
| Alpha Mining | `/alpha-mining` | [alpha-mining.md](alpha-mining.md) |
| Regimes | `/regimes` | [regimes.md](regimes.md) |
| Pairs Trading | `/pairs-trading` | [pairs-trading.md](pairs-trading.md) |
| Volatilità | `/volatility` | [volatility.md](volatility.md) |
| Sentiment | `/sentiment` | [sentiment.md](sentiment.md) |

## ⚙️ Configurazione

| Pagina | Route | Doc |
|---|---|---|
| Credenziali Exchange | `/settings/exchanges` | [settings-exchanges.md](settings-exchanges.md) |
| Supervisione AI | `/admin/ai-supervisor` | [admin-ai-supervisor.md](admin-ai-supervisor.md) |
| Autonomia | `/admin/autonomy` | [admin-autonomy.md](admin-autonomy.md) |
| Gestione Utenti | `/admin/users` | [admin-users.md](admin-users.md) |
| Backup Database | `/admin/backup` | [admin-backup.md](admin-backup.md) |

## 👤 Account

| Pagine | Route | Doc |
|---|---|---|
| Login, registrazione, 2FA, passkey, profilo (scaffold Identity) | `/Account/*` | [account.md](account.md) |

## Il workflow in una riga

**Dati** (Watchlist/Dashboard) → **analisi a priori** (Analisi Serie) → **Backtest** →
**Optimization** (walk-forward + Deflated Sharpe) → **Ensemble** (multi-strategia per
corsia) → **Trading** (Paper → Testnet → Live, mai auto-Live). In parallelo: **ML Lab**
(con Feature Selection e Alpha Mining a monte, Registry a valle), la **Pipeline** che
automatizza l'intero giro con le **Campagne** a ruotare le cacce, e i monitor
(decadimento, drift, metriche, sentiment) a sorvegliare tutto.

## Componenti condivisi ricorrenti

| Componente | Ruolo | File |
|---|---|---|
| `GuidaPanel` | Il pannello "guida" in cima a ogni pagina | [`Components/Shared/`](../../ProcioneMGR/Components/Shared) |
| `ConfigPresets` | Preset nominati + memoria dell'ultima configurazione per pagina (`UserPageConfigs`) | [`Components/Shared/ConfigPresets.razor`](../../ProcioneMGR/Components/Shared/ConfigPresets.razor) |
| `DataAvailability` | Copertura dati e gating dei bottoni Run | [`Components/Shared/DataAvailability.razor`](../../ProcioneMGR/Components/Shared/DataAvailability.razor) |
| `AdvancedPanel` | Impostazioni avanzate ripiegate con riassunto leggibile | [`Components/Shared/AdvancedPanel.razor`](../../ProcioneMGR/Components/Shared/AdvancedPanel.razor) |
| `Stat` | Tile KPI unificata | [`Components/Shared/Stat.razor`](../../ProcioneMGR/Components/Shared/Stat.razor) |
| `OhlcvChart` | Grafico candele/indicatori (riusato anche per le equity curve) | [`Components/Pages/OhlcvChart.razor`](../../ProcioneMGR/Components/Pages/OhlcvChart.razor) |
| `PollingTimer` | Auto-refresh sicuro per le pagine live | [`Components/Shared/PollingTimer.cs`](../../ProcioneMGR/Components/Shared/PollingTimer.cs) |
| `wwwroot/js/charts.js` | Modulo Plotly condiviso (timeseries, bar, barh, donut, heatmap) | [`wwwroot/js/`](../../ProcioneMGR/wwwroot/js) |
