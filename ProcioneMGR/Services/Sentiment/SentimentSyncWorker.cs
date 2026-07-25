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

        // Snapshot news per le feature ML: refresh anche quando la sync non inserisce nulla
        // (copre il primo tick post-riavvio, quando lo snapshot in-memory è vuoto ma il DB no).
        if (scope.ServiceProvider.GetService<ISentimentNewsProvider>() is { } newsProvider &&
            newsProvider.Snapshot.Count == 0)
        {
            await newsProvider.RefreshAsync(ct);
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
    /// Retention: notizie oltre NewsRetentionDays, metriche oltre MetricRetentionDays — ESCLUSE le
    /// serie che sono PATRIMONIO STORICO e non cache di sentiment:
    ///  - FearGreed: il baseline lungo (~2500 righe, un punto/giorno, cicli interi);
    ///  - FundingRate: il backfill profondo dal 2019 (T0.2) che alimenta il BACKTEST e il carry —
    ///    senza questa esenzione il primo tick dopo il backfill lo avrebbe raso a 30 giorni
    ///    (bug latente trovato il 2026-07-24 costruendo F4: il tool scriveva la storia,
    ///    il worker dell'app l'avrebbe cancellata);
    ///  - BinanceLiquidations (F4): il dato NON è ricostruibile a posteriori — l'accumulo è
    ///    l'intero valore della fonte.
    /// </summary>
    private static async Task PurgeAsync(IServiceProvider services, SentimentOptions opt, CancellationToken ct)
    {
        var dbFactory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var newsCutoff = DateTime.UtcNow.AddDays(-Math.Max(7, opt.NewsRetentionDays));
        await db.AltDataPoints.Where(a => a.TimestampUtc < newsCutoff).ExecuteDeleteAsync(ct);

        var metricCutoff = DateTime.UtcNow.AddDays(-Math.Max(30, opt.MetricRetentionDays));
        await db.SentimentMetricPoints
            .Where(p => p.TimestampUtc < metricCutoff
                        && p.Source != SentimentMetricSources.FearGreed
                        && p.Source != SentimentMetricSources.BinanceLiquidations
                        && p.Metric != SentimentMetrics.FundingRate)
            .ExecuteDeleteAsync(ct);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
