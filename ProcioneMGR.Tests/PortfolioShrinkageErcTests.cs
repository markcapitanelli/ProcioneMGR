using MathNet.Numerics.LinearAlgebra;
using ProcioneMGR.Services.Portfolio;

namespace ProcioneMGR.Tests;

/// <summary>
/// E1 — potenziamenti portafoglio: covarianza <b>Ledoit-Wolf</b> (shrinkage verso μI, ben condizionata)
/// e <b>Equal Risk Contribution</b> ESATTO (coordinate cyclical, tiene conto delle correlazioni).
/// Verifica le proprietà matematiche degli stimatori in isolamento e che gli allocatori li usino.
/// </summary>
public class PortfolioShrinkageErcTests
{
    /// <summary>Rendimenti sintetici correlati: due asset "gemelli" (ρ≈0.9) + uno indipendente.</summary>
    private static Dictionary<string, IReadOnlyList<decimal>> CorrelatedReturns(int n, int seed)
    {
        var rnd = new Random(seed);
        var a = new List<decimal>(n);
        var b = new List<decimal>(n);
        var c = new List<decimal>(n);
        for (var i = 0; i < n; i++)
        {
            var common = rnd.NextDouble() - 0.5;
            var ia = rnd.NextDouble() - 0.5;
            var ib = rnd.NextDouble() - 0.5;
            a.Add((decimal)(0.02 * common + 0.006 * ia));           // A e B condividono "common"
            b.Add((decimal)(0.02 * common + 0.006 * ib));
            c.Add((decimal)(0.02 * (rnd.NextDouble() - 0.5)));       // C indipendente
        }
        return new() { ["A"] = a, ["B"] = b, ["C"] = c };
    }

    private static Matrix<double> MatrixOf(Dictionary<string, IReadOnlyList<decimal>> r)
    {
        var keys = r.Keys.ToList();
        var n = r[keys[0]].Count;
        return Matrix<double>.Build.Dense(n, keys.Count, (row, col) => (double)r[keys[col]][row]);
    }

    // --- Ledoit-Wolf ---------------------------------------------------------------------------

    [Fact]
    public void LedoitWolf_ShrinkageIntensity_InUnitInterval()
    {
        var (_, delta) = PortfolioMath.LedoitWolf(MatrixOf(CorrelatedReturns(200, 1)));
        Assert.InRange(delta, 0.0, 1.0);
        Assert.True(delta > 0.0, $"con dati rumorosi ci si attende shrinkage > 0 (δ={delta})");
    }

    [Fact]
    public void LedoitWolf_ShrinksOffDiagonalsTowardZero()
    {
        var m = MatrixOf(CorrelatedReturns(150, 2));
        var sample = PortfolioMath.Covariance(m);
        var (shrunk, delta) = PortfolioMath.LedoitWolf(m);

        // Il target è diagonale (μI): lo shrinkage riduce in modulo le covarianze fuori diagonale.
        Assert.True(delta > 0);
        Assert.True(Math.Abs(shrunk[0, 1]) < Math.Abs(sample[0, 1]),
            $"off-diagonal shrunk |{shrunk[0, 1]:E2}| < sample |{sample[0, 1]:E2}|");
    }

    [Fact]
    public void LedoitWolf_FewObservations_ShrinksMoreThanMany()
    {
        // Meno osservazioni ⇒ S più rumorosa ⇒ shrinkage maggiore.
        var few = PortfolioMath.LedoitWolf(MatrixOf(CorrelatedReturns(20, 3))).Shrinkage;
        var many = PortfolioMath.LedoitWolf(MatrixOf(CorrelatedReturns(2000, 3))).Shrinkage;
        Assert.True(few > many, $"few(20)={few:F3} deve superare many(2000)={many:F3}");
    }

    // --- Equal Risk Contribution -----------------------------------------------------------------

    [Fact]
    public void Erc_EqualizesRiskContributions()
    {
        var cov = PortfolioMath.LedoitWolf(MatrixOf(CorrelatedReturns(500, 4))).Covariance;
        var w = PortfolioMath.EqualRiskContribution(cov);

        Assert.Equal(1.0, w.Sum(), 6);

        // RC_i = w_i·(Σw)_i devono essere ~uguali fra loro.
        var sigmaW = cov * Vector<double>.Build.Dense(w);
        var rc = Enumerable.Range(0, w.Length).Select(i => w[i] * sigmaW[i]).ToArray();
        var mean = rc.Average();
        Assert.All(rc, x => Assert.True(Math.Abs(x - mean) / mean < 0.02, $"RC={x:E3} vs media {mean:E3}"));
    }

    [Fact]
    public void Erc_CorrelatedPair_GetsLessThanInverseVolWould()
    {
        // A e B sono correlati: l'ERC esatto li penalizza (contribuiscono rischio ridondante) rispetto
        // all'inverse-vol che ignora la correlazione. Atteso: w(C indipendente) > w(A).
        var r = CorrelatedReturns(1000, 5);
        var cov = PortfolioMath.LedoitWolf(MatrixOf(r)).Covariance;
        var w = PortfolioMath.EqualRiskContribution(cov); // ordine A,B,C

        Assert.True(w[2] > w[0], $"C indipendente ({w[2]:F3}) deve pesare più di A correlato ({w[0]:F3})");
    }

    [Fact]
    public void Erc_UncorrelatedEqualVol_GivesEqualWeights()
    {
        // A correlazioni nulle e volatilità uguali, ERC == equipeso.
        var rnd = new Random(6);
        var r = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["X"] = Enumerable.Range(0, 1000).Select(_ => (decimal)((rnd.NextDouble() - 0.5) * 0.02)).ToList(),
            ["Y"] = Enumerable.Range(0, 1000).Select(_ => (decimal)((rnd.NextDouble() - 0.5) * 0.02)).ToList(),
            ["Z"] = Enumerable.Range(0, 1000).Select(_ => (decimal)((rnd.NextDouble() - 0.5) * 0.02)).ToList(),
        };
        var w = PortfolioMath.EqualRiskContribution(PortfolioMath.Covariance(MatrixOf(r)));
        Assert.All(w, x => Assert.True(Math.Abs(x - 1.0 / 3.0) < 0.05, $"peso {x:F3} ~ 0.333"));
    }

    // --- Integrazione negli allocatori -----------------------------------------------------------

    [Fact]
    public void RiskParity_ErcMode_PrefersUncorrelatedAsset_OverInverseVol()
    {
        var r = CorrelatedReturns(1000, 7);
        var erc = new RiskParityOptimizer().Optimize(r,
            new PortfolioOptimizationConfig { RiskParityMethod = RiskParityMethod.EqualRiskContribution });
        var invVol = new RiskParityOptimizer().Optimize(r,
            new PortfolioOptimizationConfig { RiskParityMethod = RiskParityMethod.InverseVolatility });

        // L'ERC dà più peso all'asset indipendente C rispetto all'inverse-vol (che ignora la correlazione).
        Assert.True(erc.Weights["C"] > invVol.Weights["C"],
            $"ERC C={erc.Weights["C"]:F3} > invVol C={invVol.Weights["C"]:F3}");
        Assert.True(Math.Abs(erc.Weights.Values.Sum() - 1m) < 0.001m);
    }
}
