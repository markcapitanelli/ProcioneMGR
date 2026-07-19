using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Pipeline.Stages;
using ProcioneMGR.Services.TimeSeries;

namespace ProcioneMGR.Tests;

// ============================================================================
// Test doubles (no DB, no network)
// ============================================================================

/// <summary>Candle cache backed by an in-memory dictionary of synthetic series.</summary>
file sealed class FakeCandleCache : IPipelineCandleCache
{
    private readonly Dictionary<string, List<OhlcvData>> _series = new();

    public void Add(string symbol, string timeframe, List<OhlcvData> candles)
        => _series[$"{symbol}|{timeframe}"] = candles;

    public Task<IReadOnlyList<OhlcvData>> GetAsync(string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct)
    {
        var all = _series.TryGetValue($"{symbol}|{timeframe}", out var list) ? list : new List<OhlcvData>();
        IReadOnlyList<OhlcvData> filtered = all.Where(c => c.TimestampUtc >= from && c.TimestampUtc <= to).ToList();
        return Task.FromResult(filtered);
    }
}

file sealed class FakeRulesProvider : IPipelineRulesProvider
{
    public PipelineRuleSet Rules { get; set; } = new();
    public string RulesFilePath => "fake://pipeline_rules.json";
    public PipelineRuleSet GetRules() => Rules;
}

file static class Synthetic
{
    /// <summary>Seeded random-walk OHLCV series (deterministic).</summary>
    public static List<OhlcvData> RandomWalk(string symbol, string timeframe, DateTime start, int count, int seed, decimal startPrice = 100m)
    {
        var rng = new Random(seed);
        var candles = new List<OhlcvData>(count);
        var price = startPrice;
        var step = timeframe == "1d" ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);
        for (var i = 0; i < count; i++)
        {
            var drift = (decimal)(rng.NextDouble() * 2.0 - 1.0) * 0.02m * price;
            var open = price;
            var close = Math.Max(1m, price + drift);
            candles.Add(new OhlcvData
            {
                Symbol = symbol,
                Timeframe = timeframe,
                TimestampUtc = start + i * step,
                Open = open,
                High = Math.Max(open, close) * 1.005m,
                Low = Math.Min(open, close) * 0.995m,
                Close = close,
                Volume = 100m + (decimal)rng.NextDouble() * 50m,
            });
            price = close;
        }
        return candles;
    }
}

// ============================================================================
// StageConfig typed parameter access
// ============================================================================

public class StageConfigExtensionsTests
{
    [Fact]
    public void TypedGetters_ParseInvariantCulture_AndFallBack()
    {
        var cfg = new StageConfig
        {
            Parameters = new()
            {
                ["dec"] = "0.25",
                ["int"] = "42",
                ["bool"] = "true",
                ["list"] = "A, B,C",
                ["garbage"] = "not-a-number",
            },
        };
        Assert.Equal(0.25m, cfg.GetDecimal("dec", 1m));
        Assert.Equal(42, cfg.GetInt("int", 0));
        Assert.True(cfg.GetBool("bool", false));
        Assert.Equal(new List<string> { "A", "B", "C" }, cfg.GetList("list"));
        Assert.Equal(9m, cfg.GetDecimal("garbage", 9m));   // unparsable -> fallback
        Assert.Equal(7, cfg.GetInt("missing", 7));          // missing -> fallback
        Assert.Empty(cfg.GetList("missing"));
    }
}

// ============================================================================
// DAG validation
// ============================================================================

