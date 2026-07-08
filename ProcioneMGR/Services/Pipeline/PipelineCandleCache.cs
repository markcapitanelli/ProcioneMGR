using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Per-run candle cache: every (symbol, timeframe, from, to) window is loaded from the DB at
/// most once. Instantiated fresh for each run by the engine — the cache lifetime IS the run
/// lifetime, so a resumed run rereads current DB data (which is what we want: candles are the
/// source of truth, not part of the checkpoint).
/// </summary>
public sealed class PipelineCandleCache(IDbContextFactory<ApplicationDbContext> dbFactory) : IPipelineCandleCache
{
    private readonly Dictionary<string, IReadOnlyList<OhlcvData>> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<OhlcvData>> GetAsync(string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct)
    {
        var key = $"{symbol}|{timeframe}|{from:O}|{to:O}";
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var candles = await db.OhlcvData.AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Timeframe == timeframe && c.TimestampUtc >= from && c.TimestampUtc <= to)
                .OrderBy(c => c.TimestampUtc)
                .ToListAsync(ct);
            _cache[key] = candles;
            return candles;
        }
        finally { _gate.Release(); }
    }
}
