using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Observability;

namespace ProcioneMGR.Services.Sentiment.Metrics;

public interface ISentimentMetricSyncService
{
    /// <summary>Interroga tutte le fonti di metriche e salva i punti nuovi. Restituisce quanti ne ha inseriti.</summary>
    Task<int> SyncAllAsync(CancellationToken ct);
}

/// <summary>
/// Orchestratore delle fonti di metriche sentiment — specchia <c>AltDataSyncService</c>: fetch
/// paralleli, una fonte che fallisce viene saltata con warning + health rossa (mai far fallire il
/// batch), dedupe applicativa sulla chiave (Source, Metric, Symbol, TimestampUtc) con l'indice
/// unico come backstop. Le fonti <see cref="IBackfillableMetricSource"/> (Fear &amp; Greed) fanno
/// il backfill dell'INTERO storico la prima volta (zero righe per quella Source), poi solo gli
/// ultimi punti.
/// </summary>
public sealed class SentimentMetricSyncService(
    IEnumerable<ISentimentMetricSource> sources,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<SentimentMetricSyncService> logger,
    SentimentSourceHealthRegistry? health = null,
    ProcioneMetrics? metrics = null) : ISentimentMetricSyncService
{
    public async Task<int> SyncAllAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var fetches = sources.Select(source => FetchSafeAsync(db, source, ct));
        var results = await Task.WhenAll(fetches);

        var inserted = 0;
        foreach (var (source, samples) in results)
        {
            if (samples is null || samples.Count == 0)
            {
                if (samples is not null) ReportOk(source.Name, 0);
                continue;
            }

            // Dedupe applicativa: chiavi già presenti nella finestra temporale del batch di questa
            // fonte (le sync si sovrappongono di proposito: limit=48 ogni 30 min).
            var minTs = samples.Min(s => s.TimestampUtc);
            var existing = (await db.SentimentMetricPoints
                    .Where(p => p.Source == source.Name && p.TimestampUtc >= minTs)
                    .Select(p => new { p.Metric, p.Symbol, p.TimestampUtc })
                    .ToListAsync(ct))
                .Select(p => (p.Metric, p.Symbol, p.TimestampUtc))
                .ToHashSet();

            var freshCount = 0;
            foreach (var s in samples)
            {
                if (!existing.Add((s.Metric, s.Symbol, s.TimestampUtc)))
                {
                    continue; // già in DB o duplicato nello stesso batch
                }
                db.SentimentMetricPoints.Add(new SentimentMetricPoint
                {
                    TimestampUtc = s.TimestampUtc,
                    Source = source.Name,
                    Metric = s.Metric,
                    Symbol = s.Symbol,
                    Value = s.Value,
                });
                freshCount++;
            }

            if (freshCount > 0)
            {
                await db.SaveChangesAsync(ct);
            }
            inserted += freshCount;
            ReportOk(source.Name, freshCount);
        }
        return inserted;
    }

    private async Task<(ISentimentMetricSource Source, IReadOnlyList<SentimentMetricSample>? Samples)> FetchSafeAsync(
        ApplicationDbContext db, ISentimentMetricSource source, CancellationToken ct)
    {
        try
        {
            // Backfill una tantum: se la fonte sa dare l'intero storico e in tabella non c'è nulla
            // di suo, la prima sync scarica tutto (es. ~2500 punti giornalieri di Fear & Greed).
            if (source is IBackfillableMetricSource backfillable &&
                !await db.SentimentMetricPoints.AnyAsync(p => p.Source == source.Name, ct))
            {
                logger.LogInformation("Sentiment metrics: backfill iniziale della fonte '{Source}'.", source.Name);
                return (source, await backfillable.FetchFullHistoryAsync(ct));
            }
            return (source, await source.FetchLatestAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sentiment metrics: fonte '{Source}' non raggiungibile, salto.", source.Name);
            health?.ReportError(source.Name, FirstLine(ex.Message));
            metrics?.RecordSentimentSync(source.Name, "error");
            return (source, null);
        }
    }

    private void ReportOk(string sourceName, int count)
    {
        health?.ReportSuccess(sourceName, count);
        metrics?.RecordSentimentSync(sourceName, "ok");
    }

    private static string FirstLine(string message)
    {
        var idx = message.IndexOfAny(['\r', '\n']);
        return idx < 0 ? message : message[..idx];
    }
}
