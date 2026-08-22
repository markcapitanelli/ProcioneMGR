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

    /// <summary>
    /// [R3 — ROADMAP-RENDIMENTO] Eventi su cui <c>Threshold</c> NON lega: i flip del Supertrend
    /// (2=FlipUp, 3=FlipDown) sono cambi di segno, non percentili. Emettere le varianti 85/95 su
    /// questi eventi produceva DUPLICATI ESATTI — stessa strategia, stesso risultato — che
    /// gonfiavano il conteggio dei trial del DSR e uscivano come "confermati" doppi dalle cacce
    /// (misurato il 2026-07-25: i due sopravvissuti della caccia densa erano lo stesso candidato).
    /// Su questi eventi si emette una sola variante canonica, col default del parametro.
    /// </summary>
    private static readonly int[] ThresholdInertEvents = [2, 3];

    public List<ComposedCandidate> Generate(ComposerConfiguration config, int quota)
    {
        var all = new List<ComposedCandidate>();
        foreach (var eventType in Enumerable.Range(0, 6))
        {
            var thresholds = ThresholdInertEvents.Contains(eventType) ? [90m] : new[] { 85m, 95m };
            foreach (var direction in new[] { 0, 1 })
            {
                foreach (var threshold in thresholds)
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

        // Conferma per SOTTOPERIODI sui migliori dello screening: finestre contigue dentro il range
        // di selezione, parametri congelati, metriche ricavate SEGMENTANDO la curva di equity dello
        // screening (vedi MeasureSubPeriod per il difetto che questo chiude e per il motivo per cui
        // NON si affettano le candele). Il backtest sul range intero c'è già: era nella tupla, e
        // veniva buttato via con `_`.
        //
        // [2026-08-22] Il nome è «sottoperiodi» e NON «out-of-sample»: le spec sono state scelte
        // (top-N per Sharpe) sull'INTERO range, quindi le finestre sono in-sample per selezione
        // anche se non per fitting. Il fuori campione vero di questa piattaforma resta l'holdout.
        // Conseguenza già misurata del vecchio difetto: con oosSharpe == screenSharpe il gate qui
        // sotto era una TAUTOLOGIA — tutte le campagne vive hanno minOosSharpe == minScreenSharpe
        // == 0,4, quindi una spec che aveva passato lo screening non poteva essere respinta dalla
        // conferma. La fase «conferma walk-forward» non ha mai respinto nulla.
        var confirmed = new List<DiscoveryCandidate>();
        foreach (var (spec, screenSharpe, screenResult) in screened.OrderByDescending(s => s.Sharpe).Take(screening.ConfirmTopN))
        {
            ct.ThrowIfCancellationRequested();
            var windows = BuildOosWindows(screening.From, screening.To, screening.OosWindowMonths);
            var sharpes = new List<decimal>();
            var trades = 0;
            var mute = 0;
            foreach (var (from, to) in windows)
            {
                var (wSharpe, wTrades) = MeasureSubPeriod(screenResult, from, to, ppy);
                if (wSharpe is not decimal s) { mute++; continue; }
                sharpes.Add(s);
                trades += wTrades;
            }

            if (sharpes.Count == 0)
            {
                // Nessun sottoperiodo misurabile: NON si inventa uno 0. Nell'ordinamento delle gambe
                // uno zero vale più di qualunque Sharpe negativo — sarebbe una promozione travestita
                // da valore neutro. La spec semplicemente non si conferma.
                progress?.Report($"NON CONFERMATA {spec.Description} — nessuno dei {windows.Count} sottoperiodi è misurabile");
                continue;
            }
            if (mute > 0)
            {
                // Degradare dicendolo, e sul canale che l'operatore legge davvero: `progress` finisce
                // in ctx.LogLine via CreativeDiscoveryStage, un LogDebug no.
                progress?.Report($"{spec.Description}: {mute}/{windows.Count} sottoperiodi senza attività, "
                    + "esclusi dalla media invece di entrarci come zero.");
            }

            var subSharpe = sharpes.Average();
            if (subSharpe >= screening.MinOosSharpe && trades >= screening.MinTrades)
            {
                confirmed.Add(new DiscoveryCandidate
                {
                    StrategyName = spec.StrategyName,
                    Symbol = screening.Symbol,
                    Timeframe = screening.Timeframe,
                    Parameters = new(spec.Parameters),
                    OutOfSampleSharpe = Math.Round(subSharpe, 2),
                    // La provenienza viaggia col numero: a valle nessuno deve indovinare cosa sia.
                    WalkForwardSource = DiscoveryCandidate.SourceSelectionSubPeriods,
                    InSampleSharpe = Math.Round(screenSharpe, 2),
                    // [2026-08-22] Rendimento e drawdown del range di selezione, UNA volta. Prima
                    // erano la somma di N esecuzioni identiche: TotalReturn valeva N volte il
                    // rendimento vero, e MaxDrawdown era il massimo di N copie dello stesso numero.
                    TotalReturn = Math.Round(screenResult.TotalReturnPercent, 2),
                    MaxDrawdown = Math.Round(screenResult.MaxDrawdownPercent, 2),
                    // Trade ENTRATI nei sottoperiodi misurati: prima era il conteggio del range
                    // intero moltiplicato per il numero di finestre, quindi il gate anti-rumore
                    // «trades >= MinTrades» valeva in realtà T >= MinTrades/N.
                    TotalTrades = trades,
                    Windows = sharpes.Count,   // le finestre MISURATE, non quelle generate
                });
                progress?.Report($"CONFERMATA {spec.Description} — {subSharpe:F2} medio su {sharpes.Count}/{windows.Count} sottoperiodi, {trades} trade");
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

    /// <summary>
    /// Metriche di un SOTTOPERIODO [from, toExclusive), ricavate SEGMENTANDO la curva di equity del
    /// backtest di screening — non rieseguendo il backtest su una fetta di candele.
    ///
    /// <para><b>[2026-08-22] Il difetto che questo chiude.</b> Qui il ciclo di conferma passava al
    /// motore l'INTERA lista <c>candles</c>, e l'overload con candele precaricate <b>ignora
    /// <c>config.From/To</c></b>: nel core di <c>BacktestEngine</c> quelle due proprietà non
    /// compaiono mai — le sole occorrenze stanno nell'altro overload, quello che carica dal DB, e il
    /// contratto di <c>IBacktestEngine</c> dice che filtrare tocca al chiamante. Risultato: N
    /// esecuzioni <b>identiche</b> sul range intero, la cui media è per forza lo Sharpe della
    /// selezione. Su 13.893 righe di <c>ResearchCandidates</c>, <b>tutte e 9.665</b> prodotte da
    /// questa fase avevano <c>WalkForwardOosSharpe = round(SelectionSharpe, 2)</c> — nessuna
    /// esclusa — contro <b>zero</b> delle 3.833 della discovery classica, che affetta davvero.</para>
    ///
    /// <para><b>La cura ovvia era peggio del male.</b> Affettare le candele rompe due cose misurate:
    /// (a) <c>SignalCatalog.GetMatrixAsync</c> è una cache per ISTANZA di lista, quindi ogni fetta è
    /// una lista nuova e servirebbero <c>ConfirmTopN × N</c> ricalcoli della matrice dove oggi ce ne
    /// sono <i>zero</i>; (b) <c>CausalPercentile</c> pretende 125 osservazioni prima di emettere, e
    /// una finestra 1d di quattro mesi ne ha ~122: su quelle campagne <b>ogni</b> finestra sarebbe
    /// stata interamente warm-up, tutti i segnali percentile null, zero candidati confermati per
    /// sempre e senza una riga che lo dicesse.</para>
    ///
    /// <para>Segmentando la curva non si affetta niente: i segnali restano quelli calcolati una
    /// volta sull'intera serie, la cache non viene toccata, e gli N backtest della conferma
    /// <b>spariscono</b> invece di moltiplicarsi.</para>
    ///
    /// <para><b>Sharpe null quando la finestra NON è misurabile</b>: meno di 3 punti di equity,
    /// oppure capitale costante per tutta la finestra. In quel caso <c>Statistics.SharpeRatio</c>
    /// restituirebbe <c>0m</c> per varianza nulla — uno zero <i>fabbricato</i>, indistinguibile da
    /// una misura, che diluirebbe la media verso il basso senza che nessuno lo veda. Non si media
    /// ciò che non si è misurato.</para>
    ///
    /// <para>Caveat dichiarato: i sottoperiodi vengono da <b>un'unica corsa continua</b> (posizioni
    /// e capitale attraversano i confini) invece che da N corse indipendenti con capitale fresco. È
    /// più fedele, non meno: un trade aperto in una finestra e chiuso nella successiva contribuisce
    /// al P&amp;L di entrambe, e conta come un trade in quella di <b>ingresso</b>.</para>
    ///
    /// Internal per il collaudo diretto (InternalsVisibleTo su ProcioneMGR.Tests).
    /// </summary>
    internal static (decimal? Sharpe, int Trades) MeasureSubPeriod(
        BacktestResult run, DateTime from, DateTime toExclusive, int ppy)
    {
        var curve = run.EquityCurve;
        int i0 = -1, i1 = -2;
        for (var i = 0; i < curve.Count; i++)
        {
            var ts = curve[i].Timestamp;
            if (ts < from) continue;
            if (ts >= toExclusive) break;   // la curva è ordinata: oltre la fine si esce
            if (i0 < 0) i0 = i;
            i1 = i;
        }
        if (i0 < 0 || i1 < i0) return (null, 0);

        // Si include il punto PRECEDENTE alla finestra, quando c'è: il rendimento della prima barra
        // è (equity[i0] − equity[i0−1])/equity[i0−1], e senza quel punto la prima barra non
        // produrrebbe alcun rendimento.
        var start = i0 > 0 ? i0 - 1 : i0;
        var seg = new List<EquityPoint>(i1 - start + 1);
        for (var i = start; i <= i1; i++) seg.Add(curve[i]);

        var mossa = false;
        for (var i = 1; i < seg.Count; i++)
        {
            if (seg[i].Capital != seg[0].Capital) { mossa = true; break; }
        }
        if (seg.Count < 3 || !mossa) return (null, 0);

        var trades = 0;
        foreach (var t in run.Trades)
        {
            if (t.EntryTime >= from && t.EntryTime < toExclusive) trades++;
        }
        return (Statistics.SharpeRatio(seg, ppy), trades);
    }

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