public class PipelineDagValidatorTests
{
    private static readonly Dictionary<string, IReadOnlyList<StageDependency>> Deps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = [],
        ["B"] = [StageDependency.On("A")],
        ["C"] = [StageDependency.On("A", "B")], // any of A or B
    };
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = "Stage A", ["B"] = "Stage B", ["C"] = "Stage C",
    };

    private static StageConfig S(string type, int order, bool enabled = true)
        => new() { Type = type, Order = order, Enabled = enabled };

    [Fact]
    public void ValidChain_NoProblems()
    {
        var problems = PipelineDagValidator.Validate([S("A", 1), S("B", 2), S("C", 3)], Deps, Names);
        Assert.Empty(problems);
    }

    [Fact]
    public void MissingDependency_IsReported()
    {
        // B enabled but its dependency A is disabled.
        var problems = PipelineDagValidator.Validate([S("A", 1, enabled: false), S("B", 2)], Deps, Names);
        Assert.Contains(problems, p => p.Contains("Stage B"));
    }

    [Fact]
    public void AnyOfDependency_SatisfiedByEitherStage()
    {
        // C depends on (A OR B): only B enabled -> still valid.
        var problems = PipelineDagValidator.Validate([S("A", 1, enabled: false), S("B", 2), S("C", 3)], Deps, Names);
        // B itself now lacks A -> 1 problem for B, but none for C.
        Assert.DoesNotContain(problems, p => p.Contains("Stage C"));
    }

    [Fact]
    public void DependencyOrderedAfter_IsNotSatisfied()
    {
        // A exists but comes AFTER B: execution order matters, not mere presence.
        var problems = PipelineDagValidator.Validate([S("B", 1), S("A", 2)], Deps, Names);
        Assert.Contains(problems, p => p.Contains("Stage B"));
    }

    [Fact]
    public void UnknownStage_IsReported()
    {
        var problems = PipelineDagValidator.Validate([S("A", 1), S("Zzz", 2)], Deps, Names);
        Assert.Contains(problems, p => p.Contains("Zzz"));
    }

    [Fact]
    public void NoEnabledStages_IsReported()
    {
        var problems = PipelineDagValidator.Validate([S("A", 1, enabled: false)], Deps, Names);
        Assert.Contains(problems, p => p.Contains("Nessuno stage abilitato"));
    }
}

// ============================================================================
// Robustness helpers (stop variants, profit factor)
// ============================================================================

public class PipelineHelperTests
{
    [Theory]
    [InlineData("SL3", 3, 0)]
    [InlineData("SL5", 5, 0)]
    [InlineData("TRAIL5", 0, 5)]
    [InlineData("base", 0, 0)]
    [InlineData("qualcosa", 0, 0)]
    public void ApplyVariant_SetsTheRightStops(string variant, decimal expectedSl, decimal expectedTrail)
    {
        var cfg = new BacktestConfiguration();
        RobustnessProbeStage.ApplyVariant(cfg, variant);
        Assert.Equal(expectedSl, cfg.StopLossPercent);
        Assert.Equal(expectedTrail, cfg.TrailingStopPercent);
    }

    [Fact]
    public void ProfitFactor_ComputedFromTrades()
    {
        var trades = new List<BacktestTrade>
        {
            new() { Pnl = 100m }, new() { Pnl = 50m }, new() { Pnl = -50m },
        };
        Assert.Equal(3m, HoldoutValidationStage.ProfitFactor(trades));
        Assert.Equal(0m, HoldoutValidationStage.ProfitFactor([]));
        Assert.Equal(999m, HoldoutValidationStage.ProfitFactor([new BacktestTrade { Pnl = 10m }])); // no losses
    }
}

// ============================================================================
// Context snapshot round-trip (the checkpoint must not lose state)
// ============================================================================

