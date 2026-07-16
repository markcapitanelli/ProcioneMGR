using MathNet.Numerics.LinearAlgebra;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Portfolio;

namespace ProcioneMGR.Tests;

/// <summary>
/// Audit FASE 1 — ottimizzatori di portafoglio su matrici di covarianza sintetiche DEGENERI:
/// non positive definite, esattamente singolari (asset duplicato), con asset a varianza zero,
/// rendimenti tutti costanti. Contratto atteso: mai eccezioni né NaN (un NaN esploderebbe nel
/// cast a decimal), pesi sempre normalizzati (somma 1) e dentro i vincoli Min/Max.
/// </summary>
public class AuditPortfolioDegenerateTests
{
    private static List<decimal> Noise(int n, double scale, int seed, double drift = 0.0)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => (decimal)(drift + (rnd.NextDouble() - 0.5) * 2 * scale)).ToList();
    }

    private static void AssertValidWeights(PortfolioAllocation alloc, int expectedCount, decimal min = 0m, decimal max = 1m)
    {
        Assert.Equal(expectedCount, alloc.Weights.Count);
        var sum = alloc.Weights.Values.Sum();
        Assert.InRange(sum, 0.999m, 1.001m);
        foreach (var (symbol, w) in alloc.Weights)
        {
            Assert.InRange(w, min - 0.0001m, max + 0.0001m);
            Assert.True(w >= 0m, $"{symbol}: peso negativo {w}");
        }
    }

    // --- Matrici NON positive definite (impossibili da rendimenti reali, possibili da input manuali/bug) ---

    [Fact]
    public void Erc_NonPositiveDefiniteCovariance_StaysFiniteAndNormalized()
    {
        // "Correlazione" 1.5 fuori diagonale: autovalori 2.5 e -0.5 -> NON PSD.
        var cov = Matrix<double>.Build.DenseOfArray(new[,] { { 1.0, 1.5 }, { 1.5, 1.0 } });
        var w = PortfolioMath.EqualRiskContribution(cov);

        Assert.All(w, x => Assert.True(double.IsFinite(x), $"peso non finito: {x}"));
        Assert.All(w, x => Assert.True(x >= 0.0, $"peso negativo: {x}"));
        Assert.Equal(1.0, w.Sum(), 6);
    }

    [Fact]
    public void Erc_IndefiniteThreeAssetMatrix_StaysFiniteAndNormalized()
    {
        // Struttura di correlazione internamente incoerente (violazione di transitività) -> indefinita.
        var cov = Matrix<double>.Build.DenseOfArray(new[,]
        {
            { 1.0,  0.9,  0.9 },
            { 0.9,  1.0, -0.9 },
            { 0.9, -0.9,  1.0 },
        });
        var w = PortfolioMath.EqualRiskContribution(cov);
        Assert.All(w, x => Assert.True(double.IsFinite(x) && x >= 0.0, $"peso invalido: {x}"));
        Assert.Equal(1.0, w.Sum(), 6);
    }

    [Fact]
    public void RiskContributions_NegativePortfolioVariance_FailsSafeToZeros()
    {
        // w'Σw = -0.5 < 0 con questa matrice indefinita: il contratto è restituire zeri, non NaN.
        var cov = Matrix<double>.Build.DenseOfArray(new[,] { { 1.0, -2.0 }, { -2.0, 1.0 } });
        var rc = PortfolioMath.RiskContributions(cov, new[] { 0.5, 0.5 });
        Assert.All(rc, x => Assert.Equal(0.0, x));
    }

    // --- Covarianza esattamente singolare: asset duplicato -------------------------------------

    [Fact]
    public void MeanVariance_DuplicatedAsset_SingularCovariance_AllEstimatorsAndObjectives_ProduceValidWeights()
    {
        var a = Noise(120, 0.02, seed: 7, drift: 0.001);
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = a,
            ["B"] = new List<decimal>(a), // clone perfetto -> covarianza ESATTAMENTE singolare
            ["C"] = Noise(120, 0.015, seed: 21, drift: 0.0005),
        };

        foreach (var estimator in new[] { CovarianceEstimator.Sample, CovarianceEstimator.LedoitWolf })
        {
            foreach (var objective in new[] { MeanVarianceObjective.MinVariance, MeanVarianceObjective.MaxSharpe })
            {
                var alloc = new MeanVarianceOptimizer().Optimize(returns, new PortfolioOptimizationConfig
                {
                    CovarianceEstimator = estimator,
                    Objective = objective,
                });
                AssertValidWeights(alloc, 3);
            }
        }
    }

    [Fact]
    public void AllOptimizers_ZeroVarianceAsset_NoThrow_ValidWeights()
    {
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["FLAT"] = Enumerable.Repeat(0m, 100).ToList(), // stablecoin/dato piatto: varianza 0
            ["A"] = Noise(100, 0.02, seed: 3),
            ["B"] = Noise(100, 0.03, seed: 5),
        };

        var optimizers = new IPortfolioOptimizer[]
        {
            new MeanVarianceOptimizer(),
            new RiskParityOptimizer(),
            new HierarchicalRiskParityOptimizer(new HierarchicalClustering()),
        };
        foreach (var opt in optimizers)
        {
            var alloc = opt.Optimize(returns);
            AssertValidWeights(alloc, 3);
        }

        // Anche la modalità inverse-volatility (score 0 per varianza nulla) deve restare valida.
        var iv = new RiskParityOptimizer().Optimize(returns, new PortfolioOptimizationConfig
        {
            RiskParityMethod = RiskParityMethod.InverseVolatility,
        });
        AssertValidWeights(iv, 3);
    }

    [Fact]
    public void MeanVariance_AllReturnsConstant_TotallyDegenerate_NoThrow()
    {
        // Tutti i rendimenti identicamente zero: covarianza nulla, media nulla.
        var returns = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["A"] = Enumerable.Repeat(0m, 50).ToList(),
            ["B"] = Enumerable.Repeat(0m, 50).ToList(),
            ["C"] = Enumerable.Repeat(0m, 50).ToList(),
        };
        foreach (var objective in new[] { MeanVarianceObjective.MinVariance, MeanVarianceObjective.MaxSharpe })
        {
            var alloc = new MeanVarianceOptimizer().Optimize(returns, new PortfolioOptimizationConfig { Objective = objective });
            AssertValidWeights(alloc, 3);
        }
    }

    [Fact]
    public void LedoitWolf_ConstantReturns_ShrinkageInUnitInterval_NoNaN()
    {
        var flat = Matrix<double>.Build.Dense(50, 3, 0.0);
        var (cov, delta) = PortfolioMath.LedoitWolf(flat);
        Assert.InRange(delta, 0.0, 1.0);
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                Assert.True(double.IsFinite(cov[i, j]), $"cov[{i},{j}] non finita");
            }
        }
    }

    // --- Vincoli Min/Max sotto stress ------------------------------------------------------------

    [Fact]
    public void AllOptimizers_HonorMinMaxBounds_OnCorrelatedUniverse()
    {
        // 6 asset fortemente correlati a coppie: stress per il water-filling vincolato.
        var rnd = new Random(11);
        var common = Enumerable.Range(0, 150).Select(_ => (rnd.NextDouble() - 0.5) * 0.03).ToArray();
        var returns = new Dictionary<string, IReadOnlyList<decimal>>();
        for (var k = 0; k < 6; k++)
        {
            var local = new Random(100 + k);
            var mult = 0.5 + k * 0.3;
            returns[$"S{k}"] = common.Select(c => (decimal)(c * mult + (local.NextDouble() - 0.5) * 0.005)).ToList();
        }

        var config = new PortfolioOptimizationConfig { MinWeight = 0.05m, MaxWeight = 0.40m };
        var optimizers = new IPortfolioOptimizer[]
        {
            new MeanVarianceOptimizer(),
            new RiskParityOptimizer(),
            new HierarchicalRiskParityOptimizer(new HierarchicalClustering()),
        };
        foreach (var opt in optimizers)
        {
            var alloc = opt.Optimize(returns, config);
            AssertValidWeights(alloc, 6, min: 0m, max: 0.40m); // min può legittimamente scendere a 0 per gli esclusi
            var sum = alloc.Weights.Values.Sum();
            Assert.InRange(sum, 0.999m, 1.001m);
        }
    }
}
