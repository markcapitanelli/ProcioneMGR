<#
.SYNOPSIS
    Avvia ProcioneMGR su PostgreSQL in ambiente Production.

.DESCRIPTION
    - Imposta ASPNETCORE_ENVIRONMENT=Production (carica appsettings.Production.json). PostgreSQL è
      l'unico provider: la connection string PostgresConnection vale in ogni ambiente.
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

Write-Host "Ambiente : $env:ASPNETCORE_ENVIRONMENT (provider: PostgreSQL)" -ForegroundColor Cyan
Write-Host "URL      : $env:ASPNETCORE_URLS" -ForegroundColor Cyan

# --- B2 (2026-07-26): ingestion remota nel cluster kind ---
# Con MarketData:UseRemoteIngestion=true il monolite NON avvia il worker di sync locale: lo
# scheduling vive nel servizio ProcioneMGR.Ingestion in-cluster (mai due scrittori OHLCV sullo
# stesso DB). La sync MANUALE dalla UI passa da http://localhost:18080 => serve il port-forward.
# Best-effort di proposito: cluster giu' = si avvisa e si parte lo stesso (le candele riprendono
# ad avanzare quando il cluster torna; il pulsante di sync manuale dara' errore fino ad allora).
$pfListening = Test-NetConnection -ComputerName localhost -Port 18080 -InformationLevel Quiet -WarningAction SilentlyContinue
if ($pfListening) {
    Write-Host "Ingestion: port-forward 18080 gia' attivo." -ForegroundColor Green
} elseif ((Get-Command kubectl -ErrorAction SilentlyContinue) -and
          (kubectl get svc procionemgr-ingestion -n procionemgr-ingestion --context kind-procionemgr-dev 2>$null)) {
    Start-Process -WindowStyle Hidden kubectl -ArgumentList "port-forward","-n","procionemgr-ingestion","svc/procionemgr-ingestion","18080:8080","--context","kind-procionemgr-dev"
    Write-Host "Ingestion: port-forward 18080 avviato (servizio in-cluster)." -ForegroundColor Green
} else {
    Write-Host "Ingestion: cluster kind non raggiungibile - sync manuale UI indisponibile finche' non torna (il worker e' in-cluster)." -ForegroundColor Yellow
}

# --- B3 (2026-07-26): motore di trading remoto nel cluster kind (core caldo) ---
# Con Trading:UseRemoteTrading=true questo processo e' il GUSCIO: non registra motore, worker,
# feed R1 ne' carry — comanda il servizio procionemgr-trading via gRPC su localhost:18092.
# NB: a differenza dell'ingestion questo port-forward e' NECESSARIO alla pagina /trading (senza,
# la UI mostra errori di connessione — il core continua a operare da solo, e' il punto di B3).
# Delegato a ensure-trading-portforward.ps1 (2026-08-11): il blocco inline che stava qui
# controllava solo "porta in ascolto" — il tunnel stantio (pod sostituito, container ripartito)
# lo riconosce solo lo script dedicato, che apre anche la porta health 18093 per il watchdog.
& (Join-Path $PSScriptRoot 'ensure-trading-portforward.ps1')
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
