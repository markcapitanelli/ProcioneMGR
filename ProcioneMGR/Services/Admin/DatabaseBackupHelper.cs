using System.Diagnostics;

namespace ProcioneMGR.Services.Admin;

/// <summary>Parametri di connessione a un database PostgreSQL, estratti dalla connection string.</summary>
public sealed record PgConnectionInfo(string Host, int Port, string Database, string Username, string? Password);

/// <summary>Esito di una verifica del backup (archivio leggibile ⇒ integro). <see cref="Ok"/> true se
/// <c>pg_restore --list</c> ha letto correttamente l'archivio.</summary>
public sealed record IntegrityResult(bool Ok, string Message);

/// <summary>Esito di un backup completo.</summary>
public sealed record BackupResult(bool Success, string BackupPath, long SizeBytes, IntegrityResult Integrity, string? Error = null);

/// <summary>Metadati di un file di backup già presente.</summary>
public sealed record BackupInfo(string FileName, string FullPath, DateTime CreatedUtc, long SizeBytes);

/// <summary>
/// Helper (puro, senza stato) per il backup/ripristino di un database PostgreSQL tramite gli
/// strumenti nativi <c>pg_dump</c>/<c>pg_restore</c>, che devono essere nel PATH.
///
/// Il formato è il <b>custom archive</b> (<c>-Fc</c>): compresso, ripristinabile selettivamente e
/// verificabile con <c>pg_restore --list</c> senza toccare il database. La password non viene mai
/// passata sulla command line: è iniettata nell'ambiente del processo figlio via <c>PGPASSWORD</c>.
///
/// A differenza del vecchio backup SQLite (copia di file + WAL checkpoint), qui il dump è già uno
/// snapshot transazionalmente consistente prodotto dal server: nessun bisogno di fermare l'app.
/// </summary>
public static class DatabaseBackupHelper
{
    /// <summary>Nome del file di backup per un dato timestamp. Prefisso = nome del database.</summary>
    private static string BackupFileName(string database, DateTime stampLocal) =>
        $"{database}-{stampLocal:yyyyMMdd-HHmmss}.dump";

    /// <summary>
    /// Backup completo e verificato: (1) <c>pg_dump -Fc</c> in <paramref name="backupDir"/> con
    /// timestamp, (2) <c>pg_restore --list</c> sulla copia per confermarne la leggibilità. Se la
    /// verifica fallisce, il file viene eliminato e il risultato è <c>Success=false</c>.
    /// </summary>
    public static BackupResult Backup(PgConnectionInfo conn, string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        var backupPath = Path.Combine(backupDir, BackupFileName(conn.Database, DateTime.Now));

        var (exit, _, stderr) = RunTool("pg_dump", new[]
        {
            "--format=custom",
            "--host", conn.Host,
            "--port", conn.Port.ToString(),
            "--username", conn.Username,
            "--dbname", conn.Database,
            "--file", backupPath,
        }, conn.Password);

        if (exit != 0)
        {
            TryDelete(backupPath);
            return new BackupResult(false, backupPath, 0,
                new IntegrityResult(false, "dump non prodotto"), $"pg_dump exit {exit}: {Truncate(stderr)}");
        }

        var integrity = IntegrityCheck(backupPath);
        if (!integrity.Ok)
        {
            TryDelete(backupPath);
            return new BackupResult(false, backupPath, 0, integrity, "verifica del backup fallita; file eliminato");
        }

        var size = new FileInfo(backupPath).Length;
        return new BackupResult(true, backupPath, size, integrity);
    }

    /// <summary>Verifica che un archivio di backup sia leggibile via <c>pg_restore --list</c>.</summary>
    public static IntegrityResult IntegrityCheck(string backupPath)
    {
        if (!File.Exists(backupPath))
            return new IntegrityResult(false, $"File non trovato: {backupPath}");

        var (exit, stdout, stderr) = RunTool("pg_restore", new[] { "--list", backupPath }, password: null);
        if (exit != 0)
            return new IntegrityResult(false, $"pg_restore --list exit {exit}: {Truncate(stderr)}");

        // Un archivio valido produce almeno una voce nel TOC.
        var hasEntries = stdout.Split('\n').Any(l => l.TrimStart().Length > 0 && !l.TrimStart().StartsWith(';'));
        return hasEntries
            ? new IntegrityResult(true, "archivio leggibile")
            : new IntegrityResult(false, "archivio vuoto o TOC illeggibile");
    }

    /// <summary>Elenca i backup (*.dump) in <paramref name="backupDir"/>, più recenti prima.</summary>
    public static IReadOnlyList<BackupInfo> ListBackups(string backupDir)
    {
        if (!Directory.Exists(backupDir)) return [];
        return Directory.EnumerateFiles(backupDir, "*.dump")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new BackupInfo(f.Name, f.FullName, f.CreationTimeUtc, f.Length))
            .ToList();
    }

    /// <summary>
    /// Ripristina un backup nel database di destinazione con <c>pg_restore --clean --if-exists</c>
    /// (droppa gli oggetti esistenti prima di ricrearli). Verifica prima la leggibilità dell'archivio.
    /// Operazione distruttiva: sovrascrive lo schema/dati correnti — da usare a trading fermo.
    /// </summary>
    public static void Restore(PgConnectionInfo conn, string backupPath)
    {
        var integrity = IntegrityCheck(backupPath);
        if (!integrity.Ok)
            throw new InvalidOperationException($"Backup non leggibile, ripristino annullato: {integrity.Message}");

        var (exit, _, stderr) = RunTool("pg_restore", new[]
        {
            "--clean",
            "--if-exists",
            "--no-owner",
            "--no-acl",
            "--host", conn.Host,
            "--port", conn.Port.ToString(),
            "--username", conn.Username,
            "--dbname", conn.Database,
            backupPath,
        }, conn.Password);

        if (exit != 0)
            throw new InvalidOperationException($"pg_restore fallito (exit {exit}): {Truncate(stderr)}");
    }

    /// <summary>Esegue uno strumento CLI di PostgreSQL catturandone exit code, stdout e stderr. La
    /// password, se presente, è passata via env <c>PGPASSWORD</c> (mai sulla command line).</summary>
    private static (int Exit, string Stdout, string Stderr) RunTool(string exe, IEnumerable<string> args, string? password)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(password)) psi.Environment["PGPASSWORD"] = password;

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Impossibile avviare '{exe}'.");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Eseguibile assente nel PATH: messaggio esplicito invece di un errore criptico.
            throw new InvalidOperationException(
                $"'{exe}' non trovato nel PATH. Installa i client PostgreSQL (pg_dump/pg_restore) o aggiungili al PATH.", ex);
        }
    }

    private static string Truncate(string s, int max = 500) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s.Trim() : s[..max].Trim() + "…");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
