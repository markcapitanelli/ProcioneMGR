using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline.Stages;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Deploys a <see cref="PipelineRecommendation"/> onto the isolated trading lanes (0..LaneCount-1),
/// with the exact validated per-leg parameters (from <c>BestStopVariant</c>) plus a data-driven
/// SL/TP bracket. Extracted verbatim from <c>Pipeline.razor</c> so the SAME apply path is used by
/// both the manual "Applica al Trading" button and the automatic re-apply loop in
/// <see cref="PipelineSchedulerWorker"/> — one implementation, no drift.
///
/// SAFETY: this only writes ensemble CONFIGURATION (per-lane <see cref="EnsembleConfiguration"/>);
/// it never starts trading, never opens a position, never switches to Live. Starting a lane is
/// always a separate, explicit action from /trading (Paper), and real execution stays behind
/// SafetyChecker + manual confirmation.
/// </summary>
public interface IPipelineApplier
{
    /// <summary>Number of isolated trading lanes (must match Program.cs LaneCount).</summary>
    int LaneCount { get; }

    /// <summary>Distributes the recommendation's legs across the lanes. Returns a report (lanes used, overflow, message).</summary>
    Task<ApplyResult> ApplyRecommendationAsync(PipelineRecommendation recommendation, CancellationToken ct = default);

    /// <summary>Loads a completed run's recommendation from the DB and applies it. Throws if the run/recommendation is missing.</summary>
    Task<ApplyResult> ApplyRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Snapshot of the ensemble currently deployed across all lanes (for comparison against a candidate).</summary>
    Task<EnsembleSummary> GetCurrentEnsembleSummaryAsync(CancellationToken ct = default);

    /// <summary>Compact, comparable snapshot of a recommendation (the candidate ensemble).</summary>
    EnsembleSummary SummarizeRecommendation(PipelineRecommendation recommendation);
}

/// <summary>Outcome of an apply operation (for the UI message + the scheduler audit log).</summary>
public sealed class ApplyResult
{
    public int LanesUsed { get; set; }
    public int Overflow { get; set; }
    public List<string> Deployed { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

/// <inheritdoc cref="IPipelineApplier"/>
public sealed class PipelineApplier(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ExcursionAnalyzer excursion,
    IServiceProvider serviceProvider) : IPipelineApplier
{
    public int LaneCount => 3;

    public async Task<ApplyResult> ApplyRunAsync(Guid runId, CancellationToken ct = default)
    {
        PipelineRecommendation? recommendation;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var run = await db.PipelineRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct)
                      ?? throw new InvalidOperationException($"Run {runId} non trovato.");
            recommendation = DeserializeRecommendation(run.RecommendationJson);
        }
        if (recommendation is null || recommendation.EnsembleLegs.Count == 0)
        {
            throw new InvalidOperationException($"Il run {runId} non ha un ensemble applicabile.");
        }
        return await ApplyRecommendationAsync(recommendation, ct);
    }

