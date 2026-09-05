# =============================================================================================
#  Build LOCALE delle immagini ProcioneMGR + import nel nodo kind [Fase 4 PRD-RISANAMENTO].
#
#  PERCHE' ESISTE: l'unica dipendenza runtime da GitHub era il pull delle immagini da
#  ghcr.io/markcapitanelli/* nei manifesti K8s. Con questo script le immagini si costruiscono
#  dalla copia locale del repo e si importano direttamente nel containerd del nodo kind: il
#  cluster riparte anche a rete staccata (insieme a imagePullPolicy: Never nei manifesti, che
#  vieta al kubelet perfino di PROVARE il pull).
#
#  COME IMPORTA -- la via validata (memoria 2026-08): la CLI `kind` non e' installata su questa
#  macchina e Git Bash converte i percorsi, quindi l'unica strada affidabile e'
#      docker save <img> | docker exec -i <nodo> ctr -n k8s.io images import -
#  ATTENZIONE PowerShell: il pipe di PS e' a oggetti/testo e CORROMPE lo stream binario del tar.
#  Il pipe passa quindi da cmd.exe (byte-stream puro). La verifica finale con crictl non e'
#  cortesia: un import silenziosamente fallito lascerebbe il cluster a puntare immagini vecchie.
#
#  DOPPIO TAG per immagine:
#    - procionemgr/<nome>:local           il nome onesto della provenienza locale;
#    - ghcr.io/markcapitanelli/<nome>:latest   il nome che i manifesti usano gia' -- cosi' i
#      Deployment risolvono i bit locali senza riscrivere ogni riferimento, e chi un giorno
#      volesse tornare alla CI non trova manifesti divergenti.
#
#  USO:
#    ./scripts/build-images-local.ps1                      # build+import dei 4 servizi (default)
#    ./scripts/build-images-local.ps1 -Targets all         # anche strategyhunter e dbbackup
#    ./scripts/build-images-local.ps1 -Targets procionemgr-trading
#    ./scripts/build-images-local.ps1 -SkipImport          # solo build (es. macchina senza kind)
#
#  NON riavvia i pod: il rollout e' una decisione separata (kubectl rollout restart), perche'
#  riavviare il motore di trading e' un atto operativo, non un dettaglio di build.
# =============================================================================================

param(
    # "core" = i 4 servizi (ui, ingestion, ml, trading); "all" aggiunge i tool batch;
    # oppure un elenco esplicito di target del Dockerfile.
    [string[]]$Targets = @('core'),
    [switch]$SkipImport,
    [string]$KindNode = 'procionemgr-dev-control-plane'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$coreTargets = @('procionemgr', 'procionemgr-ingestion', 'procionemgr-ml', 'procionemgr-trading')
$allTargets = $coreTargets + @('strategyhunter', 'dbbackup')

$resolved = switch ($Targets[0]) {
    'core' { $coreTargets }
    'all'  { $allTargets }
    default { $Targets }
}
foreach ($t in $resolved) {
    if ($allTargets -notcontains $t) {
        throw "Target sconosciuto '$t'. Validi: $($allTargets -join ', '), oppure 'core'/'all'."
    }
}

# Il PIN della promozione: local-<sha del commit>. E' l'equivalente locale del digest nei
# kustomization (un build locale non ha un digest di registro verificabile): il tag dice da quale
# sorgente viene il binario, e il bump nel kustomization resta la promozione — stessa filosofia
# GitOps, stessa forma del precedente local-c026a67 (guasto GitHub 2026-08-06).
Push-Location $repoRoot
try { $sha = (git rev-parse --short=8 HEAD).Trim(); $fullSha = (git rev-parse HEAD).Trim() } finally { Pop-Location }
if (-not $sha) { throw "git rev-parse fallito: impossibile calcolare il tag della promozione." }
$pinnedSuffix = "local-$sha"

Write-Host "Build locale da $repoRoot (commit $sha) -- target: $($resolved -join ', ')" -ForegroundColor Cyan

# --- 1. Build (un target alla volta: il layer di build condiviso viene riusato dalla cache) ---
foreach ($t in $resolved) {
    $localTag = "procionemgr/${t}:local"
    $pinnedTag = "ghcr.io/markcapitanelli/${t}:$pinnedSuffix"
    Write-Host "== docker build --target $t ==" -ForegroundColor Cyan
    # [2026-09-05] Il timbro della revisione entra nel binario (AssemblyInformationalVersion +sha):
    # senza, il motore batteva «senza timbro» e il confronto guscio↔motore non misurava nulla.
    docker build --target $t --build-arg "SOURCE_REVISION=$fullSha" -t $localTag -t $pinnedTag $repoRoot
    if ($LASTEXITCODE -ne 0) { throw "Build di '$t' fallita (exit $LASTEXITCODE)." }
}

if ($SkipImport) {
    Write-Host "Import saltato (-SkipImport). Immagini taggate: $($resolved -join ', ')" -ForegroundColor Yellow
    return
}

# --- 2. Import nel containerd del nodo kind ---
$nodeRunning = docker ps --format '{{.Names}}' | Where-Object { $_ -eq $KindNode }
if (-not $nodeRunning) {
    throw "Nodo kind '$KindNode' non in esecuzione: import impossibile. (docker ps per l'elenco; -SkipImport per il solo build.)"
}

foreach ($t in $resolved) {
    $pinnedTag = "ghcr.io/markcapitanelli/${t}:$pinnedSuffix"
    Write-Host "== import $pinnedTag -> $KindNode ==" -ForegroundColor Cyan
    # cmd.exe, non PowerShell: serve un pipe a byte per il tar (vedi intestazione).
    cmd /c "docker save $pinnedTag | docker exec -i $KindNode ctr -n k8s.io images import -"
    if ($LASTEXITCODE -ne 0) { throw "Import di '$t' fallito (exit $LASTEXITCODE)." }
}

# --- 3. Verifica con crictl: l'immagine dev'essere VISIBILE dal runtime che i kubelet usano ---
Write-Host "== verifica crictl ==" -ForegroundColor Cyan
$images = docker exec $KindNode crictl images 2>$null | Out-String
$missing = @()
foreach ($t in $resolved) {
    if ($images -match ([regex]::Escape("ghcr.io/markcapitanelli/$t") + "\s+" + [regex]::Escape($pinnedSuffix))) {
        Write-Host "  OK  ghcr.io/markcapitanelli/${t}:$pinnedSuffix presente nel nodo" -ForegroundColor Green
    }
    else {
        $missing += $t
        Write-Host "  MANCA ${t}:$pinnedSuffix" -ForegroundColor Red
    }
}
if ($missing.Count -gt 0) {
    throw "Import non verificato per: $($missing -join ', ') -- il cluster userebbe immagini vecchie."
}

Write-Host ""
Write-Host "Fatto. Immagini importate col tag della promozione: $pinnedSuffix" -ForegroundColor Cyan
Write-Host "Prossimi passi (il bump E' la promozione, resta una scelta):" -ForegroundColor Cyan
Write-Host "  1. nei kustomization.yaml dei servizi: newTag: $pinnedSuffix (rimuovendo un eventuale digest:)"
Write-Host "  2. kubectl apply -k infra/k8s/<servizio> per ogni servizio da promuovere"
Write-Host "(il riavvio del motore e' un atto operativo: le corsie recuperano lo stato dal DB.)"
