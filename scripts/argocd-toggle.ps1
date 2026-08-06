# =============================================================================================
#  Accende e spegne ArgoCD su richiesta.
#
#  PERCHE' SPENTO DI NORMA (2026-08-05): ArgoCD e' 7 pod su un cluster kind mono-nodo di un solo
#  sviluppatore. Misurato: spegnerlo ha portato il nodo da 2,306 a 1,66 GiB e la CPU a vuoto dal
#  57% al 26%, e ha FERMATO il crash-loop di kube-scheduler e kube-controller-manager (erano a
#  418 e 422 riavvii: perdevano il lease perche' non raggiungevano l'API server in tempo).
#
#  PERCHE' SU RICHIESTA E NON PROGRAMMATO: **nessuna Application ha autosync** (verificato:
#  `syncPolicy.automated` e' <none> su tutte e 8). ArgoCD non stava sincronizzando NIENTE da solo
#  — ogni deploy era gia' manuale. Tenerlo acceso, o riaccenderlo a orari fissi, costerebbe
#  memoria e CPU per un lavoro che non fa. Ha senso solo quando serve DAVVERO: davanti a un
#  deploy, o per guardare la sync UI.
#
#  COSA NON DIPENDE DA ARGOCD, e quindi continua a funzionare da spento:
#    - i CronJob del cluster (sono oggetti Kubernetes gia' presenti: ArgoCD ne sincronizza la
#      DEFINIZIONE, non li esegue). Verificato: exitlag-monthly e' partito regolarmente con
#      ArgoCD gia' spento;
#    - i tre servizi della piattaforma (ingestion, ml, trading), che restano Running;
#    - i deploy fatti a mano con `kubectl apply -k infra/k8s/...`, che e' come si e' sempre
#      deployato il trading.
#
#  USO
#    .\scripts\argocd-toggle.ps1 -Up      accende e ASPETTA che sia pronto
#    .\scripts\argocd-toggle.ps1 -Down    spegne
#    .\scripts\argocd-toggle.ps1          dice solo com'e' adesso
# =============================================================================================
param(
    [switch]$Up,
    [switch]$Down
)

$ErrorActionPreference = 'Continue'

$context = 'kind-procionemgr-dev'
$ns = 'argocd'
# Lo statefulset del controller si scala a parte: `scale deployment --all` non lo tocca, e
# dimenticarlo lascia acceso il pod piu' pesante dei sette.
$sts = 'argocd-application-controller'

function Get-ArgoState {
    $pods = kubectl --context $context -n $ns get pods --no-headers 2>$null
    if ([string]::IsNullOrWhiteSpace($pods)) { return 0 }
    return @($pods -split "`n" | Where-Object { $_.Trim() }).Count
}

if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
    Write-Host "ArgoCD   : kubectl non trovato." -ForegroundColor Yellow
    exit 0
}

if (-not (kubectl --context $context get ns $ns 2>$null)) {
    Write-Host "ArgoCD   : cluster non raggiungibile o namespace assente." -ForegroundColor Yellow
    Write-Host "           Se Docker e' appena ripartito: docker start kind-apiproxy" -ForegroundColor Yellow
    exit 0
}

if ($Down) {
    kubectl --context $context -n $ns scale deployment --all --replicas=0 2>&1 | Out-Null
    kubectl --context $context -n $ns scale statefulset $sts --replicas=0 2>&1 | Out-Null
    Write-Host "ArgoCD   : spento. I CronJob e i tre servizi della piattaforma NON sono toccati." -ForegroundColor Green
    Write-Host "           Riaccendilo prima di un deploy che passa da ArgoCD: -Up" -ForegroundColor DarkGray
    exit 0
}

if ($Up) {
    kubectl --context $context -n $ns scale deployment --all --replicas=1 2>&1 | Out-Null
    kubectl --context $context -n $ns scale statefulset $sts --replicas=1 2>&1 | Out-Null
    Write-Host "ArgoCD   : acceso, attendo che i pod siano pronti..." -ForegroundColor Cyan

    # Si ASPETTA e si VERIFICA: dire "acceso" appena dopo lo scale sarebbe una rassicurazione,
    # non un fatto. Su questa macchina i pod ArgoCD ci mettono un po' a diventare Ready.
    $ok = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 6
        $notReady = kubectl --context $context -n $ns get pods --no-headers 2>$null |
                    Where-Object { $_ -notmatch '\s+([1-9]\d*)/\1\s+Running' }
        if ((Get-ArgoState) -gt 0 -and -not $notReady) { $ok = $true; break }
    }
    if ($ok) {
        Write-Host "ArgoCD   : pronto ($(Get-ArgoState) pod)." -ForegroundColor Green
        Write-Host "           A deploy finito, spegnilo: .\scripts\argocd-toggle.ps1 -Down" -ForegroundColor DarkGray
        exit 0
    }
    Write-Host "ArgoCD   : acceso ma NON tutti i pod sono pronti dopo 4 minuti - guarda 'kubectl -n argocd get pods'." -ForegroundColor Yellow
    exit 1
}

# Nessun parametro: si dice solo com'e'.
$n = Get-ArgoState
if ($n -eq 0) {
    Write-Host "ArgoCD   : SPENTO (0 pod). Nessun sync GitOps; i deploy manuali con kubectl funzionano." -ForegroundColor Cyan
} else {
    Write-Host "ArgoCD   : acceso ($n pod)." -ForegroundColor Cyan
}
exit 0
