# 01 — Panoramica del progetto

> Audit read-only del 2026-08-04. Nessun file di progetto è stato modificato: gli unici file creati
> stanno in `docs/audit/`. Ogni affermazione è ancorata a un percorso verificabile; ciò che non ho
> potuto verificare è marcato **DA VERIFICARE**.

---

## Cos'è

**ProcioneMGR** è una piattaforma personale di **quant research e trading algoritmico** su
criptovalute, scritta in **.NET 10 / Blazor Server**. Copre l'intero ciclo di vita di una strategia:
ingestione dei dati di mercato → ricerca e backtest → validazione anti-overfitting → esecuzione
(Paper / Testnet / Live).

Fonte: [README.md](../../README.md), [docs/STATO-DELLA-PIATTAFORMA.md](../STATO-DELLA-PIATTAFORMA.md).

## Scopo principale

Due obiettivi dichiarati, entrambi visibili nel codice:

1. **Onestà statistica** — validazione out-of-sample obbligatoria (walk-forward, Purged/Combinatorial
   CV), gate anti-overfitting (Deflated Sharpe Ratio, Probability of Backtest Overfitting). Il
   progetto preferisce dichiarare "edge non significativo" piuttosto che mostrare un numero
   lusinghiero.
2. **Sicurezza per costruzione** — il passaggio a denaro reale è sbarrato da barriere di codice
   indipendenti. Nessuna metrica promuove automaticamente in Live.

Il README dichiara esplicitamente che, ad oggi, **la piattaforma non contiene una strategia che,
misurata onestamente, guadagni**: 445.280 combinazioni provate, zero significative al Deflated
Sharpe. È uno strumento di misura maturo, non una macchina da soldi.

## Tecnologie principali

| Area | Tecnologia | Dove si verifica |
|---|---|---|
| Runtime / UI | .NET 10, Blazor Server (InteractiveServer) | [ProcioneMGR.csproj](../../ProcioneMGR/ProcioneMGR.csproj) |
| Database | PostgreSQL via EF Core 10 (Npgsql) — **unico provider** | [ApplicationDbContext.cs](../../ProcioneMGR/Data/ApplicationDbContext.cs) |
| ML | Microsoft.ML + LightGBM, MathNet.Numerics | `ProcioneMGR/Services/ML/` |
| Comunicazione servizi | gRPC (HTTP/2) | [ProcioneMGR.Contracts/Protos/](../../ProcioneMGR.Contracts/Protos/) |
| Supervisione AI | Multi-provider OpenAI-compatible + Anthropic | `ProcioneMGR/Services/Llm/` |
| Scheduling | Cronos | `ProcioneMGR/Services/Pipeline/` |
| Osservabilità | OpenTelemetry (OTLP), opt-in | `ProcioneMGR/Services/Observability/` |
| Orchestrazione | Kubernetes (kind), GitOps ArgoCD | [infra/k8s/](../../infra/k8s/) |
| Auth | ASP.NET Core Identity (+ passkey/WebAuthn) | `ProcioneMGR/Components/Account/` |
| Test | xUnit, Testcontainers (PostgreSQL), bUnit | [ProcioneMGR.Tests/](../../ProcioneMGR.Tests/) |

## Entry point

- **Applicazione principale:** [ProcioneMGR/Program.cs](../../ProcioneMGR/Program.cs) — 674 righe,
  48,6 KB. Unico file di composizione: DI, hosted services, pipeline HTTP, `app.Run()` a riga 674.
- **Microservizi:** `ProcioneMGR.Ingestion/Program.cs`, `ProcioneMGR.Ml/Program.cs`,
  `ProcioneMGR.Trading/Program.cs` (host gRPC standalone).
- **CLI:** `tools/<Nome>/Program.cs` (5 strumenti).

## Comandi di avvio

```bash
./scripts/run-postgres.ps1
```

Lo script imposta `ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_URLS=http://localhost:5199`,
apre i port-forward verso il cluster kind (18080 ingestion, 18092 trading) e lancia
`dotnet run -c Release`. Vedi [scripts/run-postgres.ps1](../../scripts/run-postgres.ps1).

Avvio diretto equivalente (senza port-forward):

```bash
dotnet run --project ProcioneMGR --no-launch-profile -c Release
```

