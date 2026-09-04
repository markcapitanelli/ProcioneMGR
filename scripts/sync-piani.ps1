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

# AVANTI non e' INDIETRO, e non si "aggiorna" tornando indietro. Un piano compilato da un ramo non
# ancora mergiato CONTIENE master: ricompilarlo lo riporterebbe a master, cioe' cancellerebbe il
# lavoro che qualcuno sta provando. E' lo stesso difetto che la prova sul vivo ha trovato nel
# verdetto C# di K1, che stampava «INDIETRO di 0 commit» su un piano che era avanti.
function Test-Avanti([string]$sha) {
    if ([string]::IsNullOrWhiteSpace($sha)) { return $false }
    # --left-right, come Verdicts.Revisione: a destra i commit che a questo piano MANCANO. Se non
    # gliene manca nessuno non e' indietro, comunque sia messo il resto della storia — e un ramo
    # divergente ma piu' recente non va "aggiornato" tornando a master.
    $c = git -C $repoRoot rev-list --count --left-right "$sha...origin/master" 2>$null
    if ($LASTEXITCODE -ne 0) { return $false }
    $p = "$c" -split '\s+' | Where-Object { $_ -ne '' }
    if ($p.Count -ne 2) { return $false }
    return ([int]$p[1] -eq 0)
}

