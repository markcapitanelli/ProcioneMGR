using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Pipeline.Stages;
using ProcioneMGR.Services.Portfolio;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2.8 PRD-RISANAMENTO, chiude C-05] L'allocatore dei pesi dell'ensemble è selezionabile per
/// nome (parametro di stage <c>portfolioOptimizer</c>) invece che HRP cablato come tipo concreto.
/// Le proprietà che contano:
///   1. REGRESSIONE: default e "HRP" esplicito producono pesi identici (il comportamento storico
///      non cambia per chi non tocca nulla);
///   2. la scelta CAMBIA davvero i pesi (MeanVariance ≠ HRP su gambe con profili diversi);
///   3. nome sconosciuto ⇒ HRP con dichiarazione nel log, mai un run rotto per un typo;
///   4. il Method della proposta dichiara l'optimizer REALE (prima era l'etichetta fissa "HRP").
/// </summary>
public sealed class PortfolioOptimizerSelectionTests
{
    // ------------------------------------------------------------------ fake engine

    /// <summary>Restituisce una curva equity GIORNALIERA deterministica per strategia: la gamba
    /// "Steady" sale costante (alto rendimento, bassa vol), la "Noisy" oscilla piatta (basso
    /// rendimento, alta vol) — profili opposti così HRP e MeanVariance DEVONO divergere.</summary>
    private sealed class ScriptedEquityEngine : IBacktestEngine
    {
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, CancellationToken ct)
        {
            var curve = new List<EquityPoint>();
            var capital = 10_000m;
            var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var steady = config.StrategyName == "Steady";
            for (var d = 0; d < 90; d++)
            {
                // Profili COMPARABILI (nessuno domina al punto da mandare entrambi gli optimizer
                // al tetto 100/0) e DECORRELATI (periodicità 2 vs 3, altrimenti la covarianza è
                // degenere e la Mean-Variance diventa instabile per costruzione):
                //   Steady: media 0,15%/g, vol ~0,30%  ·  Noisy: media ~0,10%/g, vol ~0,60%.
                var r = steady
                    ? (d % 2 == 0 ? 0.0045m : -0.0015m)
                    : (d % 3 == 0 ? 0.0130m : -0.0050m);
                capital *= 1m + r;
                curve.Add(new EquityPoint { Timestamp = t0.AddDays(d), Capital = capital });
            }
            return Task.FromResult(new BacktestResult { EquityCurve = curve, CandlesEvaluated = 90 });
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

    private static List<IPortfolioOptimizer> AllOptimizers()
    {
        var clustering = new HierarchicalClustering();
        return
        [
            new HierarchicalRiskParityOptimizer(clustering),
            new MeanVarianceOptimizer(),
            new RiskParityOptimizer(),
        ];
    }

    private static PipelineContext TwoLegContext() => new()
    {
        Validated =
        [
            new ValidatedCandidate { StrategyName = "Steady", Symbol = "AAA/USDT", Timeframe = "1h", Survived = true, WalkForwardOosSharpe = 2.0m },
            new ValidatedCandidate { StrategyName = "Noisy", Symbol = "BBB/USDT", Timeframe = "1h", Survived = true, WalkForwardOosSharpe = 1.5m },
        ],
    };

    private static StageConfig ConfigWith(string? optimizer) => optimizer is null
        ? new StageConfig()
        : new StageConfig { Parameters = { ["portfolioOptimizer"] = optimizer } };

    private static async Task<Dictionary<string, decimal>> RunAsync(string? optimizerName, PipelineContext? ctxOut = null)
    {
        var stage = new EnsembleAssemblyStage(new ScriptedEquityEngine(), AllOptimizers(), new FakeRules());
        var ctx = ctxOut ?? TwoLegContext();
        await stage.ExecuteAsync(ctx, ConfigWith(optimizerName), CancellationToken.None);
        return ctx.Ensemble!.Legs.ToDictionary(l => l.StrategyName, l => l.WeightPercent);
    }

    // ------------------------------------------------------------------ 1. regressione

    [Fact]
    public async Task Default_And_ExplicitHrp_ProduceIdenticalWeights()
    {
        var byDefault = await RunAsync(null);
        var explicitHrp = await RunAsync("HRP");
        Assert.Equal(byDefault, explicitHrp);
    }

    // ------------------------------------------------------------------ 2. la scelta conta

    [Fact]
    public async Task MeanVariance_ProducesDifferentWeights_ThanHrp()
    {
        var hrp = await RunAsync("HRP");
        var mv = await RunAsync("MeanVariance");

        // Profili opposti per costruzione: se i pesi coincidessero il parametro sarebbe un
        // controllo che rassicura senza fare — la classe di difetto del Filone E.
        Assert.NotEqual(hrp["Steady"], mv["Steady"]);
        // E MV deve preferire la gamba con lo Sharpe schiacciante.
        Assert.True(mv["Steady"] > hrp["Steady"],
            $"MV Steady={mv["Steady"]}, HRP Steady={hrp["Steady"]}");
    }

    [Fact]
    public async Task ChosenOptimizer_IsDeclaredInProposalMethod()
    {
        var ctx = TwoLegContext();
        await RunAsync("RiskParity", ctx);
        Assert.Equal("RiskParity", ctx.Ensemble!.Method); // prima era l'etichetta fissa "HRP"
    }

    // ------------------------------------------------------------------ 3. typo = default sicuro

    [Fact]
    public async Task UnknownName_FallsBackToHrp_AndSaysSo()
    {
        var ctx = TwoLegContext();
        var logLines = new List<string>();
        ctx.Log = logLines.Add; // il contesto logga via callback: qui la si intercetta

        var weights = await RunAsync("Hpr", ctx); // typo
        var hrp = await RunAsync("HRP");

        Assert.Equal(hrp, weights);
        Assert.Equal("HRP", ctx.Ensemble!.Method);
        Assert.Contains(logLines, l => l.Contains("sconosciuto") && l.Contains("HRP"));
    }
}