public class PipelineContextSnapshotTests
{
    [Fact]
    public void Context_RoundTripsThroughJson()
    {
        var ctx = new PipelineContext
        {
            RunId = Guid.NewGuid(),
            ExchangeName = "Binance",
            Universe = [new SeriesSpec { Symbol = "BTC/USDT", Timeframe = "1h" }],
            Ranges = new PipelineDateRanges
            {
                SelectionFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                SelectionTo = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                HoldoutFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                HoldoutTo = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            Seed = 123,
            Validated = [new ValidatedCandidate { StrategyName = "EmaCross", Symbol = "BTC/USDT", Timeframe = "1h", Survived = true, HoldoutSharpe = 1.5m }],
            Recommendation = new PipelineRecommendation { RegimeLabel = "Choppy", Survivors = 1, FullText = "REGIME: Choppy" },
            StageSummaries = [new StageSummary { StageName = "DataIngestion", Status = StageStatus.Completed, Text = "ok" }],
        };

        var json = System.Text.Json.JsonSerializer.Serialize(ctx);
        var restored = System.Text.Json.JsonSerializer.Deserialize<PipelineContext>(json);

        Assert.NotNull(restored);
        Assert.Equal(ctx.RunId, restored!.RunId);
        Assert.Equal(123, restored.Seed);
        Assert.Single(restored.Validated);
        Assert.True(restored.Validated[0].Survived);
        Assert.Equal(1.5m, restored.Validated[0].HoldoutSharpe);
        Assert.Equal("Choppy", restored.Recommendation!.RegimeLabel);
        Assert.Single(restored.StageSummaries);
        Assert.Equal(StageStatus.Completed, restored.StageSummaries[0].Status);
    }
}

// ============================================================================
// Decision stages (the deterministic "brain") — fabricated contexts, no I/O
// ============================================================================

public class RecommendationStageTests
{
    private static PipelineContext ContextWithSurvivor()
    {
        var leg = new ProposedLeg
        {
            StrategyName = "RsiOversold",
            DisplayName = "RsiOversold DOT/USDT 15m [base]",
            Symbol = "DOT/USDT",
            Timeframe = "15m",
            WeightPercent = 100m,
            SizingPercent = 6m,
        };
        return new PipelineContext
        {
            Regimes = new RegimeOutput { CurrentRegimeId = 1, CurrentRegimeLabel = "Choppy/Volatile" },
            Volatility = new VolatilityOutput { Level = "Media", ForecastVolatility24 = 0.01, LongRunVolatility = 0.01 },
            AltData = new AltDataOutput { AvgSentimentLast24h = 0.3 },
            Validated =
            [
                new ValidatedCandidate
                {
                    StrategyName = "RsiOversold", Symbol = "DOT/USDT", Timeframe = "15m",
                    Survived = true, HoldoutSharpe = 4.05m, HoldoutProfitFactor = 5.76m, HoldoutMaxDrawdown = 4.7m,
                },
                new ValidatedCandidate { StrategyName = "EmaCross", Symbol = "BTC/USDT", Timeframe = "1h", Survived = false, RejectReason = "Sharpe" },
            ],
            Ensemble = new EnsembleProposal { Legs = [leg], Method = "EqualWeight" },
            Risk = new RiskAssessment
            {
                AverageHalfKelly = 0.118m,
                AverageRiskFactor95 = 1.85m,
                ShutdownDrawdownPercent = 9.5m,
                SuggestedStopLossPercent = 3m,
            },
        };
    }

    [Fact]
    public async Task WithSurvivor_TemplateContainsTheNumbers()
    {
        var stage = new RecommendationStage(new FakeRulesProvider());
        var ctx = ContextWithSurvivor();
        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        var rec = ctx.Recommendation!;
        Assert.Equal("Choppy/Volatile", rec.RegimeLabel);
        Assert.Equal("positivo", rec.SentimentLabel); // 0.3 > default 0.15
        Assert.Equal(1, rec.Survivors);
        Assert.Contains("RsiOversold DOT/USDT 15m", rec.BestCandidate);
        Assert.Contains("REGIME: Choppy/Volatile", rec.FullText);
        Assert.Contains("SOPRAVVISSUTI HOLDOUT: 1", rec.FullText);
        Assert.Contains("1,85", rec.FullText.Replace("1.85", "1,85")); // culture-agnostic check on RF95
        Assert.Contains(rec.SuggestedActions, a => a.Contains("Paper trading"));
    }

    [Fact]
    public async Task NoSurvivors_SaysDoNotTrade()
    {
        var stage = new RecommendationStage(new FakeRulesProvider());
        var ctx = new PipelineContext
        {
            Validated = [new ValidatedCandidate { Survived = false, RejectReason = "Sharpe" }],
        };
        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);
        Assert.Contains(ctx.Recommendation!.SuggestedActions, a => a.Contains("NON operare"));
    }

