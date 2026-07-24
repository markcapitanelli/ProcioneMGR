using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Services.Discovery;

// ============================================================================
// Models
// ============================================================================

/// <summary>A generated strategy spec: a concrete, ready-to-backtest parameterization.</summary>
public sealed class ComposedCandidate
{
    public string StrategyName { get; init; } = string.Empty;
    public Dictionary<string, decimal> Parameters { get; init; } = new();

    /// <summary>Canonical identity key (dedupe + traceability in logs/audit).</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Human-readable description ("RSI<30 AND VolPct>70 → Long").</summary>
    public string Description { get; init; } = string.Empty;
}

public sealed class ComposerConfiguration
{
    public int MaxCandidates { get; set; } = 200;
    public int Seed { get; set; } = 42;
    public bool EnableComposite { get; set; } = true;
    public bool EnableEvent { get; set; } = true;
    public bool EnableRegime { get; set; } = true;

    /// <summary>Signal ids allowed in composite specs (empty = the whole catalog).</summary>
    public List<int> SignalPool { get; set; } = new();
}

/// <summary>Screening + fixed-parameter walk-forward settings (mirrors the hunt gates).</summary>
public sealed class ComposerScreeningConfiguration
{
    public string ExchangeName { get; set; } = "Binance";
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public decimal InitialCapital { get; set; } = 10_000m;
    public decimal SlippagePercent { get; set; } = 0.05m;

    /// <summary>Commissione per lato (%) — allineata ai default di PipelineCosts (Bitget, conservativa).</summary>
    public decimal FeePercent { get; set; } = 0.1m;

    /// <summary>Funding dei perpetual (%/8h) — allineato ai default di PipelineCosts; era assente (0).</summary>
    public decimal FundingRatePercentPer8h { get; set; } = 0.01m;

    /// <summary>Selection-range gates before the walk-forward confirmation.</summary>
    public decimal MinScreenSharpe { get; set; } = 0.3m;
    public int MinTrades { get; set; } = 12;

    /// <summary>How many screened specs per series get the walk-forward confirmation.</summary>
    public int ConfirmTopN { get; set; } = 5;

    /// <summary>Fixed-parameter walk-forward: evaluate on rolling OOS windows of this many months.</summary>
    public int OosWindowMonths { get; set; } = 2;
    public decimal MinOosSharpe { get; set; } = 0.3m;
}

// ============================================================================
// Generator interfaces (one per archetype, all deterministic given the seed)
// ============================================================================

public interface ICompositeSignalGenerator
{
    List<ComposedCandidate> Generate(ComposerConfiguration config, int quota);
}

public interface IEventTriggerGenerator
{
    List<ComposedCandidate> Generate(ComposerConfiguration config, int quota);
}

public interface IRegimeMapGenerator
{
    List<ComposedCandidate> Generate(ComposerConfiguration config, int quota);
}

public interface IStrategyComposer
{
    /// <summary>Generates candidate specs (deterministic per seed, deduped, plausibility-filtered).</summary>
    List<ComposedCandidate> Compose(ComposerConfiguration config);

    /// <summary>
    /// Generates + evaluates on one series: full selection-range screen, then fixed-parameter
    /// walk-forward on the top few. Returns candidates in the same shape Discovery produces,
    /// ready for the holdout gauntlet.
    /// </summary>
    Task<List<DiscoveryCandidate>> ComposeAndScreenAsync(
        ComposerConfiguration config,
        ComposerScreeningConfiguration screening,
        IProgress<string>? progress,
        CancellationToken ct);
}

// ============================================================================
// Generators
// ============================================================================

/// <summary>
/// Systematic composition of 2-3 elementary conditions into entry rules. Deterministic:
/// enumerates the full plausible space in a fixed order, then takes a seeded sample.
/// Plausibility: per-signal (operator, threshold) menus only contain semantically sensible
/// combos (e.g. Supertrend direction is only "&gt;50" or "&lt;50"); contradictions are
/// impossible by construction (distinct signals per spec). Diversity: coarse 15-point
/// threshold steps + canonical-key dedupe.
/// </summary>
public sealed class CompositeSignalGenerator : ICompositeSignalGenerator
{
    // (op, thr) menus per semantic family: 0 = "<", 1 = ">".
    private static readonly (int Op, decimal Thr)[] OscillatorMenu = [(0, 20m), (0, 35m), (1, 65m), (1, 80m)];
    private static readonly (int Op, decimal Thr)[] DirectionMenu = [(1, 50m), (0, 50m)];
    private static readonly (int Op, decimal Thr)[] PercentileMenu = [(0, 20m), (1, 80m), (1, 65m)];

