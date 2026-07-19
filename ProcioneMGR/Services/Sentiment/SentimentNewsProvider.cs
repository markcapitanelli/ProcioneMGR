using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// Fornisce alle feature ML lo snapshot in-memory delle notizie scorate (l'input che
/// <see cref="SentimentAlphaFactor"/> richiede come dipendenza esterna). Singleton con snapshot
/// volatile: <c>Compute</c> dei fattori resta sincrono e senza I/O; il refresh avviene dopo ogni
/// sync delle news (worker, stage pipeline, bottone UI — tutti passano da AltDataSyncService).
/// </summary>
public interface ISentimentNewsProvider
{
    IReadOnlyList<ScoredNewsItem> Snapshot { get; }

    /// <summary>Ricarica lo snapshot dal DB (finestra = retention news).</summary>
    Task RefreshAsync(CancellationToken ct);
}

/// <inheritdoc cref="ISentimentNewsProvider"/>
public sealed class SentimentNewsProvider(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptionsMonitor<SentimentOptions> options,
    ILogger<SentimentNewsProvider> logger) : ISentimentNewsProvider
{
    private volatile IReadOnlyList<ScoredNewsItem> _snapshot = [];

    public IReadOnlyList<ScoredNewsItem> Snapshot => _snapshot;

    public async Task RefreshAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Max(7, options.CurrentValue.NewsRetentionDays));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AltDataPoints.AsNoTracking()
            .Where(a => a.TimestampUtc >= since && a.SentimentScore != null)
            .OrderBy(a => a.TimestampUtc)
            .Select(a => new { a.TimestampUtc, a.SentimentScore, a.SymbolsJson })
            .ToListAsync(ct);

        _snapshot = rows.Select(r => new ScoredNewsItem(
                DateTime.SpecifyKind(r.TimestampUtc, DateTimeKind.Utc),
                r.SentimentScore!.Value,
                ParseSymbols(r.SymbolsJson)))
            .ToList();
        logger.LogDebug("SentimentNewsProvider: snapshot aggiornato ({Count} notizie).", rows.Count);
    }

    private static IReadOnlyList<string> ParseSymbols(string symbolsJson)
    {
        if (string.IsNullOrWhiteSpace(symbolsJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(symbolsJson) ?? [];
        }
        catch
        {
            return []; // riga storica malformata: nessun filtro simbolo per quella notizia
        }
    }
}
