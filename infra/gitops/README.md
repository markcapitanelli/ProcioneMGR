# GitOps (ArgoCD) — Fase 3

Definizioni ArgoCD, tenute separate da `infra/k8s/` di proposito: lì stanno i **manifest**
dell'applicazione (cosa gira), qui **chi li sincronizza** (come ci arriva sul cluster). Mescolarli
significherebbe che ArgoCD gestisce se stesso in mezzo alle risorse che gestisce.

## Struttura: app-of-apps

`root-app.yaml` è l'unica Application da applicare a mano; punta a `apps/`, che contiene una
Application per ogni componente. Da lì in poi si aggiunge un servizio committando un file in
`apps/`, non lanciando comandi.

```
root-app.yaml            → sorveglia infra/gitops/apps/
apps/
  namespaces-app.yaml    → infra/k8s/namespaces   (per primo: gli altri ci creano dentro)
  shared-app.yaml        → infra/k8s/shared       (la PV condivisa: senza, le PVC restano Pending)
  ingestion-app.yaml     → infra/k8s/ingestion
  ml-app.yaml            → infra/k8s/ml
  trading-app.yaml       → infra/k8s/trading
  ui-app.yaml            → infra/k8s/ui
  jobs-app.yaml          → infra/k8s/jobs
```

## Sync manuale ovunque, e perché

Nessuna Application ha `syncPolicy.automated`. Non è pigrizia: è lo stesso gate umano che il
progetto applica dappertutto (`[Authorize]` su `ConfirmOrder`, nessuna promozione automatica a
Live). ArgoCD mostra il diff e aspetta che qualcuno lo guardi e prema Sync.

In più, con `selfHeal` acceso durante il bring-up, un `kubectl edit` fatto per diagnosticare un
problema verrebbe annullato dal controller dopo pochi secondi — senza che sia ovvio perché. Prima si
osservano parecchi sync manuali riusciti, poi semmai si valuta di automatizzare i soli
ingestion/ml (bassa sensibilità, nessuna master key, nessun vincolo di scrittore unico).

**trading e ui restano manuali comunque**: il primo perché esegue ordini veri, il secondo perché
`Recreate` causa una finestra di indisponibilità che deve essere una scelta, non l'effetto di un
poller.

## Cosa ArgoCD NON gestisce

I **Secret** (`postgres-conn`, `trading-secrets`, `ui-secrets`) non sono in Git e non lo saranno mai:
si creano con gli script in `scripts/`. Nessuna Application li referenzia come risorsa, quindi un
sync non li tocca né li cancella (`prune` è comunque disattivato).

Le **migrazioni del DB** restano un passo manuale (`dotnet ef database update`): una modifica di
schema merita lo stesso scrutinio di una promozione.

## Uso

```powershell
.\scripts\k8s-bootstrap.ps1           # cluster + namespace
.\scripts\k8s-argocd-bootstrap.ps1    # ArgoCD + root-app
# poi: port-forward, login, Sync manuale nell'ordine indicato in infra/k8s/README.md (Fase 3)
```

## Limite da tenere presente

Su un cluster kind mono-sviluppatore il valore vero di ArgoCD (audit fra più persone,
riconciliazione continua, promozione fra ambienti) è in gran parte teorico — il cluster viene
distrutto e ricreato di continuo. Quello che si porta a casa concretamente oggi è: (a) i tag immagine
pinnati in Git invece di `:latest`, e (b) un diff visibile prima di ogni applicazione. Lo stesso si
otterrebbe con `kubectl diff -k` + `kubectl apply -k`, senza i ~5 pod di ArgoCD sempre accesi. La
scelta di procedere è consapevole: è un investimento per quando gli ambienti saranno più d'uno.