    private static (int Op, decimal Thr)[] MenuFor(int signal) => signal switch
    {
        3 => DirectionMenu,                 // Supertrend dir
        0 or 1 or 2 or 10 => OscillatorMenu, // RSI, StochD, %B, MFI (tutti nativi 0-100: [3.8a] l'MFI
                                             // era finito nel menu percentile di default, perdendo la
                                             // soglia <35; è un oscillatore come l'RSI e va trattato tale)
        _ => PercentileMenu,                // percentile-normalized signals (incl. 9 OraUTC, 11 OBV, 12/13 post-evento)
    };

    public List<ComposedCandidate> Generate(ComposerConfiguration config, int quota)
    {
        var pool = config.SignalPool.Count > 0
            ? config.SignalPool.Where(s => s >= 0 && s < SignalCatalog.SignalCount).Distinct().OrderBy(s => s).ToList()
            : [.. Enumerable.Range(0, SignalCatalog.SignalCount)];

        var all = new List<ComposedCandidate>();
        var seen = new HashSet<string>();

        // Enumerate 2-condition AND specs over distinct signal pairs, both directions.
        for (var a = 0; a < pool.Count; a++)
        {
            for (var b = a + 1; b < pool.Count; b++)
            {
                foreach (var condA in MenuFor(pool[a]))
                {
                    foreach (var condB in MenuFor(pool[b]))
                    {
                        foreach (var direction in new[] { 0, 1 })
                        {
                            Add(all, seen, Build(pool[a], condA, pool[b], condB, thirdSignal: null, default, direction));
                        }
                    }
                }
            }
        }

        // A slice of 3-condition specs: extend each pair with a trend filter (Supertrend dir),
        // the classic "oscillator + volume + trend agreement" family.
        if (pool.Contains(3))
        {
            for (var a = 0; a < pool.Count; a++)
            {
                for (var b = a + 1; b < pool.Count; b++)
                {
                    if (pool[a] == 3 || pool[b] == 3)
                    {
                        continue;
                    }
                    foreach (var condA in MenuFor(pool[a]))
                    {
                        foreach (var direction in new[] { 0, 1 })
                        {
                            var trendCond = direction == 0 ? (1, 50m) : (0, 50m); // long wants trend up, short down
                            Add(all, seen, Build(pool[a], condA, pool[b], (1, 65m), 3, trendCond, direction));
                        }
                    }
                }
            }
        }

        return SeededSample(all, quota, config.Seed);
    }

    private static ComposedCandidate Build(
        int sigA, (int Op, decimal Thr) condA,
        int sigB, (int Op, decimal Thr) condB,
        int? thirdSignal, (int Op, decimal Thr) condC,
        int direction)
    {
        var parameters = new Dictionary<string, decimal>
        {
            ["Logic"] = 0m,
            ["Direction"] = direction,
            ["EntryCount"] = thirdSignal is null ? 2m : 3m,
            ["EntrySig1"] = sigA,
            ["EntryOp1"] = condA.Op,
            ["EntryThr1"] = condA.Thr,
            ["EntrySig2"] = sigB,
            ["EntryOp2"] = condB.Op,
            ["EntryThr2"] = condB.Thr,
            // Exit: mirror of the FIRST condition (oversold-entry → overbought-exit style);
            // direction-neutral because Close just flattens.
            ["ExitCount"] = 1m,
            ["ExitSig1"] = sigA,
            ["ExitOp1"] = condA.Op == 0 ? 1m : 0m,
            ["ExitThr1"] = condA.Op == 0 ? Math.Min(100m, 100m - condA.Thr) : Math.Max(0m, 100m - condA.Thr),
        };
        if (thirdSignal is int sigC)
        {
            parameters["EntrySig3"] = sigC;
            parameters["EntryOp3"] = condC.Op;
            parameters["EntryThr3"] = condC.Thr;
        }

        var desc = $"{Cond(sigA, condA)} AND {Cond(sigB, condB)}"
                 + (thirdSignal is int s3 ? $" AND {Cond(s3, condC)}" : "")
                 + (direction == 0 ? " → Long" : " → Short");
        return new ComposedCandidate
        {
            StrategyName = "Composite",
            Parameters = parameters,
            Key = Canonical(parameters),
            Description = desc,
        };

        static string Cond(int sig, (int Op, decimal Thr) c)
            => $"{SignalCatalog.SignalNames[sig]}{(c.Op == 0 ? "<" : ">")}{c.Thr:0}";
    }

