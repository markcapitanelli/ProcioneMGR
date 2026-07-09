using Npgsql;

namespace ProcioneMGR.Services.Admin;

/// <summary>
/// Wrapper iniettabile attorno a <see cref="DatabaseBackupHelper"/> per l'uso dalla UI (pagina
/// <c>/admin/backup</c>). Risolve i parametri di connessione PostgreSQL dalla connection string
/// <c>PostgresConnection</c> e la cartella <c>backup/</c> relativa alla content root, così il
/// chiamante non deve conoscerli.
///
/// Il backup usa gli strumenti nativi <c>pg_dump</c>/<c>pg_restore</c> (devono essere nel PATH):
/// vedi <see cref="DatabaseBackupHelper"/> e docs/POSTGRES_MIGRATION.md.
/// </summary>
public sealed class DatabaseBackupService
{
    private readonly PgConnectionInfo _conn;
    private readonly string _backupDir;

    public DatabaseBackupService(IConfiguration configuration, IHostEnvironment env)
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

        _backupDir = Path.Combine(env.ContentRootPath, "backup");
    }

    /// <summary>Nome del database di destinazione (per la UI).</summary>
    public string TargetDatabase => _conn.Database;

    /// <summary>Cartella dove vivono i backup.</summary>
    public string BackupDirectory => _backupDir;

    /// <summary>Crea un backup verificato del DB attivo. Vedi <see cref="DatabaseBackupHelper.Backup"/>.</summary>
    public BackupResult CreateBackup() => DatabaseBackupHelper.Backup(_conn, _backupDir);

    /// <summary>Elenca i backup esistenti, più recenti prima.</summary>
    public IReadOnlyList<BackupInfo> ListBackups() => DatabaseBackupHelper.ListBackups(_backupDir);

    /// <summary>Verifica la leggibilità di un file di backup (<c>pg_restore --list</c>).</summary>
    public IntegrityResult VerifyBackup(string backupPath) => DatabaseBackupHelper.IntegrityCheck(backupPath);

    /// <summary>Ripristina un backup nel DB attivo (<c>pg_restore --clean --if-exists</c>).</summary>
    public void Restore(string backupPath) => DatabaseBackupHelper.Restore(_conn, backupPath);
}
