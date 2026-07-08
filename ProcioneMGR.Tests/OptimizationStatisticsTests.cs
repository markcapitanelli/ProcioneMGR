using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Tests;

/// <summary>Test deterministici del calcolo Sharpe (il punto più delicato dell'ottimizzazione).</summary>
public class OptimizationStatisticsTests
{
    [Theory]
    [InlineData("1h", 8760)]
    [InlineData("1d", 365)]
    [InlineData("15m", 35040)]
    public void PeriodsPerYear_IsCorrect(string tf, int expected)
        => Assert.Equal(expected, Statistics.PeriodsPerYear(tf));

    [Fact]
    public void Sharpe_KnownEquity_MatchesHandComputed()
    {
        // equity [100, 110, 99] -> returns [+0.10, -0.10], mean=0, std(pop)=0.10
        // rfPerPeriod = 0.02/8760; Sharpe = (0 - rf)/0.10 * sqrt(8760) ≈ -0.002137
        var eq = new List<EquityPoint>
        {
            new() { Capital = 100m }, new() { Capital = 110m }, new() { Capital = 99m },
        };

        var sharpe = Statistics.SharpeRatio(eq, periodsPerYear: 8760);
        Assert.True(Math.Abs((double)sharpe - (-0.002137)) < 1e-4, $"Sharpe={sharpe}");
    }

    [Fact]
    public void Sharpe_ConstantReturns_IsZero_NoDivideByZero()
    {
        // Rendimenti tutti uguali (+10% ogni periodo) -> stdDev = 0 -> Sharpe = 0
        var eq = new List<EquityPoint>
        {
            new() { Capital = 100m }, new() { Capital = 110m }, new() { Capital = 121m }, new() { Capital = 133.1m },
        };
        Assert.Equal(0m, Statistics.SharpeRatio(eq, 8760));
    }

    [Fact]
    public void Sharpe_TooFewPoints_IsZero()
    {
        var eq = new List<EquityPoint> { new() { Capital = 100m }, new() { Capital = 110m } };
        Assert.Equal(0m, Statistics.SharpeRatio(eq, 8760));
    }

    [Fact]
    public void Sharpe_PositiveTrend_IsPositive()
    {
        // Trend in salita con un po' di rumore -> Sharpe positivo
        var eq = new List<EquityPoint>();
        decimal cap = 100m;
        var deltas = new[] { 0.02m, -0.01m, 0.03m, 0.01m, -0.005m, 0.02m, 0.015m, -0.008m, 0.025m, 0.01m };
        eq.Add(new EquityPoint { Capital = cap });
        foreach (var d in deltas)
        {
            cap *= 1 + d;
            eq.Add(new EquityPoint { Capital = cap });
        }
        Assert.True(Statistics.SharpeRatio(eq, 8760) > 0m);
    }
}
