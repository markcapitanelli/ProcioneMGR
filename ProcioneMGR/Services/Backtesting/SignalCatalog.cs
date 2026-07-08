using System.Runtime.CompilerServices;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// Catalog of ELEMENTARY SIGNALS for composable strategies, all normalized to a COMMON 0-100
/// scale so that thresholds are comparable across signals (a "&lt; 20" means "in the bottom
/// quintile of its own recent history" for every unbounded signal, and the native scale for
/// oscillators that already live in 0-100):
///
///   0 Rsi              — RSI(14), native 0-100
///   1 StochD           — Stochastic %D(14,3), native 0-100
///   2 BollingerB       — %B(20,2) × 100 (position inside the bands; can exceed 0-100 slightly)
///   3 SupertrendDir    — 100 when the Supertrend(10,3) trend is up, 0 when down
///   4 VolumeRatioPct   — causal rolling percentile of volume/SMA20(volume)
///   5 VwapDevPct       — causal rolling percentile of the deviation from the UTC-session VWAP
///   6 MomentumPct      — causal rolling percentile of the 10-bar rate of change
///   7 MacdHistPct      — causal rolling percentile of the MACD(12,26,9) histogram
///   8 DistFromSmaPct   — causal rolling percentile of (close - SMA50)/SMA50
///
/// ANTI-LOOK-AHEAD: every underlying series is causal (TechnicalIndicatorsService) and the
/// percentile normalization ranks value[i] ONLY against the previous <see cref="PercentileWindow"/>
/// values (inclusive of i) — the value at bar i is identical on the full series and on a series
/// truncated right after i.
///
/// CACHING: computing the full matrix is O(n·window) per percentile signal; strategies created
/// per-combo by the optimizer would recompute it thousands of times on the SAME candle list.
/// A <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed on the candle LIST INSTANCE gives
/// automatic reuse across all strategies/combos sharing the cached list (the optimizer and the
/// pipeline both reuse one list per series) and GC-safe eviction when the list dies.
/// </summary>
public static class SignalCatalog
{
    public const int SignalCount = 9;
    public const int PercentileWindow = 250;

    /// <summary>Display names, index-aligned to the signal ids (for UI/log readability).</summary>
    public static readonly IReadOnlyList<string> SignalNames =
    [
        "RSI", "Stoch %D", "Bollinger %B", "Supertrend dir",
        "Volume ratio pct", "VWAP dev pct", "Momentum pct", "MACD hist pct", "Dist SMA50 pct",
    ];

    private static readonly ConditionalWeakTable<object, Task<decimal?[][]>> Cache = new();
    private static readonly object Gate = new();

