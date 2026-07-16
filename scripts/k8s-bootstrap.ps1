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
} else {
    Write-Host "Creo il cluster kind '$clusterName'..." -ForegroundColor Cyan
    kind create cluster --name $clusterName --config $kindConfig
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

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
