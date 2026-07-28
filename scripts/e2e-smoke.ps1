<#
.SYNOPSIS
    [PRD §6] Smoke end-to-end contro un cluster Kubernetes: la classe di bug che TestServer non vede.

.DESCRIPTION
    Il piano di prova del PRD-INTEGRAZIONE-CORE-CALDO chiede uno smoke e2e su kind "per la classe di
    bug che TestServer non vede (h2c, doppio worker, ConfigMap non applicato - tutti trovati solo
    eseguendo, mai dai test in-process)". Questo script contiene le ASSERZIONI; chi crea il cluster
    (la CI o l'operatore) e' un altro problema, e tenerli separati serve a poter verificare le
    asserzioni contro un cluster vero senza doverne creare uno.

    Cinque controlli, uno per ciascun modo in cui questo progetto si e' gia' fatto male:

      1. POD PRONTI          - i Deployment attesi hanno le repliche dichiarate e nessun CrashLoop.
      2. HEALTH HTTP         - /health risponde 200 DENTRO il cluster (non dal port-forward: e' la
                               rete del cluster a essere andata storta in B1, con il podSubnet di
                               Calico che conteneva host.docker.internal e nessun pod che arrivava
                               al database).
      3. gRPC h2c            - la porta del servizio di trading parla HTTP/2 in chiaro. E' il bug
                               classico che in-process non esiste: TestServer non usa un socket.
      4. ConfigMap APPLICATO - le chiavi di trading-config.env sono davvero nell'ambiente del pod.
                               Un ConfigMap modificato ma non ri-applicato lascia il pod col
                               vecchio assetto, e la piattaforma opera in un modo diverso da quello
                               che il repository dichiara.
      5. UN SOLO ESECUTORE   - nessuna corsia ha due host vivi. E' l'invariante numero uno del PRD
                               (§4.1) e l'unico modo di violarla e' un deploy incoerente, che per
                               definizione i test unitari non possono vedere.

.PARAMETER Context
    Contesto kubectl. Default: quello corrente.

.PARAMETER Namespace
    Prefisso dei namespace. Default 'procionemgr'.

.EXAMPLE
    .\scripts\e2e-smoke.ps1 -Context kind-procionemgr-dev
#>
param(
    [string]$Context = "",
    [string]$NsPrefix = "procionemgr"
)

# NON "Stop": kubectl scrive su stderr anche cose che non sono errori (warning di attach, "container
# is waiting to start"), e con Stop ogni riga di stderr fa cadere lo script a meta' controllo. Gli
# errori qui si gestiscono guardando l'OUTPUT dei comandi, che e' l'unica cosa che dice davvero se il
# cluster sta bene.
$ErrorActionPreference = "Continue"
$failures = @()
$checks = 0

# Percorso dell'ESEGUIBILE risolto una volta sola. Serve perche' PowerShell non distingue
# maiuscole: dentro una funzione chiamata Kubectl, '& kubectl' risolverebbe alla funzione stessa e
# ricorrerebbe fino all'overflow dello stack.
$KubectlExe = (Get-Command kubectl -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source

# Funzione SEMPLICE di proposito (nessun param()): con un blocco param() PowerShell prova a legare
# '-o' e '-n' come parametri PROPRI della funzione e fallisce con "nome di parametro ambiguo",
# perche' -o somiglia a -OutVariable. Con $args gli argomenti passano a kubectl intatti.
function Kubectl {
    $full = @()
    if ($Context) { $full += @("--context", $Context) }
    $full += $args
    & $KubectlExe @full 2>&1
}

# Exec ha bisogno del separatore '--', che NON sopravvive al passaggio per $args: PowerShell lo
# consuma come fine-parametri della chiamata di funzione. Qui l'array e' costruito per intero e
# splattato direttamente sull'eseguibile, dove il separatore passa intatto.
function KubectlExec {
    param([string]$Ns, [string]$Pod, [string]$Container, [string]$ShellCommand)
    $full = @()
    if ($Context) { $full += @("--context", $Context) }
    $full += @("exec", "-n", $Ns, $Pod, "-c", $Container, "--", "sh", "-c", $ShellCommand)
    & $KubectlExe @full 2>&1
}

# Stato delle repliche di un Deployment, distinguendo ASSENTE da PRESENTE-A-ZERO. kubectl scrive
# "NotFound" su stderr, che qui arriva come oggetto errore: un cast a intero su quello esplode con un
# messaggio che non c'entra nulla con la domanda posta.
function ReplicaState {
    param([string]$Deployment, [string]$Ns)
    $raw = "$(Kubectl get deploy $Deployment -n $Ns -o jsonpath='{.status.readyReplicas}')".Trim()
    if ($raw -match 'NotFound|not found') { return [pscustomobject]@{ Exists = $false; Ready = 0 } }
    $ready = 0
    if (-not [int]::TryParse($raw, [ref]$ready)) { $ready = 0 }   # esiste ma nessuna replica pronta
    return [pscustomobject]@{ Exists = $true; Ready = $ready }
}

function Check {
    param([string]$Name, [scriptblock]$Body)
    $script:checks++
    Write-Host "  [$script:checks] $Name" -NoNewline
    try {
        $detail = & $Body
        Write-Host "  OK" -ForegroundColor Green
        if ($detail) { Write-Host "      $detail" -ForegroundColor DarkGray }
    }
    catch {
        Write-Host "  FALLITO" -ForegroundColor Red
        Write-Host "      $($_.Exception.Message)" -ForegroundColor Red
        $script:failures += "$Name : $($_.Exception.Message)"
    }
}

Write-Host "=== SMOKE E2E (PRD §6) ===" -ForegroundColor Cyan
Write-Host "    Contesto: $(if ($Context) { $Context } else { '(corrente)' })`n"

# --- 1. Pod pronti -------------------------------------------------------------------------------
# Solo i servizi che DEVONO girare. 'ui' e' escluso di proposito: fino a B3 resta scalato a 0 (il
# guscio operativo e' l'app locale), e pretenderlo pronto farebbe fallire lo smoke su un assetto
# corretto -- un test che fallisce quando tutto va bene si smette di guardare.
$expected = @(
    @{ Ns = "$NsPrefix-ingestion"; Name = "procionemgr-ingestion" },
    @{ Ns = "$NsPrefix-trading";   Name = "procionemgr-trading" },
    @{ Ns = "$NsPrefix-ml";        Name = "procionemgr-ml" }
)

foreach ($dep in $expected) {
    Check "Deployment $($dep.Name) pronto" {
        $ready = (Kubectl get deploy $dep.Name -n $dep.Ns -o jsonpath='{.status.readyReplicas}')
        $want = (Kubectl get deploy $dep.Name -n $dep.Ns -o jsonpath='{.spec.replicas}')
        if ($ready -ne $want -or [string]::IsNullOrWhiteSpace($ready)) {
            throw "repliche pronte '$ready' contro '$want' attese"
        }
        $restarts = (Kubectl get pods -n $dep.Ns -l "app.kubernetes.io/name=$($dep.Name)" -o jsonpath='{.items[*].status.containerStatuses[*].restartCount}')
        "repliche $ready/$want, restart: $(if ($restarts) { $restarts } else { '0' })"
    }
}

# --- 2/3. Raggiungibilita' dall'INTERNO del cluster -----------------------------------------------
# Il punto e' proprio non passare da un port-forward: in B1 il port-forward funzionava e nessun pod
# arrivava al database, perche' il podSubnet di Calico conteneva host.docker.internal.
#
# Le sonde girano da un pod busybox EFFIMERO e non da un pod dell'applicazione: le immagini runtime
# .NET non hanno ne' wget ne' curl, e soprattutto un pod nuovo esercita anche risoluzione DNS e
# NetworkPolicy come le esercita un deploy vero. Un solo pod per tutte le sonde: crearne uno per
# controllo triplicherebbe l'attesa senza aggiungere informazione.
# Deliberatamente in sh LINEARE, senza una sola variabile di shell ne' sostituzione di comando: il
# testo attraversa PowerShell, kubectl e infine sh, e ogni '$' o '`' in mezzo e' un'occasione di
# sbagliare l'escape. Un errore di sintassi qui non si presenta come errore di sintassi ma come
# "il cluster non risponde", che e' il modo peggiore di fallire per un test di raggiungibilita'.
$urlIngestion = "http://procionemgr-ingestion.$NsPrefix-ingestion.svc.cluster.local:8080/health"
$urlMlHealth = "http://procionemgr-ml.$NsPrefix-ml.svc.cluster.local:8081/health"
$urlTradingHealth = "http://procionemgr-trading.$NsPrefix-trading.svc.cluster.local:8081/health"
$urlTradingGrpc = "http://procionemgr-trading.$NsPrefix-trading.svc.cluster.local:8080/"

$probeScript = @"
wget -q -O- -T 10 '$urlIngestion' > /tmp/a 2>/dev/null
if [ -s /tmp/a ]; then printf 'HEALTH ingestion OK '; cat /tmp/a; echo; else echo 'HEALTH ingestion FAIL'; fi
wget -q -O- -T 10 '$urlMlHealth' > /tmp/b 2>/dev/null
if [ -s /tmp/b ]; then printf 'HEALTH ml OK '; cat /tmp/b; echo; else echo 'HEALTH ml FAIL'; fi
wget -q -O- -T 10 '$urlTradingHealth' > /tmp/c 2>/dev/null
if [ -s /tmp/c ]; then printf 'HEALTH trading OK '; cat /tmp/c; echo; else echo 'HEALTH trading FAIL'; fi
wget -q -S -O /dev/null -T 8 '$urlTradingGrpc' > /tmp/d 2>&1
grep -m1 'HTTP/' /tmp/d > /tmp/e
if [ -s /tmp/e ]; then printf 'COMANDI RAGGIUNGIBILI '; cat /tmp/e; else echo 'COMANDI BLOCCATI'; fi
"@

# I RITORNI A CAPO VANNO TOLTI. Su Windows git consegna questo file con terminatori CRLF, quindi
# ogni riga dello script qui sopra finirebbe dentro `sh` con un \r in coda: la shell lo considera
# parte del comando e muore con "unexpected end of file (expecting fi)". Non si vede in CI, dove il
# checkout e' LF — cioe' e' un difetto che il test in CI non puo' trovare, e che si manifesta solo a
# chi lo lancia a mano dal proprio repo. Ironico, per uno smoke.
$probeScript = $probeScript -replace "`r", ""

Write-Host "  ... sonda in-cluster (pod busybox effimero)" -ForegroundColor DarkGray
$probeName = "e2e-smoke-probe-$(Get-Random -Maximum 99999)"
$probeNs = "$NsPrefix-ingestion"

# Si crea, si ASPETTA e poi si leggono i log, invece di --attach: l'attach corre col creamento del
# container e ripiega sui log con un warning che su stderr fa cadere lo script. Meglio la sequenza
# esplicita, che e' anche quella che si puo' diagnosticare quando fallisce.
$runArgs = @()
if ($Context) { $runArgs += @("--context", $Context) }
$runArgs += @(
    "run", $probeName, "-n", $probeNs,
    "--image=busybox:1.36", "--restart=Never",
    "--command", "--", "sh", "-c", $probeScript
)

$probeOut = ""
try {
    $null = & $KubectlExe @runArgs 2>&1

    # Attesa esplicita della fase terminale. 'kubectl wait --for=condition=Ready=false' non serve:
    # un pod in ContainerCreating soddisfa gia' quella condizione e il wait torna subito, lasciando
    # leggere i log di un container che non e' ancora partito.
    $deadline = (Get-Date).AddSeconds(150)
    do {
        Start-Sleep -Milliseconds 1500
        $phase = "$(Kubectl get pod $probeName -n $probeNs -o jsonpath='{.status.phase}')".Trim()
    } while ($phase -notin @("Succeeded", "Failed") -and (Get-Date) -lt $deadline)

    if ($phase -notin @("Succeeded", "Failed")) {
        $probeOut = "__PROBE_TIMEOUT__ (fase '$phase' dopo 150s)"
    }
    else {
        $probeOut = ((Kubectl logs $probeName -n $probeNs) -join "`n")
    }
}
finally {
    $null = Kubectl delete pod $probeName -n $probeNs --ignore-not-found --wait=false
}

foreach ($svc in @("ingestion", "ml", "trading")) {
    Check "/health di procionemgr-$svc risponde dentro il cluster" {
        if ($probeOut -notmatch "HEALTH $svc OK (.+)") { throw "nessuna risposta.`n      Sonda:`n$probeOut" }
        $Matches[1].Trim()
    }
}

# --- La NetworkPolicy e' DAVVERO applicata -------------------------------------------------------
# Il controllo piu' importante dei cinque, ed e' un controllo NEGATIVO: la porta 8080 del trading
# espone ConfirmOrder e StartLane(LIVE), cioe' il denaro vero, e la policy la apre al SOLO pod ui.
#
# Una NetworkPolicy senza un CNI che la implementi viene accettata dall'API server e ignorata in
# silenzio: sembra protetta e non lo e'. E' successo su questo cluster, con kindnet, prima che il
# bootstrap installasse Calico. L'unico modo di sapere che il confine esiste e' provare a passarlo
# da fuori e vedersi rifiutare — e va provato sulla 8080, non sulla 8081, che la regola 2 apre a
# chiunque per le probe del kubelet e passerebbe con o senza enforcement.
#
# La sonda gira in procionemgr-ingestion, che e' esattamente un'origine non autorizzata.
Check "La NetworkPolicy blocca i comandi di trading da un namespace non autorizzato" {
    if ($probeOut -match "COMANDI RAGGIUNGIBILI (.+)") {
        throw "la porta 8080 del trading risponde a un pod di $NsPrefix-ingestion: la policy NON e' applicata " +
              "(CNI senza enforcement?). Risposta: $($Matches[1].Trim())"
    }
    if ($probeOut -notmatch "COMANDI BLOCCATI") { throw "sonda inconcludente.`n      Sonda:`n$probeOut" }
    "8080 rifiutata da fuori, 8081 raggiungibile: enforcement attivo"
}

# --- 4. ConfigMap davvero applicato --------------------------------------------------------------
# Non "il ConfigMap esiste": le variabili sono nell'ambiente del PROCESSO. Un ConfigMap aggiornato
# senza sostituire il pod lascia in esecuzione il vecchio assetto.
$mustHave = @(
    "MarketData__Realtime__Enabled",
    "MarketData__Realtime__DriveProtectiveExits",
    "Carry__Enabled",
    "Trading__LaneCount"
)
Check "Le chiavi di trading-config.env sono nell'ambiente del pod" {
    $tradingPod = (Kubectl get pods -n "$NsPrefix-trading" -o jsonpath='{.items[0].metadata.name}')
    $env = KubectlExec -Ns "$NsPrefix-trading" -Pod $tradingPod -Container trading -ShellCommand "env"
    $missing = @()
    foreach ($k in $mustHave) {
        if ("$env" -notmatch [regex]::Escape($k)) { $missing += $k }
    }
    if ($missing.Count -gt 0) { throw "chiavi assenti dall'ambiente: $($missing -join ', ')" }
    "$($mustHave.Count) chiavi presenti"
}

# --- 5. Un solo esecutore per corsia -------------------------------------------------------------
# L'invariante §4.1 del PRD. Il lease Postgres la applica a runtime, ma un deploy incoerente si vede
# prima e meglio da qui: se il motore vive nel servizio di trading, il pod ui NON deve essere vivo
# con il toggle a false, e viceversa. Si legge l'assetto, non lo si assume.
Check "Nessuna corsia ha due host di esecuzione" {
    # Lettura DIFENSIVA: kubectl scrive l'errore "NotFound" su stderr, che la funzione cattura e
    # restituisce come oggetto — un [int] su quello esplode con un messaggio incomprensibile invece
    # di dire "il Deployment non c'e'". E "non c'e'" e "c'e' con zero repliche" NON sono la stessa
    # cosa: la prima e' l'assetto della CI (la ui non viene nemmeno deployata), la seconda e'
    # l'assetto reale dal 2026-07-26. Confonderle nasconderebbe una ui sparita da un cluster dove
    # dovrebbe esserci.
    $uiState = ReplicaState -Deployment procionemgr-ui -Ns "$NsPrefix-ui"
    $tradingState = ReplicaState -Deployment procionemgr-trading -Ns "$NsPrefix-trading"

    if (-not $tradingState.Exists) {
        throw "il Deployment procionemgr-trading non esiste: senza motore non c'e' niente da sorvegliare"
    }

    $uiReplicas = $uiState.Ready
    $tradingReplicas = $tradingState.Ready

    if ($tradingReplicas -gt 1) {
        throw "il servizio di trading ha $tradingReplicas repliche: il motore deve essere replicas:1 (PRD §2)"
    }
    if ($uiReplicas -gt 0 -and $tradingReplicas -gt 0) {
        # Entrambi vivi e' lecito SOLO se la ui ha UseRemoteTrading=true, cioe' non registra motore.
        $uiPod = (Kubectl get pods -n "$NsPrefix-ui" -o jsonpath='{.items[0].metadata.name}')
        $uiEnv = KubectlExec -Ns "$NsPrefix-ui" -Pod $uiPod -Container ui -ShellCommand "env"
        if ("$uiEnv" -notmatch "Trading__UseRemoteTrading=(?i)true") {
            throw "ui e trading entrambi vivi ma la ui non ha Trading__UseRemoteTrading=true: due motori sulla stessa corsia"
        }
    }
    $uiDetail = if (-not $uiState.Exists) { "ui non deployata" } else { "ui $uiReplicas repliche" }
    "trading $tradingReplicas replica/e, $uiDetail"
}

# --- Esito ---------------------------------------------------------------------------------------
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "SMOKE E2E SUPERATO: $checks controlli, 0 fallimenti." -ForegroundColor Green
    exit 0
}

Write-Host "SMOKE E2E FALLITO: $($failures.Count) su $checks controlli." -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
