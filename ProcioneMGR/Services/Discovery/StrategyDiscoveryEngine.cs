using System.Diagnostics;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Services.Discovery;

/// <summary>
/// Motore di ricerca strategie. Per ogni combinazione (strategia × coppia × timeframe)
/// lancia un'ottimizzazione walk-forward e ne estrae la migliore configurazione di
/// parametri (per Sharpe out-of-sample medio = robusta, non overfittata). Ordina tutte
/// le candidate per Sharpe OOS: in cima ci sono le strategie più proficue e affidabili.
/// </summary>
public sealed class StrategyDiscoveryEngine(
    IOptimizationEngine optimizer,
    IStrategyFactory strategyFactory,
    ILogger<StrategyDiscoveryEngine> logger) : IStrategyDiscovery
{
    public async Task<StrategyDiscoveryResult> DiscoverAsync(
        StrategyDiscoveryConfiguration config, IProgress<DiscoveryProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var strategies = config.Strategies.Count > 0
            ? config.Strategies
            : strategyFactory.Prototypes.Select(p => p.Name).ToList();

        var jobs = (from sym in config.Symbols
                    from tf in config.Timeframes
                    from st in strategies
                    select (Symbol: sym, Timeframe: tf, Strategy: st)).ToList();

        var candidates = new List<DiscoveryCandidate>();
        var tested = 0;
        var done = 0;
        var bestSoFar = decimal.MinValue;

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();

            var optCfg = new OptimizationConfiguration
            {
                ExchangeName = config.ExchangeName,
                Symbol = job.Symbol,
                Timeframe = job.Timeframe,
                From = config.From,
                To = config.To,
                InitialCapital = config.InitialCapital,
                CommissionPercent = config.CommissionPercent,
                PositionSizePercent = 100m,
                StrategyName = job.Strategy,
                ParameterRanges = DefaultRanges(job.Strategy),
                WalkForward = config.WalkForward,
                SelectionMetric = OptimizationSelectionMetric.InSampleSharpe,
            };

            try
            {
                var r = await optimizer.OptimizeAsync(optCfg, null, ct);
                tested += r.TotalCombinationsTested;
                if (r.BestParameters.Count > 0 && r.WalkForwardAnalysis.Windows.Count > 0)
                {
                    var best = r.BestParameters[0];
                    candidates.Add(new DiscoveryCandidate
                    {
                        StrategyName = job.Strategy,
                        Symbol = job.Symbol,
                        Timeframe = job.Timeframe,
                        Parameters = new Dictionary<string, decimal>(best.Parameters),
                        OutOfSampleSharpe = best.OutOfSampleSharpe,
                        InSampleSharpe = best.InSampleSharpe,
                        TotalReturn = best.TotalReturn,
                        MaxDrawdown = best.MaxDrawdown,
                        TotalTrades = best.TotalTrades,
                        Windows = r.WalkForwardAnalysis.Windows.Count,
                        Validation = r.Validation,
                    });
                    if (best.OutOfSampleSharpe > bestSoFar) bestSoFar = best.OutOfSampleSharpe;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Discovery: salto {Sym} {Tf} {Strat} ({Msg}).", job.Symbol, job.Timeframe, job.Strategy, ex.Message);
            }

            done++;
            progress?.Report(new DiscoveryProgress
            {
                Completed = done,
                Total = jobs.Count,
                Message = $"{job.Symbol} {job.Timeframe} {job.Strategy}",
                BestSharpeSoFar = bestSoFar == decimal.MinValue ? 0m : bestSoFar,
            });
        }

        return new StrategyDiscoveryResult
        {
            Candidates = candidates.OrderByDescending(c => c.OutOfSampleSharpe).Take(config.TopN).ToList(),
            JobsRun = jobs.Count,
            CombinationsTested = tested,
            ExecutionTime = sw.Elapsed,
        };
    }

    /// <summary>Griglie di parametri di default per strategia (modeste, per spazzare in fretta).</summary>
    public static List<ParameterRange> DefaultRanges(string strategyName) => strategyName switch
    {
        "EmaCross" =>
        [
            new() { Name = "FastPeriod", Min = 5, Max = 30, Step = 5, IsInteger = true },
            new() { Name = "SlowPeriod", Min = 20, Max = 100, Step = 20, IsInteger = true },
        ],
        "RsiOversold" =>
        [
            new() { Name = "Period", Min = 7, Max = 21, Step = 7, IsInteger = true },
            new() { Name = "OversoldThreshold", Min = 20, Max = 35, Step = 5, IsInteger = true },
            new() { Name = "OverboughtThreshold", Min = 65, Max = 80, Step = 5, IsInteger = true },
        ],
        "MacdTrend" =>
        [
            new() { Name = "FastPeriod", Min = 8, Max = 16, Step = 4, IsInteger = true },
            new() { Name = "SlowPeriod", Min = 21, Max = 31, Step = 5, IsInteger = true },
            new() { Name = "SignalPeriod", Min = 9, Max = 9, Step = 1, IsInteger = true },
        ],
        "BollingerMeanReversion" =>
        [
            new() { Name = "Period", Min = 14, Max = 26, Step = 6, IsInteger = true },
            new() { Name = "StdDevMultiplier", Min = 1.5m, Max = 2.5m, Step = 0.5m, IsInteger = false },
        ],
        "Momentum" =>
        [
            new() { Name = "LookbackPeriod", Min = 10, Max = 30, Step = 10, IsInteger = true },
            new() { Name = "Threshold", Min = 0.03m, Max = 0.07m, Step = 0.02m, IsInteger = false },
        ],
        "DonchianBreakout" =>
        [
            new() { Name = "EntryPeriod", Min = 10, Max = 50, Step = 10, IsInteger = true },
            new() { Name = "ExitPeriod", Min = 5, Max = 15, Step = 5, IsInteger = true },
            new() { Name = "Direction", Min = 0, Max = 2, Step = 1, IsInteger = true },
        ],
        "PriceSmaCross" =>
        [
            new() { Name = "Period", Min = 50, Max = 200, Step = 50, IsInteger = true },
            new() { Name = "AllowShort", Min = 0, Max = 1, Step = 1, IsInteger = true },
        ],
        "Supertrend" =>
        [
            new() { Name = "AtrPeriod", Min = 7, Max = 14, Step = 7, IsInteger = true },
            new() { Name = "Multiplier", Min = 2m, Max = 4m, Step = 1m, IsInteger = false },
            new() { Name = "AllowShort", Min = 0, Max = 1, Step = 1, IsInteger = true },
        ],
        "Stochastic" =>
        [
            new() { Name = "KPeriod", Min = 9, Max = 14, Step = 5, IsInteger = true },
            new() { Name = "DPeriod", Min = 3, Max = 3, Step = 1, IsInteger = true },
            new() { Name = "OversoldThreshold", Min = 15, Max = 25, Step = 5, IsInteger = true },
            new() { Name = "OverboughtThreshold", Min = 75, Max = 85, Step = 5, IsInteger = true },
        ],
        "VwapReversion" =>
        [
            new() { Name = "Threshold", Min = 0.005m, Max = 0.02m, Step = 0.005m, IsInteger = false },
            new() { Name = "AllowShort", Min = 0, Max = 1, Step = 1, IsInteger = true },
        ],
        // Meta-strategie creative: la griglia di DEFAULT sweppa un template classico (le
        // combinazioni RICCHE di forme/segnali le genera lo StrategyComposer). Le pinned
        // range (Min=Max) fissano la forma, le altre sweppano le soglie.
        "Composite" =>
        [
            new() { Name = "Logic", Min = 0, Max = 0, Step = 1, IsInteger = true },
            new() { Name = "Direction", Min = 0, Max = 0, Step = 1, IsInteger = true },
            new() { Name = "EntryCount", Min = 2, Max = 2, Step = 1, IsInteger = true },
            new() { Name = "EntrySig1", Min = 0, Max = 0, Step = 1, IsInteger = true },  // RSI
            new() { Name = "EntryOp1", Min = 0, Max = 0, Step = 1, IsInteger = true },   // <
            new() { Name = "EntryThr1", Min = 20, Max = 35, Step = 15, IsInteger = true },
            new() { Name = "EntrySig2", Min = 4, Max = 4, Step = 1, IsInteger = true },  // volume pct
            new() { Name = "EntryOp2", Min = 1, Max = 1, Step = 1, IsInteger = true },   // >
            new() { Name = "EntryThr2", Min = 60, Max = 80, Step = 20, IsInteger = true },
            new() { Name = "ExitCount", Min = 1, Max = 1, Step = 1, IsInteger = true },
            new() { Name = "ExitSig1", Min = 0, Max = 0, Step = 1, IsInteger = true },
            new() { Name = "ExitOp1", Min = 1, Max = 1, Step = 1, IsInteger = true },
            new() { Name = "ExitThr1", Min = 65, Max = 80, Step = 15, IsInteger = true },
        ],
        "EventTrigger" =>
        [
            new() { Name = "EventType", Min = 0, Max = 0, Step = 1, IsInteger = true },
            new() { Name = "Direction", Min = 0, Max = 1, Step = 1, IsInteger = true },
            new() { Name = "Threshold", Min = 85, Max = 95, Step = 10, IsInteger = true },
            new() { Name = "MaxHoldBars", Min = 12, Max = 48, Step = 36, IsInteger = true },
        ],
        "RegimeConditional" =>
        [
            new() { Name = "TrendPeriod", Min = 50, Max = 100, Step = 50, IsInteger = true },
            new() { Name = "UpStrategy", Min = 3, Max = 7, Step = 4, IsInteger = true },   // MacdTrend | Supertrend
            new() { Name = "DownStrategy", Min = 0, Max = 0, Step = 1, IsInteger = true }, // stand aside
            new() { Name = "FlatStrategy", Min = 2, Max = 8, Step = 6, IsInteger = true }, // RsiOversold | Stochastic
        ],
        _ => [],
    };
}
