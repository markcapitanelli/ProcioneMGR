<#
.SYNOPSIS
    Crea il Secret 'ui-secrets' nel namespace procionemgr-ui (Fase 3 microservizi).

.DESCRIPTION
    Gemello di k8s-trading-secret.ps1: connection string + MASTER KEY. Il monolite ne ha bisogno per
    lo stesso motivo del servizio di trading — decifrare le credenziali exchange (es. il "Test
    credenziali REALE" in /settings/exchanges).

    LA MASTER KEY DEVE ESSERE LA STESSA di trading-secrets. Un Secret Kubernetes è un oggetto
    NAMESPACED: non esiste modo di condividerlo fra procionemgr-ui e procionemgr-trading senza
    aggiungere un operatore (Reflector, External Secrets, Sealed Secrets). Quindi è per forza una
    COPIA, e nessun meccanismo impedisce che le due divergano nel tempo — per esempio ruotandone una
    sola. Se succede, le credenziali cifrate nel DB si decifrano da una parte e non dall'altra, in
    silenzio, fino al primo tentativo di operazione. Per questo lo script raccomanda di impostare
    $env:PROCIONE_MGR_MASTER_KEY UNA volta e lanciare i due script nella STESSA sessione di shell.

    NB: un Secret Kubernetes è codificato base64, NON cifrato. Chiunque possa leggere i Secret del
    namespace legge la chiave in chiaro. Oltre lo sviluppo locale servono RBAC stretto +
    encryption-at-rest di etcd (o Vault/Sealed Secrets).

.PARAMETER ConnectionString
    Connection string PostgreSQL. Se omessa, si legge da $env:ConnectionStrings__PostgresConnection.

.PARAMETER MasterKey
    Master key AES (base64 di 32 byte). Se omessa, si legge da $env:PROCIONE_MGR_MASTER_KEY.

.PARAMETER GrpcSharedSecret
    Segreto condiviso per l'autorizzazione applicativa sul gRPC di trading (P1-6). Se omesso, si
    legge da $env:PROCIONE_MGR_TRADING_GRPC_SECRET. DEVE essere lo STESSO di trading-secrets:
    e' l'header che il monolite manda a ogni chiamata verso procionemgr-trading.

.NOTES
    Uso (chiave dalla env, così non finisce nella cronologia della shell):
        $env:PROCIONE_MGR_MASTER_KEY = "<base64 32 byte>"
        $env:PROCIONE_MGR_TRADING_GRPC_SECRET = "<stringa casuale, es. openssl rand -base64 32>"
        .\scripts\k8s-trading-secret.ps1 -ConnectionString "Host=host.docker.internal;Port=5432;..."
        .\scripts\k8s-ui-secret.ps1      -ConnectionString "Host=host.docker.internal;Port=5432;..."
    Il namespace deve già esistere (scripts\k8s-bootstrap.ps1).
#>

param([string]$ConnectionString, [string]$MasterKey, [string]$GrpcSharedSecret)

$ErrorActionPreference = "Stop"
$clusterCtx = "kind-procionemgr-dev"
$namespace = "procionemgr-ui"

if (-not $ConnectionString) { $ConnectionString = $env:ConnectionStrings__PostgresConnection }
if (-not $ConnectionString) {
    Write-Host "ERRORE: passa -ConnectionString oppure imposta `$env:ConnectionStrings__PostgresConnection." -ForegroundColor Red
    exit 1
}

if (-not $MasterKey) { $MasterKey = $env:PROCIONE_MGR_MASTER_KEY }
if (-not $MasterKey) {
    Write-Host "ERRORE: passa -MasterKey oppure imposta `$env:PROCIONE_MGR_MASTER_KEY." -ForegroundColor Red
    Write-Host "Deve essere la STESSA master key di trading-secrets, altrimenti le credenziali exchange non si decifrano." -ForegroundColor Yellow
    exit 1
}

# Controllo di forma (non di segretezza): 32 byte base64. Una chiave malformata farebbe fallire il
# pod a startup con un errore di derivazione, molto più tardi e molto meno chiaro di qui.
try {
    $keyBytes = [Convert]::FromBase64String($MasterKey)
} catch {
    Write-Host "ERRORE: la master key non e' base64 valido." -ForegroundColor Red
    exit 1
}
if ($keyBytes.Length -ne 32) {
    Write-Host "ERRORE: la master key decodificata e' di $($keyBytes.Length) byte, attesi 32 (AES-256)." -ForegroundColor Red
    exit 1
}

if (-not $GrpcSharedSecret) { $GrpcSharedSecret = $env:PROCIONE_MGR_TRADING_GRPC_SECRET }
if (-not $GrpcSharedSecret) {
    Write-Host "ERRORE: passa -GrpcSharedSecret oppure imposta `$env:PROCIONE_MGR_TRADING_GRPC_SECRET." -ForegroundColor Red
    Write-Host "Deve essere lo STESSO segreto di trading-secrets, altrimenti il monolite non puo' chiamare procionemgr-trading." -ForegroundColor Yellow
    exit 1
}

Write-Host "Creo/aggiorno Secret 'ui-secrets' in $namespace..." -ForegroundColor Cyan
# --dry-run=client | apply: idempotente (crea o aggiorna senza errore se gia' esiste).
kubectl create secret generic ui-secrets `
    --namespace $namespace `
    --from-literal=ConnectionStrings__PostgresConnection=$ConnectionString `
    --from-literal=Security__MasterKey=$MasterKey `
    --from-literal=Trading__GrpcSharedSecret=$GrpcSharedSecret `
    --dry-run=client -o yaml --context $clusterCtx | kubectl apply --context $clusterCtx -f -
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Secret 'ui-secrets' pronto in $namespace (connection string + master key + segreto gRPC)." -ForegroundColor Green

# Confronto attivo con trading-secrets: la divergenza fra le due copie e' un errore silenzioso
# (fallisce solo al primo uso delle credenziali/della prima chiamata gRPC), quindi vale la pena
# scoprirlo adesso invece che affidarsi alla disciplina di chi lancia gli script.
$tradingKeyB64 = kubectl get secret trading-secrets -n procionemgr-trading -o jsonpath='{.data.Security__MasterKey}' --context $clusterCtx 2>$null
if ($LASTEXITCODE -eq 0 -and $tradingKeyB64) {
    $tradingKey = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($tradingKeyB64))
    if ($tradingKey -ne $MasterKey) {
        Write-Host "ATTENZIONE: la master key di 'trading-secrets' e' DIVERSA da quella appena scritta in 'ui-secrets'." -ForegroundColor Red
        Write-Host "Le credenziali exchange cifrate nel DB saranno decifrabili da un solo servizio: allineale." -ForegroundColor Red
    } else {
        Write-Host "Verificato: master key allineata con 'trading-secrets'." -ForegroundColor Green
    }
} else {
    Write-Host "Nota: 'trading-secrets' non ancora presente, nessun confronto possibile." -ForegroundColor DarkGray
    Write-Host "Quando lo creerai, usa la STESSA master key (stessa sessione, stessa `$env:PROCIONE_MGR_MASTER_KEY)." -ForegroundColor DarkGray
}

$tradingSecretB64 = kubectl get secret trading-secrets -n procionemgr-trading -o jsonpath='{.data.Trading__GrpcSharedSecret}' --context $clusterCtx 2>$null
if ($LASTEXITCODE -eq 0 -and $tradingSecretB64) {
    $tradingSecretValue = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($tradingSecretB64))
    if ($tradingSecretValue -ne $GrpcSharedSecret) {
        Write-Host "ATTENZIONE: il segreto gRPC di 'trading-secrets' e' DIVERSO da quello appena scritto in 'ui-secrets'." -ForegroundColor Red
        Write-Host "Ogni chiamata del monolite verso procionemgr-trading verra' rifiutata Unauthenticated: allineali." -ForegroundColor Red
    } else {
        Write-Host "Verificato: segreto gRPC allineato con 'trading-secrets'." -ForegroundColor Green
    }
} else {
    Write-Host "Nota: 'trading-secrets' non ancora presente, nessun confronto possibile per il segreto gRPC." -ForegroundColor DarkGray
}
