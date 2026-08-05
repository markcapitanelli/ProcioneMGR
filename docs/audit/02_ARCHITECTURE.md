# 02 — Architettura

## Architettura generale

**Monolite modulare Blazor Server** con **microservizi opzionali estratti dietro feature-toggle**.
L'app resta eseguibile come singolo processo; i motori pesanti possono girare separati e comunicare
via gRPC. Oggi, sulla macchina del proprietario, l'assetto reale è **"guscio freddo + core caldo"**:
l'app Blazor è il guscio (UI e orchestrazione), mentre ingestione e motore di trading vivono nel
cluster kind.

```
┌──────────────────────────────────────────────────────────────┐
│  ProcioneMGR — Blazor Server (il "guscio freddo")            │
│  Components/ (UI)   Services/ (dominio)   Data/ (EF Core)    │
│  :5199                                                        │
└──────────────────────────────────────────────────────────────┘
     │ gRPC h2c (feature-toggle; default in-process)
     ├─────────────────┬────────────────────┬──────────────────
     ▼                 ▼                    ▼
 .Ingestion        .Ml                  .Trading  ← "core caldo"
 sync OHLCV        inferenza ML          motore ordini
 :18080 (pf)                             :18092 (pf)
     └─────────────────┴────────────────────┴── .Contracts (5 .proto)
                              │
                              ▼
                   PostgreSQL (unico provider)
```

I tre toggle, letti da configurazione:

| Toggle | Effetto quando `true` |
|---|---|
| `MarketData:UseRemoteIngestion` | il monolite **non** avvia il worker di sync locale (mai due scrittori sulla stessa serie) |
| `Trading:UseRemoteTrading` | questo processo **non** registra motore, worker, feed real-time né carry: comanda via gRPC |
| `Ml:Enabled` | inferenza ML delegata al servizio remoto (hot-reload a ogni candela) |

I commenti in configurazione sono espliciti sul rischio: *«MAI entrambi i motori vivi»*. Il rollback
documentato è `false` + riavvio + `scale 0` del Deployment nel cluster.

## Frontend

- **Blazor Server** con render mode `InteractiveServer`: nessuna SPA, nessun bundle JS applicativo.
  Lo stato vive sul server, il browser mantiene un **WebSocket `/_blazor`** (verificato in
  [07_BROWSER_CHECK_REPORT.md](07_BROWSER_CHECK_REPORT.md)).
