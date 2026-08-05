# ProcioneMGR — memoria completa del progetto

> Documento unico e autosufficiente, pensato per essere caricato su NotebookLM e usato come contesto
> da Claude Code. Non richiede di aver letto altro.
> Fotografia del **4 agosto 2026**, prodotta da un audit read-only del codice e da un'ispezione
> dell'applicazione in esecuzione su `http://localhost:5199`.

---

# 1. Overview

**ProcioneMGR** è una piattaforma personale di **ricerca quantitativa e trading algoritmico su
criptovalute**, scritta in **.NET 10 / Blazor Server**, con **PostgreSQL** come unico database.
Ha un solo utente: il suo autore.

Copre l'intero ciclo di vita di una strategia: ingestione dei dati di mercato → ricerca e backtest →
validazione anti-overfitting → esecuzione in Paper, Testnet o Live.

## I due valori che la definiscono

**Onestà statistica.** Ogni strategia passa da validazione fuori campione (walk-forward,
Purged/Combinatorial Cross-Validation) e da gate anti-overfitting (Deflated Sharpe Ratio,
Probability of Backtest Overfitting). Quando il risultato non è significativo, la piattaforma lo
scrive a chiare lettere invece di nasconderlo.

**Sicurezza per costruzione.** Il percorso verso il denaro reale è sbarrato da cinque livelli
indipendenti di codice. Nessuna metrica, per quanto eccellente, promuove automaticamente in Live.
Il passaggio Testnet → Live è **sempre e solo** una decisione umana esplicita.

Principio fondante dichiarato: **Safety > Solidità > Velocità.**

## Lo stato onesto del progetto

La piattaforma è matura **come strumento di misura**, ma **non contiene una strategia che, misurata
onestamente, guadagni**: 445.280 combinazioni provate, zero significative al Deflated Sharpe, zero
sopravvissute all'holdout.

Un esperimento di controllo — un edge sintetico "piantato" nei dati — dimostra che gli strumenti
funzionano: quando un edge c'è, lo trovano. I risultati negativi dicono quindi qualcosa **sui dati**,
non sugli strumenti. L'unica classe con edge positivo misurato è il **carry delta-neutro** (5–12%
netto annuo), coerente con la letteratura.

## Numeri reali dell'istanza in esecuzione (2026-08-04)

| Indicatore | Valore |
|---|---|
| Serie di mercato tracciate | 221 |
| Candele OHLCV in archivio | ≈ 12.181.001 |
| Strategie salvate | 17 |
| Corsie di trading attive | 8, **tutte in Paper** |
| Fattori segnalati in deriva | 32 |
| Route dichiarate | 66 |
| File C# nell'app principale | 414 (+ 89 `.razor`) |
| File di test | 259 |

---

# 2. Architettura

## Forma generale

Nato come **monolite modulare** Blazor Server, il progetto è stato scomposto in **microservizi
opzionali** dietro feature-toggle. L'app resta eseguibile come singolo processo, ma i motori pesanti
possono girare separati e parlare **gRPC**.

L'assetto reale oggi è **"guscio freddo + core caldo"**:

```
┌──────────────────────────────────────────────────────┐
│  ProcioneMGR — Blazor Server, il GUSCIO FREDDO       │
│  UI + orchestrazione, :5199                          │
└──────────────────────────────────────────────────────┘
      │ gRPC
      ├── ProcioneMGR.Ingestion   :18080   sync OHLCV
      ├── ProcioneMGR.Ml                   inferenza ML
      └── ProcioneMGR.Trading     :18092   ← IL CORE CALDO
                       │
                 PostgreSQL
```

Il guscio **comanda** ma non esegue: con `Trading:UseRemoteTrading=true` non registra motore, worker,
feed real-time né carry. Il core continua a operare anche se il guscio è spento — è il senso stesso
della separazione.

## I tre toggle

| Toggle | Effetto quando `true` |
|---|---|
| `MarketData:UseRemoteIngestion` | il monolite non avvia il worker di sync locale |
| `Trading:UseRemoteTrading` | il motore vive nel cluster; il guscio comanda via gRPC |
| `Ml:Enabled` | inferenza delegata al servizio remoto |

