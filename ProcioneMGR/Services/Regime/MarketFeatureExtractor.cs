using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Services.Regime;

/// <summary>
/// Implementazione del feature extractor.
///
/// ANTI-LOOK-AHEAD: per la candela all'indice i ogni feature usa SOLO valori a indice ≤ i
/// (rendimenti, finestre rolling, regressione su [i-49..i], ecc.). Nessuna feature legge
/// close[i+1] o dati futuri. La conseguenza verificabile: la feature alla candela i è
/// identica sia che si calcoli sull'intera serie sia su una serie troncata dopo i.
///
/// Le prime <c>Warmup</c> candele (dove la finestra più lunga, SMA/regressione a 50, non è
/// piena) vengono SCARTATE.
/// </summary>
public sealed class MarketFeatureExtractor(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ITechnicalIndicatorsService indicators) : IMarketFeatureExtractor
{
    private const int VolWindow = 20;
    private const int TrendWindow = 50;
    private const int VolumeWindow = 20;
    private const int AtrPeriod = 14;
    private const int RsiPeriod = 14;
    private const int RsiSmooth = 5;
    private const int HlWindow = 10;
    private const int MaWindow = 50;
    private const int Warmup = TrendWindow; // 50: la finestra più lunga

    public async Task<List<MarketFeatures>> ExtractFeaturesAsync(
        string exchangeName, string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);

