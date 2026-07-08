<#
.SYNOPSIS
    Avvia ProcioneMGR su PostgreSQL in ambiente Production.

.DESCRIPTION
    - Imposta ASPNETCORE_ENVIRONMENT=Production (appsettings.Production.json → Database:Provider=PostgreSQL).
    - NON contiene segreti: la connection string PostgreSQL vive in appsettings.json (chiave
      ConnectionStrings:PostgresConnection) e la API key di Anthropic si legge dalla variabile
      d'ambiente ANTHROPIC_API_KEY (mai committata).
    - Se ANTHROPIC_API_KEY non è impostata, il layer AI di supervisione resta semplicemente inattivo
      (l'app parte lo stesso); il resto della piattaforma funziona normalmente.

.NOTES
    Uso:  .\scripts\run-postgres.ps1
    Per il layer AI:  $env:ANTHROPIC_API_KEY = "sk-ant-..."   (in questa shell, PRIMA di lanciare)
    In produzione vera, imposta ANTHROPIC_API_KEY e la password PostgreSQL come variabili d'ambiente
    di sistema / secret manager, non in file committati.
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "ProcioneMGR"

$env:ASPNETCORE_ENVIRONMENT = "Production"
# Porta HTTP di default; sovrascrivibile esportando ASPNETCORE_URLS prima di lanciare lo script.
if (-not $env:ASPNETCORE_URLS) { $env:ASPNETCORE_URLS = "http://localhost:5199" }

Write-Host "Ambiente : $env:ASPNETCORE_ENVIRONMENT (Database:Provider=PostgreSQL)" -ForegroundColor Cyan
Write-Host "URL      : $env:ASPNETCORE_URLS" -ForegroundColor Cyan
if ($env:ANTHROPIC_API_KEY) {
    Write-Host "Layer AI : ANTHROPIC_API_KEY rilevata (supervisione AI abilitabile via Llm:Enabled)." -ForegroundColor Green
} else {
    Write-Host "Layer AI : ANTHROPIC_API_KEY NON impostata → supervisione AI inattiva (il resto funziona)." -ForegroundColor Yellow
}

Push-Location $project
try {
    dotnet run --project . --no-launch-profile -c Release
}
finally {
    Pop-Location
}
