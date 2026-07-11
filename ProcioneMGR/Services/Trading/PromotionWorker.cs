namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Rivaluta periodicamente (default ogni 6 ore) le corsie di trading e, se abilitato, promuove
/// automaticamente a Testnet quelle che hanno performato bene abbastanza a lungo — e retrocede a
/// Paper quelle Testnet il cui edge è svanito. La promozione è una decisione importante: cadenza
/// oraria bassa apposta (reagisce in meno di un giorno, non ogni minuto).
///
/// SAFETY: promuove/retrocede solo tra Paper e Testnet. NON promuove MAI a Live (neanche con metriche
/// eccellenti): Testnet→Live resta manuale dietro SafetyChecker + conferma umana. Le corsie in Live
/// non vengono toccate.
/// </summary>
public sealed class PromotionWorker(
    IPromotionEvaluator evaluator,
    ILanePromoter promoter,
    Microsoft.Extensions.Options.IOptionsMonitor<PromotionEvaluatorOptions> options,
    ILogger<PromotionWorker> logger,
    ProcioneMGR.Services.Observability.ProcioneMetrics? metrics = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Intervallo fisso all'avvio (PeriodicTimer): cambiarlo richiede riavvio. Le soglie e i
        // flag di auto-promozione/retrocessione sono invece letti a ogni valutazione (hot).
        var interval = TimeSpan.FromHours(Math.Max(1, options.CurrentValue.EvaluationIntervalHours));
        logger.LogInformation("PromotionWorker avviato (check ogni {Interval}, auto-promozione={Auto}).",
            interval, options.CurrentValue.AutoPromoteToTestnet);

        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Ciclo PromotionWorker fallito; ritento al prossimo tick."); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("PromotionWorker fermato.");
    }

    /// <summary>Un tick: valuta tutte le corsie e agisce sulle decisioni. Pubblico per test.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var decisions = await evaluator.EvaluateAllLanesAsync(ct);
        foreach (var d in decisions)
        {
            ct.ThrowIfCancellationRequested();

            // Solo corsie attive: non promuoviamo una corsia ferma (nessuna sessione da valutare/spostare).
            if (!d.IsRunning) continue;

            if (d.ShouldPromote && d.SuggestedMode == TradingMode.Testnet)
            {
                await ActAsync(d.LaneId, TradingMode.Testnet, d.Reason, ct);
            }
            else if (d.ShouldDemote && d.SuggestedMode == TradingMode.Paper)
            {
                await ActAsync(d.LaneId, TradingMode.Paper, d.Reason, ct);
            }
        }
    }

    private async Task ActAsync(int laneId, TradingMode newMode, string reason, CancellationToken ct)
    {
        try
        {
            await promoter.PromoteLaneAsync(laneId, newMode, reason, ct);
            metrics?.RecordLanePromotion(laneId, newMode.ToString());
        }
        catch (Exception ex)
        {
            // Es. credenziali Testnet mancanti: errore chiaro, corsia lasciata ferma, si ritenta al prossimo tick.
            logger.LogError(ex, "Cambio modalità corsia {Lane} → {Mode} fallito: {Msg}", laneId, newMode, ex.Message);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
