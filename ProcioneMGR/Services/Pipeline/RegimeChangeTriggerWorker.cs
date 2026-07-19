using Microsoft.Extensions.Options;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Worker del trigger contestuale (Fase 2, PRD Autonomia §5). Il trigger NON lancia mai run
/// direttamente: CHIEDE al <see cref="ICampaignPlanner"/> di anticipare la prossima esecuzione
/// (<see cref="ICampaignPlanner.WakeAsync"/> — backoff bypassato, run marcato "Event" ⚡), con
/// cooldown (default 6h) e nel pieno rispetto dello slot singolo del motore (già garantito da
/// StartRunAsync che rifiuta se occupato). Gate a monte: senza <c>Campaign:Enabled</c> il check
/// non parte nemmeno — il trigger esiste solo per servire il planner.
/// </summary>
public sealed class RegimeChangeTriggerWorker(
    IRegimeChangeDetector detector,
    ICampaignPlanner planner,
    IOptionsMonitor<RegimeTriggerOptions> options,
    IOptionsMonitor<CampaignOptions> campaignOptions,
    ILogger<RegimeChangeTriggerWorker> logger,
    ProcioneMGR.Services.Notifications.INotifier? notifier = null,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>In-memory: un riavvio azzera il cooldown, ma lo slot singolo del motore e il backoff del planner contengono comunque il fan-out.</summary>
    private DateTimeOffset? _lastFiredUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(5, options.CurrentValue.CheckIntervalMinutes));
        logger.LogInformation("RegimeChangeTriggerWorker avviato (check ogni {Interval}, cooldown {Cooldown}h).",
            interval, options.CurrentValue.CooldownHours);

        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Ciclo RegimeChangeTriggerWorker fallito; ritento al prossimo tick."); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("RegimeChangeTriggerWorker fermato.");
    }

    /// <summary>Un check completo (detector → cooldown → wake del planner → notifica). Pubblico per test.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled || !campaignOptions.CurrentValue.Enabled) return;

        var cooldown = TimeSpan.FromHours(Math.Max(1, options.CurrentValue.CooldownHours));
        if (_lastFiredUtc is DateTimeOffset last && _time.GetUtcNow() - last < cooldown) return;

        var check = await detector.CheckAsync(ct);
        if (check is null || !check.Triggered) return;

        var woken = await planner.WakeAsync($"Trigger contestuale: {check.Reason}", ct);
        if (woken == 0)
        {
            // Tutte le campagne in osservazione (o nessuna abilitata): il contesto è cambiato ma
            // non c'è niente da anticipare — nessun cooldown consumato.
            logger.LogDebug("Trigger contestuale rilevato ({Reason}) ma nessuna campagna da svegliare.", check.Reason);
            return;
        }

        _lastFiredUtc = _time.GetUtcNow();
        logger.LogWarning("TRIGGER CONTESTUALE scattato: {Reason}. {Woken} campagne svegliate (prossimo run ⚡ Event).",
            check.Reason, woken);
        if (notifier is not null)
        {
            await notifier.NotifyAsync(Notifications.NotificationSeverity.Info,
                "Trigger contestuale scattato", $"{check.Reason}. {woken} campagne svegliate: il prossimo run parte come Event.", ct);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
