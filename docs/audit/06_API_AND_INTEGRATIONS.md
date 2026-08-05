# 06 — API e integrazioni

---

## Premessa: non esiste una API REST interna per la UI

L'app è **Blazor Server**. Le pagine non fanno `fetch`: chiamano direttamente servizi C#
in-process. Chi cerca i "controller" non li trova perché non ci sono. Le uniche superfici HTTP/RPC
sono quelle elencate qui sotto.

---

## Endpoint HTTP esposti dall'app principale

| Metodo | Path | Auth | Risposta | Definizione |
|---|---|---|---|---|
| GET | `/health` | **anonimo** | `200 {"status":"ok"}` | [Program.cs:640](../../ProcioneMGR/Program.cs#L640) |
| GET | `/*` (66 route Blazor) | per-pagina | HTML + WebSocket | `@page` |
| — | `/_blazor` | sessione | WebSocket (SignalR) | framework |
| — | `/Account/*` | mista | Identity endpoints | `MapAdditionalIdentityEndpoints()` |

`/health` è pensato per liveness/readiness Kubernetes ed è **volutamente anonimo**. Non espone dati.

## Endpoint del microservizio Ingestion

| Metodo | Path | Note |
|---|---|---|
| POST | `/sync/{trackedSeriesId:int}` | sincronizza una serie tracciata |
| GET | `/health` | liveness |

[ProcioneMGR.Ingestion/Program.cs:42](../../ProcioneMGR.Ingestion/Program.cs#L42). Consumato dal
guscio via `MarketData:RemoteIngestionUrl` (port-forward `localhost:18080`).

Nota: `ingestion.proto` contiene **solo message, nessun service** — dichiarato nel file stesso:
l'API di ingestion è REST, non gRPC.

## Contratti gRPC

### `TradingCommandService` — `trading.proto` (15 rpc)

Letture: `GetLaneStatus`, `GetOpenPositions`, `GetPerformance`, `GetEngineConfig`,
`GetNotificationChannelStatus`.

Comandi: `StartLane`, `StopLane`, **`EmergencyStop`**, `ClosePosition`, `CloseAllPositions`,
`SetStopLossTakeProfit`, `ConfirmOrder`, `RejectOrder`, `SetEngineConfig`, `SendTestNotification`.

`ConfirmOrder`/`RejectOrder` sono il meccanismo di `RequireManualConfirmationForLive`: un ordine
Live resta in coda finché un umano non lo approva.

### `InferenceService` — `ml.proto` (1 rpc)

`PredictSignal(PredictSignalRequest) → PredictSignalResponse`.

### Autenticazione fra servizi

Segreto condiviso via interceptor:
[SharedSecretClientInterceptor.cs](../../ProcioneMGR.Contracts/Grpc/SharedSecretClientInterceptor.cs)
(client) e [SharedSecretAuthInterceptor.cs](../../ProcioneMGR.Trading/SharedSecretAuthInterceptor.cs)
(server). Il valore vive in `Trading:GrpcSharedSecret`.

> 🔴 Questo segreto è fra quelli **esposti pubblicamente su GitHub** — vedi
> [09 — R1](09_RISKS_AND_TECH_DEBT.md#r1).

---

## Endpoint esterni chiamati

### Exchange

| Servizio | Base URL | File |
|---|---|---|
| Binance Spot | `https://api.binance.com` | [BinanceClient.cs:134](../../ProcioneMGR/Services/Exchanges/BinanceClient.cs#L134) |
| Binance Spot Testnet | `https://testnet.binance.vision` | idem:135 |
| Binance Futures | `https://fapi.binance.com` | idem:138 |
| Binance Futures Testnet | `https://testnet.binancefuture.com` | idem:139 |
| Bitget | `https://api.bitget.com` | [BitgetClient.cs:158](../../ProcioneMGR/Services/Exchanges/BitgetClient.cs#L158) |
| Ora server (deriva orologio) | `…/api/v3/time`, `…/api/v2/public/time` | [ExchangeClock.cs:96](../../ProcioneMGR/Services/Exchanges/ExchangeClock.cs#L96) |
| Dump storici | `https://data.binance.vision/data` | [BinanceDumpDownloader.cs:37](../../ProcioneMGR/Services/Microstructure/BinanceDumpDownloader.cs#L37) |

Autenticazione: HMAC con API key/secret dell'utente, decifrati a runtime. Rate limit gestito da
`ExchangeRateLimitHandler`.

> ⚠️ **Binance Futures è inutilizzabile da IT/UE dal 2026-07-01** (MiCA). Bitget resta l'unico
> exchange a leva disponibile. Il codice ha ancora gli endpoint Futures di Binance.

### Sentiment e dati alternativi

| Fonte | URL | Stato osservato 2026-08-04 |
|---|---|---|
| Fear & Greed | `https://api.alternative.me/fng/` | ok |
| Binance Futures sentiment | `https://fapi.binance.com` | ok |
| FXSSI retail ratio | `https://c.fxssi.com/api/current-ratio` | ok |
| RSS CoinDesk | `https://www.coindesk.com/arc/outboundfeeds/rss` | ok (200) |
| RSS Cointelegraph | `https://cointelegraph.com/rss` | ok |
| RSS The Block | `https://www.theblock.co/rss.xml` | ok |
| RSS Decrypt | `https://decrypt.co/feed` | ok |
| RSS FXStreet | `https://www.fxstreet.com/rss` | ok |
| **FXStreet Central Banks** | `https://www.fxstreet.com/rss/news/central-banks` | 🔴 **404 Not Found** |
| **ForexFactory calendar** | `https://www.forexfactory.com/calendar` | 🔴 **403 Forbidden** |

Le due fonti rotte sono state osservate nel log reale:

```
warn: AltDataSyncService  AltData sync: fonte 'ForexFactory' non raggiungibile, salto.
      HttpRequestException: 403 (Forbidden).
warn: AltDataSyncService  AltData sync: fonte 'FXStreet-CentralBanks' non raggiungibile, salto.
      HttpRequestException: 404 (Not Found).
```

**Gestione errori: corretta.** `FetchSafeAsync`
([AltDataSyncService.cs:111](../../ProcioneMGR/Services/AltData/AltDataSyncService.cs#L111)) isola
ogni fonte: una che cade non abbatte le altre, viene loggata e saltata. Il problema non è la
robustezza, è che **due fonti su otto sono silenziosamente morte** e nessuno se ne accorge se non
legge i log. Vedi [09 — R5](09_RISKS_AND_TECH_DEBT.md#r5).

### Provider AI

| Provider | Endpoint | File |
|---|---|---|
| NVIDIA | `https://integrate.api.nvidia.com/v1` | [AnthropicLlmClient.cs:63](../../ProcioneMGR/Services/Llm/AnthropicLlmClient.cs#L63) |
| Google Gemini (OpenAI-compat) | `https://generativelanguage.googleapis.com/v1beta/openai` | idem:71 |
| Groq | `https://api.groq.com/openai/v1` | idem:76 |
| HuggingFace router | `https://router.huggingface.co/v1` | idem:81 |
| Anthropic | `https://api.anthropic.com/v1/models?limit=100` | idem:215 |

Il provider si sceglie **per chiamata** via `LlmClientResolver` (hot-reload da `/admin/autonomy`),
non al boot. Le chiavi vivono **cifrate sul DB** (`AiCredentials`), non su file.

**Provider effettivamente attivo al 2026-08-04**, dal log di avvio:
`LlmSupervisorWorker avviato (modello meta/llama-3.3-70b-instruct, …, chiave presente=True)` —
cioè **NVIDIA con Llama 3.3 70B**.

> Il nome del file `AnthropicLlmClient.cs` è ormai fuorviante: contiene gli endpoint di cinque
> provider. Vedi [09 — R7](09_RISKS_AND_TECH_DEBT.md#r7).

### Notifiche

| Servizio | Endpoint | File |
|---|---|---|
| Telegram | `https://api.telegram.org/bot{token}/sendMessage` | [TelegramNotifier.cs:49](../../ProcioneMGR/Services/Notifications/TelegramNotifier.cs#L49) |

---

## Errori gestiti

| Situazione | Comportamento verificato |
|---|---|
| Core di trading irraggiungibile | `RpcException Unavailable` catturata; UI mostra banner di dati stantii; i comandi falliscono in modo esplicito |
| Fonte AltData giù | isolata e saltata, il resto della sync procede |
| Riga di credenziali cifrata con master key diversa | `ExchangeCredentialReader` decifra riga per riga, flagga l'indecifrabile con badge "reinserire le credenziali" invece di abbattere la pagina |
| Chiave LLM assente | il layer degrada ad "approva" senza errori: le metriche decidono da sole |
| Master key placeholder in Production | **fail-fast**: l'app non parte |
| Rate limit exchange | `ExchangeRateLimitHandler` |
| Fill patologico dall'exchange | `FillSanityCheck` |

---

## Variabili d'ambiente

Nessun valore reale è riportato. Lette dal codice via `GetEnvironmentVariable`:

| Variabile | Ruolo | Valore |
|---|---|---|
| `PROCIONE_MGR_MASTER_KEY` | chiave AES-256 (base64, 32 byte). **Ha priorità su appsettings** | `[REDACTED]` |
| `ANTHROPIC_API_KEY` | chiave Anthropic; se assente il layer AI resta inattivo | `[REDACTED]` |
| `PROCIONE_MICROSTRUCTURE_CACHE` | percorso cache dei dump di microstruttura | percorso locale |
| `ASPNETCORE_ENVIRONMENT` | `Production` / `Development` | — |
| `ASPNETCORE_URLS` | default `http://localhost:5199` | — |

Chiavi di configurazione sensibili in `appsettings.json` (file **gitignorato**):

| Chiave | Contenuto | Valore |
|---|---|---|
| `ConnectionStrings:PostgresConnection` | host/db/utente/password | `[REDACTED]` |
| `Security:MasterKey` | chiave AES-256 | `[REDACTED]` |
| `Trading:GrpcSharedSecret` | segreto condiviso gRPC | `[REDACTED]` |
| `Notifications:ChatId` | destinatario Telegram | `[REDACTED]` |

Le chiavi dei provider AI **non** stanno in configurazione: vivono cifrate nella tabella
`AiCredentials`.

Secret Kubernetes: creati dagli script `scripts/k8s-postgres-secret.ps1`,
`scripts/k8s-trading-secret.ps1`, `scripts/k8s-ui-secret.ps1`. I nomi delle chiavi vanno letti da
quegli script, non inventati.

---

## Webhook, SDK, storage, email, pagamenti

| Categoria | Presente |
|---|---|
| Webhook in ingresso | **no** |
| SDK esterni | Anthropic; il resto sono client HTTP scritti in casa |
| Object storage | **no** — artefatti su DB e filesystem locale |
| Email | solo il canale Identity (conferma/reset); **DA VERIFICARE** se ci sia un `IEmailSender` reale configurato o il no-op di default |
| Pagamenti | **no** |
| Cache distribuita | **no** — cache in-process |
| Message broker | **no** |