Vincolo assoluto scritto in configurazione: **«MAI entrambi i motori vivi»**. Mai due scrittori sulla
stessa serie di dati.

## Progetti della soluzione

| Progetto | Ruolo |
|---|---|
| `ProcioneMGR` | app Blazor: UI, dominio, EF Core, orchestrazione |
| `ProcioneMGR.Contracts` | 5 file `.proto` condivisi |
| `ProcioneMGR.Ingestion` | microservizio di ingestione OHLCV (API REST) |
| `ProcioneMGR.Ml` | microservizio di inferenza (gRPC) |
| `ProcioneMGR.Trading` | microservizio motore di trading (gRPC) |
| `ProcioneMGR.Migrations.Postgres` | migrazioni EF Core |
| `ProcioneMGR.Tests` | suite di test |
| `tools/` | 5 CLI (solo 2 dentro la soluzione) |

## Stack

.NET 10 · Blazor Server · PostgreSQL + EF Core 10 (Npgsql) · Microsoft.ML + LightGBM ·
MathNet.Numerics · gRPC · Cronos · OpenTelemetry (opt-in) · ASP.NET Core Identity ·
xUnit + Testcontainers + bUnit · Kubernetes (kind) + ArgoCD.

## Pattern architetturali

- **Keyed DI per corsia** — ogni lane ha motore, ensemble e stato indipendenti sullo stesso DB.
- **CQRS con un solo Mediator globale** — il routing per corsia avviene *per dato*, non per istanza.
- **~25 hosted services** — sync, drift, promozione, pipeline, notifiche, supervisione AI.
- **Pipeline a stage** — stage transient risolti nello scope del run.
- **Page service** — l'orchestrazione delle pagine pesanti estratta dal markup, per renderla
  testabile.
- **Value converter cifrante** — i segreti sono cifrati AES-256-GCM prima di toccare il database.
- **Fail-closed sulla sicurezza** — `SafetyChecker.Evaluate` è statico e puro: non iniettabile,
  quindi non aggirabile.

## Database

`ApplicationDbContext` estende `IdentityDbContext` e dichiara **30 DbSet**: mercato (`OhlcvData`,
`TrackedSeries`, `SentimentMetricPoint`, `AltDataPoint`), credenziali (`ExchangeCredential`,
`ExchangeCredentialCiphertext`, `AiCredential`), ricerca (`SavedStrategy`, `SavedMlModel`,
`SavedFactor`, `RegimeModel`, `FactorIcWindow`), trading (`Order`, `OpenPosition`, `TradeRecord`,
`TradingEngineState`, `TradingAuditLog`, `LaneQuarantine`, `ExecutionJob`, `ProtectiveExitShadow`),
ensemble, pipeline, experiment tracking, monitoraggio.

**Le migrazioni non si applicano all'avvio** (pattern migrate-on-deploy): all'avvio si creano solo i
ruoli Identity.

## Autenticazione e autorizzazione

ASP.NET Core Identity con passkey/WebAuthn e 2FA. Tre ruoli: **Admin**, **Manager**, **User**.
L'autorizzazione è **per-pagina** via `@attribute [Authorize(Roles = …)]`; **non esiste una fallback
policy globale**.

---

# 3. Mappa del progetto

```
ProcioneMGR/
├── Program.cs               674 righe — tutta la composizione
├── Components/              89 .razor
│   ├── Pages/               34 pagine applicative
│   ├── Account/             32 pagine Identity
│   ├── Layout/  Shared/
├── Services/                384 .cs in 38 cartelle
│   ├── Trading/       60    il modulo più grande
│   ├── ML/            37
│   ├── Pipeline/      28
│   ├── Backtesting/   24    14 strategie
│   ├── Sentiment/     21
│   ├── Alpha/         14    catalogo Alpha158
│   ├── Exchanges/     12    Binance, Bitget
│   ├── Llm/           12    5 provider AI
│   ├── Regime/        12
│   ├── Validation/    10    il gate anti-overfitting
│   └── …
└── Data/                    21 .cs — entità + DbContext
```

