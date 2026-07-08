namespace ProcioneMGR.Services.Ensemble;

/// <summary>
/// Worker che esegue il rebalancing automatico dell'ensemble quando è abilitato.
/// Controlla periodicamente la configurazione; se <c>IsEnabled</c> e se è passato
/// <c>RebalanceIntervalDays</c> dall'ultimo rebalance, ne esegue uno nuovo.
/// </summary>
public sealed class EnsembleRebalanceWorker(
    IEnsembleManager ensemble,
    ILogger<EnsembleRebalanceWorker> logger) : BackgroundService
{
    // Frequenza con cui il worker "si sveglia" per valutare se è ora di ribilanciare.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("EnsembleRebalanceWorker avviato (check ogni {Interval}).", CheckInterval);

        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                var cfg = await ensemble.GetConfigurationAsync(stoppingToken);
                if (!cfg.IsEnabled || cfg.Strategies.All(s => !s.IsActive))
                {
                    continue;
                }

                var status = await ensemble.GetStatusAsync(stoppingToken);
                var due = status.NextRebalanceUtc is null || status.NextRebalanceUtc <= DateTime.UtcNow;
                if (due)
                {
                    await ensemble.RebalanceAsync("Scheduled", stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ciclo di rebalancing fallito; ritento al prossimo tick.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("EnsembleRebalanceWorker fermato.");
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
