using Microsoft.Extensions.Options;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Avanza nel tempo reale le fette dei piani di esecuzione (TWAP/VWAP/Iceberg) di UNA corsia:
/// ad ogni tick chiede al motore di piazzare le fette dovute. Uno per corsia (registrato nel loop
/// per-corsia di Program.cs, stesso pattern di <see cref="TradingWorker"/>). Rif. ROADMAP-QLIB §1.2.
///
/// Default safe-off: se <see cref="LiveExecutionOptions.Enabled"/> è false il tick è un no-op. Lo
/// switch è riletto AD OGNI tick (IOptionsMonitor, hot-reload) — dev'essere spegnibile senza restart.
/// </summary>
public sealed class ExecutionWorker(
    ITradingEngine engine,
    IOptionsMonitor<LiveExecutionOptions> options,
    ILogger<ExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.WorkerTickSeconds));
        logger.LogInformation("ExecutionWorker corsia {Lane} avviato (tick {Tick}).", engine.LaneId, tick);

        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(tick);
        do
        {
            try
            {
                if (options.CurrentValue.Enabled)
                {
                    await engine.ProcessDueExecutionSlicesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "ExecutionWorker corsia {Lane}: tick fallito; ritento.", engine.LaneId); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("ExecutionWorker corsia {Lane} fermato.", engine.LaneId);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
