using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.AltData;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Portfolio;

namespace ProcioneMGR.Services.Pipeline.Stages;

/// <summary>
/// Stage 11 — assembles the final survivors into a weighted ensemble proposal. Weights come
/// from HRP on the legs' selection-range equity returns (2+ legs), then get a regime bias
/// from the pipeline rules (mean-reversion legs weigh more in sideways regimes, trend legs in
/// trending ones) and are renormalized.
/// </summary>
public sealed class EnsembleAssemblyStage(
    IBacktestEngine backtest,
    HierarchicalRiskParityOptimizer hrp,
    IPipelineRulesProvider rulesProvider) : IPipelineStage
{
    public string Name => "EnsembleAssembly";
    public string DisplayName => "Assemblaggio ensemble";
    public string Description => "Pesa i sopravvissuti (HRP + bias di regime) in una proposta di ensemble.";
    public int DefaultOrder => 11;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("HoldoutValidation")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("maxLegs", "Gambe massime", "3", ""),
        .. PipelineCosts.ParameterDefinitions,
        new("positionSizePercent", "Size posizione (%)", "10", ""),
    ];

    public string? ValidateInput(PipelineContext ctx)
        => ctx.Validated.Count(v => v.Survived) == 0 ? "Nessun sopravvissuto da assemblare." : null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var rules = rulesProvider.GetRules();
        var maxLegs = config.GetInt("maxLegs", rules.MaxLegs);
        var costs = PipelineCosts.FromConfig(config);

        // Ordered by SELECTION-phase walk-forward Sharpe (the holdout stays verdict-only).
        var legs = ctx.Validated
            .Where(v => v.Survived)
            .OrderByDescending(v => v.WalkForwardOosSharpe)
            .Take(maxLegs)
            .ToList();

        var proposal = new EnsembleProposal();

        // HRP weights from daily equity returns of each leg over the selection range.
        var weights = new Dictionary<string, decimal>();
        if (legs.Count >= 2)
        {
            var returnsByLeg = new Dictionary<string, Dictionary<DateTime, decimal>>();
            foreach (var leg in legs)
            {
                ct.ThrowIfCancellationRequested();
                var cfg = costs.ApplyTo(new BacktestConfiguration
                {
                    ExchangeName = ctx.ExchangeName,
                    Symbol = leg.Symbol,
                    Timeframe = leg.Timeframe,
                    From = ctx.Ranges.SelectionFrom,
                    To = ctx.Ranges.SelectionTo,
                    InitialCapital = ctx.InitialCapital,
                    PositionSizePercent = config.GetDecimal("positionSizePercent", 10m),
                    StrategyName = leg.StrategyName,
                    StrategyParameters = new(leg.Parameters),
                });
                Stages.RobustnessProbeStage.ApplyVariant(cfg, leg.BestStopVariant);
                var result = await backtest.RunBacktestAsync(cfg, ct);
                returnsByLeg[leg.Key] = DailyReturns(result.EquityCurve);
            }

            // Intersect the dates so all series are aligned by index (HRP requirement).
            var commonDates = returnsByLeg.Values
                .Select(d => d.Keys.AsEnumerable())
                .Aggregate((a, b) => a.Intersect(b))
                .OrderBy(d => d)
                .ToList();

            if (commonDates.Count >= 30)
            {
                var aligned = returnsByLeg.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<decimal>)commonDates.Select(d => kv.Value[d]).ToList());
                var allocation = hrp.Optimize(aligned);
                weights = allocation.Weights.ToDictionary(kv => kv.Key, kv => kv.Value * 100m);
                proposal.Method = "HRP";
            }
        }

        if (weights.Count == 0)
        {
            // 1 leg, or not enough aligned history: equal weight.
            foreach (var leg in legs) weights[leg.Key] = 100m / legs.Count;
            proposal.Method = "EqualWeight";
        }

        // Regime bias: multiply the weight of legs whose strategy family matches the current
        // regime, then renormalize (deterministic rule from pipeline_rules.json).
        var regimeLabel = ctx.Regimes?.CurrentRegimeLabel ?? string.Empty;
        var isTrending = regimeLabel.Contains("Trend", StringComparison.OrdinalIgnoreCase);
        var isSideways = regimeLabel.Contains("Sideways", StringComparison.OrdinalIgnoreCase)
                      || regimeLabel.Contains("Choppy", StringComparison.OrdinalIgnoreCase)
                      || regimeLabel.Contains("Lateral", StringComparison.OrdinalIgnoreCase);
        if (isTrending || isSideways)
        {
            var favoured = isTrending ? rules.TrendStrategies : rules.MeanReversionStrategies;
            foreach (var leg in legs.Where(l => favoured.Contains(l.StrategyName, StringComparer.OrdinalIgnoreCase)))
            {
                weights[leg.Key] *= rules.RegimeMatchWeightMultiplier;
            }
            var total = weights.Values.Sum();
            if (total > 0m)
            {
                foreach (var key in weights.Keys.ToList()) weights[key] = weights[key] / total * 100m;
                proposal.Note = $"Bias di regime applicato ({(isTrending ? "trend" : "laterale")}, ×{rules.RegimeMatchWeightMultiplier}).";
            }
        }

        // Creative discovery can confirm MULTIPLE distinct specs of the same meta-strategy on
        // the same pair (e.g. two different "Composite" rules) — disambiguate the DisplayName
        // with the same short fingerprint used in Key, but only for groups that actually
        // collide (keeps the common case's DisplayName clean).
        var groupSizes = legs.GroupBy(l => (l.StrategyName, l.Symbol, l.Timeframe)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var leg in legs)
        {
            var ambiguous = groupSizes[(leg.StrategyName, leg.Symbol, leg.Timeframe)] > 1;
            var hashIdx = leg.Key.LastIndexOf('#');
            var suffix = ambiguous && hashIdx >= 0 ? $" {leg.Key[hashIdx..]}" : "";
            proposal.Legs.Add(new ProposedLeg
            {
                StrategyName = leg.StrategyName,
                DisplayName = $"{leg.StrategyName} {leg.Symbol} {leg.Timeframe} [{leg.BestStopVariant}]{suffix}",
                Symbol = leg.Symbol,
                Timeframe = leg.Timeframe,
                Parameters = new(leg.Parameters),
                WeightPercent = Math.Round(weights.GetValueOrDefault(leg.Key, 0m), 1),
                BestStopVariant = leg.BestStopVariant,
                HoldoutSharpe = leg.HoldoutSharpe,
                HoldoutProfitFactor = leg.HoldoutProfitFactor,
                HoldoutMaxDrawdown = leg.HoldoutMaxDrawdown,
                HoldoutTrades = leg.HoldoutTrades,
            });
        }
        ctx.Ensemble = proposal;
        ctx.LogLine($"[{Name}] {proposal.Legs.Count} gambe, metodo {proposal.Method}: {string.Join(", ", proposal.Legs.Select(l => $"{l.DisplayName} {l.WeightPercent}%"))}");
    }

    private static Dictionary<DateTime, decimal> DailyReturns(IReadOnlyList<EquityPoint> equity)
    {
        // Last equity point of each day → daily % returns keyed by date.
        var daily = equity
            .GroupBy(p => p.Timestamp.Date)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Timestamp).Last().Capital);
        var dates = daily.Keys.OrderBy(d => d).ToList();
        var returns = new Dictionary<DateTime, decimal>();
        for (var i = 1; i < dates.Count; i++)
        {
            var prev = daily[dates[i - 1]];
            if (prev > 0m) returns[dates[i]] = (daily[dates[i]] - prev) / prev;
        }
        return returns;
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.Ensemble ?? new EnsembleProposal();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = o.Legs.Count == 0
                ? "Nessuna gamba proposta."
                : $"Ensemble a {o.Legs.Count} gambe ({o.Method}): {string.Join("; ", o.Legs.Select(l => $"{l.DisplayName} {l.WeightPercent}%"))}. {o.Note}",
            Metrics = new() { ["Gambe"] = o.Legs.Count },
        };
    }
}

