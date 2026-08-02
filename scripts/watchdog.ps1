# =============================================================================================
#  [AF5.2] Dead-man's-switch ESTERNO della piattaforma (roadmap Autonomia Finanziaria).
#
#  PERCHE' ESISTE: l'heartbeat incrociato fra guscio e motore (AF5.1) copre la morte di UNO dei
#  due processi — ma se muoiono entrambi, o muore Postgres, o il PC si riavvia e nulla riparte,
#  nessun processo interno puo' avvisare. Questo script gira da Task Scheduler OGNI 5 MINUTI,
#  fuori dall'app, e manda Telegram DIRETTAMENTE via Bot API: sopravvive a tutto tranne che al
#  PC spento (per quello c'e' il digest giornaliero — la sua ASSENZA all'ora attesa e' l'allarme).
#
#  Cosa controlla:
#    1. guscio  : GET  http://localhost:5199/health
#    2. motore  : GET  http://localhost:18092/health (via port-forward; se giu', PRIMA prova a
#                 ripararlo con ensure-trading-portforward.ps1 e ricontrolla — il tunnel stantio
#                 e' di gran lunga la causa piu' frequente, non il motore morto)
#    3. Postgres: TCP  localhost:5432
#
#  Anti-spam: notifica UNA volta per transizione (OK->GUASTO e GUASTO->OK), mai a raffica. Lo
#  stato fra le esecuzioni vive in %TEMP%\procionemgr-watchdog-state.json (stato di macchina,
#  non configurazione da versionare).
#
#  PREREQUISITI (variabili d'ambiente DI MACCHINA, mai committate):
#    TELEGRAM_BOT_TOKEN  - lo stesso token del bot @ProcioneMGR_Bot usato dall'app
#    TELEGRAM_CHAT_ID    - la chat a cui scrivere (lo stesso ChatId di Notifications:Telegram)
#  Senza token/chat lo script logga soltanto: MAI un fallimento silenzioso spacciato per sano.
#
#  REGISTRAZIONE (una volta, da shell elevata):
#    .\scripts\watchdog.ps1 -Register
#  crea il task "ProcioneMGR Watchdog" che esegue questo script ogni 5 minuti.
# =============================================================================================
param(
    [switch]$Register
)

$ErrorActionPreference = 'Continue'

if ($Register) {
    # -ErrorAction Stop + try/catch: senza, un "Accesso negato" (cmdlet non terminante) scivolava
    # sotto il messaggio di successo — un controllo che rassicura, scoperto DAL VIVO il 2026-08-02
    # registrando il task gemello del bring-up. Il verdetto ora è la VERIFICA (Get-ScheduledTask),
    # non l'assenza di eccezioni.
    try {
        $scriptPath = $MyInvocation.MyCommand.Path
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
        $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
            -RepetitionInterval (New-TimeSpan -Minutes 5)
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 4)
        Register-ScheduledTask -TaskName 'ProcioneMGR Watchdog' -Action $action -Trigger $trigger `
            -Settings $settings -Description 'Dead-man switch esterno ProcioneMGR (AF5.2): guscio, motore, Postgres ogni 5 minuti.' `
            -Force -ErrorAction Stop | Out-Null
    } catch {
        Write-Host "Watchdog : registrazione FALLITA: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
    if (Get-ScheduledTask -TaskName 'ProcioneMGR Watchdog' -ErrorAction SilentlyContinue) {
        Write-Host "Watchdog : task 'ProcioneMGR Watchdog' registrato e VERIFICATO (ogni 5 minuti)." -ForegroundColor Green
        exit 0
    }
    Write-Host "Watchdog : Register-ScheduledTask non ha lanciato ma il task NON esiste - registrazione fallita." -ForegroundColor Red
    exit 1
}

$stateFile = Join-Path $env:TEMP 'procionemgr-watchdog-state.json'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-State {
    if (Test-Path $stateFile) {
        try { return Get-Content $stateFile -Raw | ConvertFrom-Json } catch { }
    }
    return [pscustomobject]@{ shell = $true; engine = $true; postgres = $true }
}

