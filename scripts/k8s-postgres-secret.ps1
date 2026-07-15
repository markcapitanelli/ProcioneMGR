<#
.SYNOPSIS
    Crea il Secret 'postgres-conn' nei namespace che ne hanno bisogno (Fase 1 microservizi).

.DESCRIPTION
    Il Secret NON è committato: contiene la connection string PostgreSQL (con password). Questo
    script lo crea a runtime dal valore passato o dalla env ConnectionStrings__PostgresConnection.
    Da un pod nel cluster kind verso il Postgres dell'host di sviluppo, usare host.docker.internal
    come Host nella connection string (localhost dentro il pod non raggiunge l'host).

.PARAMETER ConnectionString
    La connection string PostgreSQL. Se omessa, si legge da $env:ConnectionStrings__PostgresConnection.

.NOTES
    Uso: .\scripts\k8s-postgres-secret.ps1 -ConnectionString "Host=host.docker.internal;Port=5432;Database=procionemgr;Username=procione;Password=..."
    I namespace devono già esistere (scripts\k8s-bootstrap.ps1).
#>

param([string]$ConnectionString)

$ErrorActionPreference = "Stop"
$clusterCtx = "kind-procionemgr-dev"
$namespaces = @("procionemgr-pipeline", "procionemgr-supervisor", "procionemgr-ingestion")

if (-not $ConnectionString) { $ConnectionString = $env:ConnectionStrings__PostgresConnection }
if (-not $ConnectionString) {
    Write-Host "ERRORE: passa -ConnectionString oppure imposta \$env:ConnectionStrings__PostgresConnection." -ForegroundColor Red
    exit 1
}

foreach ($ns in $namespaces) {
    Write-Host "Creo/aggiorno Secret 'postgres-conn' in $ns..." -ForegroundColor Cyan
    # --dry-run=client | apply: idempotente (crea o aggiorna senza errore se già esiste).
    kubectl create secret generic postgres-conn `
        --namespace $ns `
        --from-literal=ConnectionStrings__PostgresConnection=$ConnectionString `
        --dry-run=client -o yaml --context $clusterCtx | kubectl apply --context $clusterCtx -f -
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Secret 'postgres-conn' pronto nei namespace: $($namespaces -join ', ')." -ForegroundColor Green