/// <summary>
/// Stage 12 — turns the robustness numbers into operating risk limits: half-Kelly sizing per
/// leg (volatility-adjusted), the Monte Carlo shutdown guard, and the system stop level.
/// </summary>
public sealed class RiskSizingStage(IPipelineRulesProvider rulesProvider) : IPipelineStage
{
    public string Name => "RiskSizing";
    public string DisplayName => "Sizing e limiti di rischio";
    public string Description => "Kelly frazionario, guardia di spegnimento Monte Carlo, aggiustamento per volatilità.";
    public int DefaultOrder => 12;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("EnsembleAssembly")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions => [];

    public string? ValidateInput(PipelineContext ctx)
        => ctx.Ensemble is null || ctx.Ensemble.Legs.Count == 0 ? "Nessun ensemble proposto su cui dimensionare il rischio." : null;

    public Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var rules = rulesProvider.GetRules();
        var legs = ctx.Ensemble!.Legs;
        var validatedByKey = ctx.Validated.ToDictionary(v => v.Key);

        var volFactor = ctx.Volatility?.Level == "Alta"
            ? 1m - rules.HighVolSizingReductionPercent / 100m
            : 1m;

        var risk = new RiskAssessment { VolatilitySizingFactor = volFactor };
        var halfKellies = new List<decimal>();
        var riskFactors = new List<decimal>();
        var shutdownLevels = new List<decimal>();
        var stopLevels = new List<decimal>();

