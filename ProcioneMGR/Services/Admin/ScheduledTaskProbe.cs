using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ProcioneMGR.Services.Admin;

/// <summary>
/// Stato di un'operazione pianificata di Windows <b>come la vede il sistema</b>, non come qualcuno
/// crede che sia.
///
/// <para><see cref="Queryable"/> distingue «la domanda non si può nemmeno porre» (non siamo su
/// Windows, PowerShell manca, la chiamata è andata in timeout) da «il task non c'è»: sono due fatti
/// diversi e confonderli produrrebbe un allarme falso ogni volta che l'app gira in container.</para>
/// </summary>
/// <param name="Queryable">La piattaforma ha permesso di interrogare il Task Scheduler.</param>
/// <param name="Exists">Il task esiste. Significativo solo se <paramref name="Queryable"/>.</param>
/// <param name="State">Ready / Running / Disabled, come lo chiama Windows.</param>
/// <param name="Arguments">Gli argomenti dell'azione, testuali: è lì che si vede dove scrive davvero.</param>
/// <param name="ScriptPath">Il <c>-File</c> estratto dagli argomenti.</param>
/// <param name="Destination">Il <c>-Destination</c> estratto dagli argomenti, se congelato nel task.</param>
/// <param name="LastRunLocal">Ultima esecuzione (ora locale).</param>
/// <param name="LastResult">Codice di uscita dell'ultima esecuzione: 0 = riuscita.</param>
/// <param name="NextRunLocal">Prossima esecuzione prevista (ora locale).</param>
/// <param name="Message">Perché non è interrogabile, o l'errore incontrato.</param>
public sealed record ScheduledTaskStatus(
    bool Queryable,
    bool Exists,
    string? State = null,
    string? Arguments = null,
    string? ScriptPath = null,
    string? Destination = null,
    DateTime? LastRunLocal = null,
    long? LastResult = null,
    DateTime? NextRunLocal = null,
    string? Message = null,
    /// <summary>
    /// [2026-09-05] Chi ha risposto: <c>null</c> = il Task Scheduler di Windows (la fonte storica),
    /// altrimenti il supervisore della plancia (<see cref="SupervisorJobProbe"/>). La pagina lo
    /// dice, perché «operazione pianificata» e «lavoro del supervisore» hanno rimedi diversi.
    /// </summary>
    string? Source = null);

/// <summary>
/// Legge lo stato di un'operazione pianificata di Windows.
///
/// <para><b>Perché serve.</b> Il backup notturno non è un worker di questa applicazione: è uno
/// script esterno lanciato dal Task Scheduler. Senza interrogarlo, <c>/admin/backup</c> potrebbe
/// solo guardare i file — e un task <b>cancellato</b> o <b>fallito</b> resterebbe invisibile finché
/// i dump non invecchiano abbastanza da far scattare la soglia. Peggio: se il task porta un
/// <c>-Destination</c> diverso da quello configurato, la pagina guarderebbe una cartella e il
/// backup ne riempirebbe un'altra, gridando «fermo» su un backup sano. È esattamente il difetto che
/// questa pagina esiste per non avere più.</para>
///
/// <para><b>Perché via PowerShell e non <c>schtasks</c>.</b> L'output di <c>schtasks /Query /V</c> è
/// <b>localizzato</b>: su un Windows italiano le intestazioni sono in italiano, e un parser che le
/// cerca in inglese fallisce in silenzio restituendo «task assente». <c>Get-ScheduledTask</c> +
/// <c>ConvertTo-Json</c> danno nomi di proprietà stabili in qualunque lingua.</para>
///
/// <para><b>Fail-open, sempre.</b> Questa è diagnostica: qualunque errore diventa
/// <c>Queryable=false</c> con il motivo scritto. Nessun percorso lancia — una pagina di backup non
/// deve poter esplodere perché il Task Scheduler ha avuto un capriccio.</para>
/// </summary>
public static class ScheduledTaskProbe
{
    /// <summary>Attesa massima: PowerShell impiega ~1s a partire, oltre questo è bloccato.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    // Il nome del task passa per ENVIRONMENT, non dentro il comando: è un valore che arriva dalla
    // configurazione, e concatenarlo in uno script PowerShell sarebbe un'iniezione di comando con
    // un apice ben piazzato.
    private const string TaskNameVariable = "PROCIONE_TASK_NAME";

