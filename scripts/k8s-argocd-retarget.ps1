<#
.SYNOPSIS
    Ripunta tutte le Application ArgoCD su un branch/tag/sha (Fase 3 microservizi).

.DESCRIPTION
    Serve per provare un branch PRIMA del merge. Agisce SOLO sugli oggetti vivi nel cluster: i file
    in infra/gitops/ restano puntati a master, così nessuno deve ricordarsi di rimetterli a posto
    dopo un test (e un file rimasto puntato a un branch di lavoro sarebbe esattamente il genere di
    residuo che poi fa deployare la cosa sbagliata).

    Va rilanciato DOPO il primo Sync di root-app: le Application figlie nascono da Git, quindi
    nascono puntate a master.

    NB: il branch dev'essere pushato sul remoto — ArgoCD legge da GitHub, non dal disco locale.

.PARAMETER TargetRevision
    Branch/tag/sha da seguire. Usa 'master' per tornare allo stato normale.

.NOTES
    Uso: .\scripts\k8s-argocd-retarget.ps1 -TargetRevision claude/mio-branch
         .\scripts\k8s-argocd-retarget.ps1 -TargetRevision master     # ripristino
#>

param([Parameter(Mandatory = $true)][string]$TargetRevision)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\k8s-common.ps1"
$ctx = "kind-$($script:KindClusterName)"

# Le virgolette del JSON vanno escapate come \" : Windows PowerShell 5.1 le perde quando passa
# l'argomento a un eseguibile nativo, e kubectl riceverebbe un JSON malformato.
$patch = '{\"spec\":{\"source\":{\"targetRevision\":\"' + $TargetRevision + '\"}}}'

$apps = kubectl get applications -n argocd -o jsonpath='{.items[*].metadata.name}' --context $ctx
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not $apps) {
    Write-Host "Nessuna Application trovata nel namespace argocd." -ForegroundColor Yellow
    exit 0
}

foreach ($app in $apps.Split(' ', [StringSplitOptions]::RemoveEmptyEntries)) {
    Write-Host "  $app -> $TargetRevision" -ForegroundColor Cyan
    kubectl patch application $app -n argocd --type merge -p $patch --context $ctx | Out-Null
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host ""
Write-Host "Fatto. Le Application seguono '$TargetRevision' (solo nel cluster: i file restano su master)." -ForegroundColor Green
Write-Host "Il Sync resta MANUALE." -ForegroundColor Yellow
