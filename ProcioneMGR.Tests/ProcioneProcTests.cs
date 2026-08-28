using Procione;

namespace ProcioneMGR.Tests;

/// <summary>
/// Prove di <c>Proc.CaptureAsync</c>, il modo in cui la plancia esegue TUTTO cio' che e' esterno
/// (docker, kubectl, powershell) — supervisore compreso.
///
/// Il caso che pinna l'incidente del 2026-08-28: bringup.ps1 avvia il guscio con
/// <c>Start-Process -RedirectStandardOutput</c>, che forza <c>UseShellExecute=false</c> e fa
/// EREDITARE al guscio gli handle standard non rediretti del proprio genitore. Quando bringup gira
/// come lavoro del supervisore quello stderr E' il pipe del supervisore: bringup esce, il guscio
/// (che vive giorni) tiene aperto l'handle, l'EOF non arriva mai — e l'attesa dell'EOF dopo
/// <c>WaitForExitAsync</c> ha appeso il supervisore INTERO oltre il suo timeout, che copriva solo
/// l'attesa del processo. Veglia, backup e deploy fermi; la plancia diceva «morto senza chiudere».
/// </summary>
public class ProcioneProcTests
{
    [Fact]
    public async Task CaptureAsync_CatturaOutputECodice()
    {
        if (!OperatingSystem.IsWindows()) return; // powershell 5.1: e' la piattaforma della plancia

        var r = await Proc.CaptureAsync("powershell",
            ["-NoProfile", "-Command", "Write-Output 'riga uno'; [Console]::Error.WriteLine('riga err'); exit 3"],
            timeoutMs: 60000);

        Assert.Equal(3, r.Code);
        Assert.Contains("riga uno", r.Out);
        Assert.Contains("riga err", r.Err);
    }

    [Fact]
    public async Task CaptureAsync_NonAspettaLEofDeiNipotiDetached()
    {
        if (!OperatingSystem.IsWindows()) return; // l'eredita' degli handle e' il comportamento Windows in prova

        // Il figlio avvia un nipote DETACHED con -RedirectStandardOutput (=> UseShellExecute=false:
        // il nipote eredita lo stderr del figlio, cioe' il NOSTRO pipe) ed esce subito. Il nipote
        // vive 25 secondi: aspettando l'EOF si tornerebbe dopo 25s — nel caso vero il nipote era il
        // guscio e viveva GIORNI. L'esito e' l'exit code del figlio, e deve arrivare subito.
        var outFile = Path.Combine(Path.GetTempPath(), $"prova-eredita-{Guid.NewGuid():N}.out");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await Proc.CaptureAsync("powershell",
                ["-NoProfile", "-Command",
                 $"Start-Process -WindowStyle Hidden powershell -RedirectStandardOutput '{outFile}' " +
                 "-ArgumentList '-NoProfile','-Command','Start-Sleep 25'; Write-Output 'figlio uscito'; exit 0"],
                timeoutMs: 60000);
            sw.Stop();

            Assert.Equal(0, r.Code);
            Assert.Contains("figlio uscito", r.Out);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(12),
                $"CaptureAsync ha impiegato {sw.Elapsed.TotalSeconds:F1}s: sta aspettando l'EOF del nipote detached");
        }
        finally
        {
            try { File.Delete(outFile); } catch { /* il nipote potrebbe averlo ancora aperto: muore da solo */ }
        }
    }

    [Fact]
    public async Task CaptureAsync_TimeoutRestituisceLOutputParziale()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Prima del 2026-08-28 il timeout buttava via tutto quello che il processo aveva detto:
        // «cosa stava dicendo quando l'ho ucciso» e' spesso l'unica diagnosi disponibile.
        var r = await Proc.CaptureAsync("powershell",
            ["-NoProfile", "-Command", "Write-Output 'passo uno fatto'; Start-Sleep 30"],
            timeoutMs: 6000);

        Assert.Equal(Proc.TimedOut, r.Code);
        Assert.Contains("passo uno fatto", r.Out);
        Assert.Contains("nessuna risposta entro 6s", r.Err);
    }
}
