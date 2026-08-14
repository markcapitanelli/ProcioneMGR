using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Pipeline.Stages;
using ProcioneMGR.Services.Portfolio;

namespace ProcioneMGR.Tests;

/// <summary>
/// [T1+T2, PRD memoria-caccia 2026-08-14] La fascia grigia entra nell'assemblaggio SOLO dietro
/// <c>includeGreyZone</c> (default false), SOLO nei posti che i sopravvissuti lasciano liberi,
/// SEMPRE etichettata; e la ridondanza fra gambe (la matrice che l'HRP calcolava e scartava)
/// esce dichiarata nella proposta. Le proprietà che contano:
///   1. REGRESSIONE: a flag spento (o assente) il comportamento è quello storico — con
///      sopravvissuti i pesi sono identici, senza sopravvissuti nessuna proposta;
///   2. un grigio non spiazza MAI un sopravvissuto, qualunque Sharpe abbia;
///   3. le gambe grigie portano SourceVerdict="Grey", il nome lo dice, la Note dichiara il
///      secondo giro di selezione;
///   4. il report di correlazione esiste, dichiara la finestra, e sopra soglia produce l'alert
///      nella raccomandazione; sotto i 30 giorni comuni dichiara l'incalcolabilità.
/// </summary>
public sealed class EnsembleAssemblyGreyZoneTests
{
    /// <summary>
    /// Curve equity deterministiche per strategia: "CloneA" e "CloneB" hanno lo STESSO profilo
    /// (ρ=1 per costruzione), "Ortho" un profilo decorrelato (periodicità 3 vs 2). "Short" produce
    /// una curva di soli 10 giorni per il ramo "storico comune insufficiente".
    /// </summary>
    private sealed class ScriptedEquityEngine : IBacktestEngine
    {
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, CancellationToken ct)
        {
            var curve = new List<EquityPoint>();
            var capital = 10_000m;
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var days = config.StrategyName == "Short" ? 10 : 90;
            for (var d = 0; d < days; d++)
            {
                var r = config.StrategyName switch
                {
                    "CloneA" or "CloneB" => d % 2 == 0 ? 0.0045m : -0.0015m,
                    "Ortho" => d % 3 == 0 ? 0.0130m : -0.0050m,
                    _ => d % 2 == 0 ? 0.0030m : -0.0010m,
                };
                capital *= 1m + r;
                curve.Add(new EquityPoint { Timestamp = t0.AddDays(d), Capital = capital });
            }
            return Task.FromResult(new BacktestResult { EquityCurve = curve, CandlesEvaluated = days });
        }

        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, CancellationToken ct)
            => RunBacktestAsync(config, ct);

        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, IStrategy strategy, CancellationToken ct)
            => RunBacktestAsync(config, ct);
    }

    private sealed class FakeRules : IPipelineRulesProvider
    {
        public PipelineRuleSet GetRules() => new();
        public string RulesFilePath => "(fake)";
    }

    private static EnsembleAssemblyStage Stage() => new(
        new ScriptedEquityEngine(),
        [new HierarchicalRiskParityOptimizer(new HierarchicalClustering()), new MeanVarianceOptimizer(), new RiskParityOptimizer()],
        new FakeRules());

    private static ValidatedCandidate Survivor(string name, decimal wfSharpe) => new()
    {
        StrategyName = name, Symbol = "AAA/USDT", Timeframe = "1h",
        Survived = true, WalkForwardOosSharpe = wfSharpe, HoldoutSharpe = 1.2m, HoldoutTrades = 30,
    };

    private static ValidatedCandidate Grey(string name, decimal wfSharpe) => new()
    {
        StrategyName = name, Symbol = "AAA/USDT", Timeframe = "1h",
        Survived = false, WalkForwardOosSharpe = wfSharpe, HoldoutSharpe = 0.9m, HoldoutTrades = 8,
        RejectReason = "Solo 8 trade in holdout (< 10)",
    };

    private static StageConfig Cfg(bool? includeGrey = null, int? maxLegs = null)
    {
        var cfg = new StageConfig();
        if (includeGrey is not null) cfg.Parameters["includeGreyZone"] = includeGrey.Value ? "true" : "false";
        if (maxLegs is not null) cfg.Parameters["maxLegs"] = maxLegs.Value.ToString();
        return cfg;
    }

    // ------------------------------------------------------------------ 1. regressione a flag spento

    [Fact]
    public async Task Default_WithSurvivors_IdenticalToExplicitFalse()
    {
        var ctxDefault = new PipelineContext { Validated = [Survivor("CloneA", 2.0m), Survivor("Ortho", 1.5m), Grey("Extra", 3.0m)] };
        var ctxExplicit = new PipelineContext { Validated = [Survivor("CloneA", 2.0m), Survivor("Ortho", 1.5m), Grey("Extra", 3.0m)] };

        await Stage().ExecuteAsync(ctxDefault, Cfg(), CancellationToken.None);
        await Stage().ExecuteAsync(ctxExplicit, Cfg(includeGrey: false), CancellationToken.None);

        Assert.Equal(
            ctxDefault.Ensemble!.Legs.Select(l => (l.StrategyName, l.WeightPercent)),
            ctxExplicit.Ensemble!.Legs.Select(l => (l.StrategyName, l.WeightPercent)));
        // Il grigio col miglior Sharpe walk-forward del run NON e' entrato: flag spento.
        Assert.DoesNotContain(ctxDefault.Ensemble.Legs, l => l.StrategyName == "Extra");
        Assert.All(ctxDefault.Ensemble.Legs, l => Assert.Equal("Survived", l.SourceVerdict));
    }

    [Fact]
    public async Task FlagOff_ZeroSurvivors_NoProposal_ButDeclaredInLog()
    {
        var logLines = new List<string>();
        var ctx = new PipelineContext { Validated = [Grey("CloneA", 1.0m), Grey("Ortho", 0.8m)], Log = logLines.Add };

        await Stage().ExecuteAsync(ctx, Cfg(), CancellationToken.None);

        // Come lo skip storico: nessuna proposta, nessun artifact — ma il log dice cosa c'era.
        Assert.Null(ctx.Ensemble);
        Assert.Contains(logLines, l => l.Contains("ESCLUSI") && l.Contains("2"));
    }

    // ------------------------------------------------------------------ 2. il gate d'ingresso

    [Fact]
    public void ValidateInput_NoSurvivorsNoGreys_Skips()
    {
        var ctx = new PipelineContext
        {
            Validated = [new ValidatedCandidate { StrategyName = "X", Symbol = "AAA/USDT", Timeframe = "1h", Survived = false, HoldoutSharpe = -1m }],
        };
        Assert.NotNull(Stage().ValidateInput(ctx));
    }

    [Fact]
    public void ValidateInput_GreysOnly_Runs()
    {
        var ctx = new PipelineContext { Validated = [Grey("CloneA", 1.0m)] };
        Assert.Null(Stage().ValidateInput(ctx));
    }

    // ------------------------------------------------------------------ 3. inclusione etichettata

    [Fact]
    public async Task FlagOn_GreysOnly_ProposesLabelledLegs()
    {
        var ctx = new PipelineContext { Validated = [Grey("CloneA", 1.0m), Grey("Ortho", 0.8m)] };

        await Stage().ExecuteAsync(ctx, Cfg(includeGrey: true), CancellationToken.None);

        var proposal = ctx.Ensemble!;
        Assert.Equal(2, proposal.Legs.Count);
        Assert.All(proposal.Legs, l => Assert.Equal("Grey", l.SourceVerdict));
        Assert.All(proposal.Legs, l => Assert.Contains("(fascia grigia)", l.DisplayName));
        Assert.NotNull(proposal.Note);
        Assert.Contains("2 gambe da fascia grigia su 2 ammissibili", proposal.Note);
        Assert.Contains("non sopravvissuti pieni", proposal.Note);
        Assert.Equal(100m, Math.Round(proposal.Legs.Sum(l => l.WeightPercent)));
    }

    [Fact]
    public async Task GreyNeverDisplacesSurvivor()
    {
        // Il grigio ha Sharpe walk-forward SCHIACCIANTE (5.0 contro 1.0/0.9) — e resta fuori:
        // sono verdetti di rango diverso, non punteggi sulla stessa scala.
        var ctx = new PipelineContext
        {
            Validated = [Survivor("CloneA", 1.0m), Survivor("Ortho", 0.9m), Grey("Extra", 5.0m)],
        };

        await Stage().ExecuteAsync(ctx, Cfg(includeGrey: true, maxLegs: 2), CancellationToken.None);

        Assert.Equal(2, ctx.Ensemble!.Legs.Count);
        Assert.DoesNotContain(ctx.Ensemble.Legs, l => l.StrategyName == "Extra");
    }

    [Fact]
    public async Task GreyFillsRemainingSlots_AfterSurvivors()
    {
        var ctx = new PipelineContext
        {
            Validated = [Survivor("CloneA", 1.0m), Grey("Ortho", 0.8m), Grey("Extra", 0.5m)],
        };

        await Stage().ExecuteAsync(ctx, Cfg(includeGrey: true, maxLegs: 2), CancellationToken.None);

        Assert.Equal(2, ctx.Ensemble!.Legs.Count);
        Assert.Equal("Survived", ctx.Ensemble.Legs.Single(l => l.StrategyName == "CloneA").SourceVerdict);
        // Fra i due grigi entra quello con Sharpe walk-forward migliore (scelta deterministica).
        Assert.Equal("Grey", ctx.Ensemble.Legs.Single(l => l.StrategyName == "Ortho").SourceVerdict);
    }

    // ------------------------------------------------------------------ 4. ridondanza dichiarata

    [Fact]
    public async Task ClonedLegs_ProduceCorrelationReport_AboveThreshold()
    {
        var ctx = new PipelineContext { Validated = [Survivor("CloneA", 2.0m), Survivor("CloneB", 1.8m)] };

        await Stage().ExecuteAsync(ctx, Cfg(), CancellationToken.None);

        var report = ctx.Ensemble!.Correlations;
        Assert.NotNull(report);
        Assert.Null(report.Note);
        var pair = Assert.Single(report.Pairs);
        Assert.True(Math.Abs(pair.Rho) >= report.WarnThreshold,
            $"gambe clonate: attesa ρ≈1, misurata {pair.Rho}");
        Assert.Single(report.AboveThreshold);
        Assert.Contains("giorni comuni", report.Window);
    }

    [Fact]
    public async Task DecorrelatedLegs_NoWarning()
    {
        var ctx = new PipelineContext { Validated = [Survivor("CloneA", 2.0m), Survivor("Ortho", 1.5m)] };

        await Stage().ExecuteAsync(ctx, Cfg(), CancellationToken.None);

        var report = ctx.Ensemble!.Correlations;
        Assert.NotNull(report);
        Assert.Empty(report.AboveThreshold); // profili a periodicita' 2 vs 3: decorrelati per costruzione
    }

    [Fact]
    public async Task InsufficientCommonHistory_DeclaresItself()
    {
        // "Short" produce 10 giorni: sotto il minimo l'assemblaggio va in EqualWeight e il report
        // DICHIARA l'incalcolabilita' invece di sparire.
        var ctx = new PipelineContext { Validated = [Survivor("Short", 2.0m), Survivor("CloneA", 1.5m)] };

        await Stage().ExecuteAsync(ctx, Cfg(), CancellationToken.None);

        Assert.Equal("EqualWeight", ctx.Ensemble!.Method);
        var report = ctx.Ensemble.Correlations;
        Assert.NotNull(report);
        Assert.Empty(report.Pairs);
        Assert.NotNull(report.Note);
        Assert.Contains("non calcolabile", report.Note);
    }

    // ------------------------------------------------------------------ 5. la raccomandazione rilancia

    [Fact]
    public async Task Recommendation_CarriesCorrelationAlert_AndGreyDeclaration()
    {
        var ctx = new PipelineContext
        {
            Validated = [Survivor("CloneA", 2.0m), Grey("Ortho", 0.8m)],
            Ensemble = new EnsembleProposal
            {
                Legs =
                [
                    new ProposedLeg { StrategyName = "CloneA", DisplayName = "CloneA AAA/USDT 1h [base]", Symbol = "AAA/USDT", Timeframe = "1h", WeightPercent = 60m, SourceVerdict = "Survived" },
                    new ProposedLeg { StrategyName = "Ortho", DisplayName = "Ortho AAA/USDT 1h [base] (fascia grigia)", Symbol = "AAA/USDT", Timeframe = "1h", WeightPercent = 40m, SourceVerdict = "Grey" },
                ],
                Correlations = new LegCorrelationReport
                {
                    WarnThreshold = 0.7,
                    Window = "selezione 2026-01-01→2026-06-01, 118 giorni comuni",
                    Pairs = [new LegCorrelationPair { KeyA = "A", DisplayA = "CloneA", KeyB = "B", DisplayB = "Ortho", Rho = 0.91 }],
                },
            },
        };

        await new RecommendationStage(new FakeRules()).ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        var rec = ctx.Recommendation!;
        Assert.Contains(rec.Alerts, a => a.Contains("Gambe correlate") && a.Contains("0,91") || a.Contains("0.91"));
        Assert.Contains(rec.Alerts, a => a.Contains("FASCIA GRIGIA") && a.Contains("1 delle 2"));
    }

    [Fact]
    public async Task RiskSizing_GreyZeros_StayOutOfAverages()
    {
        // [Review 2026-08-14] 1 sopravvissuto misurato (RF95 2,4×, half-Kelly 4%) + 1 grigio mai
        // sondato: le medie devono restare quelle del sopravvissuto, non diluirsi a metà — un
        // "RF95 medio" più basso del misurato farebbe sembrare il sistema PIÙ sicuro.
        var survivor = new ValidatedCandidate
        {
            StrategyName = "Steady", Symbol = "AAA/USDT", Timeframe = "1h", Survived = true,
            HalfKelly = 0.04m, KellyFraction = 0.08m, MonteCarloRiskFactor95 = 2.4m, MonteCarloDrawdown95 = 950m,
        };
        var grey = new ValidatedCandidate
        {
            StrategyName = "Ortho", Symbol = "AAA/USDT", Timeframe = "1h", Survived = false,
            HoldoutSharpe = 0.9m, HoldoutTrades = 8, RejectReason = "Solo 8 trade in holdout (< 10)",
        };
        var ctx = new PipelineContext
        {
            InitialCapital = 10_000m,
            Validated = [survivor, grey],
            Ensemble = new EnsembleProposal
            {
                Legs =
                [
                    new ProposedLeg { StrategyName = "Steady", Symbol = "AAA/USDT", Timeframe = "1h", WeightPercent = 60m, SourceVerdict = "Survived" },
                    new ProposedLeg { StrategyName = "Ortho", Symbol = "AAA/USDT", Timeframe = "1h", WeightPercent = 40m, SourceVerdict = "Grey" },
                ],
            },
        };

        await new RiskSizingStage(new FakeRules()).ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        Assert.Equal(2.4m, ctx.Risk!.AverageRiskFactor95);   // non 1,2: lo zero del grigio resta fuori
        Assert.Equal(0.04m, ctx.Risk.AverageHalfKelly);
        Assert.True(ctx.Risk.ShutdownDrawdownPercent > 0m);   // la guardia viene dal solo misurato
        Assert.True(ctx.Ensemble.Legs[1].SizingPercent > 0m); // il grigio ha comunque il sizing conservativo
        Assert.Contains(ctx.Risk.Notes, n => n.Contains("sole gambe misurate"));
    }

    [Fact]
    public async Task RiskSizing_GreyOnly_DeclaresGuardNotEstimable()
    {
        var grey = new ValidatedCandidate
        {
            StrategyName = "Ortho", Symbol = "AAA/USDT", Timeframe = "1h", Survived = false,
            HoldoutSharpe = 0.9m, HoldoutTrades = 8, RejectReason = "Solo 8 trade in holdout (< 10)",
        };
        var ctx = new PipelineContext
        {
            InitialCapital = 10_000m,
            Validated = [grey],
            Ensemble = new EnsembleProposal
            {
                Legs = [new ProposedLeg { StrategyName = "Ortho", Symbol = "AAA/USDT", Timeframe = "1h", WeightPercent = 100m, SourceVerdict = "Grey" }],
            },
        };

        await new RiskSizingStage(new FakeRules()).ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        // Gli zeri NON devono sembrare misure: la nota lo dice con tutte le lettere.
        Assert.Contains(ctx.Risk!.Notes, n => n.Contains("NON stimabili"));
    }

    [Fact]
    public async Task Recommendation_GreyOnly_ActionsFollowTheLegs_NotTheDeadEnd()
    {
        // [Review 2026-08-14] Zero sopravvissuti ma gambe grigie proposte: il report non può dire
        // "NON operare" tre righe sotto l'ENSEMBLE PROPOSTO — le azioni seguono le gambe, con la
        // natura grigia dichiarata.
        var ctx = new PipelineContext
        {
            Validated = [Grey("Ortho", 0.8m)],
            Ensemble = new EnsembleProposal
            {
                Legs = [new ProposedLeg { StrategyName = "Ortho", DisplayName = "Ortho AAA/USDT 1h [base] (fascia grigia)", Symbol = "AAA/USDT", Timeframe = "1h", WeightPercent = 100m, SourceVerdict = "Grey" }],
            },
        };

        await new RecommendationStage(new FakeRules()).ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        var actions = ctx.Recommendation!.SuggestedActions;
        Assert.Contains(actions, a => a.StartsWith("Paper trading:") && a.Contains("Ortho"));
        Assert.Contains(actions, a => a.Contains("FASCIA GRIGIA") && a.Contains("Paper"));
        Assert.DoesNotContain(actions, a => a.Contains("NON operare"));
    }

    [Fact]
    public async Task Recommendation_NoLegsAtAll_StillSaysDoNotOperate()
    {
        var ctx = new PipelineContext { Validated = [] };

        await new RecommendationStage(new FakeRules()).ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        Assert.Contains(ctx.Recommendation!.SuggestedActions, a => a.Contains("NON operare"));
    }

    [Fact]
    public async Task Recommendation_NoCorrelationAlert_BelowThreshold()
    {
        var ctx = new PipelineContext
        {
            Validated = [Survivor("CloneA", 2.0m)],
            Ensemble = new EnsembleProposal
            {
                Legs = [new ProposedLeg { StrategyName = "CloneA", DisplayName = "CloneA", Symbol = "AAA/USDT", Timeframe = "1h", WeightPercent = 100m, SourceVerdict = "Survived" }],
                Correlations = new LegCorrelationReport
                {
                    WarnThreshold = 0.7,
                    Pairs = [new LegCorrelationPair { KeyA = "A", DisplayA = "x", KeyB = "B", DisplayB = "y", Rho = 0.30 }],
                },
            },
        };

        await new RecommendationStage(new FakeRules()).ExecuteAsync(ctx, new StageConfig(), CancellationToken.None);

        Assert.DoesNotContain(ctx.Recommendation!.Alerts, a => a.Contains("Gambe correlate"));
        Assert.DoesNotContain(ctx.Recommendation.Alerts, a => a.Contains("FASCIA GRIGIA"));
    }
}
