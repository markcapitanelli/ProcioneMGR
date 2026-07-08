using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;

namespace ProcioneMGR.Tests;

/// <summary>Test dell'analisi Gap/Lap (Trombetta cap. 4).</summary>
public class GapLapAnalyzerTests
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
        Volume = 1000m,
    };

    [Fact]
    public void GapUp_Refilled_Deep_Pos_ClassifiedCorrectly()
    {
        // Barra 1: range 90-110, close 105.
        // Barra 2: apre a 112 (> high 110 -> GapUp da 2), scende fino a 104
        //          (sotto high 110 -> refilled; sotto close 105 -> deep refilled),
        //          chiude a 115 (> open 112 -> Pos).
        var candles = new List<OhlcvData>
        {
            Bar(100m, 110m, 90m, 105m, 1),
            Bar(112m, 116m, 104m, 115m, 2),
        };

        var report = new GapLapAnalyzer().Analyze(candles, pointValue: 50m);

        Assert.Equal(1, report.TotalBars);
        Assert.Equal(1, report.GapUp.Count);
        Assert.Equal(100m, report.GapUp.PercentOfBars);
        Assert.Equal(2m, report.GapUp.EntityAvg);          // 112 - 110
        Assert.Equal(100m, report.GapUp.MoneyAvg);         // 2 * 50
        Assert.Equal(1, report.GapUp.RefilledCount);
        Assert.Equal(1, report.GapUp.DeepRefilledCount);
        Assert.Equal(1, report.GapUp.PositiveCount);
        Assert.Equal(0, report.GapUp.NegativeCount);
        Assert.Equal(0, report.GapDown.Count);
        Assert.Equal(0, report.LapUp.Count);
    }

    [Fact]
    public void GapDown_NotRefilled_Neg_ClassifiedCorrectly()
    {
        // Barra 2: apre a 85 (< low 90 -> GapDown di -5), high 88 non torna al low 90
        // (non refilled), chiude a 80 (< open -> Neg).
        var candles = new List<OhlcvData>
        {
            Bar(100m, 110m, 90m, 105m, 1),
            Bar(85m, 88m, 78m, 80m, 2),
        };

        var report = new GapLapAnalyzer().Analyze(candles);

        Assert.Equal(1, report.GapDown.Count);
        Assert.Equal(-5m, report.GapDown.EntityAvg);       // 85 - 90
        Assert.Equal(0, report.GapDown.RefilledCount);
        Assert.Equal(0, report.GapDown.DeepRefilledCount);
        Assert.Equal(1, report.GapDown.NegativeCount);
    }

    [Fact]
    public void LapUp_And_LapDown_UseCloseAsReference()
    {
        // Barra 1: 90-110, close 100.
        // Barra 2: apre a 104 (tra close 100 e high 110 -> LapUp di 4),
        //          low 99 <= close 100 -> refilled.
        // Barra 3: apre a 101 (tra low 99 e close 103 della barra 2 -> LapDown),
        //          high 102 non raggiunge la close precedente 103 -> non refilled.
        var candles = new List<OhlcvData>
        {
            Bar(95m, 110m, 90m, 100m, 1),
            Bar(104m, 108m, 99m, 103m, 2),
            Bar(101m, 102m, 99.5m, 100m, 3),
        };

        var report = new GapLapAnalyzer().Analyze(candles);

        Assert.Equal(1, report.LapUp.Count);
        Assert.Equal(4m, report.LapUp.EntityAvg);          // 104 - 100
        Assert.Equal(1, report.LapUp.RefilledCount);
        Assert.Null(report.LapUp.DeepRefilledCount);       // i lap non hanno il livello deep

        Assert.Equal(1, report.LapDown.Count);
        Assert.Equal(-2m, report.LapDown.EntityAvg);       // 101 - 103
        Assert.Equal(0, report.LapDown.RefilledCount);     // high 102 < close prec. 103
    }

    [Fact]
    public void ContinuousMarket_OpenEqualsPrevClose_NoEvents()
    {
        // Mercato continuo (crypto spot): open == close precedente -> nessun gap/lap.
        var candles = new List<OhlcvData>
        {
            Bar(100m, 105m, 95m, 102m, 1),
            Bar(102m, 106m, 100m, 104m, 2),
            Bar(104m, 108m, 103m, 107m, 3),
        };

        var report = new GapLapAnalyzer().Analyze(candles);
        Assert.Equal(0, report.GapUp.Count + report.GapDown.Count + report.LapUp.Count + report.LapDown.Count);
    }

    [Fact]
    public void EmptyOrSingleBar_NoThrow()
    {
        var analyzer = new GapLapAnalyzer();
        Assert.Equal(0, analyzer.Analyze([]).TotalBars);
        Assert.Equal(0, analyzer.Analyze([Bar(1m, 2m, 0.5m, 1.5m)]).TotalBars);
    }
}
