<#
.SYNOPSIS
    Avvia lo stack di observability locale (Grafana + Loki + Prometheus + OTel Collector).

.DESCRIPTION
    - docker compose up -d su infra/observability/docker-compose.yml.
    - Solo sviluppo locale: nessun segreto reale, credenziali Grafana fittizie (admin/procione-local).
    - L'app esporta telemetria SOLO se Observability:Enabled=true (default OFF, opt-in).

.NOTES
    Uso:     .\scripts\observability-up.ps1
    Arresto: .\scripts\observability-down.ps1
    Grafana: http://localhost:3000 (admin / procione-local)
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repoRoot "infra\observability\docker-compose.yml"

docker compose -f $composeFile up -d
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Stack observability avviato:" -ForegroundColor Green
Write-Host "  Grafana    : http://localhost:3000  (admin / procione-local)" -ForegroundColor Cyan
Write-Host "  Prometheus : http://localhost:9090" -ForegroundColor Cyan
Write-Host "  OTLP gRPC  : localhost:4317  (endpoint per Observability:OtlpEndpoint)" -ForegroundColor Cyan
Write-Host ""
Write-Host "Per esportare telemetria dall'app: Observability:Enabled=true (appsettings locale o env Observability__Enabled)." -ForegroundColor Yellow
