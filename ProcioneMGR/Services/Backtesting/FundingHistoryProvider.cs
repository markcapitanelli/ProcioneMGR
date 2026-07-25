using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>Carica la serie storica dei funding rate per un simbolo, pronta per <see cref="BacktestConfiguration.FundingHistory"/>.</summary>
public interface IFundingHistoryProvider
{
    /// <param name="symbol">Formato piattaforma, es. "BTC/USDT": il ticker base viene estratto qui.</param>
    Task<List<FundingRatePoint>> GetAsync(string symbol, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}

/// <summary>
/// [T0.2 roadmap macchina-ricerca] Legge i funding storici da <see cref="SentimentMetricPoint"/>
/// (Metric = "FundingRate", già in percento ×100, firmati) — la serie che il sync del sentiment
/// raccoglie e che finora nessun motore consumava.
///
/// La finestra parte 8 ORE PRIMA di <paramref name="fromUtc"/>: il lookup del motore è a gradini
/// (ultimo evento ≤ ts) e senza quel margine le prime candele del backtest cadrebbero prima del
/// primo evento in finestra, degradando inutilmente alla costante.
/// </summary>
public sealed class FundingHistoryProvider(IDbContextFactory<ApplicationDbContext> dbFactory) : IFundingHistoryProvider
{
    public async Task<List<FundingRatePoint>> GetAsync(string symbol, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var baseTicker = ToBaseTicker(symbol);
        var from = fromUtc.AddHours(-8);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SentimentMetricPoints.AsNoTracking()
            .Where(p => p.Metric == SentimentMetrics.FundingRate
                        && p.Symbol == baseTicker
                        && p.TimestampUtc >= from
                        && p.TimestampUtc <= toUtc)
            .OrderBy(p => p.TimestampUtc)
            .Select(p => new FundingRatePoint(p.TimestampUtc, p.Value))
            .ToListAsync(ct);
    }

    /// <summary>"BTC/USDT" → "BTC" (stessa convenzione del sync sentiment).</summary>
    internal static string ToBaseTicker(string symbol)
    {
        var slash = symbol.IndexOf('/');
        return (slash > 0 ? symbol[..slash] : symbol).ToUpperInvariant();
    }
}