    private static void Add(List<ComposedCandidate> list, HashSet<string> seen, ComposedCandidate candidate)
    {
        if (seen.Add(candidate.Key))
        {
            list.Add(candidate);
        }
    }

    internal static string Canonical(Dictionary<string, decimal> parameters)
        => string.Join(";", parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

    internal static List<ComposedCandidate> SeededSample(List<ComposedCandidate> all, int quota, int seed)
    {
        if (all.Count <= quota)
        {
            return all;
        }
        // Deterministic partial Fisher-Yates over the (already deterministic) enumeration order.
        var rng = new Random(seed);
        var arr = all.ToArray();
        for (var i = 0; i < quota; i++)
        {
            var j = rng.Next(i, arr.Length);
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }
        return [.. arr.Take(quota)];
    }
}

/// <summary>Enumerates the discrete-event trigger space (event × direction × threshold × holding time).</summary>
public sealed class EventTriggerGenerator : IEventTriggerGenerator
{
    private static readonly string[] EventNames = ["VolSpike", "VolCrush", "FlipUp", "FlipDown", "ShockDown", "ShockUp"];

    public List<ComposedCandidate> Generate(ComposerConfiguration config, int quota)
    {
        var all = new List<ComposedCandidate>();
        foreach (var eventType in Enumerable.Range(0, 6))
        {
            foreach (var direction in new[] { 0, 1 })
            {
                foreach (var threshold in new[] { 85m, 95m })
                {
                    foreach (var hold in new[] { 12m, 48m })
                    {
                        var parameters = new Dictionary<string, decimal>
                        {
                            ["EventType"] = eventType,
                            ["Direction"] = direction,
                            ["Threshold"] = threshold,
                            ["MaxHoldBars"] = hold,
                        };
                        all.Add(new ComposedCandidate
                        {
                            StrategyName = "EventTrigger",
                            Parameters = parameters,
                            Key = CompositeSignalGenerator.Canonical(parameters),
                            Description = $"{EventNames[eventType]}@{threshold:0} → {(direction == 0 ? "Long" : "Short")} per {hold:0} barre",
                        });
                    }
                }
            }
        }
        return CompositeSignalGenerator.SeededSample(all, quota, config.Seed + 1);
    }
}

/// <summary>
/// Enumerates regime→strategy assignments using the platform's known family bias
/// (trend-followers in trends, mean-reverters sideways, optional stand-aside).
/// </summary>
public sealed class RegimeMapGenerator : IRegimeMapGenerator
{
    // Indices into RegimeConditionalStrategy.SubStrategyCatalog.
    private static readonly int[] TrendFollowers = [1, 3, 5, 7];   // EmaCross, MacdTrend, Momentum, Supertrend
    private static readonly int[] MeanReverters = [2, 4, 8, 9];    // RsiOversold, Bollinger, Stochastic, VwapReversion

