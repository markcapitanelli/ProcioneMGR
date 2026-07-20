# ProcioneMGR — Roadmap Kubernetes, Fase 1: Modernizzazione del Backend e Containerizzazione

**Continuazione di `docs/ROADMAP-K8S-FASE0.md`** — stessa disciplina: ogni raccomandazione del PDF
generico ("Da Monolito a Cloud-Native...") viene verificata contro il codice reale prima di essere
accettata, e corretta quando l'ipotesi di partenza non regge. La Fase 0 ha già stabilito i fatti
strutturali (worker singleton non scalabili, Blazor Server intrinsecamente stateful, cluster di
staging locale `kind`, naming ancorato al git SHA); questa Fase 1 li usa come vincoli, non li
ridiscute.

---

## 0. Premessa: cosa cambia rispetto al PDF per la Fase 1

Il PDF struttura la Fase 1 attorno a quattro decisioni: (a) Minimal API vs Controllers, (b) JWT
stateless auth, (c) Dockerfile multi-stage, (d) health checks. Verificate contro il codice, due sono
**non applicabili oggi** (non "sbagliate" come nel caso di Fase 0, ma premature), una è **corretta
ma incompleta** in un punto tecnico specifico, una è **direttamente applicabile e urgente**:

| Decisione del PDF | Verifica sul codice reale | Trattamento in questa Fase 1 |
|---|---|---|
| Minimal API vs Controllers | **Zero superficie REST applicativa.** `Program.cs:440-445` mappa solo `MapStaticAssets`, `MapRazorComponents<App>` (hub Blazor Server) e `MapAdditionalIdentityEndpoints()` — quest'ultimo instrada a `Components/Account/IdentityComponentsEndpointRouteBuilderExtensions.cs`, endpoint **Minimal API** interni dello scaffolding Identity (`/Account/PerformExternalLogin`, `/Account/Logout`, `/Account/Manage/DownloadPersonalData`, ecc.) consumati solo dalle pagine Razor Account. Nessun `Controllers/`, nessun `[ApiController]`, nessun consumer esterno. | **Decisione differita** (Sezione 4) — non c'è nulla da versionare/scegliere finché non esiste un consumer reale |
| JWT stateless | Auth è cookie-based Identity (già noto da Fase 0); Blazor Server resta stateful indipendentemente dal meccanismo di auth (circuito SignalR, Fase 0 §1.2) | **Decisione differita** (Sezione 5) — JWT risolverebbe un problema che oggi non esiste (nessun client headless) |
| Dockerfile multi-stage | Corretta in principio, ma il PDF non poteva sapere che `ProcioneMGR.csproj:23-24` referenzia `Microsoft.ML.LightGbm 5.0.0` — libreria con binari nativi linkati glibc, **incompatibile con le immagini Alpine/musl** usate spesso per "immagini minime" | **Applicabile, con correzione**: immagine runtime Debian-based (`aspnet:10.0`), mai `-alpine` (Sezione 1) |
| Health checks | Assenti al 100% (verificato in Fase 0, riconfermato) | **Applicabile e urgente** — prerequisito per qualunque probe K8s (Sezione 3) |

Una scoperta aggiuntiva, non prevista né dal PDF né dalla Fase 0, cambia la sezione "containerizzazione"
in meglio: **le migrazioni EF non girano già oggi in-process all'avvio**. `Data/DbInitializer.cs`
dichiara esplicitamente nel proprio docstring *"Lo schema del database si applica come passo separato
(...) l'app NON referenzia l'assembly ProcioneMGR.Migrations.Postgres (...) niente
migrate-on-startup — pattern migrate-on-deploy"* — e infatti `InitializeAsync` (righe 12-29) chiama
solo `RoleManager` per garantire i ruoli Admin/Manager/User, nessuna chiamata a `Database.Migrate()`.
Questo è **esattamente** il pattern che Kubernetes vuole (migrazione come step separato, non nel hot
path del container applicativo) — va solo formalizzato come `Job` K8s (Sezione 2), non redisegnato.

> **Nota a margine (fuori scope di questo documento, segnalata separatamente):** il commento a
> `Program.cs:447` ("Applica le migrazioni pendenti e crea i ruoli... all'avvio") e la frase nel
> README.md ("L'app crea/applica automaticamente le migrazioni al primo avvio") sono entrambi
> **disallineati** dal comportamento reale appena descritto — `DbInitializer` non applica
> migrazioni. È un refuso di documentazione preesistente, non introdotto da questa roadmap; va
> corretto a parte per non lasciare un'informazione fuorviante nel repo.

---

## 1. Dockerfile multi-stage

