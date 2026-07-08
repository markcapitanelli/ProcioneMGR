using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Portfolio;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test degli allocatori di portafoglio (cap. 5/13): Mean-Variance (Max Sharpe / Min Variance),
/// Risk Parity naive (inverse-volatility) e HRP. Casi noti (un asset molto più volatile di un
/// altro, uno con rendimento atteso molto più alto) verificano che il segno dell'allocazione sia
/// quello atteso; le proprietà strutturali (somma=1, vincoli Min/Max) sono verificate ovunque.
/// </summary>
public class PortfolioOptimizerTests
{
    private static List<decimal> ScaledNoise(int n, double scale, int seed)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => (decimal)((rnd.NextDouble() - 0.5) * 2 * scale)).ToList();
    }

    private static List<decimal> ScaledNoiseWithDrift(int n, double scale, double drift, int seed)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => (decimal)(drift + (rnd.NextDouble() - 0.5) * 2 * scale)).ToList();
    }

    // --- MeanVarianceOptimizer -----------------------------------------------------------------

    [Fact]
    public void MinVariance_FavorsLowVolatilityAsset()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["LowVol"] = ScaledNoise(1000, 0.005, seed: 1),
            ["HighVol"] = ScaledNoise(1000, 0.03, seed: 2),
        };
        var optimizer = new MeanVarianceOptimizer();
        var config = new PortfolioOptimizationConfig { Objective = MeanVarianceObjective.MinVariance };

        var result = optimizer.Optimize(returns, config);

        Assert.True(result.Weights["LowVol"] > result.Weights["HighVol"],
            $"LowVol={result.Weights["LowVol"]:F3} HighVol={result.Weights["HighVol"]:F3}");
        AssertWeightsSumToOne(result);
    }

    [Fact]
    public void MaxSharpe_FavorsHigherExpectedReturnAsset()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["HighReturn"] = ScaledNoiseWithDrift(1000, 0.01, drift: 0.003, seed: 1),
            ["LowReturn"] = ScaledNoiseWithDrift(1000, 0.01, drift: 0.0002, seed: 2),
        };
        var optimizer = new MeanVarianceOptimizer();
        var config = new PortfolioOptimizationConfig { Objective = MeanVarianceObjective.MaxSharpe, RiskFreeRateAnnual = 0m };

        var result = optimizer.Optimize(returns, config);

        Assert.True(result.Weights["HighReturn"] > result.Weights["LowReturn"],
            $"HighReturn={result.Weights["HighReturn"]:F3} LowReturn={result.Weights["LowReturn"]:F3}");
        AssertWeightsSumToOne(result);
    }

    [Fact]
    public void MeanVariance_RespectsMaxWeightBound()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["LowVol"] = ScaledNoise(1000, 0.005, seed: 1),
            ["HighVol"] = ScaledNoise(1000, 0.05, seed: 2),
        };
        var optimizer = new MeanVarianceOptimizer();
        var config = new PortfolioOptimizationConfig { Objective = MeanVarianceObjective.MinVariance, MaxWeight = 0.6m };

        var result = optimizer.Optimize(returns, config);

        Assert.True(result.Weights["LowVol"] <= 0.6m + 0.0001m, $"LowVol={result.Weights["LowVol"]}");
        AssertWeightsSumToOne(result);
    }

    [Fact]
    public void MeanVariance_TooFewSymbols_Throws()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>> { ["A"] = ScaledNoise(100, 0.01, 1) };
        Assert.Throws<ArgumentException>(() => new MeanVarianceOptimizer().Optimize(returns));
    }

    [Fact]
    public void MeanVariance_MismatchedLengths_Throws()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = ScaledNoise(100, 0.01, 1),
            ["B"] = ScaledNoise(90, 0.01, 2),
        };
        Assert.Throws<ArgumentException>(() => new MeanVarianceOptimizer().Optimize(returns));
    }

    // --- RiskParityOptimizer --------------------------------------------------------------------

    [Fact]
    public void RiskParity_WeightsAreInverselyProportionalToVolatility()
    {
        // HighVol ha 3x la scala di rumore di LowVol -> atteso w(LowVol)/w(HighVol) ~= 3.
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["LowVol"] = ScaledNoise(2000, 0.01, seed: 1),
            ["HighVol"] = ScaledNoise(2000, 0.03, seed: 2),
        };
        var optimizer = new RiskParityOptimizer();

        var result = optimizer.Optimize(returns);

        var ratio = result.Weights["LowVol"] / result.Weights["HighVol"];
        Assert.True(Math.Abs(ratio - 3m) < 0.5m, $"ratio={ratio:F2}");
        AssertWeightsSumToOne(result);
    }

    [Fact]
    public void RiskParity_EqualVolatility_GivesEqualWeights()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = ScaledNoise(1000, 0.02, seed: 1),
            ["B"] = ScaledNoise(1000, 0.02, seed: 2),
            ["C"] = ScaledNoise(1000, 0.02, seed: 3),
        };
        var optimizer = new RiskParityOptimizer();

        var result = optimizer.Optimize(returns);

        foreach (var w in result.Weights.Values)
        {
            Assert.True(Math.Abs(w - 1m / 3m) < 0.05m, $"peso={w:F3}, atteso ~0.333");
        }
        AssertWeightsSumToOne(result);
    }

    [Fact]
    public void RiskParity_TooFewSymbols_Throws()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>> { ["A"] = ScaledNoise(100, 0.01, 1) };
        Assert.Throws<ArgumentException>(() => new RiskParityOptimizer().Optimize(returns));
    }

    // --- HierarchicalRiskParityOptimizer ---------------------------------------------------------

    [Fact]
    public void Hrp_TwoAssets_FavorsLowVolatility_LikeInverseVariance()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["LowVol"] = ScaledNoise(1500, 0.01, seed: 1),
            ["HighVol"] = ScaledNoise(1500, 0.03, seed: 2),
        };
        var hrp = new HierarchicalRiskParityOptimizer(new HierarchicalClustering());

        var result = hrp.Optimize(returns);

        Assert.True(result.Weights["LowVol"] > result.Weights["HighVol"],
            $"LowVol={result.Weights["LowVol"]:F3} HighVol={result.Weights["HighVol"]:F3}");
        AssertWeightsSumToOne(result);
    }

    [Fact]
    public void Hrp_MultipleAssets_AllWeightsPositiveAndSumToOne()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = ScaledNoise(1000, 0.01, seed: 1),
            ["B"] = ScaledNoise(1000, 0.015, seed: 2),
            ["C"] = ScaledNoise(1000, 0.02, seed: 3),
            ["D"] = ScaledNoise(1000, 0.025, seed: 4),
        };
        var hrp = new HierarchicalRiskParityOptimizer(new HierarchicalClustering());

        var result = hrp.Optimize(returns);

        Assert.Equal(4, result.Weights.Count);
        Assert.All(result.Weights.Values, w => Assert.True(w > 0m, $"peso non positivo: {w}"));
        AssertWeightsSumToOne(result);
    }

    [Fact]
    public void Hrp_TooFewSymbols_Throws()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>> { ["A"] = ScaledNoise(100, 0.01, 1) };
        var hrp = new HierarchicalRiskParityOptimizer(new HierarchicalClustering());
        Assert.Throws<ArgumentException>(() => hrp.Optimize(returns));
    }

    private static void AssertWeightsSumToOne(PortfolioAllocation allocation)
    {
        var sum = allocation.Weights.Values.Sum();
        Assert.True(Math.Abs(sum - 1m) < 0.001m, $"somma pesi = {sum}");
    }
}
