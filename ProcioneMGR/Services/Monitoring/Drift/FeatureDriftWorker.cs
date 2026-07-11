using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Monitoring.Drift;

/// <summary>Opzioni del <see cref="FeatureDriftWorker"/> (sezione config "Drift"). Default safe-off.</summary>
public sealed class DriftMonitorOptions
{
    /// <summary>Master switch. Default false: il worker si spegne subito, il drift resta valutabile on-demand dalla UI.</summary>
    public bool Enabled { get; set; }

    /// <summary>Cadenza di valutazione automatica (ore).</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>Quante candele recenti usare come campione "corrente".</summary>
    public int RecentCandles { get; set; } = 200;

    /// <summary>
    /// Ciclo chiuso (Fase 2): quando un modello <b>Champion</b> va in drift, ritiralo dal registry e
    /// accoda un retrain. Default true (il worker è comunque opt-in). Il retrain NON è automatico —
    /// si marca soltanto la richiesta per l'operatore. Nessun impatto sul trading Live.
    /// </summary>
    public bool RetireChampionOnAlert { get; set; } = true;

    /// <summary>Numero minimo di feature in <c>Alert</c> per far scattare il ritiro del Champion.</summary>
    public int MinAlertsToRetire { get; set; } = 1;
}

/// <summary>
/// Valuta periodicamente (opt-in) il drift delle feature di ogni <see cref="SavedMlModel"/> e logga
/// warning/alert. AFFIANCA il <c>StrategyDecayMonitor</c>: è un segnale anticipatore sugli input,
/// non una decisione di trading — non apre/chiude nulla, scrive solo log (rif. ROADMAP-QLIB §1.5).
/// Default spento (<see cref="DriftMonitorOptions.Enabled"/>=false), come le altre automazioni.
/// </summary>
public sealed class FeatureDriftWorker(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IFeatureDriftMonitor monitor,
    ProcioneMGR.Services.Registry.IModelRegistry registry,
    Microsoft.Extensions.Options.IOptionsMonitor<DriftMonitorOptions> options,
    ILogger<FeatureDriftWorker> logger,
    ProcioneMGR.Services.Observability.ProcioneMetrics? metrics = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Enabled è valutato a OGNI tick (modello ExecutionWorker), non all'avvio: il toggle da
        // /admin/autonomy prende effetto a caldo. L'intervallo invece è fisso al primo avvio
        // (PeriodicTimer): cambiarlo richiede riavvio — un timer spento costa nulla.
        var interval = TimeSpan.FromHours(Math.Max(1, options.CurrentValue.IntervalHours));
        logger.LogInformation("FeatureDriftWorker avviato (check ogni {Interval}, Enabled={Enabled}).",
            interval, options.CurrentValue.Enabled);

        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                if (options.CurrentValue.Enabled)
                {
                    await TickAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Ciclo FeatureDriftWorker fallito; ritento al prossimo tick."); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("FeatureDriftWorker fermato.");
    }

    /// <summary>Un tick: valuta il drift di ogni modello salvato e logga gli scostamenti. Pubblico per test e per "Esegui ora" da /admin/autonomy.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var opt = options.CurrentValue; // snapshot coerente per l'intero tick
        List<SavedMlModel> models;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            models = await db.SavedMlModels.AsNoTracking().ToListAsync(ct);
        }

        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();

            List<OhlcvData> recent;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                recent = await db.OhlcvData.AsNoTracking()
                    .Where(c => c.Symbol == model.Symbol && c.Timeframe == model.Timeframe)
                    .OrderByDescending(c => c.TimestampUtc)
                    .Take(Math.Max(20, opt.RecentCandles))
                    .ToListAsync(ct);
            }
            recent.Reverse(); // rimetti in ordine cronologico

            var reports = await monitor.EvaluateAsync(model, recent, ct: ct);
            var drifting = reports.Where(r => r.Overall != DriftSeverity.None).ToList();
            if (drifting.Count == 0) continue;

            var alerts = drifting.Count(r => r.Overall == DriftSeverity.Alert);
            logger.Log(alerts > 0 ? LogLevel.Warning : LogLevel.Information,
                "Drift feature sul modello '{Model}' ({Symbol} {Tf}): {Drift}/{Total} feature in drift ({Alerts} alert). Es.: {Examples}",
                model.Name, model.Symbol, model.Timeframe, drifting.Count, reports.Count, alerts,
                string.Join(", ", drifting.Take(5).Select(r => $"{r.FeatureName}[{r.Overall}]")));
            if (alerts > 0) metrics?.RecordDriftAlerts(model.Symbol, model.Timeframe, alerts);

            // Ciclo chiuso (Fase 2): un Champion in drift Alert va ritirato e il retrain accodato.
            // Solo governance dei record: nessun retrain automatico, nessun impatto sul Live.
            if (opt.RetireChampionOnAlert
                && model.Stage == ModelStage.Champion
                && alerts >= Math.Max(1, opt.MinAlertsToRetire))
            {
                var reason = $"drift: {alerts} feature in alert ({string.Join(", ", drifting.Where(r => r.Overall == DriftSeverity.Alert).Take(5).Select(r => r.FeatureName))})";
                await registry.RetireAsync(model.Id, reason, requestRetrain: true, ct);
                metrics?.RecordModelRetired(model.Symbol, model.Timeframe);
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