**Immagine di build** (stage 1): `mcr.microsoft.com/dotnet/sdk:10.0` — coerente con
`<TargetFramework>net10.0</TargetFramework>` (`ProcioneMGR.csproj:4`).

**Immagine runtime** (stage 2): `mcr.microsoft.com/dotnet/aspnet:10.0` (Debian-based) — **non**
la variante `-alpine`. Motivazione verificata sul codice, non generica: `Microsoft.ML.LightGbm`
(`ProcioneMGR.csproj:24`) porta i binari nativi `lib_lightgbm` compilati contro glibc; le immagini
Alpine usano musl libc, incompatibilità nota e documentata nell'ecosistema ML.NET/LightGBM. Usare
Alpine "perché più piccola" romperebbe silenziosamente ogni training/inferenza LightGBM
(`GradientBoostingReturnPredictor`, usato anche dal modello Champion) solo in produzione — un
fallimento subdolo da evitare a monte scegliendo Debian fin dall'inizio.

**Contesto di build**: `ProcioneMGR.csproj` non referenzia `ProcioneMGR.Migrations.Postgres` (scelta
architetturale deliberata, per evitare un ciclo di progetti — vedi Sezione 0) né i progetti sotto
`tools/`. L'immagine dell'app web ha quindi bisogno solo di `ProcioneMGR/` come contesto:
1. `COPY ProcioneMGR/ProcioneMGR.csproj` + `dotnet restore` (layer cache separato dal codice sorgente,
   invalidato solo quando cambiano le dipendenze).
2. `COPY ProcioneMGR/` + `dotnet publish -c Release -o /app`.
3. Stage runtime: `COPY --from=build /app .`, utente non-root dedicato (`adduser` + `USER`, come
   raccomandato dal PDF — nessuna ragione per non seguirlo, principio universale non specifico di
   questo repo), `ENTRYPOINT ["dotnet", "ProcioneMGR.dll"]`.

**Porta/bind**: nessuna modifica di codice necessaria. Verificato: il binding Kestrel è interamente
guidato da `ASPNETCORE_URLS` (nessun hardcoding di `localhost` in `Program.cs` o in
`appsettings*.json`) — impostare `ENV ASPNETCORE_URLS=http://+:8080` nel Dockerfile è sufficiente
perché il container ascolti su tutte le interfacce, requisito per essere raggiungibile dal Service
K8s.

**`.dockerignore`** (assente oggi, da creare): `bin/`, `obj/`, `.vs/`, `.git/`, `**/appsettings.json`
(il file reale con i segreti — già gitignorato, ma il contesto Docker non rispetta `.gitignore` di
default, va escluso esplicitamente), `docs/*.pdf`, `backup/`, `tools/`, `ProcioneMGR.Tests/`.

---

## 2. Migrazioni EF come Job Kubernetes (formalizzazione, non redesign)

Il pattern "migrate-on-deploy" è già in uso (Sezione 0): oggi è un comando manuale
(`dotnet ef database update --project ProcioneMGR.Migrations.Postgres --startup-project
ProcioneMGR`, da `README.md`). In Kubernetes questo diventa un `Job` separato dal `Deployment`
dell'app web, eseguito prima del rollout (non un `initContainer` del pod applicativo — un
`initContainer` rieseguirebbe la migrazione a ogni riavvio/scaling del pod, mentre un `Job` gira una
volta per release).

**Raccomandazione tecnica specifica** (non generica, sfrutta uno strumento EF Core già maturo):
`dotnet ef migrations bundle` produce un eseguibile nativo self-contained che applica le migrazioni
senza richiedere l'SDK .NET nell'immagine del Job — più piccola e più veloce da avviare di
un'immagine con l'SDK completo solo per eseguire `dotnet ef`. Il Job userebbe quindi
un'immagine runtime minimale (non l'SDK) con dentro solo il bundle generato da
`ProcioneMGR.Migrations.Postgres` in fase di build CI.

**Non cambia**: `DbInitializer.InitializeAsync` continua a girare in-process all'avvio del pod
applicativo per il seeding dei ruoli (operazione idempotente, veloce — `RoleManager.RoleExistsAsync`
per 3 ruoli fissi, nessun impatto misurabile su startup probe).

---

## 3. Health checks

**Framework nativo** (`Microsoft.Extensions.Diagnostics.HealthChecks`, parte di ASP.NET Core dalla
2.2 — da non confondere con il pacchetto community `AspNetCore.HealthChecks.*`, distinzione che il
PDF sfuma): `builder.Services.AddHealthChecks()` in `Program.cs`, da aggiungere prima di
`builder.Build()`. Per la verifica di raggiungibilità Postgres serve in più il pacchetto community
`AspNetCore.HealthChecks.NpgSql` (`.AddNpgSql(connectionString)`), coerente con la sola dipendenza
esterna hard che l'app ha oggi (nessun Redis, nessun altro datastore — confermato in Fase 0).

