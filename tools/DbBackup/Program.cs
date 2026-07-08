// DbBackup: strumento CLI per il backup sicuro del database SQLite di ProcioneMGR.
// Comandi: checkpoint | backup | verify | restore | list
// Condivide la logica con l'app tramite ProcioneMGR.Services.Admin.DatabaseBackupHelper.
using ProcioneMGR.Services.Admin;

const string DefaultDb = @"C:\Users\proci\Desktop\ProgettoP\ProcioneMGR\Data\app.db";
const string DefaultBackupDir = @"C:\Users\proci\Desktop\ProgettoP\backup";

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var dbPath = args.Length > 1 ? args[1] : DefaultDb;
var backupDir = args.Length > 2 ? args[2] : DefaultBackupDir;

static string Mb(long b) => $"{b / 1024.0 / 1024.0:F1} MB";

switch (command)
{
    case "checkpoint":
    {
        var r = DatabaseBackupHelper.Checkpoint(dbPath);
        Console.WriteLine($"Checkpoint WAL: busy={r.Busy}, frames={r.WalFrames}, checkpointed={r.Checkpointed} -> {(r.FullyCheckpointed ? "COMPLETO" : "PARZIALE (altre connessioni attive: fermare l'app)")}");
        return r.FullyCheckpointed ? 0 : 2;
    }
    case "backup":
    {
        Console.WriteLine($"Backup di {dbPath} -> {backupDir} ...");
        var r = DatabaseBackupHelper.Backup(dbPath, backupDir);
        if (!r.Success)
        {
            Console.Error.WriteLine($"BACKUP FALLITO: {r.Error} (checkpoint busy={r.Checkpoint.Busy})");
            return 1;
        }
        if (!r.Checkpoint.FullyCheckpointed)
            Console.WriteLine($"  ATTENZIONE: checkpoint parziale (busy={r.Checkpoint.Busy}) — l'app era attiva? Backup comunque verificato integro.");
        Console.WriteLine($"  OK: {r.BackupPath} ({Mb(r.SizeBytes)}, integrity {r.Integrity.Message})");
        return 0;
    }
    case "verify":
    {
        var target = args.Length > 1 ? args[1] : DatabaseBackupHelper.ListBackups(backupDir).FirstOrDefault()?.FullPath;
        if (target is null) { Console.Error.WriteLine("Nessun backup da verificare."); return 1; }
        var r = DatabaseBackupHelper.IntegrityCheck(target);
        Console.WriteLine($"Verify {target}: {(r.Ok ? "OK" : "CORROTTO")} ({r.Message})");
        return r.Ok ? 0 : 1;
    }
    case "list":
    {
        var backups = DatabaseBackupHelper.ListBackups(backupDir);
        if (backups.Count == 0) { Console.WriteLine("Nessun backup."); return 0; }
        foreach (var b in backups) Console.WriteLine($"  {b.CreatedUtc:yyyy-MM-dd HH:mm:ss}  {Mb(b.SizeBytes),10}  {b.FileName}");
        return 0;
    }
    case "restore":
    {
        var src = args.Length > 1 ? args[1] : DatabaseBackupHelper.ListBackups(backupDir).FirstOrDefault()?.FullPath;
        if (src is null) { Console.Error.WriteLine("Nessun backup da ripristinare."); return 1; }
        Console.Write($"Ripristinare {src} SOPRA {dbPath}? Il DB corrente sarà salvato come .pre-restore. [scrivi 'si']: ");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("si" or "sì" or "yes" or "y"))
        {
            Console.WriteLine("Annullato.");
            return 2;
        }
        DatabaseBackupHelper.Restore(src, dbPath);
        Console.WriteLine("Ripristino completato.");
        return 0;
    }
    default:
        Console.WriteLine("""
            DbBackup — backup sicuro del DB SQLite di ProcioneMGR
            Uso: DbBackup <comando> [dbPath] [backupDir]
              checkpoint   forza WAL checkpoint(TRUNCATE) (app deve essere ferma per checkpoint completo)
              backup       checkpoint + copia verificata (integrity_check) in backup/
              verify [f]   integrity_check sull'ultimo backup (o sul file indicato)
              list         elenca i backup esistenti
              restore [f]  ripristina l'ultimo backup (o il file indicato) con conferma
            """);
        return 0;
}
