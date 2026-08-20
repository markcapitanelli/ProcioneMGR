using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.ML;
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
        new("minIcTStat", "Soglia |t-stat| IC (Newey-West)", "0", "0 = disattivo; es. 2 = tiene solo fattori con IC statisticamente significativo"),
        new("forwardHorizon", "Orizzonte forward (candele)", "1", "target dell'IC"),
        // [2.6] Default false: il gate cambia l'insieme selezionato e quindi il modello a valle —
        // si accende per scelta esplicita del run, non di nascosto.
        new("incrementalIcGate", "Filtro incrementale (IC parziale)", "false", "true = scarta i fattori che non aggiungono informazione oltre ai già selezionati"),
    ];

    public string? ValidateInput(PipelineContext ctx)
        => ctx.Universe.Count == 0 ? "Universo vuoto." : null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var primary = ctx.PrimarySeries;
        var horizon = config.GetInt("forwardHorizon", 1);
        var topK = config.GetInt("topK", 4);
        var minAbsIc = (double)config.GetDecimal("minAbsIc", 0.01m);
        var minIcTStat = (double)config.GetDecimal("minIcTStat", 0m); // 0 = gate di significatività disattivo
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
                IcTStatistic = eval.IcTStatistic,
            });
            ctx.LogLine($"[{Name}] {proto.Name}: IC {eval.InformationCoefficient:F4} (t {eval.IcTStatistic:F2}, {eval.Observations} oss.)");
        }

        // Selezione per |IC| ≥ soglia e (opzionale) significatività Newey-West |t| ≥ soglia.
        var survivors = results
            .Where(r => Math.Abs(r.InformationCoefficient) >= minAbsIc && (minIcTStat <= 0d || Math.Abs(r.IcTStatistic) >= minIcTStat))
            .OrderByDescending(r => Math.Abs(r.InformationCoefficient))
            .ToList();

        // [2.6] Gate incrementale (opt-in): prima del taglio a top-K si scartano i fattori che non
        // AGGIUNGONO informazione oltre ai già tenuti (IC parziale + nullo per permutazione, dal
        // modulo Microstructure). Applicarlo PRIMA del Take significa che i posti liberati da un
        // ridondante vanno al prossimo fattore indipendente, non persi.
        if (config.GetBool("incrementalIcGate", false) && survivors.Count > 1)
        {
            ct.ThrowIfCancellationRequested();
            var protoByName = prototypes.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var ordered = survivors
                .Select(r => protoByName[r.FactorName])
                .Select(p => new FactorSpec(p.Name, p, p.ParameterDefinitions.ToDictionary(d => d.Key, d => d.Default)))
                .ToList();
            var filter = IncrementalFactorFilter.Apply(ordered, candles, horizon);
            foreach (var entry in filter.Entries.Where(e => !e.Kept))
            {
                var o = entry.Outcome!;
                ctx.LogLine($"[{Name}] Gate incrementale: {entry.Spec.FeatureName} scartato — IC parziale {o.PartialIc:F4} (grezzo {o.RawIc:F4}, corr. col tenuto {o.CorrelationWithProxy:F2}).");
            }
            var keptNames = filter.Kept.Select(k => k.FeatureName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            survivors = survivors.Where(r => keptNames.Contains(r.FactorName)).ToList();
            ctx.LogLine($"[{Name}] Gate incrementale: {filter.DroppedCount} ridondanti scartati, {survivors.Count} indipendenti restano.");
        }

        var selected = survivors.Take(topK).ToList();
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
    IMarketFeatureExtractor featureExtractor,
    IConfiguration appConfiguration) : IPipelineStage
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

    /// <summary>
    /// Giorni minimi perché la finestra di etichettatura contenga abbastanza BARRE da superare il
    /// warmup dell'estrattore di feature (50 barre) con un margine utile allo smoothing dei regimi.
    /// Senza questo, ogni timeframe più lungo di 4h produce silenziosamente zero feature.
    /// </summary>
    /// <summary>Esposto ai test: è la regola che impedisce il regime "sconosciuto" perenne.</summary>
    internal static int MinLabelDaysForTests(string timeframe) => MinLabelDays(timeframe);

    private static int MinLabelDays(string timeframe)
    {
        const int barsNeeded = 120;   // 50 di warmup + margine per lo smoothing a conferma
        var hoursPerBar = timeframe switch
        {
            "1m" => 1 / 60d, "5m" => 5 / 60d, "15m" => 0.25d, "30m" => 0.5d,
            "1h" => 1d, "2h" => 2d, "4h" => 4d, "6h" => 6d, "12h" => 12d,
            "1d" => 24d, "1w" => 168d,
            _ => 1d,
        };
        return (int)Math.Ceiling(barsNeeded * hoursPerBar / 24d);
    }

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var primary = ctx.PrimarySeries;
        var retrain = config.GetBool("retrain", false);

        // Il modello dev'essere quello DI QUESTA serie. Prima si prendeva il più recente fra tutti
        // gli attivi e lo si usava comunque: con più coppie seguite insieme, una caccia su SOL 4h
        // poteva etichettare le proprie candele coi centroidi di BTC 1h — un numero ben formato e
        // privo di senso, che poi entrava nel contesto del run come se fosse una misura.
        var model = await regimeDetector.LoadActiveModelAsync(primary.Symbol, primary.Timeframe, ct);
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
                // [2.7] Stessa sorgente del worker e di /regimes: MarketRegime:Model decide
                // l'algoritmo (default KMeans, contratto C1). Il parametro di stage "model"
                // permette a un run di forzarlo per il confronto, senza toccare la config globale.
                Model = RegimeModelKinds.Normalize(
                    config.GetString("model", appConfiguration["MarketRegime:Model"] ?? RegimeModelKinds.KMeans)),
                JumpLambda = appConfiguration.GetValue("MarketRegime:JumpLambda", 20.0),
            }, activate: true, ct);
            trainedNew = true;
        }

        // Current regime: label the recent window up to NOW (inference, not selection —
        // reading the latest data here is legitimate: it doesn't influence any backtest choice).
        //
        // La finestra è espressa in GIORNI ma il warmup delle feature è in BARRE (50: la finestra
        // più lunga usata dall'estrattore). Su 1h trenta giorni fanno 720 barre e va bene; su 1d ne
        // fanno 30, cioè SOTTO il warmup — l'estrattore restituiva zero feature e il regime usciva
        // "sconosciuto" a ogni run, senza che niente dicesse perché. Una configurazione swing
        // giornaliera non poteva quindi avere un regime, mai. Qui il minimo di giorni si ricava dal
        // timeframe, e la finestra chiesta dall'utente può solo allargarlo.
        var lookback = Math.Max(config.GetInt("labelLookbackDays", 30), MinLabelDays(primary.Timeframe));
        var to = DateTime.UtcNow;
        var features = await featureExtractor.ExtractFeaturesAsync(ctx.ExchangeName, primary.Symbol, primary.Timeframe, to.AddDays(-lookback), to, ct);
        var labeled = await regimeDetector.LabelFeaturesAsync(features, primary.Symbol, primary.Timeframe, ct);
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

