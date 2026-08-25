# =============================================================================================
#  Backup del database ProcioneMGR, dal LATO HOST.
#
#  PERCHE' ESISTE (2026-08-05): il database non aveva alcun backup automatico. Il CronJob
#  dbbackup-nightly nel cluster e' SOSPESO di proposito e da sempre — il suo manifest lo dice:
#  con emptyDir i backup verrebbero creati e persi alla terminazione del pod, quindi resta spento
#  finche' non gli si da' un PersistentVolumeClaim. Risultato: 12,7 milioni di candele, tutto lo
#  storico dei trade e le credenziali cifrate, senza una copia.
#
#  PERCHE' DALL'HOST E NON DAL CLUSTER: Postgres NON e' nel cluster, e' un servizio Windows
#  nativo (localhost:5432). Passare dal cluster per copiarlo significherebbe far dipendere il
#  backup da Docker, da kind, dal port-forward e da un PVC — quattro cose che possono rompersi,
#  per una che non ne aveva bisogno. Da qui pg_dump parla direttamente col server, e il file
#  finisce su un disco che sopravvive alla distruzione del cluster.
#
#  DOVE FINISCONO: la sezione "Backup" di ProcioneMGR\appsettings.json (default:
#  %USERPROFILE%\ProcioneMGR-Backup). Fuori dal repo: un dump contiene la master key cifrata, le
#  credenziali exchange e tutto lo storico.
#
#  PERCHE' LA DESTINAZIONE STA IN appsettings.json E NON PIU' SOLO QUI (2026-08-23): la pagina
#  /admin/backup elencava solo la cartella backup/ dell'app, dove l'ultimo file era del
#  2026-07-09, mentre qui i dump erano giornalieri e sani. Mostrava quindi un allarme falso su un
#  backup funzionante — e avrebbe taciuto allo stesso modo se il backup si fosse fermato davvero.
#  Perche' la pagina possa dire la verita' deve conoscere QUESTA destinazione, e ricopiarla in C#
#  avrebbe creato due verita' che divergono al primo cambio (-Destination e' parametrico). Quindi
#  la fonte e' una sola, l'appsettings del repo principale, e la leggono entrambi. I parametri qui
#  sotto restano come override esplicito per un'esecuzione una tantum.
#
#  USO
#    .\scripts\db-backup.ps1                 esegue un backup adesso
#    .\scripts\db-backup.ps1 -Register       lo programma ogni notte alle 03:30
#    .\scripts\db-backup.ps1 -Verify         controlla lo stato dei backup esistenti, senza farne
#
#  NOTA SUL RESTORE: il formato e' custom (-Fc), quindi si ripristina con pg_restore. Il drill di
#  restore e' gia' stato fatto il 2026-07-26 su un server vergine in Docker — vedi i doc.
# =============================================================================================
param(
    [switch]$Register,
    [switch]$Verify,
    [string]$Destination,
    [int]$KeepDays
)

$ErrorActionPreference = 'Continue'

# ---------------------------------------------------------------------------------------------
#  Radice del repo PRINCIPALE, anche quando questa copia dello script vive in un worktree.
#
#  PERCHE' (incidente del 2026-08-17, sei notti di backup persi): il task notturno era stato
#  registrato standoci DENTRO un worktree, quindi puntava alla copia dello script che sta li'.
#  E 'ProcioneMGR/appsettings.json' e' GITIGNORATO: ogni worktree ne ha una copia propria,
#  fotografata quando il worktree e' nato e mai piu' aggiornata da nessuno. Quando la password di
#  Postgres e' stata ruotata (2026-08-09) il repo principale l'ha recepita, il worktree no — e da
#  quella notte pg_dump usciva 1 con "autenticazione fallita", ogni notte, in silenzio.
#
#  La regola che ne esce: la connessione si legge SEMPRE dal repo principale, che e' l'unica copia
#  che qualcuno tiene davvero aggiornata. Un worktree e' uno scratch, non una fonte di verita'.
function Get-MainRepoRoot([string]$start) {
    $marker = '\.claude\worktrees\'
    $i = $start.IndexOf($marker, [System.StringComparison]::OrdinalIgnoreCase)
    if ($i -ge 0) { return $start.Substring(0, $i) }
    return $start
}