        foreach (var leg in legs)
        {
            if (!validatedByKey.TryGetValue(leg.Key, out var v)) continue;

            var kellyPercent = v.KellyFraction * rules.KellyFraction * 100m; // fractional Kelly, in %
            var sizing = Math.Min(kellyPercent * volFactor, rules.MaxSizingPercent);
            if (sizing <= 0m) sizing = Math.Min(5m, rules.MaxSizingPercent); // no Kelly estimate → conservative default
            leg.SizingPercent = Math.Round(sizing, 1);

            halfKellies.Add(v.HalfKelly);
            riskFactors.Add(v.MonteCarloRiskFactor95);
            if (ctx.InitialCapital > 0m) shutdownLevels.Add(v.MonteCarloDrawdown95 / ctx.InitialCapital * 100m);
            if (v.BestStopVariant.StartsWith("SL", StringComparison.OrdinalIgnoreCase)
                && decimal.TryParse(v.BestStopVariant[2..], NumberStyles.Any, CultureInfo.InvariantCulture, out var sl))
            {
                stopLevels.Add(sl);
            }
        }

        risk.AverageHalfKelly = halfKellies.Count > 0 ? Math.Round(halfKellies.Average(), 4) : 0m;
        risk.AverageRiskFactor95 = riskFactors.Count > 0 ? Math.Round(riskFactors.Average(), 2) : 0m;
        risk.ShutdownDrawdownPercent = shutdownLevels.Count > 0 ? Math.Round(shutdownLevels.Max(), 1) : 0m;
        risk.SuggestedStopLossPercent = stopLevels.Count > 0 ? stopLevels.Min() : 3m;
        if (volFactor < 1m)
        {
            risk.Notes.Add($"Volatilità prevista ALTA: sizing ridotto del {rules.HighVolSizingReductionPercent}% su tutte le gambe.");
        }
        risk.Notes.Add($"Frazione di Kelly usata: {rules.KellyFraction:P0} del Kelly pieno (cap {rules.MaxSizingPercent}% per gamba).");

