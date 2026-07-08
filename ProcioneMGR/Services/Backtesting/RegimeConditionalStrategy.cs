using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// REGIME-CONDITIONAL meta-strategy: classifies every bar into one of three CAUSAL market
/// buckets and delegates the signal to a different sub-strategy per bucket — "trend-follow in
/// trends, mean-revert sideways, stand aside in the regime you distrust", as a single
/// backtestable/optimizable strategy.
///
/// Regime proxy (deliberately DB-free — declared deviation from the original spec, which
/// suggested loading the saved K-means RegimeModel: strategies in this platform are
/// dependency-free by design (factory is new-based), and a DB-bound strategy could not run
/// inside OptimizationEngine sweeps or the live engine without new plumbing. The proxy is
/// computed causally from candles: SMA(TrendPeriod) relative slope over the last
/// TrendPeriod/4 bars → TrendUp / TrendDown / Sideways with a ±0.5% dead zone.)
///
/// Sub-strategy catalog (index → strategy, 0 = none/stand-aside). Only strategies whose
/// EvaluateSignal is a pure function of the bar index qualify (Donchian tracks internal state
/// tied to being called every bar, so it is excluded):
///   0 None, 1 EmaCross, 2 RsiOversold, 3 MacdTrend, 4 BollingerMeanReversion,
///   5 Momentum, 6 PriceSmaCross, 7 Supertrend, 8 Stochastic, 9 VwapReversion
///
/// On a bucket TRANSITION the strategy emits Close (flat during regime hand-over), then from
/// the next bar delegates to the new bucket's sub-strategy. Sub-strategies use their
/// ParameterDefinitions defaults (sweeping nested parameters would explode the grid; the
/// composer varies the ASSIGNMENT, the optimizer varies TrendPeriod).
/// </summary>
public sealed class RegimeConditionalStrategy : IStrategy
{
    public string Name => "RegimeConditional";
    public string DisplayName => "Regime Conditional (meta)";

    /// <summary>Sub-strategy names, index-aligned to the UpStrategy/DownStrategy/FlatStrategy parameter values.</summary>
    public static readonly IReadOnlyList<string> SubStrategyCatalog =
    [
        "None", "EmaCross", "RsiOversold", "MacdTrend", "BollingerMeanReversion",
        "Momentum", "PriceSmaCross", "Supertrend", "Stochastic", "VwapReversion",
    ];

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("TrendPeriod", "Periodo SMA per il regime", 50m, 10m, 400m),
        new StrategyParameterDefinition("UpStrategy", "Strategia in trend UP (0=nessuna)", 7m, 0m, 9m),
        new StrategyParameterDefinition("DownStrategy", "Strategia in trend DOWN (0=nessuna)", 0m, 0m, 9m),
        new StrategyParameterDefinition("FlatStrategy", "Strategia in laterale (0=nessuna)", 2m, 0m, 9m),
    ];

    private int[] _bucket = [];          // 0 = up, 1 = down, 2 = flat, -1 = warm-up
    private IStrategy?[] _sub = new IStrategy?[3];
    private int _lastBucket = -1;

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        var trendPeriod = (int)parameters.GetOrDefault("TrendPeriod", 50m);
        var upIdx = (int)parameters.GetOrDefault("UpStrategy", 7m);
        var downIdx = (int)parameters.GetOrDefault("DownStrategy", 0m);
        var flatIdx = (int)parameters.GetOrDefault("FlatStrategy", 2m);
        if (trendPeriod < 10 || !ValidIdx(upIdx) || !ValidIdx(downIdx) || !ValidIdx(flatIdx))
        {
            throw new ArgumentException("Parametri RegimeConditional non validi: TrendPeriod >= 10, indici strategia in [0,9].");
        }
        if (upIdx == 0 && downIdx == 0 && flatIdx == 0)
        {
            throw new ArgumentException("RegimeConditional senza alcuna sotto-strategia: almeno un regime deve tradare.");
        }

        // Causal regime buckets: relative slope of SMA(TrendPeriod) over TrendPeriod/4 bars.
        var closeList = closes as List<decimal> ?? [.. closes];
        var sma = await indicators.CalculateSmaAsync(closeList, trendPeriod, ct);
        var slopeLag = Math.Max(2, trendPeriod / 4);
        const decimal deadZone = 0.005m; // ±0.5%: below this the market is "sideways"
        var n = candles.Count;
        _bucket = new int[n];
        for (var i = 0; i < n; i++)
        {
            if (sma[i] is decimal cur && i >= slopeLag && sma[i - slopeLag] is decimal prev && prev > 0m)
            {
                var slope = (cur - prev) / prev;
                _bucket[i] = slope > deadZone ? 0 : slope < -deadZone ? 1 : 2;
            }
            else
            {
                _bucket[i] = -1;
            }
        }

        // Instantiate + initialize the sub-strategies on the FULL series (index-aligned).
        var factory = new StrategyFactory();
        _sub = new IStrategy?[3];
        foreach (var (bucket, idx) in new[] { (0, upIdx), (1, downIdx), (2, flatIdx) })
        {
            if (idx == 0)
            {
                continue;
            }
            var sub = factory.Create(SubStrategyCatalog[idx]);
            var defaults = sub.ParameterDefinitions.ToDictionary(d => d.Key, d => d.Default);
            await sub.InitializeAsync(closes, candles, defaults, indicators, ct);
            _sub[bucket] = sub;
        }
        _lastBucket = -1;
    }

    private static bool ValidIdx(int idx) => idx >= 0 && idx < SubStrategyCatalog.Count;

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        var bucket = _bucket[index];
        if (bucket < 0)
        {
            return Signal.Hold;
        }

        // Regime hand-over: go flat first, delegate from the next bar.
        if (bucket != _lastBucket)
        {
            _lastBucket = bucket;
            return Signal.Close;
        }

        var sub = _sub[bucket];
        return sub is null ? Signal.Hold : sub.EvaluateSignal(index, currentPrice, timestamp);
    }
}
