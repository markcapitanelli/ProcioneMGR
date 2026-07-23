using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Experiments;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Optimization;

/// <summary>Un range di ricerca del form (Min/Max/Step/Intero) con la sua etichetta di UI.</summary>
public sealed record OptRange(string Key, string Label, decimal Min, decimal Max, decimal Step, bool IsInteger);

/// <summary>
/// Fotografia completa del form di <c>Optimization.razor</c> — usata per i preset/memoria dell'ultima
/// configurazione, per l'handoff da Backtest/ML Lab e come input di <see cref="OptimizationPageService.RunAsync"/>.
/// </summary>
public sealed record OptimizationConfigSnapshot(
    ExchangeName Exchange, string Symbol, string Timeframe, DateTime From, DateTime To,
    decimal InitialCapital, decimal Commission, int InSampleMonths, int OosMonths, int StepMonths,
    SearchStrategy SearchStrategy, int BayesIterations, int BayesInitialRandom, int BayesSeed,
    string StrategyName, int MlModelId, IReadOnlyList<OptRange> Ranges,
    // [T0.1] In coda e con default per compatibilità: i preset salvati prima del campo
    // deserializzano a 0 (= nessun embargo, comportamento storico).
    int EmbargoBars = 0,
    // [T1.6 fase 2] In coda e con default per la stessa ragione: i preset pre-CPCV
    // deserializzano a WalkForward (comportamento storico invariato).
    OptimizationValidationMethod Validation = OptimizationValidationMethod.WalkForward,
    int CpcvGroups = 8, int CpcvTestGroups = 2, int CpcvPurgeBars = 0, int CpcvEmbargoBars = 0);

/// <summary>Esito di un'azione con messaggio per l'operatore.</summary>
public sealed record OptActionResult(string Message, bool IsError)
{
    public static OptActionResult Ok(string message) => new(message, false);
    public static OptActionResult Error(string message) => new(message, true);
}

/// <summary>Contesto opzionale arrivato via query string (handoff dal Backtest o dal ML Lab).</summary>
public sealed record OptimizationHandoffQuery(
    string? Exchange, string? Symbol, string? Timeframe, string? Strategy,
    string? From, string? To, string? ParametersJson, int? ModelId);

/// <summary>Matrice della heatmap di robustezza (Sharpe OOS medio su 2 parametri); Z null = combinazione mai valutata.</summary>
public sealed record HeatmapMatrix(string[] Xs, string[] Ys, double?[][] Z);

