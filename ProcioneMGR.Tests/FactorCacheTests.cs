using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test della cache dei fattori (Fase 4): trasparenza (cache == ricalcolo, nessuno skew), hit/miss,
/// invalidazione al cambio di parametri o di dati, e sfratto FIFO sotto capacità. È un memoizzatore
/// puro: non deve mai cambiare il valore calcolato.
/// </summary>
public class FactorCacheTests
{
    private static List<OhlcvData> Candles(int n, string symbol = "BTCUSDT", string tf = "1h")
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var list = new List<OhlcvData>(n);
        for (var i = 0; i < n; i++)
        {
            var c = 100m + (decimal)Math.Sin(i / 5.0) * 3m + i * 0.1m;
            var prev = i > 0 ? list[i - 1].Close : c;
            list.Add(new OhlcvData
            {
                Symbol = symbol, Timeframe = tf, TimestampUtc = t0.AddHours(i),
                Open = prev, High = Math.Max(prev, c) * 1.01m, Low = Math.Min(prev, c) * 0.99m, Close = c, Volume = 100m,
            });
        }
        return list;
    }

    private static readonly Dictionary<string, decimal> Mom = new() { ["Lookback"] = 5m, ["Skip"] = 0m };

    [Fact]
    public void GetOrCompute_MatchesDirectCompute()
    {
        var cache = new FactorCache();
        var factor = new MomentumFactor();
        var candles = Candles(200);

        var cached = cache.GetOrCompute(factor, Mom, candles);
        var direct = factor.Compute(candles, Mom);

        Assert.Equal(direct.Count, cached.Count);
        for (var i = 0; i < direct.Count; i++) Assert.Equal(direct[i], cached[i]);
    }

    [Fact]
    public void SecondCall_IsAHit_AndReturnsSameReference()
    {
        var cache = new FactorCache();
        var factor = new MomentumFactor();
        var candles = Candles(200);

        var first = cache.GetOrCompute(factor, Mom, candles);
        var second = cache.GetOrCompute(factor, Mom, candles);

        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.Hits);
        Assert.Same(first, second); // stessa serie memoizzata, nessun ricalcolo
    }

    [Fact]
    public void DifferentParameters_MissAndRecompute()
    {
        var cache = new FactorCache();
        var factor = new MomentumFactor();
        var candles = Candles(200);

        cache.GetOrCompute(factor, new Dictionary<string, decimal> { ["Lookback"] = 5m, ["Skip"] = 0m }, candles);
        cache.GetOrCompute(factor, new Dictionary<string, decimal> { ["Lookback"] = 10m, ["Skip"] = 0m }, candles);

        Assert.Equal(2, cache.Misses); // parametri diversi ⇒ chiavi diverse ⇒ due calcoli
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void NewData_InvalidatesCache()
    {
        var cache = new FactorCache();
        var factor = new MomentumFactor();

        cache.GetOrCompute(factor, Mom, Candles(200));
        cache.GetOrCompute(factor, Mom, Candles(201)); // una candela in più ⇒ impronta dati diversa

        Assert.Equal(2, cache.Misses);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void DifferentSymbol_IsADifferentKey()
    {
        var cache = new FactorCache();
        var factor = new MomentumFactor();

        cache.GetOrCompute(factor, Mom, Candles(200, symbol: "BTCUSDT"));
        cache.GetOrCompute(factor, Mom, Candles(200, symbol: "ETHUSDT"));

        Assert.Equal(2, cache.Misses);
    }

    [Fact]
    public void RespectsMaxEntries_WithFifoEviction()
    {
        var cache = new FactorCache(new FactorCacheOptions { MaxEntries = 16 });
        var factor = new MomentumFactor();
        var candles = Candles(200);

        for (var lb = 1; lb <= 40; lb++)
            cache.GetOrCompute(factor, new Dictionary<string, decimal> { ["Lookback"] = lb, ["Skip"] = 0m }, candles);

        Assert.True(cache.Count <= 16, $"la cache non rispetta il tetto: {cache.Count}");
    }

    [Fact]
    public void EmptyCandles_ComputesWithoutCaching()
    {
        var cache = new FactorCache();
        var factor = new MomentumFactor();

        var result = cache.GetOrCompute(factor, Mom, new List<OhlcvData>());

        Assert.Empty(result);
        Assert.Equal(0, cache.Count);
    }
}