        ctx.Risk = risk;
        ctx.LogLine($"[{Name}] half-Kelly medio {risk.AverageHalfKelly:P1}, RF95 medio {risk.AverageRiskFactor95:F2}×, guardia DD {risk.ShutdownDrawdownPercent}%.");
        return Task.CompletedTask;
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.Risk ?? new RiskAssessment();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"Half-Kelly medio {o.AverageHalfKelly:P1}; RF95 medio {o.AverageRiskFactor95:F2}×; spegnere il sistema oltre {o.ShutdownDrawdownPercent}% di drawdown; stop suggerito {o.SuggestedStopLossPercent}%."
                 + (o.VolatilitySizingFactor < 1m ? " Sizing ridotto per volatilità alta." : ""),
            Metrics = new()
            {
                ["HalfKellyMedio"] = o.AverageHalfKelly,
                ["RF95Medio"] = o.AverageRiskFactor95,
                ["GuardiaDrawdown"] = o.ShutdownDrawdownPercent,
            },
        };
    }
}

/// <summary>Stage 13 — historical news impact on the reference symbol + alerts for recent high-impact categories.</summary>
public sealed class NewsImpactCheckStage(
    INewsImpactAnalyzer impactAnalyzer,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPipelineRulesProvider rulesProvider) : IPipelineStage
{
    public string Name => "NewsImpactCheck";
    public string DisplayName => "Impatto notizie";
    public string Description => "Misura l'impatto storico delle notizie sul simbolo di riferimento e genera alert.";
    public int DefaultOrder => 13;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("DataIngestion")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("lookbackDays", "Storico notizie (giorni)", "30", ""),
    ];

    public string? ValidateInput(PipelineContext ctx) => null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var primary = ctx.PrimarySeries;
        var rules = rulesProvider.GetRules();
        var to = DateTime.UtcNow;
        var from = to.AddDays(-config.GetInt("lookbackDays", 30));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var news = await db.AltDataPoints.AsNoTracking()
            .Where(a => a.TimestampUtc >= from)
            .ToListAsync(ct);

        var output = new NewsImpactOutput { ReferenceSymbol = primary.Symbol };
        if (news.Count > 0)
        {
            var candles = await ctx.Candles.GetAsync(primary.Symbol, primary.Timeframe, from.AddDays(-2), to, ct);
            if (candles.Count > 0)
            {
                var report = impactAnalyzer.Analyze(primary.Symbol, news, candles);
                output.ByCategory = report.ByCategory
                    .Where(c => c.Stats.Observations > 0)
                    .OrderByDescending(c => Math.Abs(c.Stats.AvgReturn24h))
                    .Select(c => new CategoryImpactDto
                    {
                        Category = c.Category,
                        Observations = c.Stats.Observations,
                        AvgReturn24hPercent = c.Stats.AvgReturn24h * 100.0,
                    })
                    .ToList();
            }
        }

        var recentAlerts = news
            .Where(n => n.TimestampUtc >= to.AddHours(-24) && rules.AlertNewsCategories.Contains(n.Category))
            .OrderByDescending(n => n.TimestampUtc)
            .Take(5)
            .Select(n => $"[{n.Category}] {n.Title}")
            .ToList();
        output.Alerts = recentAlerts;

        ctx.NewsImpact = output;
        ctx.LogLine($"[{Name}] {news.Count} notizie analizzate, {recentAlerts.Count} alert nelle ultime 24h.");
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.NewsImpact ?? new NewsImpactOutput();
        var top = o.ByCategory.FirstOrDefault();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{o.ByCategory.Count} categorie con impatto misurato su {o.ReferenceSymbol}"
                 + (top is null ? "" : $"; massimo: {top.Category} ({top.AvgReturn24hPercent:+0.00;-0.00}% a 24h su {top.Observations} oss.)")
                 + $"; {o.Alerts.Count} alert recenti.",
            Metrics = new() { ["Alert"] = o.Alerts.Count },
        };
    }
}