    private const string Script = """
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $ErrorActionPreference = 'SilentlyContinue'
        $t = Get-ScheduledTask -TaskName $env:PROCIONE_TASK_NAME
        if (-not $t) { Write-Output '{"Exists":false}'; exit 0 }
        $i = Get-ScheduledTaskInfo -TaskName $t.TaskName -TaskPath $t.TaskPath
        $last = $null
        $next = $null
        $code = $null
        if ($i) {
            if ($i.LastRunTime -and $i.LastRunTime.Year -gt 1900) { $last = $i.LastRunTime.ToString('o') }
            if ($i.NextRunTime -and $i.NextRunTime.Year -gt 1900) { $next = $i.NextRunTime.ToString('o') }
            $code = [int64]$i.LastTaskResult
        }
        $argsText = ''
        foreach ($a in $t.Actions) { if ($a.Arguments) { $argsText = [string]$a.Arguments; break } }
        $o = New-Object psobject -Property @{
            Exists         = $true
            State          = [string]$t.State
            Arguments      = $argsText
            LastRunTime    = $last
            NextRunTime    = $next
            LastTaskResult = $code
        }
        $o | ConvertTo-Json -Compress
        """;

    /// <summary>
    /// Interroga il Task Scheduler. Non lancia mai: gli errori tornano dentro il risultato.
    /// </summary>
    public static ScheduledTaskStatus Query(string taskName, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(taskName))
            return new ScheduledTaskStatus(false, false, Message: "Nessun nome di operazione pianificata configurato.");

        if (!OperatingSystem.IsWindows())
        {
            return new ScheduledTaskStatus(false, false, Message:
                "Le operazioni pianificate sono una funzione di Windows: da qui il backup notturno non è "
                + "interrogabile. Restano visibili i file che ha prodotto, se la cartella è raggiungibile.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(Script);
        psi.Environment[TaskNameVariable] = taskName;

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return new ScheduledTaskStatus(false, false, Message: "Impossibile avviare powershell.exe.");

            // Lettura ASINCRONA prima dell'attesa: leggere stdout e stderr in sequenza su un
            // processo che riempie l'altra pipe è il classico stallo a metà pagina.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return new ScheduledTaskStatus(false, false, Message:
                    $"Interrogazione del Task Scheduler oltre {(timeout ?? DefaultTimeout).TotalSeconds:0} secondi: interrotta.");
            }

            var text = stdout.GetAwaiter().GetResult().Trim();
            if (text.Length == 0)
            {
                var err = stderr.GetAwaiter().GetResult().Trim();
                return new ScheduledTaskStatus(false, false, Message:
                    err.Length > 0 ? Truncate(err) : "PowerShell non ha restituito nulla.");
            }

            return Parse(text);
        }
        catch (Exception ex)
        {
            // Compreso il Win32Exception di powershell.exe assente: è diagnostica, non si propaga.
            return new ScheduledTaskStatus(false, false, Message: Truncate(ex.Message));
        }
    }

    /// <summary>Traduce il JSON dello script nello stato tipizzato. Esposto per i test.</summary>
    internal static ScheduledTaskStatus Parse(string json)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<RawTask>(json);
            if (raw is null)
                return new ScheduledTaskStatus(false, false, Message: "Risposta del Task Scheduler illeggibile.");

            if (!raw.Exists) return new ScheduledTaskStatus(true, false);

            return new ScheduledTaskStatus(
                Queryable: true,
                Exists: true,
                State: raw.State,
                Arguments: raw.Arguments,
                ScriptPath: ExtractSwitch(raw.Arguments, "File"),
                Destination: ExtractSwitch(raw.Arguments, "Destination"),
                LastRunLocal: ParseDate(raw.LastRunTime),
                LastResult: raw.LastTaskResult,
                NextRunLocal: ParseDate(raw.NextRunTime));
        }
        catch (JsonException ex)
        {
            return new ScheduledTaskStatus(false, false, Message: $"Risposta del Task Scheduler illeggibile: {Truncate(ex.Message)}");
        }
    }

    /// <summary>
    /// Estrae il valore di un interruttore dalla riga di comando del task (<c>-Destination "C:\..."</c>
    /// oppure senza virgolette). Serve a confrontare ciò che il task <b>fa</b> con ciò che la
    /// configurazione <b>dice</b>: è l'unico modo per accorgersi che divergono.
    /// </summary>
    internal static string? ExtractSwitch(string? arguments, string name)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;

        var m = Regex.Match(
            arguments,
            $@"(?:^|\s)[-/]{Regex.Escape(name)}\s+(?:""(?<q>[^""]*)""|(?<u>[^\s""]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        if (!m.Success) return null;
        var value = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static DateTime? ParseDate(string? iso) =>
        DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d.ToLocalTime()
            : null;

    private static string Truncate(string s, int max = 300) =>
        s.Length <= max ? s : s[..max] + "…";

    private sealed record RawTask(
        [property: JsonPropertyName("Exists")] bool Exists,
        [property: JsonPropertyName("State")] string? State = null,
        [property: JsonPropertyName("Arguments")] string? Arguments = null,
        [property: JsonPropertyName("LastRunTime")] string? LastRunTime = null,
        [property: JsonPropertyName("NextRunTime")] string? NextRunTime = null,
        [property: JsonPropertyName("LastTaskResult")] long? LastTaskResult = null);
}