> ⚠️ **Difetto verificato durante questo audit.** `run-postgres.ps1` ha `$ErrorActionPreference =
> "Stop"` e chiama `kubectl` alle righe 42 e 58. Se il cluster kind non risponde, PowerShell 5.1
> converte lo stderr di `kubectl` in un `NativeCommandError` **terminante** e lo script muore prima
> di arrivare a `dotnet run` — nonostante i rami `else` siano scritti apposta per essere
> best-effort. Dettagli in [09_RISKS_AND_TECH_DEBT.md](09_RISKS_AND_TECH_DEBT.md#r2).

## Comandi di build

```bash
dotnet build ProcioneMGR.sln -c Release
```

## Comandi di test

```bash
dotnet test
```

Richiede **Docker in esecuzione**: i test d'integrazione avviano un PostgreSQL effimero via
Testcontainers. I test di logica pura girano senza Docker.

> **DA VERIFICARE — conteggio dei test.** Il [README.md](../../README.md) dichiara "988 test". Il
> conteggio statico degli attributi `[Fact]`/`[Theory]` in `ProcioneMGR.Tests/*.cs` restituisce
> **1999 righe corrispondenti** su 259 file. I due numeri non coincidono e il README è
> verosimilmente stale (le memorie di progetto citano 712 e poi 1011 in momenti diversi). Non ho
> eseguito la suite in questo audit: serve `dotnet test` con Docker attivo per il numero vero.

## Comandi di lint

Nessun linter dedicato configurato (nessun `.editorconfig` con regole di analisi rilevate, nessun
target `dotnet format` nella CI). La qualità è retta da compilazione + test. **DA VERIFICARE** se
esista una convenzione non documentata.

## Dipendenze principali

Rilevate da `ProcioneMGR/ProcioneMGR.csproj` e dall'uso nel codice:

- `Microsoft.EntityFrameworkCore` + `Npgsql.EntityFrameworkCore.PostgreSQL`
- `Microsoft.AspNetCore.Identity.EntityFrameworkCore`
- `Microsoft.ML` + LightGBM
- `MathNet.Numerics`
- `Grpc.AspNetCore` / `Grpc.Net.Client`
- `Cronos` (espressioni cron)
- `OpenTelemetry.*` (esportazione OTLP opt-in)
- SDK Anthropic + client OpenAI-compatible scritto in casa

## Struttura delle cartelle

```
ProcioneMGR/                       App Blazor — 414 .cs + 89 .razor
├── Program.cs                     Composizione unica (674 righe)
├── Components/                    UI
│   ├── Pages/                     Pagine con @page (66 route totali)
│   ├── Account/                   Identity scaffolded
│   ├── Layout/                    Layout condivisi
│   └── Shared/                    Componenti riusabili
├── Services/                      384 .cs — cuore del dominio
│   ├── Trading/ (60)  ML/ (37)  Pipeline/ (28)  Backtesting/ (24)
│   ├── Sentiment/ (21)  Alpha/ (14)  Exchanges/ (12)  Llm/ (12)  Regime/ (12)
│   └── …altre 28 cartelle
├── Data/                          21 .cs — entità EF + DbContext (30 DbSet)
└── Config/                        Configurazione
ProcioneMGR.Contracts/             5 .proto condivisi
ProcioneMGR.Ingestion/             Microservizio ingestione OHLCV
ProcioneMGR.Ml/                    Microservizio inferenza ML
ProcioneMGR.Trading/               Microservizio motore di trading
ProcioneMGR.Migrations.Postgres/   40 .cs — migrazioni EF Core
ProcioneMGR.Tests/                 259 .cs — suite di test
tools/                             5 CLI (solo 2 nella .sln — vedi 09)
infra/k8s/                         Manifest Kubernetes + ArgoCD
scripts/                           Avvio, bootstrap cluster, secret
docs/                              86 .md — PRD, report, roadmap, doc per-pagina
```

## Stato osservato dell'istanza in esecuzione (2026-08-04, ore 20:1x)

Numeri letti dalla UI reale su `http://localhost:5199`, non dal codice:

| Indicatore | Valore |
|---|---|
| Serie tracciate | 221 |
| Candele in archivio | ≈ 12.181.001 |
| Strategie salvate | 17 |
| Corsie di trading | **8** (0–7), tutte in **PAPER**; 3 e 7 non configurate |
| Stato trading | "Paper attivo" |
| Fattori in deriva | 32 segnalati in Home |

> Nota: il file di configurazione versionato più recente in repo dichiara `Trading:LaneCount = 3`,
> mentre l'istanza reale ne mostra **8**. La configurazione viva (`appsettings.json`) è gitignorata
> e non fa parte del repo — le due cose divergono legittimamente, ma è bene saperlo prima di leggere
> i file committati come se fossero la verità di produzione.