/// <summary>
/// Stage 5 — classifies the volatility level of the primary series. [C3] Il PREVISORE che decide
/// il Level è il log-HAR sulla varianza realizzata giornaliera dai 5m quando i 5m bastano (gate C3:
/// QLIKE OOS migliore del vincitore GARCH/EWMA su 6/6 simboli di sviluppo e 24/24 di conferma a 1g),
/// altrimenti GARCH(1,1) come sempre. Il GARCH viene comunque fittato: persistenza, parametri e
/// soprattutto le CODE Student-t (VaR 1%) restano sue — il gate C3 riguarda la previsione di σ,
/// non i quantili di coda.
/// </summary>
public sealed class VolatilityRegimeStage(IGarchModel garch) : IPipelineStage
{
    public string Name => "VolatilityRegime";
    public string DisplayName => "Regime di volatilità";
    public string Description => "Prevede la volatilità (log-HAR dai 5m; GARCH in fallback) e la classifica (bassa/media/alta).";
    public int DefaultOrder => 5;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("DataIngestion")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("lookbackDays", "Storico per il fit (giorni)", "180", ""),
        new("horizonSteps", "Orizzonte forecast (candele)", "24", ""),
        new("highRatio", "Soglia 'Alta' (forecast/lungo periodo)", "1.3", ""),
        new("lowRatio", "Soglia 'Bassa' (forecast/lungo periodo)", "0.8", ""),
        new("volForecaster", "Previsore (auto = log-HAR se i 5m bastano, garch = solo GARCH)", "auto", ""),
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