$mainRepoRoot = Get-MainRepoRoot (Split-Path -Parent $PSScriptRoot)
$appSettings = Join-Path $mainRepoRoot 'ProcioneMGR\appsettings.json'

# ---------------------------------------------------------------------------------------------
#  La sezione "Backup" di appsettings.json: destinazione, conservazione, soglia di stantiezza e
#  nome del task. Stessa fonte che legge /admin/backup, cosi' la pagina e lo script non possono
#  raccontare due storie diverse sullo stesso backup.
#
#  NON FALLISCE MAI. Un appsettings assente, illeggibile o senza la sezione lascia i default
#  storici: un backup che si rifiuta di partire perche' manca una chiave FACOLTATIVA sarebbe un
#  guasto peggiore di quello che la chiave risolve. Quel che e' obbligatorio (la connection
#  string) si controlla piu' avanti, dove il fallimento e' rumoroso e notificato.
function Get-BackupConfig([string]$settingsPath) {
    $cfg = @{
        NightlyDirectory  = (Join-Path $env:USERPROFILE 'ProcioneMGR-Backup')
        RetentionDays     = 14
        StaleAfterHours   = 48
        ScheduledTaskName = 'ProcioneMGR Backup DB'
    }
    if (-not (Test-Path $settingsPath)) { return $cfg }

    try { $section = (Get-Content $settingsPath -Raw | ConvertFrom-Json).Backup } catch { return $cfg }
    if (-not $section) { return $cfg }

    if (-not [string]::IsNullOrWhiteSpace($section.NightlyDirectory))  { $cfg.NightlyDirectory  = $section.NightlyDirectory.Trim() }
    if (-not [string]::IsNullOrWhiteSpace($section.ScheduledTaskName)) { $cfg.ScheduledTaskName = $section.ScheduledTaskName.Trim() }
    if ($section.RetentionDays -ge 1)   { $cfg.RetentionDays   = [int]$section.RetentionDays }
    if ($section.StaleAfterHours -ge 1) { $cfg.StaleAfterHours = [int]$section.StaleAfterHours }
    return $cfg
}

$backupCfg = Get-BackupConfig $appSettings

# Precedenza: parametro esplicito > configurazione > default. Il test e' su ContainsKey e non sul
# valore, perche' "-Destination ''" e' una richiesta sbagliata da ignorare, non una destinazione.
$destinationExplicit = $PSBoundParameters.ContainsKey('Destination') -and -not [string]::IsNullOrWhiteSpace($Destination)
$keepDaysExplicit    = $PSBoundParameters.ContainsKey('KeepDays') -and $KeepDays -ge 1
if (-not $destinationExplicit) { $Destination = $backupCfg.NightlyDirectory }
if (-not $keepDaysExplicit)    { $KeepDays    = $backupCfg.RetentionDays }

$taskName   = $backupCfg.ScheduledTaskName
$staleHours = $backupCfg.StaleAfterHours

