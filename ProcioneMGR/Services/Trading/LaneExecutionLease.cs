using System.Data;
using Npgsql;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// [B0 PRD core-caldo] Lease di ESECUZIONE per corsia: advisory lock Postgres a livello di
/// sessione. "Mai due esecutori sulla stessa corsia" era retto dalla registrazione condizionale
/// (vedi <see cref="TradingServiceCollectionExtensions"/>) più la disciplina di deploy — il
/// Deployment remoto non deve mai essere vivo col toggle a false. Il lease trasforma quel patto
/// in invariante applicata dal DATABASE, che è l'unica cosa che i due processi condividono per
/// certo: chi non ottiene il lock non alimenta il motore e lo dice con LogCritical, quindi un
/// deploy incoerente fallisce a voce alta invece di aprire ordini due volte.
/// </summary>
public interface ILaneLease : IAsyncDisposable
{
    int LaneId { get; }

    /// <summary>
    /// Verifica che la sessione che detiene il lock sia ancora viva (SELECT 1). Se la connessione
    /// è caduta, Postgres ha già liberato il lock lato server: il chiamante DEVE smettere di
    /// alimentare il motore e riacquisire.
    /// </summary>
    ValueTask<bool> IsAliveAsync(CancellationToken ct = default);
}

/// <summary>Factory del lease. Null da <see cref="TryAcquireAsync"/> = lock detenuto da un altro processo.</summary>
public interface ILaneLeaseFactory
{
    Task<ILaneLease?> TryAcquireAsync(int laneId, CancellationToken ct = default);
}

/// <inheritdoc cref="ILaneLeaseFactory"/>
public sealed class NpgsqlLaneLeaseFactory(
    IConfiguration configuration,
    ILogger<NpgsqlLaneLeaseFactory> logger) : ILaneLeaseFactory
{
    /// <summary>Spazio dei lock di questa applicazione ('PROC' in ASCII): separa i nostri advisory lock da chiunque altro usi lo stesso database.</summary>
    internal const int LeaseClassId = 0x50524F43;

    public async Task<ILaneLease?> TryAcquireAsync(int laneId, CancellationToken ct = default)
    {
        var connectionString = configuration.GetConnectionString("PostgresConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:PostgresConnection mancante: il lease di corsia richiede il database.");

        // Pooling=false è SOSTANZA, non stile: un advisory lock di sessione vive con la connessione
        // FISICA. Col pool, Dispose la restituirebbe al pool senza chiuderla — il lock resterebbe
        // detenuto lato server da una connessione idle (fino a 5 minuti coi default), bloccando il
        // legittimo acquirente successivo. Senza pool, Dispose = chiusura fisica = rilascio certo.
        // KeepAlive fa emergere le connessioni morte invece di lasciarle "Open" per sempre lato client.
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            KeepAlive = 30,
            ApplicationName = $"procione-lane-lease-{laneId}",
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@class, @lane)", connection);
            cmd.Parameters.AddWithValue("class", LeaseClassId);
            cmd.Parameters.AddWithValue("lane", laneId);
            var acquired = (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
            if (!acquired)
            {
                await connection.DisposeAsync();
                return null;
            }
            logger.LogInformation("Corsia {LaneId}: lease di esecuzione acquisito.", laneId);
            return new NpgsqlLaneLease(laneId, connection, logger);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class NpgsqlLaneLease(int laneId, NpgsqlConnection connection, ILogger logger) : ILaneLease
    {
        public int LaneId => laneId;

        public async ValueTask<bool> IsAliveAsync(CancellationToken ct = default)
        {
            if (connection.State != ConnectionState.Open) return false;
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT 1", connection);
                await cmd.ExecuteScalarAsync(ct);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Chiudere la sessione rilascia il lock lato server: nessuna unlock esplicita, così
            // il rilascio è garantito anche su crash del processo (è il motivo della scelta).
            try { await connection.DisposeAsync(); }
            catch { }
            logger.LogInformation("Corsia {LaneId}: lease di esecuzione rilasciato.", laneId);
        }
    }
}
