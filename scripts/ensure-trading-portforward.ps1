# =============================================================================================
#  Garantisce il port-forward verso il core di trading in-cluster, in UN processo kubectl:
#  18092 -> 8080 (gRPC del guscio) e 18093 -> 8081 (health HTTP, per il watchdog).
#
#  PERCHE' ESISTE: con Trading:UseRemoteTrading=true il monolite e' il GUSCIO e comanda il motore
#  via gRPC su localhost:18092. Senza questo tunnel la pagina /trading mostra
#  "Unavailable" e "DATI TRADING NON AGGIORNATI" — mentre il core continua a operare da solo nel
#  cluster, che e' esattamente il punto dell'architettura core caldo/guscio freddo (B3).
#
#  E' successo davvero il 2026-07-27: dopo uno spegnimento improvviso del PC il tunnel e' morto e
#  /trading e' rimasta in errore. La logica viveva solo dentro scripts/run-postgres.ps1, quindi
#  avviare l'app in qualunque altro modo la saltava. Estratta qui per essere richiamabile da
#  ovunque — inclusi i profili di .claude/launch.json, dove un blocco inline con virgolette
#  annidate non veniva eseguito in modo affidabile.
#
#  Idempotente: se il tunnel c'e' gia' ED E' ANCORA BUONO non fa nulla. Non fallisce mai in modo
#  bloccante — se il cluster non c'e', lo dice e lascia partire l'app comunque (il resto della
#  piattaforma funziona).
#
#  --- LA PORTA IN ASCOLTO NON BASTA (2026-07-29) ----------------------------------------------
#  Fino a oggi il controllo era "la 18092 e' in ascolto? allora c'e' gia'". E' insufficiente:
#  quando il pod viene SOSTITUITO (deploy, OOM-kill, rollout) kubectl resta in ascolto sulla porta
#  locale ma il tunnel punta a un pod che non esiste piu'. Lo script diceva "gia' attivo", il guscio
#  otteneva Unavailable, e /trading e /admin/protections mostravano il motore irraggiungibile finche'
#  qualcuno non ricreava il tunnel a mano. Successo DUE VOLTE lo stesso giorno, sui due deploy del
#  motore.
#
#  Il rimedio NON e' una sonda di rete: una connessione TCP verso un forward morto viene accettata
#  in locale e muore dopo, quindi qualunque euristica sul socket e' fragile. Si registra invece il
#  POD a cui il tunnel e' stato aperto e lo si confronta con quello vivo adesso — confronto
#  deterministico, nessuna inferenza sul comportamento della rete.
# =============================================================================================

#  --- ANCHE L'INGESTION HA IL SUO TUNNEL (2026-08-13) ------------------------------------------
#  Il pulsante «Sync now» di /market/watchlist chiama il servizio di ingestione in-cluster su
#  http://localhost:18080 (MarketData:RemoteIngestionUrl). Quel tunnel lo apriva SOLO
#  run-postgres.ps1 — che pero' e' sconsigliato (muore col cluster giu') e non e' la via con cui
#  l'app viene avviata: i profili di .claude/launch.json chiamano questo script, che fino a oggi
#  apriva soltanto le porte del trading. Risultato: «Sync now» falliva con "Rifiuto persistente del
#  computer di destinazione (localhost:18080)" a OGNI avvio, e il tunnel andava aperto a mano —
#  una funzione della UI rotta per costruzione, che si scopre solo provandola.
#  La logica di questo script (identita' del tunnel = nome pod + conteggio restart) e' gia' quella
#  giusta anche per l'ingestion, quindi si PARAMETRIZZA invece di duplicarla: una sola verita' su
#  come si stabilisce se un tunnel e' ancora buono.
param(
    [ValidateSet('trading', 'ingestion')]
    [string[]]$Targets = @('trading', 'ingestion')
)

$ErrorActionPreference = 'Continue'