    [Fact]
    public async Task Deterministic_SameContextSameText()
    {
        var stage = new RecommendationStage(new FakeRulesProvider());
        var ctx1 = ContextWithSurvivor();
        var ctx2 = ContextWithSurvivor();
        await stage.ExecuteAsync(ctx1, new StageConfig(), CancellationToken.None);
        await stage.ExecuteAsync(ctx2, new StageConfig(), CancellationToken.None);
        Assert.Equal(ctx1.Recommendation!.FullText, ctx2.Recommendation!.FullText);
    }

    [Fact]
    public async Task WithMoodSnapshot_LabelComesFromComposite_AndFieldsArePopulated()
    {
        var stage = new RecommendationStage(new FakeRulesProvider());
        var ctx = ContextWithSurvivor();
        // News legacy negative (-0.5) ma composite positivo: la label deve venire dal composite.
        ctx.AltData!.AvgSentimentLast24h = -0.5;
        ctx.AltData.Snapshot = new ProcioneMGR.Services.Sentiment.SentimentSnapshot
        {
            CompositeScore = 0.4,
            FearGreedValue = 72,
            FearGreedLabel = "Greed",
            Symbols = [new ProcioneMGR.Services.Sentiment.SymbolSentiment { Symbol = "BTC", Composite = 0.5, FundingZ = 1.2 }],
        };

        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        var rec = ctx.Recommendation!;
        Assert.Equal("positivo", rec.SentimentLabel);
        Assert.Equal(0.4, rec.SentimentComposite);
        Assert.Equal(72.0, rec.FearGreedValue);
        Assert.Contains("composite", rec.FullText);
        Assert.Contains("Fear&Greed 72", rec.FullText);
        Assert.Contains("BTC: mood", rec.FullText);
    }

    [Fact]
    public async Task WithMoodExtremes_TheyBecomeAlerts_AndAppearInFullText()
    {
        var stage = new RecommendationStage(new FakeRulesProvider());
        var ctx = ContextWithSurvivor();
        ctx.AltData!.Snapshot = new ProcioneMGR.Services.Sentiment.SentimentSnapshot
        {
            CompositeScore = 0.9,
            Extremes = ["Fear & Greed 85 (extreme greed): euforia, storicamente zona contrarian di rischio correzione."],
        };

        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        var rec = ctx.Recommendation!;
        Assert.Single(rec.SentimentExtremes);
        Assert.Contains(rec.Alerts, a => a.StartsWith("Mood:") && a.Contains("extreme greed"));
        Assert.Contains("extreme greed", rec.FullText);
    }

    [Fact]
    public async Task WithoutSnapshot_LegacyPathIsUnchanged()
    {
        var stage = new RecommendationStage(new FakeRulesProvider());
        var ctx = ContextWithSurvivor(); // Snapshot null, AvgSentimentLast24h 0.3

        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        var rec = ctx.Recommendation!;
        Assert.Equal("positivo", rec.SentimentLabel);
        Assert.Null(rec.SentimentComposite);
        Assert.Null(rec.FearGreedValue);
        Assert.Empty(rec.SentimentExtremes);
        Assert.Contains("SENTIMENT: 0", rec.FullText); // riga legacy
    }
}

public class RiskSizingStageTests
{
    private static PipelineContext BuildContext(string volLevel)
    {
        return new PipelineContext
        {
            InitialCapital = 10_000m,
            Volatility = new VolatilityOutput { Level = volLevel },
            Validated =
            [
                new ValidatedCandidate
                {
                    StrategyName = "RsiOversold", Symbol = "DOT/USDT", Timeframe = "15m",
                    Survived = true, KellyFraction = 0.236m, HalfKelly = 0.118m,
                    MonteCarloRiskFactor95 = 1.85m, MonteCarloDrawdown95 = 950m, BestStopVariant = "SL3",
                },
            ],
            Ensemble = new EnsembleProposal
            {
                Legs = [new ProposedLeg { StrategyName = "RsiOversold", Symbol = "DOT/USDT", Timeframe = "15m", WeightPercent = 100m }],
            },
        };
    }