## I file che contano di più

| File | Dimensione | Perché |
|---|---|---|
| `Services/Trading/TradingEngine.cs` | 87,8 KB | il motore; il file più grande |
| `Services/Trading/SafetyChecker.cs` | — | la barriera finale su ogni ordine |
| `Program.cs` | 48,6 KB | tutta la composizione, molto commentata |
| `Components/Pages/Admin/Autonomy.razor` | 107,6 KB | la pagina più grande, senza page service |
| `Services/Validation/` | 10 file | DSR, PBO, CPCV, permutation, gemello sintetico |

## Le cinque barriere verso il denaro reale

1. **`PromotionEvaluator`** — la modalità suggerita non è **mai** `Live`.
2. **`PromotionWorker`** — agisce solo su transizioni Paper↔Testnet.
3. **`LanePromoter`** — solleva eccezione se gli si chiede di passare una corsia a Live.
4. **`TradingEngine`** — blocca l'avvio Live con la master key placeholder.
5. **`SafetyChecker`** — su **ogni** ordine: limiti fail-closed su size, esposizione, perdita
   giornaliera, drawdown, posizioni aperte, intervallo minimo, leva massima.

---

# 4. Rotte

**66 route.** 34 applicative + 32 Identity.

| Sezione | Route |
|---|---|
| Overview | `/` (pubblica), `/dashboard` |
| Dati e monitoraggio | `/market/watchlist`, `/market-analysis`, `/market/bars`, `/metrics` |
| Ricerca e sviluppo | `/backtest`, `/optimization`, `/feature-selection`, `/ml`, `/ensemble`, `/portfolio`, `/registry`, `/experiments` |
| Strumenti avanzati | `/discovery`, `/pipeline`, `/alpha-mining`, `/regimes`, `/pairs-trading`, `/volatility`, `/sentiment`, `/strategies`, `/execution` |
| Trading | `/trading`, `/bot`, `/campaign` |
| Configurazione | `/settings/exchanges`, `/admin/ai-supervisor`, `/admin/autonomy`, `/admin/users`, `/admin/backup`, `/admin/protections` |
| Sistema | `/health` (anonimo), `/not-found`, `/Error` |

Livelli di protezione: `[Authorize]` semplice (dati propri o consultazione), **Admin+Manager**
(ricerca pesante e trading), **Admin** soltanto (utenti, backup, autonomia, protezioni).

**Verifica sul campo:** 28 route protette su 28 rispondono `302 → /Account/Login?ReturnUrl=…`.
Nessuna perdita.

---

# 5. API e integrazioni

## Non esiste una API REST interna

Essendo Blazor Server, le pagine chiamano direttamente servizi C# in-process. Chi cerca i controller
non li trova perché non ci sono.

## Superfici esposte

| Endpoint | Auth |
|---|---|
| `GET /health` | anonimo — liveness Kubernetes |
| `/_blazor` | WebSocket del framework |
| `POST /sync/{id}` (microservizio Ingestion) | interno |

## gRPC

**`TradingCommandService`** — 15 rpc: `GetLaneStatus`, `GetOpenPositions`, `GetPerformance`,
`StartLane`, `StopLane`, `EmergencyStop`, `ClosePosition`, `CloseAllPositions`,
`SetStopLossTakeProfit`, `ConfirmOrder`, `RejectOrder`, `GetEngineConfig`, `SetEngineConfig`,
`SendTestNotification`, `GetNotificationChannelStatus`.

**`InferenceService`** — `PredictSignal`.

Autenticazione fra servizi: **segreto condiviso** via interceptor.

## Servizi esterni

**Exchange:** Binance Spot e Futures (+ rispettive testnet), Bitget, dump storici da
`data.binance.vision`.
⚠️ Binance Futures è **inutilizzabile da IT/UE dal 2026-07-01** per MiCA: Bitget è l'unico exchange
a leva disponibile.

**Sentiment e dati alternativi:** Fear & Greed, posizionamento Binance Futures, FXSSI retail ratio,
5 feed RSS.
🔴 Due fonti sono **morte**: ForexFactory risponde **403**, FXStreet-CentralBanks **404**.

