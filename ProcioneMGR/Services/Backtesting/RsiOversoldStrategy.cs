using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// RSI Oversold/Overbought: Long quando RSI &lt; soglia oversold, Short quando RSI &gt;
/// soglia overbought, altrimenti Hold. Il motore gestisce il flip della posizione
/// sul segnale opposto.
/// </summary>
public sealed class RsiOversoldStrategy : IStrategy
{
    public string Name => "RsiOversold";
    public string DisplayName => "RSI Oversold";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("Period", "Periodo RSI", 14m, 2m, 500m),
        new StrategyParameterDefinition("OversoldThreshold", "Soglia oversold", 30m, 1m, 99m),
        new StrategyParameterDefinition("OverboughtThreshold", "Soglia overbought", 70m, 1m, 99m),
    ];

    private decimal?[] _rsi = [];
    private decimal _oversold = 30m;
    private decimal _overbought = 70m;

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        var period = (int)parameters.GetOrDefault("Period", 14m);
        _oversold = parameters.GetOrDefault("OversoldThreshold", 30m);
        _overbought = parameters.GetOrDefault("OverboughtThreshold", 70m);
        if (period < 2 || _oversold >= _overbought)
        {
            throw new ArgumentException("Parametri RSI non validi: Period >= 2 e Oversold < Overbought.");
        }

        var list = closes as List<decimal> ?? [.. closes];
        _rsi = [.. await indicators.CalculateRsiAsync(list, period, ct)];
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        var r = _rsi[index];
        if (r is null)
        {
            return Signal.Hold;
        }
        if (r < _oversold)
        {
            return Signal.Long;
        }
        if (r > _overbought)
        {
            return Signal.Short;
        }
        return Signal.Hold;
    }
}