        List<OhlcvData> candles;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            candles = await db.OhlcvData
                .Where(c => c.Symbol == symbol && c.Timeframe == timeframe && c.TimestampUtc >= fromUtc && c.TimestampUtc <= toUtc)
                .OrderBy(c => c.TimestampUtc)
                .ToListAsync(ct);
        }

        return ComputeFeatures(candles, timeframe, ct);
    }

    /// <summary>Calcolo puro (testabile) su una lista di candele già ordinata cronologicamente.</summary>
    internal List<MarketFeatures> ComputeFeatures(IReadOnlyList<OhlcvData> candles, string timeframe, CancellationToken ct = default)
    {
        var n = candles.Count;
        var result = new List<MarketFeatures>();
        if (n <= Warmup)
        {
            return result;
        }

        var close = new decimal[n];
        var high = new decimal[n];
        var low = new decimal[n];
        var vol = new decimal[n];
        for (var i = 0; i < n; i++)
        {
            close[i] = candles[i].Close;
            high[i] = candles[i].High;
            low[i] = candles[i].Low;
            vol[i] = candles[i].Volume;
        }

        // Rendimenti periodici.
        var ret = new decimal[n];
        for (var i = 1; i < n; i++)
        {
            ret[i] = close[i - 1] > 0m ? (close[i] - close[i - 1]) / close[i - 1] : 0m;
        }

        // ATR(14) con smoothing di Wilder.
        var atr = WilderAtr(high, low, close, AtrPeriod);

        // RSI(14) via servizio indicatori (già anti-look-ahead).
        var rsi = indicators.CalculateRsiAsync([.. close], RsiPeriod, ct).GetAwaiter().GetResult();

        var sqrtPpy = Statistics.Sqrt(Statistics.PeriodsPerYear(timeframe));

        for (var i = Warmup; i < n; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();

            var f = new MarketFeatures
            {
                Timestamp = DateTime.SpecifyKind(candles[i].TimestampUtc, DateTimeKind.Utc),
                Price = close[i],
                Volatility = StdDev(ret, i - VolWindow + 1, i) * sqrtPpy,
                VolumeRatio = SafeDiv(vol[i], Mean(vol, i - VolumeWindow + 1, i)),
                AtrNormalized = SafeDiv(atr[i], close[i]),
                RsiLevel = MeanNullable(rsi, i - RsiSmooth + 1, i),
                HighLowRange = MeanHlRange(high, low, close, i - HlWindow + 1, i),
            };

            // Regressione lineare su [i-49..i] -> slope.
            var slope = LinregSlope(close, i - TrendWindow + 1, i);
            var avgClose = Mean(close, i - TrendWindow + 1, i);
            f.TrendStrength = avgClose > 0m ? Math.Abs(slope) / avgClose : 0m;
            f.TrendDirection = f.TrendStrength < 0.0000001m ? 0m : Math.Sign(slope);

            // Distanza dalla SMA50.
            var sma50 = Mean(close, i - MaWindow + 1, i);
            f.DistanceFromMa = sma50 > 0m ? (close[i] - sma50) / sma50 : 0m;

            result.Add(f);
        }

        return result;
    }

    // ---------------------------------------------------------------- helpers (tutti su [start..end] inclusi, indici ≤ i)

    private static decimal Mean(decimal[] a, int start, int end)
    {
        if (start < 0) start = 0;
        decimal sum = 0m;
        var count = 0;
        for (var i = start; i <= end; i++) { sum += a[i]; count++; }
        return count == 0 ? 0m : sum / count;
    }

    private static decimal StdDev(decimal[] a, int start, int end)
    {
        if (start < 0) start = 0;
        var mean = Mean(a, start, end);
        decimal sumSq = 0m;
        var count = 0;
        for (var i = start; i <= end; i++) { var d = a[i] - mean; sumSq += d * d; count++; }
        if (count == 0) return 0m;
        return Statistics.Sqrt(sumSq / count);
    }

    private static decimal MeanNullable(IReadOnlyList<decimal?> a, int start, int end)
    {
        if (start < 0) start = 0;
        decimal sum = 0m;
        var count = 0;
        for (var i = start; i <= end; i++)
        {
            if (a[i].HasValue) { sum += a[i]!.Value; count++; }
        }
        return count == 0 ? 0m : sum / count;
    }

    private static decimal MeanHlRange(decimal[] high, decimal[] low, decimal[] close, int start, int end)
    {
        if (start < 0) start = 0;
        decimal sum = 0m;
        var count = 0;
        for (var i = start; i <= end; i++)
        {
            if (close[i] > 0m) { sum += (high[i] - low[i]) / close[i]; count++; }
        }
        return count == 0 ? 0m : sum / count;
    }

    private static decimal SafeDiv(decimal num, decimal den) => den == 0m ? 0m : num / den;

    /// <summary>Slope OLS dei valori su [start..end] con x = 0..k.</summary>
    private static decimal LinregSlope(decimal[] y, int start, int end)
    {
        if (start < 0) start = 0;
        var nLocal = end - start + 1;
        if (nLocal < 2) return 0m;

        // x = 0,1,...,nLocal-1
        decimal sumX = 0m, sumY = 0m, sumXY = 0m, sumXX = 0m;
        for (var k = 0; k < nLocal; k++)
        {
            decimal x = k;
            var yv = y[start + k];
            sumX += x; sumY += yv; sumXY += x * yv; sumXX += x * x;
        }
        var denom = nLocal * sumXX - sumX * sumX;
        return denom == 0m ? 0m : (nLocal * sumXY - sumX * sumY) / denom;
    }

    private static decimal[] WilderAtr(decimal[] high, decimal[] low, decimal[] close, int period)
    {
        var n = high.Length;
        var atr = new decimal[n];
        if (n == 0) return atr;

        var tr = new decimal[n];
        tr[0] = high[0] - low[0];
        for (var i = 1; i < n; i++)
        {
            var hl = high[i] - low[i];
            var hc = Math.Abs(high[i] - close[i - 1]);
            var lc = Math.Abs(low[i] - close[i - 1]);
            tr[i] = Math.Max(hl, Math.Max(hc, lc));
        }

        if (n <= period) return atr;

        // Seed: media dei primi `period` TR (indici 1..period).
        decimal seed = 0m;
        for (var i = 1; i <= period; i++) seed += tr[i];
        atr[period] = seed / period;
        for (var i = period + 1; i < n; i++)
        {
            atr[i] = (atr[i - 1] * (period - 1) + tr[i]) / period;
        }
        return atr;
    }
}
