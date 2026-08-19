using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Tests.Infrastructure;

/// <summary>
/// [I6] Semina le candele recenti che il <c>FeatureDriftWorker</c> pretende prima di emettere un
/// verdetto.
///
/// <para>Serve perché dal 2026-08-18 il worker <b>dichiara SALTATO</b> un check che non ha abbastanza
/// candele, invece di produrre <c>Overall=None</c> — cioè il badge verde, indistinguibile da «ho
/// guardato e va tutto bene». Le fixture che montavano un monitor finto e nessuna candela stavano
/// esercitando il percorso di persistenza su un check che nella realtà non sarebbe mai avvenuto: la
/// correzione giusta è renderle realistiche, non indebolire la guardia.</para>
/// </summary>
public static class DriftTestData
{
    /// <summary>Il minimo legale del worker: <c>Math.Max(20, RecentCandles)</c>.</summary>
    public const int MinimumCandles = 20;

    /// <summary>
    /// Semina <paramref name="count"/> candele orarie consecutive che finiscono ADESSO, così la
    /// finestra recente non si sovrappone ad alcun periodo di training passato.
    /// </summary>
    public static async Task SeedRecentCandlesAsync(
        IDbContextFactory<ApplicationDbContext> factory,
        string symbol,
        string timeframe = "1h",
        int count = MinimumCandles,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var end = DateTime.UtcNow;
        for (var i = count; i > 0; i--)
        {
            var t = end.AddHours(-i);
            db.OhlcvData.Add(new OhlcvData
            {
                Symbol = symbol,
                Timeframe = timeframe,
                TimestampUtc = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc),
                Open = 100m + i,
                High = 101m + i,
                Low = 99m + i,
                Close = 100m + i,
                Volume = 10m + i,
            });
        }
        await db.SaveChangesAsync(ct);
    }
}
