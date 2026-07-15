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
