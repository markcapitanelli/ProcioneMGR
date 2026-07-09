using Npgsql;

namespace ProcioneMGR.Tests.Infrastructure;

/// <summary>
/// Accesso al database PostgreSQL REALE popolato (<c>procionemgr</c>, ~7.4M candele OHLCV) per i
/// pochi test che girano su dati storici reali invece che su schema effimero. La connection string
/// si legge dalla env <c>PROCIONE_TEST_DB</c>, con un default locale. In CI (dove il DB non esiste)
/// <see cref="IsAvailable"/> è false e i test si saltano da soli — nessun fallimento.
/// </summary>
public static class RealMarketDb
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("PROCIONE_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=procionemgr;Username=procione;Password=Procione2026Pg_secure";

    /// <summary>True se il DB è raggiungibile e contiene candele OHLCV; altrimenti il test va saltato.</summary>
    public static bool IsAvailable()
    {
        try
        {
            using var conn = new NpgsqlConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM \"OhlcvData\"";
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        catch
        {
            return false;
        }
    }
}
