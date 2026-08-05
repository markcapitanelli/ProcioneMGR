# 08 — Piano di test

---

## Cosa esiste già

`ProcioneMGR.Tests/` — **259 file**, ~1999 metodi annotati `[Fact]`/`[Theory]`.

> **DA VERIFICARE:** il [README.md](../../README.md) dichiara "988 test". Non ho eseguito la suite
> in questo audit (serve Docker attivo). Il numero vero si ottiene con
> `dotnet test --list-tests`. Le due cifre non coincidono e il README è verosimilmente stale.

Distribuzione approssimativa per area (dai nomi dei file):

| Area | File |
|---|---|
| Trading / Safety | 37 |
| ML | 22 |
| Backtesting | 11 |
| Pipeline | 11 |
| UI (bUnit) | 11 |
| Sentiment | 9 |
| Exchange / Ingestion | 9 |
| AI / LLM | 6 |
| Validation | 5 |
| Portfolio | 3 |
| Altro / trasversali | ~129 |

Tecnologie: **xUnit** ovunque; **Testcontainers** (PostgreSQL effimero) in 4 file di integrazione;
**bUnit** in 5 file per i componenti Blazor.

I file di test più corposi indicano dove è concentrato lo sforzo di verifica:
`FactorDriftMonitorTests` (32 KB), `LaneInvariantWatchdogTests` (30 KB), `PipelineTests` (28,8 KB),
`AiMultiProviderTests` (28,6 KB), `AuditBlazorUiTests` (27,2 KB), `CampaignPlannerTests` (27,1 KB).

## Come si eseguono

```bash
dotnet test
```

Richiede **Docker in esecuzione**. I test di logica pura girano anche senza.

```bash
dotnet test --filter "FullyQualifiedName~SafetyChecker"
```

---

## Smoke test per `localhost:5199`

Il minimo indispensabile dopo ogni riavvio. Automatizzato in
[`playwright-smoke.mjs`](playwright-smoke.mjs); in versione manuale:

| # | Verifica | Atteso |
|---|---|---|
| S1 | `curl /health` | `200 {"status":"ok"}` |
| S2 | `GET /` anonimo | 200, landing con Login/Registrati |
| S3 | `GET /trading` anonimo | **302** → `/Account/Login?ReturnUrl=%2Ftrading` |
| S4 | `GET /rotta-inesistente` | 404 |
| S5 | Login → `/` | KPI popolati, nessun errore in console |
| S6 | `/trading` autenticato | corsie elencate; se il core è giù, **banner di dati stantii visibile** |
| S7 | `/metrics` | tessere presenti, empty state esplicito |
| S8 | Viewport 375px su `/` | nessuno scroll orizzontale |

Lo stato S6 col banner **è un test a pieno titolo**, non un ripiego: verifica che l'app non menta
quando non sa.

---

## Test manuali consigliati

### Sicurezza (priorità massima — è ciò che protegge il denaro)

| # | Scenario | Atteso |
|---|---|---|
| M1 | Avviare in Production con `Security:MasterKey` = placeholder del template | l'app **non parte** (fail-fast) |
| M2 | Tentare di portare una corsia a Live dalla UI | rifiutato; `LanePromoter` solleva eccezione |
| M3 | Ordine Live con `RequireManualConfirmationForLive=true` | resta in coda, richiede `ConfirmOrder` |
| M4 | Salvare una credenziale exchange | Secret mai rimostrato; API Key mascherata |
| M5 | Inserire una riga cifrata con master key diversa | badge "reinserire le credenziali", **la pagina non si abbatte** |
| M6 | Superare `MaxDailyLossPercent` in Paper | `SafetyChecker` rifiuta i nuovi ordini |
| M7 | Capitale non positivo | ordine rifiutato |

### Degradazione

| # | Scenario | Atteso |
|---|---|---|
| M8 | Spegnere il core di trading con `/trading` aperta | banner di dati stantii; comandi che falliscono in modo esplicito, non silenzioso |
| M9 | Fermare l'app con una pagina aperta | UI Blazor di riconnessione, poi "Failed to rejoin" |
| M10 | Togliere la chiave del provider AI | il layer degrada ad "approva" senza errori |
| M11 | Rendere irraggiungibile una fonte AltData | isolata e saltata; le altre proseguono |
| M12 | PostgreSQL giù all'avvio | errore chiaro, non uno stack trace muto |

### Dati

| # | Scenario | Atteso |
|---|---|---|
| M13 | Sync della stessa serie due volte | upsert idempotente, nessun duplicato |
| M14 | Backfill con buco temporale | buco rilevato e segnalato |
| M15 | Avviare due scrittori sulla stessa serie (guscio + cluster) | impedito dai toggle; da verificare che sia davvero impossibile |

---

## Test automatici consigliati

### Unit — da aggiungere

