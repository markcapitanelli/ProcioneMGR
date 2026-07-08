using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Tests;

/// <summary>
/// Regressione per <see cref="DefaultParameterRanges"/>: i range di sweep di default devono
/// derivare dai bound Min/Max della definizione, non dal solo Default. In particolare un flag
/// con dominio 0..1 dev'essere enumerato per intero (0 E 1), altrimenti l'ottimizzatore non
/// varierebbe mai il flag (es. "AllowShort" -> lo short non veniva mai attivato).
/// </summary>
public sealed class DefaultParameterRangesTests
{
    /// <summary>Espande un <see cref="ParameterRange"/> nei valori effettivamente campionati.</summary>
    private static List<decimal> Expand(ParameterRange r)
    {
        var values = new List<decimal>();
        for (var v = r.Min; v <= r.Max + 1e-9m; v += r.Step)
        {
            values.Add(r.IsInteger ? decimal.Round(v) : v);
        }
        return values;
    }

    [Theory]
    [InlineData(0.0)] // flag OFF di default (es. PriceSmaCross.AllowShort)
    [InlineData(1.0)] // flag ON di default (es. Supertrend/Vwap.AllowShort)
    public void FlagParameter_EnumeratesBothZeroAndOne(double defaultValue)
    {
        var def = new StrategyParameterDefinition("AllowShort", "Consenti short (0/1)", (decimal)defaultValue, 0m, 1m);

        var range = DefaultParameterRanges.Build(def);

        Assert.True(range.IsInteger, "un dominio 0..1 va marcato come intero");
        Assert.Equal(0m, range.Min);
        Assert.Equal(1m, range.Max);
        Assert.Equal(1m, range.Step);

        var values = Expand(range);
        Assert.Contains(0m, values);
        Assert.Contains(1m, values);
    }

    [Fact]
    public void SmallCategoricalDomain_EnumeratesEveryInteger()
    {
        // Selettore evento 0..5 (EventTriggerStrategy): tutti e sei i valori vanno provati.
        var def = new StrategyParameterDefinition("EventType", "Evento", 0m, 0m, 5m);

        var values = Expand(DefaultParameterRanges.Build(def));

        Assert.Equal(new[] { 0m, 1m, 2m, 3m, 4m, 5m }, values);
    }

    [Fact]
    public void ContinuousParameter_StaysWithinBounds()
    {
        // Moltiplicatore ATR di Supertrend: continuo, non deve sforare il Max della definizione.
        var def = new StrategyParameterDefinition("Multiplier", "Moltiplicatore ATR", 3.0m, 0.5m, 10m);

        var range = DefaultParameterRanges.Build(def);

        Assert.False(range.IsInteger);
        Assert.True(range.Min >= 0.5m);
        Assert.True(range.Max <= 10m);
        Assert.True(range.Step > 0m);
    }
}
