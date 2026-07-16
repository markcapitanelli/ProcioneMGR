<#
.SYNOPSIS
    Installa ArgoCD sul cluster kind e applica la root Application (Fase 3 microservizi).

.DESCRIPTION
    - Installa ArgoCD nel namespace 'argocd' da un manifest a VERSIONE PINNATA (mai 'stable': è un
      bersaglio mobile, e un cluster ricreato domani avrebbe un ArgoCD diverso senza che nulla in
      Git lo dica — la stessa ragione per cui i tag delle nostre immagini non sono ':latest').
    - Applica infra/gitops/root-app.yaml (app-of-apps): da lì in poi ArgoCD scopre le Application
      figlie da solo leggendo infra/gitops/apps/.
    - NON sincronizza nulla: tutte le Application nascono OutOfSync e aspettano un Sync manuale.
      È voluto — è lo stesso gate umano applicato ovunque nel progetto (nessuna promozione
      automatica). Vedi infra/gitops/README.md.
    - Idempotente: rilanciabile senza danni.

.PARAMETER TargetRevision
    Branch/tag/sha che ArgoCD deve seguire. Default 'master'. Serve per provare un branch PRIMA del
    merge: i file committati restano puntati a master (nessuno deve ricordarsi di rimetterli a posto
    dopo un test), e il ripuntamento avviene solo sugli oggetti vivi nel cluster.

.PARAMETER ArgoCdVersion
    Versione di ArgoCD da installare. Pinnata di default.

.NOTES
    Prerequisito: cluster già creato (scripts\k8s-bootstrap.ps1).
    Teardown: scripts\k8s-teardown.ps1 distrugge il cluster e con esso ArgoCD (zero residui).
    Uso:  .\scripts\k8s-argocd-bootstrap.ps1
          .\scripts\k8s-argocd-bootstrap.ps1 -TargetRevision claude/fase3-gitops-ui-deploy
#>

param(
    [string]$TargetRevision = "master",
    [string]$ArgoCdVersion = "v2.13.2"
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\k8s-common.ps1"
$repoRoot = Split-Path -Parent $PSScriptRoot
$rootApp = Join-Path $repoRoot "infra\gitops\root-app.yaml"
$clusterName = $script:KindClusterName
$ctx = "kind-$clusterName"
$installUrl = "https://raw.githubusercontent.com/argoproj/argo-cd/$ArgoCdVersion/manifests/install.yaml"

foreach ($tool in @("kind", "kubectl")) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Host "ERRORE: '$tool' non trovato nel PATH. Installa con: winget install Kubernetes.$tool" -ForegroundColor Red
        exit 1
    }
}

if (-not (Test-KindCluster $clusterName)) {
    Write-Host "ERRORE: cluster '$clusterName' inesistente. Lancia prima .\scripts\k8s-bootstrap.ps1" -ForegroundColor Red
    exit 1
}

Write-Host "Creo il namespace 'argocd'..." -ForegroundColor Cyan
kubectl create namespace argocd --dry-run=client -o yaml --context $ctx | kubectl apply --context $ctx -f -
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Installo ArgoCD $ArgoCdVersion..." -ForegroundColor Cyan
kubectl apply -n argocd -f $installUrl --context $ctx
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Attendo che argocd-server sia pronto (puo' richiedere qualche minuto al primo giro)..." -ForegroundColor Cyan
kubectl rollout status deployment/argocd-server -n argocd --timeout=300s --context $ctx
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Applico la root Application (app-of-apps)..." -ForegroundColor Cyan
kubectl apply -f $rootApp --context $ctx
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($TargetRevision -ne "master") {
    # Ripunta SOLO l'oggetto vivo, non il file: root-app.yaml in Git resta su master. Le Application
    # figlie nascono da Git (quindi anch'esse su master): appena root-app viene sincronizzata,
    # vanno ripuntate a loro volta — il messaggio finale lo ricorda.
    Write-Host "Ripunto root-app su '$TargetRevision' (solo nel cluster, il file resta su master)..." -ForegroundColor Yellow
    # Le virgolette del JSON vanno escapate come \" : Windows PowerShell 5.1 le perde quando passa
    # l'argomento a un eseguibile nativo, e kubectl riceverebbe un JSON malformato.
    $patch = '{\"spec\":{\"source\":{\"targetRevision\":\"' + $TargetRevision + '\"}}}'
    kubectl patch application procionemgr-root -n argocd --type merge -p $patch --context $ctx
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$pwdB64 = kubectl get secret argocd-initial-admin-secret -n argocd -o jsonpath='{.data.password}' --context $ctx 2>$null
$adminPwd = if ($pwdB64) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($pwdB64)) } else { "(gia' ruotata o Secret rimosso)" }

Write-Host ""
Write-Host "ArgoCD pronto." -ForegroundColor Green
Write-Host ""
Write-Host "  kubectl port-forward svc/argocd-server -n argocd 8081:443 --context $ctx" -ForegroundColor White
Write-Host "  -> https://localhost:8081   (certificato self-signed: il browser avvisera')" -ForegroundColor White
Write-Host "  utente: admin" -ForegroundColor White
Write-Host "  password: $adminPwd" -ForegroundColor White
Write-Host ""
Write-Host "La password e' stampata qui e MAI scritta su file (come per la master key)." -ForegroundColor DarkGray
Write-Host "Porta 8081 per non collidere con 8080/8443, gia' mappate dal kind-config." -ForegroundColor DarkGray
Write-Host ""
Write-Host "Le Application nascono OutOfSync: il Sync e' MANUALE, di proposito." -ForegroundColor Yellow
Write-Host "Prima di sincronizzare, crea i Secret (non sono in Git, ArgoCD non li tocca):" -ForegroundColor Yellow
Write-Host "  .\scripts\k8s-postgres-secret.ps1 -ConnectionString ..." -ForegroundColor White
Write-Host "  .\scripts\k8s-trading-secret.ps1  -ConnectionString ...   # + `$env:PROCIONE_MGR_MASTER_KEY" -ForegroundColor White
Write-Host "  .\scripts\k8s-ui-secret.ps1       -ConnectionString ...   # STESSA master key" -ForegroundColor White
Write-Host ""
Write-Host "Ordine di Sync: namespaces -> shared -> ingestion/ml -> trading -> ui -> jobs" -ForegroundColor Yellow
if ($TargetRevision -ne "master") {
    Write-Host ""
    Write-Host "NB: root-app punta a '$TargetRevision', ma il branch deve essere PUSHATO su GitHub:" -ForegroundColor Yellow
    Write-Host "    ArgoCD legge dal remoto, non dal disco locale." -ForegroundColor Yellow
    Write-Host "    Dopo il primo Sync di root-app, le Application figlie nasceranno puntate a master" -ForegroundColor Yellow
    Write-Host "    (e' cio' che dice il file in Git): per provarle sul branch, ripuntale con" -ForegroundColor Yellow
    Write-Host "    .\scripts\k8s-argocd-retarget.ps1 -TargetRevision $TargetRevision" -ForegroundColor White
}