| # | Oggetto | Perché |
|---|---|---|
| A1 | `SafetyChecker` con **property-based testing** (FsCheck) su size/esposizione/leva | oggi ci sono scenari puntuali; un generatore casuale coprirebbe i bordi che nessuno immagina |
| A2 | `PromotionEvaluator`: fuzzing sull'invariante "non restituisce mai Live" | esiste già del fuzzing anti-Live; estenderlo a stati incoerenti |
| A3 | Round-trip di `EncryptedStringConverter` con chiavi diverse | il bug B2 nasceva lì |
| A4 | `DecimalValueMapper` / `TradingContractMapper` round-trip dominio ↔ gRPC | perdita di precisione sui decimal è un rischio silenzioso sui soldi |
| A5 | `LaneExecutionLease` sotto contesa | il lease è ciò che impedisce due scrittori |

### Integrazione — da aggiungere

| # | Oggetto | Perché |
|---|---|---|
| A6 | `RemoteTradingEngineClient` contro un server gRPC finto che risponde `Unavailable`, timeout, e risposte malformate | oggi il caso `Unavailable` è verificato solo dal vivo, per caso |
| A7 | `SharedSecretAuthInterceptor`: chiamata con segreto sbagliato/assente | è l'unica autenticazione fra guscio e core |
| A8 | Migrazioni EF applicate da zero su Postgres vuoto | il pattern migrate-on-deploy rende questo passaggio critico e non testato all'avvio |
| A9 | `AltDataSyncService` con fonte che risponde 403/404/timeout/HTML-invece-di-RSS | due fonti sono rotte in produzione e nessuno se n'era accorto |

### E2E — da aggiungere

| # | Scenario |
|---|---|
| A10 | `playwright-smoke.mjs` in CI contro l'app avviata in container |
| A11 | Percorso completo: login → backtest → salva strategia → applica a corsia Paper → verifica ordine |
| A12 | Il workflow [e2e-kind.yml](../../.github/workflows/e2e-kind.yml) esiste già: verificare che copra il bring-up completo e i 4 prerequisiti noti (restore, DLL migrazioni, master key a design-time, nomi chiavi dei Secret) |

---

## Casi critici da verificare per primi

In ordine di danno potenziale:

1. **Nessun percorso di codice arriva a Live senza conferma umana.** Cinque barriere dichiarate
   (`PromotionEvaluator`, `PromotionWorker`, `LanePromoter`, `TradingEngine`, `SafetyChecker`):
   servono test che le attacchino *tutte insieme*, non una per volta.
2. **Le credenziali non finiscono mai in chiaro** — né in log, né in eccezioni, né nella UI.
3. **Il segreto gRPC condiviso è verificato davvero** dal lato server.
4. **Non esistono due scrittori sulla stessa serie OHLCV** né due motori vivi.
5. **La precisione decimale sopravvive** al giro dominio → protobuf → dominio.

---

## Scenario di regressione

Da eseguire prima di ogni merge che tocchi `Services/Trading/`, `Services/Security/` o `Program.cs`:

```bash
dotnet test --filter "FullyQualifiedName~Safety|FullyQualifiedName~Promotion|FullyQualifiedName~Lane"
```

Poi, a mano:

1. Avvio in Production → l'app parte, i worker si registrano, nessun errore in log oltre a quelli
   noti (gRPC se il cluster è giù).
2. `/trading` → 8 corsie, tutte Paper, nessuna promozione inattesa.
3. `/metrics` → contatori a zero dopo il riavvio, poi crescenti.
4. Smoke Playwright → 28/28 redirect corretti.
5. `/admin/protections` → i limiti mostrati coincidono con quelli in configurazione.

> ✏️ **Aggiornamento dopo il giro completo del 2026-08-04.** Sul punto 5 avevo scritto che andava
> verificato se `/admin/protections` mostrasse i valori del motore o quelli del guscio.
> **Verificato: mostra quelli del motore**, e lo dichiara da sola in cima alla pagina — *«Questi
> valori sono letti da `procionemgr-trading` via gRPC e sono quelli che sta applicando adesso»*.
> Il dubbio è chiuso.

Da aggiungere alla regressione, dopo i difetti trovati nel secondo passaggio:

6. **`SignalOrderBuilder` con `SymbolFilters` a zero in Testnet**: l'ordine non deve essere costruito
   (vedi [R15](09_RISKS_AND_TECH_DEBT.md#r15)).
7. **Conteggio ordini rifiutati per anti-spam** dopo una sessione: se cresce quanto i Filled, c'è
   una duplicazione di segnale a monte ([R19](09_RISKS_AND_TECH_DEBT.md)).
8. **`/admin/backup`**: eseguire un backup e verificare che il file compaia ([R16](09_RISKS_AND_TECH_DEBT.md#r16)).
