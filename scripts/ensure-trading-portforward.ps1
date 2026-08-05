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
#  Idempotente: se il tunnel c'e' gia' ED E' ANCORA BUONO non fa nulla. Non fallisce mai in modo
#  bloccante — se il cluster non c'e', lo dice e lascia partire l'app comunque (il resto della
#  piattaforma funziona).
#
#  --- LA PORTA IN ASCOLTO NON BASTA (2026-07-29) ----------------------------------------------
#  Fino a oggi il controllo era "la 18092 e' in ascolto? allora c'e' gia'". E' insufficiente:
#  quando il pod viene SOSTITUITO (deploy, OOM-kill, rollout) kubectl resta in ascolto sulla porta
#  locale ma il tunnel punta a un pod che non esiste piu'. Lo script diceva "gia' attivo", il guscio
#  otteneva Unavailable, e /trading e /admin/protections mostravano il motore irraggiungibile finche'
#  qualcuno non ricreava il tunnel a mano. Successo DUE VOLTE lo stesso giorno, sui due deploy del
#  motore.
#
#  Il rimedio NON e' una sonda di rete: una connessione TCP verso un forward morto viene accettata
#  in locale e muore dopo, quindi qualunque euristica sul socket e' fragile. Si registra invece il
#  POD a cui il tunnel e' stato aperto e lo si confronta con quello vivo adesso — confronto
#  deterministico, nessuna inferenza sul comportamento della rete.
# =============================================================================================

$ErrorActionPreference = 'Continue'

$port = 18092
$context = 'kind-procionemgr-dev'
$namespace = 'procionemgr-trading'
$service = 'svc/procionemgr-trading'

# Traccia del pod servito dal tunnel corrente. In TEMP e non nel repo: e' stato di macchina, non
# configurazione da versionare.
$marker = Join-Path $env:TEMP 'procionemgr-trading-portforward.pod'

function Test-PortListening([int]$p) {
    return [bool](Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue)
}

# Il selettore e' quello del SERVICE (app.kubernetes.io/component=trading), non un "app=..."
# inventato: cosi' il pod che si misura e' esattamente quello a cui il port-forward instrada.
#
# --- IL NOME DEL POD NON BASTA (2026-08-05) ---------------------------------------------------
# Il confronto sul solo NOME copriva il caso "pod sostituito" ma era cieco a quello piu' frequente:
# il RESTART DEL CONTAINER dentro lo stesso pod (OOM-kill, crash, liveness fallita). Il pod
# mantiene nome e identita', il tunnel di kubectl muore lo stesso, e questo script rispondeva
# "gia' attivo". Successo davvero: pod procionemgr-trading-9b875dd78-n6v4c con RESTARTS 2, tunnel
# morto da 8 ore, /trading mostrava ZERO corsie mentre il motore in cluster stava operando -- e la
# porta locale risultava regolarmente in ascolto. Ora l'identita' del tunnel e' la coppia
# NOME + CONTEGGIO RESTART: se il container e' ripartito, il tunnel si rifa'.
function Get-CurrentPodIdentity {
    $out = kubectl get pods -n $namespace --context $context `
        -l app.kubernetes.io/component=trading `
        --field-selector status.phase=Running `
        -o jsonpath='{.items[0].metadata.name}|{.items[0].status.containerStatuses[0].restartCount}' 2>$null
    if ([string]::IsNullOrWhiteSpace($out)) { return $null }
    $parts = "$out".Trim().Split('|')
    if ($parts.Count -lt 1 -or [string]::IsNullOrWhiteSpace($parts[0])) { return $null }
    # Il conteggio puo' mancare (container non ancora avviato): si tratta come 0 invece che come
    # ignoto -- al giro dopo, quando esiste, un valore diverso fara' semplicemente rifare il tunnel.
    $restarts = if ($parts.Count -ge 2 -and $parts[1]) { $parts[1] } else { '0' }
    return "$($parts[0])#$restarts"
}

function Stop-StalePortForward([int]$p) {
    foreach ($c in @(Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue)) {
        try { Stop-Process -Id $c.OwningProcess -Force -ErrorAction Stop } catch { }
    }
    # Il socket impiega un istante a liberarsi: senza questa attesa il port-forward nuovo puo'
    # trovare la porta ancora occupata e morire subito.
    for ($i = 0; $i -lt 10; $i++) {
        if (-not (Test-PortListening $p)) { return }
        Start-Sleep -Milliseconds 300
    }
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

$currentPod = Get-CurrentPodIdentity

if (Test-PortListening $port) {
    $servedPod = if (Test-Path $marker) { (Get-Content $marker -Raw).Trim() } else { '' }

    if ($currentPod -and $servedPod -eq $currentPod) {
        Write-Host "Trading  : port-forward $port gia' attivo verso $($currentPod.Split('#')[0]) (restart $($currentPod.Split('#')[1]))." -ForegroundColor Green
        exit 0
    }

    # Il caso che prima passava inosservato -- ora comprende anche il solo restart del container,
    # che lascia il nome del pod invariato ma uccide il tunnel.
    $detail = if ($servedPod) { "serviva $servedPod, ora c'e' $currentPod" } else { "pod servito sconosciuto" }
    Write-Host "Trading  : port-forward $port STANTIO ($detail) - lo ricreo." -ForegroundColor Yellow
    Stop-StalePortForward $port
}

Start-Process -WindowStyle Hidden kubectl -ArgumentList `
    'port-forward', '-n', $namespace, $service, "${port}:8080", '--context', $context

# Il tunnel impiega un attimo ad aprirsi: si aspetta e si VERIFICA, invece di dare per scontato
# che l'avvio del processo equivalga alla porta in ascolto.
for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Milliseconds 500
    if (Test-PortListening $port) {
        # Si annota QUALE pod sta servendo: e' il dato che al prossimo giro distingue "gia' attivo"
        # da "attivo verso un pod che non c'e' piu'".
        if ($currentPod) { Set-Content -Path $marker -Value $currentPod -Encoding utf8 }
        Write-Host "Trading  : port-forward $port avviato verso $currentPod." -ForegroundColor Green
        exit 0
    }
}

Write-Host "Trading  : port-forward $port avviato ma la porta non risulta in ascolto - controlla il cluster." -ForegroundColor Yellow
exit 0