    public async Task<ApplyResult> ApplyRecommendationAsync(PipelineRecommendation recommendation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        var result = new ApplyResult();
        if (recommendation.EnsembleLegs.Count == 0)
        {
            result.Message = "Nessuna gamba nell'ensemble: niente da applicare.";
            return result;
        }

        // Distribute the legs across the isolated lanes: each distinct symbol+timeframe group goes
        // to its OWN keyed lane, with the EXACT validated parameters (from BestStopVariant) + the
        // data-driven SL/TP bracket. Allocation is normalized to 100% within each lane (isolated
        // capital). Symbol groups beyond LaneCount are reported as overflow.
        var groups = recommendation.EnsembleLegs
            .GroupBy(l => (l.Symbol, l.Timeframe))
            .OrderByDescending(g => g.Sum(l => l.WeightPercent))
            .ToList();

        var lanesUsed = Math.Min(groups.Count, LaneCount);
        for (var lane = 0; lane < lanesUsed; lane++)
        {
            var group = groups[lane].ToList();
            var (symbol, timeframe) = groups[lane].Key;
            var (autoSl, autoTp) = await ComputeAutoBracketAsync(symbol, timeframe, ct);
            var totalWeight = group.Sum(l => l.WeightPercent);

            var mgr = serviceProvider.GetRequiredKeyedService<IEnsembleManager>(lane);
            var cfg = await mgr.GetConfigurationAsync(ct);
            cfg.Symbol = symbol;
            cfg.Timeframe = timeframe;
            cfg.ExpectedRiskFactor95 = recommendation.RiskLimits.RiskFactor95;
            cfg.Strategies = group.Select(l =>
            {
                var alloc = totalWeight > 0m
                    ? Math.Round(l.WeightPercent / totalWeight * 100m, 1)
                    : Math.Round(100m / group.Count, 1);
                return BuildLegStrategy(l, autoSl, autoTp, alloc);
            }).ToList();
            await mgr.UpdateConfigurationAsync(cfg, ct);

            var sl = cfg.Strategies.Count(s => s.StopLossPercent is not null || s.TrailingStopPercent is not null);
            var tp = cfg.Strategies.Count(s => s.TakeProfitPercent is not null);
            result.Deployed.Add($"corsia {lane}: {symbol} {timeframe} ({group.Count} gambe, SL {sl}/{group.Count}, TP {tp}/{group.Count})");
        }

        result.LanesUsed = lanesUsed;
        result.Overflow = groups.Count - lanesUsed;
        result.Message = $"Ensemble distribuito su {lanesUsed} corsie con parametri validati + SL/TP automatici — {string.Join("; ", result.Deployed)}"
                       + (result.Overflow > 0 ? $". {result.Overflow} gruppi-simbolo aggiuntivi non applicati (solo {LaneCount} corsie disponibili)" : "")
                       + ". Avvia le corsie da /trading in Paper — nessun trading è stato avviato automaticamente.";
        return result;
    }

    public async Task<EnsembleSummary> GetCurrentEnsembleSummaryAsync(CancellationToken ct = default)
    {
        var legs = new List<LegSummary>();
        var weights = new List<(decimal weight, decimal sharpe, decimal rf)>();
        var rfKnownWeight = 0m;
        var rfWeightedSum = 0m;

        for (var lane = 0; lane < LaneCount; lane++)
        {
            var mgr = serviceProvider.GetRequiredKeyedService<IEnsembleManager>(lane);
            EnsembleConfiguration cfg;
            try { cfg = await mgr.GetConfigurationAsync(ct); }
            catch { continue; }

            var laneRf = cfg.ExpectedRiskFactor95;
            foreach (var s in cfg.Strategies.Where(s => s.IsActive))
            {
                // Global weight of a leg = lane capital × its in-lane allocation. Lanes carry equal
                // isolated capital, so this reduces to allocation × TotalCapital.
                var globalWeight = cfg.TotalCapital * (s.CurrentAllocation <= 0m ? 0m : s.CurrentAllocation) / 100m;
                if (globalWeight <= 0m) globalWeight = cfg.TotalCapital / Math.Max(1, cfg.Strategies.Count);
                var sharpe = s.ExpectedSharpe ?? 0m; // unmeasured leg = 0 (conservative, never inflates)
                weights.Add((globalWeight, sharpe, laneRf));
                if (laneRf > 0m) { rfKnownWeight += globalWeight; rfWeightedSum += laneRf * globalWeight; }
                legs.Add(new LegSummary
                {
                    Symbol = cfg.Symbol,
                    Timeframe = cfg.Timeframe,
                    StrategyName = s.StrategyName,
                    WeightPercent = s.CurrentAllocation,
                    Sharpe = sharpe,
                    RiskFactor95 = laneRf,
                });
            }
        }

        var totalWeight = weights.Sum(w => w.weight);
        return new EnsembleSummary
        {
            WeightedAverageSharpe = totalWeight > 0m ? weights.Sum(w => w.sharpe * w.weight) / totalWeight : 0m,
            WeightedAverageRiskFactor95 = rfKnownWeight > 0m ? rfWeightedSum / rfKnownWeight : 0m,
            SurvivingLegs = legs.Count,
            DistinctSymbols = legs.Select(l => l.Symbol).Distinct().Count(),
            Legs = legs,
        };
    }

    public EnsembleSummary SummarizeRecommendation(PipelineRecommendation recommendation)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        var legs = recommendation.EnsembleLegs
            .Select(l => new LegSummary
            {
                Symbol = l.Symbol,
                Timeframe = l.Timeframe,
                StrategyName = l.StrategyName,
                WeightPercent = l.WeightPercent,
                Sharpe = l.HoldoutSharpe,
                RiskFactor95 = recommendation.RiskLimits.RiskFactor95,
            })
            .ToList();