- Registrazione: `builder.Services.AddRazorComponents()` +
  `app.MapRazorComponents<App>()` — [Program.cs:44](../../ProcioneMGR/Program.cs#L44),
  [Program.cs:630](../../ProcioneMGR/Program.cs#L630).
- **Conseguenza architetturale importante:** non esiste una API REST interna per la UI. Le pagine
  chiamano direttamente i servizi C# in-process. Vedi
  [06_API_AND_INTEGRATIONS.md](06_API_AND_INTEGRATIONS.md).

## Backend

Tutto il dominio vive in `ProcioneMGR/Services/` — **384 file** in 38 sottocartelle. I raggruppamenti
maggiori: `Trading/` (60), `ML/` (37), `Pipeline/` (28), `Backtesting/` (24), `Sentiment/` (21).

## Database

**PostgreSQL unico provider.** `ApplicationDbContext` estende `IdentityDbContext<ApplicationUser>` e
dichiara **30 `DbSet`** — [ApplicationDbContext.cs](../../ProcioneMGR/Data/ApplicationDbContext.cs).

Gruppi di entità:

| Gruppo | Entità |
|---|---|
| Mercato | `OhlcvData`, `TrackedSeries`, `AltDataPoint`, `SentimentMetricPoint` |
| Credenziali | `ExchangeCredential`, `ExchangeCredentialCiphertext`, `AiCredential` |
| Ricerca | `SavedStrategy`, `SavedMlModel`, `SavedFactor`, `RegimeModel`, `FactorIcWindow` |
| Trading | `Order`, `OpenPosition`, `TradeRecord`, `TradingEngineState`, `TradingAuditLog`, `LaneQuarantine`, `ExecutionJob`, `ProtectiveExitShadow` |
| Ensemble | `EnsembleState`, `EnsembleRebalanceHistory` |
| Pipeline | `PipelineConfiguration`, `PipelineRun`, `PipelineArtifact`, `VettingCampaign` |
| Experiment tracking | `ExperimentRun`, `ExperimentArtifact` |
| Monitoraggio | `DriftCheckResult`, `HostHeartbeat`, `LlmUsageRecord`, `OrchestratorDecision` |
| UI | `UserPageConfig` |

**Dettaglio notevole:** `ExchangeCredentialCiphertext` mappa *la stessa tabella* di
`ExchangeCredential` ma espone il **ciphertext grezzo** senza value converter, "per i percorsi che
decifrano riga per riga e devono sopravvivere a una riga cifrata con una master key diversa"
([ApplicationDbContext.cs:25-30](../../ProcioneMGR/Data/ApplicationDbContext.cs#L25)). È la
soluzione al bug B2 sulle credenziali. Sola lettura, keyless.

**Le migrazioni non si applicano all'avvio** (pattern migrate-on-deploy). `DbInitializer` crea solo i
ruoli Identity. Chi salta `dotnet ef database update` si ritrova un database senza tabelle.

## Pattern principali

| Pattern | Dove | Note |
|---|---|---|
| **Keyed DI per corsia** | `AddTradingLanes` in [TradingServiceCollectionExtensions.cs](../../ProcioneMGR/Services/Trading/TradingServiceCollectionExtensions.cs) | ogni corsia ha motore, ensemble e stato indipendenti sullo stesso DB |
| **CQRS / Mediator** | un solo `IMediator` globale, non keyed | il routing per corsia avviene **per dato**, non per istanza ([Program.cs:415](../../ProcioneMGR/Program.cs#L415)) |
| **Hosted services** | ~25 `AddHostedService` | worker di sync, drift, promozione, pipeline, notifiche |
| **Pipeline a stage** | `Services/Pipeline/Stages/` | stage transient risolti nello scope del run; catalogo ed engine singleton |
| **Page service** | `TradingPageService`, `MlLabService`, `BacktestPageService`, `OptimizationPageService`, `PipelinePageService`, `EnsemblePageService` | orchestrazione estratta dai `.razor` per renderla testabile (P1-5) |
| **Value converter cifrante** | `EncryptedStringConverter` | i segreti sono cifrati AES-256-GCM prima di toccare il DB |
| **Fail-closed sulla sicurezza** | `SafetyChecker.Evaluate` statico e puro | invocato dentro `TradingEngine`, non iniettabile né aggirabile |

## Moduli principali

I dieci file di dominio più grandi, come proxy della complessità concentrata:

| File | KB |
|---|---|
| `Services/Trading/TradingEngine.cs` | 87,8 |
| `Program.cs` | 48,6 |
| `Services/ML/MlLabService.cs` | 41,8 |
| `Services/Exchanges/BitgetClient.cs` | 41,7 |
| `Services/Pipeline/Stages/ModelStages.cs` | 41,0 |
| `Services/Optimization/OptimizationEngine.cs` | 34,6 |
| `Services/Backtesting/BacktestEngine.cs` | 33,7 |
| `Services/Exchanges/BinanceClient.cs` | 32,3 |
| `Services/Pipeline/PipelineModels.cs` | 29,3 |
| `Data/ApplicationDbContext.cs` | 29,0 |

## Comunicazione tra componenti

1. **UI ↔ dominio** — chiamata diretta a servizi DI. Nessun hop di rete, nessuna serializzazione.
2. **Guscio ↔ core caldo** — gRPC su HTTP/2 h2c verso `localhost:18092` (trading) e
   `localhost:18080` (ingestion), esposti da port-forward `kubectl`.
3. **Autenticazione fra servizi** — segreto condiviso via interceptor:
   [SharedSecretClientInterceptor.cs](../../ProcioneMGR.Contracts/Grpc/SharedSecretClientInterceptor.cs)
   lato client, [SharedSecretAuthInterceptor.cs](../../ProcioneMGR.Trading/SharedSecretAuthInterceptor.cs)
   lato server.
4. **Contratti** — 5 file `.proto`: `common`, `events`, `ingestion`, `ml`, `trading`.

## State management

Non esiste uno store globale in stile Redux. Lo stato è distribuito su tre livelli:

- **Stato di componente** — campi dei `.razor`, vita = circuito Blazor dell'utente.
- **Stato di sessione utente persistito** — `UserPageConfig`: preset con nome + ultima
  configurazione usata per pagina, su DB.
- **Stato di dominio** — `TradingEngineState`, `EnsembleState`, `PipelineRun` su PostgreSQL. È
  questo che sopravvive ai riavvii e che rende il guscio ricostruibile.

Servizi singleton fanno da cache in memoria (`MetricsCollector`, cache dei fattori condivisa tra
training e inferenza), ma la fonte di verità resta il database.

## Routing

Routing Blazor via `@page`. **66 route** dichiarate; l'inventario completo con protezioni è in
[05_UI_PAGES_AND_ROUTES.md](05_UI_PAGES_AND_ROUTES.md).

- 404: `app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true)`
  ([Program.cs:614](../../ProcioneMGR/Program.cs#L614)) — verificato funzionante.
- `/health`: minimal API anonima per liveness/readiness Kubernetes
  ([Program.cs:640](../../ProcioneMGR/Program.cs#L640)).

## Autenticazione e autorizzazione

- **ASP.NET Core Identity** con `AddIdentityCore<ApplicationUser>`
  ([Program.cs:574](../../ProcioneMGR/Program.cs#L574)), pagine scaffolded in
  `Components/Account/`, incluso supporto **passkey/WebAuthn** e 2FA.
- **Ruoli applicativi:** `Admin`, `Manager`, `User` — [AppRoles.cs](../../ProcioneMGR/Data/AppRoles.cs).
  Creati all'avvio da `DbInitializer`.
- **Autorizzazione per-pagina** con `@attribute [Authorize(Roles = …)]`. Tre livelli di fatto:
  - `[Authorize]` semplice: `/backtest`, `/dashboard`, `/market-analysis`, `/settings/exchanges`
  - `Admin + Manager`: la maggioranza delle pagine operative, incluso `/trading`
  - `Admin` soltanto: `/admin/users`, `/admin/backup`, `/admin/autonomy`, `/admin/protections`

> ⚠️ **Non esiste un fallback policy globale.** In [Program.cs](../../ProcioneMGR/Program.cs) non
> compare alcuna `AddAuthorization(...)` con `FallbackPolicy`/`RequireAuthenticatedUser`: l'unica
> occorrenza della parola è lo `using` a riga 2. La protezione dipende quindi **interamente** dal
> fatto che ogni pagina ricordi il proprio attributo.
>
> **Stato attuale: pulito.** Ho verificato una per una le pagine con `@page` in
> `Components/Pages/`: le uniche senza `[Authorize]` sono `Home.razor`, `Error.razor`,
> `NotFound.razor` — tutte volutamente pubbliche. E il test empirico su 33 route (sezione
> [07](07_BROWSER_CHECK_REPORT.md)) mostra **302 verso il login su tutte** quelle protette.
> Il rischio è latente, non attuale: una pagina nuova che dimentichi l'attributo nasce pubblica
> senza che nulla lo segnali. Vedi [09 — R4](09_RISKS_AND_TECH_DEBT.md#r4).

## Middleware

Ordine della pipeline HTTP, da [Program.cs:603-674](../../ProcioneMGR/Program.cs#L603):

1. `UseStatusCodePagesWithReExecute("/not-found")`
2. Redirect HTTPS — **disattivabile via config**, perché dentro il cluster il pod UI parla solo HTTP
3. `UseAntiforgery()`
4. `MapStaticAssets()`
5. `MapRazorComponents<App>()` + `MapAdditionalIdentityEndpoints()`
6. `MapGet("/health")` anonimo

## Configurazione ambiente

| File | Ruolo |
|---|---|
| `appsettings.json` | **gitignorato**, contiene i segreti veri; è la config viva |
| `appsettings.json.example` | template versionato, 26,7 KB, ricco di commenti esplicativi |
| `appsettings.Development.json` | override di sviluppo |
| `appsettings.Production.json` | override di produzione |

Variabili d'ambiente rilevanti: `PROCIONE_MGR_MASTER_KEY` (ha priorità su appsettings),
`ANTHROPIC_API_KEY`, `ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_URLS`. Elenco completo senza valori in
[06_API_AND_INTEGRATIONS.md](06_API_AND_INTEGRATIONS.md#variabili-dambiente).

**Fail-fast in Production:** l'app rifiuta di partire con la master key placeholder del template
([Program.cs:590](../../ProcioneMGR/Program.cs#L590)); in Development è permessa, e in quel caso il
trading Live resta comunque bloccato per costruzione.

## Osservabilità

- `MetricsCollector` singleton + hosted service: alimenta `/metrics` senza backend esterno.
- Export OpenTelemetry **opt-in** (`Observability:Enabled`, default OFF): senza il flag il meter
  emette a vuoto, costo ~0 ([Program.cs:372-375](../../ProcioneMGR/Program.cs#L372)).
- `HostHeartbeat`: una riga per processo (`shell` / `engine`) — è così che il guscio sa se il core è
  vivo.