function Save-State($state) {
    try { $state | ConvertTo-Json | Out-File $stateFile -Encoding utf8 } catch { }
}

function Test-Http([string]$url) {
    try {
        $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
        return $resp.StatusCode -eq 200
    } catch { return $false }
}

function Test-Tcp([string]$targetHost, [int]$port) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $async = $client.BeginConnect($targetHost, $port, $null, $null)
        $ok = $async.AsyncWaitHandle.WaitOne(5000)
        if ($ok -and $client.Connected) { $client.Close(); return $true }
        $client.Close(); return $false
    } catch { return $false }
}

function Send-Telegram([string]$text) {
    $token = $env:TELEGRAM_BOT_TOKEN
    $chatId = $env:TELEGRAM_CHAT_ID
    if ([string]::IsNullOrWhiteSpace($token) -or [string]::IsNullOrWhiteSpace($chatId)) {
        Write-Host "Watchdog : TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID mancanti - notifica NON inviata: $text" -ForegroundColor Yellow
        return
    }
    try {
        # Il token sta nel PATH dell'URL (semantica dell'API Telegram): mai loggarla per intero.
        $body = @{ chat_id = $chatId; text = $text }
        Invoke-RestMethod -Uri "https://api.telegram.org/bot$token/sendMessage" -Method Post -Body $body -TimeoutSec 15 | Out-Null
        Write-Host "Watchdog : Telegram inviato." -ForegroundColor Green
    } catch {
        Write-Host "Watchdog : invio Telegram FALLITO: $($_.Exception.Message)" -ForegroundColor Red
    }
}

$previous = Read-State
$now = Get-Date -Format 'yyyy-MM-dd HH:mm'

# --- 1. Guscio -------------------------------------------------------------------------------
$shellOk = Test-Http 'http://localhost:5199/health'

# --- 2. Motore (con auto-riparazione del tunnel prima di gridare) ----------------------------
$engineOk = Test-Http 'http://localhost:18092/health'
if (-not $engineOk) {
    $ensure = Join-Path $repoRoot 'scripts\ensure-trading-portforward.ps1'
    if (Test-Path $ensure) {
        & $ensure | Out-Null
        Start-Sleep -Seconds 3
        $engineOk = Test-Http 'http://localhost:18092/health'
    }
}

# --- 3. Postgres -----------------------------------------------------------------------------
$pgOk = Test-Tcp 'localhost' 5432

# --- Transizioni: una notifica per cambio di stato, in entrambe le direzioni -----------------
$checks = @(
    @{ Name = 'guscio';  Now = $shellOk;  Was = [bool]$previous.shell;    Fix = 'scripts\bringup.ps1 lo rilancia; oppure run-postgres.ps1 a mano.' },
    @{ Name = 'motore';  Now = $engineOk; Was = [bool]$previous.engine;   Fix = 'tunnel gia'' ritentato; se persiste guarda il pod procionemgr-trading nel cluster.' },
    @{ Name = 'Postgres'; Now = $pgOk;    Was = [bool]$previous.postgres; Fix = 'verifica il servizio PostgreSQL locale (porta 5432).' }
)

foreach ($c in $checks) {
    if ($c.Was -and -not $c.Now) {
        Send-Telegram "[$now] WATCHDOG: $($c.Name) GIU'. $($c.Fix)"
    } elseif (-not $c.Was -and $c.Now) {
        Send-Telegram "[$now] Watchdog: $($c.Name) di nuovo raggiungibile."
    }
    $label = if ($c.Now) { 'OK' } else { 'GIU''' }
    Write-Host ("Watchdog : {0,-8} {1}" -f $c.Name, $label)
}

Save-State ([pscustomobject]@{ shell = $shellOk; engine = $engineOk; postgres = $pgOk })
exit 0