        var totalWeight = legs.Sum(l => l.WeightPercent);
        // Effective sample size behind the weighted Sharpe = the weakest leg's holdout trade count
        // (conservative: the swap must be significant even for the thinnest-sampled leg).
        var observations = recommendation.EnsembleLegs.Count > 0
            ? recommendation.EnsembleLegs.Min(l => l.HoldoutTrades)
            : 0;
        return new EnsembleSummary
        {
            WeightedAverageSharpe = totalWeight > 0m
                ? legs.Sum(l => l.Sharpe * l.WeightPercent) / totalWeight
                : (legs.Count > 0 ? legs.Average(l => l.Sharpe) : 0m),
            WeightedAverageRiskFactor95 = recommendation.RiskLimits.RiskFactor95,
            SurvivingLegs = legs.Count,
            DistinctSymbols = legs.Select(l => l.Symbol).Distinct().Count(),
            Observations = observations,
            Legs = legs,
        };
    }

    private static PipelineRecommendation? DeserializeRecommendation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<PipelineRecommendation>(json); }
        catch { return null; }
    }

    /// <summary>
    /// Builds a leg's strategy with exact validated parameters + SL/TP bracket: the walk-forward
    /// confirmed stop/target (<c>BestStopVariant</c>) wins; absent that, the data-driven excursion
    /// bracket (<paramref name="autoSl"/>/<paramref name="autoTp"/>) is used.
    /// </summary>
    private static EnsembleStrategy BuildLegStrategy(ProposedLeg l, decimal autoSl, decimal autoTp, decimal allocationPercent)
    {
        var probe = new BacktestConfiguration();
        RobustnessProbeStage.ApplyVariant(probe, l.BestStopVariant);
        return new EnsembleStrategy
        {
            StrategyName = l.StrategyName,
            DisplayName = l.DisplayName,
            Parameters = new(l.Parameters),
            CurrentAllocation = allocationPercent,
            IsActive = true,
            StopLossPercent = probe.StopLossPercent > 0m ? probe.StopLossPercent : (autoSl > 0m ? autoSl : null),
            TakeProfitPercent = probe.TakeProfitPercent > 0m ? probe.TakeProfitPercent : (autoTp > 0m ? autoTp : null),
            TrailingStopPercent = probe.TrailingStopPercent > 0m ? probe.TrailingStopPercent : null,
            ExpectedSharpe = l.HoldoutSharpe != 0m ? l.HoldoutSharpe : null,
            ExpectedProfitFactor = l.HoldoutProfitFactor != 0m ? l.HoldoutProfitFactor : null,
            ExpectedMaxDrawdown = l.HoldoutMaxDrawdown != 0m ? l.HoldoutMaxDrawdown : null,
        };
    }

    /// <summary>
    /// Protective SL+TP bracket (% from entry) from the pair/timeframe's recent candles via
    /// <see cref="ExcursionAnalyzer"/>: mean of the 95th-percentile adverse (SL) and favorable (TP)
    /// excursions across long and short, for a symmetric level usable on both sides. (0,0) if data
    /// is insufficient.
    /// </summary>
    private async Task<(decimal sl, decimal tp)> ComputeAutoBracketAsync(string symbol, string timeframe, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var candles = await db.OhlcvData
                .Where(c => c.Symbol == symbol && c.Timeframe == timeframe)
                .OrderByDescending(c => c.TimestampUtc)
                .Take(5000)
                .ToListAsync(ct);
            if (candles.Count < 100) return (0m, 0m);
            candles.Reverse(); // chronological for the analysis

            var sl = excursion.SuggestStopLoss(candles);
            var tp = excursion.SuggestTakeProfit(candles);
            static decimal Avg(decimal a, decimal b)
            {
                var v = new[] { a, b }.Where(x => x > 0m).ToList();
                return v.Count > 0 ? Math.Round(v.Average(), 2) : 0m;
            }
            return (Avg(sl.LongStopPercentile95, sl.ShortStopPercentile95),
                    Avg(tp.LongTakeProfitPercentile95, tp.ShortTakeProfitPercentile95));
        }
        catch { return (0m, 0m); }
    }
}
