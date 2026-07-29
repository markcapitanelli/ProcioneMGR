<#
.SYNOPSIS
    Crea il Secret 'trading-secrets' nel namespace procionemgr-trading (Fase 2b microservizi).

.DESCRIPTION
    Script separato da k8s-postgres-secret.ps1 di proposito: questo Secret contiene ANCHE la
    MASTER KEY, un salto di sensibilità rispetto a tutti gli altri servizi. È l'unico satellite che
    la riceve, perché deve decifrare le credenziali exchange per firmare gli ordini Testnet/Live —
    ingestion e ml usano un IEncryptionService no-op e non ne hanno bisogno.

    Chi ha questa chiave + il DB può decifrare le credenziali exchange e operare sui conti reali.
    Non metterla in un YAML committato, non passarla su una riga di comando che finisce nella
    cronologia della shell (usa le env), non copiarla in namespace che non siano procionemgr-trading.

    NB: un Secret Kubernetes è codificato base64, NON cifrato: chiunque possa leggere i Secret del
    namespace legge la chiave in chiaro. Per un uso oltre lo sviluppo locale servono RBAC stretto
    sui Secret + encryption-at-rest di etcd (o un gestore esterno tipo Vault/Sealed Secrets).

.PARAMETER ConnectionString
    Connection string PostgreSQL. Se omessa, si legge da $env:ConnectionStrings__PostgresConnection.

.PARAMETER MasterKey
    Master key AES (base64 di 32 byte). Se omessa, si legge da $env:PROCIONE_MGR_MASTER_KEY.
    DEVE essere la STESSA del monolite: le credenziali sono cifrate con quella: con una chiave
    diversa il servizio parte e fallisce solo al primo ordine Testnet/Live, non prima.

.PARAMETER GrpcSharedSecret
    Segreto condiviso per l'autorizzazione applicativa sul gRPC di trading (P1-6). Se omesso, si
    legge da $env:PROCIONE_MGR_TRADING_GRPC_SECRET. DEVE essere lo STESSO del monolite (ui-secrets):
    con un valore diverso ogni chiamata gRPC del monolite verso questo servizio viene rifiutata
    Unauthenticated da SharedSecretAuthInterceptor.

.PARAMETER TelegramBotToken
    OPZIONALE. Token del bot Telegram per le notifiche EMESSE DAL MOTORE. Se omesso, si legge da
    $env:TELEGRAM_BOT_TOKEN; se manca anche quella, la chiave non viene scritta nel Secret e il
    motore resta senza canale Telegram (le notifiche ripiegano sul log).

    PERCHE' SERVE, scoperto il 2026-07-29: il producer piu' importante della piattaforma — il
    watchdog che mette una corsia in QUARANTENA — vive in QUESTO processo, non nel guscio. Il
    guscio aveva il suo token e recapitava; il motore non l'ha mai avuto, quindi quegli allarmi
    non sono mai arrivati a nessuno. Nessuno se n'era accorto perche' il dispatcher per contratto
    non propaga gli errori di recapito, e fino a quel giorno non esisteva un modo di CHIEDERE al
    canale se funzionasse (ora c'e': "Prova dal motore" in /admin/autonomy).

.NOTES
    Uso (chiavi dalle env, così non finiscono nella cronologia):
        $env:PROCIONE_MGR_MASTER_KEY = "<base64 32 byte>"
        $env:PROCIONE_MGR_TRADING_GRPC_SECRET = "<stringa casuale, es. openssl rand -base64 32>"
        $env:TELEGRAM_BOT_TOKEN = Get-Content ~/.procione/telegram.token -Raw
        .\scripts\k8s-trading-secret.ps1 -ConnectionString "Host=host.docker.internal;Port=5432;..."
    Il namespace deve già esistere (scripts\k8s-bootstrap.ps1).
#>

param([string]$ConnectionString, [string]$MasterKey, [string]$GrpcSharedSecret, [string]$TelegramBotToken)

$ErrorActionPreference = "Stop"
$clusterCtx = "kind-procionemgr-dev"
$namespace = "procionemgr-trading"

if (-not $ConnectionString) { $ConnectionString = $env:ConnectionStrings__PostgresConnection }
if (-not $ConnectionString) {
    Write-Host "ERRORE: passa -ConnectionString oppure imposta `$env:ConnectionStrings__PostgresConnection." -ForegroundColor Red
    exit 1
}

if (-not $MasterKey) { $MasterKey = $env:PROCIONE_MGR_MASTER_KEY }
if (-not $MasterKey) {
    Write-Host "ERRORE: passa -MasterKey oppure imposta `$env:PROCIONE_MGR_MASTER_KEY." -ForegroundColor Red
    Write-Host "Deve essere la STESSA master key del monolite, altrimenti le credenziali exchange non si decifrano." -ForegroundColor Yellow
    exit 1
}

# Controllo di forma (non di segretezza): 32 byte base64. Una chiave malformata farebbe fallire il
# servizio a startup con un errore di derivazione, molto più tardi e molto meno chiaro di qui.
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
    Write-Host "Deve essere lo STESSO segreto di ui-secrets, altrimenti il monolite non puo' chiamare questo servizio." -ForegroundColor Yellow
    exit 1
}

if (-not $TelegramBotToken) { $TelegramBotToken = $env:TELEGRAM_BOT_TOKEN }

Write-Host "Creo/aggiorno Secret 'trading-secrets' in $namespace..." -ForegroundColor Cyan
# --dry-run=client | apply: idempotente (crea o aggiorna senza errore se gia' esiste).
$literals = @(
    "--from-literal=ConnectionStrings__PostgresConnection=$ConnectionString",
    "--from-literal=Security__MasterKey=$MasterKey",
    "--from-literal=Trading__GrpcSharedSecret=$GrpcSharedSecret"
)
# Il token e' OPZIONALE: senza, il Secret si crea comunque e il motore ripiega sul log. Va
# aggiunto solo se c'e', altrimenti si scriverebbe una chiave vuota che sembra configurata.
if ($TelegramBotToken) { $literals += "--from-literal=TELEGRAM_BOT_TOKEN=$TelegramBotToken" }

kubectl create secret generic trading-secrets `
    --namespace $namespace `
    @literals `
    --dry-run=client -o yaml --context $clusterCtx | kubectl apply --context $clusterCtx -f -
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$telegramState = if ($TelegramBotToken) { "+ token Telegram" } else { "SENZA token Telegram (allarmi del motore solo nel log)" }
Write-Host "Secret 'trading-secrets' pronto in $namespace (connection string + master key + segreto gRPC $telegramState)." -ForegroundColor Green
Write-Host "Ricorda: solo questo namespace deve avere la master key." -ForegroundColor Yellow
