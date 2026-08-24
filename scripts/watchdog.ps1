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
#    2. motore  : GET  http://localhost:18093/health (la porta HEALTH del servizio, 8081, via
#                 port-forward; se giu', PRIMA prova a ripararlo con
#                 ensure-trading-portforward.ps1 e ricontrolla — il tunnel stantio e' di gran
#                 lunga la causa piu' frequente, non il motore morto).
#                 MAI la 18092: quella e' la 8080 gRPC h2c-only, che a un GET HTTP/1.x risponde
#                 400 SEMPRE — il check puntava li' ed era strutturalmente incapace di dire
#                 "motore sano", scoperto il 2026-08-11 (l'anti-spam sulle transizioni aveva
#                 zittito il falso "giu'" permanente).
#    3. Postgres: TCP  localhost:5432
#    4. backup  : eta' del dump piu' recente nella cartella notturna (sezione "Backup" di
#                 appsettings.json; default %USERPROFILE%\ProcioneMGR-Backup, soglia 48h).
#                 Non e' un servizio da pingare: e' l'unico controllo che vede il caso in cui il
#                 task notturno NON PARTE proprio — e quindi non puo' lamentarsi da solo.
#                 Cartella e soglia NON sono scritte qui: dal 2026-08-23 la fonte e' una sola,
#                 condivisa con db-backup.ps1 e /admin/backup (vedi Get-BackupWatch).
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

# ---------------------------------------------------------------------------------------------
#  Dove finiscono i backup, e da che eta' sono "vecchi": si legge dalla sezione "Backup" di
#  appsettings.json, la STESSA che leggono db-backup.ps1 e /admin/backup.
#
#  PERCHE' (2026-08-23): questa cartella e questa soglia erano scritte a mano in tre posti — qui,
#  nello script di backup e (implicitamente) nella pagina, che guardava tutt'altra cartella e per
#  questo dichiarava fermo un backup sano. Con tre copie, cambiare la destinazione da un solo
#  posto significa che gli altri due continuano a guardare dove non c'e' piu' niente: il watchdog
#  griderebbe "backup vecchio" ogni cinque minuti su un backup di stanotte, e dopo il terzo falso
#  allarme nessuno leggerebbe piu' nemmeno quelli veri.
#
#  Come tutto il resto di questo script, non deve poter fallire: senza il file, senza la sezione o
#  con un JSON rotto restano i default storici.
function Get-BackupWatch([string]$root) {
    $cfg = @{
        Directory       = (Join-Path $env:USERPROFILE 'ProcioneMGR-Backup')
        StaleAfterHours = 48
    }
    $settings = Join-Path $root 'ProcioneMGR\appsettings.json'
    if (-not (Test-Path $settings)) { return $cfg }
    try { $section = (Get-Content $settings -Raw | ConvertFrom-Json).Backup } catch { return $cfg }
    if (-not $section) { return $cfg }
    if (-not [string]::IsNullOrWhiteSpace($section.NightlyDirectory)) { $cfg.Directory = $section.NightlyDirectory.Trim() }
    if ($section.StaleAfterHours -ge 1) { $cfg.StaleAfterHours = [int]$section.StaleAfterHours }
    return $cfg
}

$backupWatch = Get-BackupWatch $repoRoot

