# =============================================================================================
#  [AF5.3] Bring-up IDEMPOTENTE della piattaforma dopo un riavvio del PC (roadmap Autonomia
#  Finanziaria). Ordina in un solo posto cio' che prima era sparso fra memoria, docs e mani:
#
#    1. attesa di Docker Desktop (al boot impiega minuti; senza, tutto il resto e' inutile)
#    2. proxy kind-apiproxy: Windows RISERVA la porta dell'API server dopo un riavvio e Docker
#       non ripristina il binding (docker restart NON basta — visto il 2026-07-27, due volte).
#       Rimedio permanente: container socat con --restart unless-stopped che ripubblica 6443 del
#       nodo su 127.0.0.1:16443 + kubectl config set-cluster. Qui si verifica che l'API server
#       RISPONDA attraverso il proxy (running non basta: l'IP del nodo cambia a ogni riavvio di
#       Docker, 2026-08-04 e 2026-08-11) e, se non risponde, si ricrea puntando al nome DNS.
#    3. attesa del nodo kind Ready e del pod di trading Running (il core caldo riparte da solo:
#       qui si aspetta, non si comanda)
#    4. port-forward: motore 18092 (ensure-trading-portforward.ps1, con il controllo del pod
#       stantio) e ingestion 18080 (best-effort)
#    5. guscio: se la 5199 non risponde, lancia scripts\run-postgres.ps1 in una shell separata
#
#  PREREQUISITI UNA-TANTUM (mai fatti da questo script, vedi infra/k8s/README.md):
#    - cluster kind creato (scripts\k8s-bootstrap.ps1) e Secret popolati con gli script dedicati
#      k8s-postgres-secret.ps1 / k8s-trading-secret.ps1 / k8s-ui-secret.ps1 (i NOMI delle chiavi
#      vivono la' dentro: non inventarli)
#    - Docker Desktop impostato per partire al logon
#    - PostgreSQL locale come servizio Windows (parte da solo)
#
#  REGISTRAZIONE (una volta, da shell elevata):
#    .\scripts\bringup.ps1 -Register
#  crea il task "ProcioneMGR BringUp" che esegue questo script al LOGON dell'utente.
#
#  Non fallisce mai in modo bloccante: ogni passo dice cosa manca e si prosegue col possibile —
#  il watchdog (watchdog.ps1, ogni 5') segnala su Telegram cio' che resta giu'.
# =============================================================================================
param(
    [switch]$Register
)

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$context = 'kind-procionemgr-dev'
$proxyName = 'kind-apiproxy'
$proxyPort = 16443
$logFile = Join-Path $env:TEMP 'procionemgr-bringup.log'