    [Fact]
    public async Task NormalVol_SizingIsFractionalKellyCapped()
    {
        var rules = new FakeRulesProvider();
        var stage = new RiskSizingStage(rules);
        var ctx = BuildContext("Media");
        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        // Kelly 23.6% * 0.5 = 11.8% -> capped at MaxSizingPercent (10).
        Assert.Equal(10m, ctx.Ensemble!.Legs[0].SizingPercent);
        Assert.Equal(1m, ctx.Risk!.VolatilitySizingFactor);
        Assert.Equal(9.5m, ctx.Risk.ShutdownDrawdownPercent); // 950 / 10000 * 100
        Assert.Equal(3m, ctx.Risk.SuggestedStopLossPercent);  // from the SL3 variant
    }

    [Fact]
    public async Task HighVol_ReducesSizing()
    {
        var rules = new FakeRulesProvider();
        var stage = new RiskSizingStage(rules);
        var ctx = BuildContext("Alta");
        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        // Kelly 23.6% * 0.5 = 11.8%, * 0.7 (high-vol reduction 30%) = 8.26 -> under the cap.
        Assert.Equal(0.7m, ctx.Risk!.VolatilitySizingFactor);
        Assert.True(ctx.Ensemble!.Legs[0].SizingPercent < 10m);
        Assert.Contains(ctx.Risk.Notes, n => n.Contains("ALTA"));
    }

    /// <summary>
    /// Regression: creative discovery can confirm TWO distinct specs of the SAME meta-strategy
    /// on the SAME pair (e.g. two different "Composite" rules) — a real live bug where
    /// (a) ToDictionary threw "same key already added" before the parameter fingerprint was
    /// added to Key, then (b) after that fix, this stage rebuilt its OWN short key inline
    /// instead of reusing ProposedLeg.Key, silently failing every lookup (half-Kelly/RF95
    /// stayed 0 for every leg without any exception — caught only by inspecting live output).
    /// </summary>
    [Fact]
    public async Task DuplicateStrategySymbolTimeframe_DifferentParameters_BothSizedCorrectly()
    {
        var ctx = new PipelineContext
        {
            InitialCapital = 10_000m,
            Volatility = new VolatilityOutput { Level = "Media" },
            Validated =
            [
                new ValidatedCandidate
                {
                    StrategyName = "Composite", Symbol = "NEAR/USDT", Timeframe = "4h",
                    Parameters = new() { ["EntrySig1"] = 0m, ["EntryThr1"] = 30m },
                    Survived = true, KellyFraction = 0.20m, HalfKelly = 0.10m,
                    MonteCarloRiskFactor95 = 2.33m, MonteCarloDrawdown95 = 500m, BestStopVariant = "SL5",
                },
                new ValidatedCandidate
                {
                    StrategyName = "Composite", Symbol = "NEAR/USDT", Timeframe = "4h",
                    Parameters = new() { ["EntrySig1"] = 4m, ["EntryThr1"] = 70m },
                    Survived = true, KellyFraction = 0.14m, HalfKelly = 0.07m,
                    MonteCarloRiskFactor95 = 1.91m, MonteCarloDrawdown95 = 300m, BestStopVariant = "SL5",
                },
            ],
        };
        ctx.Ensemble = new EnsembleProposal
        {
            Legs =
            [
                new ProposedLeg { StrategyName = "Composite", Symbol = "NEAR/USDT", Timeframe = "4h", Parameters = new(ctx.Validated[0].Parameters), WeightPercent = 60m },
                new ProposedLeg { StrategyName = "Composite", Symbol = "NEAR/USDT", Timeframe = "4h", Parameters = new(ctx.Validated[1].Parameters), WeightPercent = 40m },
            ],
        };

        // Distinct parameters -> distinct keys, even though Strategy/Symbol/Timeframe collide.
        Assert.NotEqual(ctx.Validated[0].Key, ctx.Validated[1].Key);
        Assert.Equal(ctx.Validated[0].Key, ctx.Ensemble.Legs[0].Key); // ValidatedCandidate and ProposedLeg must agree

        var stage = new RiskSizingStage(new FakeRulesProvider());
        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        // Both legs must find their match and get a non-zero sizing (the bug produced 0 for both).
        Assert.True(ctx.Ensemble.Legs[0].SizingPercent > 0m);
        Assert.True(ctx.Ensemble.Legs[1].SizingPercent > 0m);
        Assert.True(ctx.Risk!.AverageRiskFactor95 > 0m);
        Assert.True(ctx.Risk.AverageHalfKelly > 0m);
    }
}

