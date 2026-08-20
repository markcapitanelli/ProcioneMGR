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

    // ------------------------------------------------------------------ significatività dello swap
    //
    // [A4, 2026-08-20] Il campione del test è la DURATA dell'holdout, non il conteggio trade: lo
    // Sharpe è annualizzato e l'errore standard di Lo vuole T nella stessa frequenza. Prima qui si
    // passava il numero di trade, e un test costruito su quel numero restava verde con l'unità
    // sbagliata — è la ragione per cui questi casi si scrivono ora sui mesi.

    private static EnsembleSummary SummaryWindow(decimal sharpe, int legs, int symbols, decimal? holdoutMonths, int trades = 40)
    {
        var s = Summary(sharpe, legs, symbols);
        s.HoldoutMonths = holdoutMonths;
        s.Observations = trades;
        return s;
    }

    [Fact]
    public void LargeImprovement_ShortWindow_NotSignificant_Keeps()
    {
        // +50% di Sharpe ma su 4 mesi di holdout: z = 0,5 × √(4/12) = 0,29 < 0,35 → rumore.
        var c = Make().Compare(Summary(1.0m, 2, 2), SummaryWindow(1.5m, 2, 2, holdoutMonths: 4m));
        Assert.False(c.ShouldReplace);
        Assert.Contains("non significativo", c.Reason);
        Assert.True(c.SignificanceZ < 0.35m, $"z={c.SignificanceZ}");
    }

    [Fact]
    public void SameImprovement_LongWindow_Significant_Replaces()
    {
        // Stesso +50% ma su 18 mesi: z = 0,5 × √1,5 = 0,61 ≥ 0,35 → scambio giustificato.
        var c = Make().Compare(Summary(1.0m, 2, 2), SummaryWindow(1.5m, 2, 2, holdoutMonths: 18m));
        Assert.True(c.ShouldReplace);
        Assert.True(c.SignificanceZ >= 0.35m, $"z={c.SignificanceZ}");
    }

    [Fact]
    public void UnknownWindow_FallsBackToHysteresisOnly()
    {
        // HoldoutMonths null (raccomandazione storica) → gate inattivo → decide la sola isteresi.
        var c = Make().Compare(Summary(1.0m, 2, 2), SummaryWindow(1.5m, 2, 2, holdoutMonths: null));
        Assert.True(c.ShouldReplace);
    }

    [Fact]
    public void TradeCount_AloneDoesNotActivateTheGate()
    {
        // Il conteggio trade non è più il campione del test: da solo non accende il gate.
        // Senza questa asserzione l'unità vecchia potrebbe rientrare senza che nessuno se ne accorga.
        var candidate = Summary(1.5m, 2, 2);
        candidate.Observations = 4;               // campione "sottile" nell'unità sbagliata
        var c = Make().Compare(Summary(1.0m, 2, 2), candidate);
        Assert.True(c.ShouldReplace);
        Assert.Equal(0m, c.SignificanceZ);
    }

    [Theory]
    // z = ΔSharpe × √(mesi/12), verificato contro il calcolo a mano.
    [InlineData(1.0, 12, 1.00)]
    [InlineData(0.5, 3, 0.25)]
    [InlineData(0.61, 4, 0.35)]
    [InlineData(1.73, 4, 1.00)]
    public void SharpeAdvantageZ_ScalesWithTheSquareRootOfTheWindow(double delta, int months, double atteso)
    {
        var z = EnsembleComparator.SharpeAdvantageZ(1.0m + (decimal)delta, 1.0m, months);
        Assert.Equal(atteso, (double)z, precision: 2);
    }

    [Fact]
    public void SharpeAdvantageZ_IsIndependentOfTheSharpeLevel()
    {
        // La forma 1/√Y non dipende dal livello dello Sharpe: due coppie con lo stesso Δ e la stessa
        // finestra danno lo stesso z. È la semplificazione dichiarata nel commento della funzione.
        var basso = EnsembleComparator.SharpeAdvantageZ(0.5m, 0.0m, 6m);
        var alto = EnsembleComparator.SharpeAdvantageZ(2.5m, 2.0m, 6m);
        Assert.Equal((double)basso, (double)alto, precision: 6);
    }

    [Fact]
    public void SharpeAdvantageZ_NonPositiveWindow_IsNeutral()
    {
        Assert.Equal(0m, EnsembleComparator.SharpeAdvantageZ(2.0m, 1.0m, null));
        Assert.Equal(0m, EnsembleComparator.SharpeAdvantageZ(2.0m, 1.0m, 0m));
        Assert.Equal(0m, EnsembleComparator.SharpeAdvantageZ(2.0m, 1.0m, -3m));
    }
}
