using ProcioneMGR.Services.Ensemble;

namespace ProcioneMGR.Tests;

/// <summary>Test della pesatura vincolata (water-filling) dell'ensemble.</summary>
public class EnsembleAllocatorTests
{
    [Fact]
    public void SpecExample_RespectsConstraints_SumsToOne()
    {
        // Sharpe [1.5, 0.8, -0.2], Min 5%, Max 40%.
        // Lo Sharpe negativo -> 0; con Max=40% il risultato corretto è 40/40/20 (NON 40/45/15).
        var w = EnsembleAllocator.ComputeWeights([1.5m, 0.8m, -0.2m], 0.05m, 0.40m);

        Assert.Equal(3, w.Length);
        Assert.Equal(1.0, (double)w.Sum(), precision: 6);
        Assert.All(w, x => Assert.InRange(x, 0.05m - 0.0001m, 0.40m + 0.0001m));

        // I due Sharpe positivi vanno al cap, il terzo assorbe il resto.
        Assert.Equal(0.40, (double)w[0], precision: 4);
        Assert.Equal(0.40, (double)w[1], precision: 4);
        Assert.Equal(0.20, (double)w[2], precision: 4);
    }

    [Fact]
    public void AllZeroOrNegative_GivesEqualWeights()
    {
        var w = EnsembleAllocator.ComputeWeights([-1m, 0m, -0.5m], 0.05m, 0.40m);
        Assert.Equal(1.0, (double)w.Sum(), precision: 6);
        Assert.All(w, x => Assert.Equal(1.0 / 3, (double)x, precision: 4));
    }

    [Fact]
    public void SingleStrategy_GetsEverything()
    {
        var w = EnsembleAllocator.ComputeWeights([0.9m], 0.05m, 0.40m);
        Assert.Single(w);
        Assert.Equal(1.0, (double)w[0], precision: 6);
    }

    [Fact]
    public void HigherSharpe_GetsMoreCapital()
    {
        // Sharpe distinti, vincoli larghi -> proporzionale: il maggiore prende di più.
        var w = EnsembleAllocator.ComputeWeights([2.0m, 1.0m, 0.5m], 0.01m, 0.99m);
        Assert.Equal(1.0, (double)w.Sum(), precision: 6);
        Assert.True(w[0] > w[1]);
        Assert.True(w[1] > w[2]);
    }

    [Fact]
    public void RespectsMinFloor_ForZeroSharpeStrategy()
    {
        // Una strategia a 0 deve comunque ricevere almeno il minimo (non 0%).
        var w = EnsembleAllocator.ComputeWeights([1.5m, 1.5m, 0m], 0.10m, 0.45m);
        Assert.Equal(1.0, (double)w.Sum(), precision: 6);
        Assert.True(w[2] >= 0.10m - 0.0001m, $"w2={w[2]}");
    }
}
