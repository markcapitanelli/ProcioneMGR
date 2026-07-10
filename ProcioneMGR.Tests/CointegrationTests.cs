using ProcioneMGR.Services.TimeSeries;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="EngleGrangerCointegrationTest"/> e <see cref="PairsSpreadAnalyzer"/>: una
/// coppia costruita per essere cointegrata (Y = beta*X + rumore stazionario) deve superare il
/// test, con hedge ratio recuperato vicino al vero beta; due random walk indipendenti (nessuna
/// relazione di lungo periodo) non devono risultare cointegrate.
/// </summary>
public class CointegrationTests
{
    private readonly ICointegrationTest _test = new EngleGrangerCointegrationTest();

    private static List<decimal> RandomWalk(int n, double stepScale, int seed)
    {
        var rnd = new Random(seed);
        var series = new List<decimal>(n) { 100m };
        for (var i = 1; i < n; i++)
        {
            var step = (rnd.NextDouble() - 0.5) * 2 * stepScale;
            series.Add(series[^1] + (decimal)step);
        }
        return series;
    }

    [Fact]
    public void CointegratedPair_IsDetected_WithHedgeRatioClosToTrue()
    {
        var x = RandomWalk(1000, stepScale: 1.0, seed: 1);
        var rnd = new Random(2);
        const double trueBeta = 2.0;
        const double trueIntercept = 5.0;
        var y = x.Select(xi => (decimal)(trueIntercept + trueBeta * (double)xi + (rnd.NextDouble() - 0.5) * 0.5)).ToList();

        var result = _test.Test(y, x);

        Assert.True(result.IsCointegrated, $"ADF={result.AdfStatistic:F3} (atteso < CV MacKinnon {result.CriticalValue:F3})");
        Assert.True(Math.Abs(result.HedgeRatio - trueBeta) < 0.1, $"hedgeRatio={result.HedgeRatio:F3}, atteso ~{trueBeta}");
    }

    [Fact]
    public void MacKinnonCriticalValue_IsStricterThanPlainAdf_AndReportsLags()
    {
        // P0-1: il valore critico di cointegrazione al 5% (~-3.34) è più severo del vecchio ADF -2.86.
        var x = RandomWalk(1000, stepScale: 1.0, seed: 1);
        var rnd = new Random(2);
        var y = x.Select(xi => (decimal)(5.0 + 2.0 * (double)xi + (rnd.NextDouble() - 0.5) * 0.5)).ToList();

        var result = _test.Test(y, x);

        Assert.True(result.CriticalValue < -2.86, $"CV={result.CriticalValue:F3} atteso più severo di -2.86");
        Assert.InRange(result.CriticalValue, -3.5, -3.2); // ~-3.34 per T grande
        Assert.Equal(5.0, result.SignificanceLevelPercent);
        Assert.InRange(result.AdfLags, 0, 20);
        // Il giudizio usa la statistica contro il valore critico MacKinnon riportato.
        Assert.Equal(result.AdfStatistic < result.CriticalValue, result.IsCointegrated);
    }

    [Fact]
    public void IndependentRandomWalks_AreNotCointegrated()
    {
        var x = RandomWalk(1000, stepScale: 1.0, seed: 10);
        var y = RandomWalk(1000, stepScale: 1.0, seed: 20);

        var result = _test.Test(y, x);

        Assert.False(result.IsCointegrated, $"ADF={result.AdfStatistic:F3} (atteso >= CV MacKinnon {result.CriticalValue:F3}, spread non stazionario)");
    }

    [Fact]
    public void Spread_HasSameLengthAsInput()
    {
        var x = RandomWalk(200, 1.0, 1);
        var y = RandomWalk(200, 1.0, 2);
        var result = _test.Test(y, x);
        Assert.Equal(200, result.Spread.Count);
    }

    [Fact]
    public void MismatchedLengths_Throws()
    {
        var x = RandomWalk(100, 1.0, 1);
        var y = RandomWalk(90, 1.0, 2);
        Assert.Throws<ArgumentException>(() => _test.Test(y, x));
    }

    [Fact]
    public void TooFewObservations_Throws()
    {
        var x = RandomWalk(10, 1.0, 1);
        var y = RandomWalk(10, 1.0, 2);
        Assert.Throws<ArgumentException>(() => _test.Test(y, x));
    }

    // --- PairsSpreadAnalyzer -------------------------------------------------------------------

    [Fact]
    public void SpreadAnalyzer_ZScore_NullDuringWarmup_ThenPopulated()
    {
        var x = RandomWalk(500, 1.0, seed: 3);
        var rnd = new Random(4);
        var y = x.Select(xi => (decimal)(1.5 * (double)xi + (rnd.NextDouble() - 0.5) * 0.3)).ToList();

        var analyzer = new PairsSpreadAnalyzer(_test);
        var analysis = analyzer.Analyze(y, x, zScoreLookback: 20);

        for (var i = 0; i < 19; i++) Assert.Null(analysis.ZScore[i]);
        for (var i = 19; i < 500; i++) Assert.NotNull(analysis.ZScore[i]);
    }

    [Fact]
    public void SpreadAnalyzer_ZScore_IsCausal_TruncationDoesNotChangePastValues()
    {
        // Lo z-score rolling e' causale rispetto allo SPREAD (non rispetto all'hedge ratio,
        // stimato sull'intero campione - limite dichiarato). A parita' di spread, il valore di
        // z a un indice i non deve cambiare troncando la serie DOPO i.
        var x = RandomWalk(300, 1.0, seed: 5);
        var rnd = new Random(6);
        var y = x.Select(xi => (decimal)(0.8 * (double)xi + (rnd.NextDouble() - 0.5) * 0.2)).ToList();

        var spread = Enumerable.Range(0, 300).Select(i => Math.Sin(i * 0.1) * 10).ToList(); // spread sintetico noto
        var full = PairsSpreadAnalyzer.RollingZScore(spread, 20);
        var truncated = PairsSpreadAnalyzer.RollingZScore(spread.Take(150).ToList(), 20);

        for (var i = 19; i < 150; i++)
        {
            Assert.Equal(full[i]!.Value, truncated[i]!.Value, 9);
        }
    }

    [Fact]
    public void SpreadAnalyzer_InvalidLookback_Throws()
    {
        var x = RandomWalk(100, 1.0, 1);
        var y = RandomWalk(100, 1.0, 2);
        var analyzer = new PairsSpreadAnalyzer(_test);
        Assert.Throws<ArgumentOutOfRangeException>(() => analyzer.Analyze(y, x, zScoreLookback: 2));
    }
}