if ($Register) {
    # Il trigger -AtLogOn richiede una shell ELEVATA: senza, Register-ScheduledTask fallisce con
    # "Accesso negato" — e la prima versione di questo blocco stampava comunque "registrato"
    # (l'errore è non terminante). Scoperto dal vivo il 2026-08-02. Ora: -ErrorAction Stop, e su
    # fallimento si RIPIEGA da soli sulla cartella Esecuzione automatica, che non chiede privilegi
    # e produce lo stesso effetto (partenza al logon dell'utente).
    $scriptPath = $MyInvocation.MyCommand.Path
    try {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
        $trigger = New-ScheduledTaskTrigger -AtLogOn
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
        Register-ScheduledTask -TaskName 'ProcioneMGR BringUp' -Action $action -Trigger $trigger `
            -Settings $settings -Description 'Bring-up ProcioneMGR al logon (AF5.3): Docker, proxy kind, port-forward, guscio.' `
            -Force -ErrorAction Stop | Out-Null
        Write-Host "BringUp  : task 'ProcioneMGR BringUp' registrato e VERIFICATO (al logon)." -ForegroundColor Green
        exit 0
    } catch {
        Write-Host "BringUp  : Register-ScheduledTask fallito ($($_.Exception.Message.Trim())) - ripiego sulla cartella Esecuzione automatica." -ForegroundColor Yellow
        $startup = [Environment]::GetFolderPath('Startup')
        $cmd = Join-Path $startup 'ProcioneMGR-BringUp.cmd'
        Set-Content -Path $cmd -Encoding ascii -Value "@echo off`r`nstart `"`" /min powershell -NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
        if (Test-Path $cmd) {
            Write-Host "BringUp  : collegamento creato e VERIFICATO in Esecuzione automatica ($cmd)." -ForegroundColor Green
            exit 0
        }
        Write-Host "BringUp  : anche il ripiego e' fallito - registra a mano da una shell elevata." -ForegroundColor Red
        exit 1
    }
}

function Log([string]$msg, [string]$color = 'Gray') {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg
    Write-Host $line -ForegroundColor $color
    try { Add-Content -Path $logFile -Value $line -Encoding utf8 } catch { }
}

Log "=== BringUp avviato ===" 'Cyan'

# --- 1. Docker -------------------------------------------------------------------------------
$dockerUp = $false
for ($i = 0; $i -lt 60; $i++) {
    docker info *> $null
    if ($LASTEXITCODE -eq 0) { $dockerUp = $true; break }
    if ($i -eq 0) { Log "Docker   : non ancora pronto, attendo (fino a 10 minuti)..." 'Yellow' }
    Start-Sleep -Seconds 10
}
if (-not $dockerUp) {
    Log "Docker   : NON disponibile dopo 10 minuti - mi fermo qui (il watchdog avvisera')." 'Red'
    exit 0
}
Log "Docker   : pronto." 'Green'

# --- 1b. [Fase 5] Assetto Docker Compose gia' attivo? -----------------------------------------
# Se il progetto compose 'procionemgr' ha il guscio in esecuzione, la piattaforma e' SERVITA da
# compose (restart: always la fa ripartire da sola col demone): questo bring-up non deve fare
# nulla -- e soprattutto NON deve lanciare un secondo guscio sulla 5199. Un solo scrittore, sempre.
$composeUi = docker ps --filter 'label=com.docker.compose.project=procionemgr' `
    --filter 'label=com.docker.compose.service=ui' --filter 'status=running' --format '{{.Names}}' 2>$null
if ($composeUi) {
    Log "Compose  : assetto compose attivo ($composeUi, restart: always) - il bring-up kind non serve, esco." 'Green'
    exit 0
}

# --- 2. Proxy kind-apiproxy (la porta riservata di Windows) ----------------------------------
# "Container running" NON basta (2026-08-11): il socat riparte con Docker ma inoltra all'IP che
# il nodo aveva PRIMA del riavvio, e Docker riassegna gli IP della rete kind a ogni avvio.
# Successo identico due volte a ruoli invertiti: 2026-08-04 (socat->.3, nodo su .2) e 2026-08-11
# (socat->.2, nodo su .3) — un'ora di "TLS handshake timeout" con proxy e nodo entrambi "sani".
# Il verdetto e' quindi la RISPOSTA dell'API server ATTRAVERSO il proxy; e il proxy si (ri)crea
# puntando al NOME del container sulla rete kind (DNS interno di Docker, stabile), mai all'IP.
$proxyAnswers = $false
try {
    # /livez del kube-apiserver risponde anche anonimo; il certificato e' self-signed, quindi
    # per questa sola sonda si sospende la validazione (PS 5.1 non ha -SkipCertificateCheck).
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
    $resp = Invoke-WebRequest -Uri "https://127.0.0.1:$proxyPort/livez" -UseBasicParsing -TimeoutSec 8
    $proxyAnswers = ($resp.StatusCode -eq 200)
} catch { }
finally {
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $null
}

if ($proxyAnswers) {
    Log "Proxy    : kind-apiproxy attivo e l'API server RISPONDE attraverso il proxy." 'Green'
} else {
    $nodeExists = docker inspect procionemgr-dev-control-plane --format '{{.Name}}' 2>$null
    if ([string]::IsNullOrWhiteSpace("$nodeExists")) {
        Log "Proxy    : nodo kind non trovato - cluster assente? (k8s-bootstrap.ps1 e' il prerequisito)." 'Red'
    } else {
        docker rm -f $proxyName *> $null
        # Nome DNS, non IP: se il nodo sta ancora partendo il socat puo' morire al primo giro,
        # ma --restart unless-stopped lo ripresenta finche' la risoluzione non riesce.
        docker run -d --name $proxyName --network kind --restart unless-stopped `
            -p "127.0.0.1:${proxyPort}:6443" alpine/socat `
            'tcp-listen:6443,fork,reuseaddr' 'tcp-connect:procionemgr-dev-control-plane:6443' *> $null
        Log "Proxy    : kind-apiproxy ricreato verso procionemgr-dev-control-plane:6443 (porta $proxyPort)." 'Green'
    }
}

# kubectl deve puntare al proxy: set-cluster e' idempotente e sopravvive ai riavvii, ma se il
# kubeconfig e' stato rigenerato (kind ricreato) il server torna alla porta riservata morta.
kubectl config set-cluster $context --server="https://127.0.0.1:$proxyPort" *> $null

# --- 3. Cluster e pod di trading -------------------------------------------------------------
$nodeReady = $false
for ($i = 0; $i -lt 30; $i++) {
    $status = kubectl get nodes --context $context -o jsonpath='{.items[0].status.conditions[?(@.type=="Ready")].status}' 2>$null
    if ("$status" -eq 'True') { $nodeReady = $true; break }
    if ($i -eq 0) { Log "Cluster  : attendo il nodo Ready (fino a 5 minuti)..." 'Yellow' }
    Start-Sleep -Seconds 10
}
if ($nodeReady) {
    Log "Cluster  : nodo Ready." 'Green'
    $podReady = $false
    for ($i = 0; $i -lt 30; $i++) {
        $phase = kubectl get pods -n procionemgr-trading --context $context `
            -l app.kubernetes.io/component=trading `
            --field-selector status.phase=Running -o jsonpath='{.items[0].metadata.name}' 2>$null
        if (-not [string]::IsNullOrWhiteSpace("$phase")) { $podReady = $true; break }
        if ($i -eq 0) { Log "Motore   : attendo il pod di trading Running (fino a 5 minuti)..." 'Yellow' }
        Start-Sleep -Seconds 10
    }
    if ($podReady) { Log "Motore   : pod di trading Running." 'Green' }
    else { Log "Motore   : pod di trading NON Running dopo 5 minuti - proseguo, il watchdog avvisera'." 'Red' }
} else {
    Log "Cluster  : nodo NON Ready dopo 5 minuti - proseguo col possibile." 'Red'
}

# --- 4. Port-forward -------------------------------------------------------------------------
& (Join-Path $repoRoot 'scripts\ensure-trading-portforward.ps1')

$ingListening = Get-NetTCPConnection -State Listen -LocalPort 18080 -ErrorAction SilentlyContinue
if (-not $ingListening) {
    $svc = kubectl get svc procionemgr-ingestion -n procionemgr-ingestion --context $context 2>$null
    if ($svc) {
        Start-Process -WindowStyle Hidden kubectl -ArgumentList 'port-forward', '-n', 'procionemgr-ingestion', 'svc/procionemgr-ingestion', '18080:8080', '--context', $context
        Log "Ingestion: port-forward 18080 avviato." 'Green'
    } else {
        Log "Ingestion: servizio non trovato - sync manuale UI indisponibile." 'Yellow'
    }
} else {
    Log "Ingestion: port-forward 18080 gia' attivo." 'Green'
}

# --- 5. Guscio -------------------------------------------------------------------------------
$shellOk = $false
try {
    $resp = Invoke-WebRequest -Uri 'http://localhost:5199/health' -UseBasicParsing -TimeoutSec 5
    $shellOk = ($resp.StatusCode -eq 200)
} catch { }

if ($shellOk) {
    Log "Guscio   : gia' in ascolto su 5199." 'Green'
} else {
    $runScript = Join-Path $repoRoot 'scripts\run-postgres.ps1'
    # Console NASCOSTA, non minimizzata, con l'output su file.
    #
    # PERCHE' (2026-08-23): quella finestra minimizzata NON conteneva l'output di qualcosa, ERA il
    # guscio — chiuderla, e prima o poi qualcuno la chiude, spegne l'applicazione. E' successo
    # poche ore dopo aver spostato le automazioni dentro la plancia: unico rosso del quadro, unica
    # cosa caduta, e nessuno se n'era accorto guardando lo schermo.
    #
    # Non si perde niente: quel testo non lo leggeva nessuno, e adesso e' un file — `procione log
    # guscio`. Il verdetto sulla salute resta /health, non «la finestra c'e'».
    #
    # ANCHE stderr va rediretto (2026-08-28): -RedirectStandardOutput forza UseShellExecute=false,
    # e con quello il guscio EREDITA gli handle standard non rediretti di questo script. Quando
    # bringup gira come lavoro del supervisore, quello stderr E' il pipe del supervisore: il
    # guscio, che vive giorni, lo teneva aperto — e il supervisore, in attesa dell'EOF, e' rimasto
    # appeso con tutti i lavori fermi. Due file distinti perche' Start-Process lo pretende.
    $guscioLog = Join-Path $env:TEMP 'procionemgr-guscio.log'
    $guscioErr = Join-Path $env:TEMP 'procionemgr-guscio.err.log'
    $argomenti = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$runScript`"")
    try {
        Start-Process -WindowStyle Hidden powershell -RedirectStandardOutput $guscioLog `
            -RedirectStandardError $guscioErr -ArgumentList $argomenti -ErrorAction Stop
        Log "Guscio   : avviato con run-postgres.ps1 (nessuna finestra; log in $guscioLog)." 'Green'
    } catch {
        # Il log e' un di piu', il guscio no. Se il file e' occupato (un'istanza precedente che non
        # ha ancora mollato la presa) si parte SENZA redirezione invece di lasciare la piattaforma
        # senza applicazione — un bring-up che fallisce per non aver potuto scrivere un log
        # sarebbe la coda che morde il cane.
        Log "Guscio   : log non scrivibile ($($_.Exception.Message)); avvio senza redirezione." 'Yellow'
        Start-Process -WindowStyle Hidden powershell -ArgumentList $argomenti
        Log "Guscio   : avviato con run-postgres.ps1 (nessuna finestra)." 'Green'
    }
}

Log "=== BringUp completato ===" 'Cyan'
exit 0