/// <summary>
/// <see cref="EnsembleAssemblyStage"/> con UNA sola gamba sopravvissuta: salta il ramo HRP
/// (richiede 2+ gambe) e va dritto a EqualWeight, quindi non serve un IBacktestEngine reale —
/// verifica solo che il BestStopVariant validato in walk-forward arrivi nel ProposedLeg (prima
/// di questo campo restava incorporato SOLO nella DisplayName, una stringa non parsabile in modo
/// affidabile dal wiring stop-loss di Pipeline.razor).
/// </summary>
public class EnsembleAssemblyStageTests
{
    private sealed class UnusedBacktestEngine : IBacktestEngine
    {
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, CancellationToken ct) => throw new NotImplementedException();
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, CancellationToken ct) => throw new NotImplementedException();
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, IStrategy strategy, CancellationToken ct) => throw new NotImplementedException();
    }

    [Fact]
    public async Task SingleLeg_CarriesBestStopVariantIntoProposedLeg()
    {
        var stage = new EnsembleAssemblyStage(
            new UnusedBacktestEngine(),
            new ProcioneMGR.Services.Portfolio.HierarchicalRiskParityOptimizer(new ProcioneMGR.Services.ML.HierarchicalClustering()),
            new FakeRulesProvider());

        var ctx = new PipelineContext
        {
            Validated =
            [
                new ValidatedCandidate
                {
                    StrategyName = "Momentum", Symbol = "NEAR/USDT", Timeframe = "4h",
                    Survived = true, WalkForwardOosSharpe = 1.8m, BestStopVariant = "SL5",
                },
            ],
        };

        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        var leg = Assert.Single(ctx.Ensemble!.Legs);
        Assert.Equal("SL5", leg.BestStopVariant);
        Assert.Contains("[SL5]", leg.DisplayName);
    }
}

public class ExecutionPlanStageTests
{
    [Fact]
    public async Task LiveMode_NeverAutoExecutes_AndWarnsAboutSafety()
    {
        var stage = new ExecutionPlanStage();
        var ctx = new PipelineContext
        {
            ExecutionMode = "Live",
            Recommendation = new PipelineRecommendation
            {
                EnsembleLegs = [new ProposedLeg { StrategyName = "EmaCross", Symbol = "BTC/USDT", Timeframe = "1h", DisplayName = "EmaCross BTC/USDT 1h", WeightPercent = 100m, SizingPercent = 5m }],
            },
        };
        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        Assert.Equal("Live", ctx.Plan!.Mode);
        Assert.Single(ctx.Plan.Actions);
        Assert.Contains(ctx.Plan.Notes, n => n.Contains("SafetyChecker"));
        Assert.Contains(ctx.Plan.Notes, n => n.Contains("NON viene eseguito automaticamente"));
    }

    [Fact]
    public async Task PaperMode_ProducesActionsFromLegs()
    {
        var stage = new ExecutionPlanStage();
        var ctx = new PipelineContext
        {
            ExecutionMode = "Paper",
            Recommendation = new PipelineRecommendation
            {
                EnsembleLegs =
                [
                    new ProposedLeg { StrategyName = "A", Symbol = "BTC/USDT", Timeframe = "1h", DisplayName = "A", SizingPercent = 5m },
                    new ProposedLeg { StrategyName = "B", Symbol = "ETH/USDT", Timeframe = "4h", DisplayName = "B", SizingPercent = 4m },
                ],
            },
        };
        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);
        Assert.Equal(2, ctx.Plan!.Actions.Count);
        Assert.Contains(ctx.Plan.Notes, n => n.Contains("Paper"));
    }
}

// ============================================================================
// Analysis stages on synthetic data (deterministic, no DB)
// ============================================================================