**Provider AI:** NVIDIA, Google Gemini, Groq, HuggingFace, Anthropic — scelti *per chiamata*, con
chiavi cifrate su database. Attivo oggi: **NVIDIA con `meta/llama-3.3-70b-instruct`**.

**Notifiche:** Telegram.

## Segreti

`PROCIONE_MGR_MASTER_KEY`, `ANTHROPIC_API_KEY`, `ConnectionStrings:PostgresConnection`,
`Security:MasterKey`, `Trading:GrpcSharedSecret` — tutti `[REDACTED]` in questa documentazione.

---

# 6. Interfaccia utente

Blazor Server con render mode `InteractiveServer`: nessuna SPA, nessun bundle JS applicativo. Lo
stato vive sul server, il browser tiene un WebSocket.

**Navigazione:** sidebar a sezioni, breadcrumb, **ricerca globale con `Ctrl K`**, pannello "Guida"
collassabile in cima a quasi ogni pagina.

**Responsive:** verificato a 375 px — sidebar in hamburger, KPI riflowati, nessuno scroll
orizzontale.

## Il comportamento che vale la pena citare

Durante l'audit il core di trading era irraggiungibile. La pagina `/trading` ha mostrato:

> ⚠️ **DATI TRADING NON AGGIORNATI da 0s:** il servizio di trading non risponde (Unavailable).
> Quanto vedi qui sotto è l'ultimo stato noto, **non** quello attuale — posizioni e PnL potrebbero
> essere cambiati. I comandi falliranno finché il servizio non torna.

È l'esatto opposto del "controllo che rassicura a prescindere dalla realtà" — una classe di difetto
che questo progetto ha già incontrato e corretto in passato.

---

# 7. Problemi noti

## 🔴 Alta priorità

**1. Segreti committati per errore, rotazione obbligatoria.** Un file di configurazione con segreti
reali è finito tracciato in git ed è arrivato su un ramo pubblicato: il `.gitignore` intercettava il
nome esatto ma non le sue varianti con suffisso. Fra i segreti coinvolti c'è la **chiave di
cifratura a riposo**, quella che protegge le credenziali API degli exchange sul database.
Vanno **ruotati tutti**: ciò che è stato pubblico va considerato compromesso. Non è teorico — nel
database ci sono **tre credenziali exchange reali** cifrate proprio con quella chiave.

> 🔒 Percorso del file, commit di origine ed elenco puntuale delle chiavi sono **fuori dal
> repository pubblico** finché la rotazione non è completata. Il proprietario ha il dettaglio.

**1-bis. La quantità dell'ordine può partire non arrotondata verso l'exchange.** Difetto con
evidenza di produzione: nello storico ordini ci sono ordini rifiutati da Binance con
`-1100 Illegal characters found in parameter 'quantity'`. Catena: `qty = notional / price` produce
fino a 28-29 cifre decimali; `SymbolFilters.RoundQuantity` **non arrotonda** se `StepSize == 0` e
`IsTradable` **approva tutto** se i minimi sono zero. Un `SymbolFilters` restituito ma non popolato
trasforma il guard in un no-op silenzioso. `RoundPrice` ha la stessa forma e lo stesso problema
latente.

**1-ter. Nessun backup del database esiste.** `/admin/backup` riporta «Nessun backup presente».
Il database contiene ≈12,18 milioni di candele, 196 run di esperimenti, decine di modelli e le
credenziali cifrate di tre account exchange. La funzione esiste ed è a un clic: non è mai stata usata.

**2. `run-postgres.ps1` muore se il cluster kind è giù.** `$ErrorActionPreference = "Stop"` più le
chiamate a `kubectl` fanno morire lo script prima di `dotnet run` — annullando i rami `else` scritti
apposta per quel caso. Succede dopo ogni riavvio di Windows o Docker, cioè proprio quando serve.

**3. Il conteggio dei test nel README non corrisponde.** Dichiara 988; il conteggio statico ne
trova ~1999.

