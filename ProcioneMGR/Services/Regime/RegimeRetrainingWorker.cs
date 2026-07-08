using Microsoft.Extensions.Configuration;
using ProcioneMGR.Services.Ensemble;

namespace ProcioneMGR.Services.Regime;

/// <summary>
/// Riallena periodicamente il modello di regime per la serie dell'ensemble (il mercato
/// cambia). Attiva il nuovo modello SOLO se il Silhouette migliora di almeno +0.05,
/// altrimenti lo scarta. Config: appsettings "MarketRegime:RetrainingIntervalDays" (default 7).
/// </summary>
public sealed class RegimeRetrainingWorker(
    IRegimeDetector detector,
    IEnsembleManager ensemble,
    IConfiguration configuration,
    ILogger<RegimeRetrainingWorker> logger) : BackgroundService
{
    private const double SilhouetteImprovementThreshold = 0.05;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("MarketRegime:Enabled", true))
        {
            logger.LogInformation("RegimeRetrainingWorker disabilitato da configurazione.");
            return;
        }

        var days = Math.Max(1, configuration.GetValue("MarketRegime:RetrainingIntervalDays", 7));
        var interval = TimeSpan.FromDays(days);
        logger.LogInformation("RegimeRetrainingWorker avviato (ogni {Days} giorni).", days);

        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await RetrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retraining regime fallito; ritento al prossimo tick.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("RegimeRetrainingWorker fermato.");
    }

    private async Task RetrainAsync(CancellationToken ct)
    {
        var cfg = await ensemble.GetConfigurationAsync(ct);
        if (cfg.Strategies.Count == 0)
        {
            return; // niente ensemble configurato: niente da riallenare
        }

        var to = DateTime.UtcNow;
        var trainingConfig = new TrainingConfiguration
        {
            ExchangeName = cfg.ExchangeName,
            Symbol = cfg.Symbol,
            Timeframe = cfg.Timeframe,
            From = to.AddYears(-2),
            To = to,
            NumberOfRegimes = 4,
            MaxIterations = 100,
        };

        var current = await detector.LoadLatestModelAsync(ct);
        var currentSil = current is not null && current.Symbol == cfg.Symbol && current.Timeframe == cfg.Timeframe
            ? current.SilhouetteScore : double.MinValue;

        // Addestra SENZA attivare, poi decidi.
        var candidate = await detector.TrainAsync(trainingConfig, activate: false, ct);

        if (currentSil == double.MinValue || candidate.SilhouetteScore >= currentSil + SilhouetteImprovementThreshold)
        {
            await detector.ActivateModelAsync(candidate, ct);
            logger.LogInformation("Retrained regime model for {Symbol} {Tf}. Silhouette: {Old:F2} → {New:F2}. New model active.",
                cfg.Symbol, cfg.Timeframe, currentSil == double.MinValue ? 0 : currentSil, candidate.SilhouetteScore);
        }
        else
        {
            logger.LogInformation("Retrained regime model for {Symbol} {Tf}: kept existing (Silhouette {New:F2} <= {Old:F2}+{Thr}).",
                cfg.Symbol, cfg.Timeframe, candidate.SilhouetteScore, currentSil, SilhouetteImprovementThreshold);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
