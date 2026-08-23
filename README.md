# ProcioneMGR

**Piattaforma di ricerca e trading algoritmico** per criptovalute, costruita in **.NET 10 / Blazor Server**. Copre l'intero ciclo di vita di una strategia — dall'ingestione dei dati di mercato fino all'esecuzione degli ordini — con un rigore metodologico anti-overfitting di stampo accademico (López de Prado, Jansen *ML4T*, Qlib di Microsoft) e un modello di sicurezza a più livelli che rende **fisicamente impossibile** operare con denaro reale senza intervento umano esplicito.

> ⚠️ **Disclaimer.** Software sperimentale per ricerca personale. **Non è consulenza finanziaria.** Il trading di criptovalute (a maggior ragione con leva/futures) comporta rischio concreto di perdita del capitale. L'esecuzione **Live è disabilitata di default** e protetta da molteplici barriere; qualunque risultato di backtest/ottimizzazione è soggetto a *selection bias* e non garantisce performance future. Usa a tuo rischio.

**Principio fondante:** *Safety > Solidità > Velocità.* Niente promozione automatica in Live; determinismo e anti-overfitting sono obbligatori, non opzionali.

---

## Cosa ha trovato finora, detto onestamente

*Stato al 2026-08-23. La versione lunga è in [`docs/STATO-DELLA-PIATTAFORMA.md`](docs/STATO-DELLA-PIATTAFORMA.md) (aggiornata al 2026-07-20) e la cronaca viva in [`docs/ROADMAP.md`](docs/ROADMAP.md).*

La piattaforma è matura come **strumento di misura**. Su una cosa è stata insistente e coerente:

| filone | esito |
|---|---|
| **direzionale-tecnico** (medie, oscillatori, breakout, e le loro combinazioni generate) | **una decina di no consecutivi**, l'ultimo su un mese di dati mai toccati. 445.280 combinazioni in una sola campagna: 6 oltre i gate, **0** significative al Deflated Sharpe, **0/6** sopravvissute all'holdout |
| **esperimento di controllo** con un edge sintetico piantato | la pipeline lo trova (DSR 1,00) — **gli strumenti funzionano**, quindi i no dicono qualcosa sui dati |
| **carry** sul funding dei perpetui | **unica classe con edge misurato positivo**: netto 5,5–11,9%/anno alle soglie storiche, riprodotto. Ma il solo tratto 2025-06 → oggi è **negativo** al netto (regime magro) |
| **fascia grigia → forward test Paper** | in corso. Candidati bocciati per sola finestra corta, schierati in Paper perché il forward test è l'unico giudice immune al multiple testing su campioni piccoli |

**Perché non consolida quasi mai:** su quattro mesi di dati il Deflated Sharpe è insuperabile per pura
aritmetica (uno Sharpe vero di 1,0 richiederebbe ~6,2 anni per distinguersi dal rumore). Non è un
gate troppo severo: è la finestra a essere corta. Per questo esiste il forward test Paper.

**Cosa NON troverai qui:** una strategia pronta da schierare, e un numero che dica quanto guadagnerai.
Ogni Sharpe accanto a una strategia salvata è quello della *selezione* — misura il passato con cui è
stata scelta.

---

## Indice