## 🟡 Media priorità

- Nessuna fallback policy di autorizzazione: una pagina nuova senza `[Authorize]` nasce pubblica.
  Oggi lo stato è pulito su tutte e 34.
- Due fonti di dati alternativi morte in silenzio (403 e 404).
- File molto grandi senza page service: `Autonomy.razor` (107,6 KB), `Sentiment.razor` (55 KB).
- `AnthropicLlmClient.cs` contiene cinque provider: il nome mente.
- Tre CLI (`FuturesVerify`, `PlatformExpand`, `SpotVerify`) fuori dalla soluzione: nessuno si
  accorge se si rompono.
- Warning EF Core: `First`/`FirstOrDefault` senza `OrderBy` — non deterministico su PostgreSQL.

## 🟢 Bassa priorità

Pagine Identity non tradotte, errori WebAuthn in console sul login, `<select>` Exchange troppo
stretto, corsie "non configurata" senza indicazioni, nessun linter configurato.

## Cosa invece è fatto bene

Cifratura AES-256-GCM a riposo con decifratura per-riga resiliente; fail-fast sulla master key
placeholder; cinque barriere indipendenti verso Live con `SafetyChecker` statico e non aggirabile;
layer AI strutturalmente incapace di eseguire ordini; autorizzazione verificata 28/28; credenziali
isolate per utente; nessuna duplicazione strutturale (la composizione delle corsie e la catena
"valuta e applica" sono condivise, non copiate).

---

# 8. Piano di test

**Suite esistente:** 259 file, ~1999 metodi. xUnit ovunque, Testcontainers per PostgreSQL effimero,
bUnit per i componenti Blazor. `dotnet test` richiede Docker.

**Smoke test** su `localhost:5199`: `/health` → 200; `/` anonimo → 200; `/trading` anonimo → 302 al
login; rotta inesistente → 404; `/metrics` con empty state espliciti; viewport 375 px senza scroll
orizzontale. Automatizzato in `docs/audit/playwright-smoke.mjs`.

**Casi critici, in ordine di danno potenziale:** nessun percorso arriva a Live senza conferma umana;
le credenziali non finiscono mai in chiaro; il segreto gRPC è verificato davvero lato server; non
esistono due scrittori sulla stessa serie né due motori vivi; la precisione decimale sopravvive al
giro dominio → protobuf → dominio.

**Test consigliati da aggiungere:** property-based testing su `SafetyChecker`; fuzzing
sull'invariante "mai Live"; round-trip dei mapper gRPC; `RemoteTradingEngineClient` contro un server
finto che risponde `Unavailable`; `SharedSecretAuthInterceptor` con segreto sbagliato; migrazioni da
zero su Postgres vuoto; `AltDataSyncService` con fonti che rispondono 403/404/HTML.

---

# 9. Indicazioni per Claude Code

## Comandi

```
dotnet run --project ProcioneMGR --no-launch-profile -c Release   # :5199
dotnet test                                                        # richiede Docker
dotnet ef database update --project ProcioneMGR.Migrations.Postgres --startup-project ProcioneMGR
```

## Convenzioni

- **Lingua italiana** per commenti, log e messaggi UI; nomi di tipi e membri in inglese.
- I commenti spiegano il **perché**, spesso con data e ragione della scelta. Aggiornali quando
  cambi la decisione che documentano.
- `IDbContextFactory`, non il DbContext scoped, nei servizi a vita lunga.
- Page service per l'orchestrazione delle pagine pesanti: la logica non sta nel markup.

## Regole da non violare

1. **Non rendere `SafetyChecker` iniettabile.** È statico apposta: non mockabile, non aggirabile.
2. **Un solo scrittore.** Mai due motori vivi, mai due scrittori sulla stessa serie.
3. **Fail-closed sulla sicurezza, fail-open sulla diagnostica.** Sono due politiche diverse e
   deliberate: non uniformarle.
4. **Degradare dicendolo.** Mai mostrare un valore vecchio come se fosse attuale.
5. **Nessun servizio di esecuzione dentro `Services/Llm/`.** Il layer AI può porre veto, mai forzare.
6. **`DriveProtectiveExits = false` e `RegimeRouting:DriveDecisions = false` non sono sviste:** sono
   risultati di misure. Non "correggerli".

