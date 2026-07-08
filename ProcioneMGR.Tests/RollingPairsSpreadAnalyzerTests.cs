using ProcioneMGR.Services.PairsTrading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="RollingPairsSpreadAnalyzer"/>: anti-look-ahead (invariante di
/// troncamento, come per gli IAlphaFactor), recupero approssimato dell'hedge ratio su una
/// coppia sintetica cointegrata, e struttura del warm-up.
/// </summary>
public class RollingPairsSpreadAnalyzerTests
{
    private readonly RollingPairsSpreadAnalyzer _analyzer = new();

    private static List<decimal> RandomWalk(int n, double stepScale, int seed)
    {
        var rnd = new Random(seed);
        var series = new List<decimal>(n) { 100m };
        for (var i = 1; i < n; i++)
        {
            series.Add(series[^1] + (decimal)((rnd.NextDouble() - 0.5) * 2 * stepScale));
        }
        return series;
    }

    [Fact]
    public void Analyze_IsAntiLookAhead_TruncationDoesNotChangePastValues()
    {
        var x = RandomWalk(500, 1.0, seed: 1);
        var rnd = new Random(2);
        var y = x.Select(xi => (decimal)(1.5 * (double)xi + (rnd.NextDouble() - 0.5) * 0.5)).ToList();

        var full = _analyzer.Analyze(y, x, lookbackWindow: 60, recalibrationInterval: 20, zScoreLookback: 15);

        foreach (var cut in new[] { 200, 300, 499 })
        {
            var truncated = _analyzer.Analyze(y.Take(cut + 1).ToList(), x.Take(cut + 1).ToList(), 60, 20, 15);

            Assert.Equal(full.HedgeRatio[cut].HasValue, truncated.HedgeRatio[cut].HasValue);
            if (full.HedgeRatio[cut].HasValue)
            {
                Assert.Equal(full.HedgeRatio[cut]!.Value, truncated.HedgeRatio[cut]!.Value, 9);
            }
            Assert.Equal(full.Spread[cut].HasValue, truncated.Spread[cut].HasValue);
            if (full.Spread[cut].HasValue)
            {
                Assert.Equal(full.Spread[cut]!.Value, truncated.Spread[cut]!.Value, 6);
            }
        }
    }

    [Fact]
    public void Analyze_HedgeRatio_ApproximatesTrueBeta_OnCointegratedPair()
    {
        var x = RandomWalk(1000, 1.0, seed: 3);
        var rnd = new Random(4);
        const double trueBeta = 2.5;
        var y = x.Select(xi => (decimal)(trueBeta * (double)xi + (rnd.NextDouble() - 0.5) * 0.3)).ToList();

        var result = _analyzer.Analyze(y, x, lookbackWindow: 100, recalibrationInterval: 50, zScoreLookback: 20);

        var lastHedge = result.HedgeRatio[^1];
        Assert.NotNull(lastHedge);
        Assert.True(Math.Abs(lastHedge!.Value - trueBeta) < 0.2, $"hedgeRatio={lastHedge}, atteso ~{trueBeta}");
    }

    [Fact]
    public void Analyze_WarmupBeforeLookbackWindow_IsNull()
    {
        var x = RandomWalk(200, 1.0, seed: 5);
        var y = RandomWalk(200, 1.0, seed: 6);

        var result = _analyzer.Analyze(y, x, lookbackWindow: 50, recalibrationInterval: 10, zScoreLookback: 10);

        for (var i = 0; i < 50; i++) Assert.Null(result.HedgeRatio[i]);
        Assert.NotNull(result.HedgeRatio[50]);
    }

    [Fact]
    public void Analyze_ZScore_NullOnlyDuringCombinedWarmup()
    {
        var x = RandomWalk(300, 1.0, seed: 7);
        var y = RandomWalk(300, 1.0, seed: 8);

        var result = _analyzer.Analyze(y, x, lookbackWindow: 50, recalibrationInterval: 10, zScoreLookback: 15);

        // Lo z-score richiede sia l'hedge ratio (warm-up 50) sia altre 15 osservazioni di spread.
        Assert.Null(result.ZScore[63]);
        Assert.NotNull(result.ZScore[65]);
    }

    [Fact]
    public void Analyze_MismatchedLengths_Throws()
    {
        var x = RandomWalk(100, 1.0, 1);
        var y = RandomWalk(90, 1.0, 2);
        Assert.Throws<ArgumentException>(() => _analyzer.Analyze(y, x, 50, 10, 10));
    }

    [Fact]
    public void Analyze_InvalidLookbackWindow_Throws()
    {
        var x = RandomWalk(100, 1.0, 1);
        var y = RandomWalk(100, 1.0, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => _analyzer.Analyze(y, x, lookbackWindow: 5, recalibrationInterval: 10, zScoreLookback: 10));
    }
}
