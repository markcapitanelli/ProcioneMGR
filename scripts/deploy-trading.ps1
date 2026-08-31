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
#    1-bis. [K4] la CI su quel commit è VERDE? Se è in corso o non consultabile si salta il giro
#       (si riprova fra mezz'ora); se è rossa non si promuove. Il cancello era «master è
#       cambiato», non «master è sano»: un test rotto arrivava nel cluster in 30 minuti;
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
    [switch]$NoPush,
    # [K4] Scavalca il cancello della CI. Esiste per il percorso umano — «so io cosa sto facendo,
    # promuovi» — mai per il lavoro schedulato, che non deve poter scavalcare niente.
    [switch]$SkipCiCheck
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

    # --- 1-bis. La CI su quel commit è verde? [K4, 2026-08-31] --------------------------------
    # Il cancello d'ingresso di questo script era «origin/master è cambiato», non «origin/master è
    # sano». La rete di sicurezza dichiarata in testa al file — «agisce solo dopo una merge, un
    # atto umano» — presuppone che la merge implichi la CI verde, ma non lo verificava: un push su
    # master che rompe i TEST veniva comunque compilato e applicato al cluster entro 30 minuti. La
    # build locale ferma solo ciò che non COMPILA; un test rosso passava indisturbato, e il
    # rollout andava a buon fine su un motore che la CI aveva già bocciato.
    #
    # Si guarda il workflow «CI» (build + test + audit), non tutti: «Docker build» pubblica su un
    # registry che questa piattaforma non usa a runtime, e farlo pesare qui significherebbe legare
    # il deploy locale a un servizio esterno di cui è dichiaratamente indipendente.
    if (-not $SkipCiCheck) {
        $shaPieno = (git rev-parse origin/master).Trim()
        $ci = $null
        try {
            $env:GIT_TERMINAL_PROMPT = '0'
            $ci = gh run list --branch master --commit $shaPieno --workflow CI --limit 5 --json status,conclusion 2>$null | ConvertFrom-Json
        } catch { $ci = $null }

        if ($null -eq $ci -or $ci.Count -eq 0) {
            # Non consultabile NON è verde. Ma non è nemmeno un guasto da gridare: si salta il giro
            # e si riprova fra mezz'ora. Fail-closed sull'AZIONE, senza bloccarsi per sempre —
            # la CI potrebbe non essere ancora partita, o gh non essere autenticato.
            Write-Host "CI non consultabile per $shaPieno (gh assente, non autenticato, o nessun run ancora): salto il giro."
            exit 0
        }

        $inCorso = @($ci | Where-Object { $_.status -ne 'completed' })
        if ($inCorso.Count -gt 0) {
            Write-Host "CI ancora in corso su $shaPieno ($($inCorso.Count) run): salto il giro, si riprova al prossimo."
            exit 0
        }

        $rotti = @($ci | Where-Object { $_.conclusion -ne 'success' })
        if ($rotti.Count -gt 0) {
            # L'esito si estrae PRIMA: una sottoespressione con indice dentro una stringa a doppi
            # apici, seguita da un apostrofo, manda in confusione il parser di PowerShell 5.1.
            $esito = $rotti[0].conclusion
            throw "CI NON verde su origin/master (esito: $esito): non promuovo. E' il cancello che mancava, un test rosso arrivava nel cluster in 30 minuti. Per forzare: -SkipCiCheck."
        }
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
