# =============================================================================================
#  [Fase 0 PRD-RISANAMENTO, 2026-08-09] Riallinea i Secret K8s ai valori CORRENTI di
#  ProcioneMGR/appsettings.json dopo una rotazione (master key, segreto gRPC, password Postgres).
#
#  PERCHE' ESISTE: k8s-trading-secret.ps1 e k8s-ui-secret.ps1 leggono da variabili d'ambiente di
#  sessione (mai da riga di comando: finirebbe nella cronologia della shell) e raccomandano di
#  lanciarli nella STESSA sessione perche' le due copie della master key non divergano. Questo
#  wrapper fa esattamente quello: legge i tre valori dal file di configurazione del monolite —
#  che DOPO una rotazione e' la fonte di verita' — li mette nelle env di QUESTA sessione e
#  invoca i tre script ufficiali. NESSUN valore viene mai stampato.
#
#  PREREQUISITI: cluster kind raggiungibile (scripts\bringup.ps1 se il PC e' stato riavviato).
#  DOPO: riavviare i deployment perche' i pod rileggano i Secret:
#    kubectl -n procionemgr-trading rollout restart deploy
#    kubectl -n procionemgr-ui      rollout restart deploy   (se il guscio gira come pod)
# =============================================================================================
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$appsettings = Join-Path $repoRoot 'ProcioneMGR\appsettings.json'
$json = Get-Content $appsettings -Raw | ConvertFrom-Json

$masterKey = $json.Security.MasterKey
$grpc      = $json.Trading.GrpcSharedSecret
$connStr   = $json.ConnectionStrings.PostgresConnection

if (-not $masterKey -or $masterKey -like '__*__') { throw 'MasterKey assente o segnaposto: completa la rotazione prima.' }
if ($connStr -like '*__NUOVA_PASSWORD_PG__*')     { throw 'La connection string contiene ancora il segnaposto della password.' }

# DENTRO un pod 'localhost' e' il pod stesso: verso il Postgres dell'host si passa da
# host.docker.internal (documentato in k8s-postgres-secret.ps1, e imparato dal vivo il
# 2026-08-09: pod in crash-loop su 'localhost:5432' dopo il primo giro di questo wrapper).
# Il file locale NON si tocca: la riscrittura vale solo per la copia diretta ai Secret.
$connStrForPods = $connStr -replace 'Host=localhost', 'Host=host.docker.internal'

# Env di SESSIONE (non persistite): i tre script ufficiali le leggono da qui.
$env:PROCIONE_MGR_MASTER_KEY                 = $masterKey
$env:PROCIONE_MGR_TRADING_GRPC_SECRET        = $grpc
$env:ConnectionStrings__PostgresConnection   = $connStrForPods

Write-Host 'Secret Postgres...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'k8s-postgres-secret.ps1')
Write-Host 'Secret trading (master key + gRPC + conn)...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'k8s-trading-secret.ps1')
Write-Host 'Secret ui (master key + conn)...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'k8s-ui-secret.ps1')

# Igiene: le env di sessione non servono piu'.
Remove-Item Env:PROCIONE_MGR_MASTER_KEY, Env:PROCIONE_MGR_TRADING_GRPC_SECRET, Env:ConnectionStrings__PostgresConnection -ErrorAction SilentlyContinue
Write-Host 'Fatto. Ora: kubectl -n procionemgr-trading rollout restart deploy' -ForegroundColor Green
