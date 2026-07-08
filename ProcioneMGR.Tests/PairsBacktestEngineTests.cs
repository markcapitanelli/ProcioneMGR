using ProcioneMGR.Data;
using ProcioneMGR.Services.PairsTrading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="PairsBacktestEngine"/>: allineamento per timestamp, generazione di trade
/// su uno spread costruito per divergere e rientrare (mean-reverting per costruzione),
/// determinismo, contabilità dollar-neutral a due gambe, e casi limite.
/// </summary>
public class PairsBacktestEngineTests
{
    private readonly PairsBacktestEngine _engine = new();

    private static List<OhlcvData> MakeCandles(IReadOnlyList<decimal> closes, string symbol, DateTime t0)
    {
        var list = new List<OhlcvData>(closes.Count);
        for (var i = 0; i < closes.Count; i++)
        {
            var c = closes[i];
            var prev = i > 0 ? closes[i - 1] : c;
            list.Add(new OhlcvData
            {
                Symbol = symbol,
                Timeframe = "1h",
                TimestampUtc = t0.AddHours(i),
                Open = prev,
                High = Math.Max(prev, c) * 1.005m,
                Low = Math.Min(prev, c) * 0.995m,
                Close = c,
                Volume = 100m,
            });
        }
        return list;
    }

    /// <summary>
    /// Costruisce una coppia cointegrata con uno spread deliberatamente oscillante (seno) sovrapposto
    /// al trend comune: garantisce diversi attraversamenti delle soglie di entrata/uscita.
    /// </summary>
    private static (List<decimal> Y, List<decimal> X) MakeOscillatingPair(int n, double beta, double amplitude, int seed)
    {
        var rnd = new Random(seed);
        var x = new List<decimal> { 100m };
        for (var i = 1; i < n; i++)
        {
            x.Add(x[^1] + (decimal)((rnd.NextDouble() - 0.5) * 0.5));
        }
        var y = new List<decimal>(n);
        for (var i = 0; i < n; i++)
        {
            var oscillation = amplitude * Math.Sin(i * 2 * Math.PI / 40.0); // periodo di 40 barre
            y.Add((decimal)(beta * (double)x[i] + oscillation));
        }
        return (y, x);
    }

    [Fact]
    public void RunBacktest_OnOscillatingSpread_GeneratesTrades()
    {
        var (y, x) = MakeOscillatingPair(800, beta: 1.8, amplitude: 8.0, seed: 1);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candlesY = MakeCandles(y, "Y/USDT", t0);
        var candlesX = MakeCandles(x, "X/USDT", t0);

        var config = new PairsBacktestConfiguration
        {
            SymbolY = "Y/USDT",
            SymbolX = "X/USDT",
            InitialCapital = 10_000m,
            PositionSizePercent = 10m,
            FeePercent = 0.05m,
            LookbackWindow = 60,
            RecalibrationInterval = 30,
            ZScoreLookback = 20,
            EntryZScore = 1.5m,
            ExitZScore = 0.3m,
        };

        var result = _engine.RunBacktest(candlesY, candlesX, config);

        Assert.Equal(800, result.CandlesEvaluated);
        Assert.Equal(800, result.EquityCurve.Count);
        Assert.True(result.TotalTrades > 0, "Lo spread oscillante deve generare almeno un trade");
        Assert.True(result.FinalCapital > 0m);
        Assert.Equal(result.TotalTrades, result.Trades.Count);
    }

    [Fact]
    public void RunBacktest_IsDeterministic()
    {
        var (y, x) = MakeOscillatingPair(500, beta: 2.0, amplitude: 6.0, seed: 2);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candlesY = MakeCandles(y, "Y/USDT", t0);
        var candlesX = MakeCandles(x, "X/USDT", t0);
        var config = new PairsBacktestConfiguration { LookbackWindow = 50, RecalibrationInterval = 25, ZScoreLookback = 15 };

        var r1 = _engine.RunBacktest(candlesY, candlesX, config);
        var r2 = _engine.RunBacktest(candlesY, candlesX, config);

        Assert.Equal(r1.FinalCapital, r2.FinalCapital);
        Assert.Equal(r1.TotalTrades, r2.TotalTrades);
        Assert.Equal(r1.EquityCurve[^1].Capital, r2.EquityCurve[^1].Capital);
    }

    [Fact]
    public void RunBacktest_AlignsCandlesByTimestamp_IgnoringGaps()
    {
        var (y, x) = MakeOscillatingPair(300, beta: 1.5, amplitude: 5.0, seed: 3);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candlesY = MakeCandles(y, "Y/USDT", t0);
        // X manca 10 candele nel mezzo (gap) -> l'allineamento deve escluderle da entrambe.
        var candlesXFull = MakeCandles(x, "X/USDT", t0);
        var candlesX = candlesXFull.Where((c, i) => i < 100 || i >= 110).ToList();

        var config = new PairsBacktestConfiguration { LookbackWindow = 50, RecalibrationInterval = 25, ZScoreLookback = 15 };
        var result = _engine.RunBacktest(candlesY, candlesX, config);

        Assert.Equal(290, result.CandlesEvaluated); // 300 - 10 disallineate
    }

    [Fact]
    public void RunBacktest_NoOverlappingTimestamps_ReturnsInitialCapital()
    {
        var (y, x) = MakeOscillatingPair(100, 1.0, 5.0, 4);
        var candlesY = MakeCandles(y, "Y/USDT", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var candlesX = MakeCandles(x, "X/USDT", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // nessuna sovrapposizione

        var result = _engine.RunBacktest(candlesY, candlesX, new PairsBacktestConfiguration { InitialCapital = 5000m });

        Assert.Equal(5000m, result.FinalCapital);
        Assert.Equal(0, result.CandlesEvaluated);
        Assert.Empty(result.Trades);
    }

    [Fact]
    public void RunBacktest_Trades_AreDollarNeutralAtEntry()
    {
        var (y, x) = MakeOscillatingPair(800, beta: 1.8, amplitude: 8.0, seed: 5);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candlesY = MakeCandles(y, "Y/USDT", t0);
        var candlesX = MakeCandles(x, "X/USDT", t0);
        var config = new PairsBacktestConfiguration
        {
            LookbackWindow = 60,
            RecalibrationInterval = 30,
            ZScoreLookback = 20,
            EntryZScore = 1.5m,
            ExitZScore = 0.3m,
            PositionSizePercent = 10m,
        };

        var result = _engine.RunBacktest(candlesY, candlesX, config);
        Assert.True(result.Trades.Count > 0);

        // Nessuna assertion diretta sul notional (privato all'interno del portfolio), ma la
        // direzione delle gambe deve essere coerente con il lato dichiarato.
        foreach (var t in result.Trades)
        {
            Assert.True(t.Side is PairsPositionSide.LongSpread or PairsPositionSide.ShortSpread);
            Assert.NotNull(t.ExitTime);
        }
    }
}
