using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Validation;

namespace ProcioneMGR.Services.Pipeline.Stages;

/// <summary>
/// Stage 10 (opt-in) — il giudice del gemello nullo sui SOPRAVVISSUTI all'holdout: ogni finalista
/// viene ribattezzato su N mercati nulli (<see cref="NullTwinGenerator"/>: stessa volatilità, zero
/// struttura direzionale) e sopravvive solo se il suo Sharpe holdout supera il quantile richiesto
/// della distribuzione nulla. È il terzo giudice indipendente dopo Sharpe/trade e DSR/PBO — quello
/// che nei tool CLI ha smascherato il falso positivo SEI/USDT — reso organo della pipeline con la
/// POLICY UNIFICATA di <see cref="NullTwinJudge"/> (200 gemelli, 99°), mai più due giudici con
/// rigore diverso.
///
/// Fail-safe dichiarato: un candidato NON giudicabile (holdout troppo corto, gemelli falliti in
/// massa) resta sopravvissuto e viene detto a voce alta — un giudice che non può giudicare non
/// boccia al buio, coerente con la regola del CorrelatedExposureGuard.
/// </summary>
public sealed class NullTwinValidationStage(INullTwinJudge judge) : IPipelineStage
{
    public string Name => "NullTwinValidation";
    public string DisplayName => "Giudice del gemello nullo";
    public string Description => "Ribattezza i sopravvissuti su N mercati nulli: chi non batte il quantile richiesto della distribuzione nulla è selezione, non edge.";
    // Stesso DefaultOrder della RobustnessProbe: entrambi leggono i sopravvissuti dell'holdout e
    // non dipendono l'uno dall'altro; a parità di ordine vince la posizione nel catalogo (questo
    // stage sta prima, così la probe non spreca Monte Carlo su candidati che il nullo boccerebbe).
    public int DefaultOrder => 10;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("HoldoutValidation")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("twins", "Gemelli nulli", "200", "quanti mercati nulli per candidato; sotto la metà di validi il giudizio è 'non applicabile', mai una bocciatura al buio"),
        new("nullPercentile", "Percentile richiesto", "99", "lo Sharpe holdout reale deve superare questo percentile della distribuzione nulla (99 = policy; 95 su ~15.000 tentativi lascia passare il rumore per costruzione)"),
        new("maxCandidates", "Massimo candidati giudicati", "8", "tetto di calcolo: i migliori sopravvissuti per Sharpe holdout"),
        new("meanBlockLength", "Lunghezza media blocchi", "24", "blocchi (barre) dello stationary bootstrap dei gemelli"),
        .. PipelineCosts.ParameterDefinitions,
        new("positionSizePercent", "Size posizione (%)", "10", "stessi costi e size dell'holdout: il gemello va giudicato alle condizioni del reale"),
    ];

    public string? ValidateInput(PipelineContext ctx)
        => ctx.Validated.Count == 0 ? "Nessun candidato validato (eseguire prima HoldoutValidation)." : null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var twins = config.GetInt("twins", NullTwinJudge.DefaultTwins);
        var percentile = config.GetDecimal("nullPercentile", 99m);
        var maxCandidates = config.GetInt("maxCandidates", 8);
        var meanBlockLength = (double)config.GetDecimal("meanBlockLength", 24m);
        var costs = PipelineCosts.FromConfig(config);
        var sizePercent = config.GetDecimal("positionSizePercent", 10m);

        var finalists = ctx.Validated
            .Where(v => v.Survived)
            .OrderByDescending(v => v.HoldoutSharpe)
            .Take(Math.Max(1, maxCandidates))
            .ToList();

        var judged = 0;
        var rejected = 0;
        var notJudgeable = 0;

        foreach (var v in finalists)
        {
            ct.ThrowIfCancellationRequested();

            var candles = await ctx.Candles.GetAsync(v.Symbol, v.Timeframe, ctx.Ranges.HoldoutFrom, ctx.Ranges.HoldoutTo, ct);
            if (candles.Count < 200)
            {
                notJudgeable++;
                ctx.LogLine($"[{Name}] {v.Key}: holdout di sole {candles.Count} barre, non giudicabile — lasciato passare (a voce alta).");
                continue;
            }

            var cfg = costs.ApplyTo(new BacktestConfiguration
            {
                ExchangeName = ctx.ExchangeName,
                Symbol = v.Symbol,
                Timeframe = v.Timeframe,
                From = ctx.Ranges.HoldoutFrom,
                To = ctx.Ranges.HoldoutTo,
                InitialCapital = ctx.InitialCapital,
                PositionSizePercent = sizePercent,
                StrategyName = v.StrategyName,
                StrategyParameters = new(v.Parameters),
            });

            var verdict = await judge.JudgeAsync(
                cfg, candles, v.HoldoutSharpe,
                twins: twins,
                requiredPercentile: (double)percentile / 100.0,
                meanBlockLength: meanBlockLength,
                ct: ct);

            if (verdict is null)
            {
                notJudgeable++;
                ctx.LogLine($"[{Name}] {v.Key}: gemelli validi insufficienti, non giudicabile — lasciato passare (a voce alta).");
                continue;
            }

            judged++;
            v.NullTwinPercentile = verdict.PercentileOfReal;
            if (verdict.Passed)
            {
                ctx.LogLine($"[{Name}] {v.Key}: reale {verdict.RealSharpe:F2} OLTRE il {percentile:F0}° del nullo "
                          + $"(P{percentile:F0} {verdict.Threshold:F2}, {verdict.ValidTwins} gemelli) → confermato.");
            }
            else
            {
                rejected++;
                v.Survived = false;
                v.RejectReason = $"Gemello nullo: reale {verdict.RealSharpe:F2} al {verdict.PercentileOfReal:F0}° percentile "
                               + $"(serve > {percentile:F0}°; P{percentile:F0} nullo {verdict.Threshold:F2} su {verdict.ValidTwins} gemelli)";
                ctx.LogLine($"[{Name}] {v.Key}: {v.RejectReason} → dentro il nullo: selezione, non edge.");
            }
        }

        ctx.LogLine($"[{Name}] Giudicati {judged}/{finalists.Count} finalisti: {rejected} bocciati, {notJudgeable} non giudicabili (passati a voce alta).");
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var survivors = ctx.Validated.Count(v => v.Survived);
        var judged = ctx.Validated.Count(v => v.NullTwinPercentile is not null);
        var rejectedHere = ctx.Validated.Count(v => !v.Survived && v.RejectReason?.StartsWith("Gemello nullo", StringComparison.Ordinal) == true);
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{judged} finalisti giudicati contro il mercato nullo, {rejectedHere} bocciati; {survivors} sopravvissuti totali.",
            Metrics = new()
            {
                ["Giudicati"] = judged,
                ["Bocciati dal nullo"] = rejectedHere,
                ["Sopravvissuti"] = survivors,
            },
        };
    }
}
