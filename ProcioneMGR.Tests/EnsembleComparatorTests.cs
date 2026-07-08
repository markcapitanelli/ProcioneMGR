using ProcioneMGR.Services.Ensemble;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica il confronto oggettivo "nuovo ensemble vs corrente" con hysteresis: sostituzione solo
/// per miglioramenti reali (Sharpe sopra soglia o RF95 nettamente migliore a Sharpe non inferiore),
/// niente cambi per rumore, e il floor strutturale (minimo gambe / simboli distinti).
/// </summary>
public class EnsembleComparatorTests
{
    private static EnsembleComparator Make(EnsembleComparatorOptions? o = null) => new(o ?? new EnsembleComparatorOptions());

    private static EnsembleSummary Summary(decimal sharpe, int legs, int symbols, decimal rf = 0m)
    {
        var list = new List<LegSummary>();
        for (var i = 0; i < legs; i++)
        {
            list.Add(new LegSummary { Symbol = $"SYM{i % Math.Max(1, symbols)}/USDT", Sharpe = sharpe, WeightPercent = 100m / legs });
        }
        return new EnsembleSummary
        {
            WeightedAverageSharpe = sharpe,
            WeightedAverageRiskFactor95 = rf,
            SurvivingLegs = legs,
            DistinctSymbols = symbols,
            Legs = list,
        };
    }

    [Fact]
    public void BetterCandidate_AboveHysteresis_Replaces()
    {
        var c = Make().Compare(Summary(1.0m, 2, 2), Summary(1.5m, 2, 2));
        Assert.True(c.ShouldReplace);
        Assert.Equal(0.5m, c.SharpeDelta);
    }

    [Fact]
    public void WorseCandidate_Keeps()
    {
        var c = Make().Compare(Summary(1.0m, 2, 2), Summary(0.8m, 2, 2));
        Assert.False(c.ShouldReplace);
    }

    [Fact]
    public void MarginalImprovement_BelowHysteresis_Keeps()
    {
        var c = Make().Compare(Summary(1.00m, 2, 2), Summary(1.05m, 2, 2)); // +5% < 10%
        Assert.False(c.ShouldReplace);
    }

    [Fact]
    public void NoCurrentEnsemble_AppliesFirst()
    {
        Assert.True(Make().Compare(null, Summary(0.9m, 2, 2)).ShouldReplace);
        Assert.True(Make().Compare(new EnsembleSummary(), Summary(0.9m, 2, 2)).ShouldReplace);
    }

    [Fact]
    public void CandidateBelowMinLegs_Rejected()
    {
        var c = Make().Compare(Summary(0.5m, 2, 2), Summary(3.0m, 1, 1));
        Assert.False(c.ShouldReplace);
        Assert.Contains("gambe", c.Reason);
    }

    [Fact]
    public void CandidateBelowMinDistinctSymbols_Rejected()
    {
        var c = Make().Compare(Summary(0.5m, 2, 2), Summary(3.0m, 2, 1));
        Assert.False(c.ShouldReplace);
        Assert.Contains("simboli", c.Reason);
    }

    [Fact]
    public void SaferRiskFactor_AtEqualSharpe_Replaces()
    {
        // Sharpe identico ma RF95 candidato molto piu' basso (-40%) → sostituisce sul rischio.
        var current = Summary(1.0m, 2, 2, rf: 2.0m);
        var candidate = Summary(1.0m, 2, 2, rf: 1.2m);
        var c = Make().Compare(current, candidate);
        Assert.True(c.ShouldReplace);
        Assert.True(c.RiskFactorDelta < 0m);
    }

    [Fact]
    public void SharpeFromNonPositiveBase_AnyPositive_Replaces()
    {
        var c = Make().Compare(Summary(-0.2m, 2, 2), Summary(0.4m, 2, 2));
        Assert.True(c.ShouldReplace);
    }
}
