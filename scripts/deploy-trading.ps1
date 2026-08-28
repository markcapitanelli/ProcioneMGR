# =============================================================================================
#  Sync AUTOMATICO del motore di trading: nuovo master -> build locale -> import -> apply.
#
#  DECISIONE DEL PROPRIETARIO (2026-08-25, sera): «il sync del trading da ora in poi deve
#  diventare automatico». Rovescia la regola scritta in infra/gitops/apps/trading-app.yaml
#  («sync SEMPRE MANUALE») — e va detto qui, non nascosto: quella regola temeva un controller
#  che riavvia il motore DI SUA INIZIATIVA (selfHeal/prune). Questo script non è quello:
#  agisce SOLO quando master è avanzato, cioè dopo una merge — un atto umano — e si limita a
#  eseguire la promozione che quella merge dichiara. ArgoCD resta fuori: non raggiunge il repo
#  privato dal 2026-08-05 («authentication required»), e la piattaforma è volutamente
#  indipendente da GitHub a runtime (build locali, imagePullPolicy: Never).
#
#  COSA FA, in ordine, fermandosi al primo errore:
#    1. git fetch: c'è un commit nuovo su origin/master rispetto al pin del kustomization?
#       (-IfNewCommit: se no, esce dicendo «già allineato» — è la modalità del lavoro
#       schedulato nella plancia, che gira spesso e deve costare nulla quando non c'è nulla);
#    2. git pull --ff-only (un albero sporco o divergente ferma tutto, a voce alta);
#    3. build + import nel nodo kind via build-images-local.ps1 (con la sua verifica crictl);
#    4. bump del pin newTag nel kustomization + kubectl apply -k + rollout status atteso;
#    5. commit del pin e push su master (salvo -NoPush): git e cluster devono dire la stessa
#       cosa, e il pin è la dichiarazione. Se il push fallisce (auth), il commit resta locale
#       e lo si dice — meglio un push mancato dichiarato che un pin divergente silenzioso.
#
#  COSA NON FA: non tocca il guscio (riavviarlo chiude le sessioni: resta un atto separato),
#  non tocca ConfigMap/Secret (kubectl diff della promozione tipo: cambia SOLO l'immagine),
#  non decide COSA promuovere (lo decide la merge su master).
#
#  USO:
#    ./scripts/deploy-trading.ps1                    # deploy incondizionato dell'HEAD di master
#    ./scripts/deploy-trading.ps1 -IfNewCommit      # solo se master è avanzato (lavoro plancia)
#    ./scripts/deploy-trading.ps1 -NoPush           # non pusha il commit del pin
# =============================================================================================

param(
    [switch]$IfNewCommit,
    [switch]$NoPush
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$kustomization = Join-Path $repoRoot 'infra/k8s/trading/kustomization.yaml'

Push-Location $repoRoot
try {
    # --- 1. C'è qualcosa di nuovo? -----------------------------------------------------------
    git fetch --quiet origin master
    if ($LASTEXITCODE -ne 0) { throw "git fetch fallito (exit $LASTEXITCODE): senza il remoto non si sa cosa promuovere." }
    $remoteSha = (git rev-parse --short=8 origin/master).Trim()

    $pinLine = Select-String -Path $kustomization -Pattern 'newTag:\s*local-([0-9a-f]+)'
    $pinnedSha = if ($pinLine) { $pinLine.Matches[0].Groups[1].Value } else { '' }

    if ($IfNewCommit -and $pinnedSha) {
        # «Nuovo commit» NON significa «HEAD diverso dal pin»: il commit del PIN stesso fa
        # avanzare master, e confrontare gli sha creerebbe un loop — deploy, pin, master avanza,
        # nuovo deploy, ogni 30 minuti per sempre (trovato al primo giro vero, 2026-08-25). La
        # domanda giusta e': da quando abbiamo promosso, e' cambiato QUALCOSA OLTRE al pin?
        git diff --quiet $pinnedSha origin/master -- . ':(exclude)infra/k8s/trading/kustomization.yaml'
        if ($LASTEXITCODE -eq 0) {
            # Un giro PRECEDENTE puo' essere morto fra il bump del pin e il commit (successo il
            # 2026-08-26: apply fallito sotto la pressione della build). Il pin sporco va raccolto
            # QUI, o resta orfano: il prossimo pull --ff-only che tocca il kustomization
            # fallirebbe, e il sync si fermerebbe per un residuo che nessuno vede.
            git diff --quiet -- $kustomization
            if ($LASTEXITCODE -ne 0) {
                git add $kustomization
                git commit --quiet -m "deploy(trading): pin local-$pinnedSha [raccolto da un giro interrotto]"
                if (-not $NoPush) {
                    $env:GIT_TERMINAL_PROMPT = '0'
                    git -c credential.helper= -c 'credential.helper=!gh auth git-credential' push --quiet origin master
                    if ($LASTEXITCODE -ne 0) { Write-Warning "Push del pin raccolto FALLITO: il commit resta locale." }
                }
            }
            Write-Host "Gia' allineato: da local-$pinnedSha a origin/master cambia solo il pin. Nessun deploy."
            exit 0
        }
        if ($LASTEXITCODE -ne 1) { throw "git diff fallito (exit $LASTEXITCODE): il pin local-$pinnedSha non e' un commit noto? Serve un occhio umano." }
    }

    # --- 2. Allineamento del repo (ff-only: una divergenza è un problema, non un dettaglio) ---
    git pull --ff-only --quiet origin master
    if ($LASTEXITCODE -ne 0) { throw "git pull --ff-only fallito: albero locale divergente o sporco. Si risolve a mano, non da un automatismo." }
    $sha = (git rev-parse --short=8 HEAD).Trim()

    # --- 3. Build + import (con verifica crictl dentro lo script chiamato) --------------------
    & (Join-Path $PSScriptRoot 'build-images-local.ps1') -Targets procionemgr-trading
    if ($LASTEXITCODE -ne 0) { throw "build/import dell'immagine fallita (exit $LASTEXITCODE)." }

    # --- 4. Promozione: pin + apply + rollout atteso ------------------------------------------
    (Get-Content $kustomization -Raw) -replace 'newTag:\s*local-[0-9a-f]+', "newTag: local-$sha" |
        Set-Content $kustomization -NoNewline

    kubectl apply -k (Join-Path $repoRoot 'infra/k8s/trading')
    if ($LASTEXITCODE -ne 0) { throw "kubectl apply fallito (exit $LASTEXITCODE): il pin locale resta, il cluster no. Rilanciare." }

    kubectl rollout status deployment/procionemgr-trading -n procionemgr-trading --timeout=240s
    if ($LASTEXITCODE -ne 0) { throw "rollout NON completato: il pod nuovo non e' diventato Ready. Guardare i log del pod, il vecchio ReplicaSet regge." }

    # --- 5. Il pin in git: la promozione dichiarata dove tutti la leggono ---------------------
    git add $kustomization
    git -c core.safecrlf=false diff --cached --quiet
    if ($LASTEXITCODE -ne 0) {
        git commit --quiet -m "deploy(trading): pin local-$sha [sync automatico della plancia]"
        if ($LASTEXITCODE -ne 0) { throw "commit del pin fallito." }
        if (-not $NoPush) {
            $env:GIT_TERMINAL_PROMPT = '0'
            git -c credential.helper= -c 'credential.helper=!gh auth git-credential' push --quiet origin master
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Push del pin FALLITO (auth?): il commit resta locale. git e GitHub divergono finche' non si pusha."
            }
        }
    }

    Write-Host "Deploy completato: motore su local-$sha (era local-$pinnedSha)."
}
finally { Pop-Location }
