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
    ProcioneMGR.Services.Observability.ProcioneMetrics? metrics = null,
    ProcioneMGR.Services.Notifications.INotifier? notifier = null) : BackgroundService
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

            // Whitelist ESPLICITA delle transizioni lecite, con la modalità di partenza compresa
            // ([AF4a]: prima bastava la coppia flag+SuggestedMode; con la retrocessione da Live la
            // partenza è diventata parte del contratto — Live→Paper diretto non esiste).
            if (d.ShouldPromote && d.CurrentMode == TradingMode.Paper && d.SuggestedMode == TradingMode.Testnet)
            {
                await ActAsync(d.LaneId, TradingMode.Testnet, d.Reason, Notifications.NotificationSeverity.Info, ct);
            }
            else if (d.ShouldDemote && d.CurrentMode == TradingMode.Testnet && d.SuggestedMode == TradingMode.Paper)
            {
                await ActAsync(d.LaneId, TradingMode.Paper, d.Reason, Notifications.NotificationSeverity.Info, ct);
            }
            else if (d.ShouldDemote && d.CurrentMode == TradingMode.Live && d.SuggestedMode == TradingMode.Testnet)
            {
                // [AF4a] La retrocessione di sicurezza: soldi veri appena messi fuori pericolo —
                // Warning, non Info: l'operatore deve alzare gli occhi.
                await ActAsync(d.LaneId, TradingMode.Testnet, d.Reason, Notifications.NotificationSeverity.Warning, ct);
            }
            else if (d.ShouldPromote || d.ShouldDemote)
            {
                // Difesa in profondità (livello 2): la decisione CHIEDE un'azione ma NON corrisponde a
                // nessuna transizione lecita (Paper→Testnet / Testnet→Paper) — sintomo di evaluator
                // buggato o config corrotta, es. SuggestedMode=Live. Non agiamo MAI (il confine
                // anti-Live regge sotto), ma un bug così NON deve sparire in silenzio: qui diventa un
                // errore visibile in log/observability invece di uno scarto muto.
                logger.LogError(
                    "Corsia {Lane}: decisione di promozione INCOERENTE ignorata (promote={Promote}, demote={Demote}, suggested={Suggested}, current={Current}). {Reason}",
                    d.LaneId, d.ShouldPromote, d.ShouldDemote, d.SuggestedMode, d.CurrentMode, d.Reason);
            }
        }
    }

    private async Task ActAsync(int laneId, TradingMode newMode, string reason,
        Notifications.NotificationSeverity severity, CancellationToken ct)
    {
        try
        {
            await promoter.PromoteLaneAsync(laneId, newMode, reason, ct);
            metrics?.RecordLanePromotion(laneId, newMode.ToString());
            // Fase 4 (PRD Autonomia §7): la promozione automatica è una delle azioni da riferire.
            if (notifier is not null)
            {
                await notifier.NotifyAsync(severity, $"Corsia {laneId} → {newMode}", reason, ct);
            }
        }
        catch (Exception ex)
        {
            // [2026-08-17] Es. credenziali della modalità di destinazione mancanti. Il commento
            // che stava qui prometteva «si ritenta al prossimo tick»: FALSO, perché il tick
            // successivo salta le corsie non in esecuzione (`if (!d.IsRunning) continue`) e
            // PromoteLaneAsync ha già lasciato il motore fermo. La corsia resta ferma per sempre.
            // Il fatto grave è che le posizioni REALI sono già state chiuse reduce-only PRIMA dello
            // StartAsync fallito: sulla retrocessione di sicurezza Live→Testnet la piattaforma ha
            // appena disfatto un'operatività reale, e senza questa notifica l'unico segnale sarebbe
            // stato una riga di log fra le altre.
            logger.LogError(ex, "Cambio modalità corsia {Lane} → {Mode} fallito: {Msg}", laneId, newMode, ex.Message);

            if (notifier is not null)
            {
                try
                {
                    await notifier.NotifyAsync(
                        severity == Notifications.NotificationSeverity.Info
                            ? Notifications.NotificationSeverity.Warning
                            : severity,
                        $"Corsia {laneId}: passaggio a {newMode} FALLITO",
                        $"{ex.Message} Le posizioni sono già state chiuse e il motore è FERMO: la corsia resta "
                        + "ferma finché non la riavvii da /trading — l'automatismo non la ritenta, perché salta "
                        + "le corsie non in esecuzione.", ct);
                }
                catch (Exception notifyEx)
                {
                    // Un canale di notifica giù non deve trasformare un fallimento in un'eccezione
                    // che risale al tick del worker.
                    logger.LogWarning(notifyEx, "Notifica del cambio modalità fallito non inviata (corsia {Lane}).", laneId);
                }
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