/// <summary>
/// Orchestrazione estratta da <c>Components/Pages/Optimization.razor</c> (P1-5, PRD-CONSOLIDAMENTO-
/// ARCHITETTURA.md §3.3): costruzione dei range di default per strategia (incluso il caso speciale
/// "Ml" a soglie), validazione e run dello sweep walk-forward (grid/Bayesian) con experiment
/// tracking, handoff da Backtest/ML Lab col ricentraggio dei range, (de)serializzazione validata dei
/// preset, salvataggio della configurazione migliore e parsing della matrice heatmap — tutta la
/// logica che prima viveva nel blocco <c>@code</c> del componente senza test indipendenti da Blazor.
/// Il componente resta responsabile solo di ciò che è intrinsecamente Blazor: binding del form,
/// progress bar (<c>IProgress</c>+<c>StateHasChanged</c>), CTS di annullamento e JS interop della heatmap.
///
/// Lo stato del "run corrente" (risultato, config del run, equity walk-forward) vive qui perché è
/// stato applicativo condiviso fra run→heatmap→salvataggio→link Backtest, non stato di UI.
/// Registrato Scoped: in Blazor Server uno scope = un circuito, un'istanza per sessione utente.
/// </summary>
public sealed class OptimizationPageService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptimizationEngine optEngine,
    IStrategyFactory strategyFactory,
    IExperimentTracker tracker)
{
    public const string MlStrategyName = "Ml";

    // --- Stato caricato / del run corrente (letto dal markup, mai scritto dal componente) ------

    public IReadOnlyList<string> KnownSymbols { get; private set; } = [];
    public List<SavedMlModel> MlModels { get; private set; } = [];

    public OptimizationResult? Result { get; private set; }

    /// <summary>[T1.6 fase 2] Esito CPCV del run corrente (mutuamente esclusivo con <see cref="Result"/>).</summary>
    public CpcvResult? CpcvResult { get; private set; }

    /// <summary>Config del run corrente: i link "Backtest →" restano coerenti anche se il form cambia dopo.</summary>
    public OptimizationConfiguration? ResultConfig { get; private set; }

    public List<IndicatorSeries> EquitySeries { get; private set; } = [];

    /// <summary>NB: come nell'originale, i modelli ML NON sono filtrati per utente (la select filtra poi per symbol/timeframe).</summary>
    public async Task LoadInitialDataAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        KnownSymbols = await db.OhlcvData.Select(c => c.Symbol).Distinct().OrderBy(s => s).ToListAsync(ct);
        MlModels = await db.SavedMlModels.OrderByDescending(m => m.CreatedAtUtc).ToListAsync(ct);
    }

    // --- Range di default per strategia --------------------------------------------------------

    /// <summary>
    /// Range di partenza per la strategia: per "Ml" le sole soglie Long/Short (il modello si sceglie
    /// a parte); per le strategie a regole, min = default e max = default + 4 step, con step al 20%
    /// (interi: parametri "Period"/"Lookback") o 10% del default.
    /// </summary>
    public IReadOnlyList<OptRange> DefaultRangesFor(string strategyName)
    {
        if (strategyName == MlStrategyName)
        {
            return
            [
                new OptRange("LongThreshold", "Soglia Long", 0.0005m, 0.02m, 0.0025m, IsInteger: false),
                new OptRange("ShortThreshold", "Soglia Short", 0.0005m, 0.02m, 0.0025m, IsInteger: false),
            ];
        }

        var proto = strategyFactory.Prototypes.FirstOrDefault(p => p.Name == strategyName) ?? strategyFactory.Prototypes[0];
        return proto.ParameterDefinitions.Select(d =>
        {
            var isInt = d.Key.Contains("Period") || d.Key.Contains("Lookback");
            var step = isInt ? Math.Max(1m, Math.Round(d.Default * 0.2m)) : Math.Round(d.Default * 0.1m, 4);
            if (step <= 0m) step = isInt ? 1m : 0.01m;
            return new OptRange(d.Key, d.Label, d.Default, d.Default + 4 * step, step, isInt);
        }).ToList();
    }

    /// <summary>Numero di combinazioni del prodotto cartesiano dei range (0 se uno step non è positivo).</summary>
    public static int TotalCombinations(IReadOnlyList<OptRange> ranges)
    {
        var total = 1;
        foreach (var r in ranges)
        {
            if (r.Step <= 0m) return 0;
            var count = (int)Math.Floor((double)((r.Max - r.Min) / r.Step)) + 1;
            total *= Math.Max(1, count);
        }
        return total;
    }

    // --- Preset: (de)serializzazione VALIDATA --------------------------------------------------

    /// <summary>Forma JSON dei preset — invariata rispetto al blocco @code originale, così i preset già salvati restano leggibili.</summary>
    private sealed record RangeDto(string Key, decimal Min, decimal Max, decimal Step);
    private sealed record ConfigDto(
        string Exchange, string Symbol, string Timeframe, DateTime From, DateTime To,
        decimal InitialCapital, decimal Commission, int InSampleMonths, int OosMonths, int StepMonths,
        string SearchStrategy, int BayesIterations, int BayesInitialRandom, int BayesSeed,
        string StrategyName, int MlModelId, List<RangeDto> Ranges, int EmbargoBars = 0,
        string Validation = "WalkForward", int CpcvGroups = 8, int CpcvTestGroups = 2,
        int CpcvPurgeBars = 0, int CpcvEmbargoBars = 0);

    public string SerializeConfig(OptimizationConfigSnapshot cfg) => JsonSerializer.Serialize(new ConfigDto(
        cfg.Exchange.ToString(), cfg.Symbol.Trim(), cfg.Timeframe, cfg.From, cfg.To,
        cfg.InitialCapital, cfg.Commission, cfg.InSampleMonths, cfg.OosMonths, cfg.StepMonths,
        cfg.SearchStrategy.ToString(), cfg.BayesIterations, cfg.BayesInitialRandom, cfg.BayesSeed,
        cfg.StrategyName, cfg.MlModelId,
        cfg.Ranges.Select(r => new RangeDto(r.Key, r.Min, r.Max, r.Step)).ToList(),
        cfg.EmbargoBars,
        cfg.Validation.ToString(), cfg.CpcvGroups, cfg.CpcvTestGroups, cfg.CpcvPurgeBars, cfg.CpcvEmbargoBars));

    /// <summary>
    /// Applica un preset a <paramref name="current"/>: exchange/timeframe/strategia presi dal preset
    /// solo se ancora validi ("Ml" incluso); il modello ML si azzera se la strategia finale non è
    /// "Ml"; i range sono i default della strategia finale con overlay Min/Max/Step delle sole chiavi
    /// ancora esistenti (Intero resta dall'euristica). JSON malformato ⇒ <paramref name="current"/>
    /// invariato. Stessa semantica del vecchio <c>ApplyConfigJson</c>, ora testabile in isolamento.
    /// </summary>
    public OptimizationConfigSnapshot ApplyConfig(string json, OptimizationConfigSnapshot current)
    {
        ConfigDto? dto;
        try { dto = JsonSerializer.Deserialize<ConfigDto>(json); }
        catch (JsonException) { return current; }
        if (dto is null) return current;

        var exchange = Enum.TryParse<ExchangeName>(dto.Exchange, ignoreCase: true, out var ex) ? ex : current.Exchange;
        var symbol = string.IsNullOrWhiteSpace(dto.Symbol) ? current.Symbol : dto.Symbol;
        var timeframe = Timeframes.Supported.ContainsKey(dto.Timeframe) ? dto.Timeframe : current.Timeframe;
        var search = Enum.TryParse<SearchStrategy>(dto.SearchStrategy, out var ss) ? ss : current.SearchStrategy;
        var strategy = dto.StrategyName == MlStrategyName || strategyFactory.Prototypes.Any(p => p.Name == dto.StrategyName)
            ? dto.StrategyName : current.StrategyName;

        var ranges = DefaultRangesFor(strategy).Select(range =>
        {
            var saved = dto.Ranges.FirstOrDefault(r => r.Key == range.Key);
            return saved is null ? range : range with { Min = saved.Min, Max = saved.Max, Step = saved.Step };
        }).ToList();

        return current with
        {
            Exchange = exchange,
            Symbol = symbol,
            Timeframe = timeframe,
            From = dto.From.Date,
            To = dto.To.Date,
            InitialCapital = dto.InitialCapital,
            Commission = dto.Commission,
            InSampleMonths = dto.InSampleMonths,
            OosMonths = dto.OosMonths,
            StepMonths = dto.StepMonths,
            EmbargoBars = Math.Max(0, dto.EmbargoBars),
            Validation = Enum.TryParse<OptimizationValidationMethod>(dto.Validation, out var vm) ? vm : current.Validation,
            CpcvGroups = Math.Max(2, dto.CpcvGroups),
            CpcvTestGroups = Math.Max(1, dto.CpcvTestGroups),
            CpcvPurgeBars = Math.Max(0, dto.CpcvPurgeBars),
            CpcvEmbargoBars = Math.Max(0, dto.CpcvEmbargoBars),
            SearchStrategy = search,
            BayesIterations = dto.BayesIterations,
            BayesInitialRandom = dto.BayesInitialRandom,
            BayesSeed = dto.BayesSeed,
            StrategyName = strategy,
            MlModelId = strategy == MlStrategyName ? dto.MlModelId : 0,
            Ranges = ranges,
        };
    }

    // --- Handoff dal Backtest / ML Lab (query string) ------------------------------------------

    /// <summary>
    /// Applica il contesto arrivato via query string: valori assenti o malformati lasciano i
    /// correnti. I range partono dai default della strategia finale e vengono RICENTRATI sui
    /// parametri del run di provenienza (min = valore, max = valore + 4 step). Il messaggio è
    /// non-null solo quando è arrivato davvero un contesto (symbol presente).
    /// </summary>
    public (OptimizationConfigSnapshot Snapshot, string? Message) ApplyHandoff(OptimizationHandoffQuery q, OptimizationConfigSnapshot current)
    {
        var symbol = string.IsNullOrWhiteSpace(q.Symbol) ? current.Symbol : q.Symbol.Trim();
        var timeframe = !string.IsNullOrWhiteSpace(q.Timeframe) && Timeframes.Supported.ContainsKey(q.Timeframe) ? q.Timeframe : current.Timeframe;
        var exchange = Enum.TryParse<ExchangeName>(q.Exchange, ignoreCase: true, out var ex) ? ex : current.Exchange;
        var from = DateTime.TryParse(q.From, out var f) ? f.Date : current.From;
        var to = DateTime.TryParse(q.To, out var t) ? t.Date : current.To;
        var strategy = !string.IsNullOrWhiteSpace(q.Strategy)
                       && (q.Strategy == MlStrategyName || strategyFactory.Prototypes.Any(p => p.Name == q.Strategy))
            ? q.Strategy : current.StrategyName;

        // Modello ML preselezionato (dal ML Lab): la select lo mostra se compatibile con symbol/timeframe.
        var mlModelId = strategy == MlStrategyName && q.ModelId is > 0 ? q.ModelId.Value : current.MlModelId;

        // Range: default della strategia finale, ricentrati sui parametri del run di provenienza.
        var ranges = DefaultRangesFor(strategy).ToList();
        if (!string.IsNullOrWhiteSpace(q.ParametersJson))
        {
            try
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, decimal>>(q.ParametersJson);
                if (values is not null)
                {
                    ranges = ranges.Select(range =>
                    {
                        if (!values.TryGetValue(range.Key, out var v)) return range;
                        var step = range.IsInteger ? Math.Max(1m, Math.Round(v * 0.2m)) : Math.Round(v * 0.1m, 4);
                        if (step <= 0m) step = range.IsInteger ? 1m : 0.01m;
                        return range with { Min = v, Max = v + 4 * step, Step = step };
                    }).ToList();
                }
            }
            catch (JsonException)
            {
                // Query malformata: si tengono i range di default.
            }
        }

        var snapshot = current with
        {
            Exchange = exchange,
            Symbol = symbol,
            Timeframe = timeframe,
            From = from,
            To = to,
            StrategyName = strategy,
            MlModelId = mlModelId,
            Ranges = ranges,
        };
        string? message = null;
        if (!string.IsNullOrWhiteSpace(q.Symbol))
        {
            var source = q.ModelId is > 0 ? "dal ML Lab" : "dal Backtest";
            message = $"Configurazione importata {source}: {symbol} {timeframe}, strategia {strategy}.";
        }
        return (snapshot, message);
    }

    /// <summary>Link al Backtest precompilato col contesto del run corrente e i parametri della riga scelta.</summary>
    public string BacktestHandoffUrl(Dictionary<string, decimal> parameters)
    {
        if (ResultConfig is null) return "backtest";
        var json = JsonSerializer.Serialize(parameters);
        return "backtest"
             + $"?exchange={Uri.EscapeDataString(ResultConfig.ExchangeName)}"
             + $"&symbol={Uri.EscapeDataString(ResultConfig.Symbol)}"
             + $"&timeframe={Uri.EscapeDataString(ResultConfig.Timeframe)}"
             + $"&strategy={Uri.EscapeDataString(ResultConfig.StrategyName)}"
             + $"&from={ResultConfig.From:yyyy-MM-dd}&to={ResultConfig.To:yyyy-MM-dd}"
             + $"&parameters={Uri.EscapeDataString(json)}";
    }

    // --- Esecuzione ----------------------------------------------------------------------------

    /// <summary>
    /// Esegue lo sweep walk-forward (grid o Bayesian) e popola Result/ResultConfig/EquitySeries +
    /// experiment tracking best-effort. Il progress e l'annullamento appartengono al chiamante
    /// (<see cref="OperationCanceledException"/> propaga: il componente possiede il CTS).
    /// </summary>
    public async Task<OptActionResult> RunAsync(OptimizationConfigSnapshot cfg, string? userId, IProgress<OptimizationProgress>? progress, CancellationToken ct)
    {
        ResetRun();

        if (cfg.StrategyName == MlStrategyName && cfg.MlModelId == 0)
            return OptActionResult.Error("Seleziona un modello ML salvato per questo symbol/timeframe.");

        // [T1.6 fase 2] Il CPCV pre-calcola i backtest per (combinazione × gruppo) sull'INTERA
        // griglia: una ricerca guidata che propone i punti uno alla volta non è compatibile.
        if (cfg.Validation == OptimizationValidationMethod.Cpcv && cfg.SearchStrategy == SearchStrategy.Bayesian)
            return OptActionResult.Error("La validazione CPCV richiede Grid Search: la ricerca Bayesian propone i punti in modo incrementale e non copre la griglia.");

        var ranges = cfg.Ranges.Select(r => new ParameterRange
        {
            Name = r.Key, Min = r.Min, Max = r.Max, Step = r.Step, IsInteger = r.IsInteger,
        }).ToList();
        if (cfg.StrategyName == MlStrategyName)
        {
            // Range "pinnato" (Min=Max) per veicolare il riferimento al modello attraverso
            // lo stesso meccanismo di sweep dei parametri: nessun cambiamento a OptimizationEngine.
            ranges.Add(new ParameterRange { Name = "SavedModelId", Min = cfg.MlModelId, Max = cfg.MlModelId, Step = 1, IsInteger = true });
        }

        var config = new OptimizationConfiguration
        {
            ExchangeName = cfg.Exchange.ToString(),
            Symbol = cfg.Symbol.Trim(),
            Timeframe = cfg.Timeframe,
            From = cfg.From.Date,
            To = cfg.To.Date,
            InitialCapital = cfg.InitialCapital,
            CommissionPercent = cfg.Commission,
            StrategyName = cfg.StrategyName,
            ParameterRanges = ranges,
            WalkForward = new WalkForwardConfiguration
            {
                InSampleMonths = cfg.InSampleMonths, OutOfSampleMonths = cfg.OosMonths, StepMonths = cfg.StepMonths,
                EmbargoBars = cfg.EmbargoBars,
            },
            SearchStrategy = cfg.SearchStrategy,
            BayesianIterations = cfg.BayesIterations,
            BayesianInitialRandom = cfg.BayesInitialRandom,
            BayesianSeed = cfg.BayesSeed,
        };

        if (cfg.Validation == OptimizationValidationMethod.Cpcv)
        {
            return await RunCpcvAsync(cfg, config, userId, progress, ct);
        }

        var result = await optEngine.OptimizeAsync(config, progress, ct);
        Result = result;
        ResultConfig = config;

        EquitySeries =
        [
            new IndicatorSeries
            {
                Title = "Equity WF", Color = "#2962FF", Type = IndicatorSeriesType.Line,
                Points = result.WalkForwardAnalysis.CombinedEquityCurve
                    .Select(p => new IndicatorPoint(new DateTimeOffset(DateTime.SpecifyKind(p.Timestamp, DateTimeKind.Utc)).ToUnixTimeSeconds(), (double)p.Capital))
                    .ToList(),
            },
        ];

        // Experiment tracking (best-effort): un run per sweep, con i parametri migliori e la
        // dimensione dello spazio esplorato — così un grid e un Bayesiano sono comparabili.
        var best = result.BestParameters.Count > 0 ? result.BestParameters[0] : null;
        var optRunId = await tracker.SafeStartRunAsync(
            "Optimization",
            $"{cfg.StrategyName} · {cfg.Symbol.Trim()} · {cfg.Timeframe}",
            new
            {
                config.StrategyName,
                config.Symbol,
                config.Timeframe,
                config.From,
                config.To,
                SearchStrategy = config.SearchStrategy.ToString(),
                Ranges = config.ParameterRanges.Select(r => new { r.Name, r.Min, r.Max, r.Step }).ToList(),
                config.WalkForward.InSampleMonths,
                config.WalkForward.OutOfSampleMonths,
                config.WalkForward.StepMonths,
                BestParameters = best?.Parameters,
            },
            config.Symbol, config.Timeframe, userId);
        var optMetrics = new Dictionary<string, decimal>
        {
            ["CombinationsTested"] = result.TotalCombinationsTested,
            ["Windows"] = result.WalkForwardAnalysis.Windows.Count,
            ["BestOosSharpe"] = best?.OutOfSampleSharpe ?? 0m,
        };
        if (result.Validation is { } val)
        {
            // Verdetto anti-overfitting (Fase 1) tracciato accanto alle metriche grezze.
            foreach (var (k, mv) in val.ToMetrics()) optMetrics[k] = mv;
        }
        await tracker.SafeLogMetricsAsync(optRunId, optMetrics);
        await tracker.SafeCompleteAsync(optRunId, "Completed");

        return OptActionResult.Ok($"Ottimizzazione completata: {result.WalkForwardAnalysis.Windows.Count} finestre, {result.TotalCombinationsTested} valutazioni.");
    }

    /// <summary>
    /// [T1.6 fase 2] Ramo CPCV di <see cref="RunAsync"/>: la stessa griglia del walk-forward, ma il
    /// giudizio è una DISTRIBUZIONE di Sharpe out-of-sample su C(gruppi, gruppiTest) percorsi —
    /// più out-of-sample dagli stessi dati, l'antidoto strutturale al "one lucky path".
    /// </summary>
    private async Task<OptActionResult> RunCpcvAsync(
        OptimizationConfigSnapshot cfg, OptimizationConfiguration config, string? userId,
        IProgress<OptimizationProgress>? progress, CancellationToken ct)
    {
        var cpcvConfig = new CpcvConfiguration
        {
            Groups = Math.Max(2, cfg.CpcvGroups),
            TestGroups = Math.Clamp(cfg.CpcvTestGroups, 1, Math.Max(1, cfg.CpcvGroups - 1)),
            PurgeBars = Math.Max(0, cfg.CpcvPurgeBars),
            EmbargoBars = Math.Max(0, cfg.CpcvEmbargoBars),
        };

        var cpcv = await optEngine.OptimizeCpcvAsync(config, cpcvConfig, progress, ct);
        CpcvResult = cpcv;
        ResultConfig = config;

        var runId = await tracker.SafeStartRunAsync(
            "OptimizationCpcv",
            $"{cfg.StrategyName} · {cfg.Symbol.Trim()} · {cfg.Timeframe} · CPCV {cpcvConfig.Groups}/{cpcvConfig.TestGroups}",
            new
            {
                config.StrategyName,
                config.Symbol,
                config.Timeframe,
                config.From,
                config.To,
                cpcvConfig.Groups,
                cpcvConfig.TestGroups,
                cpcvConfig.PurgeBars,
                cpcvConfig.EmbargoBars,
                Ranges = config.ParameterRanges.Select(r => new { r.Name, r.Min, r.Max, r.Step }).ToList(),
                ModalParameters = cpcv.ModalParameters,
            },
            config.Symbol, config.Timeframe, userId);
        var metrics = new Dictionary<string, decimal>
        {
            ["CombinationsTested"] = cpcv.CombinationsTested,
            ["Paths"] = cpcv.Paths.Count,
            ["PositivePaths"] = cpcv.PositivePaths,
            ["MedianOosSharpe"] = cpcv.MedianOosSharpe,
            ["P05OosSharpe"] = cpcv.P05OosSharpe,
            ["P95OosSharpe"] = cpcv.P95OosSharpe,
            ["SelectionStability"] = cpcv.SelectionStability,
        };
        if (cpcv.Pbo is { } pbo) metrics["Pbo"] = (decimal)pbo;
        await tracker.SafeLogMetricsAsync(runId, metrics);
        await tracker.SafeCompleteAsync(runId, "Completed");

        return OptActionResult.Ok(
            $"CPCV completato: {cpcv.Paths.Count} percorsi ({cpcv.PositivePaths} positivi), " +
            $"Sharpe OOS mediano {cpcv.MedianOosSharpe:F2}, stabilità selezione {cpcv.SelectionStability:P0}.");
    }

    // --- Heatmap (2 parametri): parsing di AllResults in matrice --------------------------------

    /// <summary>
    /// Costruisce la matrice della heatmap dai risultati "k1=v1,k2=v2" → Sharpe OOS. Null se non c'è
    /// un run. Z null = combinazione mai valutata (tipico del Bayesian, che non copre la griglia).
    /// </summary>
    public HeatmapMatrix? BuildHeatmapMatrix(string xName, string yName)
    {
        if (Result is null) return null;

        var parsed = Result.AllResults.Select(kv =>
        {
            var dict = kv.Key.Split(',').Select(p => p.Split('=')).ToDictionary(a => a[0], a => decimal.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture));
            return (X: dict[xName], Y: dict[yName], Z: kv.Value);
        }).ToList();

        var xs = parsed.Select(p => p.X).Distinct().OrderBy(v => v).ToList();
        var ys = parsed.Select(p => p.Y).Distinct().OrderBy(v => v).ToList();
        var z = new double?[ys.Count][];
        for (var yi = 0; yi < ys.Count; yi++)
        {
            z[yi] = new double?[xs.Count];
            for (var xi = 0; xi < xs.Count; xi++)
            {
                var match = parsed.FirstOrDefault(p => p.X == xs[xi] && p.Y == ys[yi]);
                z[yi][xi] = match.Z == 0m && !parsed.Any(p => p.X == xs[xi] && p.Y == ys[yi]) ? null : (double)match.Z;
            }
        }

        return new HeatmapMatrix(
            xs.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            ys.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            z);
    }

    // --- Salvataggio della configurazione migliore ---------------------------------------------

    /// <summary>
    /// Salva la combinazione migliore del run corrente come strategia "ottimizzata". Null = niente
    /// da salvare (nessun run o nessun risultato): nessun messaggio, come nell'originale.
    /// </summary>
    public async Task<OptActionResult?> SaveBestAsync(string name, string strategyName, string userId, CancellationToken ct = default)
    {
        // [T1.6 fase 2] Da un run CPCV si salvano i parametri MODALI (i più spesso scelti dai
        // train dei percorsi) con lo Sharpe OOS MEDIANO: il candidato stabile, non il più fortunato.
        Dictionary<string, decimal>? parameters = null;
        decimal sharpe = 0m;
        if (Result is { BestParameters.Count: > 0 })
        {
            parameters = Result.BestParameters[0].Parameters;
            sharpe = Result.BestParameters[0].OutOfSampleSharpe;
        }
        else if (CpcvResult is { ModalParameters.Count: > 0 } cpcv)
        {
            parameters = cpcv.ModalParameters;
            sharpe = cpcv.MedianOosSharpe;
        }
        if (parameters is null) return null;
        if (string.IsNullOrWhiteSpace(name)) return OptActionResult.Error("Inserisci un nome.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.SavedStrategies.Add(new SavedStrategy
        {
            UserId = userId,
            Name = name.Trim(),
            StrategyName = strategyName,
            ParametersJson = JsonSerializer.Serialize(parameters),
            IsOptimized = true,
            OptimizationDate = DateTime.UtcNow,
            OptimizationSharpe = sharpe,
        });
        await db.SaveChangesAsync(ct);
        return OptActionResult.Ok($"Configurazione ottimizzata '{name}' salvata (Sharpe OOS {sharpe:F2}).");
    }

    private void ResetRun()
    {
        Result = null;
        CpcvResult = null;
        ResultConfig = null;
        EquitySeries = [];
    }
}