**Due endpoint, seguendo la distinzione già corretta del PDF** (probe "dumb" per liveness, per
evitare cascading failure):
- `/healthz/live` — solo verifica che il processo risponda, nessuna dipendenza esterna controllata.
  Mappato a `livenessProbe`.
- `/healthz/ready` — include il check Postgres (`AddNpgSql`). Mappato a `readinessProbe`: se
  Postgres è irraggiungibile, il pod viene tolto dagli endpoint del Service ma **non** riavviato
  (comportamento corretto — riavviare il processo non risolve un Postgres down).

**Startup probe**: necessario perché `DbInitializer.InitializeAsync` (righe 450-453 di `Program.cs`)
è **bloccante** prima che `app.Run()` inizi ad accettare connessioni — include un round-trip reale
verso Postgres (`RoleManager.RoleExistsAsync`/`CreateAsync`). In assenza di un `startupProbe`
dedicato con `failureThreshold` generoso, un `livenessProbe` troppo aggressivo rischierebbe di
uccidere il pod mentre sta ancora aspettando Postgres al primo avvio (esattamente il problema che il
PDF descrive in astratto — qui è verificabile: il blocco esiste davvero, a righe precise).

**Attenzione al fail-fast esistente** (`Program.cs:415-422`): se `ASPNETCORE_ENVIRONMENT=Production`
e la master key è ancora il placeholder del template, l'app lancia un'eccezione **prima di qualunque
middleware**, il processo termina e il pod entra in `CrashLoopBackOff`. Questo è un comportamento
**corretto da preservare** (è la barriera di sicurezza contro credenziali "cifrate" con una chiave
pubblica su git), ma significa che il Secret K8s con `PROCIONE_MGR_MASTER_KEY` reale deve esistere
**prima** del primo deploy, altrimenti il primo rollout fallirà in modo rumoroso ma sicuro (da
preferire silenziosamente insicuro).

**Nessun impatto sui worker**: `MarketDataSyncWorker` (delay 15s), `RegimeRetrainingWorker` (30s),
`TradingWorker`/`ExecutionWorker` (10s/20s) ritardano volutamente il primo lavoro reale dopo
l'avvio (righe verificate: `MarketDataSyncWorker.cs:34`, `TradingWorker.cs:30`) e non caricano
modelli ML né chiamano API exchange in modo sincrono — non influenzano i tempi di readiness/liveness,
che dipendono solo da `DbInitializer` e dall'HTTP listener.

---

## 4. Decisione differita: Minimal API vs Controllers

Non c'è nulla da decidere ora — decidere uno stile per zero endpoint applicativi sarebbe scegliere
in astratto, esattamente l'errore che il PDF commette scrivendo una Fase 1 generica. **Precedente
già stabilito nel codice**: gli unici endpoint HTTP espliciti oggi (`IdentityComponentsEndpointRouteBuilderExtensions.cs`)
sono già in stile Minimal API (scaffolding standard di ASP.NET Core Identity) — quando servirà un
primo endpoint applicativo reale (candidato più probabile: un piccolo endpoint di health/readiness,
già coperto in Sezione 3 dal framework nativo; oppure, più avanti, un'API per il servizio
`MarketDataSyncWorker` estratto — Fase 0 §1.3), il criterio è: **Minimal API di default**, coerente
sia con il precedente esistente sia con la raccomandazione del PDF stesso per "nuova logica o
microservizi leggeri". Passare a Controllers avrebbe senso solo per una superficie ampia e
complessa che oggi non esiste e non è pianificata.

---

## 5. Decisione differita: autenticazione JWT

La UI Blazor Server **mantiene** l'auth a cookie ASP.NET Identity — non c'è alcun beneficio a
introdurre JWT per essa: la staticità richiesta da JWT (statelessness per scalare orizzontalmente)
non risolverebbe nulla finché il circuito SignalR resta pinnato a un'istanza server (Fase 0 §1.2).
Introdurre JWT ora sarebbe infrastruttura speculativa senza consumer — la stessa disciplina già
applicata altrove nel progetto (`docs/ROADMAP-QLIB.md` §4, principio di "additività architetturale":
non costruire per un bisogno ipotetico).

