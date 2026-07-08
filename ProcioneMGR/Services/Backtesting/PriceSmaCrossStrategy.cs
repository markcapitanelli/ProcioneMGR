using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// La strategia "preferita" di McAllen (Charting and Technical Analysis, cap. 16-17):
/// prezzo contro media mobile semplice di lungo periodo (classico 200 DMA).
///  - Long quando la chiusura attraversa la SMA dal basso verso l'alto;
///  - Close (o Short, se abilitato) quando la viola dall'alto verso il basso.
/// La SMA agisce storicamente da supporto/resistenza: sopra la media si sta nel mercato,
/// sotto si sta in cash. Combinare con StopLossPercent/TrailingStopPercent della
/// <see cref="BacktestConfiguration"/> per replicare il sistema completo del libro
/// (200 DMA + stop loss 6-8% + trailing stop).
/// </summary>
public sealed class PriceSmaCrossStrategy : IStrategy
{
    public string Name => "PriceSmaCross";
    public string DisplayName => "Price/SMA Cross (200 DMA)";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("Period", "Periodo SMA", 200m, 2m, 1000m),
        new StrategyParameterDefinition("AllowShort", "Short sotto la SMA (0=no, 1=si)", 0m, 0m, 1m),
    ];

    private decimal?[] _sma = [];
    private decimal[] _closes = [];
    private bool _allowShort;

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        var period = (int)parameters.GetOrDefault("Period", 200m);
        _allowShort = parameters.GetOrDefault("AllowShort", 0m) >= 1m;
        if (period < 2)
        {
            throw new ArgumentException("Parametro PriceSmaCross non valido: Period >= 2.");
        }

        _closes = closes as decimal[] ?? [.. closes];
        var sma = await indicators.CalculateSmaAsync([.. closes], period, ct);
        _sma = [.. sma];
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        if (index < 1 || !_sma[index].HasValue || !_sma[index - 1].HasValue)
        {
            return Signal.Hold;
        }

        var above = currentPrice > _sma[index]!.Value;
        var wasAbove = _closes[index - 1] > _sma[index - 1]!.Value;

        if (above && !wasAbove)
        {
            return Signal.Long;
        }
        if (!above && wasAbove)
        {
            return _allowShort ? Signal.Short : Signal.Close;
        }
        return Signal.Hold;
    }
}
