<#
.SYNOPSIS
    Crea il cluster kind locale per la Fase 0 della migrazione a microservizi.

.DESCRIPTION
    - Crea il cluster "procionemgr-dev" da infra/k8s/kind-config.yaml (mono-nodo).
    - Applica i namespace dei 6 bounded context (infra/k8s/namespaces/).
    - NON deploya alcun workload applicativo: in Fase 0 il cluster serve solo a validare
      il tooling e a fissare la nomenclatura per le Fasi 1+.
    - Idempotente: se il cluster esiste già, salta la creazione e riapplica i namespace.

.NOTES
    Prerequisiti: Docker Desktop attivo, kind e kubectl nel PATH
                  (winget install Kubernetes.kind / Kubernetes.kubectl).
    Uso:      .\scripts\k8s-bootstrap.ps1
    Teardown: .\scripts\k8s-teardown.ps1
#>

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\k8s-common.ps1"
$repoRoot = Split-Path -Parent $PSScriptRoot
$kindConfig = Join-Path $repoRoot "infra\k8s\kind-config.yaml"
$namespacesDir = Join-Path $repoRoot "infra\k8s\namespaces"
$clusterName = $script:KindClusterName

foreach ($tool in @("kind", "kubectl")) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Host "ERRORE: '$tool' non trovato nel PATH. Installa con: winget install Kubernetes.$tool" -ForegroundColor Red
        exit 1
    }
}

if (Test-KindCluster $clusterName) {
    Write-Host "Cluster '$clusterName' già esistente: salto la creazione." -ForegroundColor Yellow
    Write-Host "NB: se è stato creato PRIMA del passaggio a Calico (Fase 3), va ricreato:" -ForegroundColor Yellow
    Write-Host "    .\scripts\k8s-teardown.ps1 ; .\scripts\k8s-bootstrap.ps1" -ForegroundColor Yellow
} else {
    Write-Host "Creo il cluster kind '$clusterName'..." -ForegroundColor Cyan
    kind create cluster --name $clusterName --config $kindConfig
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# --- CNI: Calico (Fase 3) ---
# kind-config.yaml disattiva kindnet (disableDefaultCNI): senza un CNI i nodi restano NotReady e
# nessun pod parte. Calico è qui perché APPLICA le NetworkPolicy, che kindnet ignora in silenzio —
# e la policy su procionemgr-trading è l'unico controllo di accesso davanti a ConfirmOrder (ordini
# Live reali). Versione PINNATA, mai un tag mobile: stesso patto delle nostre immagini e di ArgoCD.
# L'apply è idempotente: rilanciare lo script su un cluster già con Calico non cambia nulla.
#
# Il pool IPv4 viene PATCHATO a 10.244.0.0/16 (default: 192.168.0.0/16) prima dell'apply, in
# accordo col podSubnet di kind-config.yaml. Motivo (B1, 2026-07-26, trovato solo eseguendo):
# host.docker.internal è 192.168.65.254, DENTRO il pool di default — Calico non fa SNAT verso
# destinazioni interne al pool, quindi ogni connessione dei pod al Postgres dell'host partiva con
# IP di pod e moriva in timeout. Il patch è verificato: se il testo del manifest cambia con una
# futura versione di Calico, lo script FALLISCE invece di applicare il default sbagliato.
$calicoVersion = "v3.29.1"
$calicoManifestUrl = "https://raw.githubusercontent.com/projectcalico/calico/$calicoVersion/manifests/calico.yaml"
$podCidr = "10.244.0.0/16"
Write-Host "Installo il CNI Calico $calicoVersion (pool $podCidr, applica le NetworkPolicy)..." -ForegroundColor Cyan
$calicoYaml = (Invoke-WebRequest -UseBasicParsing -Uri $calicoManifestUrl).Content
$cidrBlock = "# - name: CALICO_IPV4POOL_CIDR`n            #   value: `"192.168.0.0/16`""
if (-not $calicoYaml.Contains($cidrBlock)) {
    Write-Host "ERRORE: il manifest Calico $calicoVersion non contiene il blocco CALICO_IPV4POOL_CIDR atteso." -ForegroundColor Red
    Write-Host "Senza il patch i pod non avrebbero SNAT verso host.docker.internal: mi fermo." -ForegroundColor Red
    exit 1
}
$calicoYaml = $calicoYaml.Replace($cidrBlock, "- name: CALICO_IPV4POOL_CIDR`n              value: `"$podCidr`"")
$calicoTmp = Join-Path $env:TEMP "calico-$calicoVersion-patched.yaml"
[IO.File]::WriteAllText($calicoTmp, $calicoYaml)
kubectl apply -f $calicoTmp --context "kind-$clusterName"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Attendo che Calico e il nodo siano pronti..." -ForegroundColor Cyan
kubectl rollout status daemonset/calico-node -n kube-system --timeout=300s --context "kind-$clusterName"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
kubectl wait --for=condition=Ready node --all --timeout=300s --context "kind-$clusterName"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Applico i namespace dei bounded context..." -ForegroundColor Cyan
# -k (kustomize), non -f: da quando la cartella contiene un kustomization.yaml (Fase 3), un
# `apply -f <dir>` proverebbe ad applicare ANCHE quel file come se fosse una risorsa, e fallirebbe.
kubectl apply -k $namespacesDir --context "kind-$clusterName"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
kubectl cluster-info --context "kind-$clusterName"
Write-Host ""
Write-Host "Cluster pronto. Namespace:" -ForegroundColor Green
kubectl get namespaces --context "kind-$clusterName" -l app.kubernetes.io/part-of=procionemgr
