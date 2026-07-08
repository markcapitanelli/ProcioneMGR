using ProcioneMGR.Services.Validation;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test della libreria di rigore statistico (Fase 1): Deflated/Probabilistic Sharpe, Combinatorial
/// Purged CV e Probability of Backtest Overfitting. Verifica identità esatte note dalla letteratura,
/// monotonicità e i due comportamenti-cardine: pannello di rumore ⇒ PBO≈0.5 e DSR non significativo;
/// edge reale e persistente ⇒ PBO≈0 e DSR alto. Tutto deterministico (RNG seedato).
/// </summary>
public class ValidationStatisticsTests
{
    // --- ReturnMoments -----------------------------------------------------------------------

    [Fact]
    public void PerPeriodSharpe_ZeroVariance_ReturnsZero()
    {
        Assert.Equal(0.0, ReturnMoments.PerPeriodSharpe(new[] { 2.0, 2.0, 2.0 }));
    }

    [Fact]
    public void PerPeriodSharpe_ZeroMean_ReturnsZero()
    {
        Assert.Equal(0.0, ReturnMoments.PerPeriodSharpe(new[] { 1.0, -1.0, 1.0, -1.0 }), 10);
    }

    [Fact]
    public void Skewness_SymmetricSeries_IsApproximatelyZero()
    {
        var skew = ReturnMoments.Skewness(new[] { -2.0, -1.0, 0.0, 1.0, 2.0 });
        Assert.True(Math.Abs(skew) < 1e-9, $"atteso ~0, ottenuto {skew}");
    }

    [Fact]
    public void Kurtosis_DegenerateSeries_ReturnsGaussianValue()
    {
        Assert.Equal(3.0, ReturnMoments.Kurtosis(new[] { 5.0, 5.0 }));
    }

    // --- Probabilistic / Deflated Sharpe -----------------------------------------------------

    [Fact]
    public void ProbabilisticSharpe_BenchmarkEqualsObserved_IsExactlyHalf()
    {
        // z = 0 ⇒ Φ(0) = 0.5, indipendentemente da T e dai momenti.
        var psr = DeflatedSharpeRatio.ProbabilisticSharpe(observedSharpe: 0.2, benchmarkSharpe: 0.2, observations: 250, skewness: 0.0, kurtosis: 3.0);
        Assert.Equal(0.5, psr, 10);
    }

    [Fact]
    public void ProbabilisticSharpe_IsMonotonicInObservedSharpe()
    {
        var low = DeflatedSharpeRatio.ProbabilisticSharpe(0.05, 0.0, 250, 0.0, 3.0);
        var high = DeflatedSharpeRatio.ProbabilisticSharpe(0.20, 0.0, 250, 0.0, 3.0);
        Assert.True(high > low);
        Assert.InRange(low, 0.0, 1.0);
        Assert.InRange(high, 0.0, 1.0);
    }

    [Fact]
    public void ExpectedMaxSharpe_SingleTrial_IsZero()
    {
        Assert.Equal(0.0, DeflatedSharpeRatio.ExpectedMaxSharpe(varianceOfTrialSharpes: 0.04, trials: 1));
    }

    [Fact]
    public void ExpectedMaxSharpe_GrowsWithNumberOfTrials()
    {
        var few = DeflatedSharpeRatio.ExpectedMaxSharpe(0.04, 10);
        var many = DeflatedSharpeRatio.ExpectedMaxSharpe(0.04, 1000);
        Assert.True(many > few, $"più tentativi ⇒ soglia più alta (few={few}, many={many})");
        Assert.True(few > 0);
    }

    [Fact]
    public void Deflated_MoreTrials_LowersTheDeflatedSharpe()
    {
        // Stesso Sharpe osservato: con più tentativi la soglia SR* sale ⇒ DSR scende (più difficile
        // che l'edge sia reale invece di un artefatto della ricerca).
        const double observed = 0.18, variance = 0.02;
        var withFew = DeflatedSharpeRatio.Deflated(observed, observations: 500, skewness: 0.0, kurtosis: 3.0, varianceOfTrialSharpes: variance, trials: 5);
        var withMany = DeflatedSharpeRatio.Deflated(observed, observations: 500, skewness: 0.0, kurtosis: 3.0, varianceOfTrialSharpes: variance, trials: 5000);
        Assert.True(withMany < withFew, $"few={withFew}, many={withMany}");
    }

    [Fact]
    public void Deflated_StrongEdgeFewTrials_IsSignificant()
    {
        // Sharpe per-periodo alto, track record lungo, pochi tentativi, poca varianza cross-trial.
        var dsr = DeflatedSharpeRatio.Deflated(observedSharpe: 0.25, observations: 1000, skewness: 0.0, kurtosis: 3.0, varianceOfTrialSharpes: 0.005, trials: 5);
        Assert.True(dsr > 0.95, $"atteso significativo (>0.95), ottenuto {dsr}");
    }