**Quando JWT diventerà rilevante**: solo se/quando nascerà un client headless reale — l'esempio più
concreto oggi è un futuro servizio `MarketDataSyncWorker` estratto (Fase 0 §1.3) che dovrebbe
autenticarsi verso il resto della piattaforma senza sessione browser, o un consumer esterno
(script di automazione, mobile). Design di riferimento da riusare quando servirà (dal PDF, corretto
solo nei dettagli implementativi, non nel principio): access token breve (~15 min) + refresh token
in cookie `HttpOnly`, rotazione del refresh token a ogni uso, validazione rigorosa di
issuer/audience/scadenza/firma. Non implementato ora.

---

## 6. Naming immagine e verifica sul cluster locale

**Naming**: applica la convenzione già fissata in Fase 0 §7 — `procionemgr-web:<git-sha-corto>`
(es. `procionemgr-web:7385846`), label `app.kubernetes.io/name=procionemgr`,
`app.kubernetes.io/component=web`, `app.kubernetes.io/part-of=procionemgr`.

**Verifica end-to-end su `kind`** (chiude il cerchio con Fase 0 §3, primo uso reale del cluster
locale bootstrap in quella fase):
1. `docker build` dell'immagine, `kind load docker-image procionemgr-web:<sha>` nel cluster
   `procionemgr-staging` già esistente.
2. Manifest minimi applicati manualmente (`kubectl apply`, non ancora GitOps — quello è materia
   della futura Fase 4 del PDF, fuori scope qui): `Deployment` con **`replicas: 1`** (vincolo
   ereditato da Fase 0 §1.1 — oggi web e worker di trading vivono nello stesso processo, quindi lo
   stesso limite si applica all'intero pod finché non avviene una scomposizione), `Service`
   `ClusterIP`, `ConfigMap` per la configurazione non sensibile, `Secret` per
   `PROCIONE_MGR_MASTER_KEY` e `ConnectionStrings__PostgresConnection` (convenzione standard
   ASP.NET Core a doppio underscore, già in uso in `.claude/launch.json:16` — nessuna traduzione
   necessaria).
3. Un Postgres di riferimento per lo staging (può essere lo stesso pattern Testcontainers-style già
   noto ai test, oppure un `Deployment` Postgres separato nel namespace `procionemgr-staging` — da
   decidere nella prossima fase di dettaglio, non qui).
4. Riesecuzione della suite Playwright di Fase 0 §4 (5 percorsi critici) puntata sull'app
   containerizzata invece che sul processo `dotnet run` — è la prova che containerizzazione e
   health check non hanno cambiato comportamento osservabile, lo stesso principio di baseline
   immutabile già applicato in Fase 0.

---

## Tabella riassuntiva: attività Fase 1

| # | Attività | Richiede codice? | Priorità |
|---|---|---|---|
| 1 | Dockerfile multi-stage (Debian, non Alpine — Sezione 1) | Sì (nuovo file, no modifiche a codice esistente) | P0 |
| 2 | `.dockerignore` (Sezione 1) | Sì (nuovo file) | P0 |
| 3 | Job K8s per migrazioni via `dotnet ef migrations bundle` (Sezione 2) | Sì (manifest + step CI) | P0 |
| 4 | Health checks `/healthz/live` + `/healthz/ready` (Sezione 3) | Sì (`Program.cs` + 1 pacchetto NuGet) | P0 |
| 5 | Manifest K8s minimi + verifica su `kind` (Sezione 6) | No (solo YAML + comandi) | P0 |
| 6 | Minimal API vs Controllers | Nessuna azione — decisione differita (Sezione 4) | Differita |
| 7 | JWT auth | Nessuna azione — decisione differita (Sezione 5) | Differita |
| 8 | Correzione commento stale `Program.cs:447` + README.md sulle migrazioni | Sì (fuori scope di questo documento, segnalata a parte) | Fuori scope |

---

## Prossimo passo operativo

Le attività P0 hanno una dipendenza naturale: il Dockerfile (1-2) deve esistere prima che i health
check (4) siano verificabili in un container reale, e prima che la migrazione a Job (3) e i manifest
K8s (5) abbiano senso. Ordine consigliato: (1) Dockerfile + `.dockerignore`, verificato con un
semplice `docker run` locale (senza K8s) per confermare che l'app parte, si connette a un Postgres
raggiungibile e risponde su `ASPNETCORE_URLS`; (2) aggiunta degli health check endpoint, verificati
con lo stesso `docker run`; (3) bundle di migrazione EF, verificato a mano contro un Postgres
effimero; (4) manifest K8s minimi e primo deploy sul cluster `kind` di Fase 0, chiuso dalla
riesecuzione della suite Playwright come prova di non-regressione.

Le due decisioni differite (Sezioni 4-5) non bloccano nulla di quanto sopra — restano annotate come
criteri pronti da applicare il giorno in cui un consumer reale (API o client headless) le renderà
rilevanti, non prima.
