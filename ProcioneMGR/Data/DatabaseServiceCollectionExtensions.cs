using Microsoft.EntityFrameworkCore;

namespace ProcioneMGR.Data;

/// <summary>
/// Registrazione condivisa del DbContextFactory Postgres per il monolite e gli host satellite
/// (ProcioneMGR.Ingestion oggi; trading/ml nelle fasi successive): unica fonte per le opzioni
/// Npgsql e per il MigrationsAssembly, così gli host non divergono su resilienza/timeout.
/// La connection string viene risolta SUBITO (fail-fast a startup, non alla prima creazione
/// del context) e la lambda delle opzioni cattura solo la stringa, non l'intero builder.
/// </summary>
public static class DatabaseServiceCollectionExtensions
{
    public static IServiceCollection AddProcioneDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var pg = configuration.GetConnectionString("PostgresConnection")
                 ?? throw new InvalidOperationException("Connection string 'PostgresConnection' non trovata.");
        services.AddDbContextFactory<ApplicationDbContext>(options =>
            options.UseNpgsql(pg, npgsql => npgsql
                .MigrationsAssembly("ProcioneMGR.Migrations.Postgres")
                // Retrying execution strategy: assorbe i transitori (rete, failover, connessione
                // ricreata) che in K8s sono fisiologici. Il punto più delicato è la persistenza
                // POST-fill in TradingEngine.ProcessCandleAsync: un blip verso Postgres lì, senza
                // retry, lascerebbe un ordine reale eseguito e non persistito (rete di sicurezza:
                // ReconcileUncertainOrder). Sicuro qui perché il codice NON apre transazioni
                // esplicite (BeginTransaction/TransactionScope): l'unico caso incompatibile con la
                // strategia di retry non è presente. Ereditata da tutti gli host (monolite + satelliti).
                .EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null)));
        return services;
    }
}
