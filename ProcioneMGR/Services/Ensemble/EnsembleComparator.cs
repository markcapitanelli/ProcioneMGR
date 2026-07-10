namespace ProcioneMGR.Services.Ensemble;

/// <summary>
/// Objective, deterministic comparison of two ensembles (the one currently deployed on the trading
/// lanes vs a candidate produced by a fresh pipeline run) for the continuous auto-reapply loop.
/// The decision is numeric only (no "gut feeling"): a candidate replaces the incumbent ONLY when it
/// is meaningfully better, gated by a configurable hysteresis so the deployed ensemble does not
/// churn on marginal, noise-level improvements. If there is no incumbent (first deployment), any
/// candidate that clears the structural floor (min legs / min distinct symbols) is accepted.
/// </summary>
public interface IEnsembleComparator
{
    /// <summary>Decides whether <paramref name="candidate"/> should replace <paramref name="current"/>. <paramref name="current"/> null/empty = first deployment.</summary>
    EnsembleComparison Compare(EnsembleSummary? current, EnsembleSummary candidate);
}

/// <summary>Tunable thresholds for <see cref="EnsembleComparator"/> (bound from the <c>EnsembleComparator</c> config section).</summary>
public sealed class EnsembleComparatorOptions
{
    /// <summary>Minimum weighted-Sharpe improvement (percent of the incumbent) required to replace — the hysteresis band.</summary>
    public decimal MinSharpeImprovementPercent { get; set; } = 10m;

    /// <summary>Minimum Monte-Carlo RiskFactor95 improvement (percent, lower is better) that can justify a swap on its own when Sharpe is not worse.</summary>
    public decimal MinRiskFactorImprovementPercent { get; set; } = 15m;

    /// <summary>A candidate with fewer surviving legs than this is rejected outright (too thin to deploy).</summary>
    public int MinLegs { get; set; } = 2;

    /// <summary>A candidate covering fewer distinct symbols than this is rejected outright (not diversified enough).</summary>
    public int MinDistinctSymbols { get; set; } = 2;

    /// <summary>
    /// Minimo z-score di significatività statistica del vantaggio di Sharpe del candidato sull'incumbent,
    /// oltre alla soglia percentuale di isteresi. Un miglioramento percentuale grande su un campione piccolo
    /// è rumore: pretendere che sia anche significativo evita di scambiare l'ensemble su differenze non
    /// distinguibili dal caso. z = (SR_cand − SR_incumbent) / SE(SR_cand), con SE di Lo (2002).
    /// Si attiva solo quando il candidato riporta <see cref="EnsembleSummary.Observations"/> &gt; 0
    /// (altrimenti si ricade sulla sola isteresi percentuale). Default 1.0 (≈1σ, modesto ma non nullo).
    /// </summary>
    public decimal MinSharpeSignificanceZ { get; set; } = 1.0m;
}

/// <summary>Compact, comparable snapshot of an ensemble (deployed or proposed). All metrics are weighted by allocation.</summary>
public sealed class EnsembleSummary
{
    /// <summary>Allocation-weighted average expected/holdout Sharpe across the surviving legs.</summary>
    public decimal WeightedAverageSharpe { get; set; }

    /// <summary>Allocation-weighted average Monte-Carlo RiskFactor95 (lower = safer). 0 = unknown (not recorded for this ensemble).</summary>
    public decimal WeightedAverageRiskFactor95 { get; set; }

    /// <summary>Number of active/surviving legs.</summary>
    public int SurvivingLegs { get; set; }

    /// <summary>Number of distinct symbols the legs span (diversification proxy).</summary>
    public int DistinctSymbols { get; set; }

    /// <summary>
    /// Effective sample size behind <see cref="WeightedAverageSharpe"/> (e.g. the weakest leg's holdout
    /// trade count) used to test the statistical significance of a swap. 0 = unknown → the significance
    /// gate is skipped and only the percentage hysteresis applies.
    /// </summary>
    public int Observations { get; set; }

    /// <summary>Per-leg breakdown (for logging/UI/debug).</summary>
    public IReadOnlyList<LegSummary> Legs { get; set; } = new List<LegSummary>();

    /// <summary>True when there is nothing meaningful to compare against (no legs).</summary>
    public bool IsEmpty => SurvivingLegs == 0 || Legs.Count == 0;
}