- [Cos'è](#cosè)
- [Il ciclo di vita di una strategia](#il-ciclo-di-vita-di-una-strategia)
- [Caratteristiche principali](#caratteristiche-principali)
- [Metodologia di ricerca (anti-overfitting)](#metodologia-di-ricerca-anti-overfitting)
- [Convenzioni di misura](#convenzioni-di-misura)
- [Modello di sicurezza](#modello-di-sicurezza)
- [Architettura](#architettura)
- [Stack tecnico](#stack-tecnico)
- [Mappa delle pagine](#mappa-delle-pagine)
- [Requisiti](#requisiti)
- [Setup e avvio](#setup-e-avvio)
- [Test](#test)
- [Infrastruttura e deployment](#infrastruttura-e-deployment)
- [Struttura del repository](#struttura-del-repository)
- [Documentazione](#documentazione)

---

## Cos'è

ProcioneMGR è un ambiente completo di **quant research** e **trading automatico** per un singolo operatore. A differenza di un semplice bot, integra in un'unica applicazione tutto ciò che serve per *scoprire, validare onestamente e mettere in produzione* strategie di trading, senza mai saltare i controlli che separano un edge reale da un artefatto statistico.

I due valori distintivi:

1. **Onestà statistica.** Ogni strategia passa da validazione *out-of-sample* (walk-forward, Purged/Combinatorial CV) e gate anti-overfitting (Deflated Sharpe Ratio, Probability of Backtest Overfitting). La piattaforma preferisce dire "questo edge non è significativo" piuttosto che illudere. E quando un numero è stantio, degradato o prodotto con una convenzione diversa, **lo dichiara** invece di mostrarlo come se fosse attuale.
2. **Sicurezza per costruzione.** Il percorso verso il denaro reale è sbarrato da 5 livelli indipendenti di codice: nessuna metrica, per quanto eccellente, promuove automaticamente in Live.

L'orizzonte di riferimento è **intraday / swing breve**: ogni misura si dichiara con i suoi
*trade al mese attesi* e la *durata mediana della posizione*, perché uno Sharpe senza il ritmo che
lo produce non è confrontabile con niente.

## Il ciclo di vita di una strategia

```
  Dati            Ricerca              Validazione            Esecuzione
 ┌──────┐   ┌───────────────┐   ┌────────────────────┐   ┌──────────────────┐
 │Ingest│──▶│ Backtest      │──▶│ Walk-forward OOS   │──▶│ Paper            │
 │OHLCV │   │ Optimization  │   │ Purged/CPCV        │   │  ↓ (osservazione)│
 │      │   │ Discovery     │   │ Deflated Sharpe    │   │ Testnet          │
 │      │   │ ML / Alpha158 │   │ PBO gate           │   │  ↓ (SOLO manuale)│
 └──────┘   └───────────────┘   └─────────┬──────────┘   │ Live ⛔ bloccato │
                                          │              └──────────────────┘
                                          │ bocciati per SOLA finestra corta
                                          ▼                        ▲
                                  ┌────────────────┐               │
                                  │ Fascia grigia  │───────────────┘
                                  │ → corsie flotta│  forward test Paper
                                  └────────────────┘  (mai un salto di gate)
```

Il passaggio **Paper → Testnet** può essere automatico (dopo settimane di osservazione reale e superamento delle soglie); il passaggio **Testnet → Live** è **sempre e solo** una decisione umana esplicita. L'unica automazione ammessa che tocchi una corsia Live è la **retrocessione** a Testnet, opt-in e in dry-run.

## Caratteristiche principali

### Dati e analisi
- **Ingestione OHLCV** da Binance e Bitget, con upsert idempotente e watchlist auto-aggiornata in background. Ordine di grandezza corrente: ~220 serie, ~12 milioni di candele.
- **Analisi statistica** dei dati storici, **barre a volume/dollaro** (vs barre a tempo) con confronto statistico.
- **Classificazione dei regimi di mercato** (trend/laterale) riaddestrabile, con encoding one-hot opzionale.
- **Sentiment** da fonti crypto e macro, con analisi di impatto storico misurata (non assunta) e tre scorer a confronto: lessico, provider AI, pilota ONNX locale.

> ⚠️ **MiCA.** Dal 1° luglio 2026 Binance non offre più derivati e leva nello Spazio Economico Europeo: su Binance resta il solo Spot. **Bitget è l'unico exchange a leva utilizzabile** da IT/UE.

### Ricerca e machine learning
- **Backtest** (`/backtest`) con report ricco: Profit Factor, Kestner ratio, **criterio di Kelly** (binario, continuo, empirico), analisi **Montecarlo**, e un **leverage advisor** che stima P(rovina) per livello di leva. Costi realistici di default: fee, slippage e — sui perpetui — la **serie storica dei funding** con il segno vero, non una costante.
- **Optimization** (`/optimization`) — Grid Search **o** Bayesian, sempre in modalità **walk-forward** con Deflated Sharpe sui risultati; in alternativa validazione **CPCV** con PBO e stabilità della selezione.
- **Alpha158** — catalogo di ~150 fattori causali in stile Qlib (KBAR, rolling su prezzo/volume, correlazioni), con invariante anti-look-ahead verificata sull'intero catalogo.
- **Modelli ML** (`/ml`) — Linear, Random Forest, LightGBM, MLP (rete C# pura), predittori *stacked*, con selezione feature per **Information Coefficient** (`/feature-selection`).
- **Discovery** (`/discovery`) — ricerca autonoma delle combinazioni strategia×coppia×timeframe più promettenti su tutto l'universo, con gate anti-overfitting, più una **modalità creativa** che genera specifiche mai codificate (combinazioni di segnali, trigger su eventi, meta-strategie condizionate dal regime).
- **Pipeline autonoma** (`/pipeline`) — automazione end-to-end da dati grezzi a strategia applicabile, schedulabile via cron. Un run schedulato non esegue **mai** in Live: viene saltato con un avviso, non declassato di nascosto.
- **Archivio candidati** (`/research`) — ogni candidato mai provato resta leggibile con le sue metriche, il motivo di scarto e la **provenienza** di ciascun numero. Serve a leggere la caccia invece di ripeterla.
- **Campaign planner** (`/campaign`) — che cosa fare *dopo* un run, deciso dalla piattaforma: rotazione delle configurazioni con backoff, e stop quando qualcosa è stato davvero schierato.
- **Genetic alpha mining** (`/alpha-mining`).

### Portafoglio ed esecuzione
- **Ottimizzazione di portafoglio** (`/portfolio`) — Mean-Variance (Max Sharpe / Min Variance), Risk Parity (**ERC** esatto), **HRP** (Hierarchical Risk Parity), con stimatore di covarianza **Ledoit-Wolf**.
- **Ensemble multi-corsia** (`/ensemble`) — corsie ("lane") keyed indipendenti e isolate, ognuna un ensemble di strategie pesato per Sharpe rolling con vincoli Min/Max.
- **Esecuzione a fette** (`/execution` per misurarla, `/trading` per usarla) — TWAP / VWAP / Iceberg / **Adaptive** (Almgren-Chriss closed-form) sulle *aperture* Testnet/Live, default-off. Le chiusure restano immediate: una protezione non si rateizza.
- **Pairs trading, volatilità (GARCH), sentiment** (`/pairs-trading`, `/volatility`, `/sentiment`).

### Automazione e supervisione
- **Trading engine** (`/trading`) per corsia — Paper / Testnet / Live con `SafetyChecker` su ogni ordine. Dopo un riavvio ogni corsia riprende stato, posizioni **e gambe attive**, in tutte e tre le modalità.
- **Modalità semplice** (`/bot`) — due scelte e un pulsante, sempre e solo in simulazione: capitale e profilo di rischio, il resto lo decide la piattaforma.
- **Auto-promozione** Paper → Testnet con *hysteresis* (mai Live), **monitor di decadimento** (realizzato vs atteso), **feature drift** (PSI / KS / Page-Hinkley) e ritiro delle corsie **in inedia** (che producono meno trade di quanti l'holdout ne prometteva).
- **Orchestratore di flotta** — governa le corsie oltre l'impronta storica: schiera la fascia grigia sui posti liberi, ritira i forward test perdenti con isteresi.
- **Scheduler** (Cronos) che ri-applica automaticamente l'ensemble migliore, dietro comparatore con isteresi e veto AI.
- **Supervisione AI advisory** (`/admin/ai-supervisor`) — layer **multi-provider** (Nvidia, Groq, Gemini, HuggingFace, Anthropic) con **failover automatico** e budget separato per provider; scrive pareri sui run e, in comitato, vota su quale candidato schierare quando c'è un pareggio da rompere. **Advisory-only**: non avvia trading, non bypassa il `SafetyChecker`, non esegue nulla — il suo potere massimo è il **veto**. Nessun servizio di esecuzione vive sotto `Services/Llm/`.
- **Osservabilità** (`/metrics`, `/dashboard`) — KPI di piattaforma + OpenTelemetry. Con il motore in un processo separato la pagina lo **dichiara**: i suoi contatori restano lì.

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
| **Benchmark passivo** | «Batte davvero il non-fare-niente nella stessa direzione?» — misura, non gate | `/research`, colonna *vs passivo* |
| **Criterio di Kelly** (empirico) | Sizing prudente robusto alle code grasse cripto | Risk sizing |
| **Forward test Paper** | L'unico giudice immune al multiple testing su finestre corte | Corsie di flotta |

Il messaggio è coerente: se il *Deflated Sharpe* dice "non significativo", la piattaforma lo scrive a chiare lettere invece di nasconderlo. **Non si abbassa una soglia per far passare un risultato**, e non si randomizza su asset correlati per stimare la significatività: fabbrica significatività falsa, è un errore già pagato.

## Convenzioni di misura

Due convenzioni vanno conosciute per leggere qualunque numero della piattaforma.

- **Sharpe a tasso privo di rischio zero** (dal 2026-08-22). Nella simulazione la cassa non rende
  nulla ed è investito solo `PositionSizePercent` del capitale: sottrarre il costo-opportunità del
  capitale *intero* era un doppio conteggio. La convenzione contabilmente corretta — accreditare il
  tasso a tutta l'equity e poi sottrarlo — dà **esattamente** lo stesso numero, quindi zero *è*
  quella convenzione. Il dazio del vecchio 2% valeva `rf/σ`, cioè una funzione dell'esposizione e
  non della qualità: mediana **0,545 punti di Sharpe** su 12.967 candidati d'archivio, e più severo
  proprio con le strategie selettive. Gli Sharpe prodotti prima di quella data **non si confrontano**
  con quelli dopo, e il codice si rifiuta di confrontarli.
- **Provenienza del walk-forward.** Per le famiglie generate (Composite, EventTrigger,
  RegimeConditional) i parametri sono già fissi nella spec: rieseguire il backtest su finestre
  mobili restituiva N volte lo stesso numero. Oggi la conferma segmenta la curva di equity in
  sotto-periodi della selezione, e ogni candidato **dichiara** da dove viene il suo numero.

Dettaglio in [`docs/audit/35_RISK_FREE_2026-08-22.md`](docs/audit/35_RISK_FREE_2026-08-22.md).

## Modello di sicurezza

Il sistema è progettato perché sia **impossibile per il codice** andare in Live senza intervento umano esplicito. Il confine è difeso su **5 livelli indipendenti**:

1. **`PromotionEvaluator`** — la modalità suggerita non è **mai** `Live`; le corsie Live non vengono nemmeno valutate.
2. **`PromotionWorker`** — agisce solo su transizioni Paper↔Testnet; una decisione incoerente viene loggata come errore, mai eseguita.
3. **`LanePromoter`** — solleva un'eccezione se richiesto di passare una corsia a Live.
4. **`TradingEngine`** — blocca l'avvio Live con la master key placeholder di sviluppo; il modello "Champion" del registry **non può** alimentare una corsia Live.
5. **`SafetyChecker`** (su ogni ordine) — limiti *fail-closed* su size posizione, esposizione totale, perdita giornaliera, drawdown, numero posizioni aperte, intervallo minimo tra ordini, leva massima; ordine Live rifiutato senza conferma manuale; capitale non positivo → ordine rifiutato. Resta **statico e puro**, non mockabile né iniettabile: è il punto.

Due politiche deliberatamente diverse: **fail-closed sulla sicurezza, fail-open sulla diagnostica**.
Un sync di dati accessori che fallisce non deve uccidere una caccia; una chiusura di posizione senza
credenziali viene invece **rifiutata**, perché chiudere solo nel database mentre l'esposizione resta
viva sull'exchange è peggio del non chiudere.

Inoltre: **credenziali exchange cifrate AES-256-GCM** a riposo (mai in chiaro sul DB), con un
**keyring** che permette di ruotare la master key ri-cifrando le righe invece di ricrearle; auth via
ASP.NET Core Identity; `RequireManualConfirmationForLive` che tiene ogni ordine Live in coda finché
l'operatore non lo approva a mano.

**Tre impostazioni che sembrano dimenticanze e non lo sono** — sono risultati di misure, e cambiarle
significa ribaltare una misura:

| Flag | Valore | Perché |
|---|---|---|
| `RealtimeFeed:DriveProtectiveExits` | `false` | Uscire al tocco del prezzo è peggio che a barra chiusa in **24 configurazioni su 24** (2026-07-28) |
| `RegimeRouting:DriveDecisions` | `false` | Un gate sul regime toglie operazioni, e togliere operazioni migliora quasi sempre lo storico su cui il regime è stato calcolato |
| CronJob `dbbackup-nightly` | sospeso | Con `emptyDir` i backup verrebbero creati e persi alla terminazione del pod. Il backup notturno vero gira sull'host |

## Architettura

Nato come **monolite modulare** Blazor Server, il progetto è stato progressivamente scomposto in **microservizi opzionali** estratti dietro feature-toggle: l'app resta eseguibile come singolo processo, ma i motori pesanti possono girare separati e comunicare via **gRPC**.

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

**Core caldo, guscio freddo.** Nell'assetto di esercizio il motore di trading gira nel cluster e
sopravvive al riavvio della UI, mentre il guscio locale è un client gRPC. Due conseguenze da tenere
a mente:

- **Il motore in cluster non si aggiorna da solo.** L'immagine si costruisce in locale
  (`scripts/build-images-local.ps1`), si importa nel nodo e si fissa nel `kustomization.yaml`:
  **il bump del tag *è* la promozione**. Un `dotnet build` sull'host non tocca il pod.
- **`/metrics` mostra i contatori del guscio**, non quelli del motore: la pagina lo dichiara.

**Progetti della soluzione:**

| Progetto | Ruolo |
|---|---|
| `ProcioneMGR` | App Blazor principale: UI, servizi di dominio, EF Core, orchestrazione |
| `ProcioneMGR.Contracts` | Definizioni gRPC (`.proto`) condivise |
| `ProcioneMGR.Ingestion` | Microservizio di ingestione OHLCV (toggle `MarketData:UseRemoteIngestion`) |
| `ProcioneMGR.Ml` | Microservizio di inferenza ML via gRPC (toggle `Ml:Enabled`) |
| `ProcioneMGR.Trading` | Microservizio del motore di trading (toggle `Trading:UseRemoteTrading`) |
| `ProcioneMGR.Migrations.Postgres` | Migrazioni EF Core per PostgreSQL |
| `ProcioneMGR.Tests` | Suite di test (**3.029 test**, unit + integrazione + bUnit) |

**Multi-corsia:** il trading è isolato in corsie indipendenti (`Trading:LaneCount`, **8** in esercizio, default 3, tetto 12), ciascuna con motore, ensemble e stato propri (keyed DI) sullo stesso database. Le prime tre sono l'*impronta storica* che la ri-applica della pipeline riscrive per indice; le restanti sono corsie di **flotta**, governate dall'orchestratore.

> ⚠️ **UN SOLO SCRITTORE.** Mai due motori vivi sulla stessa corsia, mai due scrittori sulla stessa serie OHLCV. Il lease per corsia è il guardrail di ultima istanza, non un permesso.

## Stack tecnico

| Area | Tecnologia |
|------|-----------|
| Runtime / UI | .NET 10, Blazor Server (InteractiveServer) |
| Database | PostgreSQL via EF Core 10 (Npgsql) — **unico provider** |
| ML | Microsoft.ML 5 + LightGBM, MathNet.Numerics, ONNX Runtime (pilota sentiment) |
| Comunicazione servizi | gRPC (HTTP/2, h2c) |
| AI supervisor | Multi-provider (Nvidia · Groq · Gemini · HuggingFace · Anthropic) con failover, advisory-only |
| Scheduling | Cronos |
| Osservabilità | OpenTelemetry (OTLP), stack LGTM-lite |
| Orchestrazione | Kubernetes (kind), GitOps con ArgoCD |
| Auth | ASP.NET Core Identity |
| Notifiche | Telegram (bot dedicato) |
| Test | xUnit, Testcontainers (PostgreSQL effimero), bUnit |

## Mappa delle pagine

33 pagine, ognuna con una **Guida** apribile in testa che spiega a cosa serve e come leggerne i numeri.

| Sezione | Pagine | Ruolo minimo |
|---|---|---|
| **Overview** | `/` (workflow guidato), `/dashboard` | User |
| **Dati & Monitoraggio** | `/market/watchlist`, `/market-analysis`, `/market/bars`, `/metrics` | Manager (`/market-analysis`: User) |
| **Ricerca & Sviluppo** | `/backtest`, `/optimization`, `/feature-selection`, `/ml`, `/ensemble`, `/portfolio`, `/registry`, `/experiments`, `/research` | Manager (`/backtest`: User) |
| **Strumenti Avanzati** | `/discovery`, `/pipeline`, `/campaign`, `/alpha-mining`, `/regimes`, `/pairs-trading`, `/volatility`, `/sentiment`, `/strategies`, `/execution` | Manager (`/strategies`: User) |
| **Trading** | `/trading` (Paper/Testnet/Live), `/bot` (modalità semplice, solo Paper) | Manager — soglie di sicurezza: Admin |
| **Configurazione** | `/settings/exchanges`, `/admin/ai-supervisor`, `/admin/autonomy`, `/admin/protections`, `/admin/users`, `/admin/backup` | Admin (`/settings/exchanges`: User · `/admin/ai-supervisor`: Manager) |

**Ogni chiave di configurazione nasce con il suo pannello.** È un mandato vincolante: niente
manopole amministrabili solo da `appsettings.json`, e i rischi si spiegano nel testo accanto alla
manopola, non in un documento a parte. Un documento per pagina in [`docs/pagine/`](docs/pagine/).

## Requisiti

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **PostgreSQL** (unico provider) + client `pg_dump`/`pg_restore` nel PATH (per `/admin/backup`)
- **Docker** — richiesto per eseguire i test (PostgreSQL effimero via Testcontainers) e per l'assetto Compose
- (Opzionale) una chiave per almeno un provider AI, inseribile da `/admin/ai-supervisor` — viene **cifrata a DB**. In alternativa la variabile d'ambiente del provider (`NVIDIA_API_KEY`, `ANTHROPIC_API_KEY`, …)

## Setup e avvio

La piattaforma supporta **due assetti**, e la regola che li governa è una sola:

> ⚠️ **UN SOLO SCRITTORE.** Mai i due assetti insieme sullo stesso database: sarebbero due motori
> sulle stesse corsie. Il compose usa di default un Postgres SUO (isolato, non pubblicato
> sull'host), quindi da solo non può violare la regola; il lease per corsia è il guardrail di
> ultima istanza, non un permesso. `bringup.ps1` e `watchdog.ps1` riconoscono da soli quale
> assetto è attivo.

### Assetto A — Docker Compose (portabile: qualunque dispositivo con Docker)

```bash
cp .env.example .env
# compila POSTGRES_PASSWORD e PROCIONE_MGR_MASTER_KEY (generatori nei commenti del file)
docker compose up -d
```

UI su `http://localhost:5199`, **migrazioni applicate da sole al primo avvio** (la DLL sta
nell'immagine), tutto `restart: always`: sopravvive al riavvio del demone Docker e del PC.
Il motore separato è opzionale: `TRADING_REMOTE=true` nel `.env` e
`docker compose --profile engine up -d`. Le scritture dei pannelli admin e il keyring di Data
Protection vivono su volumi dedicati: la ricreazione dei container non li perde.

### Assetto B — Windows host + cluster kind (l'assetto storico di sviluppo)

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

   > 🔐 `appsettings.json` **non va mai committato**: contiene MasterKey e password. Con la master key placeholder di sviluppo, il trading **Live è bloccato** per costruzione.
   >
   > 🔁 Per **ruotare** la master key non serve ricreare le credenziali: la chiave vecchia va in
   > `Security:PreviousMasterKeys`, la nuova in `Security:MasterKey`, e da `/settings/exchanges` si
   > lancia la ri-cifratura riga per riga. A rotazione conclusa si svuota l'elenco.
   >
   > ⚠️ **Lo stato reale dei flag si legge solo nell'`appsettings.json` non tracciato del repo
   > principale**, mai nell'`.example`: una sezione assente fa vincere il default del POCO, che non
   > sempre è il valore che credi.

2. **Schema DB** — le migrazioni si applicano **da sole all'avvio**
   (`Database:AutoMigrate=true`, con advisory lock per gli avvii concorrenti), a patto che la DLL
   `ProcioneMGR.Migrations.Postgres.dll` stia accanto a `ProcioneMGR.dll` **e sia aggiornata**:

   > ⚠️ Il build dell'app **non** ricostruisce il progetto delle migrazioni. Dopo ogni merge che ne
   > aggiunge una, ricostruiscilo esplicitamente, altrimenti l'app parte con una DLL stantia e le
   > migrazioni nuove non si applicano — silenziosamente. La riga di log `DatabaseMigrator` dice
   > quante ne ha viste: leggila.

   ```powershell
   dotnet build ProcioneMGR.Migrations.Postgres -c Release
   ```
   In alternativa (o con `AutoMigrate=false`) lo schema si applica a mano:
   ```powershell
   dotnet ef database update --project ProcioneMGR.Migrations.Postgres --startup-project ProcioneMGR
   ```

3. **Avvio:**
   ```bash
   dotnet run --project ProcioneMGR --no-launch-profile -c Release
   ```
   Lo script `./scripts/run-postgres.ps1` aggiunge i port-forward verso il cluster kind, ma
   **muore se il cluster è giù** (`$ErrorActionPreference="Stop"` più lo stderr di `kubectl`):
   finché non è corretto, con il cluster spento usa `dotnet run` diretto.

## Test

Richiedono **Docker in esecuzione**: la suite avvia un PostgreSQL effimero via Testcontainers e crea uno schema usa-e-getta per ogni test d'integrazione. I test di logica pura girano senza Docker.

```bash
dotnet test
```

**3.029 test**: unit (matematica/algoritmi), integrazione (Postgres), UI (bUnit). La suite copre, tra l'altro: correttezza matematica dei fattori Alpha158 (anti-look-ahead), invarianti anti-leakage della cross-validation, ottimizzatori di portafoglio su matrici degeneri, `SafetyChecker` in scenari estremi, stress test di ingestione/training/esecuzione concorrente, la state machine di promozione (con fuzzing anti-Live), l'invarianza dello Sharpe alla scala dei rendimenti (il guardiano che rende impossibile reintrodurre il risk-free), la ripresa delle corsie dopo un riavvio, e i componenti Blazor critici.

Il giro completo dura circa un'ora sull'hardware di sviluppo.

## Infrastruttura e deployment

- **Container** — Dockerfile per ogni immagine (main + 3 microservizi), pubblicate su GHCR da workflow CI (matrice di 4 immagini).
- **Kubernetes** — manifest per cluster `kind`, con `NetworkPolicy` (enforcement Calico), secret separati e stack di osservabilità LGTM-lite dietro `Observability:Enabled`. Il cluster usa `imagePullPolicy: Never` con immagini costruite in locale: **GHCR non è sul percorso di esecuzione**.
- **GitOps** — deploy via **ArgoCD** (sync manuale).
- **CI** — `ci.yml` (build + test dell'intera soluzione a ogni push/PR), `docker-build.yml` (build delle immagini), `e2e-kind.yml` (smoke end-to-end su cluster effimero).
- **Backup** — `scripts/db-backup.ps1`, registrabile come operazione pianificata notturna (03:30). Scrive in `%USERPROFILE%\ProcioneMGR-Backup`, **non** nella cartella che `/admin/backup` elenca: quella pagina mostra solo i backup manuali.

I microservizi (pipeline, supervisor, ingestion, ml, trading) restano **in-process per default**: l'estrazione è attivabile per fase tramite i rispettivi feature-toggle.

## Struttura del repository

```
ProcioneMGR/                     App Blazor: Components/ Services/ Data/ Config/
ProcioneMGR.Contracts/           Protos gRPC condivisi
ProcioneMGR.Ingestion/           Microservizio ingestione OHLCV
ProcioneMGR.Ml/                  Microservizio inferenza ML
ProcioneMGR.Trading/             Microservizio motore di trading
ProcioneMGR.Migrations.Postgres/ Migrazioni EF Core (PostgreSQL)
ProcioneMGR.Tests/               Suite di test (3.029)
tools/                           CLI: DbBackup, FuturesVerify, PlatformExpand, SpotVerify, StrategyHunter
infra/k8s/                       Manifest Kubernetes (deployment/service/networkpolicy) + ArgoCD, jobs
scripts/                         bringup, watchdog, build-images-local, db-backup, bootstrap K8s/ArgoCD, osservabilità
docs/                            ROADMAP.md + PRD correnti; audit/ e pagine/; storico in archive/
```

## Documentazione

| Percorso | Cosa contiene |
|---|---|
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | La roadmap **viva**: cosa è aperto, cosa è chiuso e con quale misura |
| [`docs/STATO-DELLA-PIATTAFORMA.md`](docs/STATO-DELLA-PIATTAFORMA.md) | Cosa è stato misurato e cosa è venuto fuori — da leggere prima di decidere qualcosa |
| [`docs/audit/00_INDEX.md`](docs/audit/00_INDEX.md) | Audit completo: superficie API, ogni opzione, ogni campo, ogni test. **Cercare qui prima di esplorare il codice** |
| [`docs/audit/09_RISKS_AND_TECH_DEBT.md`](docs/audit/09_RISKS_AND_TECH_DEBT.md) | Rischi aperti con priorità |
| [`docs/STANDARD-VERIFICA.md`](docs/STANDARD-VERIFICA.md) | I 4 livelli di verifica obbligatori per ogni fase |
| [`docs/pagine/`](docs/pagine/) | Un documento per pagina UI (nome file = slug della rotta) |
| `docs/archive/` | Report e roadmap storiche |

**Convenzioni di codice:** italiano per commenti, log e messaggi UI; inglese per nomi di tipi e
membri. I commenti spiegano il **perché**, con la data e il riferimento alla misura che ha motivato
la scelta — se cambi la decisione, aggiorna il commento.

---

*Progetto personale di ricerca quantitativa. La documentazione dettagliata di ogni fase di sviluppo vive in [`docs/`](docs/).*
