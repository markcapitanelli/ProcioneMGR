using System.Globalization;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Tests;

/// <summary>
/// Regressione: <see cref="OptimizationEngine.ComboKey"/> deve formattare i decimal in
/// InvariantCulture. Bug reale scoperto integrando MlStrategy in Optimization (soglie
/// Long/Short, non intere): sotto cultura it-IT (virgola come separatore decimale) una chiave
/// come "LongThreshold=0,001,ShortThreshold=0,001" spezza il parsing dell'heatmap (che separa i
/// parametri per virgola), mai emerso prima perché tutte le strategie a regole sweepano solo
/// parametri interi (FastPeriod, SlowPeriod, ...).
/// </summary>
public class OptimizationComboKeyTests
{
    [Fact]
    public void ComboKey_UnderItalianCulture_UsesInvariantDecimalSeparator()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");

            var combo = new Dictionary<string, decimal> { ["LongThreshold"] = 0.001m, ["ShortThreshold"] = 0.001m };
            var key = OptimizationEngine.ComboKey(combo);

            Assert.Equal("LongThreshold=0.001,ShortThreshold=0.001", key);
            Assert.DoesNotContain(',', key.Replace("LongThreshold=0.001,ShortThreshold=0.001", "")); // nessuna virgola "spuria" oltre al separatore fra parametri
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ComboKey_Parseable_RoundTripsEachParameter()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");

            var combo = new Dictionary<string, decimal>
            {
                ["LongThreshold"] = 0.0025m,
                ["ShortThreshold"] = 0.0075m,
                ["SavedModelId"] = 3m,
            };
            var key = OptimizationEngine.ComboKey(combo);

            // Esattamente lo split usato da RenderHeatmapAsync: se la formattazione non fosse
            // invariant-culture, questo esploderebbe con IndexOutOfRangeException.
            var parsed = key.Split(',').Select(p => p.Split('=')).ToDictionary(a => a[0], a => decimal.Parse(a[1], CultureInfo.InvariantCulture));

            Assert.Equal(3, parsed.Count);
            Assert.Equal(0.0025m, parsed["LongThreshold"]);
            Assert.Equal(0.0075m, parsed["ShortThreshold"]);
            Assert.Equal(3m, parsed["SavedModelId"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ComboKey_OrdersParametersByNameOrdinal_IndependentOfCulture()
    {
        var combo = new Dictionary<string, decimal> { ["ShortThreshold"] = 1m, ["LongThreshold"] = 2m };
        var key = OptimizationEngine.ComboKey(combo);
        Assert.StartsWith("LongThreshold=", key);
    }
}
