// DbBackup: strumento CLI per il backup del database PostgreSQL di ProcioneMGR.
// Comandi: backup | verify | list | restore
// Condivide la logica con l'app tramite ProcioneMGR.Services.Admin.DatabaseBackupHelper
// (pg_dump/pg_restore: devono essere nel PATH).
using Npgsql;
using ProcioneMGR.Services.Admin;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

// Risoluzioni PIGRE: env richieste solo dai comandi che le usano davvero. Così `help` funziona
// sempre a zero configurazione, e `list <dir>`/`verify <file>` con argomento esplicito non
// pretendono env inutili (il tool gira anche come K8s Job, dove ogni env va fornita a mano).

// Connection string (serve solo a backup/restore): solo da env, nessun fallback hardcoded
// (containerizzato, un fallback con password finirebbe/verrebbe usato in ambienti non voluti).
PgConnectionInfo GetConn()
{
    var pgConn = Environment.GetEnvironmentVariable("ConnectionStrings__PostgresConnection")
        ?? throw new InvalidOperationException(
            "Variabile d'ambiente ConnectionStrings__PostgresConnection non impostata (obbligatoria per questo comando).");
    var b = new NpgsqlConnectionStringBuilder(pgConn);
    return new PgConnectionInfo(
        Host: string.IsNullOrWhiteSpace(b.Host) ? "localhost" : b.Host,
        Port: b.Port == 0 ? 5432 : b.Port,
        Database: b.Database ?? throw new InvalidOperationException("PostgresConnection senza 'Database'."),
        Username: b.Username ?? throw new InvalidOperationException("PostgresConnection senza 'Username'."),
        Password: b.Password);
}

// Directory di backup: env BACKUP_DIR; su Windows resta il default storico dello sviluppatore,
// su Linux/container la env è obbligatoria (va montato un volume/PVC).
string GetBackupDir() => Environment.GetEnvironmentVariable("BACKUP_DIR")
    ?? (OperatingSystem.IsWindows()
        ? @"C:\Users\proci\Desktop\ProgettoP\backup"
        : throw new InvalidOperationException(
            "Variabile d'ambiente BACKUP_DIR non impostata (obbligatoria fuori da Windows)."));

static string Mb(long by) => $"{by / 1024.0 / 1024.0:F1} MB";

switch (command)
{
    case "backup":
    {
        var conn = GetConn();
        var backupDir = GetBackupDir();
        Console.WriteLine($"Backup di {conn.Database} -> {backupDir} (pg_dump -Fc) ...");
        var r = DatabaseBackupHelper.Backup(conn, backupDir);
        if (!r.Success)
        {
            Console.Error.WriteLine($"BACKUP FALLITO: {r.Error ?? r.Integrity.Message}");
            return 1;
        }
        Console.WriteLine($"  OK: {r.BackupPath} ({Mb(r.SizeBytes)}, {r.Integrity.Message})");
        return 0;
    }
    case "verify":
    {
        var target = args.Length > 1 ? args[1] : DatabaseBackupHelper.ListBackups(GetBackupDir()).FirstOrDefault()?.FullPath;
        if (target is null) { Console.Error.WriteLine("Nessun backup da verificare."); return 1; }
        var r = DatabaseBackupHelper.IntegrityCheck(target);
        Console.WriteLine($"Verify {target}: {(r.Ok ? "OK" : "NON LEGGIBILE")} ({r.Message})");
        return r.Ok ? 0 : 1;
    }
    case "list":
    {
        var backups = DatabaseBackupHelper.ListBackups(args.Length > 1 ? args[1] : GetBackupDir());
        if (backups.Count == 0) { Console.WriteLine("Nessun backup."); return 0; }
        foreach (var bk in backups) Console.WriteLine($"  {bk.CreatedUtc:yyyy-MM-dd HH:mm:ss}  {Mb(bk.SizeBytes),10}  {bk.FileName}");
        return 0;
    }
    case "restore":
    {
        var conn = GetConn();
        var src = args.Length > 1 ? args[1] : DatabaseBackupHelper.ListBackups(GetBackupDir()).FirstOrDefault()?.FullPath;
        if (src is null) { Console.Error.WriteLine("Nessun backup da ripristinare."); return 1; }
        Console.Write($"Ripristinare {src} SOPRA il database {conn.Database} (pg_restore --clean)? Operazione distruttiva. [scrivi 'si']: ");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("si" or "sì" or "yes" or "y"))
        {
            Console.WriteLine("Annullato.");
            return 2;
        }
        DatabaseBackupHelper.Restore(conn, src);
        Console.WriteLine("Ripristino completato.");
        return 0;
    }
    default:
        Console.WriteLine("""
            DbBackup — backup del DB PostgreSQL di ProcioneMGR (pg_dump/pg_restore, devono essere nel PATH)
            Env richieste: ConnectionStrings__PostgresConnection (backup/restore);
                           BACKUP_DIR (dove non c'è un argomento esplicito; su Windows ha un default)
            Uso: DbBackup <comando> [arg]
              backup        pg_dump -Fc + verifica leggibilità, in BACKUP_DIR
              verify [f]    pg_restore --list sull'ultimo backup (o sul file indicato)
              list [dir]    elenca i backup esistenti
              restore [f]   ripristina l'ultimo backup (o il file indicato) con conferma
            """);
        return 0;
}