# La revisione dichiarata dal guscio VIVO. Il binario su disco direbbe cosa e' stato compilato per
# ultimo, che non e' la stessa cosa: i due hanno divergito per giorni.
function Get-RevisioneGuscio {
    # 60 secondi, non 10. Misurato il 2026-08-31 con una caccia di config 19 in corso (5m,
    # 139.000 osservazioni per fattore): /health ha impiegato prima 13,3 secondi e poi PIU' DI 30.
    # Un timeout tarato sulla macchina a riposo trasforma il carico in un guasto, e questo script
    # concluderebbe «guscio giu'» proprio mentre il guscio lavora di piu' — cioe' quasi sempre,
    # visto che le cacce girano 4-8 volte al giorno con stage da venti minuti.
    try {
        $r = Invoke-RestMethod -Uri "$shellUrl/health" -TimeoutSec 60
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
        # [2026-08-31, sera] «Lo rialza il bring-up» era falso in pratica, ed e' costato un guscio
        # morto per ore. Il lavoro `avvio` gira UNA volta per sessione; il watchdog manda un
        # messaggio e si ferma li'; questo script si limitava a dirlo. Quindi un guscio che muore a
        # meta' sessione restava morto fino al logon successivo — cioe' finche' non se ne accorgeva
        # un umano, che e' la definizione di non autonoma. Con lui restano ferme campagne, flotta,
        # pipeline e promozioni: 22 hosted service.
        #
        # MA MAI DUE GUSCII. Un processo che c'e' e non risponde sta COMPILANDO (`dotnet run` ci
        # mette minuti): rilanciare il bring-up ne accenderebbe un secondo, ed e' l'incidente del
        # 2026-07-20 — un'istanza di troppo che intercetta l'utente. Si guarda quindi il PROCESSO,
        # non solo la porta, e si agisce solo quando non c'e' nessuno.
        $vivi = @(Get-Process -Name ProcioneMGR, dotnet -ErrorAction SilentlyContinue |
                  Where-Object { $_.Path -and $_.Path -like '*ProgettoP*' })
        if ($vivi.Count -gt 0) {
            Log "Guscio   : non risponde ma $($vivi.Count) processi suoi sono vivi: sta partendo, non tocco nulla."
        }
        elseif ($DryRun) {
            Log "Guscio   : [DryRun] e' GIU' e nessun processo vivo: lancerei il bring-up." 'Cyan'
        }
        else {
            Log "Guscio   : e' GIU' e nessun processo lo sta avviando - lancio il bring-up." 'Yellow'
            & (Join-Path $PSScriptRoot 'bringup.ps1')
            Log "Guscio   : bring-up eseguito." 'Green'
        }
    }
    else {
        $allineato = if ($rev) { Test-Allineato $rev } else { $false }
        if ($allineato -eq $true) {
            Log "Guscio   : allineato ($($rev.Substring(0,8)))." 'Green'
        }
        elseif (Test-Avanti $rev) {
            Log "Guscio   : AVANTI a master ($($rev.Substring(0,8))): ramo non mergiato, non lo tocco." 'Yellow'
        }
        else {
            $quale = if ($rev) { $rev.Substring(0, 8) } else { 'precedente a K1 (nessuna revisione dichiarata)' }
            Log "Guscio   : STANTIO ($quale)." 'Yellow'

            $quiete = $null
            try { $quiete = Invoke-RestMethod -Uri "$shellUrl/health/quiet" -TimeoutSec 60 } catch { }

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
                    if ($LASTEXITCODE -ne 0) {
                        # [2026-09-05] Se il guscio NON e' stato fermato, il bring-up lo trova in
                        # ascolto e dichiara «gia' in ascolto»: un rilascio finto, con la revisione
                        # vecchia che continua a girare. E' successo alle 00:54: macchina satura,
                        # query dei pid scaduta, «gia' fermo» letto da una lista vuota.
                        Log "Guscio   : `procione ferma guscio` NON e' riuscito (codice $LASTEXITCODE): non lancio il bring-up, riprovo al prossimo giro." 'Red'
                    }
                    else {
                        # bringup ricompila e riavvia: `dotnet run -c Release` rifa' il binario
                        # dall'albero di lavoro, che ora e' master. E' anche il passo che rimette i
                        # port-forward, che muoiono col guscio.
                        & (Join-Path $PSScriptRoot 'bringup.ps1')

                        # Si VERIFICA la revisione dopo, invece di dichiarare l'esito prima: il
                        # bring-up e' idempotente e non distingue «l'ho avviato» da «c'era gia'».
                        $dopo = $null
                        try { $dopo = (Invoke-RestMethod -Uri "$shellUrl/health" -TimeoutSec 60).revision } catch { }
                        if ($dopo -and $rev -and $dopo -eq $rev) {
                            Log "Guscio   : NON aggiornato - dopo il bring-up risponde ancora $($rev.Substring(0,8)). Il processo vecchio non e' stato fermato: riprovo al prossimo giro." 'Red'
                        }
                        elseif ($dopo) {
                            Log "Guscio   : aggiornato e riavviato ($($dopo.Substring(0,8)))." 'Green'
                        }
                        else {
                            Log "Guscio   : bring-up eseguito, ma /health non risponde ancora: la revisione la dira' il prossimo giro." 'Yellow'
                        }
                    }
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
    elseif (Test-Avanti $revP) {
        Log "Plancia  : AVANTI a master ($($revP.Substring(0,8))): ramo non mergiato, non la tocco." 'Yellow'
    }
    elseif ($DryRun) {
        Log "Plancia  : [DryRun] la ricompilerei e riavvierei." 'Cyan'
    }
    else {
        # [2026-08-31, sera] LA PRIMA STESURA NON FUNZIONAVA, e il modo in cui ha fallito vale piu'
        # della correzione. Spawnavo l'aggiornatore con Start-Process passando lo script come
        # -Command multiriga, poi chiedevo al supervisore di uscire. Risultato osservato due volte
        # dal vivo: il supervisore usciva, l'aggiornatore NON girava (nessun log, binario
        # invariato), e la plancia restava giu' finche' il trigger di risveglio (K5b) non la
        # rianimava dieci minuti dopo — con lo stesso binario vecchio. Cioe' un CICLO: uscita ogni
        # venti minuti, resurrezione dieci minuti dopo, mai un aggiornamento, e il dead-man switch
        # fermo per un terzo del tempo. Un'automazione che non puo' riuscire e continua a provarci
        # al prezzo della disponibilita' e' peggio di nessuna automazione.
        #
        # Due cause, entrambe reali:
        #  1. uno script multiriga passato come -Command dentro -ArgumentList viene appiattito e
        #     smette di essere PowerShell valido. Ora si scrive su FILE e si lancia con -File.
        #  2. il figlio di Start-Process resta nell'albero dei processi del lavoro, e quando il
        #     supervisore esce quell'albero viene ucciso — insieme all'aggiornatore. Win32_Process
        #     .Create crea un processo figlio di WmiPrvSE, non nostro: sopravvive per costruzione.
        #     (E, come prima, NESSUNA redirezione: sarebbe la catena di handle ereditati del
        #     2026-08-28. L'aggiornatore apre da se' il file su cui scrive.)
        Log "Plancia  : STANTIA - preparo l'aggiornatore." 'Yellow'

        # Terza guardia, la piu' importante: non si ritenta lo STESSO sha a ripetizione. Se la
        # build fallisce, riprovarci ogni venti minuti costa solo indisponibilita'.
        $shaMaster = (git -C $repoRoot rev-parse origin/master 2>$null).Trim()
        $marcatore = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.procione\aggiorna-plancia.ultimo'
        $gia = if (Test-Path $marcatore) { (Get-Content $marcatore -Raw).Trim() } else { '' }
        if ($gia -eq $shaMaster) {
            Log "Plancia  : aggiornamento a $($shaMaster.Substring(0,8)) GIA' TENTATO e non riuscito: non ritento." 'Red'
            Log "           a mano: procione servizio ferma; dotnet build tools/Procione -c Release; riavvia l'attivita'"
        }
        else {
            git -C $repoRoot pull --ff-only --quiet origin master
            if ($LASTEXITCODE -ne 0) {
                Log "Plancia  : git pull --ff-only fallito: non tocco nulla." 'Red'
            }
            else {
                $logAgg = Join-Path $env:TEMP 'procionemgr-aggiorna-plancia.log'
                $fileAgg = Join-Path $env:TEMP 'procionemgr-aggiorna-plancia.ps1'
                $csproj = Join-Path $repoRoot 'tools\Procione\Procione.csproj'

                # -p:SourceRevisionId ESPLICITO. Misurato il 2026-08-31: il timbro dello sha e'
                # APPICCICOSO fra una build e l'altra — l'aggiornatore ha ricompilato davvero
                # (7,58s, zero errori) e il binario ha continuato a dichiarare la revisione
                # PRECEDENTE, perche' l'AssemblyInfo generato in obj/ conserva il valore calcolato
                # al primo giro. Conseguenza: `piani` avrebbe visto la plancia ancora stantia e
                # avrebbe chiesto un'altra uscita, all'infinito. Passandolo a mano il timbro non
                # dipende piu' da nessuna cache. (Il guscio non ha il problema: `dotnet run` lo
                # ricalcola, e infatti dichiara master correttamente.)

                $corpo = @"
`$log = '$logAgg'
function L(`$m) { "`$(Get-Date -Format 'HH:mm:ss') `$m" | Out-File -FilePath `$log -Append -Encoding utf8 }
L '--- aggiornatore della plancia ---'
for (`$i = 0; `$i -lt 60; `$i++) {
    if (-not (Get-Process -Name procione -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Seconds 2
}
if (Get-Process -Name procione -ErrorAction SilentlyContinue) { L 'la plancia non e uscita in 2 minuti: non ricompilo'; exit 1 }
L 'ricompilo'
& dotnet build '$csproj' -c Release --nologo -v q -p:SourceRevisionId=$shaMaster 2>&1 | Out-File -FilePath `$log -Append -Encoding utf8
if (`$LASTEXITCODE -ne 0) { L "build FALLITA (`$LASTEXITCODE)" } else { L 'build ok' }
Start-ScheduledTask -TaskName 'ProcioneMGR Plancia'
L 'attivita rilanciata'
"@
                Set-Content -Path $fileAgg -Value $corpo -Encoding UTF8

                $cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$fileAgg`""
                $esito = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }

                if ($esito.ReturnValue -ne 0 -or -not $esito.ProcessId) {
                    # Non si chiede al supervisore di uscire se non c'e' nessuno che lo rimettera'
                    # su con un binario nuovo: sarebbe indisponibilita' senza contropartita.
                    Log "Plancia  : aggiornatore NON avviato (Win32_Process.Create -> $($esito.ReturnValue)): resto viva." 'Red'
                }
                else {
                    New-Item -ItemType Directory -Force -Path (Split-Path $marcatore) | Out-Null
                    Set-Content -Path $marcatore -Value $shaMaster -Encoding ascii
                    Log "Plancia  : aggiornatore avviato (pid $($esito.ProcessId)); chiedo al supervisore di uscire." 'Yellow'
                    & (Join-Path $repoRoot 'tools\Procione\bin\Release\net10.0\procione.exe') servizio ferma
                }
            }
        }
    }
}

Log "=== fine ===" 'Cyan'
exit 0