/// <summary>One leg of an <see cref="EnsembleSummary"/>.</summary>
public sealed class LegSummary
{
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public string StrategyName { get; set; } = string.Empty;
    public decimal WeightPercent { get; set; }
    public decimal Sharpe { get; set; }
    public decimal RiskFactor95 { get; set; }
}

/// <summary>Verdict of an ensemble comparison, with the numeric deltas that drove it (for transparent logging).</summary>
public sealed class EnsembleComparison
{
    public bool ShouldReplace { get; set; }

    /// <summary>Human-readable, Italian explanation of the verdict (logged + audited — never a silent decision).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>candidate.Sharpe - current.Sharpe (positive = candidate better).</summary>
    public decimal SharpeDelta { get; set; }

    /// <summary>candidate.RiskFactor95 - current.RiskFactor95 (negative = candidate better/safer).</summary>
    public decimal RiskFactorDelta { get; set; }

    /// <summary>Sharpe improvement as a percentage of the incumbent (for the hysteresis check).</summary>
    public decimal SharpeImprovementPercent { get; set; }

    /// <summary>
    /// z-score of the candidate's Sharpe advantage over the incumbent, given the candidate's sample size
    /// (0 when it could not be computed — unknown Observations or non-positive base). Recorded for audit.
    /// </summary>
    public decimal SignificanceZ { get; set; }
}

