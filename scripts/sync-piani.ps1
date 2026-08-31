# =============================================================================================
#  [K2+K3, PRD autonomia-piena 2026-08-31] I due piani che NON si aggiornavano da soli.
#
#  Il motore si sincronizza ogni 30' (deploy-trading.ps1). Il guscio e la plancia no: una
#  correzione mergiata su master, con la CI verde, arrivava nel pod in mezz'ora e negli altri due
#  restava fuori per GIORNI — finche' qualcuno non riavviava il PC. Misura del 2026-08-30: guscio
#  indietro di 7 commit, plancia di 13, e la plancia stantia si e' appesa sullo stesso pipe
#  dell'incidente del 28/08 con dentro il binario il fix che lo impediva. L'unica volta che il
#  guscio si e' allineato e' stato PER INCIDENTE: un riavvio della macchina, che fa ricompilare
#  `dotnet run`.
#
#  COSA FA, per ciascun piano, in ordine e fermandosi al primo che agisce:
#    1. legge la revisione VIVA (guscio: /health; plancia: ProductVersion del suo Procione.dll);
#    2. la confronta col CONTENUTO di origin/master, escluso il file del pin — stesso cancello di
#       deploy-trading.ps1: senza quell'esclusione ogni deploy del motore renderebbe "stantio"
#       anche chi non c'entra;
#    3. il guscio si aggiorna solo in FINESTRA DI QUIETE: glielo si chiede su /health/quiet, che
#       risponde no se c'e' un run di pipeline in volo o una campagna appesa. Costo misurato di un
#       riavvio: ~3m36s, e NON tocca posizioni, corsie ne' lease (vivono nel pod);
#    4. la plancia non puo' ricompilarsi da sola mentre gira (l'eseguibile e' bloccato): delega a
#       un aggiornatore DETACHED che aspetta la sua uscita, ricompila e la rilancia. Se anche
#       quello fallisse, il trigger di risveglio ogni 10' (K5b) la rimette su comunque.
#
#  COSA NON FA: non tocca il motore (e' di deploy-trading.ps1), non fa merge, non forza nulla se
#  git e' sporco o divergente.
#
#  USO:
#    ./scripts/sync-piani.ps1              # come lo chiama il lavoro `piani` della plancia
#    ./scripts/sync-piani.ps1 -DryRun      # dice cosa farebbe, non tocca niente
#    ./scripts/sync-piani.ps1 -SoloGuscio  # limita l'azione a un piano solo
# =============================================================================================

param(
    [switch]$DryRun,
    [switch]$SoloGuscio,
    [switch]$SoloPlancia
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$pinFile  = 'infra/k8s/trading/kustomization.yaml'
$shellUrl = 'http://localhost:5199'

function Log([string]$m, [string]$c = 'Gray') { Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) -ForegroundColor $c }

# Uno sha e' "allineato" se il CONTENUTO di master non differisce dal suo, pin escluso.
function Test-Allineato([string]$sha) {
    if ([string]::IsNullOrWhiteSpace($sha)) { return $null }
    git -C $repoRoot diff --quiet $sha origin/master -- . ":(exclude)$pinFile" 2>$null
    switch ($LASTEXITCODE) {
        0 { return $true }
        1 { return $false }
        default { return $null }   # sha sconosciuto al repo: non e' "allineato", e' non misurabile
    }
}

# La revisione dichiarata dal guscio VIVO. Il binario su disco direbbe cosa e' stato compilato per
# ultimo, che non e' la stessa cosa: i due hanno divergito per giorni.
function Get-RevisioneGuscio {
    try {
        $r = Invoke-RestMethod -Uri "$shellUrl/health" -TimeoutSec 10
        if ($r.revision) { return "$($r.revision)" }
        return ''      # guscio precedente a K1: e' esso stesso il sintomo
    } catch { return $null }   # guscio giu': non e' compito di questo script rialzarlo
}

Log "=== sync-piani ===" 'Cyan'
git -C $repoRoot fetch --quiet origin master
if ($LASTEXITCODE -ne 0) { Log "git fetch fallito: senza il remoto non si sa cosa allineare." 'Red'; exit 0 }