/// <summary>
/// Stage 14 — the deterministic "brain": renders the final conclusion from the numbers the
/// previous stages produced, applying the rules of pipeline_rules.json. No LLM: every claim
/// in the output traces back to a verifiable metric in the context.
/// </summary>
public sealed class RecommendationStage(IPipelineRulesProvider rulesProvider) : IPipelineStage
{
    public string Name => "Recommendation";
    public string DisplayName => "Raccomandazione";
    public string Description => "Sintesi deterministica: regime, volatilità, sopravvissuti, ensemble, limiti di rischio, alert.";
    public int DefaultOrder => 14;
    public IReadOnlyList<StageDependency> Dependencies => [];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions => [];

    public string? ValidateInput(PipelineContext ctx) => null;

    public Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var rules = rulesProvider.GetRules();
        var rec = new PipelineRecommendation
        {
            RegimeLabel = ctx.Regimes?.CurrentRegimeLabel ?? "sconosciuto",
            VolatilityLabel = ctx.Volatility?.Level ?? "sconosciuta",
            CandidatesEvaluated = ctx.Validated.Count > 0 ? ctx.Validated.Count : ctx.Candidates.Count,
            Survivors = ctx.Validated.Count(v => v.Survived),
            EnsembleLegs = ctx.Ensemble?.Legs ?? new List<ProposedLeg>(),
        };

        // Sentiment 2.0: quando lo snapshot composite c'è, la label deriva dal composite (stesse
        // soglie) e la recommendation porta i campi strutturati + i flag contrarian come alert.
        // Senza snapshot: percorso legacy identico (media news 24h), run vecchi compatibili.
        var snapshot = ctx.AltData?.Snapshot;
        var sentiment = snapshot?.CompositeScore ?? ctx.AltData?.AvgSentimentLast24h ?? 0.0;
        rec.SentimentLabel = sentiment >= rules.SentimentPositiveThreshold ? "positivo"
                           : sentiment <= rules.SentimentNegativeThreshold ? "negativo"
                           : "neutro";
        if (snapshot is not null)
        {
            rec.SentimentComposite = snapshot.CompositeScore;
            rec.FearGreedValue = snapshot.FearGreedValue;
            rec.SentimentExtremes.AddRange(snapshot.Extremes);
            rec.Alerts.AddRange(snapshot.Extremes.Select(e => $"Mood: {e}"));
        }

        var best = ctx.Validated.Where(v => v.Survived).OrderByDescending(v => v.HoldoutSharpe).FirstOrDefault();
        if (best is not null)
        {
            rec.BestCandidate = $"{best.Key} [{best.BestStopVariant}] — Sharpe holdout {best.HoldoutSharpe:F2}, PF {best.HoldoutProfitFactor:F2}, MaxDD {best.HoldoutMaxDrawdown:F1}%";
        }

        if (ctx.Risk is RiskAssessment risk)
        {
            rec.RiskLimits = new RecommendationRiskLimits
            {
                HalfKellyPercent = Math.Round(risk.AverageHalfKelly * 100m, 1),
                RiskFactor95 = risk.AverageRiskFactor95,
                ShutdownDrawdownPercent = risk.ShutdownDrawdownPercent,
                StopLossPercent = risk.SuggestedStopLossPercent,
            };
        }

        rec.Alerts.AddRange(ctx.NewsImpact?.Alerts ?? []);
        if (ctx.Volatility?.Level == "Alta")
        {
            rec.Alerts.Add($"Volatilità prevista in aumento (forecast {ctx.Volatility.ForecastVolatility24:P2} vs lungo periodo {ctx.Volatility.LongRunVolatility:P2}).");
        }
        if (ctx.Regimes?.TrainedNewModel == true)
        {
            rec.Alerts.Add("Il modello di regime è stato riaddestrato in questo run: verificare il profilo prima di fidarsi delle etichette.");
        }

