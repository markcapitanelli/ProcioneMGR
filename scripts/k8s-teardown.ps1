<#
.SYNOPSIS
    Elimina il cluster kind locale "procionemgr-dev".

.DESCRIPTION
    Reversibilità totale della Fase 0: rimuove il cluster e tutto il suo contenuto,
    senza lasciare residui (il cluster kind vive interamente in container Docker).

.NOTES
    Uso: .\scripts\k8s-teardown.ps1
#>

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\k8s-common.ps1"
$clusterName = $script:KindClusterName

if (-not (Get-Command kind -ErrorAction SilentlyContinue)) {
    Write-Host "ERRORE: 'kind' non trovato nel PATH." -ForegroundColor Red
    exit 1
}

if (Test-KindCluster $clusterName) {
    Write-Host "Elimino il cluster '$clusterName'..." -ForegroundColor Cyan
    kind delete cluster --name $clusterName
    Write-Host "Cluster eliminato." -ForegroundColor Green
} else {
    Write-Host "Cluster '$clusterName' non esistente: niente da fare." -ForegroundColor Yellow
}
