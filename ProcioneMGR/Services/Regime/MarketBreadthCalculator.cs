using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Regime;

/// <summary>
/// [3.8a/4.9] Breadth interna del "mercato" che la piattaforma già possiede: a ogni barra, la
/// frazione dei simboli /USDT tracciati la cui chiusura sta sopra la PROPRIA SMA50. È l'indicatore
/// classico di partecipazione: un rally con breadth 0,9 è mosso da tutto il listino, uno con 0,4 da
/// due titani — regimi diversi che le feature per-simbolo non distinguono.
/// </summary>
public interface IMarketBreadthCalculator
{
    /// <summary>
    /// Breadth (0..1) per ogni timestamp del timeframe nel range richiesto. CAUSALE: la SMA50 di un
    /// simbolo a t usa solo le sue 50 chiusure fino a t. I simboli senza SMA disponibile a t non
    /// contano né sopra né sotto (denominatore ridotto). Vuoto se nessun simbolo ha dati.
    /// </summary>
    Task<Dictionary<DateTime, decimal>> ComputeAsync(string timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}

public sealed class MarketBreadthCalculator(IDbContextFactory<ApplicationDbContext> dbFactory) : IMarketBreadthCalculator
{
    private const int SmaPeriod = 50;

    public async Task<Dictionary<DateTime, decimal>> ComputeAsync(
        string timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        // Warm-up: servono 50 barre PRIMA di fromUtc perché la breadth alla prima barra sia già
        // sensata. Si stima all'indietro col passo del timeframe (approssimazione larga: 80 barre).
        var step = Exchanges.Timeframes.Supported.TryGetValue(timeframe, out var ts) ? ts : TimeSpan.FromHours(1);
        var warmupFrom = fromUtc - step * (SmaPeriod + 30);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.OhlcvData
            .Where(c => c.Timeframe == timeframe
                        && c.Symbol.EndsWith("/USDT")
                        && c.TimestampUtc >= warmupFrom && c.TimestampUtc <= toUtc)
            .Select(c => new { c.Symbol, c.TimestampUtc, c.Close })
            .OrderBy(c => c.Symbol).ThenBy(c => c.TimestampUtc)
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        // Per simbolo: SMA50 rolling causale, poi voto sopra/sotto per timestamp.
        var above = new Dictionary<DateTime, int>();
        var total = new Dictionary<DateTime, int>();
        foreach (var group in rows.GroupBy(r => r.Symbol))
        {
            var closes = group.ToList(); // già ordinate per timestamp
            decimal rollingSum = 0m;
            var window = new Queue<decimal>(SmaPeriod);
            foreach (var row in closes)
            {
                ct.ThrowIfCancellationRequested();
                window.Enqueue(row.Close);
                rollingSum += row.Close;
                if (window.Count > SmaPeriod) rollingSum -= window.Dequeue();
                if (window.Count < SmaPeriod || row.TimestampUtc < fromUtc) continue;

                var sma = rollingSum / SmaPeriod;
                total[row.TimestampUtc] = total.GetValueOrDefault(row.TimestampUtc) + 1;
                if (row.Close > sma)
                {
                    above[row.TimestampUtc] = above.GetValueOrDefault(row.TimestampUtc) + 1;
                }
            }
        }

        var result = new Dictionary<DateTime, decimal>(total.Count);
        foreach (var (tsKey, count) in total)
        {
            result[tsKey] = count > 0 ? (decimal)above.GetValueOrDefault(tsKey) / count : 0.5m;
        }
        return result;
    }
}
