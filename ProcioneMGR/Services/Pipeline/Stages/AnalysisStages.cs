using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.PairsTrading;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.TimeSeries;

namespace ProcioneMGR.Services.Pipeline.Stages;

/// <summary>
/// Stage 3 — evaluates the alpha-factor library (Information Coefficient) on the primary
/// series over the SELECTION range only, and selects the top-K factors as ML features.
/// </summary>
public sealed class FeatureEngineeringStage(
    IAlphaFactorFactory factorFactory,
    IFactorEvaluator evaluator) : IPipelineStage
{
    public string Name => "FeatureEngineering";
    public string DisplayName => "Feature engineering";
    public string Description => "Valuta i fattori alpha (IC) sul range di selezione e sceglie i top-K come feature ML.";
    public int DefaultOrder => 3;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("DataIngestion")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("factors", "Fattori da valutare (csv)", "", "vuoto = tutti quelli disponibili"),
        new("topK", "Top-K fattori selezionati", "4", "quante feature tenere per il modello ML"),
        new("minAbsIc", "Soglia |IC| minima", "0.01", "sotto questa soglia il fattore non viene selezionato"),
        new("forwardHorizon", "Orizzonte forward (candele)", "1", "target dell'IC"),
    ];

    public string? ValidateInput(PipelineContext ctx)
        => ctx.Universe.Count == 0 ? "Universo vuoto." : null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var primary = ctx.PrimarySeries;
        var horizon = config.GetInt("forwardHorizon", 1);
        var topK = config.GetInt("topK", 4);
        var minAbsIc = (double)config.GetDecimal("minAbsIc", 0.01m);
        var requested = config.GetList("factors");

        // ANTI-LOOK-AHEAD: only the selection range feeds any choice.
        var candles = await ctx.Candles.GetAsync(primary.Symbol, primary.Timeframe, ctx.Ranges.SelectionFrom, ctx.Ranges.SelectionTo, ct);
        if (candles.Count < 200)
        {
            throw new InvalidOperationException($"Servono almeno 200 candele di selezione per {primary.Symbol} {primary.Timeframe} (trovate {candles.Count}).");
        }

        var prototypes = factorFactory.Prototypes
            .Where(p => requested.Count == 0 || requested.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var evalConfig = new FactorEvaluationConfig { ForwardHorizon = horizon };
        var results = new List<FactorIcSummary>();
        foreach (var proto in prototypes)
        {
            ct.ThrowIfCancellationRequested();
            var defaults = proto.ParameterDefinitions.ToDictionary(d => d.Key, d => d.Default);
            var eval = evaluator.Evaluate(proto, candles, defaults, evalConfig);
            results.Add(new FactorIcSummary
            {
                FactorName = proto.Name,
                DisplayName = proto.DisplayName,
                InformationCoefficient = eval.InformationCoefficient,
                RollingIcMean = eval.RollingIcMean,
                InformationRatio = eval.RollingIcStd > 0 ? eval.RollingIcMean / eval.RollingIcStd : 0,
                Observations = eval.Observations,
            });
            ctx.LogLine($"[{Name}] {proto.Name}: IC {eval.InformationCoefficient:F4} ({eval.Observations} oss.)");
        }

        var selected = results
            .Where(r => Math.Abs(r.InformationCoefficient) >= minAbsIc)
            .OrderByDescending(r => Math.Abs(r.InformationCoefficient))
            .Take(topK)
            .ToList();
        foreach (var s in selected) s.Selected = true;

        ctx.Features = new FeatureSelectionOutput
        {
            Symbol = primary.Symbol,
            Timeframe = primary.Timeframe,
            ForwardHorizon = horizon,
            Factors = results.OrderByDescending(r => Math.Abs(r.InformationCoefficient)).ToList(),
            SelectedFactorNames = selected.Select(s => s.FactorName).ToList(),
        };
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.Features ?? new FeatureSelectionOutput();
        var best = o.Factors.FirstOrDefault();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{o.Factors.Count} fattori valutati su {o.Symbol} {o.Timeframe}; selezionati: {(o.SelectedFactorNames.Count > 0 ? string.Join(", ", o.SelectedFactorNames) : "nessuno")}."
                 + (best is null ? "" : $" Miglior IC: {best.FactorName} ({best.InformationCoefficient:F4})."),
            Metrics = new()
            {
                ["FattoriValutati"] = o.Factors.Count,
                ["FattoriSelezionati"] = o.SelectedFactorNames.Count,
                ["MigliorIC"] = best is null ? 0m : (decimal)best.InformationCoefficient,
            },
        };
    }
}

