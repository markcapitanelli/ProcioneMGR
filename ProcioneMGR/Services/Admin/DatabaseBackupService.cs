using Microsoft.Data.Sqlite;

namespace ProcioneMGR.Services.Admin;

/// <summary>
/// Wrapper iniettabile attorno a <see cref="DatabaseBackupHelper"/> per l'uso dalla UI (pagina
/// <c>/admin/backup</c>). Risolve il path del file <c>app.db</c> dalla connection string SQLite e la
/// cartella <c>backup/</c> relativa alla content root, così il chiamante non deve conoscere i path.
///
/// Il servizio è significativo solo quando il provider attivo è SQLite: PostgreSQL ha un proprio
/// meccanismo di backup (pg_dump/pg_restore, vedi docs/POSTGRES_MIGRATION.md). Con provider diverso
/// da SQLite <see cref="IsSqlite"/> è false e le operazioni lanciano <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class DatabaseBackupService
{
    private readonly string? _dbPath;
    private readonly string _backupDir;

    public DatabaseBackupService(IConfiguration configuration, IHostEnvironment env)
    {
        var provider = (configuration.GetValue<string>("Database:Provider") ?? "SQLite").Trim();
        var isSqlite = provider.Equals("SQLite", StringComparison.OrdinalIgnoreCase);

        if (isSqlite)
        {
            var conn = configuration.GetConnectionString("DefaultConnection")
                       ?? configuration.GetConnectionString("SqliteConnection")
                       ?? "DataSource=Data/app.db";
            // "DataSource=Data/app.db;Cache=Shared" -> "Data/app.db"
            var dataSource = new SqliteConnectionStringBuilder(conn).DataSource;
            _dbPath = Path.IsPathRooted(dataSource)
                ? dataSource
                : Path.Combine(env.ContentRootPath, dataSource);
        }

        _backupDir = Path.Combine(env.ContentRootPath, "backup");
    }

    /// <summary>True solo se il provider attivo è SQLite: le operazioni di backup hanno senso solo lì.</summary>
    public bool IsSqlite => _dbPath is not null;

    /// <summary>Path assoluto del file DB SQLite attivo (null se il provider non è SQLite).</summary>
    public string? DatabasePath => _dbPath;

    /// <summary>Cartella dove vivono i backup.</summary>
    public string BackupDirectory => _backupDir;

    private string RequireDbPath() => _dbPath
        ?? throw new InvalidOperationException("Backup disponibile solo con provider SQLite. Per PostgreSQL usare pg_dump/pg_restore.");

    /// <summary>Crea un backup verificato del DB attivo. Vedi <see cref="DatabaseBackupHelper.Backup"/>.</summary>
    public BackupResult CreateBackup() => DatabaseBackupHelper.Backup(RequireDbPath(), _backupDir);

    /// <summary>Elenca i backup esistenti, più recenti prima.</summary>
    public IReadOnlyList<BackupInfo> ListBackups() => DatabaseBackupHelper.ListBackups(_backupDir);

    /// <summary>Verifica l'integrità di un file di backup (<c>PRAGMA integrity_check</c>).</summary>
    public IntegrityResult VerifyBackup(string backupPath) => DatabaseBackupHelper.IntegrityCheck(backupPath);

    /// <summary>Ripristina un backup sul DB attivo (salva prima una copia <c>.pre-restore</c> di sicurezza).</summary>
    public void Restore(string backupPath) => DatabaseBackupHelper.Restore(backupPath, RequireDbPath());
}