## Errori comuni

Saltare `dotnet ef database update` (database senza tabelle); dimenticare `@attribute [Authorize]`
su una pagina nuova (pagina pubblica); lanciare `run-postgres.ps1` col cluster giù; credere che
l'`appsettings.json` committato descriva la realtà (è gitignorato: quello versionato dice 3 corsie,
l'istanza ne ha 8); inventare i nomi delle chiavi dei Secret Kubernetes invece di leggerli dagli
script.

## Quando misuri qualcosa

Dichiara sempre **trade/mese e durata mediana della posizione** — il proprietario lavora
intraday/swing breve. Nessun risultato senza gate anti-overfitting. Non randomizzare su asset
correlati per stimare la significatività: fabbrica falsa significatività, è un errore già pagato.

---

# 10. Glossario

| Termine | Significato in questo progetto |
|---|---|
| **Corsia / lane** | unità di trading isolata: motore, ensemble e stato indipendenti sullo stesso DB. Oggi 8, tutte in Paper |
| **Guscio freddo** | il processo Blazor: UI e orchestrazione, comanda ma non esegue |
| **Core caldo** | il servizio `ProcioneMGR.Trading` nel cluster: esegue davvero, sopravvive allo spegnimento del guscio |
| **Paper / Testnet / Live** | simulato / ordini reali con soldi finti / soldi veri. Testnet→Live solo a mano |
| **DSR — Deflated Sharpe Ratio** | Sharpe corretto per il numero di tentativi: quanto dello Sharpe è fortuna da selezione |
| **PBO — Probability of Backtest Overfitting** | probabilità che il "migliore" trovato sia rumore |
| **Purged CV** | cross-validation che elimina il leakage dei rendimenti futuri sovrapposti (purge + embargo) |
| **CPCV — Combinatorial Purged CV** | molti percorsi out-of-sample dallo stesso storico; alimenta il PBO |
| **IC — Information Coefficient** | correlazione fra previsione e rendimento realizzato: la misura di "quanto vale" un fattore |
| **Alpha158** | catalogo di ~150 fattori causali in stile Qlib |
| **Gemello sintetico / null twin** | serie costruita senza edge: se la pipeline ci "trova" qualcosa, la pipeline è rotta |
| **Deriva / drift** | un fattore che smette di funzionare: l'IC scende sotto soglia |
| **PSI / KS / Page-Hinkley** | test statistici per rilevare drift nelle feature |
| **Carry delta-neutro** | incasso del funding rate con esposizione direzionale nulla. **L'unica classe con edge positivo misurato** |
| **Bracket** | coppia stop-loss / take-profit associata a una posizione |
| **Uscite protettive** | chiusure automatiche su SL/TP. Misurato: uscire al tocco è **peggio** che a barra chiusa |
| **Quarantena di corsia** | sospensione automatica per violazione di invarianti contabili |
| **Ensemble** | insieme di strategie pesate per Sharpe rolling, con vincoli min/max |
| **HRP / ERC / Ledoit-Wolf** | ottimizzatori di portafoglio: gerarchico, risk parity esatto, stimatore di covarianza shrinkato |
| **Regime** | classificazione del mercato (trend/laterale) via K-means. Oggi **osserva ma non decide** |
| **Queen Bee / flotta** | orchestratore deterministico delle corsie. Default OFF |
| **Fascia grigia** | zona intermedia di promozione, deploy graduale |
| **Advisory-only** | il layer AI può esprimere pareri e porre veto, mai eseguire |
| **Fail-closed / fail-open** | in dubbio blocca (sicurezza) / in dubbio lascia passare (diagnostica) |

---

# 11. FAQ operative

**Come avvio l'app?**
`dotnet run --project ProcioneMGR --no-launch-profile -c Release`, poi `http://localhost:5199`.
Lo script ufficiale `./scripts/run-postgres.ps1` aggiunge i port-forward, ma oggi si rompe se il
cluster kind è giù.

