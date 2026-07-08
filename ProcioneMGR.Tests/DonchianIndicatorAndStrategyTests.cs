using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Tests;

/// <summary>Test di SMA, Donchian Channel e della strategia DonchianBreakout (cap. 3 e 6).</summary>
public class DonchianIndicatorAndStrategyTests
{
    // --- SMA ---------------------------------------------------------------------------------

    [Fact]
    public async Task Sma_MatchesNaive_AndAlignsWarmup()
    {
        var svc = new TechnicalIndicatorsService();
        var values = new List<decimal> { 1m, 2m, 3m, 4m, 5m, 6m };

        var sma = await svc.CalculateSmaAsync(values, 3);

        Assert.Equal(values.Count, sma.Count);
        Assert.Null(sma[0]);
        Assert.Null(sma[1]);
        Assert.Equal(2m, sma[2]); // (1+2+3)/3
        Assert.Equal(3m, sma[3]);
        Assert.Equal(5m, sma[5]);
    }

    // --- Donchian ----------------------------------------------------------------------------

    [Fact]
    public async Task Donchian_HhvLlv_MatchNaive()
    {
        var svc = new TechnicalIndicatorsService();
        var highs = new List<decimal> { 10m, 12m, 11m, 15m, 9m, 8m };
        var lows = new List<decimal> { 5m, 7m, 6m, 9m, 4m, 3m };

        var (upper, lower) = await svc.CalculateDonchianAsync(highs, lows, 3);

        Assert.Null(upper[1]);
        for (var i = 2; i < highs.Count; i++)
        {
            var expectedHigh = Math.Max(highs[i], Math.Max(highs[i - 1], highs[i - 2]));
            var expectedLow = Math.Min(lows[i], Math.Min(lows[i - 1], lows[i - 2]));
            Assert.Equal(expectedHigh, upper[i]);
            Assert.Equal(expectedLow, lower[i]);
        }
    }

    [Fact]
    public async Task Donchian_MismatchedLengths_Throws()
    {
        var svc = new TechnicalIndicatorsService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CalculateDonchianAsync([1m, 2m], [1m], 2));
    }

    // --- DonchianBreakoutStrategy ---------------------------------------------------------------

    private static List<OhlcvData> Candles(params (decimal High, decimal Low, decimal Close)[] bars)
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return bars.Select((b, i) => new OhlcvData
        {
            Symbol = "TEST",
            Timeframe = "1d",
            TimestampUtc = t0.AddDays(i),
            Open = b.Close,
            High = b.High,
            Low = b.Low,
            Close = b.Close,
            Volume = 1m,
        }).ToList();
    }

    private static async Task<List<Signal>> RunStrategy(List<OhlcvData> candles, Dictionary<string, decimal> parameters)
    {
        var strategy = new DonchianBreakoutStrategy();
        var closes = candles.Select(c => c.Close).ToList();
        await strategy.InitializeAsync(closes, candles, parameters, new TechnicalIndicatorsService(), CancellationToken.None);

        var signals = new List<Signal>(candles.Count);
        for (var i = 0; i < candles.Count; i++)
        {
            signals.Add(strategy.EvaluateSignal(i, closes[i], candles[i].TimestampUtc));
        }
        return signals;
    }

    [Fact]
    public async Task DonchianBreakout_Long_EntersOnBreakout_ExitsOnLlvViolation()
    {
        // EntryPeriod=3, ExitPeriod=2, solo long.
        // Barre: range stabile, poi breakout della close sopra l'HHV3 precedente,
        // poi crollo sotto il LLV2 precedente.
        var candles = Candles(
            (10m, 8m, 9m),    // 0
            (10m, 8m, 9m),    // 1
            (10m, 8m, 9m),    // 2: HHV3 = 10
            (12m, 9m, 11m),   // 3: close 11 > HHV3[2]=10 -> LONG
            (13m, 10m, 12m),  // 4: in posizione
            (12m, 7m, 7.5m)); // 5: close 7.5 < LLV2[4]=min(9,10)=9 -> CLOSE

        var signals = await RunStrategy(candles, new()
        {
            ["EntryPeriod"] = 3m,
            ["ExitPeriod"] = 2m,
            ["Direction"] = 0m,
        });

        Assert.Equal(Signal.Hold, signals[2]);
        Assert.Equal(Signal.Long, signals[3]);
        Assert.Equal(Signal.Hold, signals[4]);
        Assert.Equal(Signal.Close, signals[5]);
    }

    [Fact]
    public async Task DonchianBreakout_ShortDirection_EntersOnBreakdown()
    {
        var candles = Candles(
            (10m, 8m, 9m),
            (10m, 8m, 9m),
            (10m, 8m, 9m),   // LLV3 = 8
            (9m, 6m, 7m));   // close 7 < LLV3[2]=8 -> SHORT

        var signals = await RunStrategy(candles, new()
        {
            ["EntryPeriod"] = 3m,
            ["ExitPeriod"] = 2m,
            ["Direction"] = 1m,
        });

        Assert.Equal(Signal.Short, signals[3]);
    }

    [Fact]
    public async Task DonchianBreakout_LongOnly_IgnoresBreakdown()
    {
        var candles = Candles(
            (10m, 8m, 9m),
            (10m, 8m, 9m),
            (10m, 8m, 9m),
            (9m, 6m, 7m)); // breakdown, ma Direction=0 (solo long)

        var signals = await RunStrategy(candles, new()
        {
            ["EntryPeriod"] = 3m,
            ["ExitPeriod"] = 2m,
            ["Direction"] = 0m,
        });

        Assert.Equal(Signal.Hold, signals[3]);
    }

    [Fact]
    public async Task DonchianBreakout_InvalidParameters_Throw()
    {
        var strategy = new DonchianBreakoutStrategy();
        var candles = Candles((10m, 8m, 9m));
        await Assert.ThrowsAsync<ArgumentException>(() => strategy.InitializeAsync(
            [9m], candles, new Dictionary<string, decimal> { ["EntryPeriod"] = 1m },
            new TechnicalIndicatorsService(), CancellationToken.None));
    }

    [Fact]
    public void Factory_CreatesDonchianBreakout()
    {
        var factory = new StrategyFactory();
        Assert.Equal("DonchianBreakout", factory.Create("DonchianBreakout").Name);
        Assert.Contains(factory.Prototypes, p => p.Name == "DonchianBreakout");
    }
}