/// <summary>
/// Stage 4 — labels the current market regime with the active K-means model (training one on
/// the selection range only when none exists, or when retrain=true).
/// </summary>
public sealed class RegimeAnalysisStage(
    IRegimeDetector regimeDetector,
    IMarketFeatureExtractor featureExtractor) : IPipelineStage
{
    public string Name => "RegimeAnalysis";
    public string DisplayName => "Analisi di regime";
    public string Description => "Identifica il regime di mercato corrente (K-means) e il suo profilo per-strategia.";
    public int DefaultOrder => 4;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("DataIngestion")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("numberOfRegimes", "Numero di regimi (K)", "4", "usato solo se serve addestrare un modello"),
        new("retrain", "Riaddestra e attiva il modello", "false", "true = sostituisce il modello attivo con uno nuovo sul range di selezione"),
        new("labelLookbackDays", "Finestra di labeling (giorni)", "30", "quanti giorni recenti etichettare per il regime corrente"),
    ];

    public string? ValidateInput(PipelineContext ctx) => null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var primary = ctx.PrimarySeries;
        var retrain = config.GetBool("retrain", false);

        var model = await regimeDetector.LoadLatestModelAsync(ct);
        var trainedNew = false;
        if (model is null || retrain)
        {
            ctx.LogLine($"[{Name}] Addestro un modello di regime su {primary.Symbol} {primary.Timeframe} (selection range)…");
            model = await regimeDetector.TrainAsync(new TrainingConfiguration
            {
                ExchangeName = ctx.ExchangeName,
                Symbol = primary.Symbol,
                Timeframe = primary.Timeframe,
                From = ctx.Ranges.SelectionFrom,
                To = ctx.Ranges.SelectionTo,
                NumberOfRegimes = config.GetInt("numberOfRegimes", 4),
            }, activate: true, ct);
            trainedNew = true;
        }

        // Current regime: label the recent window up to NOW (inference, not selection —
        // reading the latest data here is legitimate: it doesn't influence any backtest choice).
        var lookback = config.GetInt("labelLookbackDays", 30);
        var to = DateTime.UtcNow;
        var features = await featureExtractor.ExtractFeaturesAsync(ctx.ExchangeName, primary.Symbol, primary.Timeframe, to.AddDays(-lookback), to, ct);
        var labeled = await regimeDetector.LabelFeaturesAsync(features, ct);
        var current = labeled.LastOrDefault(f => f.RegimeId is not null);

        var profiles = System.Text.Json.JsonSerializer.Deserialize<List<RegimeProfile>>(model.RegimeProfilesJson) ?? [];
        var currentProfile = current?.RegimeId is int rid ? profiles.FirstOrDefault(p => p.RegimeId == rid) : null;

        ctx.Regimes = new RegimeOutput
        {
            CurrentRegimeId = current?.RegimeId ?? -1,
            CurrentRegimeLabel = currentProfile?.SuggestedLabel ?? "sconosciuto",
            SilhouetteScore = model.SilhouetteScore,
            TrainedNewModel = trainedNew,
            Profiles = profiles,
        };
        ctx.LogLine($"[{Name}] Regime corrente: {ctx.Regimes.CurrentRegimeLabel} (id {ctx.Regimes.CurrentRegimeId}), silhouette {model.SilhouetteScore:F3}.");
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.Regimes ?? new RegimeOutput();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"Regime corrente: {o.CurrentRegimeLabel} (id {o.CurrentRegimeId}); silhouette {o.SilhouetteScore:F3}"
                 + (o.TrainedNewModel ? " (modello nuovo addestrato)." : " (modello attivo riusato)."),
            Metrics = new()
            {
                ["RegimeId"] = o.CurrentRegimeId,
                ["Silhouette"] = (decimal)o.SilhouetteScore,
            },
        };
    }
}

