using Procione;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K4/K5c, 2026-08-31] <b>Ogni script di <c>scripts/</c> deve essere PowerShell leggibile.</b>
///
/// <para>Nasce da un errore mio, lo stesso giorno: modificando <c>deploy-trading.ps1</c> ho scritto
/// una sottoespressione con indice dentro una stringa a doppi apici seguita da un apostrofo, e il
/// parser di PowerShell 5.1 ne è uscito con «carattere di terminazione mancante». Il file
/// compilava per il mio occhio e per git; sarebbe esploso alla prima esecuzione — cioè dentro il
/// lavoro schedulato della plancia, alle tre di notte, con l'esito assorbito da un log che nessuno
/// stava guardando.</para>
///
/// <para>Questi script non sono accessori: <c>bringup.ps1</c> è il modo in cui la piattaforma si
/// rialza, <c>db-backup.ps1</c> è l'unico dump, <c>watchdog.ps1</c> è il dead-man switch,
/// <c>deploy-trading.ps1</c> è ciò che promuove il motore. Un errore di sintassi in uno di loro è
/// un'indisponibilità che si scopre nel momento peggiore. Il compilatore C# non li guarda: nessuno
/// li guardava.</para>
///
/// <para>Il riferimento è <b>indipendente</b> dal nostro codice: è il parser di PowerShell stesso.
/// Non esegue nulla — <c>ParseFile</c> legge e basta.</para>
/// </summary>
public class ScriptsSintassiTests
{
    [Fact]
    public void TuttiGliScript_SonoPowerShellValido()
    {
        if (!OperatingSystem.IsWindows()) return; // gli script sono PowerShell 5.1: e' la piattaforma

        var cartella = Path.Combine(Platform.RepoRoot, "scripts");
        Assert.True(Directory.Exists(cartella), $"cartella degli script non trovata: {cartella}");

        var script = Directory.GetFiles(cartella, "*.ps1", SearchOption.TopDirectoryOnly);
        Assert.True(script.Length >= 15, $"solo {script.Length} script trovati: la cartella e' quella giusta?");

        // Un solo processo PowerShell per tutti: avviarne uno per file costerebbe piu' del test.
        var elenco = string.Join("','", script.Select(p => p.Replace("'", "''")));
        var comando =
            "$falliti = @(); " +
            $"foreach ($f in @('{elenco}')) {{ " +
            "  $e = $null; " +
            "  [void][System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$null, [ref]$e); " +
            "  if ($e -and $e.Count -gt 0) { $falliti += \"$([System.IO.Path]::GetFileName($f)) riga $($e[0].Extent.StartLineNumber): $($e[0].Message)\" } " +
            "} " +
            "if ($falliti.Count -gt 0) { $falliti -join \"`n\"; exit 1 } else { exit 0 }";

        var r = Proc.Capture("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", comando],
                             timeoutMs: 120000);

        Assert.True(r.Code == 0,
            $"script con errori di sintassi (il parser di PowerShell li legge, il compilatore C# no):\n{r.Out}\n{r.Err}");
    }
}
