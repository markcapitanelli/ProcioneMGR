# =============================================================================================
#  Garantisce il port-forward 18092 -> core di trading in-cluster (procionemgr-trading:8080).
#
#  PERCHE' ESISTE: con Trading:UseRemoteTrading=true il monolite e' il GUSCIO e comanda il motore
#  via gRPC su localhost:18092. Senza questo tunnel la pagina /trading mostra
#  "Unavailable" e "DATI TRADING NON AGGIORNATI" — mentre il core continua a operare da solo nel
#  cluster, che e' esattamente il punto dell'architettura core caldo/guscio freddo (B3).
#
#  E' successo davvero il 2026-07-27: dopo uno spegnimento improvviso del PC il tunnel e' morto e
#  /trading e' rimasta in errore. La logica viveva solo dentro scripts/run-postgres.ps1, quindi
#  avviare l'app in qualunque altro modo la saltava. Estratta qui per essere richiamabile da
#  ovunque — inclusi i profili di .claude/launch.json, dove un blocco inline con virgolette
#  annidate non veniva eseguito in modo affidabile.
#
#  Idempotente: se il tunnel c'e' gia' non fa nulla. Non fallisce mai in modo bloccante — se il
#  cluster non c'e', lo dice e lascia partire l'app comunque (il resto della piattaforma funziona).
# =============================================================================================

$ErrorActionPreference = 'Continue'

$port = 18092
$context = 'kind-procionemgr-dev'
$namespace = 'procionemgr-trading'
$service = 'svc/procionemgr-trading'

function Test-PortListening([int]$p) {
    return [bool](Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue)
}

if (Test-PortListening $port) {
    Write-Host "Trading  : port-forward $port gia' attivo (motore in-cluster)." -ForegroundColor Green
    exit 0
}

if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
    Write-Host "Trading  : kubectl non trovato - /trading non potra' comandare il motore." -ForegroundColor Yellow
    exit 0
}

$svc = kubectl get svc procionemgr-trading -n $namespace --context $context 2>$null
if (-not $svc) {
    Write-Host "Trading  : cluster kind non raggiungibile - /trading restera' in errore finche' non torna." -ForegroundColor Yellow
    Write-Host "           Se e' appena stato riavviato Docker, vedi il proxy kind-apiproxy in docs/." -ForegroundColor Yellow
    exit 0
}

Start-Process -WindowStyle Hidden kubectl -ArgumentList `
    'port-forward', '-n', $namespace, $service, "${port}:8080", '--context', $context

# Il tunnel impiega un attimo ad aprirsi: si aspetta e si VERIFICA, invece di dare per scontato
# che l'avvio del processo equivalga alla porta in ascolto.
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Milliseconds 500
    if (Test-PortListening $port) {
        Write-Host "Trading  : port-forward $port avviato (motore in-cluster)." -ForegroundColor Green
        exit 0
    }
}

Write-Host "Trading  : port-forward $port avviato ma la porta non risulta in ascolto - controlla il cluster." -ForegroundColor Yellow
exit 0