#  --- LA PORTA HEALTH VIAGGIA NELLO STESSO TUNNEL (2026-08-11) --------------------------------
#  La 8080 del servizio e' gRPC h2c-only: a un GET HTTP/1.x risponde SEMPRE 400. Il watchdog
#  interrogava http://localhost:18092/health e quindi non poteva mai vedere il motore sano —
#  per mesi, senza che nessuno se ne accorgesse (l'anti-spam sulle transizioni ha taciuto il
#  falso "giu'" permanente). Il servizio espone da sempre una porta health HTTP dedicata (8081):
#  da oggi il tunnel la porta su 18093, nello stesso processo kubectl del 18092.
$context = 'kind-procionemgr-dev'

# I due tunnel, con la stessa disciplina. Il trading ne ha DUE porte nello stesso processo kubectl
# (gRPC + health); l'ingestion una sola (HTTP), quindi PortMap ha un elemento solo.
$tunnels = @{
    trading   = @{
        Etichetta = 'Trading  '
        Namespace = 'procionemgr-trading'
        Service   = 'svc/procionemgr-trading'
        Component = 'trading'
        PortMap   = @('18092:8080', '18093:8081')
        Porte     = @(18092, 18093)
        Marker    = Join-Path $env:TEMP 'procionemgr-trading-portforward.pod'
        Serve     = '/trading e il comando del motore via gRPC'
    }
    ingestion = @{
        Etichetta = 'Ingestion'
        Namespace = 'procionemgr-ingestion'
        Service   = 'svc/procionemgr-ingestion'
        Component = 'ingestion'
        PortMap   = @('18080:8080')
        Porte     = @(18080)
        Marker    = Join-Path $env:TEMP 'procionemgr-ingestion-portforward.pod'
        Serve     = "il pulsante 'Sync now' di /market/watchlist"
    }
}

function Test-PortListening([int]$p) {
    return [bool](Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue)
}