# --- 1. Guscio -------------------------------------------------------------------------------
if (-not $SoloPlancia) {
    $rev = Get-RevisioneGuscio
    if ($null -eq $rev) {
        Log "Guscio   : non risponde su $shellUrl - lo rialza il bring-up, non questo script."
    }
    else {
        $allineato = if ($rev) { Test-Allineato $rev } else { $false }
        if ($allineato -eq $true) {
            Log "Guscio   : allineato ($($rev.Substring(0,8)))." 'Green'
        }
        else {
            $quale = if ($rev) { $rev.Substring(0, 8) } else { 'precedente a K1 (nessuna revisione dichiarata)' }
            Log "Guscio   : STANTIO ($quale)." 'Yellow'

            $quiete = $null
            try { $quiete = Invoke-RestMethod -Uri "$shellUrl/health/quiet" -TimeoutSec 20 } catch { }

            if ($null -eq $quiete) {
                # Un guscio troppo vecchio per avere l'endpoint e' proprio quello da aggiornare, ma
                # non si riavvia al buio: la quiete non e' deducibile e la prima volta la si concede
                # a mano. Meglio un aggiornamento in ritardo che un run di pipeline troncato.
                Log "Guscio   : /health/quiet non risponde (guscio precedente a K3): NON riavvio da solo." 'Yellow'
                Log "           una volta sola, a mano: procione ferma guscio && procione avvia guscio"
            }
            elseif (-not $quiete.quiet) {
                Log "Guscio   : non e' il momento - $($quiete.reason). Riprovo al prossimo giro."
            }
            elseif ($DryRun) {
                Log "Guscio   : [DryRun] lo aggiornerei adesso - $($quiete.reason)." 'Cyan'
            }
            else {
                Log "Guscio   : finestra di quiete APERTA - $($quiete.reason)." 'Green'
                git -C $repoRoot pull --ff-only --quiet origin master
                if ($LASTEXITCODE -ne 0) {
                    Log "Guscio   : git pull --ff-only fallito (albero sporco o divergente): non tocco nulla." 'Red'
                }
                else {
                    # La plancia sa gia' fermare il guscio: non si riscrive quel codice qui.
                    & (Join-Path $repoRoot 'tools\Procione\bin\Release\net10.0\procione.exe') ferma guscio
                    # bringup ricompila e riavvia: `dotnet run -c Release` rifa' il binario
                    # dall'albero di lavoro, che ora e' master. E' anche il passo che rimette i
                    # port-forward, che muoiono col guscio.
                    & (Join-Path $PSScriptRoot 'bringup.ps1')
                    Log "Guscio   : aggiornato e riavviato." 'Green'
                }
            }
        }
    }
}

# --- 2. Plancia ------------------------------------------------------------------------------
if (-not $SoloGuscio) {
    $dll = Join-Path $repoRoot 'tools\Procione\bin\Release\net10.0\Procione.dll'
    $revP = ''
    if (Test-Path $dll) {
        # ProductVersion porta l'AssemblyInformationalVersion: "1.0.0+<sha>".
        $pv = (Get-Item $dll).VersionInfo.ProductVersion
        if ($pv -match '\+([0-9a-fA-F]{7,40})$') { $revP = $Matches[1].ToLower() }
    }

    $allineataP = if ($revP) { Test-Allineato $revP } else { $null }
    if ($allineataP -eq $true) {
        Log "Plancia  : allineata ($($revP.Substring(0,8)))." 'Green'
    }
    elseif ($DryRun) {
        Log "Plancia  : [DryRun] la ricompilerei e riavvierei." 'Cyan'
    }
    else {
        Log "Plancia  : STANTIA - delego a un aggiornatore detached." 'Yellow'
        git -C $repoRoot pull --ff-only --quiet origin master
        if ($LASTEXITCODE -ne 0) {
            Log "Plancia  : git pull --ff-only fallito: non tocco nulla." 'Red'
        }
        else {
            # NESSUNA redirezione qui. Start-Process con -RedirectStandard* forza
            # UseShellExecute=false e fa EREDITARE gli handle: e' esattamente la catena che il
            # 2026-08-28 ha appeso il supervisore per un'ora e cinquanta. L'aggiornatore scrive il
            # proprio log da se', su un file che apre lui.
            $agg = @"
`$log = Join-Path `$env:TEMP 'procionemgr-aggiorna-plancia.log'
function L(`$m) { "`$(Get-Date -Format 'HH:mm:ss') `$m" | Out-File -FilePath `$log -Append -Encoding utf8 }
L '--- aggiornatore della plancia ---'
for (`$i = 0; `$i -lt 60; `$i++) {
    if (-not (Get-Process -Name procione -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Seconds 2
}
if (Get-Process -Name procione -ErrorAction SilentlyContinue) { L 'la plancia non e uscita in 2 minuti: non ricompilo'; exit 1 }
L 'ricompilo'
& dotnet build '$($repoRoot -replace "'", "''")\tools\Procione\Procione.csproj' -c Release --nologo -v q 2>&1 | Out-File -FilePath `$log -Append -Encoding utf8
if (`$LASTEXITCODE -ne 0) { L "build FALLITA (`$LASTEXITCODE): il trigger di risveglio rimettera su la versione vecchia" }
else { L 'build ok' }
Start-ScheduledTask -TaskName 'ProcioneMGR Plancia'
L 'attivita rilanciata'
"@
            Start-Process powershell -WindowStyle Hidden -ArgumentList @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $agg
            ) | Out-Null

            Log "Plancia  : aggiornatore avviato; chiedo al supervisore di uscire." 'Yellow'
            & (Join-Path $repoRoot 'tools\Procione\bin\Release\net10.0\procione.exe') servizio ferma
        }
    }
}

Log "=== fine ===" 'Cyan'
exit 0
