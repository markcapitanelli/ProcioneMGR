<#
.SYNOPSIS
    Helper condivisi dagli script kind (dot-source: . "$PSScriptRoot\k8s-common.ps1").
#>

# Nome del cluster kind di sviluppo, unico punto di verità per bootstrap/teardown/secret.
$script:KindClusterName = "procionemgr-dev"

<#
.SYNOPSIS
    True se il cluster kind $name esiste.
.DESCRIPTION
    Usa cmd /c per evitare che lo stderr informativo di kind ("No kind clusters found.")
    diventi un errore fatale sotto $ErrorActionPreference = "Stop" (Windows PowerShell 5.1
    trasforma le righe stderr redirette in ErrorRecord).
#>
function Test-KindCluster([string]$name) {
    $existing = @(cmd /c "kind get clusters 2>nul")
    return $existing -contains $name
}
