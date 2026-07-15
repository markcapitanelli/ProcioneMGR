<#
.SYNOPSIS
    Arresta lo stack di observability locale.

.DESCRIPTION
    docker compose down su infra/observability/docker-compose.yml.
    Con -Purge rimuove anche i volumi (dati Prometheus/Loki/Grafana persi — sono solo dati
    di telemetria locale, nessun dato applicativo).

.NOTES
    Uso: .\scripts\observability-down.ps1 [-Purge]
#>

param([switch]$Purge)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repoRoot "infra\observability\docker-compose.yml"

if ($Purge) {
    docker compose -f $composeFile down -v
} else {
    docker compose -f $composeFile down
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Stack observability arrestato." -ForegroundColor Green