    [Fact]
    public void Deflated_IsDeterministic()
    {
        var a = DeflatedSharpeRatio.Deflated(0.15, 400, 0.1, 3.5, 0.01, 100);
        var b = DeflatedSharpeRatio.Deflated(0.15, 400, 0.1, 3.5, 0.01, 100);
        Assert.Equal(a, b);
    }

    // --- Combinatorial Purged CV -------------------------------------------------------------

    [Fact]
    public void Cpcv_NumberOfSplits_EqualsBinomialCoefficient()
    {
        var cpcv = new CombinatorialPurgedCv();
        var splits = cpcv.Split(sampleCount: 120, groups: 6, testGroups: 2, purgeWindow: 0, embargoPeriods: 0);
        Assert.Equal(15, splits.Count); // C(6,2) = 15
        Assert.All(splits, s => Assert.Equal(2, s.TestGroups.Count));
    }

    [Fact]
    public void Cpcv_TrainAndTest_NeverOverlap()
    {
        var cpcv = new CombinatorialPurgedCv();
        var splits = cpcv.Split(120, 6, 2, purgeWindow: 3, embargoPeriods: 2);
        foreach (var s in splits)
        {
            var train = s.TrainIndices.ToHashSet();
            foreach (var t in s.TestIndices) Assert.DoesNotContain(t, train);
        }
    }

    [Fact]
    public void Cpcv_PurgeAndEmbargo_ExcludeBandsAroundTestGroups()
    {
        var cpcv = new CombinatorialPurgedCv();
        // 120 campioni, 6 gruppi da 20: gruppo 0=[0,20), gruppo 2=[40,60).
        var splits = cpcv.Split(120, 6, 2, purgeWindow: 3, embargoPeriods: 2);
        // Split con test = gruppi {0,2}: embargo dopo il gruppo 0 esclude [20,22); purge prima del
        // gruppo 2 esclude [37,40); embargo dopo il gruppo 2 esclude [60,62).
        var split = splits.Single(s => s.TestGroups.SequenceEqual(new[] { 0, 2 }));
        var train = split.TrainIndices.ToHashSet();
        Assert.DoesNotContain(20, train);
        Assert.DoesNotContain(21, train);
        Assert.DoesNotContain(38, train);
        Assert.DoesNotContain(60, train);
        Assert.DoesNotContain(61, train);
        Assert.Contains(22, train); // fuori dall'embargo del gruppo 0
        Assert.Contains(62, train); // fuori dall'embargo del gruppo 2
    }

