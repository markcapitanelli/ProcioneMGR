using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// Implementazione della sincronizzazione incrementale delle serie tracciate.
/// </summary>
public sealed class MarketDataSyncService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOhlcvIngestionService ingestion,
    IConfiguration configuration,
    ILogger<MarketDataSyncService> logger) : IMarketDataSyncService
{
    public async Task<int> SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var series = await db.TrackedSeries.FirstOrDefaultAsync(s => s.Id == trackedSeriesId, ct);
        if (series is null)
        {
            return 0;
        }

        // Cursore incrementale: riparti dall'ultima candela salvata (la ri-scarichiamo
        // perche' potrebbe essere stata ancora "aperta"); se non c'e' nulla, backfill.
        var lastStored = await db.OhlcvData
            .Where(c => c.Symbol == series.Symbol && c.Timeframe == series.Timeframe)
            .MaxAsync(c => (DateTime?)c.TimestampUtc, ct);

        var backfillDays = configuration.GetValue("MarketData:DefaultBackfillDays", 7);
        var from = lastStored ?? DateTime.UtcNow.AddDays(-backfillDays);
        var to = DateTime.UtcNow;

        try
        {
            var result = await ingestion.IngestHistoricalDataAsync(
                series.Exchange.ToString(), series.Symbol, series.Timeframe, from, to, progress: null, ct);

            series.LastSyncUtc = DateTime.UtcNow;
            series.LastSyncStatus = $"OK: {result.CandlesProcessed} candele";
            await db.SaveChangesAsync(ct);
            return (int)result.CandlesProcessed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync fallita per {Symbol} {Timeframe} su {Exchange}.",
                series.Symbol, series.Timeframe, series.Exchange);
            series.LastSyncUtc = DateTime.UtcNow;
            series.LastSyncStatus = $"Errore: {Trunc(ex.Message)}";
            await db.SaveChangesAsync(CancellationToken.None);
            return 0;
        }
    }

    public async Task SyncAllEnabledAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.TrackedSeries
            .Where(s => s.Enabled)
            .Select(s => s.Id)
            .ToListAsync(ct);

        logger.LogInformation("Sync ciclo: {Count} serie abilitate.", ids.Count);

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            // SyncSeriesAsync e' gia' resiliente agli errori della singola serie.
            await SyncSeriesAsync(id, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    private static string Trunc(string s) => s.Length <= 200 ? s : s[..200];
}
