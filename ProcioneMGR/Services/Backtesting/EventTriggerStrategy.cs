using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// EVENT-TRIGGERED strategy: enters on a DISCRETE market event (not a continuous price
/// condition) and exits after a maximum holding time — the time-bound trade structure typical
/// of event-driven intraday systems, which NO other strategy in the platform has.
///
/// Event types (0-100 percentile scale where applicable, from <see cref="SignalCatalog"/>):
///   0 VolSpike     — realized volatility jumps above the <c>Threshold</c> percentile
///                    (crossing, not level: the bar BEFORE was below)
///   1 VolCrush     — volatility drops below (100 - Threshold) percentile (crossing)
///   2 RegimeFlipUp — the causal Supertrend direction flips down→up
///   3 RegimeFlipDown — flips up→down
///   4 PriceShockDown — a single-bar return in the bottom (100 - Threshold) percentile
///   5 PriceShockUp   — a single-bar return above the Threshold percentile
///
/// Exit: unconditionally after <c>MaxHoldBars</c> bars from entry (Close). The engine's
/// SL/TP/trailing overlays remain available on top (stop variants of the robustness probe).
///
/// NOTE (declared deviation from the original spec): news/sentiment-based triggers are NOT
/// implemented — the alt-data history starts 2026-07-01 (a few days), so any walk-forward on
/// the 2023-2026 selection ranges would see zero events and could never validate them. The
/// market-derived events above are computable and testable over the whole history; the news
/// variant becomes meaningful only after months of accumulated alt-data.
///
/// STATE: tracks its own open-trade bar index (like DonchianBreakout tracks _side); the
/// backtest engine always honors signals so the mirror stays in sync.
/// </summary>
public sealed class EventTriggerStrategy : IStrategy
{
    public string Name => "EventTrigger";
    public string DisplayName => "Event Trigger (time-bound)";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("EventType", "Evento (0=volSpike,1=volCrush,2=flipUp,3=flipDown,4=shockDown,5=shockUp)", 0m, 0m, 5m),
        new StrategyParameterDefinition("Direction", "Direzione (0=long, 1=short)", 0m, 0m, 1m),
        new StrategyParameterDefinition("Threshold", "Soglia percentile evento (50-99)", 90m, 50m, 99m),
        new StrategyParameterDefinition("MaxHoldBars", "Barre massime di detenzione", 24m, 1m, 500m),
    ];

    private decimal?[] _volPct = [];
    private decimal?[] _retPct = [];
    private decimal?[] _trendDir = [];
    private int _eventType;
    private bool _short;
    private decimal _threshold = 90m;
    private int _maxHold = 24;
    private int _openIndex = -1; // -1 = flat (mirrors the engine position)

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        _eventType = (int)parameters.GetOrDefault("EventType", 0m);
        _short = parameters.GetOrDefault("Direction", 0m) >= 0.5m;
        _threshold = parameters.GetOrDefault("Threshold", 90m);
        _maxHold = (int)parameters.GetOrDefault("MaxHoldBars", 24m);
        if (_eventType is < 0 or > 5 || _maxHold < 1 || _threshold is < 50m or > 99m)
        {
            throw new ArgumentException("Parametri EventTrigger non validi: EventType in [0,5], MaxHoldBars >= 1, Threshold in [50,99].");
        }

        var n = candles.Count;

        // Realized volatility: rolling stddev(20) of 1-bar returns, then causal percentile.
        var returns = new decimal?[n];
        for (var i = 1; i < n; i++)
        {
            if (candles[i - 1].Close > 0m)
            {
                returns[i] = (candles[i].Close - candles[i - 1].Close) / candles[i - 1].Close;
            }
        }
        var vol = RollingStd(returns, 20);
        _volPct = SignalCatalog.CausalPercentile(vol, SignalCatalog.PercentileWindow, ct);

        // Single-bar return percentile (for shock events).
        _retPct = SignalCatalog.CausalPercentile(returns, SignalCatalog.PercentileWindow, ct);

        // Trend direction for the flip events (reuses the catalog's Supertrend direction).
        var matrix = await SignalCatalog.GetMatrixAsync(candles, indicators, ct);
        _trendDir = matrix[3];

        _openIndex = -1;
    }

    private static decimal?[] RollingStd(decimal?[] values, int window)
    {
        var n = values.Length;
        var result = new decimal?[n];
        var buffer = new Queue<decimal>(window + 1);
        decimal sum = 0m, sumSq = 0m;
        for (var i = 0; i < n; i++)
        {
            if (values[i] is not decimal v)
            {
                continue;
            }
            buffer.Enqueue(v);
            sum += v;
            sumSq += v * v;
            if (buffer.Count > window)
            {
                var dropped = buffer.Dequeue();
                sum -= dropped;
                sumSq -= dropped * dropped;
            }
            if (buffer.Count == window)
            {
                var mean = sum / window;
                var variance = sumSq / window - mean * mean;
                result[i] = variance <= 0m ? 0m : Indicators.TechnicalIndicatorsService.Sqrt(variance);
            }
        }
        return result;
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        // Time-bound exit has absolute priority.
        if (_openIndex >= 0)
        {
            if (index - _openIndex >= _maxHold)
            {
                _openIndex = -1;
                return Signal.Close;
            }
            return Signal.Hold; // one trade at a time, wait for expiry
        }

        if (index < 1 || !EventFired(index))
        {
            return Signal.Hold;
        }

        _openIndex = index;
        return _short ? Signal.Short : Signal.Long;
    }

    private bool EventFired(int index) => _eventType switch
    {
        // Vol events are CROSSINGS (prev below/above, now above/below): discrete, not a state.
        0 => _volPct[index] is decimal v && _volPct[index - 1] is decimal p && v > _threshold && p <= _threshold,
        1 => _volPct[index] is decimal v && _volPct[index - 1] is decimal p && v < 100m - _threshold && p >= 100m - _threshold,
        2 => _trendDir[index] is decimal t && _trendDir[index - 1] is decimal q && t > 50m && q < 50m,
        3 => _trendDir[index] is decimal t && _trendDir[index - 1] is decimal q && t < 50m && q > 50m,
        4 => _retPct[index] is decimal r && r < 100m - _threshold,
        5 => _retPct[index] is decimal r && r > _threshold,
        _ => false,
    };
}
