using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Sentiment;

namespace ProcioneMGR.Services.AltData;

public interface IAltDataSyncService
{
    /// <summary>Interroga tutte le fonti registrate, classifica/scora le notizie nuove e le salva. Restituisce quante ne ha inserite.</summary>
    Task<int> SyncAllAsync(CancellationToken ct);
}

/// <summary>
/// Implementazione di <see cref="IAltDataSyncService"/>. Deduplica per Source+Url (o Source+Title
/// se una fonte non fornisce un link), tollera fonti temporaneamente irraggiungibili (le salta
/// con un warning, non fa fallire l'intera sync — stesso spirito resiliente di
/// <c>MarketDataSyncService</c> per l'OHLCV).
/// </summary>
public sealed class AltDataSyncService(
    IEnumerable<IAltDataSource> sources,
    ISentimentScorer scorer,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<AltDataSyncService> logger,
    SentimentSourceHealthRegistry? health = null) : IAltDataSyncService
{
    public async Task<int> SyncAllAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existingKeys = (await db.AltDataPoints.Select(a => a.DedupeKey).ToListAsync(ct)).ToHashSet();

        // Le fetch HTTP sono indipendenti fra loro (I/O-bound): eseguirle in parallelo evita che
        // una fonte lenta/irraggiungibile ritardi in sequenza tutte le altre.
        var fetches = sources.Select(source => FetchSafeAsync(source, ct));
        var results = await Task.WhenAll(fetches);

        var inserted = 0;
        foreach (var (source, items) in results)
        {
            if (items is null) continue;

            var freshFromSource = 0;
            foreach (var item in items)
            {
                var dedupeKey = $"{source.Name}:{item.Url ?? item.Title}";
                if (!existingKeys.Add(dedupeKey))
                {
                    continue; // già presente (in DB o in questo stesso batch, fra fonti diverse)
                }

                // Le fonti strutturali (calendario economico, sentiment retail) forniscono i
                // propri override: non sono testo libero da classificare/scorare col lessico.
                var category = item.CategoryOverride ?? NewsImpactClassifier.Classify(item.Title, item.Summary);
                var symbols = item.SymbolsOverride ?? NewsImpactClassifier.DetectSymbols(item.Title, item.Summary);
                var sentiment = item.SentimentScoreOverride ?? scorer.Score(item.Title, item.Summary);

                db.AltDataPoints.Add(new AltDataPoint
                {
                    TimestampUtc = item.PublishedUtc,
                    Source = source.Name,
                    Title = item.Title,
                    Summary = item.Summary,
                    Url = item.Url,
                    Category = category.ToString(),
                    SymbolsJson = JsonSerializer.Serialize(symbols),
                    SentimentScore = sentiment,
                    DedupeKey = dedupeKey,
                });
                inserted++;
                freshFromSource++;
            }
            health?.ReportSuccess(source.Name, freshFromSource);
        }

        if (inserted > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        return inserted;
    }

    private async Task<(IAltDataSource Source, IReadOnlyList<RawNewsItem>? Items)> FetchSafeAsync(IAltDataSource source, CancellationToken ct)
    {
        try
        {
            return (source, await source.FetchLatestAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AltData sync: fonte '{Source}' non raggiungibile, salto.", source.Name);
            health?.ReportError(source.Name, ex.Message);
            return (source, null);
        }
    }
}
