using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Guida il trading engine alimentandolo con le candele. Quando l'engine viene avviato
/// (nuova sessione), riproduce progressivamente le ultime <c>ReplayDays</c> giornate di
/// dati storici (a piccoli batch per tick) così l'attività è osservabile in tempo reale
/// nella UI; una volta raggiunto il presente, elabora le nuove candele man mano che
/// arrivano dal MarketDataSyncWorker.
/// </summary>
public sealed class TradingWorker(
    ITradingEngine engine,
    IEnsembleManager ensemble,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<TradingWorker> logger) : BackgroundService
{
    private const int ReplayDays = 30;
    private const int BatchPerTick = 25;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    private DateTime? _sessionStart;
    private DateTime _cursor = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TradingWorker avviato (tick {Tick}).", Tick);
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await FeedAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "TradingWorker: errore nel feed."); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private async Task FeedAsync(CancellationToken ct)
    {
        var status = await engine.GetStatusAsync(ct);
        if (!status.IsRunning || status.IsEmergencyStopped)
        {
            _sessionStart = null; // reset: alla prossima partenza si riparte dal replay
            return;
        }

        // Nuova sessione di trading.
        if (status.StartedAtUtc != _sessionStart)
        {
            _sessionStart = status.StartedAtUtc;
            // Paper: replay osservabile delle ultime giornate. Testnet/Live: SOLO candele nuove
            // (niente replay, altrimenti si piazzerebbero ordini reali in massa sullo storico).
            _cursor = status.Mode == TradingMode.Paper ? DateTime.UtcNow.AddDays(-ReplayDays) : DateTime.UtcNow;
            logger.LogInformation("TradingWorker: nuova sessione {Mode}, cursore da {From:u}.", status.Mode, _cursor);
        }

        var cfg = await ensemble.GetConfigurationAsync(ct);
        List<OhlcvData> batch;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            batch = await db.OhlcvData
                .Where(c => c.Symbol == cfg.Symbol && c.Timeframe == cfg.Timeframe && c.TimestampUtc > _cursor)
                .OrderBy(c => c.TimestampUtc)
                .Take(BatchPerTick)
                .ToListAsync(ct);
        }

        foreach (var c in batch)
        {
            await engine.ProcessCandleAsync(c, ct);
            _cursor = c.TimestampUtc;
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
