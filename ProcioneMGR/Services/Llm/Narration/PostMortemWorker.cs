using Microsoft.Extensions.Options;

namespace ProcioneMGR.Services.Llm.Narration;

/// <summary>
/// [G4] Il worker che scrive i post-mortem. Tick lento (l'analisi di un trade chiuso non ha
/// fretta), spento per default, e mai bloccante: un errore si logga e si riprova al giro dopo.
///
/// <para>Vive nel guscio, come il resto del layer AI: legge trade chiusi e scrive righe di testo,
/// non tocca il motore.</para>
/// </summary>
public sealed class PostMortemWorker(
    IPostMortemService service,
    IOptionsMonitor<PostMortemOptions> options,
    ILogger<PostMortemWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ritardo iniziale: all'avvio l'app ha di meglio da fare che rileggere lo storico.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                if (options.CurrentValue.Enabled)
                {
                    await service.AnalyzeRecentAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Giro di post-mortem fallito; riprovo al prossimo tick.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
