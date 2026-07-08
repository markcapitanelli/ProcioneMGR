using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// Oscillatore stocastico (mean-reversion, un secondo oscillatore distinto dall'RSI, molto
/// usato intraday): %K = 100*(close - LLV) / (HHV - LLV) sui minimi/massimi a
/// <c>KPeriod</c>; %D = SMA(%K, <c>DPeriod</c>) (linea lenta, filtra il rumore). Long quando
/// %D scende sotto la soglia di ipervenduto, Short quando supera l'ipercomprato — stessa
/// struttura robusta di RsiOversold ma con un oscillatore che reagisce alla posizione della
/// close nel range, non alla forza relativa.
///
/// Riusa gli indicatori esistenti (Donchian per HHV/LLV, SMA per %D): nessun nuovo calcolo.
/// </summary>
public sealed class StochasticStrategy : IStrategy
{
    public string Name => "Stochastic";
    public string DisplayName => "Stochastic Oscillator";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("KPeriod", "Periodo %K", 14m, 2m, 100m),
        new StrategyParameterDefinition("DPeriod", "Smoothing %D", 3m, 1m, 50m),
        new StrategyParameterDefinition("OversoldThreshold", "Soglia ipervenduto", 20m, 1m, 49m),
        new StrategyParameterDefinition("OverboughtThreshold", "Soglia ipercomprato", 80m, 51m, 99m),
    ];

    private decimal?[] _d = [];
    private decimal _oversold = 20m;
    private decimal _overbought = 80m;

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        var kPeriod = (int)parameters.GetOrDefault("KPeriod", 14m);
        var dPeriod = (int)parameters.GetOrDefault("DPeriod", 3m);
        _oversold = parameters.GetOrDefault("OversoldThreshold", 20m);
        _overbought = parameters.GetOrDefault("OverboughtThreshold", 80m);
        if (kPeriod < 2 || dPeriod < 1 || _oversold >= _overbought)
        {
            throw new ArgumentException("Parametri Stochastic non validi: KPeriod >= 2, DPeriod >= 1, Oversold < Overbought.");
        }

        var n = candles.Count;
        var highs = new List<decimal>(n);
        var lows = new List<decimal>(n);
        var closeList = closes as List<decimal> ?? [.. closes];
        foreach (var c in candles) { highs.Add(c.High); lows.Add(c.Low); }

        var (hhv, llv) = await indicators.CalculateDonchianAsync(highs, lows, kPeriod, ct);

        // %K grezzo (nullable, allineato per indice).
        var k = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            if (hhv[i] is decimal hi && llv[i] is decimal lo && hi > lo)
            {
                k[i] = 100m * (closeList[i] - lo) / (hi - lo);
            }
        }

        // %D = SMA(%K, DPeriod) sulla sotto-serie densa non-null, poi ri-mappata (come il MACD signal).
        _d = new decimal?[n];
        var firstIdx = Array.FindIndex(k, v => v.HasValue);
        if (firstIdx >= 0)
        {
            var dense = new List<decimal>(n - firstIdx);
            for (var i = firstIdx; i < n; i++)
            {
                dense.Add(k[i] ?? dense[^1]); // i buchi interni (HHV==LLV) ereditano il precedente: %K resta continuo
            }
            var denseD = await indicators.CalculateSmaAsync(dense, dPeriod, ct);
            for (var j = 0; j < denseD.Count; j++)
            {
                _d[firstIdx + j] = denseD[j];
            }
        }
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        if (_d[index] is not decimal d)
        {
            return Signal.Hold;
        }
        if (d < _oversold)
        {
            return Signal.Long;
        }
        if (d > _overbought)
        {
            return Signal.Short;
        }
        return Signal.Hold;
    }
}
