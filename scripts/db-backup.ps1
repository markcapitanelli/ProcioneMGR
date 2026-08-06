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
#  DOVE FINISCONO: %USERPROFILE%\ProcioneMGR-Backup (sovrascrivibile con -Destination). Fuori dal
#  repo, che e' PUBBLICO: un dump contiene la master key cifrata, le credenziali exchange e tutto
#  lo storico.
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
    [string]$Destination = (Join-Path $env:USERPROFILE 'ProcioneMGR-Backup'),
    [int]$KeepDays = 14
)

$ErrorActionPreference = 'Continue'

$taskName = 'ProcioneMGR Backup DB'

if ($Register) {
    # Stesso pattern di watchdog.ps1: il verdetto e' la VERIFICA, non l'assenza di eccezioni.
    # Un "Accesso negato" da Register-ScheduledTask e' NON terminante e scivolerebbe sotto un
    # messaggio di successo — il classico controllo che rassicura.
    try {
        $scriptPath = $MyInvocation.MyCommand.Path
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Destination `"$Destination`" -KeepDays $KeepDays"
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
        Write-Host "Backup   : task '$taskName' registrato e VERIFICATO (ogni notte alle 03:30, destinazione $Destination)." -ForegroundColor Green
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
    # Oltre le 48h il backup notturno non sta girando: e' un guasto, e va detto.
    if ($ageHours -gt 48) {
        Write-Host "           ATTENZIONE: piu' vecchio di 48 ore - il task notturno non sta girando." -ForegroundColor Red
        exit 1
    }
    exit 0
}

# --- Backup ---------------------------------------------------------------------------------
$repoRoot = Split-Path -Parent $PSScriptRoot
$appSettings = Join-Path $repoRoot 'ProcioneMGR\appsettings.json'
if (-not (Test-Path $appSettings)) {
    Write-Host "Backup   : $appSettings non trovato - impossibile leggere la connessione." -ForegroundColor Red
    exit 1
}

try {
    $cs = (Get-Content $appSettings -Raw | ConvertFrom-Json).ConnectionStrings.PostgresConnection
} catch {
    Write-Host "Backup   : appsettings.json illeggibile: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

$cfg = @{}
foreach ($kv in $cs.Split(';')) { if ($kv -match '=') { $k, $v = $kv.Split('=', 2); $cfg[$k.Trim()] = $v.Trim() } }

$pgDump = Join-Path ${env:ProgramFiles} 'PostgreSQL\18\bin\pg_dump.exe'
if (-not (Test-Path $pgDump)) {
    Write-Host "Backup   : pg_dump non trovato in $pgDump." -ForegroundColor Red
    exit 1
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
    Write-Host "Backup   : FALLITO (codice $code)." -ForegroundColor Red
    if (Test-Path $outFile) { Remove-Item $outFile -Force -ErrorAction SilentlyContinue }
    exit 1
}

$sizeMb = [math]::Round((Get-Item $outFile).Length / 1MB, 0)
if ($sizeMb -lt 1) {
    Write-Host "Backup   : file prodotto ma SOSPETTO ($sizeMb MB) - lo tengo, ma va guardato." -ForegroundColor Yellow
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