        // Suggested actions (paper): concrete, verifiable, human-in-the-loop.
        if (rec.Survivors == 0)
        {
            rec.SuggestedActions.Add("Nessun sopravvissuto all'holdout: NON operare. Ripetere il run con più dati o attendere condizioni diverse.");
        }
        else
        {
            foreach (var leg in rec.EnsembleLegs)
            {
                rec.SuggestedActions.Add($"Paper trading: {leg.DisplayName} — peso {leg.WeightPercent}%, sizing {leg.SizingPercent}% del capitale.");
            }
            rec.SuggestedActions.Add($"Impostare la guardia di spegnimento a {rec.RiskLimits.ShutdownDrawdownPercent}% di drawdown (MC 95°).");
            rec.SuggestedActions.Add("Osservare 1-3 mesi in paper prima di considerare Testnet/Live (conferma manuale sempre attiva).");
        }

        rec.FullText = RenderTemplate(ctx, rec, sentiment);
        ctx.Recommendation = rec;
        return Task.CompletedTask;
    }

    private static string RenderTemplate(PipelineContext ctx, PipelineRecommendation rec, double sentiment)
    {
        var sb = new StringBuilder();
        var regimeProfile = ctx.Regimes?.Profiles.FirstOrDefault(p => p.RegimeId == ctx.Regimes.CurrentRegimeId);
        sb.AppendLine($"REGIME: {rec.RegimeLabel}" + (regimeProfile is null ? "" : $" (vol media {regimeProfile.MeanVolatility:F2}, trend {regimeProfile.MeanTrendDirection:+0.00;-0.00})"));
        sb.AppendLine($"VOLATILITÀ: {(ctx.Volatility is null ? "n/d" : $"forecast {ctx.Volatility.ForecastVolatility24:P2} per periodo")} — {rec.VolatilityLabel}");
        var moodSnapshot = ctx.AltData?.Snapshot;
        if (moodSnapshot is null)
        {
            sb.AppendLine($"SENTIMENT: {sentiment:F3} (ultime 24h) — {rec.SentimentLabel}");
        }
        else
        {
            sb.AppendLine($"SENTIMENT: composite {moodSnapshot.CompositeScore:+0.000;-0.000} — {rec.SentimentLabel}" +
                (moodSnapshot.FearGreedValue is null ? "" :
                    $"; Fear&Greed {moodSnapshot.FearGreedValue:F0} ({moodSnapshot.FearGreedLabel}" +
                    (moodSnapshot.FearGreedDelta7d is null ? ")" : $", Δ7g {moodSnapshot.FearGreedDelta7d:+0;-0})")));
            foreach (var s in moodSnapshot.Symbols)
            {
                sb.AppendLine($"  - {s.Symbol}: mood {s.Composite:+0.00;-0.00}" +
                    (s.FundingZ is null ? "" : $", funding z {s.FundingZ:+0.0;-0.0}") +
                    (s.GlobalLongShortZ is null ? "" : $", L/S z {s.GlobalLongShortZ:+0.0;-0.0}") +
                    (s.OiChange24hPercent is null ? "" : $", OI 24h {s.OiChange24hPercent:+0.0;-0.0}%"));
            }
        }
        sb.AppendLine();
        sb.AppendLine($"CANDIDATI VALUTATI: {rec.CandidatesEvaluated}");
        sb.AppendLine($"SOPRAVVISSUTI HOLDOUT: {rec.Survivors}");
        sb.AppendLine($"MIGLIOR CANDIDATO: {rec.BestCandidate}");
        sb.AppendLine();
        sb.AppendLine("ENSEMBLE PROPOSTO:");
        if (rec.EnsembleLegs.Count == 0) sb.AppendLine("  - nessuno");
        foreach (var leg in rec.EnsembleLegs)
        {
            sb.AppendLine($"  - {leg.DisplayName}: peso {leg.WeightPercent}%, sizing {leg.SizingPercent}% capitale");
        }
        sb.AppendLine();
        sb.AppendLine("RISK LIMITS:");
        sb.AppendLine($"  - Half-Kelly medio: {rec.RiskLimits.HalfKellyPercent}%");
        sb.AppendLine($"  - MC RiskFactor95: {rec.RiskLimits.RiskFactor95:F2}× → spegnere se DD > {rec.RiskLimits.ShutdownDrawdownPercent}%");
        sb.AppendLine($"  - Stop loss sistema: {rec.RiskLimits.StopLossPercent}%");
        sb.AppendLine();
        sb.AppendLine("ALERT:");
        if (rec.Alerts.Count == 0) sb.AppendLine("  - nessuno");
        foreach (var alert in rec.Alerts) sb.AppendLine($"  - {alert}");
        sb.AppendLine();
        sb.AppendLine("AZIONI SUGGERITE (paper):");
        foreach (var action in rec.SuggestedActions) sb.AppendLine($"  - {action}");
        return sb.ToString();
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.Recommendation ?? new PipelineRecommendation();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"Regime {o.RegimeLabel}, volatilità {o.VolatilityLabel}, sentiment {o.SentimentLabel}; {o.Survivors}/{o.CandidatesEvaluated} sopravvissuti; {o.EnsembleLegs.Count} gambe proposte; {o.Alerts.Count} alert.",
            Metrics = new()
            {
                ["Sopravvissuti"] = o.Survivors,
                ["Gambe"] = o.EnsembleLegs.Count,
                ["Alert"] = o.Alerts.Count,
            },
        };
    }
}

