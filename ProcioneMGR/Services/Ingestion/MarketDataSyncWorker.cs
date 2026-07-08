using Microsoft.Extensions.Configuration;

namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// Worker schedulato: a intervalli regolari sincronizza tutte le serie abilitate
/// della watchlist. Gira nel processo dell'app come <see cref="BackgroundService"/>.
///
/// Configurazione (sezione "MarketData" in appsettings.json):
///  - Enabled              : true/false per accendere/spegnere il worker (default true)
///  - SyncIntervalMinutes  : intervallo tra i cicli (default 5)
///  - DefaultBackfillDays   : finestra di backfill alla prima sync di una serie (default 7)
///
/// Usa <see cref="IServiceScopeFactory"/> perche' i servizi di dominio sono scoped
/// mentre il worker e' singleton.
/// </summary>
public sealed class MarketDataSyncWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<MarketDataSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("MarketData:Enabled", true))
        {
            logger.LogInformation("MarketDataSyncWorker disabilitato da configurazione.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("MarketData:SyncIntervalMinutes", 5)));
        logger.LogInformation("MarketDataSyncWorker avviato, intervallo {Interval}.", interval);

        // Breve attesa iniziale per non competere con lo startup dell'app.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<IMarketDataSyncService>();
                await sync.SyncAllEnabledAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ciclo di sincronizzazione fallito; ritento al prossimo tick.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("MarketDataSyncWorker fermato.");
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