    [Fact]
    public void Cpcv_InvalidTestGroups_Throws()
    {
        var cpcv = new CombinatorialPurgedCv();
        Assert.Throws<ArgumentOutOfRangeException>(() => cpcv.Split(120, 6, 6, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => cpcv.Split(120, 6, 0, 0, 0));
    }

    [Fact]
    public void Combinations_AreDistinctSortedAndLexicographic()
    {
        var combos = CombinatorialPurgedCv.Combinations(5, 2).ToList();
        Assert.Equal(10, combos.Count); // C(5,2)
        // Ogni combinazione è ordinata; nessun duplicato.
        Assert.All(combos, c => Assert.True(c[0] < c[1]));
        var asStrings = combos.Select(c => string.Join(",", c)).ToList();
        Assert.Equal(asStrings.Distinct().Count(), asStrings.Count);
        Assert.Equal("0,1", asStrings[0]);   // primo lessicografico
        Assert.Equal("3,4", asStrings[^1]);  // ultimo lessicografico
    }

    // --- SelectionValidator (aggancio annualizzato → per-periodo, come negli engine) ----------

    [Fact]
    public void SelectionValidator_StrongChosenFewTrials_IsSignificant()
    {
        // Rendimenti periodici del candidato scelto con Sharpe per-periodo chiaramente positivo.
        var rng = new Random(3);
        var chosen = new double[1000];
        for (var i = 0; i < chosen.Length; i++) chosen[i] = 0.002 + 0.004 * NextGaussian(rng); // Sharpe/periodo ~0.5
        // Pochi trial, Sharpe annualizzati con poca dispersione.
        var trials = new decimal[] { 1.1m, 1.0m, 0.9m, 1.2m, 0.95m };

        var v = SelectionValidator.Validate(trials, chosen, periodsPerYear: 365, trials: 5);
        Assert.True(v.IsSignificant, $"atteso significativo, DSR={v.DeflatedSharpe}");
        Assert.Equal(1000, v.Observations);
        Assert.Equal(5, v.Trials);
    }

    [Fact]
    public void SelectionValidator_MoreTrials_LowerDeflatedSharpe()
    {
        // Sharpe osservato modesto (zona sensibile, non saturata a 1.0) e trial con vera dispersione:
        // aumentando N la soglia SR* sale e il DSR cala in modo misurabile.
        var rng = new Random(3);
        var chosen = new double[1000];
        for (var i = 0; i < chosen.Length; i++) chosen[i] = 0.0004 + 0.01 * NextGaussian(rng); // Sharpe/periodo ~0.04
        var trials = new decimal[] { 2.0m, -1.0m, 0.5m, 1.5m, -0.5m, 1.0m };

        var few = SelectionValidator.Validate(trials, chosen, 365);                 // N = 6
        var many = SelectionValidator.Validate(trials, chosen, 365, trials: 20000);
        Assert.True(many.DeflatedSharpe < few.DeflatedSharpe, $"few={few.DeflatedSharpe}, many={many.DeflatedSharpe}");
        Assert.True(many.ExpectedMaxSharpePerPeriod > few.ExpectedMaxSharpePerPeriod);
    }

    // --- Probability of Backtest Overfitting (PBO via CSCV) ----------------------------------

    [Fact]
    public void Pbo_PureNoisePanel_IsNearOneHalf()
    {
        // 24 strategie di puro rumore i.i.d.: nessun edge ⇒ la scelta del migliore IS è casuale,
        // il suo rango OOS è ~uniforme ⇒ PBO ≈ 0.5.
        var rng = new Random(12345);
        var panel = new List<IReadOnlyList<double>>();
        for (var s = 0; s < 24; s++)
        {
            var series = new double[300];
            for (var i = 0; i < series.Length; i++) series[i] = NextGaussian(rng);
            panel.Add(series);
        }
        var result = BacktestOverfitting.ProbabilityOfOverfitting(panel, partitions: 10);
        Assert.InRange(result.ProbabilityOfBacktestOverfitting, 0.30, 0.70);
        Assert.Equal(252, result.Combinations); // C(10,5)
    }

    [Fact]
    public void Pbo_OnePersistentEdge_IsLow()
    {
        // Una strategia con edge positivo costante in ogni periodo + rumore; le altre puro rumore.
        // Il migliore IS è quasi sempre quello con l'edge, che domina anche OOS ⇒ PBO basso.
        var rng = new Random(999);
        var panel = new List<IReadOnlyList<double>>();
        var edge = new double[300];
        for (var i = 0; i < edge.Length; i++) edge[i] = 0.5 + 0.2 * NextGaussian(rng); // media 0.5, Sharpe alto
        panel.Add(edge);
        for (var s = 0; s < 23; s++)
        {
            var series = new double[300];
            for (var i = 0; i < series.Length; i++) series[i] = NextGaussian(rng);
            panel.Add(series);
        }
        var result = BacktestOverfitting.ProbabilityOfOverfitting(panel, partitions: 10);
        Assert.True(result.ProbabilityOfBacktestOverfitting < 0.10, $"atteso basso, ottenuto {result.ProbabilityOfBacktestOverfitting}");
    }

    [Fact]
    public void Pbo_IsDeterministic_ForSameInput()
    {
        var rng1 = new Random(7);
        var rng2 = new Random(7);
        var panel1 = BuildNoisePanel(rng1, strategies: 12, length: 200);
        var panel2 = BuildNoisePanel(rng2, strategies: 12, length: 200);
        var r1 = BacktestOverfitting.ProbabilityOfOverfitting(panel1, 8);
        var r2 = BacktestOverfitting.ProbabilityOfOverfitting(panel2, 8);
        Assert.Equal(r1.ProbabilityOfBacktestOverfitting, r2.ProbabilityOfBacktestOverfitting);
    }

    [Theory]
    [InlineData(3)]  // dispari
    [InlineData(5)]  // dispari
    public void Pbo_InvalidPartitions_Throws(int partitions)
    {
        var rng = new Random(1);
        var panel = BuildNoisePanel(rng, 6, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestOverfitting.ProbabilityOfOverfitting(panel, partitions));
    }

    // --- helper --------------------------------------------------------------------------------

    private static List<IReadOnlyList<double>> BuildNoisePanel(Random rng, int strategies, int length)
    {
        var panel = new List<IReadOnlyList<double>>();
        for (var s = 0; s < strategies; s++)
        {
            var series = new double[length];
            for (var i = 0; i < length; i++) series[i] = NextGaussian(rng);
            panel.Add(series);
        }
        return panel;
    }

    /// <summary>Normale standard via Box–Muller (deterministica dato il seed).</summary>
    private static double NextGaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
