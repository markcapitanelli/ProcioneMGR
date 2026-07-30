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
    OPZIONALE. Token del bot Telegram per le notifiche EMESSE DAL MOTORE. Tre sorgenti in cascata:
    questo parametro, poi $env:TELEGRAM_BOT_TOKEN, poi il file ~/.procione/telegram.token — la
    stessa sorgente che usa il profilo di avvio in .claude/launch.json, cosi' il token vive in un
    solo posto e nessuno deve ricordarsi di esportare una variabile.

    Se nessuna sorgente ha un token MA il Secret sul cluster ne ha gia' uno, quello viene
    PRESERVATO: questo script ricrea il Secret intero, quindi una chiave assente dai literal
    verrebbe cancellata, e un rilancio distratto spegnerebbe gli allarmi di quarantena in silenzio.

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
        .\scripts\k8s-trading-secret.ps1 -ConnectionString "Host=host.docker.internal;Port=5432;..."
    Il token Telegram NON va esportato: lo script legge ~/.procione/telegram.token da se'.
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

# --- Token Telegram del MOTORE: tre sorgenti in cascata, e una rete di sicurezza --------------
#
# Perche' la cascata: il parametro serve agli script, la env serve alle sessioni interattive, ma
# ENTRAMBI dipendono dal ricordarsene. Il FILE e' l'unica sorgente che non dipende dalla memoria di
# nessuno, ed e' la stessa che usa il profilo di avvio dell'app (.claude/launch.json): un solo posto
# dove il token vive, letto da tutti quelli che ne hanno bisogno.
$telegramSource = $null
if ($TelegramBotToken) { $telegramSource = 'parametro' }

if (-not $TelegramBotToken -and $env:TELEGRAM_BOT_TOKEN) {
    $TelegramBotToken = $env:TELEGRAM_BOT_TOKEN
    $telegramSource = 'variabile d''ambiente'
}

if (-not $TelegramBotToken) {
    $tokenFile = Join-Path $env:USERPROFILE '.procione\telegram.token'
    if (Test-Path $tokenFile) {
        $TelegramBotToken = (Get-Content $tokenFile -Raw).Trim()
        $telegramSource = "file ~/.procione/telegram.token"
    }
}

# LA RETE DI SICUREZZA, e il motivo per cui vale la pena di tutto questo.
#
# Questo script ricrea il Secret INTERO (create --dry-run | apply): una chiave assente dai literal
# non resta com'era, VIENE CANCELLATA. Quindi un rilancio senza token — dopo aver cambiato macchina,
# o con il file non ancora copiato — spegnerebbe le notifiche del motore in silenzio, e con esse gli
# allarmi di QUARANTENA, esattamente il guasto scoperto il 2026-07-29 e appena chiuso.
#
# Se non c'e' alcuna sorgente ma il Secret sul cluster ha gia' un token, lo si RIUSA invece di
# perderlo. Distruggere una configurazione funzionante non e' mai il comportamento giusto per
# l'assenza di un input opzionale.
if (-not $TelegramBotToken) {
    $existing = kubectl get secret trading-secrets -n $namespace --context $clusterCtx `
        -o "jsonpath={.data.TELEGRAM_BOT_TOKEN}" 2>$null
    if ($existing) {
        $TelegramBotToken = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($existing))
        $telegramSource = 'Secret esistente (preservato)'
        Write-Host "Telegram : nessun token fornito - PRESERVO quello gia' nel Secret." -ForegroundColor Yellow
    }
}

if ($TelegramBotToken) {
    # Solo forma, mai il valore: un token Telegram e' <id-numerico>:<segreto>.
    if ($TelegramBotToken -notmatch '^[0-9]{6,}:[A-Za-z0-9_-]{30,}$') {
        Write-Host "ERRORE: il token Telegram non ha la forma <id>:<segreto>. Sorgente: $telegramSource." -ForegroundColor Red
        Write-Host "Meglio fermarsi che scrivere un token malformato: il motore fallirebbe ogni recapito in silenzio." -ForegroundColor Yellow
        exit 1
    }
    Write-Host "Telegram : token preso da $telegramSource (bot id $(($TelegramBotToken -split ':')[0]))." -ForegroundColor Green
} else {
    Write-Host "Telegram : NESSUN token, ne' fornito ne' presente nel Secret." -ForegroundColor Yellow
    Write-Host "           Gli allarmi del motore (quarantena corsie) non raggiungeranno nessuno via Telegram." -ForegroundColor Yellow
    Write-Host "           Metti il token in ~/.procione/telegram.token e rilancia; verificalo poi con" -ForegroundColor Yellow
    Write-Host "           'Prova dal motore' in /admin/autonomy." -ForegroundColor Yellow
}

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
