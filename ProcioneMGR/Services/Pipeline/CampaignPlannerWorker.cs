using Microsoft.Extensions.Options;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Worker del Campaign Planner (Fase 1, PRD Autonomia): loop sottile sul
/// <see cref="ICampaignPlanner"/> col pattern PeriodicTimer degli altri worker.
/// Il gate <c>Campaign:Enabled</c> è dentro <see cref="ICampaignPlanner.TickAsync"/> (hot-reload):
/// col default OFF questo worker gira a vuoto e la piattaforma si comporta esattamente come prima.
/// </summary>
public sealed class CampaignPlannerWorker(
    ICampaignPlanner planner,
    IOptionsMonitor<CampaignOptions> options,
    ILogger<CampaignPlannerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, options.CurrentValue.TickSeconds));
        logger.LogInformation("CampaignPlannerWorker avviato (tick ogni {Interval}, enabled={Enabled}).",
            interval, options.CurrentValue.Enabled);

        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try { await planner.TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Ciclo CampaignPlannerWorker fallito; ritento al prossimo tick."); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("CampaignPlannerWorker fermato.");
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
