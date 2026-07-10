using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// P0-4: i backtest della pipeline devono usare i costi reali del venue (Bitget), INCLUSO il
/// funding dei perpetual — che prima restava a 0 (default di BacktestConfiguration) mentre fee e
/// slippage erano già applicati. PipelineCosts centralizza lettura + applicazione dei tre costi.
/// </summary>
public class PipelineCostsTests
{
    [Fact]
    public void FromConfig_EmptyConfig_UsesVenueDefaults_IncludingFunding()
    {
        var costs = PipelineCosts.FromConfig(new StageConfig());
        Assert.Equal(0.05m, costs.SlippagePercent);
        Assert.Equal(0.1m, costs.FeePercent);
        // Il gap che P0-4 chiude: il funding non è più 0 di default.
        Assert.Equal(0.01m, costs.FundingRatePercentPer8h);
        Assert.True(costs.FundingRatePercentPer8h > 0m);
    }

    [Fact]
    public void FromConfig_ReadsOverrides()
    {
        var cfg = new StageConfig
        {
            Parameters =
            {
                ["slippagePercent"] = "0.2",
                ["feePercent"] = "0.06",
                ["fundingRatePercentPer8h"] = "0.03",
            },
        };
        var costs = PipelineCosts.FromConfig(cfg);
        Assert.Equal(0.2m, costs.SlippagePercent);
        Assert.Equal(0.06m, costs.FeePercent);
        Assert.Equal(0.03m, costs.FundingRatePercentPer8h);
    }

    [Fact]
    public void ApplyTo_SetsAllThreeCostsOnBacktestConfig()
    {
        var costs = new PipelineCosts(SlippagePercent: 0.07m, FeePercent: 0.06m, FundingRatePercentPer8h: 0.02m);
        var cfg = costs.ApplyTo(new BacktestConfiguration());

        Assert.Equal(0.07m, cfg.SlippagePercent);
        Assert.Equal(0.06m, cfg.FeePercent);
        Assert.Equal(0.02m, cfg.FundingRatePercentPer8h);
    }

    [Fact]
    public void ParameterDefinitions_ExposeTheThreeCostKnobs()
    {
        var keys = PipelineCosts.ParameterDefinitions.Select(d => d.Key).ToList();
        Assert.Contains("slippagePercent", keys);
        Assert.Contains("feePercent", keys);
        Assert.Contains("fundingRatePercentPer8h", keys);
    }
}
