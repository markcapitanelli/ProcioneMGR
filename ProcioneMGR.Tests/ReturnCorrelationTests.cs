using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Portfolio;

namespace ProcioneMGR.Tests;

/// <summary>
/// [T2, PRD memoria-caccia 2026-08-14] La formula di correlazione condivisa fra assemblaggio in
/// pipeline e pannello Ensemble. Livello 1: contro valori calcolati A MANO (riferimento
/// indipendente, non la stessa formula rieseguita). Livello 2: rumore indipendente non deve mai
/// superare la soglia di avviso.
/// </summary>
public sealed class ReturnCorrelationTests
{
    private static List<decimal> D(params double[] xs) => xs.Select(x => (decimal)x).ToList();

    // ------------------------------------------------------------------ livello 1: riferimento a mano

    [Fact]
    public void PerfectlyLinear_IsOne()
    {
        Assert.Equal(1.0, ReturnCorrelation.Pearson(D(1, 2, 3, 4), D(2, 4, 6, 8)), precision: 12);
        Assert.Equal(-1.0, ReturnCorrelation.Pearson(D(1, 2, 3, 4), D(8, 6, 4, 2)), precision: 12);
    }

    [Fact]
    public void HandComputedCase_MatchesExactly()
    {
        // a=[1..5] (media 3), b=[2,1,4,3,5] (media 3).
        // deviazioni: da=[-2,-1,0,1,2], db=[-1,-2,1,0,2]
        // cov = 2+2+0+0+4 = 8 · varA = 10 · varB = 10 ⇒ ρ = 8/10 = 0,8 esatto.
        Assert.Equal(0.8, ReturnCorrelation.Pearson(D(1, 2, 3, 4, 5), D(2, 1, 4, 3, 5)), precision: 12);
    }

    [Fact]
    public void ZeroVariance_IsZero_NeverNaN()
    {
        // Una gamba che non si muove non ha relazione dichiarabile: 0, mai NaN (un NaN
        // serializzato romperebbe il report della proposta a valle).
        Assert.Equal(0.0, ReturnCorrelation.Pearson(D(1, 1, 1, 1), D(1, 2, 3, 4)));
    }

    [Fact]
    public void MismatchedLengths_Throw() =>
        Assert.Throws<ArgumentException>(() => ReturnCorrelation.Pearson(D(1, 2), D(1, 2, 3)));

    [Fact]
    public void DailyReturns_TakesLastPointOfEachDay()
    {
        // Giorno 1: 10.000 → (intraday) → chiude 10.100. Giorno 2: chiude 10.302 (=+2%).
        // Il punto intraday da 9.000 NON deve contare: vale l'ultimo equity della giornata.
        var t0 = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var equity = new List<EquityPoint>
        {
            new() { Timestamp = t0, Capital = 10_000m },
            new() { Timestamp = t0.AddHours(3), Capital = 9_000m },
            new() { Timestamp = t0.AddHours(8), Capital = 10_100m },
            new() { Timestamp = t0.AddDays(1).AddHours(8), Capital = 10_302m },
        };
        var returns = ReturnCorrelation.DailyReturns(equity);
        Assert.Single(returns); // il primo giorno e' la base, non un rendimento
        Assert.Equal(0.02m, Math.Round(returns[t0.Date.AddDays(1)], 6));
    }

    [Fact]
    public void AllPairs_OrdersByAbsRho_Descending()
    {
        var a = D(1, 2, 3, 4, 5);
        var b = D(2, 4, 6, 8, 10);   // ρ(a,b)=1
        var c = D(2, 1, 4, 3, 5);    // ρ(a,c)=0,8
        var pairs = ReturnCorrelation.AllPairs([("A", "gamba A", a), ("B", "gamba B", b), ("C", "gamba C", c)]);

        Assert.Equal(3, pairs.Count);
        Assert.Equal(("A", "B"), (pairs[0].KeyA, pairs[0].KeyB)); // |1| prima di |0,8|
        Assert.True(Math.Abs(pairs[0].Rho) >= Math.Abs(pairs[1].Rho));
        Assert.True(Math.Abs(pairs[1].Rho) >= Math.Abs(pairs[2].Rho));
    }

    // ------------------------------------------------------------------ livello 2: il rumore tace

    [Fact]
    public void IndependentNoise_NeverCrossesWarnThreshold()
    {
        // Con 90 osservazioni, la deviazione standard di ρ su serie indipendenti e' ~1/√90 ≈ 0,105:
        // la soglia 0,7 sta a >6σ. Se questo test fallisse, o la formula e' rotta o la soglia
        // e' finita sotto il pavimento del rumore — entrambe cose da sapere subito.
        for (var seed = 0; seed < 10; seed++)
        {
            var rng = new Random(seed);
            var a = Enumerable.Range(0, 90).Select(_ => (decimal)(rng.NextDouble() - 0.5) / 100m).ToList();
            var b = Enumerable.Range(0, 90).Select(_ => (decimal)(rng.NextDouble() - 0.5) / 100m).ToList();
            var rho = ReturnCorrelation.Pearson(a, b);
            Assert.True(Math.Abs(rho) < ReturnCorrelation.DefaultWarnThreshold,
                $"seme {seed}: ρ={rho:F3} sopra la soglia {ReturnCorrelation.DefaultWarnThreshold} su rumore indipendente");
        }
    }
}
