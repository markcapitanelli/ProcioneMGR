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
        // Enabled è riletto a OGNI tick da IConfiguration (reloadOnChange): il toggle da
        // /admin/autonomy prende effetto a caldo, senza riavvio. L'intervallo invece è fisso
        // al primo avvio (PeriodicTimer): cambiarlo richiede riavvio.
        var interval = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("MarketData:SyncIntervalMinutes", 5)));
        logger.LogInformation("MarketDataSyncWorker avviato, intervallo {Interval} (Enabled={Enabled}).",
            interval, configuration.GetValue("MarketData:Enabled", true));

        // Breve attesa iniziale per non competere con lo startup dell'app.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Budget di ciclo = 2× l'intervallo: largo per un recupero profondo legittimo (il cursore
        // è incrementale, un arretrato grosso converge attraverso più tick), stretto abbastanza da
        // non lasciare il worker parcheggiato. Vedi RunCycleAsync per l'incidente che lo motiva.
        var budget = interval * 2;

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                if (configuration.GetValue("MarketData:Enabled", true))
                {
                    await RunCycleAsync(budget, stoppingToken);
                }
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

    /// <summary>
    /// Un ciclo di sync con TETTO DI TEMPO. Il tetto esiste per misura, non per scrupolo: il
    /// 2026-08-13 una richiesta klines è rimasta appesa senza risposta (thread starvation nel pod,
    /// cpu limit 1) e il worker — che aspettava il completamento del ciclo per riarmare il timer —
    /// è rimasto muto per 30 minuti, con 89 serie oltre tolleranza. Una chiamata appesa deve
    /// costare al massimo un ciclo, mai un pod zombie.
    /// <para>Pubblico per i test. Restituisce false se il budget è scaduto (ciclo interrotto:
    /// il cursore incrementale riprende da dov'era al tick successivo); true se completato.
    /// La cancellazione del CHIAMANTE (shutdown) ripropaga: non è un timeout.</para>
    /// </summary>
    public async Task<bool> RunCycleAsync(TimeSpan budget, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IMarketDataSyncService>();

        using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cycleCts.CancelAfter(budget);
        try
        {
            await sync.SyncAllEnabledAsync(cycleCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (cycleCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Ciclo di sincronizzazione oltre il budget di {Budget}: interrotto (una chiamata appesa non deve " +
                "parcheggiare il worker). Il cursore incrementale riprende dal punto raggiunto al prossimo tick.",
                budget);
            return false;
        }
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
