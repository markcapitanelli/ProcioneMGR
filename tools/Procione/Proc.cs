using System.Diagnostics;

namespace Procione;

/// <summary>Esito di un processo esterno.</summary>
internal readonly record struct ExecResult(int Code, string Out, string Err)
{
    public bool Ok => Code == 0;

    /// Output utile: stdout se c'e', altrimenti stderr (kubectl e docker scrivono le diagnosi li').
    public string Text => Out.Length > 0 ? Out : Err;

    public string FirstLine
    {
        get
        {
            var t = Text;
            var i = t.IndexOf('\n');
            return (i < 0 ? t : t[..i]).Trim();
        }
    }
}

/// <summary>
/// Esecuzione di processi esterni (docker, kubectl, powershell, dotnet).
///
/// Ogni sonda ha un TIMEOUT e nessuna eccezione esce da qui: quando il cluster e' irraggiungibile
/// <c>kubectl</c> resta appeso per minuti, e una plancia che si pianta mentre prova a dire "il
/// cluster e' giu'" e' peggio di nessuna plancia. Il fallimento e' un valore di ritorno, non
/// un'eccezione.
/// </summary>
internal static class Proc
{
    // Codici sintetici, distinti dai codici di uscita veri (che sono >= 0).
    public const int NotStarted = -1;
    public const int TimedOut = -2;
    public const int Failed = -3;

    /// <param name="ambiente">
    /// Variabili da aggiungere a quelle ereditate. Serve al supervisore, che passa al watchdog il
    /// token Telegram quando non e' nell'ambiente: un dead-man switch che scopre il guasto e non
    /// riesce a dirlo e' mezzo dead-man switch.
    /// </param>
    public static async Task<ExecResult> CaptureAsync(string file, IEnumerable<string> args, int timeoutMs = 15000,
                                                      IReadOnlyDictionary<string, string>? ambiente = null)
    {
        // CreateNoWindow + UseShellExecute=false: NESSUNA finestra nasce mai da qui. E' la
        // differenza fra il supervisore e le attivita' pianificate che ha sostituito, che aprivano
        // una console PowerShell davanti all'utente 288 volte al giorno.
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (ambiente is not null)
            foreach (var (chiave, valore) in ambiente) psi.Environment[chiave] = valore;

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new ExecResult(NotStarted, "", $"impossibile avviare '{file}'");

            // Lettura ASINCRONA dei due flussi: leggerli in sequenza dopo WaitForExit puo' bloccare
            // per sempre se il figlio riempie il buffer dell'altro flusso.
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new ExecResult(TimedOut, "", $"nessuna risposta entro {timeoutMs / 1000}s");
            }

            return new ExecResult(p.ExitCode, (await so).Trim(), (await se).Trim());
        }
        catch (Exception ex)
        {
            // Tipicamente: eseguibile non nel PATH. E' un'informazione, non un incidente.
            return new ExecResult(Failed, "", ex.Message.Trim());
        }
    }

    public static ExecResult Capture(string file, IEnumerable<string> args, int timeoutMs = 15000)
        => CaptureAsync(file, args, timeoutMs).GetAwaiter().GetResult();

    /// <summary>Esegue ereditando la console: l'output dello script si vede mentre scorre.</summary>
    public static int Inherit(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) { Ui.Error($"impossibile avviare '{file}'."); return NotStarted; }
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Ui.Error($"'{file}' non eseguibile: {ex.Message.Trim()}");
            return Failed;
        }
    }

    // --- PowerShell ----------------------------------------------------------------------------
    // Windows PowerShell 5.1: e' quello con cui TUTTI gli script di scripts/ sono stati scritti e
    // provati (here-string, Get-NetTCPConnection, Register-ScheduledTask). Non si passa a pwsh.
    private const string PowerShellExe = "powershell";

    /// <summary>Lancia uno script di <c>scripts/</c> mostrandone l'output.</summary>
    public static int Script(string nome, params string[] args)
    {
        var path = Platform.Script(nome);
        if (!File.Exists(path))
        {
            Ui.Error($"script non trovato: {path}");
            return Failed;
        }
        var argv = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path };
        argv.AddRange(args);
        return Inherit(PowerShellExe, argv);
    }

    /// <summary>Esegue un frammento PowerShell e ne cattura l'output.</summary>
    public static Task<ExecResult> PsAsync(string comando, int timeoutMs = 20000)
        => CaptureAsync(PowerShellExe, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", comando], timeoutMs);

    public static ExecResult Ps(string comando, int timeoutMs = 20000)
        => PsAsync(comando, timeoutMs).GetAwaiter().GetResult();

    /// <summary>Esegue un frammento PowerShell mostrandone l'output (per i comandi che seguono un log).</summary>
    public static int PsInherit(string comando)
        => Inherit(PowerShellExe, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", comando]);

    /// <summary>
    /// Avvia un processo in una finestra SEPARATA e non lo aspetta. Serve per il guscio, che vive
    /// per giorni: tenerlo figlio della plancia significherebbe ucciderlo alla chiusura di questa.
    /// </summary>
    public static bool Detach(string comandoPowerShell, bool minimizzata = true)
    {
        var psi = new ProcessStartInfo(PowerShellExe)
        {
            // UseShellExecute = true e' l'unico modo, da .NET, di ottenere una finestra NUOVA
            // (CREATE_NEW_CONSOLE non e' esposto). Con true si deve usare Arguments, non
            // ArgumentList: quest'ultima non e' supportata e Process.Start lancerebbe.
            UseShellExecute = true,
            WindowStyle = minimizzata ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{comandoPowerShell.Replace("\"", "\\\"")}\"",
        };
        try { return Process.Start(psi) is not null; }
        catch (Exception ex) { Ui.Error($"avvio in background fallito: {ex.Message.Trim()}"); return false; }
    }

    // --- kubectl -------------------------------------------------------------------------------

    /// <summary>
    /// kubectl sul contesto giusto e con un tetto di attesa SUO: senza <c>--request-timeout</c>
    /// una chiamata verso un API server morto resta appesa oltre il nostro timeout di processo, e
    /// l'unico modo di chiuderla e' ucciderla — piu' brutale e piu' lento.
    /// </summary>
    public static Task<ExecResult> KubectlAsync(IEnumerable<string> args, int timeoutMs = 12000)
    {
        var argv = new List<string>(args) { "--context", Platform.KubeContext, "--request-timeout=8s" };
        return CaptureAsync("kubectl", argv, timeoutMs);
    }

    public static ExecResult Kubectl(IEnumerable<string> args, int timeoutMs = 12000)
        => KubectlAsync(args, timeoutMs).GetAwaiter().GetResult();

    /// <summary>kubectl che mostra il suo output (logs -f, rollout status, ...).</summary>
    public static int KubectlInherit(IEnumerable<string> args)
    {
        var argv = new List<string>(args) { "--context", Platform.KubeContext };
        return Inherit("kubectl", argv);
    }
}