function Read-State {
    $defaults = [ordered]@{ shell = $true; engine = $true; postgres = $true; backup = $true }
    if (Test-Path $stateFile) {
        try {
            $s = Get-Content $stateFile -Raw | ConvertFrom-Json
            # Chiavi nate DOPO il file di stato (es. 'backup', 2026-08-17): senza riempirle, la
            # prima esecuzione le leggerebbe $null, cioe' "era GUASTO", e sparerebbe una falsa
            # notifica di ripristino su qualcosa che non era mai stato giu'.
            foreach ($k in $defaults.Keys) {
                if ($null -eq $s.PSObject.Properties[$k]) {
                    $s | Add-Member -NotePropertyName $k -NotePropertyValue $defaults[$k]
                }
            }
            return $s
        } catch { }
    }
    return [pscustomobject]$defaults
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

# --- [Fase 5] Riconoscimento dell'assetto ----------------------------------------------------
# Con l'assetto Docker Compose (progetto 'procionemgr', nome fissato nel docker-compose.yml) il
# DB non e' pubblicato sull'host e il motore non ha tunnel: i controlli 2 e 3 passano dallo STATO
# DEI CONTAINER, non dalla rete. Il controllo del guscio resta identico (stessa 5199).
function Get-ComposeService([string]$service) {
    docker ps --filter 'label=com.docker.compose.project=procionemgr' `
        --filter "label=com.docker.compose.service=$service" --filter 'status=running' `
        --format '{{.Names}}' 2>$null
}
$composeMode = [bool](Get-ComposeService 'ui')

# --- 1. Guscio -------------------------------------------------------------------------------
$shellOk = Test-Http 'http://localhost:5199/health'

if ($composeMode) {
    # --- 2c. Motore: solo se il profilo engine fa parte dell'assetto (container esistente,
    # anche se fermo). Assente del tutto = non previsto, nessun allarme.
    $engineDefined = docker ps -a --filter 'label=com.docker.compose.project=procionemgr' `
        --filter 'label=com.docker.compose.service=trading' --format '{{.Names}}' 2>$null
    $engineOk = if ($engineDefined) { [bool](Get-ComposeService 'trading') } else { $true }

    # --- 3c. Postgres: container running (la porta non e' pubblicata di proposito).
    $pgOk = [bool](Get-ComposeService 'postgres')

    $shellFix = 'docker compose up -d; se insiste: docker logs del container ui.'
    $engineFix = 'docker compose --profile engine up -d (di norma il restart: always lo rialza da solo).'
    $pgFix = 'docker compose up -d postgres; se insiste: docker logs del container postgres.'
}
else {
    # --- 2. Motore (con auto-riparazione del tunnel prima di gridare) ------------------------
    # 18093 = porta health (8081) del servizio; la 18092 e' gRPC e a HTTP/1.x risponde sempre 400.
    $engineOk = Test-Http 'http://localhost:18093/health'
    if (-not $engineOk) {
        $ensure = Join-Path $repoRoot 'scripts\ensure-trading-portforward.ps1'
        if (Test-Path $ensure) {
            & $ensure | Out-Null
            Start-Sleep -Seconds 3
            $engineOk = Test-Http 'http://localhost:18093/health'
        }
    }

    # --- 3. Postgres -------------------------------------------------------------------------
    $pgOk = Test-Tcp 'localhost' 5432

    $shellFix = 'scripts\bringup.ps1 lo rilancia; oppure run-postgres.ps1 a mano.'
    $engineFix = 'tunnel gia'' ritentato; se persiste guarda il pod procionemgr-trading nel cluster.'
    $pgFix = 'verifica il servizio PostgreSQL locale (porta 5432).'
}

# --- 4. Freschezza dei backup ----------------------------------------------------------------
#  PERCHE' (2026-08-17): il dump notturno ha fallito SEI notti di fila senza che nessuno se ne
#  accorgesse — il task usciva 1 e quel codice non lo legge nessuno. Da oggi db-backup.ps1 avvisa
#  quando fallisce, ma non puo' avvisare quando NON PARTE: task disabilitato, azione che punta a
#  uno script sparito, PC spento all'ora giusta. L'unica prova che regge in tutti i casi e' l'ETA'
#  del dump piu' recente, e va guardata DA FUORI — cioe' da qui.
#  Indipendente dall'assetto: i backup sono sempre sul disco dell'host (vedi db-backup.ps1).
$backupDir = $backupWatch.Directory
$lastBackup = Get-ChildItem -Path $backupDir -Filter 'procionemgr-*.dump' -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
# 48h e non 24h: una notte saltata (PC spento) non e' un guasto, due lo sono. La soglia e' quella
# CONFIGURATA (sezione Backup), la stessa che usano db-backup.ps1 -Verify e /admin/backup: tre
# strumenti che guardano lo stesso fatto non possono avere tre soglie proprie, o prima o poi danno
# tre verdetti diversi sullo stesso backup.
$backupOk = ($null -ne $lastBackup) -and (((Get-Date) - $lastBackup.LastWriteTime).TotalHours -le $backupWatch.StaleAfterHours)
$backupFix = 'lancia scripts\db-backup.ps1 a mano e LEGGI l''errore; poi controlla il task "ProcioneMGR Backup DB".'
# "GIU'" non descrive un backup vecchio: qui l'allarme deve dire DA QUANTO, altrimenti non si
# capisce se e' una notte saltata o un guasto che dura da una settimana.
$backupDown = if ($lastBackup) {
    "backup DB FERMO da $([math]::Round(((Get-Date) - $lastBackup.LastWriteTime).TotalHours, 0)) ore (ultimo: $($lastBackup.Name))"
} else {
    "backup DB MAI eseguito (nessun dump in $backupDir)"
}

# --- Transizioni: una notifica per cambio di stato, in entrambe le direzioni -----------------
$checks = @(
    @{ Name = 'guscio';  Now = $shellOk;  Was = [bool]$previous.shell;    Fix = $shellFix },
    @{ Name = 'motore';  Now = $engineOk; Was = [bool]$previous.engine;   Fix = $engineFix },
    @{ Name = 'Postgres'; Now = $pgOk;    Was = [bool]$previous.postgres; Fix = $pgFix },
    @{ Name = 'backup';  Now = $backupOk; Was = [bool]$previous.backup;   Fix = $backupFix
       Down = $backupDown; Up = 'backup DB di nuovo aggiornato' }
)

foreach ($c in $checks) {
    if ($c.Was -and -not $c.Now) {
        $what = if ($c.Down) { $c.Down } else { "$($c.Name) GIU'" }
        Send-Telegram "[$now] WATCHDOG: $what. $($c.Fix)"
    } elseif (-not $c.Was -and $c.Now) {
        $what = if ($c.Up) { $c.Up } else { "$($c.Name) di nuovo raggiungibile" }
        Send-Telegram "[$now] Watchdog: $what."
    }
    $label = if ($c.Now) { 'OK' } else { 'GIU''' }
    Write-Host ("Watchdog : {0,-8} {1}" -f $c.Name, $label)
}

Save-State ([pscustomobject]@{ shell = $shellOk; engine = $engineOk; postgres = $pgOk; backup = $backupOk })
exit 0
