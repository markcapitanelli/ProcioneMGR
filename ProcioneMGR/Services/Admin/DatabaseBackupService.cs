using Microsoft.Extensions.Options;
using Npgsql;

namespace ProcioneMGR.Services.Admin;

/// <summary>Verdetto sul backup notturno. Ogni valore è un fatto diverso, non una gradazione.</summary>
public enum NightlyVerdict
{
    /// <summary>Un dump esiste ed è più recente della soglia di stantiezza.</summary>
    Healthy,

    /// <summary>Ci sono dump, ma il più recente è oltre la soglia: il notturno non sta girando.</summary>
    Stale,

    /// <summary>La cartella esiste ma è vuota: nessun backup notturno è mai arrivato a destinazione.</summary>
    NeverRun,

    /// <summary>La cartella configurata non esiste (o non è raggiungibile da questo processo).</summary>
    DirectoryMissing,

    /// <summary>La destinazione non è nemmeno determinabile: va scritta a mano.</summary>
    NotDeterminable,
}

/// <summary>
/// Fotografia del backup <b>notturno</b>: dove scrive, cosa c'è, quando è stato l'ultimo, e cosa
/// dice di sé l'operazione pianificata che lo esegue.
/// </summary>
/// <param name="Warnings">
/// Le divergenze fra ciò che la configurazione dice e ciò che il sistema fa. Sono la ragione per cui
/// questa fotografia esiste: un elenco di file, da solo, non sa distinguere «backup fermo» da
/// «backup che scrive in un'altra cartella».
/// </param>
public sealed record NightlyBackupStatus(
    string Directory,
    bool DirectoryExists,
    bool SameAsManual,
    int FileCount,
    long TotalBytes,
    BackupInfo? Latest,
    double? AgeHours,
    int StaleAfterHours,
    NightlyVerdict Verdict,
    ScheduledTaskStatus Task,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Wrapper iniettabile attorno a <see cref="DatabaseBackupHelper"/> per l'uso dalla UI (pagina
/// <c>/admin/backup</c>). Risolve i parametri di connessione PostgreSQL dalla connection string
/// <c>PostgresConnection</c> e le <b>due</b> cartelle di backup che esistono davvero:
///
/// <list type="bullet">
///   <item><b>manuale</b> — <c>backup/</c> sotto la content root, dove scrive il pulsante «Crea
///   backup ora» di questa pagina;</item>
///   <item><b>notturna</b> — la destinazione di <c>scripts/db-backup.ps1</c>, letta da
///   <see cref="BackupOptions"/> (che è anche ciò che lo script legge: una sola fonte di verità).</item>
/// </list>
///
/// <para><b>Perché entrambe (2026-08-23).</b> La pagina ne mostrava una sola — quella manuale, ferma
/// al 2026-07-09 — mentre il backup notturno produceva un dump sano ogni notte nell'altra. Un
/// controllo che dichiara «ultimo backup: un mese e mezzo fa» quando il backup è di stanotte non è
/// una vista parziale: è un controllo che risponde a prescindere dalla realtà, e in un'emergenza
/// porta a decidere sul falso.</para>
///
/// Il backup usa gli strumenti nativi <c>pg_dump</c>/<c>pg_restore</c> (devono essere nel PATH):
/// vedi <see cref="DatabaseBackupHelper"/> e docs/POSTGRES_MIGRATION.md.
/// </summary>
public sealed class DatabaseBackupService
{
    private readonly PgConnectionInfo _conn;
    private readonly string _manualDir;
    private readonly IOptionsMonitor<BackupOptions> _options;

    public DatabaseBackupService(
        IConfiguration configuration,
        IHostEnvironment env,
        IOptionsMonitor<BackupOptions> options)
    {
        var connString = configuration.GetConnectionString("PostgresConnection")
            ?? throw new InvalidOperationException("Connection string 'PostgresConnection' non trovata.");

        var b = new NpgsqlConnectionStringBuilder(connString);
        _conn = new PgConnectionInfo(
            Host: string.IsNullOrWhiteSpace(b.Host) ? "localhost" : b.Host,
            Port: b.Port == 0 ? 5432 : b.Port,
            Database: b.Database ?? throw new InvalidOperationException("PostgresConnection senza 'Database'."),
            Username: b.Username ?? throw new InvalidOperationException("PostgresConnection senza 'Username'."),
            Password: b.Password);

        _manualDir = Path.Combine(env.ContentRootPath, "backup");
        _options = options;
    }

    /// <summary>Nome del database di destinazione (per la UI).</summary>
    public string TargetDatabase => _conn.Database;

    /// <summary>Cartella dei backup creati DA QUESTA PAGINA.</summary>
    public string ManualDirectory => _manualDir;

    /// <summary>Cartella dei backup NOTTURNI, risolta. Vuota se non determinabile.</summary>
    public string NightlyDirectory => ResolveNightlyDirectory(_options.CurrentValue);

    /// <summary>Le opzioni in vigore adesso (il monitor le rilegge dal file entro ~1s).</summary>
    public BackupOptions CurrentOptions => _options.CurrentValue;

    /// <summary>
    /// Dove finiscono i dump notturni. Vuoto in configurazione = il default storico del parametro
    /// <c>-Destination</c> dello script, cioè <c>%USERPROFILE%\ProcioneMGR-Backup</c>.
    ///
    /// <para>Restituisce stringa vuota se non è determinabile (nessun profilo utente: succede in
    /// container). Restituire un percorso relativo plausibile sarebbe peggio — verrebbe risolto
    /// contro la directory di lavoro del processo e la pagina mostrerebbe con sicurezza una cartella
    /// che non c'entra niente.</para>
    /// </summary>
    public static string ResolveNightlyDirectory(BackupOptions options)
    {
        var configured = options.NightlyDirectory?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            try { return Path.GetFullPath(configured); }
            catch (Exception) { return configured; }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME") ?? "";

        return string.IsNullOrWhiteSpace(home) ? "" : Path.Combine(home, "ProcioneMGR-Backup");
    }

    /// <summary>Crea un backup verificato del DB attivo. Vedi <see cref="DatabaseBackupHelper.Backup"/>.</summary>
    public BackupResult CreateBackup() => DatabaseBackupHelper.Backup(_conn, _manualDir);

    /// <summary>
    /// Elenca i backup di ENTRAMBE le cartelle, più recenti prima, ognuno con la provenienza
    /// dichiarata. Se le due cartelle coincidono si elenca una volta sola, marcando le righe
    /// <see cref="BackupSource.Shared"/>: i due nomi di file hanno la stessa forma, quindi
    /// attribuirle sarebbe indovinare.
    /// </summary>
    public IReadOnlyList<BackupInfo> ListBackups()
    {
        var nightlyDir = NightlyDirectory;

        if (SameDirectory(_manualDir, nightlyDir))
            return DatabaseBackupHelper.ListBackups(_manualDir, BackupSource.Shared);

        return
        [
            .. DatabaseBackupHelper.ListBackups(_manualDir, BackupSource.Manual)
                .Concat(DatabaseBackupHelper.ListBackups(nightlyDir, BackupSource.Nightly))
                .OrderByDescending(x => x.CreatedUtc)
        ];
    }

    /// <summary>
    /// Lo stato del backup notturno: file, età, verdetto, e cosa dice di sé l'operazione
    /// pianificata. Sincrona e potenzialmente lenta (avvia PowerShell): chiamala fuori dal thread
    /// di render.
    /// </summary>
    public NightlyBackupStatus ReadNightlyStatus()
    {
        var options = _options.CurrentValue;
        // [2026-09-05] Dal 2026-08-23 il backup notturno è un lavoro del supervisore della plancia,
        // non un'operazione pianificata: si legge PRIMA il suo file di stato, e solo se non c'è si
        // interroga il Task Scheduler (macchine dove la migrazione `procione attivita migra` non è
        // stata fatta). Cercare solo il task dichiarava «NON REGISTRATA» su un backup sano.
        var task = SupervisorJobProbe.TryRead(SupervisorJobProbe.StatePath, DateTimeOffset.Now)
                   ?? ScheduledTaskProbe.Query(options.ScheduledTaskName);
        return BuildStatus(options, task);
    }

    /// <summary>
    /// La parte deterministica di <see cref="ReadNightlyStatus"/>: dato lo stato del task, calcola
    /// verdetto e avvertimenti. Separata perché è quella che decide se la pagina dice il vero, e un
    /// test non deve dipendere dal Task Scheduler della macchina che lo esegue.
    /// </summary>
    internal NightlyBackupStatus BuildStatus(BackupOptions options, ScheduledTaskStatus task)
    {
        var dir = ResolveNightlyDirectory(options);
        var sameAsManual = SameDirectory(_manualDir, dir);
        var staleAfter = Math.Max(1, options.StaleAfterHours);

        var files = DatabaseBackupHelper.ListBackups(
            dir, sameAsManual ? BackupSource.Shared : BackupSource.Nightly);
        var latest = files.Count > 0 ? files[0] : null;
        var ageHours = latest is null ? (double?)null : (DateTime.UtcNow - latest.CreatedUtc).TotalHours;

        var exists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        var verdict =
            string.IsNullOrEmpty(dir) ? NightlyVerdict.NotDeterminable
            : !exists ? NightlyVerdict.DirectoryMissing
            : latest is null ? NightlyVerdict.NeverRun
            : ageHours > staleAfter ? NightlyVerdict.Stale
            : NightlyVerdict.Healthy;

        return new NightlyBackupStatus(
            Directory: dir,
            DirectoryExists: exists,
            SameAsManual: sameAsManual,
            FileCount: files.Count,
            TotalBytes: files.Sum(f => f.SizeBytes),
            Latest: latest,
            AgeHours: ageHours,
            StaleAfterHours: staleAfter,
            Verdict: verdict,
            Task: task,
            Warnings: BuildWarnings(dir, task, options));
    }

    /// <summary>
    /// Le divergenze fra configurazione e realtà. Esposto per i test: sono la parte che decide se
    /// questa pagina dice la verità o si limita a essere rassicurante.
    /// </summary>
    internal static IReadOnlyList<string> BuildWarnings(string resolvedDir, ScheduledTaskStatus task, BackupOptions options)
    {
        var warnings = new List<string>();

        if (!task.Queryable)
        {
            // Non è un allarme: è l'ammissione che di questo pezzo non sappiamo nulla. Tacere
            // lascerebbe credere che il silenzio sia una conferma.
            warnings.Add($"Stato dell'operazione pianificata NON verificato — {task.Message}");
            return warnings;
        }

        if (!task.Exists)
        {
            warnings.Add(
                $"Nessuna operazione pianificata di nome «{options.ScheduledTaskName}» e nessun supervisore della "
                + "plancia su questo host: il backup notturno non è registrato. I file eventualmente presenti sono di "
                + "un'altra epoca. Dal 2026-08-23 il modo giusto è il supervisore (`procione servizio`, o "
                + "`procione attivita migra` una volta sola); `.\\scripts\\db-backup.ps1 -Register` resta per le macchine "
                + "senza plancia — mai i due insieme, sarebbero due backup notturni.");
            return warnings;
        }

        if (string.Equals(task.State, "supervisore FERMO", StringComparison.Ordinal))
        {
            warnings.Add($"Il lavoro «backup» esiste ma {task.Message}");
        }

        if (task.Destination is { Length: > 0 } destination && !SameDirectory(destination, resolvedDir))
        {
            warnings.Add(
                $"L'operazione pianificata scrive in «{destination}», la configurazione dice «{resolvedDir}». "
                + "Questa pagina guarda la SECONDA: finché divergono può dichiarare fermo un backup sano. "
                + "Riallinea con: .\\scripts\\db-backup.ps1 -Register (non congela più la destinazione nel task).");
        }

        if (task.ScriptPath is { Length: > 0 } script
            && script.Contains(@"\.claude\worktrees\", StringComparison.OrdinalIgnoreCase))
        {
            // L'incidente del 2026-08-17: sei notti di backup perse in silenzio perché il task
            // puntava alla copia dello script dentro un worktree, con il suo appsettings.json
            // gitignorato e fermo alla password precedente.
            warnings.Add(
                $"L'operazione pianificata esegue la copia dello script dentro un WORKTREE ({script}). "
                + "È il guasto del 2026-08-17: il worktree ha un appsettings.json proprio e stantio, e il "
                + "dump fallisce in silenzio appena la password cambia. Ri-registra dal repo principale.");
        }

        if (task.LastResult is { } code && code != 0 && code != TaskRunningCode && code != TaskNeverRunCode)
        {
            warnings.Add($"L'ultima esecuzione è uscita con codice {DescribeResult(code)} — il dump non è stato prodotto.");
        }

        if (string.Equals(task.State, "Disabled", StringComparison.OrdinalIgnoreCase))
            warnings.Add("L'operazione pianificata esiste ma è DISABILITATA: non partirà.");

        return warnings;
    }

    /// <summary>0x00041301: il task è in esecuzione adesso.</summary>
    public const long TaskRunningCode = 267009;

    /// <summary>0x00041303: il task non è mai stato eseguito.</summary>
    public const long TaskNeverRunCode = 267011;

    /// <summary>Codice di uscita del Task Scheduler in forma leggibile.</summary>
    public static string DescribeResult(long code) => code switch
    {
        0 => "0 (riuscita)",
        1 => "1 (errore generico: lo script è uscito con 1 — di solito pg_dump ha fallito)",
        TaskRunningCode => "0x41301 (in esecuzione adesso)",
        TaskNeverRunCode => "0x41303 (mai eseguita)",
        267014 => "0x41306 (terminata dall'utente)",
        _ => $"{code} (0x{code:X})",
    };

    /// <summary>Verifica la leggibilità di un file di backup (<c>pg_restore --list</c>).</summary>
    public IntegrityResult VerifyBackup(string backupPath)
    {
        EnsureKnownLocation(backupPath);
        return DatabaseBackupHelper.IntegrityCheck(backupPath);
    }

    /// <summary>Ripristina un backup nel DB attivo (<c>pg_restore --clean --if-exists</c>).</summary>
    public void Restore(string backupPath)
    {
        EnsureKnownLocation(backupPath);
        DatabaseBackupHelper.Restore(_conn, backupPath);
    }

    /// <summary>
    /// Il file dev'essere in una delle due cartelle note. La pagina passa solo percorsi che ha
    /// elencato lei, quindi oggi non può violarlo — ma <see cref="Restore"/> sovrascrive l'intero
    /// database, e un metodo pubblico che accetta qualunque percorso è un'arma che aspetta il
    /// chiamante distratto.
    /// </summary>
    private void EnsureKnownLocation(string backupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);

        var parent = Path.GetDirectoryName(Path.GetFullPath(backupPath));
        if (SameDirectory(parent, _manualDir) || SameDirectory(parent, NightlyDirectory)) return;

        throw new InvalidOperationException(
            $"«{backupPath}» non è in una cartella di backup nota ({_manualDir} o {NightlyDirectory}): operazione rifiutata.");
    }

    /// <summary>
    /// Due percorsi indicano la stessa cartella. Confronto sui percorsi normalizzati e senza
    /// separatore finale; su Windows senza distinzione di maiuscole, altrove con.
    /// </summary>
    internal static bool SameDirectory(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        static string Normalize(string p)
        {
            try { p = Path.GetFullPath(p); } catch (Exception) { /* percorso indicibile: si confronta com'è */ }
            return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return string.Equals(Normalize(a), Normalize(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
