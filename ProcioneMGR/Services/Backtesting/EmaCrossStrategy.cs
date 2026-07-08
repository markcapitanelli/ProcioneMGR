using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// EMA Cross: segnale Long quando l'EMA veloce incrocia SOPRA la lenta,
/// Short quando incrocia SOTTO. Il crossing usa la candela corrente e la precedente.
/// </summary>
public sealed class EmaCrossStrategy : IStrategy
{
    public string Name => "EmaCross";
    public string DisplayName => "EMA Cross";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("FastPeriod", "EMA veloce", 12m, 1m, 500m),
        new StrategyParameterDefinition("SlowPeriod", "EMA lenta", 26m, 2m, 1000m),
    ];

    private decimal?[] _fast = [];
    private decimal?[] _slow = [];

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        var fast = (int)parameters.GetOrDefault("FastPeriod", 12m);
        var slow = (int)parameters.GetOrDefault("SlowPeriod", 26m);
        if (fast < 1 || slow < 2 || fast >= slow)
        {
            throw new ArgumentException("Parametri EMA non validi: serve 1 <= FastPeriod < SlowPeriod.");
        }

        var list = closes as List<decimal> ?? [.. closes];
        _fast = [.. await indicators.CalculateEmaAsync(list, fast, ct)];
        _slow = [.. await indicators.CalculateEmaAsync(list, slow, ct)];
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        if (index < 1)
        {
            return Signal.Hold;
        }

        var pf = _fast[index - 1];
        var ps = _slow[index - 1];
        var cf = _fast[index];
        var cs = _slow[index];

        if (pf is null || ps is null || cf is null || cs is null)
        {
            return Signal.Hold;
        }

        if (pf <= ps && cf > cs)
        {
            return Signal.Long;   // cross verso l'alto
        }
        if (pf >= ps && cf < cs)
        {
            return Signal.Short;  // cross verso il basso
        }
        return Signal.Hold;
    }
}
