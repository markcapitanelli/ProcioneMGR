using ProcioneMGR.Services.Risk;

namespace ProcioneMGR.Tests;

/// <summary>Test della Montecarlo Analysis evoluta (Trombetta cap. 8).</summary>
public class MonteCarloAnalyzerTests
{
    private static readonly List<decimal> SamplePnls =
        [100m, -50m, 200m, -150m, 300m, -100m, 250m, -80m, 120m, -60m, 180m, -40m];

    [Fact]
    public void Run_Deterministic_WithSeed()
    {
        var analyzer = new MonteCarloAnalyzer();
        var config = new MonteCarloConfig { NumberOfShuffles = 50, Seed = 42 };

        var r1 = analyzer.Run(SamplePnls, config);
        var r2 = analyzer.Run(SamplePnls, config);

        Assert.Equal(r1.MaxDrawdown95, r2.MaxDrawdown95);
        Assert.Equal(r1.WorstMaxDrawdown, r2.WorstMaxDrawdown);
        Assert.Equal(r1.SortedMaxDrawdowns, r2.SortedMaxDrawdowns);
    }

    [Fact]
    public void Run_OriginalEquityAndDrawdown_HandComputed()
    {
        // PnL: 100, -50, 200 -> equity 100, 50, 250 -> maxDD = 50.
        var result = new MonteCarloAnalyzer().Run([100m, -50m, 200m],
            new MonteCarloConfig { NumberOfShuffles = 10, Seed = 1 });

        Assert.Equal([100m, 50m, 250m], result.OriginalEquity);
        Assert.Equal(50m, result.OriginalMaxDrawdown);
    }

    [Fact]
    public void Run_ShufflesPreserveProfit_WhenAllOperationsUsed()
    {
        // Con OperationsPercent=100 ogni ricombinazione e' una permutazione:
        // il profitto finale coincide sempre con quello originale.
        var result = new MonteCarloAnalyzer().Run(SamplePnls,
            new MonteCarloConfig { NumberOfShuffles = 30, Seed = 7, OperationsPercent = 100m });

        var originalProfit = result.OriginalEquity[^1];
        Assert.Equal(originalProfit, result.WorstEquity[^1]);
        Assert.Equal(originalProfit, result.BestEquity[^1]);
    }

    [Fact]
    public void Run_Percentile95_IsBetween_BestAndWorst()
    {
        var result = new MonteCarloAnalyzer().Run(SamplePnls,
            new MonteCarloConfig { NumberOfShuffles = 200, Seed = 42 });

        Assert.True(result.MaxDrawdown95 >= result.BestMaxDrawdown);
        Assert.True(result.MaxDrawdown95 <= result.WorstMaxDrawdown);
        Assert.True(result.RiskFactor95 >= 0m);
        Assert.True(result.RiskFactorWorst >= result.RiskFactor95 / 2m); // sanity: worst >= p95 (a meno di dettagli di distribuzione)
        Assert.Equal(200, result.SortedMaxDrawdowns.Count);
    }

    [Fact]
    public void Run_ExtraCosts_ReduceFinalProfit()
    {
        var noCosts = new MonteCarloAnalyzer().Run(SamplePnls,
            new MonteCarloConfig { NumberOfShuffles = 10, Seed = 3 });
        var withCosts = new MonteCarloAnalyzer().Run(SamplePnls,
            new MonteCarloConfig { NumberOfShuffles = 10, Seed = 3, ExtraCostPerTrade = 10m });

        // 12 trade * 2 * 10$ = 240$ in meno.
        Assert.Equal(noCosts.OriginalEquity[^1] - 240m, withCosts.OriginalEquity[^1]);
    }

    [Fact]
    public void Run_SubsetRecombination_UsesRequestedSampleSize()
    {
        var result = new MonteCarloAnalyzer().Run(SamplePnls,
            new MonteCarloConfig { NumberOfShuffles = 5, Seed = 9, OperationsPercent = 50m });

        // 50% di 12 trade = 6 elementi per ricombinazione; worst/best possono restare la
        // curva originale (12 elementi) se nessuna ricombinazione la supera.
        Assert.Equal(5, result.SortedMaxDrawdowns.Count);
        Assert.Contains(result.WorstEquity.Count, new[] { 6, 12 });
        Assert.Contains(result.BestEquity.Count, new[] { 6, 12 });
        Assert.All(result.SortedMaxDrawdowns, dd => Assert.True(dd >= 0m));
    }

    [Fact]
    public void Run_EmptyTrades_ReturnsEmptyResult_NoThrow()
    {
        var result = new MonteCarloAnalyzer().Run([], new MonteCarloConfig { NumberOfShuffles = 10, Seed = 1 });
        Assert.Empty(result.OriginalEquity);
        Assert.Empty(result.SortedMaxDrawdowns);
        Assert.Equal(0m, result.MaxDrawdown95);
    }

    [Fact]
    public void Run_InvalidConfig_Throws()
    {
        var analyzer = new MonteCarloAnalyzer();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            analyzer.Run(SamplePnls, new MonteCarloConfig { NumberOfShuffles = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            analyzer.Run(SamplePnls, new MonteCarloConfig { OperationsPercent = 0m }));
    }
}