public class PipelineAnalysisStagesTests
{
    private static PipelineContext SyntheticContext(int seed = 7)
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cache = new FakeCandleCache();
        cache.Add("AAA/USDT", "1h", Synthetic.RandomWalk("AAA/USDT", "1h", start, 2000, seed));
        cache.Add("BBB/USDT", "1h", Synthetic.RandomWalk("BBB/USDT", "1h", start, 2000, seed + 1));
        return new PipelineContext
        {
            ExchangeName = "Binance",
            Universe =
            [
                new SeriesSpec { Symbol = "AAA/USDT", Timeframe = "1h" },
                new SeriesSpec { Symbol = "BBB/USDT", Timeframe = "1h" },
            ],
            Ranges = new PipelineDateRanges
            {
                SelectionFrom = start,
                SelectionTo = start.AddHours(1800),
                HoldoutFrom = start.AddHours(1800),
                HoldoutTo = start.AddHours(2000),
            },
            Candles = cache,
            Seed = seed,
        };
    }

    [Fact]
    public async Task FeatureEngineering_EvaluatesAndSelectsFactors_Deterministically()
    {
        var stage = new FeatureEngineeringStage(new AlphaFactorFactory(), new FactorEvaluator());
        var config = new StageConfig { Parameters = new() { ["topK"] = "3", ["minAbsIc"] = "0" } };

        var ctx1 = SyntheticContext();
        await stage.ExecuteAsync(ctx1, config, CancellationToken.None);
        var ctx2 = SyntheticContext();
        await stage.ExecuteAsync(ctx2, config, CancellationToken.None);

        Assert.NotNull(ctx1.Features);
        Assert.True(ctx1.Features!.Factors.Count >= 5, "tutti i fattori della libreria devono essere valutati");
        Assert.True(ctx1.Features.SelectedFactorNames.Count <= 3);
        // Determinism: same data + params -> identical ICs and selection.
        Assert.Equal(
            ctx1.Features.Factors.Select(f => (f.FactorName, f.InformationCoefficient)),
            ctx2.Features!.Factors.Select(f => (f.FactorName, f.InformationCoefficient)));
        Assert.Equal(ctx1.Features.SelectedFactorNames, ctx2.Features.SelectedFactorNames);
    }

    [Fact]
    public async Task VolatilityRegime_FitsGarchAndClassifies()
    {
        var stage = new VolatilityRegimeStage(new GarchModel());
        var ctx = SyntheticContext();
        // The stage reads a recent window up to UtcNow: re-anchor the synthetic series near now.
        var start = DateTime.UtcNow.AddHours(-1500);
        var cache = new FakeCandleCache();
        cache.Add("AAA/USDT", "1h", Synthetic.RandomWalk("AAA/USDT", "1h", start, 1500, 11));
        ctx.Candles = cache;

        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        Assert.NotNull(ctx.Volatility);
        Assert.True(ctx.Volatility!.CurrentVolatility > 0);
        Assert.Contains(ctx.Volatility.Level, new[] { "Bassa", "Media", "Alta" });
    }

    [Fact]
    public async Task PairsScreening_TestsEveryPairOnce()
    {
        var stage = new PairsScreeningStage(new EngleGrangerCointegrationTest());
        var ctx = SyntheticContext();
        await stage.ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        Assert.NotNull(ctx.Pairs);
        Assert.Single(ctx.Pairs!.Pairs); // 2 symbols -> exactly 1 unordered pair
        var pair = ctx.Pairs.Pairs[0];
        Assert.True(pair.AlignedCandles >= 200);
        // Two independent random walks: being NOT cointegrated is the expected outcome.
        Assert.False(pair.IsCointegrated);
    }

    [Fact]
    public void ValidateInput_PairsScreening_RequiresTwoSymbols()
    {
        var stage = new PairsScreeningStage(new EngleGrangerCointegrationTest());
        var ctx = new PipelineContext { Universe = [new SeriesSpec { Symbol = "AAA/USDT", Timeframe = "1h" }] };
        Assert.NotNull(stage.ValidateInput(ctx));
    }
}