**L'app parte ma le tabelle non esistono.**
Manca `dotnet ef database update`. L'app **non** applica le migrazioni all'avvio: crea solo i ruoli
Identity.

**`/trading` mostra un banner giallo di dati non aggiornati.**
Il core caldo non risponde: quasi sempre è il port-forward 18092 chiuso, non il motore fermo. Vedi
`scripts/ensure-trading-portforward.ps1`.

**Perché i test falliscono?**
Docker non è in esecuzione: i test d'integrazione avviano un PostgreSQL effimero via Testcontainers.

**Dove sono i segreti?**
In `appsettings.json`, che è **gitignorato**, e nella variabile `PROCIONE_MGR_MASTER_KEY` (ha
priorità). Le chiavi dei provider AI vivono cifrate sulla tabella `AiCredentials`, non su file.

**Posso far promuovere una corsia in Live automaticamente?**
No, ed è deliberato. Cinque barriere indipendenti lo impediscono. Testnet→Live è sempre una
decisione umana.

**Il file di configurazione dice 3 corsie ma ne vedo 8.**
Il file committato non è quello in uso: `appsettings.json` è gitignorato. I file versionati non
descrivono la configurazione reale.

**Quale provider AI è attivo?**
NVIDIA con `meta/llama-3.3-70b-instruct`. Anthropic è stata retrocessa dopo l'esaurimento del
credito. Il provider si sceglie per chiamata, con hot-reload da `/admin/autonomy`.

**Perché tanti risultati negativi?**
Perché sono veri. Il DSR su pochi mesi di storico è insuperabile per pura aritmetica (con Sharpe 1,0
servirebbero ~6,2 anni). Il forward test in Paper è l'unico giudice attendibile.

---

# 12. Decisioni architetturali

| # | Decisione | Motivo |
|---|---|---|
| D1 | **Blazor Server** invece di SPA | un solo utente, latenza locale irrilevante; niente API interna da mantenere né duplicazione di modelli fra C# e TypeScript |
| D2 | **PostgreSQL unico provider**, SQLite rimosso | i dual-provider costano test doppi e bug sottili; migrazione completata a luglio 2026 |
| D3 | **Microservizi dietro toggle**, non estrazione forzata | l'app resta avviabile come processo unico: il debug locale non richiede un cluster |
| D4 | **Corsie fisse e in numero limitato** invece di orchestratore dinamico | stato su `EnsembleState` invece che su DbContext separati; molto meno da sbagliare |
| D5 | **Un solo `IMediator` globale**, non keyed per corsia | il routing avviene per dato: meno istanze, meno stato |
| D6 | **Migrazioni non applicate all'avvio** | pattern migrate-on-deploy: un'app che migra da sola è un'app che può corrompere il DB in produzione |
| D7 | **`SafetyChecker` statico e puro** | non iniettabile ⇒ non sostituibile ⇒ non aggirabile per configurazione. È l'ultima barriera e non deve dipendere dalla DI |
| D8 | **Layer AI advisory-only**, senza servizi di esecuzione iniettati | l'incapacità di eseguire è strutturale, non una regola che qualcuno deve ricordare |
| D9 | **Cifratura a riposo con value converter EF** | i segreti non possono raggiungere il DB in chiaro nemmeno per errore |
| D10 | **`ExchangeCredentialCiphertext`**: seconda entità sulla stessa tabella | una riga cifrata con master key diversa abbatteva l'intera pagina; ora si decifra riga per riga |
| D11 | **`DriveProtectiveExits = false`** | misurato: uscire al tocco è peggio che a barra chiusa, 24/24 configurazioni |
| D12 | **`RegimeRouting:DriveDecisions = false`** | 5–37 trade per regime: scrivere regole su questi numeri sarebbe curve-fitting. Il router osserva e accumula |
| D13 | **Osservabilità opt-in** | senza il flag il meter emette a vuoto, costo ~0 |
| D14 | **Notifiche e autonomia default OFF** | ogni automatismo si accende esplicitamente |

---

# 13. Parti incerte, da approfondire

