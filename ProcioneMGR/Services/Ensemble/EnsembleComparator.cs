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
    /// Si attiva solo quando il candidato riporta <see cref="EnsembleSummary.HoldoutMonths"/> &gt; 0
    /// (altrimenti si ricade sulla sola isteresi percentuale).
    ///
    /// [A4, 2026-08-20] Default portato da 1,0 a <b>0,35</b> INSIEME alla correzione del denominatore,
    /// e i due numeri vanno letti insieme. Prima il denominatore era il conteggio TRADE mentre lo
    /// Sharpe è annualizzato: SE risultava 4,5-20× troppo piccolo, quindi «z ≥ 1,0» era una soglia
    /// molto più permissiva di quanto la parola z lasciasse credere (punto operativo effettivo
    /// ΔSharpe ≈ 0,19, sotto il quale comandava comunque l'isteresi del 10%). Col denominatore giusto
    /// e un holdout di 4 mesi: z 1,0 ⇒ ΔSharpe ≥ 1,73 (la via-Sharpe si chiuderebbe di fatto);
    /// z 0,50 ⇒ ≥ 0,87; <b>z 0,35 ⇒ ≥ 0,61</b>; z 0,25 ⇒ ≥ 0,43. Scelto 0,35 dal proprietario: un
    /// freno che un ensemble davvero migliore può ancora superare, circa 3× più stretto del punto
    /// operativo di prima — senza cadere né nel churn né nel gate che non passa mai. Da rialzare
    /// quando l'holdout si allunga: la soglia e la finestra si scelgono insieme.
    /// </summary>
    public decimal MinSharpeSignificanceZ { get; set; } = 0.35m;
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
    /// [RF0, 2026-08-22] Almeno una gamba porta uno Sharpe atteso misurato con la <b>convenzione
    /// precedente</b> (risk-free 2% sottratto sull'equity totale). Vedi <c>MetricsConvention</c>.
    ///
    /// <para>Quando e' vero il confronto NON si fa: il candidato fresco guadagnerebbe ~0,5 punti di
    /// pura unita' di misura contro un'isteresi del 10% e un gate di significativita' che scatta a
    /// 0,55 — cioe' quasi esattamente il dazio mediano. La corsia si sblocca ri-applicando
    /// l'ensemble da /ensemble, che ri-misura le gambe con la convenzione corrente.</para>
    /// </summary>
    public bool HasLegacyMetrics { get; set; }

    /// <summary>
    /// Conteggio trade dell'holdout dietro <see cref="WeightedAverageSharpe"/> (il minimo fra le gambe).
    /// [A4, 2026-08-20] Serve SOLO al racconto: dire all'operatore quanto è spesso il campione. Non è
    /// più il denominatore del test di significatività, perché non è nell'unità dello Sharpe —
    /// per quello c'è <see cref="HoldoutMonths"/>.
    /// </summary>
    public int Observations { get; set; }

    /// <summary>
    /// [A4] Ampiezza della finestra di holdout in MESI dietro <see cref="WeightedAverageSharpe"/>.
    /// È il campione nell'unità giusta per l'errore standard di uno Sharpe ANNUALIZZATO: null =
    /// finestra ignota (raccomandazioni JSON storiche prive del campo) → il gate di significatività
    /// è saltato e decide la sola isteresi percentuale, come prima.
    /// </summary>
    public decimal? HoldoutMonths { get; set; }

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

        // [RF0, 2026-08-22] FAIL-CLOSED sul confronto fra CONVENZIONI DIVERSE.
        //
        // Le gambe schierate prima del 2026-08-22 portano uno Sharpe atteso col risk-free del 2%
        // dentro; il candidato fresco no. Il divario e' di PURA unita' di misura, e i numeri dicono
        // che nessuno dei presidi esistenti lo fermerebbe: sulle otto gambe schierate quel giorno la
        // media pesata sarebbe passata da 1,934 a ~2,395, cioe' +23,9% contro un'isteresi del 10%;
        // e il gate z scatta a un delta di 0,55 su 4,87 mesi di holdout mentre il dazio mediano per
        // candidato e' 0,545 — un margine del 2%, che e' una coincidenza, non un presidio.
        //
        // Sta QUI e non piu' in alto di proposito: il ramo «nessun incumbent» non ha nulla da
        // confrontare, quindi non ha il problema. Regola 4: in dubbio non si sostituisce, e si dice
        // perche'.
        if (current.HasLegacyMetrics)
        {
            return new EnsembleComparison
            {
                ShouldReplace = false,
                Reason = "Ensemble corrente mantenuto: le gambe schierate portano uno Sharpe atteso di convenzione "
                       + $"precedente al {Optimization.MetricsConvention.RiskFreeZeroSinceUtc:yyyy-MM-dd} (risk-free 2% sull'equity "
                       + "totale). Confrontarlo con un candidato calcolato a risk-free 0 regalerebbe al candidato ~0,5 "
                       + "punti di puro cambio di unità di misura. Ri-applica l'ensemble da /ensemble per ri-misurare "
                       + "le gambe con la convenzione corrente.",
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
        // benchmark nullo). Attiva solo se il candidato riporta la FINESTRA dell'holdout; altrimenti
        // z=0 e il gate è neutro (si ricade sulla sola isteresi percentuale).
        var significanceZ = SharpeAdvantageZ(candidate.WeightedAverageSharpe, current.WeightedAverageSharpe, candidate.HoldoutMonths);
        result.SignificanceZ = Math.Round(significanceZ, 2);
        var significanceKnown = candidate.HoldoutMonths is > 0m && options.MinSharpeSignificanceZ > 0m;
        var significant = !significanceKnown || significanceZ >= options.MinSharpeSignificanceZ;

        // 4. Primary path: a meaningful Sharpe improvement above the hysteresis band AND, when the
        // sample size is known, one that is statistically distinguishable from noise.
        if (sharpeImprovementPct >= options.MinSharpeImprovementPercent)
        {
            if (!significant)
            {
                result.ShouldReplace = false;
                result.Reason = $"Ensemble corrente mantenuto: +{result.SharpeImprovementPercent:F1}% di Sharpe ma non significativo (z {result.SignificanceZ:F2} < {options.MinSharpeSignificanceZ:F2} su {candidate.HoldoutMonths:0.##} mesi di holdout, {candidate.Observations} trade sulla gamba più magra).";
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
    /// l'errore standard asintotico dello Sharpe di Lo (2002): SE(SR) ≈ √((1 + ½·SR²) / T), dove
    /// <b>SR e T devono stare nella stessa frequenza</b>. Restituisce 0 se la finestra è ignota o
    /// non positiva. Una finestra più corta gonfia SE → z più basso → più difficile giustificare
    /// uno swap (esattamente l'intento anti-churn).
    ///
    /// [A4, 2026-08-20] Qui SR è ANNUALIZZATO (<c>Statistics.SharpeRatio</c> moltiplica per √ppy),
    /// e prima passava come T il conteggio TRADE dell'holdout: due grandezze diverse, con SE
    /// 4,5-20× troppo piccolo e uno z che ne usciva altrettanto gonfiato. De-annualizzando Lo si
    /// ottiene Var(SR_annuo) = (1 + SR_annuo²/(2k)) / Y, con k = periodi/anno e Y = anni di holdout.
    /// Il termine correttivo SR²/(2k) vale ≤ 0,006 per ogni timeframe supportato a Sharpe ≤ 2
    /// (a 4h, k = 2190, Sharpe 1 ⇒ 0,00023), cioè sotto la precisione con cui lo z viene mostrato:
    /// si usa la forma <b>SE = 1/√Y</b>, che è l'espressione nota dell'errore standard di uno Sharpe
    /// annualizzato e non richiede di trasportare un ppy dentro l'<see cref="EnsembleSummary"/>.
    /// </summary>
    internal static decimal SharpeAdvantageZ(decimal candidateSharpe, decimal incumbentSharpe, decimal? holdoutMonths)
    {
        if (holdoutMonths is not > 0m)
        {
            return 0m;
        }
        var years = (double)holdoutMonths.Value / 12.0;
        var se = Math.Sqrt(1.0 / years);
        if (se <= 0.0 || double.IsNaN(se) || double.IsInfinity(se))
        {
            return 0m;
        }
        return (decimal)(((double)(candidateSharpe - incumbentSharpe)) / se);
    }
}
