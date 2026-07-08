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

        // 4. Primary path: a meaningful Sharpe improvement above the hysteresis band.
        if (sharpeImprovementPct >= options.MinSharpeImprovementPercent)
        {
            result.ShouldReplace = true;
            result.Reason = $"Ensemble sostituito: Sharpe medio {candidate.WeightedAverageSharpe:F2} vs {current.WeightedAverageSharpe:F2} (+{result.SharpeImprovementPercent:F1}%, sopra la soglia {options.MinSharpeImprovementPercent:F0}%).";
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
}