Cose che questo audit **non** ha potuto stabilire:

1. **Il numero vero dei test.** README dice 988, il conteggio statico ~1999. Serve
   `dotnet test --list-tests` con Docker attivo.
2. **Quale query genera il warning EF 10103** (`First` senza `OrderBy`). Il log non la nomina.
3. **Se esiste un `IEmailSender` reale** configurato per Identity, o se è il no-op di default.
4. **Se i tre CLI fuori dalla soluzione** (`FuturesVerify`, `PlatformExpand`, `SpotVerify`) siano
   ancora usati.
5. **Se esistano dipendenze vulnerabili.** `dotnet list package --vulnerable` non è stato eseguito.
6. **Dipendenze circolari a livello di namespace** dentro `Services/`: a livello di progetto non ce
   ne sono, ma non ho eseguito un'analisi automatica più fine.
7. **Se esista una convenzione di lint** non documentata: non ho trovato configurazione di analisi.
8. **L'origine degli ordini duplicati per candela** — se sia un ensemble a due gambe o una doppia
   valutazione della stessa barra nel `TradingWorker`.
9. **Se il flusso notizie sia davvero popolato**: quasi tutte le fonti verdi riportano *(0 elementi)*
   all'ultima sync. Probabile deduplica, ma da confermare.

### Chiuse nel secondo passaggio del 2026-08-04

- ~~Perché l'API server del cluster kind non risponde~~ → il proxy socat `kind-apiproxy` inoltrava a
  `172.18.0.3` mentre Docker aveva riassegnato al control-plane `172.18.0.2`. Ricreare il container
  con l'IP corrente risolve. **Va aggiunto alla procedura di ripristino: l'IP cambia a ogni riavvio.**
- ~~Il comportamento nominale di `/trading`~~ → verificato: 8 corsie, corsia 0 `RUNNING` su AAVE/USDT
  1d. E il guscio **si riconnette da solo** quando il core torna, senza riavvio.
- ~~Gli stati di loading~~ → catturato su `/market/watchlist` (`Caricamento…` prima delle 221 serie).
- ~~Se `/admin/protections` mostri i valori del motore o del guscio~~ → mostra quelli del **motore
  remoto**, e lo dichiara in cima alla pagina.
- ~~Se le fonti AltData rotte falliscano in silenzio~~ → **no**: `/sentiment` le marca in rosso con
  l'errore esatto e l'ora.

---

# 14. Stato osservato dal vivo (2026-08-04 sera, cluster acceso)

| Area | Stato |
|---|---|
| Corsie | 8, tutte **Paper**; 3 e 7 libere; corsia 0 `RUNNING` su AAVE/USDT 1d, 0 trade dal 27/07 |
| Capitale corsia 0 | 10.000,00 total / available, 0,00 used |
| Watchlist | 221 serie, sync in-cluster funzionante (ultimo giro ~19:22 UTC) |
| Esperimenti | 196 run con hash di riproducibilità; stessi hash per stesse config = determinismo verificabile |
| Campagne | 1 attiva, 2 configurazioni, **0 sopravvissuti** su 10 run |
| Registry | **nessun Champion attivo** su alcun simbolo; decine di modelli fermi in Staging |
| Regimi | K-means attivo su AAVE/USDT 1h, K=4, silhouette 0,400, 27.140 candele |
| Sentiment | Fear & Greed **25 (Extreme Fear)**; 9 fonti verdi, **2 rosse** (ForexFactory 403, FXStreet-CentralBanks 404) |
| Supervisore AI | Nvidia `meta/llama-3.3-70b-instruct`; advisory ok **31**, errori **19**; 4 chiavi cifrate a DB |
| Flotta (Queen Bee) | **ATTIVA in DRY-RUN**, ultimo tick 21:14, 0 azioni |
| Esecuzione a fette | **SPENTA** (master switch) |
| Credenziali exchange | 3 reali, API key mascherate, nessun allarme master key |
| Utenti | 2, entrambi Admin |
| Backup database | **nessuno** |

Il quadro è coerente con la tesi del progetto: la macchina gira, misura, e dice di no.