/// <inheritdoc cref="IEnsembleComparator"/>
public sealed class EnsembleComparator(EnsembleComparatorOptions options) : IEnsembleComparator
{
    public EnsembleComparison Compare(EnsembleSummary? current, EnsembleSummary candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // 1. Structural floor: a candidate that is too thin or not diversified enough is never
        // deployed, regardless of how good its Sharpe looks (avoids concentrating capital on a
        // single fragile leg just because it happened to score well on the holdout).
        if (candidate.SurvivingLegs < options.MinLegs)
        {
            return new EnsembleComparison
            {
                ShouldReplace = false,
                Reason = $"Candidato scartato: solo {candidate.SurvivingLegs} gambe (minimo {options.MinLegs}).",
            };
        }
        if (candidate.DistinctSymbols < options.MinDistinctSymbols)
        {
            return new EnsembleComparison
            {
                ShouldReplace = false,
                Reason = $"Candidato scartato: solo {candidate.DistinctSymbols} simboli distinti (minimo {options.MinDistinctSymbols}).",
            };
        }

        // 2. No incumbent (first deployment): accept the candidate that cleared the floor.
        if (current is null || current.IsEmpty)
        {
            return new EnsembleComparison
            {
                ShouldReplace = true,
                Reason = $"Nessun ensemble corrente: applico il candidato (Sharpe medio {candidate.WeightedAverageSharpe:F2}, {candidate.SurvivingLegs} gambe).",
                SharpeDelta = candidate.WeightedAverageSharpe,
            };
        }

        var sharpeDelta = candidate.WeightedAverageSharpe - current.WeightedAverageSharpe;
        var rfDelta = candidate.WeightedAverageRiskFactor95 - current.WeightedAverageRiskFactor95;

        // Sharpe improvement as a percentage of the incumbent. When the incumbent's Sharpe is not
        // positive, any positive candidate Sharpe is treated as a full improvement (the percentage
        // is undefined/meaningless against a non-positive base).
        decimal sharpeImprovementPct;
        if (current.WeightedAverageSharpe > 0m)
        {
            sharpeImprovementPct = sharpeDelta / current.WeightedAverageSharpe * 100m;
        }
        else
        {
            sharpeImprovementPct = candidate.WeightedAverageSharpe > 0m ? decimal.MaxValue : 0m;
        }

        var result = new EnsembleComparison
        {
            SharpeDelta = sharpeDelta,
            RiskFactorDelta = rfDelta,
            SharpeImprovementPercent = sharpeImprovementPct == decimal.MaxValue ? 100m : Math.Round(sharpeImprovementPct, 1),
        };

        // 3. Strictly worse on both axes → keep the incumbent, no question.
        var rfKnown = current.WeightedAverageRiskFactor95 > 0m && candidate.WeightedAverageRiskFactor95 > 0m;
        if (sharpeDelta < 0m && (!rfKnown || rfDelta >= 0m))
        {
            result.ShouldReplace = false;
            result.Reason = $"Ensemble corrente mantenuto: candidato peggiore (Sharpe {candidate.WeightedAverageSharpe:F2} vs {current.WeightedAverageSharpe:F2}).";
            return result;
        }

        // Significatività statistica del vantaggio di Sharpe (test a un campione: l'incumbent è il
        // benchmark nullo). Attiva solo se il candidato riporta una dimensione campionaria; altrimenti
        // z=0 e il gate è neutro (si ricade sulla sola isteresi percentuale).
        var significanceZ = SharpeAdvantageZ(candidate.WeightedAverageSharpe, current.WeightedAverageSharpe, candidate.Observations);
        result.SignificanceZ = Math.Round(significanceZ, 2);
        var significanceKnown = candidate.Observations > 0 && options.MinSharpeSignificanceZ > 0m;
        var significant = !significanceKnown || significanceZ >= options.MinSharpeSignificanceZ;

        // 4. Primary path: a meaningful Sharpe improvement above the hysteresis band AND, when the
        // sample size is known, one that is statistically distinguishable from noise.
        if (sharpeImprovementPct >= options.MinSharpeImprovementPercent)
        {
            if (!significant)
            {
                result.ShouldReplace = false;
                result.Reason = $"Ensemble corrente mantenuto: +{result.SharpeImprovementPercent:F1}% di Sharpe ma non significativo (z {result.SignificanceZ:F2} < {options.MinSharpeSignificanceZ:F2}, {candidate.Observations} osservazioni).";
                return result;
            }
            result.ShouldReplace = true;
            var zNote = significanceKnown ? $", z {result.SignificanceZ:F2} ≥ {options.MinSharpeSignificanceZ:F2}" : "";
            result.Reason = $"Ensemble sostituito: Sharpe medio {candidate.WeightedAverageSharpe:F2} vs {current.WeightedAverageSharpe:F2} (+{result.SharpeImprovementPercent:F1}%, sopra la soglia {options.MinSharpeImprovementPercent:F0}%{zNote}).";
            return result;
        }

        // 5. Secondary path: Sharpe is not worse AND the candidate is materially safer (RF95 down
        // by more than its own hysteresis band). Both RF values must be known for this to apply.
        if (rfKnown && sharpeDelta >= 0m)
        {
            var rfImprovementPct = (current.WeightedAverageRiskFactor95 - candidate.WeightedAverageRiskFactor95)
                                   / current.WeightedAverageRiskFactor95 * 100m;
            if (rfImprovementPct >= options.MinRiskFactorImprovementPercent)
            {
                result.ShouldReplace = true;
                result.Reason = $"Ensemble sostituito: rischio RF95 {candidate.WeightedAverageRiskFactor95:F2} vs {current.WeightedAverageRiskFactor95:F2} (-{rfImprovementPct:F1}%) a Sharpe non inferiore.";
                return result;
            }
        }

        // 6. Improvement too marginal to justify churning the deployed capital.
        result.ShouldReplace = false;
        result.Reason = $"Ensemble corrente mantenuto: miglioramento marginale (Sharpe {candidate.WeightedAverageSharpe:F2} vs {current.WeightedAverageSharpe:F2}, +{result.SharpeImprovementPercent:F1}% sotto la soglia {options.MinSharpeImprovementPercent:F0}%).";
        return result;
    }

    /// <summary>
    /// z-score del vantaggio di Sharpe del candidato sull'incumbent (test a un campione), usando
    /// l'errore standard asintotico dello Sharpe di Lo (2002): SE(SR) ≈ √((1 + ½·SR²) / T).
    /// Restituisce 0 se la dimensione campionaria è ignota/non positiva. Un campione più piccolo
    /// gonfia SE → z più basso → più difficile giustificare uno swap (esattamente l'intento anti-churn).
    /// </summary>
    internal static decimal SharpeAdvantageZ(decimal candidateSharpe, decimal incumbentSharpe, int observations)
    {
        if (observations <= 1)
        {
            return 0m;
        }
        var sr = (double)candidateSharpe;
        var se = Math.Sqrt((1.0 + 0.5 * sr * sr) / observations);
        if (se <= 0.0)
        {
            return 0m;
        }
        return (decimal)(((double)(candidateSharpe - incumbentSharpe)) / se);
    }
}
