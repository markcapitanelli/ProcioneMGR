using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// MACD Trend: Long quando il MACD incrocia SOPRA la Signal (istogramma da negativo a
/// positivo), Short quando incrocia SOTTO (istogramma da positivo a negativo).
/// </summary>
public sealed class MacdTrendStrategy : IStrategy
{
    public string Name => "MacdTrend";
    public string DisplayName => "MACD Trend";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("FastPeriod", "EMA veloce", 12m, 1m, 200m),
        new StrategyParameterDefinition("SlowPeriod", "EMA lenta", 26m, 2m, 400m),
        new StrategyParameterDefinition("SignalPeriod", "Signal", 9m, 1m, 100m),
    ];

    private decimal?[] _histogram = [];

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        var fast = (int)parameters.GetOrDefault("FastPeriod", 12m);
        var slow = (int)parameters.GetOrDefault("SlowPeriod", 26m);
        var signal = (int)parameters.GetOrDefault("SignalPeriod", 9m);
        if (fast < 1 || slow <= fast || signal < 1)
        {
            throw new ArgumentException("Parametri MACD non validi: serve 1 <= Fast < Slow e Signal >= 1.");
        }

        var list = closes as List<decimal> ?? [.. closes];
        var (_, _, histogram) = await indicators.CalculateMacdAsync(list, fast, slow, signal, ct);
        _histogram = [.. histogram];
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        if (index < 1)
        {
            return Signal.Hold;
        }

        var prev = _histogram[index - 1];
        var cur = _histogram[index];
        if (prev is null || cur is null)
        {
            return Signal.Hold;
        }

        // L'istogramma = MACD - Signal: il suo cambio di segno è il cross MACD/Signal.
        if (prev <= 0m && cur > 0m)
        {
            return Signal.Long;
        }
        if (prev >= 0m && cur < 0m)
        {
            return Signal.Short;
        }
        return Signal.Hold;
    }
}
