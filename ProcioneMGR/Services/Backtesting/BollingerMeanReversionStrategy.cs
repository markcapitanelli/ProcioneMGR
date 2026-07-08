using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// Bollinger Mean Reversion: Long quando il prezzo sfonda la banda inferiore (oversold),
/// Short quando sfonda la superiore (overbought), Close quando il prezzo rientra
/// attraversando la banda centrale (media).
/// </summary>
public sealed class BollingerMeanReversionStrategy : IStrategy
{
    public string Name => "BollingerMeanReversion";
    public string DisplayName => "Bollinger Mean Reversion";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("Period", "Periodo", 20m, 2m, 500m),
        new StrategyParameterDefinition("StdDevMultiplier", "Moltiplicatore σ", 2.0m, 0.5m, 5m),
    ];

    private decimal[] _closes = [];
    private decimal?[] _upper = [];
    private decimal?[] _middle = [];
    private decimal?[] _lower = [];

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        var period = (int)parameters.GetOrDefault("Period", 20m);
        var mult = parameters.GetOrDefault("StdDevMultiplier", 2.0m);
        if (period < 2 || mult <= 0m)
        {
            throw new ArgumentException("Parametri Bollinger non validi: Period >= 2 e Multiplier > 0.");
        }

        var list = closes as List<decimal> ?? [.. closes];
        _closes = [.. list];
        var (upper, middle, lower) = await indicators.CalculateBollingerAsync(list, period, mult, ct);
        _upper = [.. upper];
        _middle = [.. middle];
        _lower = [.. lower];
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        var upper = _upper[index];
        var middle = _middle[index];
        var lower = _lower[index];
        if (upper is null || middle is null || lower is null)
        {
            return Signal.Hold;
        }

        if (currentPrice < lower)
        {
            return Signal.Long;   // oversold -> mean reversion verso l'alto
        }
        if (currentPrice > upper)
        {
            return Signal.Short;  // overbought -> mean reversion verso il basso
        }

        // Chiudi quando il prezzo riattraversa la media (rientro alla media).
        if (index >= 1 && _middle[index - 1] is decimal prevMid)
        {
            var prevSide = _closes[index - 1] - prevMid;
            var curSide = currentPrice - middle.Value;
            if (prevSide * curSide < 0m)
            {
                return Signal.Close;
            }
        }

        return Signal.Hold;
    }
}
