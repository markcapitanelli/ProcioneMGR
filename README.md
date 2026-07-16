# ProcioneMGR

**Piattaforma di ricerca e trading algoritmico** per criptovalute, costruita in **.NET 10 / Blazor Server**. Copre l'intero ciclo di vita di una strategia — dall'ingestione dei dati di mercato fino all'esecuzione degli ordini — con un rigore metodologico anti-overfitting di stampo accademico (López de Prado, Jansen *ML4T*, Qlib di Microsoft) e un modello di sicurezza a più livelli che rende **fisicamente impossibile** operare con denaro reale senza intervento umano esplicito.

> ⚠️ **Disclaimer.** Software sperimentale per ricerca personale. **Non è consulenza finanziaria.** Il trading di criptovalute (a maggior ragione con leva/futures) comporta rischio concreto di perdita del capitale. L'esecuzione **Live è disabilitata di default** e protetta da molteplici barriere; qualunque risultato di backtest/ottimizzazione è soggetto a *selection bias* e non garantisce performance future. Usa a tuo rischio.

**Principio fondante:** *Safety > Solidità > Velocità.* Niente promozione automatica in Live; determinismo e anti-overfitting sono obbligatori, non opzionali.

---

## Indice

- [Cos'è](#cosè)
- [Il ciclo di vita di una strategia](#il-ciclo-di-vita-di-una-strategia)
- [Caratteristiche principali](#caratteristiche-principali)
- [Metodologia di ricerca (anti-overfitting)](#metodologia-di-ricerca-anti-overfitting)
- [Modello di sicurezza](#modello-di-sicurezza)
- [Architettura](#architettura)
- [Stack tecnico](#stack-tecnico)
- [Mappa delle pagine](#mappa-delle-pagine)
- [Requisiti](#requisiti)
- [Setup e avvio](#setup-e-avvio)
- [Test](#test)
- [Infrastruttura e deployment](#infrastruttura-e-deployment)
- [Struttura del repository](#struttura-del-repository)

---

## Cos'è

ProcioneMGR è un ambiente completo di **quant research** e **trading automatico** per un singolo operatore. A differenza di un semplice bot, integra in un'unica applicazione tutto ciò che serve per *scoprire, validare onestamente e mettere in produzione* strategie di trading, senza mai saltare i controlli che separano un edge reale da un artefatto statistico.

I due valori distintivi:

1. **Onestà statistica.** Ogni strategia passa da validazione *out-of-sample* (walk-forward, Purged/Combinatorial CV) e gate anti-overfitting (Deflated Sharpe Ratio, Probability of Backtest Overfitting). La piattaforma preferisce dire "questo edge non è significativo" piuttosto che illudere.
2. **Sicurezza per costruzione.** Il percorso verso il denaro reale è sbarrato da 5 livelli indipendenti di codice: nessuna metrica, per quanto eccellente, promuove automaticamente in Live.

## Il ciclo di vita di una strategia

```
  Dati            Ricerca              Validazione            Esecuzione
 ┌──────┐   ┌───────────────┐   ┌────────────────────┐   ┌──────────────────┐
 │Ingest│──▶│ Backtest      │──▶│ Walk-forward OOS   │──▶│ Paper            │
 │OHLCV │   │ Optimization  │   │ Purged/CPCV        │   │  ↓ (osservazione)│
 │      │   │ Discovery     │   │ Deflated Sharpe    │   │ Testnet          │
 │      │   │ ML / Alpha158 │   │ PBO gate           │   │  ↓ (SOLO manuale)│
 └──────┘   └───────────────┘   └────────────────────┘   │ Live ⛔ bloccato │
                                                          └──────────────────┘
```

Il passaggio **Paper → Testnet** può essere automatico (dopo settimane di osservazione reale e superamento delle soglie); il passaggio **Testnet → Live** è **sempre e solo** una decisione umana esplicita.

## Caratteristiche principali

### Dati e analisi
- **Ingestione OHLCV** da Binance e Bitget (Spot/Futures), con upsert idempotente e watchlist auto-aggiornata in background.
- **Analisi statistica** dei dati storici, **barre a volume/dollaro** (vs barre a tempo) con confronto statistico.
- **Classificazione dei regimi di mercato** (trend/laterale) riaddestrabile, con encoding one-hot opzionale.

### Ricerca e machine learning
- **Backtest** (`/backtest`) con report ricco: Profit Factor, Kestner ratio, **criterio di Kelly** (binario, continuo, empirico), analisi **Montecarlo**, e un **leverage advisor** che stima P(rovina) per livello di leva.
- **Optimization** (`/optimization`) — Grid Search **o** Bayesian, sempre in modalità **walk-forward** con Deflated Sharpe sui risultati.
- **Alpha158** — catalogo di ~150 fattori causali in stile Qlib (KBAR, rolling su prezzo/volume, correlazioni), con invariante anti-look-ahead verificata sull'intero catalogo.
- **Modelli ML** (`/ml`) — Linear, Random Forest, LightGBM, MLP (rete C# pura), predittori *stacked*, con selezione feature per **Information Coefficient** (`/feature-selection`).
- **Discovery** (`/discovery`) — ricerca autonoma delle combinazioni strategia×coppia×timeframe più promettenti su tutto l'universo, con gate anti-overfitting.
- **Pipeline autonoma** (`/pipeline`) — automazione end-to-end da dati grezzi a strategia applicabile, in un flusso schedulabile.
- **Genetic alpha mining** (`/alpha-mining`).

### Portafoglio ed esecuzione
- **Ottimizzazione di portafoglio** (`/portfolio`) — Mean-Variance (Max Sharpe / Min Variance), Risk Parity (**ERC** esatto), **HRP** (Hierarchical Risk Parity), con stimatore di covarianza **Ledoit-Wolf**.
- **Ensemble multi-lane** (`/ensemble`) — 3 corsie ("lane") keyed indipendenti e isolate, ognuna un ensemble di strategie pesato per Sharpe rolling con vincoli Min/Max.
- **Esecuzione avanzata** (`/execution`) — ordini "sliced" TWAP / VWAP / Iceberg / **Adaptive** (Almgren-Chriss closed-form), solo Testnet/Live, default-off.
- **Pairs trading, volatilità (GARCH), sentiment** (`/pairs-trading`, `/volatility`, `/sentiment`).

### Automazione e supervisione
- **Trading engine** (`/trading`) per corsia — Paper / Testnet / Live con `SafetyChecker` su ogni ordine.
- **Auto-promozione** Paper → Testnet con *hysteresis* (mai Live), **monitor di decadimento** (realizzato vs atteso) e **feature drift** (PSI / KS / Page-Hinkley).
- **Scheduler** (Cronos) che ri-applica automaticamente l'ensemble migliore.
- **Supervisione AI advisory** (`/admin/ai-supervisor`) — layer basato su Claude (SDK Anthropic, `claude-opus-4-8`) che legge i run e scrive pareri: **advisory-only**, non può avviare trading né bypassare il `SafetyChecker`; se manca la API key degrada silenziosamente ad "approva".
- **Osservabilità** (`/metrics`, `/dashboard`) — KPI di piattaforma + OpenTelemetry.

## Metodologia di ricerca (anti-overfitting)

Il cuore intellettuale della piattaforma. Riferimenti: Marcos López de Prado (*Advances in Financial ML*), Stefan Jansen (*Machine Learning for Trading*), Qlib.

| Tecnica | A cosa serve | Dove |
|---|---|---|
| **Walk-forward** (IS/OOS/Step) | Validazione fuori campione realistica | Optimization, Discovery, Pipeline |
| **Purged Time-Series CV** | Elimina il leakage dei forward-return sovrapposti (purge + embargo) | `PurgedTimeSeriesCv` |
| **Combinatorial Purged CV** | Molti percorsi OOS dallo stesso storico → alimenta il PBO | `CombinatorialPurgedCv` |
| **Deflated Sharpe Ratio** | Corregge lo Sharpe per il numero di trial (selection bias) | Optimization/Discovery |
| **Probability of Backtest Overfitting** | Probabilità che il "migliore" sia rumore | Overfitting gate |
| **IC + t-stat Newey-West** | Significatività dell'edge robusta all'autocorrelazione | Feature selection |
| **Criterio di Kelly** (empirico) | Sizing prudente robusto alle code grasse cripto | Risk sizing |

Il messaggio è coerente: se il *Deflated Sharpe* dice "non significativo", la piattaforma lo scrive a chiare lettere invece di nasconderlo.

## Modello di sicurezza

Il sistema è progettato perché sia **impossibile per il codice** andare in Live senza intervento umano esplicito. Il confine è difeso su **5 livelli indipendenti**:

1. **`PromotionEvaluator`** — la modalità suggerita non è **mai** `Live`; le corsie Live non vengono nemmeno valutate.
2. **`PromotionWorker`** — agisce solo su transizioni Paper↔Testnet; una decisione incoerente viene loggata come errore, mai eseguita.
3. **`LanePromoter`** — solleva un'eccezione se richiesto di passare una corsia a Live.
4. **`TradingEngine`** — blocca l'avvio Live con la master key placeholder di sviluppo; il modello "Champion" del registry **non può** alimentare una corsia Live.
5. **`SafetyChecker`** (su ogni ordine) — limiti *fail-closed* su size posizione, esposizione totale, perdita giornaliera, drawdown, numero posizioni aperte, intervallo minimo tra ordini, leva massima; ordine Live rifiutato senza conferma manuale; capitale non positivo → ordine rifiutato.

Inoltre: **credenziali exchange cifrate AES-256-GCM** a riposo (mai in chiaro sul DB), auth via ASP.NET Core Identity, e `RequireManualConfirmationForLive` che tiene ogni ordine Live in coda finché l'operatore non lo approva a mano.

## Architettura

Nato come **monolite modulare** Blazor Server, il progetto è stato progressivamente scomposto in **microservizi opzionali** estratti dietro feature-toggle (default-off): l'app resta eseguibile come singolo processo, ma i motori pesanti possono girare separati e comunicare via **gRPC**.

```
┌───────────────────────────────────────────────────────────┐
│  ProcioneMGR  (Blazor Server, UI + orchestrazione)         │
│  Services/  Data/ (EF Core)  Components/ (pagine .razor)   │
└───────────────────────────────────────────────────────────┘
        │ gRPC (feature-toggle, default in-process)
        ├──────────────┬───────────────┬─────────────────────
        ▼              ▼               ▼
 ProcioneMGR.Ingestion  ProcioneMGR.Ml   ProcioneMGR.Trading
  (sync OHLCV)          (inferenza ML)    (motore ordini)
        └──────────────┴───────────────┴──── ProcioneMGR.Contracts (.proto)
```

**Progetti della soluzione:**

| Progetto | Ruolo |
|---|---|
| `ProcioneMGR` | App Blazor principale: UI, servizi di dominio, EF Core, orchestrazione |
| `ProcioneMGR.Contracts` | Definizioni gRPC (`.proto`) condivise |
| `ProcioneMGR.Ingestion` | Microservizio di ingestione OHLCV (toggle `MarketData:UseRemoteIngestion`) |
| `ProcioneMGR.Ml` | Microservizio di inferenza ML via gRPC (toggle `Ml:Enabled`) |
| `ProcioneMGR.Trading` | Microservizio del motore di trading (toggle `Trading:UseRemoteTrading`) |
| `ProcioneMGR.Migrations.Postgres` | Migrazioni EF Core per PostgreSQL |
| `ProcioneMGR.Tests` | Suite di test (**988 test**, unit + integrazione + bUnit) |

**Multi-lane:** il trading è isolato in 3 corsie (`TradingLanes.Count`), ciascuna con motore, ensemble e stato indipendenti (keyed DI) sullo stesso database — così si possono osservare più strategie in parallelo senza interferenze.

## Stack tecnico

| Area | Tecnologia |
|------|-----------|
| Runtime / UI | .NET 10, Blazor Server (InteractiveServer) |
| Database | PostgreSQL via EF Core 10 (Npgsql) — **unico provider** |
| ML | Microsoft.ML 5 + LightGBM, MathNet.Numerics |
| Comunicazione servizi | gRPC (HTTP/2, h2c) |
| AI supervisor | Anthropic SDK (`claude-opus-4-8`), advisory-only |
| Scheduling | Cronos |
| Osservabilità | OpenTelemetry (OTLP), stack LGTM-lite |
| Orchestrazione | Kubernetes (kind), GitOps con ArgoCD |
| Auth | ASP.NET Core Identity |
| Test | xUnit, Testcontainers (PostgreSQL effimero), bUnit |

## Mappa delle pagine

| Sezione | Pagine |
|---|---|
| **Overview** | `/` (workflow guidato), `/dashboard` |
| **Dati & Monitoraggio** | `/market/watchlist`, `/market-analysis`, `/market/bars`, `/metrics` |
| **Ricerca & Sviluppo** | `/backtest`, `/optimization`, `/feature-selection`, `/ml`, `/ensemble`, `/portfolio`, `/registry`, `/experiments` |
| **Strumenti Avanzati** | `/discovery`, `/pipeline`, `/alpha-mining`, `/regimes`, `/pairs-trading`, `/volatility`, `/sentiment`, `/strategies`, `/execution` |
| **Trading** | `/trading` (Paper/Testnet/Live) |
| **Configurazione** | `/settings/exchanges`, `/admin/ai-supervisor`, `/admin/autonomy`, `/admin/users`, `/admin/backup` |

## Requisiti

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **PostgreSQL** (unico provider) + client `pg_dump`/`pg_restore` nel PATH (per `/admin/backup`)
- **Docker** — richiesto solo per eseguire i test (PostgreSQL effimero via Testcontainers)
- (Opzionale) `ANTHROPIC_API_KEY` per il layer AI di supervisione

## Setup e avvio

1. **Configurazione** — copia il template e compila i segreti (il file è gitignorato):
   ```bash
   cp ProcioneMGR/appsettings.json.example ProcioneMGR/appsettings.json
   ```
   Poi imposta:
   - `Security:MasterKey` — chiave AES-256 (base64 di 32 byte). Genera con:
     ```powershell
     [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Max 256 }))
     ```
     In alternativa esportala come variabile d'ambiente `PROCIONE_MGR_MASTER_KEY` (ha priorità su appsettings).
   - `ConnectionStrings:PostgresConnection` — host/db/utente/password del PostgreSQL.

   > 🔐 `appsettings.json` **non va mai committato**: contiene MasterKey e password. La API key di Anthropic si legge **solo** dalla env `ANTHROPIC_API_KEY`, mai dal file. Con la master key placeholder di sviluppo, il trading **Live è bloccato** per costruzione.

2. **Schema DB** — applica le migrazioni PostgreSQL:
   ```powershell
   dotnet ef database update --project ProcioneMGR.Migrations.Postgres --startup-project ProcioneMGR
   ```
   (L'app applica comunque le migrazioni automaticamente al primo avvio.)

3. **Avvio:**
   ```powershell
   ./scripts/run-postgres.ps1        # avvio persistente su PostgreSQL
   # oppure
   dotnet run --project ProcioneMGR
   ```

## Test

Richiedono **Docker in esecuzione**: la suite avvia un PostgreSQL effimero via Testcontainers e crea uno schema usa-e-getta per ogni test d'integrazione. I test di logica pura girano senza Docker.

```bash
dotnet test        # 988 test: unit (matematica/algoritmi), integrazione (Postgres), UI (bUnit)
```

La suite copre, tra l'altro: correttezza matematica dei fattori Alpha158 (anti-look-ahead), invarianti anti-leakage della cross-validation, ottimizzatori di portafoglio su matrici degeneri, `SafetyChecker` in scenari estremi, stress test di ingestione/training/esecuzione concorrente, la state machine di promozione (con fuzzing anti-Live), e i componenti Blazor critici.

## Infrastruttura e deployment

- **Container** — Dockerfile per ogni immagine (main + 3 microservizi), pubblicate su GHCR da workflow CI (matrice di 4 immagini).
- **Kubernetes** — manifest per cluster `kind`, con `NetworkPolicy` (enforcement Calico), secret separati e stack di osservabilità LGTM-lite dietro `Observability:Enabled`.
- **GitOps** — deploy via **ArgoCD** (sync manuale).
- **CI** — build + test dell'intera soluzione ad ogni push/PR (`build-test`), build delle immagini (`build-images`).

I microservizi (pipeline, supervisor, ingestion, ml, trading) restano **in-process per default**: l'estrazione è attivabile per fase tramite i rispettivi feature-toggle.

## Struttura del repository

```
ProcioneMGR/                     App Blazor: Components/ Services/ Data/ Config/
ProcioneMGR.Contracts/           Protos gRPC condivisi
ProcioneMGR.Ingestion/           Microservizio ingestione OHLCV
ProcioneMGR.Ml/                  Microservizio inferenza ML
ProcioneMGR.Trading/             Microservizio motore di trading
ProcioneMGR.Migrations.Postgres/ Migrazioni EF Core (PostgreSQL)
ProcioneMGR.Tests/               Suite di test (988)
tools/                           CLI: DbBackup, FuturesVerify, PlatformExpand, StrategyHunter, SpotVerify, ...
infra/k8s/                       Manifest Kubernetes (deployment/service/networkpolicy) + ArgoCD, jobs
scripts/                         run-postgres.ps1, bootstrap K8s/ArgoCD, osservabilità
docs/                            Report e roadmap (ML4T, Qlib, autonomia, pipeline, microservizi, ...)
```

---

*Progetto personale di ricerca quantitativa. La documentazione dettagliata di ogni fase di sviluppo vive in [`docs/`](docs/).*
