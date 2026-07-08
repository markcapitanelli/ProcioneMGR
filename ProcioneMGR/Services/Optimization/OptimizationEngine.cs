using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;

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

        // 3) Genera tutte le combinazioni di parametri (prodotto cartesiano).
        var combos = GenerateCombinations(config.ParameterRanges);
        if (combos.Count == 0)
        {
            throw new InvalidOperationException("Nessuna combinazione di parametri generata.");
        }

        var totalWork = combos.Count * windows.Count;
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

        logger.LogInformation("Ottimizzazione {Strategy}: {Combos} combinazioni × {Windows} finestre = {Total} backtest-coppie.",
            config.StrategyName, combos.Count, windows.Count, totalWork);

        for (var w = 0; w < windows.Count; w++)
        {
            ct.ThrowIfCancellationRequested();
            var win = windows[w];
            var inSlice = Slice(allCandles, win.IsStart, win.IsEnd);
            var outSlice = Slice(allCandles, win.OosStart, win.OosEnd);

            var comboResults = new ConcurrentBag<ComboResult>();
            var currentWindow = w;

            await Parallel.ForEachAsync(combos, parallelOpts, async (combo, ct2) =>
            {
                ComboResult? cr = null;
                try
                {
                    var isCfg = BuildBacktestConfig(config, combo, win.IsStart, win.IsEnd);
                    var isRes = await backtestEngine.RunBacktestAsync(isCfg, inSlice, ct2);
                    var isSharpe = Statistics.SharpeRatio(isRes.EquityCurve, periodsPerYear);

                    var oosCfg = BuildBacktestConfig(config, combo, win.OosStart, win.OosEnd);
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
                        Message = $"Window {currentWindow + 1}/{windows.Count} — combinazione {n}/{totalWork}",
                    });
                }
            });

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
                OutOfSampleStart = win.OosStart,
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
        StrategyName = config.StrategyName,
        StrategyParameters = new Dictionary<string, decimal>(combo),
    };

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