# Il selettore e' quello del SERVICE (app.kubernetes.io/component=trading), non un "app=..."
# inventato: cosi' il pod che si misura e' esattamente quello a cui il port-forward instrada.
#
# --- IL NOME DEL POD NON BASTA (2026-08-05) ---------------------------------------------------
# Il confronto sul solo NOME copriva il caso "pod sostituito" ma era cieco a quello piu' frequente:
# il RESTART DEL CONTAINER dentro lo stesso pod (OOM-kill, crash, liveness fallita). Il pod
# mantiene nome e identita', il tunnel di kubectl muore lo stesso, e questo script rispondeva
# "gia' attivo". Successo davvero: pod procionemgr-trading-9b875dd78-n6v4c con RESTARTS 2, tunnel
# morto da 8 ore, /trading mostrava ZERO corsie mentre il motore in cluster stava operando -- e la
# porta locale risultava regolarmente in ascolto. Ora l'identita' del tunnel e' la coppia
# NOME + CONTEGGIO RESTART: se il container e' ripartito, il tunnel si rifa'.
function Get-CurrentPodIdentity([string]$namespace, [string]$component) {
    $out = kubectl get pods -n $namespace --context $context `
        -l "app.kubernetes.io/component=$component" `
        --field-selector status.phase=Running `
        -o jsonpath='{.items[0].metadata.name}|{.items[0].status.containerStatuses[0].restartCount}' 2>$null
    if ([string]::IsNullOrWhiteSpace($out)) { return $null }
    $parts = "$out".Trim().Split('|')
    if ($parts.Count -lt 1 -or [string]::IsNullOrWhiteSpace($parts[0])) { return $null }
    # Il conteggio puo' mancare (container non ancora avviato): si tratta come 0 invece che come
    # ignoto -- al giro dopo, quando esiste, un valore diverso fara' semplicemente rifare il tunnel.
    $restarts = if ($parts.Count -ge 2 -and $parts[1]) { $parts[1] } else { '0' }
    return "$($parts[0])#$restarts"
}

function Stop-StalePortForward([int]$p) {
    foreach ($c in @(Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue)) {
        try { Stop-Process -Id $c.OwningProcess -Force -ErrorAction Stop } catch { }
    }
    # Il socket impiega un istante a liberarsi: senza questa attesa il port-forward nuovo puo'
    # trovare la porta ancora occupata e morire subito.
    for ($i = 0; $i -lt 10; $i++) {
        if (-not (Test-PortListening $p)) { return }
        Start-Sleep -Milliseconds 300
    }
}

function Ensure-Tunnel([hashtable]$t) {
    $etichetta = $t.Etichetta
    $porte = $t.Porte
    $elencoPorte = ($porte -join '+')
    $svcNome = $t.Service -replace '^svc/', ''

    $svc = kubectl get svc $svcNome -n $t.Namespace --context $context 2>$null
    if (-not $svc) {
        Write-Host "${etichetta}: cluster kind non raggiungibile - $($t.Serve) restera' in errore finche' non torna." -ForegroundColor Yellow
        Write-Host "           Se e' appena stato riavviato Docker, vedi il proxy kind-apiproxy in docs/." -ForegroundColor Yellow
        return
    }

    $currentPod = Get-CurrentPodIdentity $t.Namespace $t.Component
    $tutteInAscolto = @($porte | Where-Object { Test-PortListening $_ }).Count -eq $porte.Count
    $qualcunaInAscolto = @($porte | Where-Object { Test-PortListening $_ }).Count -gt 0

    if ($tutteInAscolto) {
        $servedPod = if (Test-Path $t.Marker) { (Get-Content $t.Marker -Raw).Trim() } else { '' }

        if ($currentPod -and $servedPod -eq $currentPod) {
            Write-Host "${etichetta}: port-forward $elencoPorte gia' attivo verso $($currentPod.Split('#')[0]) (restart $($currentPod.Split('#')[1]))." -ForegroundColor Green
            return
        }

        # Il caso che prima passava inosservato -- ora comprende anche il solo restart del container,
        # che lascia il nome del pod invariato ma uccide il tunnel.
        $detail = if ($servedPod) { "serviva $servedPod, ora c'e' $currentPod" } else { "pod servito sconosciuto" }
        Write-Host "${etichetta}: port-forward $elencoPorte STANTIO ($detail) - lo ricreo." -ForegroundColor Yellow
        foreach ($p in $porte) { Stop-StalePortForward $p }
    }
    elseif ($qualcunaInAscolto) {
        # Tunnel a meta': una porta sola in ascolto. Succede col tunnel aperto da una versione
        # precedente di questo script, o con un kubectl morente. Si rifa' intero.
        Write-Host "${etichetta}: tunnel incompleto (manca una delle porte $elencoPorte) - lo ricreo." -ForegroundColor Yellow
        foreach ($p in $porte) { Stop-StalePortForward $p }
    }

    $argomenti = @('port-forward', '-n', $t.Namespace, $t.Service) + $t.PortMap + @('--context', $context)
    Start-Process -WindowStyle Hidden kubectl -ArgumentList $argomenti

    # Il tunnel impiega un attimo ad aprirsi: si aspetta e si VERIFICA, invece di dare per scontato
    # che l'avvio del processo equivalga alla porta in ascolto. Fino a 10 s: con l'apiserver appena
    # ripartito i primi 5 non bastavano (visto il 2026-08-11: messaggio d'allarme, tunnel poi sano).
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 500
        if (@($porte | Where-Object { Test-PortListening $_ }).Count -eq $porte.Count) {
            # Si annota QUALE pod sta servendo: e' il dato che al prossimo giro distingue "gia' attivo"
            # da "attivo verso un pod che non c'e' piu'".
            if ($currentPod) { Set-Content -Path $t.Marker -Value $currentPod -Encoding utf8 }
            Write-Host "${etichetta}: port-forward $elencoPorte avviato verso $currentPod ($($t.Serve))." -ForegroundColor Green
            return
        }
    }

    Write-Host "${etichetta}: port-forward avviato ma le porte $elencoPorte non risultano in ascolto - controlla il cluster." -ForegroundColor Yellow
}

if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
    Write-Host "Tunnel   : kubectl non trovato - /trading non potra' comandare il motore e «Sync now» fallira'." -ForegroundColor Yellow
    exit 0
}

# Un tunnel che non si apre NON blocca gli altri ne' l'avvio dell'app: il resto della piattaforma
# funziona comunque (stessa politica best-effort di sempre).
foreach ($nome in $Targets) { Ensure-Tunnel $tunnels[$nome] }
exit 0