    public List<ComposedCandidate> Generate(ComposerConfiguration config, int quota)
    {
        var all = new List<ComposedCandidate>();
        foreach (var up in TrendFollowers.Prepend(0))
        {
            foreach (var flat in MeanReverters.Prepend(0))
            {
                if (up == 0 && flat == 0)
                {
                    continue; // nothing would ever trade
                }
                foreach (var down in new[] { 0, up }) // stand aside in downtrends, or run the trend-follower there too
                {
                    if (down == up && up == 0)
                    {
                        continue;
                    }
                    foreach (var trendPeriod in new[] { 50m, 100m })
                    {
                        var parameters = new Dictionary<string, decimal>
                        {
                            ["TrendPeriod"] = trendPeriod,
                            ["UpStrategy"] = up,
                            ["DownStrategy"] = down,
                            ["FlatStrategy"] = flat,
                        };
                        var key = CompositeSignalGenerator.Canonical(parameters);
                        if (all.Any(c => c.Key == key))
                        {
                            continue;
                        }
                        all.Add(new ComposedCandidate
                        {
                            StrategyName = "RegimeConditional",
                            Parameters = parameters,
                            Key = key,
                            Description = $"Up→{Name(up)}, Down→{Name(down)}, Flat→{Name(flat)} (SMA{trendPeriod:0})",
                        });
                    }
                }
            }
        }
        return CompositeSignalGenerator.SeededSample(all, quota, config.Seed + 2);

        static string Name(int idx) => RegimeConditionalStrategy.SubStrategyCatalog[idx];
    }
}

// ============================================================================
// Composer (orchestrator + screening)
// ============================================================================

/// <summary>
/// Creative-discovery orchestrator: generates candidate specs from the three archetype
/// generators (deterministic per seed), then evaluates them with the SAME honesty rules of
/// the classic hunt: full selection-range screen (Sharpe + trade-count gates) → fixed-parameter
/// walk-forward on rolling OOS windows for the top few → DiscoveryCandidate output for the
/// standard holdout gauntlet. Registered SCOPED (declared deviation from the "Singleton" in
/// the spec: it depends on IBacktestEngine, which is scoped).
/// </summary>
public sealed class StrategyComposer(
    ICompositeSignalGenerator compositeGenerator,
    IEventTriggerGenerator eventGenerator,
    IRegimeMapGenerator regimeGenerator,
    IBacktestEngine backtest,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<StrategyComposer> logger) : IStrategyComposer
{
    public List<ComposedCandidate> Compose(ComposerConfiguration config)
    {
        var enabled = new List<(bool On, double Share, Func<int, List<ComposedCandidate>> Gen)>
        {
            (config.EnableComposite, 0.6, q => compositeGenerator.Generate(config, q)),
            (config.EnableEvent, 0.2, q => eventGenerator.Generate(config, q)),
            (config.EnableRegime, 0.2, q => regimeGenerator.Generate(config, q)),
        }.Where(g => g.On).ToList();
        if (enabled.Count == 0)
        {
            return [];
        }

        var totalShare = enabled.Sum(g => g.Share);
        var result = new List<ComposedCandidate>();
        foreach (var (_, share, generate) in enabled)
        {
            var quota = Math.Max(1, (int)Math.Round(config.MaxCandidates * share / totalShare));
            result.AddRange(generate(quota));
        }
        return result.Take(config.MaxCandidates).ToList();
    }

    public async Task<List<DiscoveryCandidate>> ComposeAndScreenAsync(
        ComposerConfiguration config,
        ComposerScreeningConfiguration screening,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var candidates = Compose(config);
        progress?.Report($"{candidates.Count} spec generate; screening su {screening.Symbol} {screening.Timeframe}…");

        // Candles loaded ONCE per series; the SignalCatalog cache keys on this instance,
        // so the (expensive) normalized matrix is computed a single time for ALL specs.
        List<OhlcvData> candles;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            candles = await db.OhlcvData.AsNoTracking()
                .Where(c => c.Symbol == screening.Symbol && c.Timeframe == screening.Timeframe
                         && c.TimestampUtc >= screening.From && c.TimestampUtc <= screening.To)
                .OrderBy(c => c.TimestampUtc)
                .ToListAsync(ct);
        }
        if (candles.Count < 500)
        {
            logger.LogWarning("Composer: {Symbol} {Tf} ha solo {N} candele nel range: serie saltata.",
                screening.Symbol, screening.Timeframe, candles.Count);
            return [];
        }

        var ppy = Statistics.PeriodsPerYear(screening.Timeframe);
        var screened = new List<(ComposedCandidate Spec, decimal Sharpe, BacktestResult Result)>();
        var evaluated = 0;
        foreach (var spec in candidates)
        {
            ct.ThrowIfCancellationRequested();
            evaluated++;
            try
            {
                var result = await backtest.RunBacktestAsync(
                    BuildConfig(spec, screening, screening.From, screening.To), candles, ct);
                if (result.TotalTrades < screening.MinTrades)
                {
                    continue;
                }
                var sharpe = Statistics.SharpeRatio(result.EquityCurve, ppy);
                if (sharpe >= screening.MinScreenSharpe)
                {
                    screened.Add((spec, sharpe, result));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A malformed spec must not sink the whole batch (the generators should not
                // produce any, but the guard keeps the run resilient).
                logger.LogWarning(ex, "Composer: spec {Key} scartata (backtest fallito).", spec.Key);
            }
            if (evaluated % 50 == 0)
            {
                progress?.Report($"screen {evaluated}/{candidates.Count} — {screened.Count} oltre i gate");
            }
        }

        // Fixed-parameter walk-forward on the top few: rolling OOS windows over the selection
        // range. No optimization happens here (parameters are frozen), so every window is
        // out-of-sample by construction.
        var confirmed = new List<DiscoveryCandidate>();
        foreach (var (spec, screenSharpe, _) in screened.OrderByDescending(s => s.Sharpe).Take(screening.ConfirmTopN))
        {
            ct.ThrowIfCancellationRequested();
            var windows = BuildOosWindows(screening.From, screening.To, screening.OosWindowMonths);
            var sharpes = new List<decimal>();
            var trades = 0;
            var totalReturn = 0m;
            var maxDd = 0m;
            foreach (var (from, to) in windows)
            {
                var result = await backtest.RunBacktestAsync(BuildConfig(spec, screening, from, to), candles, ct);
                sharpes.Add(Statistics.SharpeRatio(result.EquityCurve, ppy));
                trades += result.TotalTrades;
                totalReturn += result.TotalReturnPercent;
                maxDd = Math.Max(maxDd, result.MaxDrawdownPercent);
            }
            var oosSharpe = sharpes.Count > 0 ? sharpes.Average() : 0m;
            if (oosSharpe >= screening.MinOosSharpe && trades >= screening.MinTrades)
            {
                confirmed.Add(new DiscoveryCandidate
                {
                    StrategyName = spec.StrategyName,
                    Symbol = screening.Symbol,
                    Timeframe = screening.Timeframe,
                    Parameters = new(spec.Parameters),
                    OutOfSampleSharpe = Math.Round(oosSharpe, 2),
                    InSampleSharpe = Math.Round(screenSharpe, 2),
                    TotalReturn = Math.Round(totalReturn, 2),
                    MaxDrawdown = Math.Round(maxDd, 2),
                    TotalTrades = trades,
                    Windows = windows.Count,
                });
                progress?.Report($"CONFERMATA {spec.Description} — OOS {oosSharpe:F2} su {windows.Count} finestre");
            }
        }

        logger.LogInformation("Composer {Symbol} {Tf}: {Gen} generate, {Screen} oltre lo screen, {Conf} confermate WF.",
            screening.Symbol, screening.Timeframe, candidates.Count, screened.Count, confirmed.Count);
        return confirmed;
    }

    private static BacktestConfiguration BuildConfig(
        ComposedCandidate spec, ComposerScreeningConfiguration screening, DateTime from, DateTime to)
        => new()
        {
            ExchangeName = screening.ExchangeName,
            Symbol = screening.Symbol,
            Timeframe = screening.Timeframe,
            From = from,
            To = to,
            InitialCapital = screening.InitialCapital,
            PositionSizePercent = 10m,
            StrategyName = spec.StrategyName,
            StrategyParameters = new(spec.Parameters),
            SlippagePercent = screening.SlippagePercent,
            FeePercent = screening.FeePercent,
            FundingRatePercentPer8h = screening.FundingRatePercentPer8h,
        };

    /// <summary>Rolling, non-overlapping OOS windows covering [from, to]. Public for direct testability.</summary>
    public static List<(DateTime From, DateTime To)> BuildOosWindows(DateTime from, DateTime to, int windowMonths)
    {
        var windows = new List<(DateTime, DateTime)>();
        var cursor = from;
        while (cursor < to)
        {
            var end = cursor.AddMonths(windowMonths);
            if (end > to)
            {
                end = to;
            }
            // Skip degenerate tail windows shorter than half the span.
            if ((end - cursor).TotalDays >= windowMonths * 15)
            {
                windows.Add((cursor, end));
            }
            cursor = end;
        }
        return windows;
    }
}