if ($Register) {
    # Stesso pattern di watchdog.ps1: il verdetto e' la VERIFICA, non l'assenza di eccezioni.
    # Un "Accesso negato" da Register-ScheduledTask e' NON terminante e scivolerebbe sotto un
    # messaggio di successo — il classico controllo che rassicura.
    # Si registra SEMPRE la copia del repo principale, mai quella da cui stiamo girando: un task
    # che punta a un worktree e' esattamente il guasto del 2026-08-17 (vedi Get-MainRepoRoot).
    $scriptPath = Join-Path $mainRepoRoot 'scripts\db-backup.ps1'
    if (-not (Test-Path $scriptPath)) {
        Write-Host "Backup   : lo script del repo principale non esiste ($scriptPath) - non registro un task che non potrebbe funzionare." -ForegroundColor Red
        exit 1
    }
    if ($scriptPath -ne $MyInvocation.MyCommand.Path) {
        Write-Host "Backup   : registro la copia del repo principale ($scriptPath), non questa in worktree." -ForegroundColor Yellow
    }
    # Gli argomenti NON congelano piu' destinazione e conservazione (2026-08-23): un valore scritto
    # dentro il task e' una seconda verita' che il primo cambio in /admin/backup fa divergere — la
    # pagina guarderebbe la cartella configurata e il task ne riempirebbe un'altra, gridando
    # "backup fermo" su un backup sano. Senza quegli argomenti lo script rilegge appsettings.json a
    # ogni notte, e la fonte resta una sola. Chi passa -Destination/-KeepDays a mano sta chiedendo
    # esplicitamente un task fuori configurazione: glielo si da', ma dicendoglielo.
    $argument = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
    if ($destinationExplicit) { $argument += " -Destination `"$Destination`"" }
    if ($keepDaysExplicit)    { $argument += " -KeepDays $KeepDays" }
    if ($destinationExplicit -or $keepDaysExplicit) {
        Write-Host "Backup   : ATTENZIONE - il task viene registrato con argomenti FISSI, che vincono sulla sezione Backup di appsettings.json." -ForegroundColor Yellow
        Write-Host "           /admin/backup lo segnalera' come divergenza finche' non lo ri-registri senza parametri." -ForegroundColor Yellow
    }

    try {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argument
        # 03:30 e non 03:00: il CronJob del cluster, se un giorno verra' acceso, sta alle 03:00.
        # Due dump insieme sullo stesso server sono solo due volte il carico.
        $trigger = New-ScheduledTaskTrigger -Daily -At '03:30'
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 2)
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
            -Settings $settings -Description 'Backup notturno del database ProcioneMGR (pg_dump -Fc verso il disco dell host).' `
            -Force -ErrorAction Stop | Out-Null
    } catch {
        Write-Host "Backup   : registrazione FALLITA: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        $origine = if ($destinationExplicit) { 'FISSATA nel task' } else { 'letta da appsettings.json a ogni esecuzione' }
        Write-Host "Backup   : task '$taskName' registrato e VERIFICATO (ogni notte alle 03:30)." -ForegroundColor Green
        Write-Host "           Destinazione: $Destination ($origine)."
        exit 0
    }
    Write-Host "Backup   : Register-ScheduledTask non ha lanciato ma il task NON esiste - registrazione fallita." -ForegroundColor Red
    exit 1
}

# --- Stato dei backup esistenti -------------------------------------------------------------
if ($Verify) {
    if (-not (Test-Path $Destination)) {
        Write-Host "Backup   : nessuna cartella $Destination - non e' mai stato eseguito un backup." -ForegroundColor Yellow
        exit 1
    }
    $files = @(Get-ChildItem -Path $Destination -Filter 'procionemgr-*.dump' -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending)
    if ($files.Count -eq 0) {
        Write-Host "Backup   : cartella presente ma VUOTA - nessun backup recuperabile." -ForegroundColor Yellow
        exit 1
    }
    $latest = $files[0]
    $ageHours = [math]::Round(((Get-Date) - $latest.LastWriteTime).TotalHours, 1)
    $totalMb  = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 0)
    Write-Host "Backup   : $($files.Count) backup, $totalMb MB in totale." -ForegroundColor Green
    Write-Host "           Piu' recente: $($latest.Name) ($([math]::Round($latest.Length/1MB,0)) MB, $ageHours ore fa)."
    # Oltre la soglia il backup notturno non sta girando: e' un guasto, e va detto. La soglia e' la
    # STESSA che usa /admin/backup (sezione Backup di appsettings.json): due soglie diverse sullo
    # stesso fatto sono due verdetti che prima o poi si contraddicono davanti all'operatore.
    if ($ageHours -gt $staleHours) {
        Write-Host "           ATTENZIONE: piu' vecchio di $staleHours ore - il task notturno non sta girando." -ForegroundColor Red
        exit 1
    }
    exit 0
}