    /// <summary>
    /// Normalized signal matrix for the series: <c>matrix[signalId][barIndex]</c>, null during
    /// each signal's warm-up. Cached per candle-list instance (thread-safe, computed once).
    /// </summary>
    public static Task<decimal?[][]> GetMatrixAsync(
        IReadOnlyList<OhlcvData> candles, ITechnicalIndicatorsService indicators, CancellationToken ct)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(candles, out var cached))
            {
                return cached;
            }
            var task = ComputeMatrixAsync(candles, indicators, ct);
            Cache.Add(candles, task);
            return task;
        }
    }

    private static async Task<decimal?[][]> ComputeMatrixAsync(
        IReadOnlyList<OhlcvData> candles, ITechnicalIndicatorsService indicators, CancellationToken ct)
    {
        var n = candles.Count;
        var closes = new List<decimal>(n);
        var highs = new List<decimal>(n);
        var lows = new List<decimal>(n);
        var volumes = new List<decimal>(n);
        foreach (var c in candles)
        {
            closes.Add(c.Close);
            highs.Add(c.High);
            lows.Add(c.Low);
            volumes.Add(c.Volume);
        }

        var matrix = new decimal?[SignalCount][];

        // 0) RSI(14) — native 0-100.
        matrix[0] = [.. await indicators.CalculateRsiAsync(closes, 14, ct)];

        // 1) Stochastic %D(14,3): %K from Donchian HHV/LLV, %D = SMA(%K,3) (same construction
        //    as StochasticStrategy, kept causal).
        var (hhv, llv) = await indicators.CalculateDonchianAsync(highs, lows, 14, ct);
        var stochK = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            if (hhv[i] is decimal hi && llv[i] is decimal lo && hi > lo)
            {
                stochK[i] = 100m * (closes[i] - lo) / (hi - lo);
            }
        }
        matrix[1] = await RemapSmaAsync(stochK, 3, indicators, ct);

        // 2) Bollinger %B(20,2) × 100.
        var (upper, _, lower) = await indicators.CalculateBollingerAsync(closes, 20, 2m, ct);
        var pctB = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            if (upper[i] is decimal u && lower[i] is decimal l && u > l)
            {
                pctB[i] = 100m * (closes[i] - l) / (u - l);
            }
        }
        matrix[2] = pctB;

        // 3) Supertrend(10,3) direction: 100 = up, 0 = down (same band-locking logic as
        //    SupertrendStrategy, recomputed here to keep the strategy classes independent).
        matrix[3] = await ComputeSupertrendDirAsync(candles, closes, highs, lows, indicators, ct);

        // 4) Volume ratio percentile: volume / SMA20(volume), then causal percentile.
        var volSma = await indicators.CalculateSmaAsync(volumes, 20, ct);
        var volRatio = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            if (volSma[i] is decimal vs && vs > 0m)
            {
                volRatio[i] = volumes[i] / vs;
            }
        }
        matrix[4] = CausalPercentile(volRatio, PercentileWindow, ct);

        // 5) VWAP deviation percentile: (close - sessionVwap)/sessionVwap, UTC-session anchored
        //    (same construction as VwapReversionStrategy), then causal percentile.
        var vwapDev = new decimal?[n];
        decimal cumPv = 0m, cumV = 0m;
        DateTime? sessionDate = null;
        for (var i = 0; i < n; i++)
        {
            var c = candles[i];
            if (sessionDate != c.TimestampUtc.Date)
            {
                cumPv = 0m; cumV = 0m; sessionDate = c.TimestampUtc.Date;
            }
            var typical = (c.High + c.Low + c.Close) / 3m;
            cumPv += typical * c.Volume;
            cumV += c.Volume;
            var vwap = cumV > 0m ? cumPv / cumV : typical;
            if (vwap > 0m)
            {
                vwapDev[i] = (c.Close - vwap) / vwap;
            }
        }
        matrix[5] = CausalPercentile(vwapDev, PercentileWindow, ct);

        // 6) Momentum percentile: 10-bar rate of change, then causal percentile.
        var roc = new decimal?[n];
        for (var i = 10; i < n; i++)
        {
            if (closes[i - 10] > 0m)
            {
                roc[i] = (closes[i] - closes[i - 10]) / closes[i - 10];
            }
        }
        matrix[6] = CausalPercentile(roc, PercentileWindow, ct);

        // 7) MACD(12,26,9) histogram percentile.
        var (_, _, histogram) = await indicators.CalculateMacdAsync(closes, 12, 26, 9, ct);
        matrix[7] = CausalPercentile([.. histogram], PercentileWindow, ct);

        // 8) Distance from SMA50 percentile.
        var sma50 = await indicators.CalculateSmaAsync(closes, 50, ct);
        var dist = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            if (sma50[i] is decimal s && s > 0m)
            {
                dist[i] = (closes[i] - s) / s;
            }
        }
        matrix[8] = CausalPercentile(dist, PercentileWindow, ct);

        return matrix;
    }

    /// <summary>
    /// Causal rolling percentile rank ×100: value[i] ranked against the last
    /// <paramref name="window"/> non-null values ending AT i (inclusive). Never reads past i.
    /// Requires at least window/2 observations before emitting (stable ranks, honest warm-up).
    /// </summary>
    public static decimal?[] CausalPercentile(decimal?[] values, int window, CancellationToken ct = default)
    {
        var n = values.Length;
        var result = new decimal?[n];
        var buffer = new List<decimal>(window + 1); // rolling window of the last non-null values
        var minObs = Math.Max(2, window / 2);

        for (var i = 0; i < n; i++)
        {
            if ((i & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            if (values[i] is not decimal v)
            {
                continue; // nulls don't enter the window and produce null output
            }

            buffer.Add(v);
            if (buffer.Count > window)
            {
                buffer.RemoveAt(0);
            }
            if (buffer.Count >= minObs)
            {
                var below = 0;
                foreach (var b in buffer)
                {
                    if (b < v) below++;
                }
                result[i] = 100m * below / (buffer.Count - 1 == 0 ? 1 : buffer.Count - 1);
            }
        }
        return result;
    }

    private static async Task<decimal?[]> RemapSmaAsync(
        decimal?[] source, int period, ITechnicalIndicatorsService indicators, CancellationToken ct)
    {
        // SMA over the dense non-null sub-series, re-mapped to original indices (internal
        // gaps inherit the previous value so the series stays continuous — same convention
        // as the MACD signal line and StochasticStrategy).
        var n = source.Length;
        var result = new decimal?[n];
        var firstIdx = Array.FindIndex(source, v => v.HasValue);
        if (firstIdx < 0)
        {
            return result;
        }
        var dense = new List<decimal>(n - firstIdx);
        for (var i = firstIdx; i < n; i++)
        {
            dense.Add(source[i] ?? dense[^1]);
        }
        var smoothed = await indicators.CalculateSmaAsync(dense, period, ct);
        for (var j = 0; j < smoothed.Count; j++)
        {
            result[firstIdx + j] = smoothed[j];
        }
        return result;
    }

    private static async Task<decimal?[]> ComputeSupertrendDirAsync(
        IReadOnlyList<OhlcvData> candles, List<decimal> closes, List<decimal> highs, List<decimal> lows,
        ITechnicalIndicatorsService indicators, CancellationToken ct)
    {
        var n = candles.Count;
        var result = new decimal?[n];
        var atr = await indicators.CalculateAtrAsync(highs, lows, closes, 10, ct);

        decimal finalUpper = 0m, finalLower = 0m;
        var initialized = false;
        var prevTrendUp = true;
        for (var i = 0; i < n; i++)
        {
            if (atr[i] is not decimal a)
            {
                continue;
            }
            var hl2 = (highs[i] + lows[i]) / 2m;
            var basicUpper = hl2 + 3m * a;
            var basicLower = hl2 - 3m * a;

            if (!initialized)
            {
                finalUpper = basicUpper;
                finalLower = basicLower;
                prevTrendUp = closes[i] >= hl2;
                initialized = true;
            }
            else
            {
                var prevClose = closes[i - 1];
                finalUpper = (basicUpper < finalUpper || prevClose > finalUpper) ? basicUpper : finalUpper;
                finalLower = (basicLower > finalLower || prevClose < finalLower) ? basicLower : finalLower;
                prevTrendUp = prevTrendUp ? closes[i] >= finalLower : closes[i] > finalUpper;
            }
            result[i] = prevTrendUp ? 100m : 0m;
        }
        return result;
    }
}
