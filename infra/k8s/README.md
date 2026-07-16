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
kubectl apply -k infra/k8s/ingestion/             # Deployment + Service ingestion
kubectl apply -k infra/k8s/jobs/                  # registra Job + CronJob (CronJob sospeso)
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
kubectl apply -k infra/k8s/trading/          # PVC + Deployment + Service + NetworkPolicy
kubectl port-forward -n procionemgr-trading svc/procionemgr-trading 18092:8080
# → poi monolite con Trading:UseRemoteTrading=true e Trading:RemoteUrl=http://localhost:18092
```

Il Deployment richiede il PVC `procionemgr-config`, ora definito in `trading/pvc.yaml` (in Fase 2b
era referenziato per nome ma non esisteva come manifest: su un cluster pulito il pod sarebbe rimasto
`Pending`). Vedi la sezione Fase 3 per come è condiviso col monolite.

## Fase 3 — GitOps (ArgoCD) + il monolite come pod (`infra/gitops/`, `infra/k8s/ui/`)

Questa fase non estrae nuovi servizi: **automatizza ciò che esiste** e porta in cluster l'ultimo
pezzo che non c'era. I bounded context `pipeline` (Autonomous Pipeline in-process) e `supervisor`
(LlmSupervisorWorker, drift, promozioni) restano nel monolite, per scelta.

| Workload | Namespace | Tipo | Note |
|---|---|---|---|
| `procionemgr-ui` | `procionemgr-ui` | `Deployment` + `Service` | **`replicas: 1` sempre** + `Recreate`. Primo deploy K8s del monolite: l'immagine esisteva da mesi, non era mai stata distribuita. Configurato come "client puro" (toggle verso i 3 servizi remoti). Nessun Ingress: `port-forward`. Ha la **master key** (copia di quella di trading). |

### Il tag immagine è pinnato in Git, non `:latest`

Ogni servizio ha un `kustomization.yaml` con un blocco `images:`. I `deployment.yaml` mantengono
`:latest` come placeholder leggibile, ma **ciò che viene applicato è il tag scritto nel
kustomization**. `:latest` è un bersaglio mobile: due sync identici a un giorno di distanza possono
far girare binari diversi senza che il repo lo dica. Con un tag pinnato, "cosa sta girando?" ha una
risposta in Git e il rollback è un `revert`.

Il bump del tag è **manuale**: il bump *è* la promozione. Automatizzarlo da CI sarebbe l'opposto del
"nessuna promozione automatica" applicato dappertutto. Quando servirà, il passo giusto è un workflow
che apre una **PR** col bump, da approvare a mano.

> ⚠️ Aggiungere un `kustomization.yaml` a una cartella **rompe `kubectl apply -f <dir>`**: kubectl
> proverebbe ad applicare anche quel file come se fosse una risorsa. Da qui in poi si usa
> `kubectl apply -k <dir>`. Gli script e le istruzioni sopra sono già aggiornati.

### ArgoCD: sync manuale ovunque

```powershell
.\scripts\k8s-bootstrap.ps1              # cluster + namespace
.\scripts\k8s-argocd-bootstrap.ps1       # ArgoCD (versione pinnata) + root-app
# password admin stampata a schermo, mai su file. Poi:
kubectl port-forward svc/argocd-server -n argocd 8081:443 --context kind-procionemgr-dev
```

`root-app.yaml` è l'unica Application da applicare a mano: sorveglia `infra/gitops/apps/` e crea da
sé le altre 7 (app-of-apps). Da lì in poi si aggiunge un servizio committando un file.

**Nessuna Application ha `syncPolicy.automated`.** Non è pigrizia: è lo stesso gate umano di
`ConfirmOrder`. In più, con `selfHeal` acceso, un `kubectl edit` fatto per diagnosticare un problema
verrebbe annullato dal controller senza spiegazioni. Per `trading` e `ui` il manuale è definitivo
(ordini veri; `Recreate` = finestra di indisponibilità che deve essere una scelta).

Ordine di Sync (le `sync-wave` lo codificano): `namespaces` → `shared` → `ingestion`/`ml` →
`trading` → `ui` → `jobs`.

**Cosa ArgoCD non gestisce**: i Secret (non sono in Git, si creano con gli script) e le migrazioni
del DB (`dotnet ef database update`, passo manuale — una modifica di schema merita lo stesso
scrutinio di una promozione). Se il pod ui parte prima della migrazione va in crash-loop con
`relation "AspNetRoles" does not exist` e si riprende da solo appena lo schema c'è: **migrare
prima**.

Per provare un branch prima del merge (ArgoCD legge da GitHub, non dal disco):
```powershell
.\scripts\k8s-argocd-bootstrap.ps1 -TargetRevision <branch>   # il branch dev'essere PUSHATO
.\scripts\k8s-argocd-retarget.ps1  -TargetRevision <branch>   # dopo il primo Sync di root-app
.\scripts\k8s-argocd-retarget.ps1  -TargetRevision master     # ripristino
```
Agiscono solo sugli oggetti nel cluster: i file restano puntati a `master`, così non resta un branch
di lavoro committato per sbaglio.

### Il file di configurazione condiviso: due PV, non una

`AppConfigWriter` scrive **letteralmente** su `<ContentRootPath>/appsettings.json`. Perché i limiti
di rischio modificati dal pannello `/trading` valgano nel motore che esegue gli ordini, i due pod
devono vedere **lo stesso file**.

Il punto contro-intuitivo: **il binding PV↔PVC è 1:1, sempre**. RWX dice quanti *pod* possono
montare *una* PVC, non quante PVC per PV. E le PVC sono namespaced. Quindi `ui` e `trading` **non
possono** condividere né una PVC né una PV: con una PV sola la prima PVC se la prende e l'altra
resta `Pending` per sempre (verificato dal vivo, non dedotto).

La soluzione è quella che si usa con NFS: **due PV che puntano allo stesso storage**, ognuna
pre-legata alla sua PVC con `claimRef` (senza, l'assegnazione dipenderebbe dall'ordine di arrivo).
La PV non è lo storage, è un puntatore.

Un `initContainer` scrive `{}` al primo mount: un `subPath` montato su un file mai creato lo
materializza **vuoto**, e un `appsettings.json` vuoto non è JSON valido — l'app morirebbe a startup
con un errore di parsing (l'`optional` del provider copre il file *assente*, non quello malformato).

### Data Protection: keyring persistito

Il monolite ora chiama `PersistKeysToFileSystem` se `DataProtection:KeyRingPath` è configurato (in
cluster: una PVC RWO dedicata). Senza, il keyring vive in memoria e **ogni** riavvio del pod — un
OOM-kill, una liveness probe fallita, non solo un deploy — invalida tutti i cookie e disconnette gli
utenti in silenzio. Fuori dal cluster la chiave non è impostata e vale il default di ASP.NET Core
(cartella del profilo utente, già persistente): in locale non cambia nulla.

### Rischi e limiti (Fase 3)

- ⚠️ **`hostPath` non è un vero RWX.** Su kind (mono-nodo) due PV sullo stesso `hostPath` *sono* lo
  stesso file, e questo basta a validare il **contratto applicativo** (ui scrive → trading rilegge).
  Non valida la semantica RWX su più nodi: in un cluster reale le due PV vanno ripuntate allo stesso
  export NFS/CephFS/Azure Files, altrimenti due pod su nodi diversi vedrebbero due file diversi.
- ⚠️ **La master key è duplicata** in `ui-secrets` e `trading-secrets` (i Secret sono namespaced:
  è per forza una copia). `k8s-ui-secret.ps1` confronta le due e avvisa se divergono, ma nulla
  impedisce di ruotarne una sola più tardi: le credenziali si decifrerebbero da una parte e non
  dall'altra, in silenzio, fino al primo uso.
- ⚠️ **ArgoCD e i `Job`**: i campi di un Job sono immutabili. Se cambia il template di
  `strategyhunter-job.yaml`, il sync fallisce finché non si fa
  `kubectl delete job strategyhunter-discover -n procionemgr-pipeline`. È un limite di Kubernetes
  (GitOps presume "riapplica per convergere"), non di ArgoCD. Il CronJob non ne soffre.
- **Ingress rimandato**: si raggiunge la UI solo via `port-forward`. Servirebbe ricreare il cluster
  (label `ingress-ready`) e sistemare `app.UseHttpsRedirection()` (`Program.cs`), oggi chiamato
  incondizionatamente: dietro un ingress in chiaro rimanderebbe tutto a `https://`. Con
  `ASPNETCORE_URLS` solo-HTTP quella riga è di fatto inerte, quindi via port-forward non dà
  fastidio — ma va risolta *prima* di esporre la UI.
- **Il valore di ArgoCD qui è, onestamente, marginale**: un solo sviluppatore, un solo cluster che
  viene distrutto e ricreato di continuo. Quello che si porta a casa davvero è (a) i tag pinnati e
  (b) un diff visibile prima di applicare — entrambi ottenibili con `kubectl diff -k` + `apply -k`,
  senza i ~5 pod di ArgoCD sempre accesi. La scelta di procedere è consapevole: è un investimento
  per quando gli ambienti saranno più d'uno.
- **La NetworkPolicy resta non applicata su kind** (kindnet la ignora): riverificato in Fase 3 con
  `ui` come pod reale — una probe da `procionemgr-pipeline` verso trading **passa**. Il confine di
  autorizzazione davanti a `ConfirmOrder` è attivo solo su un cluster con Calico/Cilium.