# --- Notifica -------------------------------------------------------------------------------
#  PERCHE' (2026-08-17): il backup ha fallito sei notti di fila senza che nessuno se ne
#  accorgesse. Il watchdog guarda guscio, motore e Postgres — non i backup — e il codice di uscita
#  del Task Scheduler non lo legge nessuno. Un backup che fallisce in silenzio e' un backup che
#  non esiste, e lo scopri il giorno in cui ti serve.
#
#  Duplicato (e non condiviso) con watchdog.ps1 di proposito: come quello, questo script deve
#  poter funzionare da solo, senza dipendere da un altro file del repo che potrebbe mancare.
#  Stesse variabili d'ambiente del watchdog, quindi zero configurazione nuova.
function Send-Telegram([string]$text) {
    $token = $env:TELEGRAM_BOT_TOKEN
    $chatId = $env:TELEGRAM_CHAT_ID
    if ([string]::IsNullOrWhiteSpace($token) -or [string]::IsNullOrWhiteSpace($chatId)) {
        Write-Host "Backup   : TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID mancanti - notifica NON inviata: $text" -ForegroundColor Yellow
        return
    }
    try {
        # Il token sta nel PATH dell'URL (semantica dell'API Telegram): mai loggarlo per intero.
        $body = @{ chat_id = $chatId; text = $text }
        Invoke-RestMethod -Uri "https://api.telegram.org/bot$token/sendMessage" -Method Post -Body $body -TimeoutSec 15 | Out-Null
    } catch {
        Write-Host "Backup   : invio Telegram FALLITO: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# Ogni uscita per fallimento passa da qui: cosi' non esiste un percorso di errore muto. Una volta
# per notte, quindi nessun rischio di raffica: non serve l'anti-spam del watchdog.
function Stop-WithFailure([string]$message) {
    Write-Host "Backup   : $message" -ForegroundColor Red
    Send-Telegram "[$(Get-Date -Format 'yyyy-MM-dd HH:mm')] BACKUP DB FALLITO: $message"
    exit 1
}

# --- Backup ---------------------------------------------------------------------------------
# $appSettings e' gia' risolto in cima (lo legge anche Get-BackupConfig). Qui la sua assenza NON e'
# tollerabile: senza connection string non c'e' backup, e il fallimento dev'essere rumoroso.
if (-not (Test-Path $appSettings)) {
    Stop-WithFailure "$appSettings non trovato - impossibile leggere la connessione."
}

try {
    $cs = (Get-Content $appSettings -Raw | ConvertFrom-Json).ConnectionStrings.PostgresConnection
} catch {
    Stop-WithFailure "appsettings.json illeggibile: $($_.Exception.Message)"
}

$cfg = @{}
foreach ($kv in $cs.Split(';')) { if ($kv -match '=') { $k, $v = $kv.Split('=', 2); $cfg[$k.Trim()] = $v.Trim() } }

$pgDump = Join-Path ${env:ProgramFiles} 'PostgreSQL\18\bin\pg_dump.exe'
if (-not (Test-Path $pgDump)) {
    Stop-WithFailure "pg_dump non trovato in $pgDump."
}

if (-not (Test-Path $Destination)) { New-Item -ItemType Directory -Path $Destination -Force | Out-Null }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outFile = Join-Path $Destination "procionemgr-$stamp.dump"

Write-Host "Backup   : dump di $($cfg['Database']) verso $outFile ..." -ForegroundColor Cyan
$env:PGPASSWORD = $cfg['Password']
try {
    # -Fc: formato custom, comprimibile e ripristinabile selettivamente con pg_restore.
    & $pgDump -h $cfg['Host'] -p $cfg['Port'] -U $cfg['Username'] -d $cfg['Database'] -Fc -f $outFile
    $code = $LASTEXITCODE
} finally {
    $env:PGPASSWORD = $null
}

# Il codice di uscita NON basta: pg_dump puo' uscire 0 lasciando un file troncato se il disco si
# riempie. Si guarda il file, che e' il solo esito che conta.
if ($code -ne 0 -or -not (Test-Path $outFile)) {
    if (Test-Path $outFile) { Remove-Item $outFile -Force -ErrorAction SilentlyContinue }
    Stop-WithFailure "pg_dump FALLITO (codice $code) - vedi l'errore qui sopra."
}

$sizeMb = [math]::Round((Get-Item $outFile).Length / 1MB, 0)

# --- Il dump e' RIPRISTINABILE? --------------------------------------------------------------
# "Esiste ed e' grosso" non e' integrita': un custom dump puo' essere illeggibile e pesare uguale.
# pg_restore --list apre il file e ne legge l'indice — se il formato e' corrotto o l'intestazione
# e' monca, fallisce qui invece che il giorno del disastro. Costa un paio di secondi.
#
# ONESTA' SUL LIMITE: --list legge la TOC, non i blocchi di dati. Prova che il formato e' valido e
# che il contenuto atteso e' censito, NON che ogni byte dei dati sia integro. La prova piena resta
# il drill di restore su server vergine (l'ultimo il 2026-07-26), che e' un'altra cosa e va rifatta
# a mano ogni tanto.
$pgRestore = Join-Path ${env:ProgramFiles} 'PostgreSQL\18\bin\pg_restore.exe'
if (Test-Path $pgRestore) {
    $toc = & $pgRestore --list $outFile
    $tocCode = $LASTEXITCODE
    # Le righe che iniziano per ';' sono l'intestazione del catalogo: le voci vere sono le altre.
    $entries = @($toc | Where-Object { $_ -match '\S' -and $_ -notmatch '^\s*;' }).Count
    if ($tocCode -ne 0 -or $entries -lt 1) {
        Remove-Item $outFile -Force -ErrorAction SilentlyContinue
        Stop-WithFailure "dump da $sizeMb MB prodotto ma NON leggibile da pg_restore (codice $tocCode, $entries voci) - rimosso, NON e' un backup."
    }
    Write-Host "Backup   : integrita' del formato verificata ($entries voci nell'indice)." -ForegroundColor Green
} else {
    Write-Host "Backup   : pg_restore non trovato in $pgRestore - integrita' NON verificata." -ForegroundColor Yellow
}

if ($sizeMb -lt 1) {
    # Sotto 1 MB non e' questo database: lo teniamo (non si butta l'unica copia) ma si grida.
    Write-Host "Backup   : file prodotto ma SOSPETTO ($sizeMb MB) - lo tengo, ma va guardato." -ForegroundColor Yellow
    Send-Telegram "[$(Get-Date -Format 'yyyy-MM-dd HH:mm')] Backup DB SOSPETTO: solo $sizeMb MB (attesi centinaia). Il file e' stato tenuto, ma va guardato."
} else {
    Write-Host "Backup   : completato, $sizeMb MB." -ForegroundColor Green
}

# --- Rotazione ------------------------------------------------------------------------------
# Si cancella solo DOPO un backup riuscito: un backup fallito non deve poter portarsi via anche i
# vecchi. E si tiene sempre l'ultimo, qualunque eta' abbia.
$old = @(Get-ChildItem -Path $Destination -Filter 'procionemgr-*.dump' -ErrorAction SilentlyContinue |
         Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$KeepDays) } |
         Sort-Object LastWriteTime -Descending | Select-Object -Skip 0)
$remaining = @(Get-ChildItem -Path $Destination -Filter 'procionemgr-*.dump' -ErrorAction SilentlyContinue).Count
foreach ($f in $old) {
    if ($remaining -le 1) { break }
    Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue
    $remaining--
    Write-Host "           rimosso backup oltre $KeepDays giorni: $($f.Name)"
}
exit 0
