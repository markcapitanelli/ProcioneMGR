using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;

namespace ProcioneMGR.Tests;

/// <summary>Test dell'analisi ciclica (Activity Factor, bias orario/settimanale, stagionalita' - cap. 5).</summary>
public class CyclicalAnalyzerTests
{
    private static OhlcvData Bar(DateTime ts, decimal open, decimal close, decimal volume = 100m) => new()
    {
        Symbol = "TEST",
        Timeframe = "1h",
        TimestampUtc = ts,
        Open = open,
        High = Math.Max(open, close) + 1m,
        Low = Math.Min(open, close) - 1m,
        Close = close,
        Volume = volume,
    };

    [Fact]
    public void ActivityFactor_AveragesPerHour_AndNormalizes()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>
        {
            Bar(t0.AddHours(10), 100m, 101m, volume: 1000m),
            Bar(t0.AddDays(1).AddHours(10), 100m, 101m, volume: 3000m), // ora 10: media 2000
            Bar(t0.AddHours(3), 100m, 101m, volume: 500m),              // ora 3: media 500
        };

        var activity = new CyclicalAnalyzer().ActivityFactor(candles);

        Assert.Equal(24, activity.Count);
        Assert.Equal(2000m, activity[10].AverageVolume);
        Assert.Equal(2, activity[10].Samples);
        Assert.Equal(1m, activity[10].NormalizedMax);      // ora piu' attiva
        Assert.Equal(0.25m, activity[3].NormalizedMax);    // 500 / 2000
        Assert.Equal(0m, activity[0].AverageVolume);
    }

    [Fact]
    public void HourlyPriceBias_ComputesAverageBody_AndConcordance()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Ora 9: tre body +2, +2, -1 -> media +1, concordanza 2/3 = 66.67%.
        // Ora 15: due body -3, -1 -> media -2, concordanza 100% (negativi).
        var candles = new List<OhlcvData>
        {
            Bar(t0.AddHours(9), 100m, 102m),
            Bar(t0.AddDays(1).AddHours(9), 100m, 102m),
            Bar(t0.AddDays(2).AddHours(9), 100m, 99m),
            Bar(t0.AddHours(15), 100m, 97m),
            Bar(t0.AddDays(1).AddHours(15), 100m, 99m),
        };

        var bias = new CyclicalAnalyzer().HourlyPriceBias(candles);

        Assert.Equal(1m, bias[9].AverageBody);
        Assert.InRange(bias[9].ConcordantPercent, 66.6m, 66.7m);
        Assert.Equal(-2m, bias[15].AverageBody);
        Assert.Equal(100m, bias[15].ConcordantPercent);

        // normalizeMaxMin: massimo positivo -> +1, minimo negativo -> -1.
        Assert.Equal(1m, bias[9].Normalized);
        Assert.Equal(-1m, bias[15].Normalized);
    }

    [Fact]
    public void CombineHourlyBias_WeightsLongPeriodMore()
    {
        var analyzer = new CyclicalAnalyzer();
        var t0 = new DateTime(2024, 1, 1, 9, 0, 0, DateTimeKind.Utc);

        // Periodo "lungo": ora 9 sempre positiva (100%). Periodo "breve": mai (0%).
        var longPeriod = analyzer.HourlyPriceBias([Bar(t0, 100m, 101m), Bar(t0.AddDays(1), 100m, 102m)]);
        var shortPeriod = analyzer.HourlyPriceBias([Bar(t0.AddDays(2), 100m, 99m)]);

        var combo = analyzer.CombineHourlyBias([longPeriod, shortPeriod], [3m, 1m]);

        // (100*3 + 100*1)/4: attenzione, il breve ha media negativa -> concordanza sui negativi = 100.
        // Qui entrambe le concordanze sono 100 -> anche il combo e' 100.
        Assert.Equal(100m, combo[9].WeightedConcordantPercent);

        // Con un breve al 50% (un positivo, un negativo con media 0 -> concordanza sui positivi 50%).
        var mixed = analyzer.HourlyPriceBias([Bar(t0.AddDays(3), 100m, 101m), Bar(t0.AddDays(4), 100m, 99m)]);
        var combo2 = analyzer.CombineHourlyBias([longPeriod, mixed], [3m, 1m]);
        Assert.Equal(87.5m, combo2[9].WeightedConcordantPercent); // (100*3 + 50*1)/4
    }

    [Fact]
    public void DayOfWeekBias_SeparatesIntradayAndOvernight()
    {
        // Lunedi' 2024-01-01, martedi' 2024-01-02.
        var monday = Bar(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m, 102m); // intraday +2%
        var tuesday = Bar(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 104m, 103m); // intraday ~-0.96%, overnight (104-102)/102 = +1.96%

        var bias = new CyclicalAnalyzer().DayOfWeekBias([monday, tuesday]);

        var mon = bias.Single(b => b.Day == DayOfWeek.Monday);
        var tue = bias.Single(b => b.Day == DayOfWeek.Tuesday);

        Assert.Equal(2m, mon.IntradayAvgPercent);
        Assert.Equal(100m, mon.IntradayConcordantPercent);
        Assert.True(tue.IntradayAvgPercent < 0m);
        Assert.InRange(tue.OvernightAvgPercent, 1.9m, 2m);
    }

    [Fact]
    public void Seasonality_CumulativeCurve_IsRunningSumOfAverages()
    {
        // Due anni: il 2 gennaio +1% e +3% (media +2%), il 3 gennaio -1% entrambi.
        var candles = new List<OhlcvData>
        {
            Bar(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m, 100m),
            Bar(new DateTime(2023, 1, 2, 0, 0, 0, DateTimeKind.Utc), 100m, 101m),   // +1%
            Bar(new DateTime(2023, 1, 3, 0, 0, 0, DateTimeKind.Utc), 101m, 99.99m), // -1%
            Bar(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100m, 100m),
            Bar(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 100m, 103m),   // +3%
            Bar(new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), 103m, 101.97m),// -1%
        };
        // NB: la variazione close-su-close usa la close del giorno precedente della stessa serie.

        var seasonality = new CyclicalAnalyzer().Seasonality(candles);

        var jan2 = seasonality.Single(p => p.DayOfYear == 2);
        Assert.Equal(2, jan2.Samples);
        Assert.InRange(jan2.AvgChangePercent, 1.9m, 2.1m);

        var jan3 = seasonality.Single(p => p.DayOfYear == 3);
        Assert.InRange(jan3.AvgChangePercent, -1.1m, -0.9m);
        // Cumulata al giorno 3 ~= +2 - 1 = +1.
        Assert.InRange(jan3.CumulativePercent, 0.9m, 1.1m);
    }

    [Fact]
    public void TestSeasonalWindow_CountsYearsAndDirection()
    {
        // Finestra 1-10 gennaio, direzione long: 2023 positivo, 2024 negativo.
        var candles = new List<OhlcvData>
        {
            Bar(new DateTime(2023, 1, 2, 0, 0, 0, DateTimeKind.Utc), 100m, 100m),
            Bar(new DateTime(2023, 1, 5, 0, 0, 0, DateTimeKind.Utc), 100m, 105m),   // +5%
            Bar(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 105m, 105m),
            Bar(new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc), 105m, 102.9m), // -2%
        };

        var result = new CyclicalAnalyzer().TestSeasonalWindow(candles, 1, 1, 1, 10, isLong: true);

        Assert.Equal(2, result.YearsTested);
        Assert.Equal(50m, result.SuccessPercent);
        Assert.True(result.Years.Single(y => y.Year == 2023).IsSuccess);
        Assert.False(result.Years.Single(y => y.Year == 2024).IsSuccess);
    }
}
