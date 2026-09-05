using System.Text.Json;

namespace ProcioneMGR.Services.Admin;

/// <summary>
/// [2026-09-05] <b>Lo stato del backup notturno si legge dal supervisore della plancia, non dal
/// Task Scheduler.</b> Dal 2026-08-23 le automazioni girano <i>dentro</i> `procione servizio` (una sola
/// attività pianificata, «ProcioneMGR Plancia»); il task «ProcioneMGR Backup DB» non esiste più, e
/// la pagina che lo cercava dichiarava «NON REGISTRATA» con quattordici dump sani sul disco —
/// suggerendo un <c>-Register</c> che avrebbe creato un <b>secondo</b> backup notturno. Un controllo
/// che allarma a prescindere dalla realtà è la classe di difetto che questa piattaforma bonifica da
/// luglio; questo lettore lo chiude leggendo la stessa fonte che <c>procione lavoro</c> legge.
///
/// <para>Il file è <c>%TEMP%\procionemgr-supervisore.json</c> (vedi <c>tools/Procione/Platform.cs</c>):
/// battito del supervisore e, per ogni lavoro, ultima esecuzione, codice d'uscita, sintesi. La
/// funzione di lettura è pura: si prova su una stringa.</para>
/// </summary>
public static class SupervisorJobProbe
{
    /// <summary>Il nome del lavoro del supervisore che lancia <c>db-backup.ps1</c>.</summary>
    public const string BackupJobName = "backup";

    /// <summary>Oltre questo silenzio del battito il supervisore si considera fermo: i lavori non partiranno.</summary>
    public static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>Il file di stato del supervisore su questa macchina (stesso percorso della plancia).</summary>
    public static string StatePath => Path.Combine(Path.GetTempPath(), "procionemgr-supervisore.json");

    /// <summary>
    /// Legge il file di stato se esiste. <c>null</c> = nessun supervisore su questa macchina (o file
    /// illeggibile): il chiamante ripiega sul Task Scheduler, come prima del 2026-08-23.
    /// </summary>
    public static ScheduledTaskStatus? TryRead(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Parse(File.ReadAllText(path), now);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Traduce lo stato del lavoro <c>backup</c> nella forma che la pagina già sa mostrare.
    /// <c>null</c> se il JSON non è quello del supervisore o il lavoro non c'è.
    /// </summary>
    public static ScheduledTaskStatus? Parse(string json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
                return null;

            JsonElement? job = null;
            foreach (var j in jobs.EnumerateArray())
            {
                if (j.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                    && string.Equals(n.GetString(), BackupJobName, StringComparison.OrdinalIgnoreCase))
                {
                    job = j;
                    break;
                }
            }
            if (job is not { } b) return null;

            var heartbeat = ReadDate(root, "Heartbeat");
            var lastRun = ReadDate(b, "LastRun");
            var runningSince = ReadDate(b, "RunningSince");
            var enabled = !b.TryGetProperty("Enabled", out var en) || en.ValueKind != JsonValueKind.False;
            long? lastCode = b.TryGetProperty("LastCode", out var lc) && lc.ValueKind == JsonValueKind.Number ? lc.GetInt64() : null;
            var summary = b.TryGetProperty("LastSummary", out var ls) && ls.ValueKind == JsonValueKind.String ? ls.GetString() : null;

            var supervisorAlive = heartbeat is { } hb && now - hb <= HeartbeatStaleAfter;
            var state =
                !enabled ? "Disabled"
                : !supervisorAlive ? "supervisore FERMO"
                : runningSince is not null ? "in esecuzione"
                : "acceso";

            var message = !supervisorAlive
                ? $"il supervisore della plancia non batte da {Describe(now - (heartbeat ?? DateTimeOffset.MinValue))}: i lavori non partono. Rimedio: `procione servizio`."
                : summary;

            return new ScheduledTaskStatus(
                Queryable: true,
                Exists: true,
                State: state,
                LastRunLocal: lastRun?.LocalDateTime,
                LastResult: lastCode,
                Message: message,
                Source: "supervisore della plancia (lavoro «backup»)");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadDate(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    private static string Describe(TimeSpan age)
        => age.TotalDays >= 1 ? $"{age.TotalDays:F0} giorni"
         : age.TotalHours >= 1 ? $"{age.TotalHours:F0} ore"
         : $"{Math.Max(1, age.TotalMinutes):F0} minuti";
}