/// <summary>
/// Stage 15 — turns the recommendation into a concrete (paper-first) action plan. It NEVER
/// starts trading by itself: the plan is applied by the user from the UI ("Applica al
/// Trading"), and Live execution always goes through SafetyChecker + per-order manual
/// confirmation in /trading — the pipeline cannot bypass either.
/// </summary>
public sealed class ExecutionPlanStage : IPipelineStage
{
    public string Name => "ExecutionPlan";
    public string DisplayName => "Piano di esecuzione";
    public string Description => "Traduce la raccomandazione in un piano operativo (paper di default, mai auto-eseguito).";
    public int DefaultOrder => 15;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("Recommendation")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions => [];

    public string? ValidateInput(PipelineContext ctx)
        => ctx.Recommendation is null ? "Nessuna raccomandazione da tradurre in piano." : null;

    public Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var rec = ctx.Recommendation!;
        var plan = new ExecutionPlan { Mode = ctx.ExecutionMode };

        foreach (var leg in rec.EnsembleLegs)
        {
            plan.Actions.Add(new PlannedAction
            {
                Description = $"Aggiungere all'ensemble: {leg.DisplayName} (peso {leg.WeightPercent}%, sizing {leg.SizingPercent}%)",
                Symbol = leg.Symbol,
                Timeframe = leg.Timeframe,
                StrategyName = leg.StrategyName,
                SizingPercent = leg.SizingPercent,
            });
        }

        switch (ctx.ExecutionMode)
        {
            case "Disabled":
                plan.Notes.Add("Esecuzione disabilitata dalla configurazione: il piano è solo informativo.");
                break;
            case "Live":
                plan.Notes.Add("Modalità LIVE richiesta: il piano NON viene eseguito automaticamente. Applicalo da /ensemble, avvia da /trading e conferma manualmente ogni ordine (SafetyChecker sempre attivo).");
                break;
            default:
                plan.Notes.Add("Modalità Paper: applica il piano da /ensemble e avvia il paper trading da /trading per osservarlo senza rischio.");
                break;
        }
        if (plan.Actions.Count == 0)
        {
            plan.Notes.Add("Nessuna azione: non ci sono gambe sopravvissute da operare.");
        }

        ctx.Plan = plan;
        return Task.CompletedTask;
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.Plan ?? new ExecutionPlan();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"Piano {o.Mode}: {o.Actions.Count} azioni. {string.Join(" ", o.Notes)}",
            Metrics = new() { ["Azioni"] = o.Actions.Count },
        };
    }
}
