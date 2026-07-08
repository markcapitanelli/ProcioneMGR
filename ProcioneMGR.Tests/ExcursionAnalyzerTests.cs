using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;

namespace ProcioneMGR.Tests;

/// <summary>Test dell'analisi delle escursioni e dell'effetto memoria (Trombetta cap. 4).</summary>
public class ExcursionAnalyzerTests
{
    private static OhlcvData Bar(decimal open, decimal high, decimal low, decimal close, int day = 1) => new()
    {
        Symbol = "TEST",
        Timeframe = "1d",
        TimestampUtc = new DateTime(2024, 1, day, 0, 0, 0, DateTimeKind.Utc),
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = 1m,
    };

    [Fact]
    public void SuggestStopLoss_SeparatesPositiveAndNegativeBars()
    {
        // 3 barre positive con ricorrezioni open->low di 1%, 2%, 3% dell'open (open=100)
        // e 1 barra negativa con escursione open->high del 4%.
        var candles = new List<OhlcvData>
        {
            Bar(100m, 106m, 99m, 105m, 1),   // positiva, OL = 1%
            Bar(100m, 106m, 98m, 105m, 2),   // positiva, OL = 2%
            Bar(100m, 106m, 97m, 105m, 3),   // positiva, OL = 3%
            Bar(100m, 104m, 90m, 95m, 4),    // negativa, HO = 4%
        };

        var suggestion = new ExcursionAnalyzer().SuggestStopLoss(candles);

        Assert.Equal(3, suggestion.PositiveBars);
        Assert.Equal(1, suggestion.NegativeBars);
        // Percentile 95 su [1,2,3] con interpolazione: 1 + 0.95*2*... -> tra 2 e 3, vicino a 3.
        Assert.InRange(suggestion.LongStopPercentile95, 2.5m, 3m);
        Assert.InRange(suggestion.LongStopPercentile99, suggestion.LongStopPercentile95, 3m);
        Assert.Equal(4m, suggestion.ShortStopPercentile95); // unico campione
    }

    [Fact]
    public void ComputeBarAnatomy_HandComputed()
    {
        var anatomy = new ExcursionAnalyzer().ComputeBarAnatomy([Bar(100m, 110m, 95m, 105m)]);

        var a = Assert.Single(anatomy);
        Assert.Equal(5m, a.Body);            // 105 - 100
        Assert.Equal(15m, a.Range);          // 110 - 95
        Assert.Equal(5m, a.OpenLow);         // 100 - 95
        Assert.Equal(10m, a.HighOpen);       // 110 - 100
        Assert.Equal(10m, a.CloseLow);       // 105 - 95
        Assert.Equal(5m, a.HighClose);       // 110 - 105
        Assert.True(a.IsWhite);
        // BodyRangePerc = 5/15 = 33.33%; ClosePerc = (105-95)/15 = 66.67%.
        Assert.InRange(a.BodyRangePercent, 33.3m, 33.4m);
        Assert.InRange(a.ClosePercent, 66.6m, 66.7m);
    }

    [Fact]
    public void LaggedAutocorrelation_AlternatingSeries_Lag1Negative_Lag2Positive()
    {
        // Variazioni alternate +1% / -1%: autocorrelazione lag1 fortemente negativa,
        // lag2 fortemente positiva.
        var values = new List<decimal> { 100m };
        for (var i = 0; i < 40; i++)
        {
            values.Add(values[^1] * (i % 2 == 0 ? 1.01m : 0.99m));
        }

        var corr = new ExcursionAnalyzer().LaggedAutocorrelation(values, maxLag: 3);

        Assert.Equal(3, corr.Count);
        Assert.True(corr[0].Correlation < -0.9m, $"lag1 atteso fortemente negativo, era {corr[0].Correlation}");
        Assert.True(corr[1].Correlation > 0.9m, $"lag2 atteso fortemente positivo, era {corr[1].Correlation}");
    }

    [Fact]
    public void ContinuationProbability_MonotoneUptrend_Is100Percent()
    {
        // Serie sempre crescente: dopo ogni variazione positiva ne segue un'altra positiva.
        var values = Enumerable.Range(1, 20).Select(i => (decimal)(100 + i)).ToList();
        var stats = new ExcursionAnalyzer().ContinuationProbability(values);

        Assert.True(stats.Setups > 0);
        Assert.Equal(100m, stats.SuccessPercent);
    }

    [Fact]
    public void ContinuationProbability_Threshold_FiltersSmallMoves()
    {
        // Variazioni tutte da +0.5%: con soglia 1% nessun setup.
        var values = new List<decimal> { 100m };
        for (var i = 0; i < 10; i++) values.Add(values[^1] * 1.005m);

        var stats = new ExcursionAnalyzer().ContinuationProbability(values, thresholdPercent: 1m);
        Assert.Equal(0, stats.Setups);
    }
}
