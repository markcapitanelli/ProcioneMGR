# 04 — Runtime e flusso dei dati

> Questa sezione non è dedotta dal codice soltanto: l'app è stata **avviata davvero** durante
> l'audit (2026-08-04, `ASPNETCORE_ENVIRONMENT=Production`, porta 5199) e le righe citate vengono
> dal suo log reale.

---

## Cosa succede all'avvio

### 1. Prima ancora della DI

[Program.cs:24](../../ProcioneMGR/Program.cs#L24) attiva il *Npgsql legacy timestamp behavior*, che
permette a `timestamp without time zone` di accettare `DateTime` di qualunque `Kind`. Va impostato
prima di toccare il provider.

[Program.cs:34](../../ProcioneMGR/Program.cs#L34) risolve un caso specifico: gli asset statici
quando si gira **da sorgente** con `ASPNETCORE_ENVIRONMENT=Production` (cioè esattamente come fa
`run-postgres.ps1`). Sull'output pubblicato — le immagini Docker — il percorso è diverso.

### 2. Composizione

Registrazione di: Razor Components, Identity + authentication, DataProtection con keyring
persistito, `IEncryptionService` (singleton — la chiave si legge una volta), `MasterKeyProbe`,
`IDbContextFactory<ApplicationDbContext>`, client exchange, e poi decine di servizi di dominio.

Le **corsie di trading** vengono composte da `AddTradingLanes`
([Program.cs:407](../../ProcioneMGR/Program.cs#L407)): è lì che il toggle `Trading:UseRemoteTrading`
commuta fra motore locale e client gRPC. Ogni corsia è **keyed** per id, con motore, ensemble e
stato indipendenti sullo stesso database.

### 3. Fail-fast di sicurezza

[Program.cs:590](../../ProcioneMGR/Program.cs#L590): in Production l'app **non parte** con la master
key placeholder del template. In Development parte, ma il trading Live resta bloccato per
costruzione.

### 4. Pipeline HTTP

`UseStatusCodePagesWithReExecute("/not-found")` → redirect HTTPS (disattivabile: nel cluster il pod
UI parla solo HTTP) → `UseAntiforgery()` → `MapStaticAssets()` → `MapRazorComponents<App>()` →
`MapAdditionalIdentityEndpoints()` → `MapGet("/health")`.

### 5. `DbInitializer`

Crea i ruoli applicativi `Admin`/`Manager`/`User`. **Non applica le migrazioni** — è dichiarato
esplicitamente nel codice. Viene saltato sotto i tool di design-time (`dotnet ef`), per non tentare
una connessione mentre si generano le migrazioni.

### 6. Hosted services

Al primo tick partono i worker. Ecco quelli **osservati davvero** nel log di avvio, con la cadenza
che hanno dichiarato:

| Worker | Cadenza | Stato osservato |
|---|---|---|
| `SeriesFreshnessWatchWorker` | 15 min | attivo |
| `SentimentSyncWorker` | metriche 30 min, news 60 min | `Enabled=True` |
| `FactorDriftWorker` | 12 h | `Enabled=True` |
| `FeatureDriftWorker` | 6 h | **`Enabled=False`** |
| `EnsembleRebalanceWorker` | 6 h | attivo — **una istanza per corsia** (8 righe identiche nel log) |
| `PromotionWorker` | 6 h | `auto-promozione=True` |
| `RegimeRetrainingWorker` | 7 giorni | `Enabled=True` |
| `RegimeChangeTriggerWorker` | 30 min, cooldown 6 h | attivo |
| `PipelineSchedulerWorker` | 5 min | `ri-applica automatica=True` |
| `CampaignPlannerWorker` | 1 min | `enabled=True` |
| `LlmSupervisorWorker` | 5 min | `Enabled=True`, modello `meta/llama-3.3-70b-instruct`, chiave presente |
| `LiquidationSyncWorker`, `DailyDigestWorker`, `LlmUsageFlushWorker`, `MetricsCollector` | varie | registrati |

Il supervisore AI gira quindi su **NVIDIA con Llama 3.3 70B**, non su Anthropic — coerente con la
retrocessione di Anthropic dopo l'esaurimento del credito.

Entro un minuto dall'avvio il sistema ha iniziato lavoro reale da solo:

```
Campagna 2: Run avviato (config 17 'Caccia 4h universo largo (34 serie)', trigger Event).
```

## Come viene caricata la UI

1. Il browser richiede una route → il server rende l'HTML iniziale (SSR).
2. Il client apre un **WebSocket** su `/_blazor` — verificato:
   `WebSocket connected to ws://localhost:5199/_blazor?id=…`.
3. Da lì in poi gli eventi (click, input) viaggiano sul socket e il server rimanda **diff di DOM**.

Conseguenze pratiche:
- **Nessun fetch di dati lato client.** Il "data fetching" è una chiamata a un servizio C# dentro
  `OnInitializedAsync`.
- Se il circuito cade, Blazor mostra la sua UI di riconnessione (gli elementi
  "Rejoining the server…", "The session has been paused by the server." sono presenti nel DOM di
  ogni pagina, nascosti).
- Latenza e stato sono legati al server: una sessione persa è una pagina da ricaricare.

## Come vengono fetchati i dati

Tre percorsi distinti:

**A. Dati storici → da PostgreSQL, in-process**
```
Pagina .razor → PageService → DbContextFactory → PostgreSQL
```
Si usa `IDbContextFactory` (non il DbContext scoped) per i servizi a vita lunga e i componenti
interattivi, così ogni operazione ha un context a vita breve.

**B. Dati di mercato freschi → dagli exchange**
```
IngestionService → BinanceClient / BitgetClient → REST exchange → upsert idempotente su OhlcvData
```
Con `UseRemoteIngestion=true` questo giro avviene **nel servizio in-cluster**, non nel guscio: mai
due scrittori sulla stessa serie.

**C. Stato di trading → dal core caldo via gRPC**
```
Trading.razor → IMediator → GetLaneStatusQuery → RemoteTradingEngineClient → gRPC :18092
```

## Passaggio di stato tra componenti

- **Dentro una pagina:** campi del componente + `StateHasChanged`.
- **Fra pagine:** l'handoff avviene per **query string / parametri di route**, più
  `UserPageConfig` su DB per i preset per-utente.
- **Fra processi:** il database è il canale. `HostHeartbeat` (una riga per processo, `shell` /
  `engine`) è il modo in cui il guscio sa se il core è vivo.

## Flussi principali

### Flusso 1 — Ricerca: dall'idea alla strategia applicabile
```
/backtest (parametri) → BacktestPageService → BacktestEngine → OhlcvData
   → report (Profit Factor, Kelly, Montecarlo)
   → /optimization → OptimizationEngine (walk-forward) → DeflatedSharpeRatio
   → gate PBO → /registry (se sopravvive)
```

### Flusso 2 — Pipeline autonoma
```
PipelineSchedulerWorker (Cronos, 5 min)
   → PipelineEngine → stage da PipelineStageCatalog (Data → Analysis → Model → Decision)
   → RunApplyEvaluator confronta il nuovo ensemble col corrente (hysteresis)
   → PipelineSupervisor (LLM) può porre VETO
   → PipelineApplier schiera sulle corsie in Paper
```
Scrive **solo configurazione**: non avvia trading, non passa in Live.

### Flusso 3 — Esecuzione di un ordine
```
TradingWorker (tick per corsia)
   → strategia produce segnale
   → SignalOrderBuilder → SafetyChecker.Evaluate  ← FAIL-CLOSED
   → PositionOpener → BracketOrderManager (SL/TP)
   → exchange (Paper: simulato / Testnet / Live: in coda se RequireManualConfirmationForLive)
   → FillSanityCheck → TradingPersistence → Order / TradeRecord / TradingAuditLog
```

### Flusso 4 — Promozione di corsia
```
PromotionWorker (6h) → PromotionEvaluator (mai "Live")
   → LanePromoter (eccezione se gli si chiede Live)
   → stop → restart della corsia in modalità nuova
   → notifica
```

## Side effects rilevanti

Il processo, appena avviato, **agisce da solo**: ri-applica ensemble, promuove corsie
Paper→Testnet, lancia campagne di vaglio, interroga un LLM esterno a pagamento, scrive su DB e
manda notifiche Telegram. Non è un'app che aspetta l'utente.

Non è un difetto — è il progetto — ma va saputo prima di lanciarla "per dare un'occhiata".

## Job, worker, WebSocket, cron, cache, code

| Meccanismo | Presente | Dove |
|---|---|---|
| **Background worker** | sì, ~25 | `AddHostedService` in Program.cs |
| **Cron** | sì | Cronos, `PipelineSchedulerWorker` |
| **WebSocket in ingresso** | sì | `/_blazor` (framework) |
| **WebSocket in uscita** | sì | feed real-time R1 verso l'exchange, factory condivisa con `LiquidationSyncWorker` |
| **Cache** | sì | cache dei fattori condivisa fra training e inferenza; `PipelineCandleCache`; `MetricsCollector` in memoria |
| **Queue** | no broker esterno | la "coda" degli ordini Live è una tabella con conferma manuale |
| **Lease / lock** | sì | `LaneExecutionLease` — un solo scrittore per corsia |

## Stato degradato osservato durante l'audit

Il black-out ha lasciato il cluster kind con l'API server irraggiungibile. L'app è partita comunque,
e il degrado si è manifestato così:

```
warn: ProcioneMGR.Services.Trading.RemoteEngineConfigStore[0]
      Grpc.Core.RpcException: Status(StatusCode="Unavailable", …)
      SocketException (10061): Rifiuto persistente del computer di destinazione.

warn: ProcioneMGR.Services.Trading.LaneCountCoherenceProbe[0]
      Coerenza corsie non verificabile: Motore non raggiungibile (di solito il port-forward 18092
      è chiuso, non il motore fermo: vedi scripts/ensure-trading-portforward.ps1)
```

Due cose meritano di essere notate, ed entrambe sono **a favore** del progetto:

1. Il guscio **non è morto** né ha avviato un motore locale di ripiego. Ha continuato a servire la
   UI con l'ultimo stato noto.
2. Il messaggio d'errore **distingue le due ipotesi** (port-forward chiuso vs motore fermo) e indica
   lo script che risolve. È diagnostica scritta da chi ha già pagato quell'errore.

E la pagina `/trading` lo dice all'utente in chiaro, invece di mostrare numeri vecchi come se fossero
attuali — vedi [07_BROWSER_CHECK_REPORT.md](07_BROWSER_CHECK_REPORT.md).
