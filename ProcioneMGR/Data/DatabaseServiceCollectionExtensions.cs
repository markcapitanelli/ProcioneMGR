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
            options.UseNpgsql(pg, npgsql => npgsql.MigrationsAssembly("ProcioneMGR.Migrations.Postgres")));
        return services;
    }
}