        // [C3] Previsore del Level: log-HAR sulla RV giornaliera dai 5m, se i 5m bastano. Il ratio
        // forecast/lungo-periodo resta adimensionale: numeratore e denominatore si riscalano insieme
        // (la classificazione confronta livelli, non unità). Il fallback GARCH scatta anche a metà
        // strada: qualunque intoppo sui 5m non deve far fallire lo stage.
        //
        // [A1, 2026-08-20] Le tre grandezze del log-HAR nascono da varianze realizzate GIORNALIERE e
        // finivano tali e quali dentro campi il cui contratto dice «per-period», cioè per candela del
        // timeframe primario: su 1h il numero scritto era 4,90× troppo grande. Il ratio non se ne
        // accorgeva, quindi Level, dosaggio e gate C3 erano salvi — ma il trigger contestuale, che
        // confronta ForecastVolatility24 con una realizzata PER CANDELA, ne usciva degenerato: con
        // banda 1,5 il ramo «compressione» era vero per aritmetica su ogni timeframe intraday, e ogni
        // sveglia spuria bypassava il backoff della campagna. Si riporta la varianza sulla scala della
        // candela PRIMA della radice (la varianza è additiva nel tempo, quindi il fattore è lineare
        // sulla varianza e in radice sul sigma). Approssimazione dichiarata: assume assenza di
        // stagionalità intragiornaliera e di drift, che è la convenzione standard di riscalamento.
        var forecastSource = "garch";
        if (!string.Equals(config.GetString("volForecaster", "auto"), "garch", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var tfMinutes = Timeframes.Supported.GetValueOrDefault(primary.Timeframe, TimeSpan.FromHours(1)).TotalMinutes;
                var bars5m = await ctx.Candles.GetAsync(primary.Symbol, "5m", to.AddDays(-365), to, ct);
                var daily = RealizedVariance.DailyFromIntraday(bars5m);
                var horizonDays = Math.Clamp((int)Math.Round(horizon * tfMinutes / 1440.0), 1, 5);
                var rv = daily.Select(d => d.Rv).ToList();
                var harSeries = HarRvForecaster.ForecastSeries(rv, horizonDays, onLogRv: true);
                if (harSeries.Length > 0 && harSeries[^1] is { } harVariance)
                {
                    // Da varianza giornaliera a varianza per candela del timeframe primario.
                    var perCandle = tfMinutes / 1440.0;
                    var harForecastVol = Math.Sqrt(harVariance * perCandle);
                    var harLongRunVol = Math.Sqrt(rv.Average() * perCandle);
                    var harCurrentVol = Math.Sqrt(rv[^1] * perCandle);
                    if (harLongRunVol > 0)
                    {
                        forecastVol = harForecastVol;
                        longRunVol = harLongRunVol;
                        currentVol = harCurrentVol;
                        forecastSource = "har-log-rv";
                    }
                }
                else
                {
                    ctx.LogLine($"[{Name}] 5m insufficienti per il log-HAR ({daily.Count} giorni di RV): classifico col GARCH.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.LogLine($"[{Name}] log-HAR non disponibile ({ex.GetType().Name}): classifico col GARCH.");
            }
        }

        var ratio = longRunVol > 0 ? forecastVol / longRunVol : 1.0;
        var level = ratio >= (double)config.GetDecimal("highRatio", 1.3m) ? "Alta"
                  : ratio <= (double)config.GetDecimal("lowRatio", 0.8m) ? "Bassa"
                  : "Media";

        var vol = new VolatilityOutput
        {
            Symbol = primary.Symbol,
            ForecastSource = forecastSource,
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
        ctx.LogLine($"[{Name}] {primary.Symbol} [{forecastSource}]: persistenza {fit.Persistence:F4}, vol {currentVol:P3} → forecast {forecastVol:P3} ({level})"
                  + (vol.TailDegreesOfFreedom is double dof ? $"; ν={dof:F1}, VaR1% {vol.ForecastTailMove99:P2}." : "."));
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.Volatility ?? new VolatilityOutput();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{o.Symbol}: volatilità {o.Level} [{o.ForecastSource}] (attuale {o.CurrentVolatility:P3}, forecast {o.ForecastVolatility24:P3}, persistenza {o.Persistence:F3})"
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
                        IsHedgeRatioPlausible = result.IsHedgeRatioPlausible,
                        AlignedCandles = alignedY.Count,
                    });
                }
            }
        }

        output.Pairs = output.Pairs.OrderBy(p => p.AdfStatistic).ToList();
        output.CointegratedCount = output.Pairs.Count(p => p.IsCointegrated);
        output.TradeableCount = output.Pairs.Count(p => p.IsTradeable);
        ctx.Pairs = output;

        // I due numeri sono riportati separati apposta: la differenza dice quante coppie hanno uno
        // spread stazionario ma un'elasticità che rende il portafoglio negoziato un'altra cosa.
        var discarded = output.CointegratedCount - output.TradeableCount;
        ctx.LogLine($"[{Name}] {output.Pairs.Count} coppie testate, {output.CointegratedCount} cointegrate, "
                  + $"{output.TradeableCount} operabili"
                  + (discarded > 0 ? $" ({discarded} scartate per elasticità fuori banda)." : "."));
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.Pairs ?? new PairsOutput();

        // "Migliore" fra le OPERABILI, non fra le cointegrate: la lista è ordinata per ADF, e
        // prendere il primo elemento e basta è come una coppia con elasticità fuori banda finiva
        // consigliata all'operatore nonostante fosse la peggiore delle otto una volta negoziata.
        var best = o.Pairs.FirstOrDefault(p => p.IsTradeable);
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{o.Pairs.Count} coppie testate, {o.CointegratedCount} cointegrate, {o.TradeableCount} operabili"
                 + (best is null ? "." : $"; migliore: {best.SymbolY}/{best.SymbolX} (ADF {best.AdfStatistic:F2}, elasticità {best.HedgeRatio:F3})."),
            Metrics = new()
            {
                ["CoppieTestate"] = o.Pairs.Count,
                ["CoppieCointegrate"] = o.CointegratedCount,
                ["CoppieOperabili"] = o.TradeableCount,
            },
        };
    }
}
