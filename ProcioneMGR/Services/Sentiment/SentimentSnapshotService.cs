using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Sentiment.Metrics;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>Ultimo snapshot calcolato, per UI/prompt/pipeline senza ricomputo. Singleton.</summary>
public sealed class SentimentSnapshotCache
{
    private volatile SentimentSnapshot? _current;
    public SentimentSnapshot? Current => _current;
    public void Set(SentimentSnapshot snapshot) => _current = snapshot;
}

public interface ISentimentSnapshotService
{
    /// <summary>
    /// Carica metriche (finestra baseline) e news (24h) dal DB, calcola lo snapshot col
    /// calcolatore puro e aggiorna la cache. Null se non c'è alcun dato (mai un finto "neutro").
    /// </summary>
    Task<SentimentSnapshot?> ComputeAsync(CancellationToken ct);
}

/// <inheritdoc cref="ISentimentSnapshotService"/>
public sealed class SentimentSnapshotService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptionsMonitor<SentimentOptions> options,
    SentimentSnapshotCache cache,
    ILogger<SentimentSnapshotService> logger) : ISentimentSnapshotService
{
    public async Task<SentimentSnapshot?> ComputeAsync(CancellationToken ct)
    {
        var opt = options.CurrentValue;
        var now = DateTime.UtcNow;
        var baselineFrom = now.AddDays(-Math.Max(1, opt.BaselineDays));
        var newsFrom = now.AddHours(-24);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var metrics = await db.SentimentMetricPoints.AsNoTracking()
            .Where(p => p.TimestampUtc >= baselineFrom)
            .ToListAsync(ct);

        var news = await db.AltDataPoints.AsNoTracking()
            .Where(a => a.TimestampUtc >= newsFrom && a.SentimentScore != null)
            .Select(a => new { a.SentimentScore, a.SymbolsJson })
            .ToListAsync(ct);

        if (metrics.Count == 0 && news.Count == 0)
        {
            logger.LogDebug("Snapshot sentiment non calcolato: nessun dato (né metriche né news).");
            return null;
        }

        var baseSymbols = opt.Symbols.Select(BinanceFuturesSentimentClient.ToBaseTicker).Distinct().ToList();

        // Media 24h per simbolo (dal SymbolsJson delle news) e di mercato (tutte le news scorate).
        var perSymbol = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in baseSymbols)
        {
            var scores = news
                .Where(n => ContainsSymbol(n.SymbolsJson, symbol))
                .Select(n => (double)n.SentimentScore!.Value)
                .ToList();
            if (scores.Count > 0) perSymbol[symbol] = scores.Average();
        }
        double? marketNews = news.Count > 0 ? news.Average(n => (double)n.SentimentScore!.Value) : null;

        var snapshot = SentimentCompositeCalculator.Compute(opt, now, metrics, perSymbol, marketNews, baseSymbols);
        cache.Set(snapshot);
        return snapshot;
    }

    private static bool ContainsSymbol(string symbolsJson, string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbolsJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(symbolsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array &&
                   doc.RootElement.EnumerateArray().Any(e => string.Equals(e.GetString(), symbol, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false; // JSON malformato in una riga storica: la si ignora
        }
    }
}
