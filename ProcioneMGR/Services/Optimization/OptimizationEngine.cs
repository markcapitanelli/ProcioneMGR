using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization.Bayesian;

namespace ProcioneMGR.Services.Optimization;

/// <summary>
/// Ottimizzazione parametri via Grid Search + Walk-Forward Validation.
///
/// Per ogni finestra walk-forward (train in-sample / test out-of-sample):
///  - testa tutte le combinazioni di parametri IN PARALLELO (Parallel.ForEachAsync);
///  - per ognuna esegue 2 backtest (in-sample e out-of-sample) su candele GIA' caricate
///    (nessuna ricarica dal DB) e ne calcola lo Sharpe annualizzato;
///  - sceglie i parametri migliori della finestra (default: per Sharpe IN-SAMPLE, vedi nota);
///  - concatena (compounded) l'equity out-of-sample alla curva walk-forward globale.
///
/// NOTA METODOLOGICA: la selezione per-finestra usa lo Sharpe IN-SAMPLE
/// (<see cref="OptimizationSelectionMetric"/>). Selezionare sull'out-of-sample equivale a
/// ottimizzare sul test set (peeking) e gonfia lo Sharpe OOS: lo si puo' forzare via config
/// ma sconsigliato. Lo Sharpe OOS resta la metrica di VALUTAZIONE, non di selezione.
///
/// Memoria: si tengono solo gli aggregati scalari per combinazione e l'equity della sola
/// finestra corrente; non si trattengono tutti i backtest.
/// </summary>
public sealed class OptimizationEngine(
    IBacktestEngine backtestEngine,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<OptimizationEngine> logger) : IOptimizationEngine
{
    public async Task<OptimizationResult> OptimizeAsync(
        OptimizationConfiguration config,
        IProgress<OptimizationProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidateConfig(config);

        var sw = Stopwatch.StartNew();
        var periodsPerYear = Statistics.PeriodsPerYear(config.Timeframe);

        // 1) Carica TUTTE le candele del range una sola volta (caching).
        var allCandles = await LoadCandlesAsync(config, ct);
        if (allCandles.Count == 0)
        {
            throw new InvalidOperationException(
                $"Nessuna candela per {config.Symbol} {config.Timeframe} nel range richiesto. Esegui prima l'ingestione.");
        }

        // 2) Genera le finestre walk-forward.
        var windows = GenerateWindows(config);
        if (windows.Count == 0)
        {
            throw new InvalidOperationException(
                "Nessuna finestra walk-forward generata: il range è troppo corto per InSample + OutOfSample.");
        }

        // 3) Prepara lo spazio di ricerca. GridSearch: prodotto cartesiano esaustivo. Bayesian:
        //    nessun prodotto — i punti vengono proposti per finestra dal Gaussian Process.
        var bayesian = config.SearchStrategy == SearchStrategy.Bayesian;
        List<Dictionary<string, decimal>> combos = bayesian ? [] : GenerateCombinations(config.ParameterRanges);
        if (!bayesian && combos.Count == 0)
        {
            throw new InvalidOperationException("Nessuna combinazione di parametri generata.");
        }

        var perWindowBudget = bayesian
            ? Math.Max(1, config.BayesianInitialRandom) + Math.Max(0, config.BayesianIterations)
            : combos.Count;
        var totalWork = perWindowBudget * windows.Count;
        var tested = 0;
        var bestSoFar = decimal.MinValue;
        var bestLock = new object();

        var agg = new ConcurrentDictionary<string, ComboAggregate>();
        var wfWindows = new List<WalkForwardWindow>(windows.Count);
        var combined = new List<EquityPoint>();
        var runningCapital = config.InitialCapital;

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct,
        };

        logger.LogInformation("Ottimizzazione {Strategy} ({Mode}): {Budget} valutazioni × {Windows} finestre = {Total} backtest-coppie.",
            config.StrategyName, bayesian ? "Bayesian" : "GridSearch", perWindowBudget, windows.Count, totalWork);

        for (var w = 0; w < windows.Count; w++)
        {
            ct.ThrowIfCancellationRequested();
            var win = windows[w];
            var inSlice = Slice(allCandles, win.IsStart, win.IsEnd);
            var outSlice = Slice(allCandles, win.OosStart, win.OosEnd);

            // [T0.1] Embargo: salta le prime EmbargoBars barre dell'OOS, così l'informazione a
            // cavallo del confine (posizioni aperte a fine IS, lookback degli indicatori) non
            // contamina la misura fuori campione. Default 0 = comportamento storico invariato.
            var embargo = config.WalkForward.EmbargoBars;
            var oosStartEffective = win.OosStart;
            if (embargo > 0)
            {
                if (outSlice.Count - embargo < 2)
                {
                    logger.LogWarning(
                        "Finestra {W}: l'embargo di {Embargo} barre consuma quasi tutto l'out-of-sample ({N} barre): finestra saltata.",
                        w, embargo, outSlice.Count);
                    continue;
                }
                outSlice = outSlice.Skip(embargo).ToList();
                oosStartEffective = outSlice[0].TimestampUtc;
            }

            var comboResults = new ConcurrentBag<ComboResult>();
            var currentWindow = w;

            // Valutazione di UNA combinazione (IS+OOS su candele già in memoria) + bookkeeping
            // condiviso (agg/best/progress). Identica per grid e Bayesian: unico punto di verità.
            async Task<ComboResult?> EvaluateAsync(Dictionary<string, decimal> combo, CancellationToken ct2)
            {
                ComboResult? cr = null;
                try
                {
                    var isCfg = BuildBacktestConfig(config, combo, win.IsStart, win.IsEnd);
                    var isRes = await backtestEngine.RunBacktestAsync(isCfg, inSlice, ct2);
                    var isSharpe = Statistics.SharpeRatio(isRes.EquityCurve, periodsPerYear);

                    var oosCfg = BuildBacktestConfig(config, combo, oosStartEffective, win.OosEnd);
                    var oosRes = await backtestEngine.RunBacktestAsync(oosCfg, outSlice, ct2);
                    var oosSharpe = Statistics.SharpeRatio(oosRes.EquityCurve, periodsPerYear);

                    cr = new ComboResult(combo, isSharpe, oosSharpe,
                        oosRes.TotalReturnPercent, oosRes.MaxDrawdownPercent, oosRes.TotalTrades, oosRes.EquityCurve);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Combinazione invalida (es. FastPeriod >= SlowPeriod): si ignora, niente crash.
                    logger.LogDebug(ex, "Combinazione scartata: {Combo}", ComboKey(combo));
                }

                if (cr is not null)
                {
                    comboResults.Add(cr);
                    agg.AddOrUpdate(ComboKey(combo), _ => ComboAggregate.From(cr), (_, a) => a.Add(cr));
                    lock (bestLock)
                    {
                        if (cr.OosSharpe > bestSoFar) bestSoFar = cr.OosSharpe;
                    }
                }

                var n = Interlocked.Increment(ref tested);
                if (progress is not null && (n % 10 == 0 || n == totalWork))
                {
                    decimal best;
                    lock (bestLock) { best = bestSoFar; }
                    progress.Report(new OptimizationProgress
                    {
                        CombinationsTested = n,
                        TotalCombinations = totalWork,
                        CurrentWindow = currentWindow + 1,
                        TotalWindows = windows.Count,
                        BestSharpeSoFar = best == decimal.MinValue ? 0m : best,
                        Message = $"Window {currentWindow + 1}/{windows.Count} — valutazione {n}/{totalWork}",
                    });
                }
                return cr;
            }

            if (bayesian)
            {
                // La ricerca bayesiana è sequenziale: ogni iterazione rifitta il surrogato Gaussian
                // Process e ottimizza l'Expected Improvement, lavoro CPU-bound che altrimenti girerebbe
                // sul thread chiamante (in Blazor Server è il circuito → la UI resterebbe congelata per
                // l'intera ricerca). Task.Run lo sposta su un thread del pool; l'obiettivo stesso è
                // ormai genuinamente asincrono (nessun sync-over-async), quindi la sola CPU del GP
                // resta la ragione dell'offload. Simmetrico al grid, che gira su Parallel.ForEachAsync.
                await Task.Run(() => RunBayesianWindowAsync(config, EvaluateAsync, ct), ct);
            }
            else
            {
                await Parallel.ForEachAsync(combos, parallelOpts, async (combo, ct2) => await EvaluateAsync(combo, ct2));
            }

            // Selezione dei parametri migliori della finestra.
            var valid = comboResults.ToList();
            if (valid.Count == 0)
            {
                logger.LogWarning("Finestra {W}: nessuna combinazione valida (parametri tutti scartati?).", w);
                continue;
            }

            var best = config.SelectionMetric == OptimizationSelectionMetric.OutOfSampleSharpe
                ? valid.OrderByDescending(c => c.OosSharpe).ThenBy(c => ComboKey(c.Parameters)).First()
                : valid.OrderByDescending(c => c.IsSharpe).ThenBy(c => ComboKey(c.Parameters)).First();

            wfWindows.Add(new WalkForwardWindow
            {
                WindowIndex = w,
                InSampleStart = win.IsStart,
                InSampleEnd = win.IsEnd,
                // Con embargo attivo l'OOS effettivo inizia DOPO il cuscinetto: il report riflette
                // quello che è stato misurato davvero, non la finestra nominale.
                OutOfSampleStart = oosStartEffective,
                OutOfSampleEnd = win.OosEnd,
                BestParameters = new Dictionary<string, decimal>(best.Parameters),
                InSampleSharpe = best.IsSharpe,
                OutOfSampleSharpe = best.OosSharpe,
                OutOfSampleReturn = best.OosReturn,
            });

            AppendCompounded(combined, best.OosEquity, config.InitialCapital, ref runningCapital);
        }

        sw.Stop();
        return BuildResult(config, agg, wfWindows, combined, tested, sw.Elapsed);
    }

    // ---------------------------------------------------------------- CPCV (T1.6)

    /// <summary>
    /// [T1.6 roadmap macchina-ricerca] Validazione CPCV del percorso strategie: la serie è divisa in
    /// gruppi temporali contigui e per OGNI combinazione C(gruppi, gruppiTest) i parametri si
    /// scelgono sui gruppi di train (con purge/embargo attorno ai test) e si giudicano sui gruppi di
    /// test mai visti da quella scelta. Il risultato non è UN numero ma una DISTRIBUZIONE di Sharpe
    /// out-of-sample — più percorsi dagli stessi dati, che è l'antidoto strutturale al "one lucky
    /// path" del singolo walk-forward+holdout. Riusa <see cref="Validation.CombinatorialPurgedCv"/>
    /// (finora confinato al percorso ML) e <see cref="Validation.BacktestOverfitting"/> per il PBO.
    ///
    /// Il train di uno split è l'insieme dei gruppi INTERAMENTE contenuti negli indici di train:
    /// i gruppi mutilati dalle bande di purge/embargo vengono scartati (conservativo — meglio meno
    /// train pulito che train contaminato). Lo score di train di una combinazione è la MEDIA degli
    /// Sharpe sui gruppi di train: premia la costanza, non il singolo periodo fortunato.
    /// </summary>
    public async Task<CpcvResult> OptimizeCpcvAsync(
        OptimizationConfiguration config,
        CpcvConfiguration cpcv,
        IProgress<OptimizationProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(cpcv);
        ValidateConfig(config);

        var ppy = Statistics.PeriodsPerYear(config.Timeframe);
        var allCandles = await LoadCandlesAsync(config, ct);
        if (allCandles.Count < cpcv.Groups * 30)
        {
            throw new InvalidOperationException(
                $"Servono almeno {cpcv.Groups * 30} candele per {cpcv.Groups} gruppi CPCV sensati (trovate {allCandles.Count}).");
        }

        var combos = GenerateCombinations(config.ParameterRanges);
        if (combos.Count == 0) throw new InvalidOperationException("Nessuna combinazione di parametri generata.");

        var splits = new Validation.CombinatorialPurgedCv()
            .Split(allCandles.Count, cpcv.Groups, cpcv.TestGroups, cpcv.PurgeBars, cpcv.EmbargoBars);

        // Confini dei gruppi (stessa segmentazione dello splitter: l'ultimo assorbe il resto).
        var groupSize = allCandles.Count / cpcv.Groups;
        var groupSlices = new List<OhlcvData>[cpcv.Groups];
        for (var g = 0; g < cpcv.Groups; g++)
        {
            var start = g * groupSize;
            var end = g == cpcv.Groups - 1 ? allCandles.Count : start + groupSize;
            groupSlices[g] = allCandles.GetRange(start, end - start);
        }

        // Pre-calcolo per (combinazione × gruppo): Sharpe e rendimenti per-candela del gruppo.
        // È il costo dominante (combos × gruppi backtest), parallelizzato sulle combinazioni.
        var perGroupSharpe = new decimal[combos.Count, cpcv.Groups];
        var perGroupReturns = new double[combos.Count, cpcv.Groups][];
        var tested = 0;
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };

        await Parallel.ForAsync(0, combos.Count, parallelOpts, async (c, ct2) =>
        {
            for (var g = 0; g < cpcv.Groups; g++)
            {
                try
                {
                    var cfg = BuildBacktestConfig(config, combos[c], groupSlices[g][0].TimestampUtc, groupSlices[g][^1].TimestampUtc);
                    var res = await backtestEngine.RunBacktestAsync(cfg, groupSlices[g], ct2);
                    perGroupSharpe[c, g] = Statistics.SharpeRatio(res.EquityCurve, ppy);
                    perGroupReturns[c, g] = PerPeriodReturns(res.EquityCurve);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // Combinazione invalida su questo gruppo: Sharpe pessimo, mai scelta dal train.
                    perGroupSharpe[c, g] = decimal.MinValue;
                    perGroupReturns[c, g] = [];
                }
            }
            var n = Interlocked.Increment(ref tested);
            progress?.Report(new OptimizationProgress
            {
                CombinationsTested = n,
                TotalCombinations = combos.Count,
                CurrentWindow = 1,
                TotalWindows = splits.Count,
                Message = $"CPCV: {n}/{combos.Count} combinazioni × {cpcv.Groups} gruppi",
            });
        });

        // Un percorso per split: scelta sul train, giudizio sul test.
        var result = new CpcvResult { CombinationsTested = combos.Count, TotalPaths = splits.Count };
        foreach (var split in splits)
        {
            ct.ThrowIfCancellationRequested();

            var trainSet = split.TrainIndices.ToHashSet();
            var trainGroups = new List<int>();
            for (var g = 0; g < cpcv.Groups; g++)
            {
                var start = g * groupSize;
                var end = g == cpcv.Groups - 1 ? allCandles.Count : start + groupSize;
                var whole = true;
                for (var i = start; i < end; i++)
                {
                    if (!trainSet.Contains(i)) { whole = false; break; }
                }
                if (whole) trainGroups.Add(g);
            }
            if (trainGroups.Count == 0) continue;

            var bestCombo = -1;
            var bestScore = decimal.MinValue;
            for (var c = 0; c < combos.Count; c++)
            {
                decimal sum = 0m;
                var valid = true;
                foreach (var g in trainGroups)
                {
                    if (perGroupSharpe[c, g] == decimal.MinValue) { valid = false; break; }
                    sum += perGroupSharpe[c, g];
                }
                if (!valid) continue;
                var score = sum / trainGroups.Count;
                if (score > bestScore || (score == bestScore && bestCombo >= 0 && ComboKey(combos[c]).CompareTo(ComboKey(combos[bestCombo])) < 0))
                {
                    bestScore = score;
                    bestCombo = c;
                }
            }
            if (bestCombo < 0) continue;

            // Rendimenti OOS = concatenazione cronologica dei gruppi di test del percorso.
            var oosReturns = new List<double>();
            foreach (var g in split.TestGroups.OrderBy(x => x))
            {
                oosReturns.AddRange(perGroupReturns[bestCombo, g]);
            }

            result.Paths.Add(new CpcvPathResult
            {
                Combination = split.Combination,
                TestGroups = split.TestGroups,
                BestParameters = new Dictionary<string, decimal>(combos[bestCombo]),
                TrainSharpe = bestScore,
                OosSharpe = SharpeFromReturns(oosReturns, ppy),
            });
        }

        if (result.Paths.Count > 0)
        {
            var sharpes = result.Paths.Select(p => p.OosSharpe).OrderBy(x => x).ToList();
            result.MedianOosSharpe = TradeStatistics.Percentile(sharpes, 0.5m);
            result.P05OosSharpe = TradeStatistics.Percentile(sharpes, 0.05m);
            result.P95OosSharpe = TradeStatistics.Percentile(sharpes, 0.95m);
            result.PositivePaths = result.Paths.Count(p => p.OosSharpe > 0m);

            // Stabilità della selezione: quanto spesso i train scelgono gli stessi parametri.
            var modal = result.Paths.GroupBy(p => ComboKey(p.BestParameters)).OrderByDescending(g => g.Count()).First();
            result.ModalParameters = new Dictionary<string, decimal>(modal.First().BestParameters);
            result.SelectionStability = (decimal)modal.Count() / result.Paths.Count;
        }

        // PBO sul pannello dei rendimenti full-period (tutti i gruppi concatenati) dei candidati.
        try
        {
            var panel = new List<IReadOnlyList<double>>(combos.Count);
            for (var c = 0; c < combos.Count; c++)
            {
                var full = new List<double>();
                for (var g = 0; g < cpcv.Groups; g++) full.AddRange(perGroupReturns[c, g]);
                if (full.Count >= 10) panel.Add(full);
            }
            if (panel.Count >= 2)
            {
                result.Pbo = Validation.BacktestOverfitting.ProbabilityOfOverfitting(panel, partitions: 10)
                    .ProbabilityOfBacktestOverfitting;
            }
        }
        catch (ArgumentException) { result.Pbo = null; }

        return result;
    }

    private static double[] PerPeriodReturns(IReadOnlyList<EquityPoint> equity)
    {
        if (equity.Count < 2) return [];
        var r = new double[equity.Count - 1];
        for (var i = 1; i < equity.Count; i++)
        {
            var prev = equity[i - 1].Capital;
            r[i - 1] = prev > 0m ? (double)((equity[i].Capital - prev) / prev) : 0d;
        }
        return r;
    }

    private static decimal SharpeFromReturns(IReadOnlyList<double> returns, int periodsPerYear)
    {
        if (returns.Count < 3) return 0m;
        var mean = returns.Average();
        var sd = Math.Sqrt(returns.Sum(v => (v - mean) * (v - mean)) / (returns.Count - 1));
        return sd > 1e-12 ? (decimal)(mean / sd * Math.Sqrt(periodsPerYear)) : 0m;
    }

    // ---------------------------------------------------------------- validazione

    private static void ValidateConfig(OptimizationConfiguration config)
    {
        if (config.WalkForward is null)
            throw new ArgumentException("WalkForward è obbligatorio: il walk-forward non è opzionale.");
        if (config.WalkForward.InSampleMonths <= 0)
            throw new ArgumentException("InSampleMonths deve essere > 0 (il walk-forward è obbligatorio).");
        if (config.WalkForward.OutOfSampleMonths <= 0)
            throw new ArgumentException("OutOfSampleMonths deve essere > 0.");
        if (config.WalkForward.StepMonths <= 0)
            throw new ArgumentException("StepMonths deve essere > 0.");
        if (config.WalkForward.EmbargoBars < 0)
            throw new ArgumentException("EmbargoBars non può essere negativo.");
        if (config.ParameterRanges is null || config.ParameterRanges.Count == 0)
            throw new ArgumentException("Serve almeno un ParameterRange.");
        if (config.To <= config.From)
            throw new ArgumentException("L'intervallo non è valido: 'To' deve essere dopo 'From'.");
        foreach (var r in config.ParameterRanges)
        {
            if (r.Step <= 0m) throw new ArgumentException($"Step non valido per '{r.Name}' (deve essere > 0).");
            if (r.Max < r.Min) throw new ArgumentException($"Range non valido per '{r.Name}' (Max < Min).");
        }
    }

    // ---------------------------------------------------------------- caricamento dati

    private async Task<List<OhlcvData>> LoadCandlesAsync(OptimizationConfiguration config, CancellationToken ct)
    {
        var fromUtc = DateTime.SpecifyKind(config.From, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(config.To, DateTimeKind.Utc);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.OhlcvData
            .Where(c => c.Symbol == config.Symbol
                        && c.Timeframe == config.Timeframe
                        && c.TimestampUtc >= fromUtc
                        && c.TimestampUtc <= toUtc)
            .OrderBy(c => c.TimestampUtc)
            .ToListAsync(ct);
    }

    // ---------------------------------------------------------------- finestre walk-forward

    private static List<Window> GenerateWindows(OptimizationConfiguration config)
    {
        var wf = config.WalkForward;
        var windows = new List<Window>();
        for (var k = 0; k < 10_000; k++)
        {
            var isStart = config.From.AddMonths(k * wf.StepMonths);
            var isEnd = isStart.AddMonths(wf.InSampleMonths);
            var oosStart = isEnd;
            var oosEnd = oosStart.AddMonths(wf.OutOfSampleMonths);
            if (oosEnd > config.To)
            {
                break;
            }
            windows.Add(new Window(k, isStart, isEnd, oosStart, oosEnd));
        }
        return windows;
    }

    private static List<OhlcvData> Slice(List<OhlcvData> sorted, DateTime startInclusive, DateTime endExclusive)
    {
        // Candele in [start, end). end esclusivo: out-of-sample inizia dove finisce l'in-sample.
        var result = new List<OhlcvData>();
        foreach (var c in sorted)
        {
            if (c.TimestampUtc >= startInclusive && c.TimestampUtc < endExclusive)
            {
                result.Add(c);
            }
            else if (c.TimestampUtc >= endExclusive)
            {
                break; // ordinate: oltre la fine
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- combinazioni

    private static List<Dictionary<string, decimal>> GenerateCombinations(List<ParameterRange> ranges)
    {
        // Valori per ciascun parametro.
        var perParam = new List<(string Name, List<decimal> Values)>();
        foreach (var r in ranges)
        {
            var values = new List<decimal>();
            for (var v = r.Min; v <= r.Max; v += r.Step)
            {
                values.Add(r.IsInteger ? Math.Round(v, MidpointRounding.AwayFromZero) : v);
            }
            if (r.IsInteger)
            {
                values = values.Distinct().ToList();
            }
            perParam.Add((r.Name, values));
        }

        // Prodotto cartesiano iterativo.
        var combos = new List<Dictionary<string, decimal>> { new() };
        foreach (var (name, values) in perParam)
        {
            var next = new List<Dictionary<string, decimal>>(combos.Count * values.Count);
            foreach (var partial in combos)
            {
                foreach (var val in values)
                {
                    var copy = new Dictionary<string, decimal>(partial) { [name] = val };
                    next.Add(copy);
                }
            }
            combos = next;
        }
        return combos;
    }

    // ---------------------------------------------------------------- backtest config

    private static BacktestConfiguration BuildBacktestConfig(
        OptimizationConfiguration config, Dictionary<string, decimal> combo, DateTime from, DateTime to) => new()
    {
        ExchangeName = config.ExchangeName,
        Symbol = config.Symbol,
        Timeframe = config.Timeframe,
        From = from,
        To = to,
        InitialCapital = config.InitialCapital,
        PositionSizePercent = config.PositionSizePercent,
        FeePercent = config.CommissionPercent,
        // [R2] Prima mancava: la selezione dei parametri girava senza attrito mentre la validazione
        // holdout lo applicava. Vedi OptimizationConfiguration.SlippagePercent.
        SlippagePercent = config.SlippagePercent,
        StrategyName = config.StrategyName,
        StrategyParameters = new Dictionary<string, decimal>(combo),
    };

    // ---------------------------------------------------------------- ricerca bayesiana

    /// <summary>
    /// Ramo Bayesian per UNA finestra: il Gaussian Process propone i punti (invece del prodotto
    /// cartesiano) massimizzando l'Expected Improvement sullo Sharpe della finestra (surrogato
    /// economico e stazionario, in-sample o OOS secondo <c>SelectionMetric</c>). Sequenziale per
    /// costruzione (ogni proposta dipende dallo storico). La valutazione riusa <paramref name="evaluateAsync"/>
    /// — lo STESSO percorso del grid (IS+OOS, agg, best, progress). Il verdetto Deflated Sharpe
    /// resta calcolato UNA VOLTA a fine sweep in <see cref="BuildResult"/>, sui punti visitati.
    /// </summary>
    private static async Task RunBayesianWindowAsync(
        OptimizationConfiguration config,
        Func<Dictionary<string, decimal>, CancellationToken, Task<ComboResult?>> evaluateAsync,
        CancellationToken ct)
    {
        var space = BuildParameterSpace(config.ParameterRanges);
        var search = new BayesianSearch(new BayesianOptimizationEngine(new BayesianOptions { Seed = config.BayesianSeed }));
        var selectOos = config.SelectionMetric == OptimizationSelectionMetric.OutOfSampleSharpe;

        async Task<double> ObjectiveAsync(double[] vector)
        {
            ct.ThrowIfCancellationRequested();
            var combo = ToCombo(space, vector);
            var cr = await evaluateAsync(combo, ct);
            if (cr is null) return double.MinValue;   // combinazione invalida: regione da evitare
            return (double)(selectOos ? cr.OosSharpe : cr.IsSharpe);
        }

        await search.MaximizeAsync(space, ObjectiveAsync, config.BayesianIterations, config.BayesianInitialRandom, config.BayesianSeed);
    }

    private static ParameterSpace BuildParameterSpace(List<ParameterRange> ranges)
    {
        var dims = ranges
            .Select(r => new ParameterDimension(r.Name, (double)r.Min, (double)r.Max, r.IsInteger, (double)r.Step))
            .ToList();
        return new ParameterSpace(dims);
    }

    /// <summary>Vettore reale (già agganciato a intero/passo da <c>Denormalize</c>) → combinazione parametri.</summary>
    private static Dictionary<string, decimal> ToCombo(ParameterSpace space, double[] vector)
    {
        var combo = new Dictionary<string, decimal>(vector.Length);
        for (var i = 0; i < vector.Length; i++)
            combo[space.Dimensions[i].Name] = Math.Round((decimal)vector[i], 8);
        return combo;
    }

    // ---------------------------------------------------------------- equity concatenata

    private static void AppendCompounded(List<EquityPoint> combined, List<EquityPoint> windowEquity, decimal baseCapital, ref decimal runningCapital)
    {
        if (windowEquity.Count == 0)
        {
            return;
        }
        var startCap = windowEquity[0].Capital;
        if (startCap <= 0m)
        {
            startCap = baseCapital;
        }
        foreach (var p in windowEquity)
        {
            var factor = startCap == 0m ? 1m : p.Capital / startCap;
            combined.Add(new EquityPoint { Timestamp = p.Timestamp, Capital = runningCapital * factor });
        }
        runningCapital = combined[^1].Capital;
    }

    // ---------------------------------------------------------------- risultato

    private OptimizationResult BuildResult(
        OptimizationConfiguration config,
        ConcurrentDictionary<string, ComboAggregate> agg,
        List<WalkForwardWindow> windows,
        List<EquityPoint> combined,
        int tested,
        TimeSpan elapsed)
    {
        var allResults = new Dictionary<string, decimal>(agg.Count);
        var sets = new List<ParameterSet>(agg.Count);
        foreach (var (key, a) in agg)
        {
            var avgOos = a.Count == 0 ? 0m : a.SumOos / a.Count;
            allResults[key] = avgOos;
            sets.Add(new ParameterSet
            {
                Parameters = new Dictionary<string, decimal>(a.Parameters),
                InSampleSharpe = a.Count == 0 ? 0m : a.SumIs / a.Count,
                OutOfSampleSharpe = avgOos,
                TotalReturn = a.Count == 0 ? 0m : a.SumReturn / a.Count,
                MaxDrawdown = a.Count == 0 ? 0m : a.SumDd / a.Count,
                TotalTrades = a.Count == 0 ? 0 : (int)(a.SumTrades / a.Count),
            });
        }

        var top10 = sets.OrderByDescending(s => s.OutOfSampleSharpe).Take(10).ToList();
        var avgWindowOos = windows.Count == 0 ? 0m : windows.Average(x => x.OutOfSampleSharpe);

        // Verdetto anti-overfitting (Fase 1): il migliore è scelto tra agg.Count combinazioni distinte;
        // il Deflated Sharpe misura se lo Sharpe realizzato OOS batte la soglia attesa per puro effetto
        // del test multiplo. Osservato = Sharpe per-periodo della curva OOS combinata (ciò che si ottiene
        // davvero); trial = Sharpe OOS annualizzati di tutte le combinazioni.
        var validation = BuildValidation(allResults.Values, combined, Statistics.PeriodsPerYear(config.Timeframe));

        return new OptimizationResult
        {
            BestParameters = top10,
            WalkForwardAnalysis = new WalkForwardResult
            {
                Windows = windows,
                AverageOutOfSampleSharpe = avgWindowOos,
                CombinedEquityCurve = combined,
            },
            AllResults = allResults,
            ExecutionTime = elapsed,
            TotalCombinationsTested = tested,
            Validation = validation,
        };
    }

    /// <summary>
    /// Deflated Sharpe del migliore selezionato. null se meno di 2 combinazioni o curva OOS troppo
    /// corta (&lt; 3 punti). I rendimenti periodici della curva combinata alimentano i momenti
    /// (asimmetria/curtosi/T); gli Sharpe OOS annualizzati di tutte le combinazioni la varianza cross-trial.
    /// </summary>
    private static Validation.SelectionValidation? BuildValidation(
        ICollection<decimal> annualizedTrialSharpes, List<EquityPoint> combined, int periodsPerYear)
    {
        if (annualizedTrialSharpes.Count < 2 || combined.Count < 3) return null;

        var returns = new List<double>(combined.Count - 1);
        for (var i = 1; i < combined.Count; i++)
        {
            var prev = combined[i - 1].Capital;
            if (prev <= 0m) continue;
            returns.Add((double)((combined[i].Capital - prev) / prev));
        }
        if (returns.Count < 2) return null;

        return Validation.SelectionValidator.Validate(annualizedTrialSharpes.ToList(), returns, periodsPerYear);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// InvariantCulture esplicita: la chiave è anche PARSATA altrove (es. per l'heatmap) — con la
    /// cultura corrente del thread (es. it-IT, virgola come separatore decimale) un valore come
    /// "0,001" spezzerebbe lo split per virgola usato per separare i parametri, corrompendo il
    /// parsing. Bug latente mai emerso finché tutti i parametri sweepati erano interi (es.
    /// FastPeriod/SlowPeriod): esposto dalle soglie decimali di MlStrategy. Pubblico per essere
    /// testabile direttamente.
    /// </summary>
    public static string ComboKey(IReadOnlyDictionary<string, decimal> combo) =>
        string.Join(",", combo.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

    private readonly record struct Window(int Index, DateTime IsStart, DateTime IsEnd, DateTime OosStart, DateTime OosEnd);

    private sealed record ComboResult(
        Dictionary<string, decimal> Parameters,
        decimal IsSharpe,
        decimal OosSharpe,
        decimal OosReturn,
        decimal OosMaxDrawdown,
        int OosTrades,
        List<EquityPoint> OosEquity);

    private sealed record ComboAggregate(
        Dictionary<string, decimal> Parameters,
        decimal SumOos,
        decimal SumIs,
        decimal SumReturn,
        decimal SumDd,
        long SumTrades,
        int Count)
    {
        public static ComboAggregate From(ComboResult c) =>
            new(c.Parameters, c.OosSharpe, c.IsSharpe, c.OosReturn, c.OosMaxDrawdown, c.OosTrades, 1);

        public ComboAggregate Add(ComboResult c) =>
            this with
            {
                SumOos = SumOos + c.OosSharpe,
                SumIs = SumIs + c.IsSharpe,
                SumReturn = SumReturn + c.OosReturn,
                SumDd = SumDd + c.OosMaxDrawdown,
                SumTrades = SumTrades + c.OosTrades,
                Count = Count + 1,
            };
    }
}
