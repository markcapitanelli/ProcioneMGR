# ProcioneMGR

Piattaforma di **ricerca e trading algoritmico** per criptovalute, costruita in **.NET 10 / Blazor Server**. Copre l'intero ciclo: ingest dati di mercato → analisi e feature engineering → machine learning e ottimizzazione → backtest → ensemble di strategie → esecuzione (Paper / Testnet / Live), con un layer di **supervisione autonoma** e uno di **supervisione AI advisory**.

> ⚠️ **Disclaimer.** Software sperimentale per ricerca personale. Non è consulenza finanziaria. Il trading di criptovalute (a maggior ragione con leva/futures) comporta rischio di perdita del capitale. L'esecuzione **Live è disabilitata di default** e protetta da molteplici barriere di sicurezza — usala a tuo rischio.

---

## Caratteristiche principali

- **Pipeline di ricerca end-to-end** (`/pipeline`) — dai dati grezzi a un ensemble applicabile al trading, in un unico flusso automatizzabile.
- **Machine Learning** (`/ml`, `/feature-selection`) — Microsoft.ML + LightGBM, selezione feature per Information Coefficient, modello "Champion" promuovibile.
- **Ottimizzazione** (`/optimization`) — ricerca iperparametri Grid **o** Bayesian (selettore `SearchStrategy`).
- **Backtest** (`/backtest`) — con suggerimento automatico di **stop-loss / take-profit** data-driven (percentili di escursione).
- **Ensemble & Multi-lane** (`/ensemble`) — 3 corsie ("lane") keyed indipendenti, con Champion schierabile per corsia.
- **Discovery creativo di strategie** (`/discovery`, `/alpha-mining`, `/strategies`) — generazione ed esplorazione automatica di strategie.
- **Regimi di mercato** (`/regimes`) — classificazione del regime (one-hot opzionale), riaddestrabile.
- **Pairs trading, volatilità, sentiment** (`/pairs-trading`, `/volatility`, `/sentiment`).
- **Esecuzione avanzata** (`/execution`) — ordini "sliced": TWAP / VWAP / Iceberg / Adaptive (Almgren-Chriss), solo Testnet/Live, default-off.
- **Autonomia** — scheduler (Cronos) che ri-applica automaticamente l'ensemble migliore con *hysteresis*, promozione automatica Paper→Testnet (**mai** Live), monitor di *decay*.
- **Supervisione AI advisory** (`/admin/ai-supervisor`) — layer basato su Claude (SDK Anthropic) che legge i run e scrive pareri; **advisory-only**, non può avviare trading né bypassare il `SafetyChecker`.
- **Osservabilità** (`/metrics`, `/dashboard`) — dashboard metriche + OpenTelemetry.
- **Exchange supportati** — Binance & Bitget (Spot / Futures / leva), con firma HMAC e credenziali cifrate AES-256.

## Stack tecnico

| Area | Tecnologia |
|------|-----------|
| Runtime / UI | .NET 10, Blazor Server (InteractiveServer) |
| Database | PostgreSQL (prod) o SQLite (dev) via EF Core 10 — dual-provider |
| ML | Microsoft.ML 5.0 + LightGBM, MathNet.Numerics |
| AI supervisor | Anthropic SDK (`claude-opus-4-8`), advisory-only |
| Scheduling | Cronos |
| Osservabilità | OpenTelemetry (OTLP) |
| Auth | ASP.NET Core Identity |

## Struttura del repository

```
ProcioneMGR/                    App Blazor principale (Components, Services, Data, Config)
ProcioneMGR.Migrations.Postgres/ Migrazioni EF Core per PostgreSQL
ProcioneMGR.Tests/              Suite di test (~640 test)
tools/                          Utility CLI: DataMigration, DbBackup, FuturesVerify,
                                PlatformExpand, StrategyHunter
scripts/run-postgres.ps1        Avvio persistente su PostgreSQL
docs/                           Report e roadmap (ML4T, Qlib, autonomia, pipeline, ...)
```

## Requisiti

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- (Opzionale, per prod) **PostgreSQL** — in dev è sufficiente SQLite
- (Opzionale) `ANTHROPIC_API_KEY` per il layer AI di supervisione

## Setup

1. **Configurazione** — copia il template e compila i segreti:
   ```bash
   cp ProcioneMGR/appsettings.json.example ProcioneMGR/appsettings.json
   ```
   Poi imposta:
   - `Security:MasterKey` — chiave AES-256 (base64 di 32 byte). Genera con:
     ```powershell
     [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Max 256 }))
     ```
   - `ConnectionStrings:PostgresConnection` — password del DB (solo se usi PostgreSQL)
   - `Database:Provider` — `SQLite` (default dev) o `PostgreSQL`

   > 🔐 `appsettings.json` è **gitignorato** e non deve mai essere committato: contiene MasterKey e password. La API key di Anthropic **non** va nel file — si legge solo dalla env `ANTHROPIC_API_KEY`.

2. **Avvio (SQLite, dev):**
   ```bash
   dotnet run --project ProcioneMGR
   ```

3. **Avvio (PostgreSQL, persistente):**
   ```powershell
   ./scripts/run-postgres.ps1
   ```

L'app crea/applica automaticamente le migrazioni al primo avvio.

## Test

```bash
dotnet test
```

## Barriere di sicurezza (safety-first)

Il sistema è progettato per non poter operare con denaro reale senza intervento umano esplicito:

- **Live disabilitato di default** (`Trading:LiveExecution:Enabled = false`) e richiede conferma manuale (`RequireManualConfirmationForLive`).
- **`SafetyChecker`** — limiti su size posizione, esposizione totale, perdita giornaliera, drawdown, numero posizioni aperte, intervallo minimo tra ordini.
- **Auto-promozione solo fino a Testnet** — il passaggio Testnet→Live resta **sempre** manuale.
- **AI supervisor advisory-only** — può porre un *veto* a una sostituzione, mai forzarla, mai avviare trading; se manca la API key degrada silenziosamente ad "approva".
- **Credenziali exchange cifrate** AES-256 a riposo.

---

*Progetto personale di ricerca. Documentazione dettagliata di ogni fase in [`docs/`](docs/).*
