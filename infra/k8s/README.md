# Kubernetes locale (kind) — Fase 0

Ambiente Kubernetes locale per la migrazione a microservizi (PRD "Da Monolite a Microservizi").
In Fase 0 il cluster **non ospita alcun workload applicativo**: serve a validare il tooling
(kind + kubectl) e a fissare la nomenclatura dei namespace per i 6 bounded context.

## Prerequisiti

- Docker Desktop attivo (già richiesto per i test con Testcontainers).
- CLI: `winget install Kubernetes.kind` e `kubectl` (bundlato in Docker Desktop, oppure
  `winget install Kubernetes.kubectl`).

## Uso

```powershell
# Crea il cluster procionemgr-dev e applica i namespace
.\scripts\k8s-bootstrap.ps1

# Verifica
kubectl get nodes --context kind-procionemgr-dev
kubectl get namespaces --context kind-procionemgr-dev | Select-String procionemgr

# Distrugge il cluster (reversibilità totale, zero residui)
.\scripts\k8s-teardown.ps1
```

## Contenuto

| File | Scopo |
|---|---|
| `kind-config.yaml` | Cluster mono-nodo `procionemgr-dev`, porte 80/443 mappate su 8080/8443 host (pronte per un futuro ingress, Fase 1+) |
| `namespaces/00-namespaces.yaml` | I 6 namespace dei bounded context (`procionemgr-ui`, `-trading`, `-ml`, `-pipeline`, `-ingestion`, `-supervisor`) |

## Vincoli per le fasi successive

- `procionemgr-trading`: i futuri Deployment di TradingWorker/ExecutionWorker avranno **sempre
  `replicas: 1`** (vincolo di sicurezza non negoziabile: mai doppia esecuzione di ordini).
- `procionemgr-ingestion`: anche il futuro Deployment del servizio di ingestione avrà **sempre
  `replicas: 1`**. Motivo: `MarketDataSyncService.SyncSeriesAsync` calcola il cursore incrementale
  con un `MaxAsync(TimestampUtc)` non lockato e `OhlcvIngestionService` fa un upsert SELECT-poi-
  INSERT (non atomico `ON CONFLICT`). Due repliche in scrittura concorrente sulla stessa serie
  rischierebbero una `DbUpdateException` da violazione dell'indice unico. Finché non c'è un lock
  distribuito o un partizionamento per serie, una sola replica.
- Nessuna promozione automatica a Live: qualunque futura pipeline di deploy mantiene il gate
  manuale umano.

## Fase 1 — Workload (`infra/k8s/ingestion/` + `infra/k8s/jobs/`)

Mapping namespace → workload:

