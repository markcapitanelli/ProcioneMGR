using Microsoft.Data.Sqlite;

namespace ProcioneMGR.Services.Admin;

/// <summary>Esito di un checkpoint WAL. <see cref="FullyCheckpointed"/> è true solo se nessun'altra
/// connessione tratteneva il WAL (busy == 0): condizione necessaria per un backup sicuro.</summary>
public sealed record CheckpointResult(bool FullyCheckpointed, int Busy, int WalFrames, int Checkpointed);

/// <summary>Esito di <c>PRAGMA integrity_check</c>: Ok true se il DB è integro ("ok").</summary>
public sealed record IntegrityResult(bool Ok, string Message);

/// <summary>Esito di un backup completo.</summary>
public sealed record BackupResult(bool Success, string BackupPath, long SizeBytes, IntegrityResult Integrity, CheckpointResult Checkpoint, string? Error = null);

/// <summary>Metadati di un file di backup già presente.</summary>
public sealed record BackupInfo(string FileName, string FullPath, DateTime CreatedUtc, long SizeBytes);

/// <summary>
/// Helper (puro, basato su path) per il backup sicuro del database SQLite.
///
/// Il passo critico è il <b>WAL checkpoint TRUNCATE</b>: forza la scrittura di tutte le pagine dal
/// write-ahead log nel file principale e svuota il WAL, così una successiva copia del file cattura un
/// database auto-consistente invece di uno stato "strappato". Senza checkpoint, copiare <c>app.db</c>
/// mentre il trading engine scrive può produrre un file corrotto.
///
/// Statico e senza stato (solo path) per essere testabile in isolamento contro un file SQLite
/// temporaneo. Le connessioni usano <c>Pooling=False</c>: su Windows una connessione in pool
/// tratterrebbe l'handle del file impedendo la copia (sharing violation).
/// </summary>
public static class DatabaseBackupHelper
{
    private static string ConnString(string dbPath) => new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
    }.ToString();

    /// <summary>Esegue <c>PRAGMA wal_checkpoint(TRUNCATE)</c>. Restituisce (busy, log, checkpointed).
    /// busy != 0 ⇒ altre connessioni attive: il backup NON è sicuro finché non vengono chiuse.</summary>
    public static CheckpointResult Checkpoint(string dbPath)
    {
        using var conn = new SqliteConnection(ConnString(dbPath));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = cmd.ExecuteReader();
        int busy = 0, log = 0, ckpt = 0;
        if (reader.Read())
        {
            busy = reader.GetInt32(0);
            log = reader.GetInt32(1);
            ckpt = reader.GetInt32(2);
        }
        return new CheckpointResult(busy == 0, busy, log, ckpt);
    }

    /// <summary>Esegue <c>PRAGMA integrity_check</c> su un file SQLite qualsiasi.</summary>
    public static IntegrityResult IntegrityCheck(string dbPath)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var lines = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read()) lines.Add(reader.GetString(0));
        }
        var ok = lines.Count == 1 && string.Equals(lines[0], "ok", StringComparison.OrdinalIgnoreCase);
        return new IntegrityResult(ok, string.Join("; ", lines));
    }

    /// <summary>
    /// Backup completo e verificato: (1) checkpoint TRUNCATE, (2) copia del file (+ -wal/-shm se ancora
    /// presenti) in <paramref name="backupDir"/> con timestamp, (3) <c>integrity_check</c> sulla copia.
    /// Se l'integrità fallisce, la copia viene eliminata e il risultato è <c>Success=false</c>.
    /// </summary>
    public static BackupResult Backup(string dbPath, string backupDir)
    {
        if (!File.Exists(dbPath))
            return new BackupResult(false, string.Empty, 0, new IntegrityResult(false, "db assente"), new CheckpointResult(false, 0, 0, 0), $"File non trovato: {dbPath}");

        Directory.CreateDirectory(backupDir);
        var checkpoint = Checkpoint(dbPath);
        // Le connessioni con Pooling=False sono chiuse: gli handle sono rilasciati, la copia è sicura.

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var baseName = Path.GetFileName(dbPath);
        var backupPath = Path.Combine(backupDir, $"{baseName}.{stamp}.bak");

        File.Copy(dbPath, backupPath, overwrite: false);
        // -wal/-shm: dopo un TRUNCATE pulito il WAL è vuoto, ma copiali se esistono ancora per completezza.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var side = dbPath + suffix;
            if (File.Exists(side)) File.Copy(side, backupPath + suffix, overwrite: false);
        }

        var integrity = IntegrityCheck(backupPath);
        if (!integrity.Ok)
        {
            TryDelete(backupPath);
            foreach (var suffix in new[] { "-wal", "-shm" }) TryDelete(backupPath + suffix);
            return new BackupResult(false, backupPath, 0, integrity, checkpoint, "integrity_check fallito sulla copia; backup eliminato");
        }

        var size = new FileInfo(backupPath).Length;
        return new BackupResult(true, backupPath, size, integrity, checkpoint);
    }

    /// <summary>Elenca i backup (*.bak) in <paramref name="backupDir"/>, più recenti prima.</summary>
    public static IReadOnlyList<BackupInfo> ListBackups(string backupDir)
    {
        if (!Directory.Exists(backupDir)) return [];
        return Directory.EnumerateFiles(backupDir, "*.bak")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new BackupInfo(f.Name, f.FullName, f.CreationTimeUtc, f.Length))
            .ToList();
    }

    /// <summary>
    /// Ripristina un backup sul path del DB attivo. Verifica prima l'integrità del backup; salva il DB
    /// corrente in un file <c>.pre-restore</c> di sicurezza; rimuove eventuali -wal/-shm orfani.
    /// </summary>
    public static void Restore(string backupPath, string dbPath)
    {
        var integrity = IntegrityCheck(backupPath);
        if (!integrity.Ok)
            throw new InvalidOperationException($"Backup non integro, ripristino annullato: {integrity.Message}");

        if (File.Exists(dbPath))
        {
            var safety = dbPath + $".pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(dbPath, safety, overwrite: false);
        }
        // Rimuovi WAL/SHM del DB attivo: il file ripristinato è auto-consistente e non deve ereditarli.
        foreach (var suffix in new[] { "-wal", "-shm" }) TryDelete(dbPath + suffix);
        File.Copy(backupPath, dbPath, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