/// <summary>Stage 5 — fits GARCH(1,1) on recent returns of the primary series and classifies the volatility level.</summary>
public sealed class VolatilityRegimeStage(IGarchModel garch) : IPipelineStage
{
    public string Name => "VolatilityRegime";
    public string DisplayName => "Regime di volatilità";
    public string Description => "Stima GARCH(1,1), prevede la volatilità e la classifica (bassa/media/alta).";
    public int DefaultOrder => 5;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("DataIngestion")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("lookbackDays", "Storico per il fit (giorni)", "180", ""),
        new("horizonSteps", "Orizzonte forecast (candele)", "24", ""),
        new("highRatio", "Soglia 'Alta' (forecast/lungo periodo)", "1.3", ""),
        new("lowRatio", "Soglia 'Bassa' (forecast/lungo periodo)", "0.8", ""),
    ];

    public string? ValidateInput(PipelineContext ctx) => null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var primary = ctx.PrimarySeries;
        var to = DateTime.UtcNow;
        var from = to.AddDays(-config.GetInt("lookbackDays", 180));
        var candles = await ctx.Candles.GetAsync(primary.Symbol, primary.Timeframe, from, to, ct);
        if (candles.Count < 60)
        {
            throw new InvalidOperationException($"Servono almeno 60 candele recenti per il GARCH su {primary.Symbol} {primary.Timeframe} (trovate {candles.Count}).");
        }

        var returns = new List<decimal>(candles.Count - 1);
        for (var i = 1; i < candles.Count; i++)
        {
            if (candles[i - 1].Close > 0m) returns.Add((candles[i].Close - candles[i - 1].Close) / candles[i - 1].Close);
        }

        var fit = garch.Fit(returns);
        var horizon = config.GetInt("horizonSteps", 24);
        var currentVol = Math.Sqrt(Math.Max(0, fit.ConditionalVariances[^1]));
        var longRunVol = double.IsNaN(fit.LongRunVariance) ? currentVol : Math.Sqrt(Math.Max(0, fit.LongRunVariance));
        var forecastVol = Math.Sqrt(Math.Max(0, fit.ForecastVariance(horizon)));

        var ratio = longRunVol > 0 ? forecastVol / longRunVol : 1.0;
        var level = ratio >= (double)config.GetDecimal("highRatio", 1.3m) ? "Alta"
                  : ratio <= (double)config.GetDecimal("lowRatio", 0.8m) ? "Bassa"
                  : "Media";

        var vol = new VolatilityOutput
        {
            Symbol = primary.Symbol,
            Omega = fit.Omega,
            Alpha = fit.Alpha,
            Beta = fit.Beta,
            Persistence = fit.Persistence,
            CurrentVolatility = currentVol,
            LongRunVolatility = longRunVol,
            ForecastVolatility24 = forecastVol,
            Level = level,
        };

        // Fit Student-t AGGIUNTIVO solo per le metriche di coda (non tocca la classificazione del
        // regime, che resta gaussiana): espone ν e la mossa avversa all'1% consapevole delle code
        // grasse, come distanza di stop prudente. Non deve mai far fallire lo stage. Audit 2026-07 §4.
        try
        {
            var tailFit = garch.Fit(returns, GarchInnovation.StudentT);
            vol.TailDegreesOfFreedom = tailFit.DegreesOfFreedom;
            vol.ForecastTailMove99 = Math.Abs(tailFit.TailQuantile(0.01, horizon));
        }
        catch (Exception ex)
        {
            ctx.LogLine($"[{Name}] fit Student-t di coda non riuscito ({ex.GetType().Name}): metriche di coda omesse.");
        }

        ctx.Volatility = vol;
        ctx.LogLine($"[{Name}] {primary.Symbol}: persistenza {fit.Persistence:F4}, vol {currentVol:P3} → forecast {forecastVol:P3} ({level})"
                  + (vol.TailDegreesOfFreedom is double dof ? $"; ν={dof:F1}, VaR1% {vol.ForecastTailMove99:P2}." : "."));
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.Volatility ?? new VolatilityOutput();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{o.Symbol}: volatilità {o.Level} (attuale {o.CurrentVolatility:P3}, forecast {o.ForecastVolatility24:P3}, persistenza {o.Persistence:F3})"
                 + (o.TailDegreesOfFreedom is double dof ? $"; code grasse ν={dof:F1}, VaR1% {o.ForecastTailMove99:P2}." : "."),
            Metrics = new()
            {
                ["Persistenza"] = (decimal)o.Persistence,
                ["VolAttuale"] = (decimal)o.CurrentVolatility,
                ["VolForecast"] = (decimal)o.ForecastVolatility24,
                ["VaR1%coda"] = (decimal)o.ForecastTailMove99,
            },
        };
    }
}

