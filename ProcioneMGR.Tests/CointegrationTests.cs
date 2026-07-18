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

    // --- PairsSpreadAnalyzer.RollingZScore (z-score causale) -----------------------------------

    [Fact]
    public void RollingZScore_NullDuringWarmup_ThenPopulated()
    {
        var spread = Enumerable.Range(0, 500).Select(i => Math.Sin(i * 0.1) * 10).ToList();
        var z = PairsSpreadAnalyzer.RollingZScore(spread, lookback: 20);

        for (var i = 0; i < 19; i++) Assert.Null(z[i]);
        for (var i = 19; i < 500; i++) Assert.NotNull(z[i]);
    }

    [Fact]
    public void RollingZScore_IsCausal_TruncationDoesNotChangePastValues()
    {
        // Lo z-score rolling e' causale: il valore a un indice i non deve cambiare troncando la
        // serie DOPO i (usa solo la finestra passata dello spread).
        var spread = Enumerable.Range(0, 300).Select(i => Math.Sin(i * 0.1) * 10).ToList();
        var full = PairsSpreadAnalyzer.RollingZScore(spread, 20);
        var truncated = PairsSpreadAnalyzer.RollingZScore(spread.Take(150).ToList(), 20);

        for (var i = 19; i < 150; i++)
        {
            Assert.Equal(full[i]!.Value, truncated[i]!.Value, 9);
        }
    }
}
