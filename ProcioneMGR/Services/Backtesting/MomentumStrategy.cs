using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// Momentum: Long quando il momentum su <c>LookbackPeriod</c> supera +Threshold,
/// Short quando scende sotto -Threshold, Close quando il momentum rientra vicino a zero
/// (|momentum| &lt; Threshold/2).
///   momentum = (price - price[index - lookback]) / price[index - lookback]
/// </summary>
public sealed class MomentumStrategy : IStrategy
{
    public string Name => "Momentum";
    public string DisplayName => "Momentum";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("LookbackPeriod", "Lookback", 20m, 1m, 1000m),
        new StrategyParameterDefinition("Threshold", "Soglia (frazione)", 0.05m, 0.001m, 1m),
    ];

    private decimal[] _closes = [];
    private int _lookback = 20;
    private decimal _threshold = 0.05m;

    public Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        _lookback = (int)parameters.GetOrDefault("LookbackPeriod", 20m);
        _threshold = parameters.GetOrDefault("Threshold", 0.05m);
        if (_lookback < 1 || _threshold <= 0m)
        {
            throw new ArgumentException("Parametri Momentum non validi: Lookback >= 1 e Threshold > 0.");
        }
        _closes = closes as decimal[] ?? [.. closes];
        return Task.CompletedTask;
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        if (index < _lookback)
        {
            return Signal.Hold;
        }

        var past = _closes[index - _lookback];
        if (past <= 0m)
        {
            return Signal.Hold;
        }

        var momentum = (currentPrice - past) / past;

        if (momentum > _threshold)
        {
            return Signal.Long;
        }
        if (momentum < -_threshold)
        {
            return Signal.Short;
        }
        if (Math.Abs(momentum) < _threshold / 2m)
        {
            return Signal.Close;
        }
        return Signal.Hold;
    }
}