/// <summary>
/// Stage 6 — screens every same-timeframe symbol pair of the universe for cointegration
/// (Engle-Granger) over the selection range.
/// </summary>
public sealed class PairsScreeningStage(ICointegrationTest cointegration) : IPipelineStage
{
    public string Name => "PairsScreening";
    public string DisplayName => "Screening coppie";
    public string Description => "Test di cointegrazione Engle-Granger su tutte le coppie dell'universo (stesso timeframe).";
    public int DefaultOrder => 6;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("DataIngestion")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("minAlignedCandles", "Minimo candele allineate", "200", "coppie con meno osservazioni comuni vengono saltate"),
    ];

    public string? ValidateInput(PipelineContext ctx)
        => ctx.Universe.Select(u => u.Symbol).Distinct().Count() < 2
            ? "Servono almeno 2 simboli distinti nell'universo per lo screening delle coppie."
            : null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var minAligned = config.GetInt("minAlignedCandles", 200);
        var output = new PairsOutput();

        var byTimeframe = ctx.Universe.GroupBy(u => u.Timeframe);
        foreach (var group in byTimeframe)
        {
            var symbols = group.Select(g => g.Symbol).Distinct().ToList();
            for (var i = 0; i < symbols.Count; i++)
            {
                for (var j = i + 1; j < symbols.Count; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    var candlesY = await ctx.Candles.GetAsync(symbols[i], group.Key, ctx.Ranges.SelectionFrom, ctx.Ranges.SelectionTo, ct);
                    var candlesX = await ctx.Candles.GetAsync(symbols[j], group.Key, ctx.Ranges.SelectionFrom, ctx.Ranges.SelectionTo, ct);
                    if (candlesY.Count < minAligned || candlesX.Count < minAligned) continue;

                    var (alignedY, alignedX) = PairsCandleAligner.Align(candlesY, candlesX);
                    if (alignedY.Count < minAligned) continue;

                    var result = cointegration.Test(
                        alignedY.Select(c => c.Close).ToList(),
                        alignedX.Select(c => c.Close).ToList());

                    output.Pairs.Add(new PairScreenResult
                    {
                        SymbolY = symbols[i],
                        SymbolX = symbols[j],
                        Timeframe = group.Key,
                        AdfStatistic = result.AdfStatistic,
                        IsCointegrated = result.IsCointegrated,
                        HedgeRatio = result.HedgeRatio,
                        AlignedCandles = alignedY.Count,
                    });
                }
            }
        }

        output.Pairs = output.Pairs.OrderBy(p => p.AdfStatistic).ToList();
        output.CointegratedCount = output.Pairs.Count(p => p.IsCointegrated);
        ctx.Pairs = output;
        ctx.LogLine($"[{Name}] {output.Pairs.Count} coppie testate, {output.CointegratedCount} cointegrate.");
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.Pairs ?? new PairsOutput();
        var best = o.Pairs.FirstOrDefault();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{o.Pairs.Count} coppie testate, {o.CointegratedCount} cointegrate"
                 + (best is null ? "." : $"; migliore: {best.SymbolY}/{best.SymbolX} (ADF {best.AdfStatistic:F2}, hedge {best.HedgeRatio:F3})."),
            Metrics = new()
            {
                ["CoppieTestate"] = o.Pairs.Count,
                ["CoppieCointegrate"] = o.CointegratedCount,
            },
        };
    }
}
