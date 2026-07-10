using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Npgsql;
using ProcioneMGR.Services.Security;
using Testcontainers.PostgreSql;

namespace ProcioneMGR.Tests.Infrastructure;

/// <summary>
/// Setup a livello di assembly di test: attiva lo switch Npgsql "legacy timestamp behavior" PRIMA che
/// qualunque data source venga costruito. Necessario perché lo schema mappa tutte le date a
/// <c>timestamp without time zone</c> e parte del codice/test scrive DateTime con <c>Kind=Utc</c>, che
/// senza lo switch Npgsql rifiuterebbe. Replica ciò che <c>Program.cs</c> fa a runtime (qui Program.cs
/// non gira). Il <see cref="ModuleInitializerAttribute"/> garantisce l'esecuzione al caricamento del
/// modulo di test, prima di ogni fixture o test.
/// </summary>
internal static class TestNpgsqlSetup
{
    [ModuleInitializer]
    internal static void Init() => AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
}

/// <summary>
/// Fixture condivisa (una per intera run). Di default avvia UN container PostgreSQL effimero via
/// Testcontainers (richiede Docker). In alternativa, se è impostata la env <c>POSTGRES_TEST_ADMIN</c>
/// con una connection string di amministrazione, usa quel server già esistente SENZA container (utile
/// dove Docker non è disponibile) — il ruolo indicato deve poter fare <c>CREATE DATABASE</c>.
///
/// Ogni test ottiene un database isolato con nome univoco tramite <see cref="CreateDatabase"/>, così
/// da replicare l'isolamento che i vecchi test avevano con file/<c>:memory:</c> SQLite distinti. I
/// database creati vengono rimossi al termine della run (col container basta buttarlo, ma sul server
/// esterno la pulizia è necessaria per non lasciare DB orfani).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer? _container;
    private readonly ConcurrentBag<string> _createdDatabases = [];
    private string _adminConnectionString = "";

    public PostgresFixture()
    {
        var external = Environment.GetEnvironmentVariable("POSTGRES_TEST_ADMIN");
        if (string.IsNullOrWhiteSpace(external))
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
        }
        else
        {
            _adminConnectionString = external;
        }
    }

    /// <summary>Connection string verso il database di amministrazione (per <c>CREATE DATABASE</c>).</summary>
    public string AdminConnectionString => _adminConnectionString;

    public async Task InitializeAsync()
    {
        if (_container is not null)
        {
            await _container.StartAsync();
            _adminConnectionString = _container.GetConnectionString();
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        // Server esterno: droppa i database di test creati (FORCE termina eventuali connessioni residue).
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync();
        foreach (var name in _createdDatabases)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
            try { await cmd.ExecuteNonQueryAsync(); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Crea un database con nome univoco e restituisce la relativa connection string. Lo schema NON è
    /// ancora creato: il chiamante fa <c>EnsureCreated()</c> come prima.
    /// </summary>
    public string CreateDatabase()
    {
        var name = "t" + Guid.NewGuid().ToString("N");
        using (var conn = new NpgsqlConnection(_adminConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{name}\"";
            cmd.ExecuteNonQuery();
        }
        _createdDatabases.Add(name);
        return new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = name }.ConnectionString;
    }
}

/// <summary>Collezione xUnit che condivide una sola <see cref="PostgresFixture"/> fra tutte le classi
/// di test marcate con <c>[Collection("Postgres")]</c>.</summary>
[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}

/// <summary>Cifratura passthrough condivisa dai test (nessuna dipendenza dalla chiave master reale).</summary>
public sealed class PassthroughEncryption : IEncryptionService
{
    public string Encrypt(string plaintext) => plaintext;
    public string Decrypt(string ciphertext) => ciphertext;
}