| Workload | Namespace | Tipo | Note |
|---|---|---|---|
| `procionemgr-ingestion` | `procionemgr-ingestion` | `Deployment` + `Service` | **`replicas: 1` sempre** + strategy `Recreate` (mai due worker di sync vivi insieme). Service solo `ClusterIP`: nessun Ingress in Fase 1 (l'endpoint `/sync` non ha autenticazione; il confine è la rete del cluster). **⚠ Doppio scrittore**: deployare questo servizio SOLO con il monolite in modalità remota (`MarketData:UseRemoteIngestion=true`, che spegne il worker locale) oppure con `MarketData:Enabled=false` nel monolite — mai entrambi i worker attivi sullo stesso DB. |
| `procionemgr-ml` | `procionemgr-ml` | `Deployment` + `Service` | **Sola lettura** (inferenza gRPC, Fase 2a dual-read): legge i `SavedMlModels`, nessuna scrittura → **nessun** vincolo `replicas: 1` (parte a 1 solo per prudenza, scalabile in futuro). Service `ClusterIP`, nessun Ingress. Il monolite lo usa in modo puramente osservativo (`Ml:Enabled`+`Ml:RemoteUrl`): un servizio ml giù non impatta il trading. Nessun rischio doppio-scrittore. |
| `procionemgr-trading` | `procionemgr-trading` | `Deployment` + `Service` + **`NetworkPolicy`** | **`replicas: 1` sempre** + strategy `Recreate`. Tutte e 3 le lane girano in questo singolo processo: la mutua esclusione per corsia è un `SemaphoreSlim` **di istanza**, che con 2 repliche proteggerebbe due stati scollegati → doppia esecuzione di ordini. **⚠ Doppio motore**: deployare SOLO col monolite in `Trading:UseRemoteTrading=true` (che non registra i worker locali). Unico servizio con la **master key**. Vedi Fase 2b sotto. |
| `strategyhunter-discover` | `procionemgr-pipeline` | `Job` | Discovery/ottimizzazione batch. Fase esplicita via args (`discover`); `all` non esiste nel tool. |
| `dbbackup-nightly` | `procionemgr-supervisor` | `CronJob` | **`suspend: true` di default**: con l'`emptyDir` attuale i backup andrebbero persi alla fine del pod. Attivare solo dopo aver montato un PVC. Solo `backup`/`verify`/`list`; **`restore` mai schedulato** (distruttivo → gate umano manuale). |

### Secret Postgres (non committato)

I Job leggono la connection string da un Secret `postgres-conn` (chiave
`ConnectionStrings__PostgresConnection`), **mai** committato in un manifest YAML. Crearlo con:

```powershell
.\scripts\k8s-postgres-secret.ps1 -ConnectionString "Host=host.docker.internal;Port=5432;Database=procionemgr;Username=procione;Password=..."
```

**Raggiungibilità Postgres da kind**: il cluster non ospita un proprio Postgres; i pod usano quello
dell'host di sviluppo. Da dentro un pod, `localhost` è il pod stesso — per raggiungere l'host serve
`host.docker.internal` (risolto da Docker Desktop su Windows/Mac) come `Host` nella connection string.

### Smoke test

```powershell
.\scripts\k8s-bootstrap.ps1                       # cluster + namespace
.\scripts\k8s-postgres-secret.ps1 -ConnectionString "Host=host.docker.internal;..."
kubectl apply -f infra/k8s/ingestion/             # Deployment + Service ingestion
kubectl apply -f infra/k8s/jobs/                  # registra Job + CronJob (CronJob sospeso)
# One-shot del CronJob senza aspettare le 03:00 (funziona anche da sospeso):
kubectl create job --from=cronjob/dbbackup-nightly dbbackup-smoke -n procionemgr-supervisor
kubectl logs -f job/dbbackup-smoke -n procionemgr-supervisor
# Raggiungere il servizio ingestion dal monolite sull'host:
kubectl port-forward -n procionemgr-ingestion svc/procionemgr-ingestion 18080:8080
```

Nota: il CronJob nasce `suspend: true` perché usa un `emptyDir` per `/backup` (i backup non
sopravvivono al pod — va bene solo per lo smoke test one-shot). Prima di attivarlo
(`suspend: false`) sostituire il volume con un `persistentVolumeClaim`.

## Fase 2b — Trading (`infra/k8s/trading/`)

Il servizio più delicato della migrazione: è l'unico che esegue ordini veri. Tre cose lo
distinguono da ingestion/ml, e vale la pena averle chiare prima di deployarlo.

### 1. `replicas: 1` non è prudenza, è correttezza

Le 3 lane girano **tutte dentro un solo processo**. Il vincolo "mai due esecuzioni simultanee sulla
stessa corsia" è retto da un `SemaphoreSlim` di **istanza** (`TradingEngine._gate`), non da un lock
distribuito: con 2 repliche quel semaforo protegge due stati in-memory scollegati e i due pod
aprirebbero ordini sulla stessa corsia. Per lo stesso motivo la strategy è `Recreate` e non
`RollingUpdate` (che farebbe coesistere due pod per qualche secondo). Scalare richiede prima un
lock distribuito o uno sharding delle lane per pod — non un numero più alto qui.

### 2. Mutua esclusione col monolite: è il toggle, non un lock

Dentro un processo l'esclusione è garantita **per costruzione** dalla composizione DI
(`Trading:UseRemoteTrading=true` non registra `TradingEngine`/`TradingWorker`/`ExecutionWorker`
locali ma solo `RemoteTradingEngineClient`; con `false` l'esatto opposto). Lo verifica
`TradingServiceCollectionExtensionsTests`. **Fra i due processi**, invece, la garanzia è
operativa: deployare questo servizio **solo** col monolite in modalità remota. Con il toggle a
`false` e questo Deployment attivo, due motori eseguirebbero sullo stesso DB.

Il toggle richiede un **riavvio** del monolite (il canale gRPC si crea una volta sola). Ordine
consigliato: fermare le lane da `/trading` → deployare il servizio → riavviare il monolite con
`Trading:UseRemoteTrading=true` e `Trading:RemoteUrl=http://procionemgr-trading.procionemgr-trading.svc.cluster.local:8080`
→ riavviare le lane. `Trading:RemoteUrl` mancante = **fail-fast a startup** (meglio non partire che
partire con un trading muto).

### 3. Master key: il salto di sensibilità

`trading-secrets` contiene `ConnectionStrings__PostgresConnection` **+ `Security__MasterKey`.
È l'unico servizio satellite che riceve la master key**: gli serve per decifrare le credenziali
exchange e firmare le chiamate Testnet/Live (ingestion e ml usano un `IEncryptionService` no-op e
non la ricevono). Deve essere la **stessa** del monolite, o le credenziali non si decifrano.

```powershell
$env:PROCIONE_MGR_MASTER_KEY = "<base64 32 byte>"   # via env: non finisce nella cronologia shell
.\scripts\k8s-trading-secret.ps1 -ConnectionString "Host=host.docker.internal;Port=5432;..."
```

Un Secret Kubernetes è **base64, non cifrato**: chiunque possa leggere i Secret del namespace legge
la chiave. Oltre lo sviluppo locale servono RBAC stretto sui Secret + encryption-at-rest di etcd
(o Vault/Sealed Secrets).

### `NetworkPolicy` — la prima del progetto, e il suo limite

`ConfirmOrder` sblocca un ordine Live reale e `StartLane(LIVE)` avvia una sessione con denaro vero.
Nel monolite quelle azioni sono dietro `[Authorize(Admin,Manager)]` di `Trading.razor`; **dietro
gRPC quel gate non esiste**: non c'è autenticazione a livello RPC. `networkpolicy.yaml` è l'unico
controllo di accesso — ingress consentito solo dal pod `procionemgr-ui`.

> ⚠️ **Una NetworkPolicy la applica il CNI.** Senza un CNI che la implementi viene accettata
> dall'API server e **ignorata in silenzio**: sembra protetta e non lo è. Il CNI di default di kind
> (**kindnet**) **non le applica** — sul cluster di sviluppo questo confine **non è attivo**. Per
> una verifica reale serve Calico/Cilium. Da provare esplicitamente, non da dare per fatto:

```powershell
# Da un pod di un namespace terzo: DEVE fallire (e su kindnet invece riesce).
kubectl run probe -n procionemgr-pipeline --rm -it --image=curlimages/curl --restart=Never -- `
  curl -sS -m 5 http://procionemgr-trading.procionemgr-trading.svc.cluster.local:8080/health
```

Nota: `kubectl port-forward` passa dall'API server e **non** dalla rete dei pod — scavalca la
NetworkPolicy. Comodo in sviluppo, da non scambiare per un accesso consentito.

### `SafetyConfiguration`: PVC condiviso (limite noto)

`AppConfigWriter` scrive **letteralmente** su `<ContentRootPath>/appsettings.json` — non c'è un
provider di configurazione astratto da ripuntare altrove senza refactor. Perché i limiti di
sicurezza modificati dal pannello `/trading` (monolite) valgano anche nel motore (servizio), i due
pod devono vedere **lo stesso file**: PVC `procionemgr-config` **ReadWriteMany** montato sullo
stesso path assoluto in entrambi. Il monolite scrive, il trading rilegge a caldo (`reloadOnChange`,
~1s).

Limiti da conoscere:
- **RWX richiede una storage class che lo supporti** (NFS, CephFS, Azure Files…). La default di
  kind (`local-path`) è **solo RWO**: in locale i due processi vanno puntati a mano sullo stesso
  file su disco (scelta operativa, non codice).
- Se i due file divergono, il servizio applica **limiti di sicurezza diversi da quelli mostrati in
  UI** — e nessuno se ne accorge finché un ordine non viene rifiutato (o accettato) a sorpresa.

### Smoke test

```powershell
.\scripts\k8s-bootstrap.ps1
$env:PROCIONE_MGR_MASTER_KEY = "<base64 32 byte>"
.\scripts\k8s-trading-secret.ps1 -ConnectionString "Host=host.docker.internal;Port=5432;..."
kubectl apply -f infra/k8s/trading/          # Deployment + Service + NetworkPolicy
kubectl port-forward -n procionemgr-trading svc/procionemgr-trading 18092:8080
# → poi monolite con Trading:UseRemoteTrading=true e Trading:RemoteUrl=http://localhost:18092
```

Il Deployment richiede il PVC `procionemgr-config`: senza, il pod resta in `Pending`. In locale, se
la storage class non fa RWX, togliere il volume dal manifest e montare l'appsettings condiviso in
un altro modo (o accettare che le due configurazioni divergano — **mai** con denaro reale).
