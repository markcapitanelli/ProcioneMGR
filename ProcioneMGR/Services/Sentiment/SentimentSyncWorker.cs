using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.AltData;
using ProcioneMGR.Services.Sentiment.Metrics;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// Worker di Sentiment 2.0: raccoglie le serie di market mood (ogni tick), sincronizza le notizie
/// (a cadenza più lenta — supera il vecchio "solo on-demand" di /sentiment), ricalcola lo snapshot
/// composite e applica la retention. Default ON (a differenza delle automazioni decisionali):
/// sole GET pubbliche keyless a cadenza modesta, e le serie derivate di Binance esistono SOLO per
/// 30 giorni — un worker spento significa buchi irrecuperabili nei baseline degli z-score.
/// <c>Enabled</c> è per-tick (hot da /admin/autonomy); gli intervalli sono letti al boot.
/// I fallimenti delle fonti restano log + salute in UI: niente Telegram (non azionabili).
/// </summary>
public sealed class SentimentSyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<SentimentOptions> options,
    ILogger<SentimentSyncWorker> logger) : BackgroundService
{
    private DateTime _lastNewsSyncUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(5, options.CurrentValue.MetricsIntervalMinutes));
        logger.LogInformation("SentimentSyncWorker avviato (metriche ogni {Interval}, news ogni {News} min, Enabled={Enabled}).",
            interval, options.CurrentValue.NewsIntervalMinutes, options.CurrentValue.Enabled);

        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
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
            catch (Exception ex) { logger.LogError(ex, "Ciclo SentimentSyncWorker fallito; ritento al prossimo tick."); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("SentimentSyncWorker fermato.");
    }

    /// <summary>
    /// Un tick completo: metriche sempre, news solo se è passato l'intervallo dedicato, snapshot,
    /// retention. Pubblico per i test e per "Esegui ora" dalla UI (che forza anche le news).
    /// </summary>
    public async Task<(int Metrics, int News)> TickAsync(CancellationToken ct, bool forceNews = false)
    {
        var opt = options.CurrentValue;
        await using var scope = scopeFactory.CreateAsyncScope();

        var metricsInserted = await scope.ServiceProvider
            .GetRequiredService<ISentimentMetricSyncService>()
            .SyncAllAsync(ct);

        var newsInserted = 0;
        var newsDue = forceNews || DateTime.UtcNow - _lastNewsSyncUtc >= TimeSpan.FromMinutes(Math.Max(5, opt.NewsIntervalMinutes));
        if (newsDue)
        {
            newsInserted = await scope.ServiceProvider
                .GetRequiredService<IAltDataSyncService>()
                .SyncAllAsync(ct);
            _lastNewsSyncUtc = DateTime.UtcNow;
        }

        await scope.ServiceProvider
            .GetRequiredService<ISentimentSnapshotService>()
            .ComputeAsync(ct);

        await PurgeAsync(scope.ServiceProvider, opt, ct);

        logger.LogInformation("Sentiment tick: {Metrics} punti metrici nuovi, {News} notizie nuove (news sync: {Due}).",
            metricsInserted, newsInserted, newsDue);
        return (metricsInserted, newsInserted);
    }

    /// <summary>
    /// Retention: notizie oltre NewsRetentionDays, metriche oltre MetricRetentionDays — ESCLUSA la
    /// fonte FearGreed, che è il baseline lungo (~2500 righe totali, un punto al giorno: tenerla
    /// tutta costa nulla e conserva i cicli interi per i confronti storici).
    /// </summary>
    private static async Task PurgeAsync(IServiceProvider services, SentimentOptions opt, CancellationToken ct)
    {
        var dbFactory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var newsCutoff = DateTime.UtcNow.AddDays(-Math.Max(7, opt.NewsRetentionDays));
        await db.AltDataPoints.Where(a => a.TimestampUtc < newsCutoff).ExecuteDeleteAsync(ct);

        var metricCutoff = DateTime.UtcNow.AddDays(-Math.Max(30, opt.MetricRetentionDays));
        await db.SentimentMetricPoints
            .Where(p => p.TimestampUtc < metricCutoff && p.Source != SentimentMetricSources.FearGreed)
            .ExecuteDeleteAsync(ct);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
