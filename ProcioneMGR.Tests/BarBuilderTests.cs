using ProcioneMGR.Data;
using ProcioneMGR.Services.Ingestion;

namespace ProcioneMGR.Tests;

/// <summary>Test delle barre a volume/controvalore costante (Jansen ML4T, cap. 2).</summary>
public class BarBuilderTests
{
    private static OhlcvData Bar(int hour, decimal open, decimal high, decimal low, decimal close, decimal volume) => new()
    {
        Symbol = "TEST",
        Timeframe = "1h",
        TimestampUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(hour),
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = volume,
    };

    [Fact]
    public void VolumeBars_AggregateUntilThreshold()
    {
        // Volumi 60, 50, 40, 120, 30 con soglia 100:
        //  barra 1 = candele 0+1 (110 >= 100), barra 2 = candele 2+3 (160 >= 100),
        //  la coda (30) resta incompleta e viene scartata.
        var candles = new List<OhlcvData>
        {
            Bar(0, 100m, 102m, 99m, 101m, 60m),
            Bar(1, 101m, 104m, 100m, 103m, 50m),
            Bar(2, 103m, 105m, 101m, 102m, 40m),
            Bar(3, 102m, 108m, 98m, 106m, 120m),
            Bar(4, 106m, 107m, 105m, 106m, 30m),
        };

        var bars = new BarBuilder().BuildVolumeBars(candles, volumePerBar: 100m);

        Assert.Equal(2, bars.Count);

        var b1 = bars[0];
        Assert.Equal(candles[0].TimestampUtc, b1.StartUtc);
        Assert.Equal(candles[1].TimestampUtc, b1.EndUtc);
        Assert.Equal(100m, b1.Open);
        Assert.Equal(104m, b1.High);   // max dei due high
        Assert.Equal(99m, b1.Low);     // min dei due low
        Assert.Equal(103m, b1.Close);  // close dell'ultima candela inclusa
        Assert.Equal(110m, b1.Volume);
        Assert.Equal(2, b1.SourceCandles);

        var b2 = bars[1];
        Assert.Equal(160m, b2.Volume);
        Assert.Equal(108m, b2.High);
        Assert.Equal(98m, b2.Low);
        Assert.Equal(106m, b2.Close);
    }

    [Fact]
    public void DollarBars_UseTypicalPriceTimesVolume()
    {
        // Prezzo tipico costante 100 -> controvalore = 100 * volume: con soglia 10000
        // servono 100 di volume per barra.
        var candles = Enumerable.Range(0, 6)
            .Select(i => Bar(i, 100m, 100m, 100m, 100m, 50m)) // tipico = 100, dv = 5000
            .ToList();

        var bars = new BarBuilder().BuildDollarBars(candles, dollarPerBar: 10_000m);

        Assert.Equal(3, bars.Count); // 6 candele da 5000 -> 3 barre da 2 candele
        Assert.All(bars, b => Assert.Equal(2, b.SourceCandles));
        Assert.All(bars, b => Assert.Equal(10_000m, b.DollarValue));
        Assert.All(bars, b => Assert.Equal(100m, b.Vwap)); // prezzo costante -> VWAP = prezzo
    }

    [Fact]
    public void SuggestThresholds_TargetBarCount()
    {
        var candles = Enumerable.Range(0, 10)
            .Select(i => Bar(i, 100m, 100m, 100m, 100m, 100m))
            .ToList(); // volume totale 1000, controvalore totale 100_000

        var builder = new BarBuilder();
        Assert.Equal(200m, builder.SuggestVolumeThreshold(candles, targetBarCount: 5));
        Assert.Equal(20_000m, builder.SuggestDollarThreshold(candles, targetBarCount: 5));

        // La soglia suggerita produce circa il numero di barre richiesto.
        var bars = builder.BuildVolumeBars(candles, builder.SuggestVolumeThreshold(candles, 5));
        Assert.Equal(5, bars.Count);
    }

    [Fact]
    public void ToOhlcv_ProducesSyntheticCandles()
    {
        var candles = new List<OhlcvData>
        {
            Bar(0, 100m, 102m, 99m, 101m, 60m),
            Bar(1, 101m, 104m, 100m, 103m, 50m),
        };
        var builder = new BarBuilder();
        var bars = builder.BuildVolumeBars(candles, 100m);
        var synthetic = builder.ToOhlcv(bars, "TEST", "vol100");

        var s = Assert.Single(synthetic);
        Assert.Equal("vol100", s.Timeframe);
        Assert.Equal(bars[0].EndUtc, s.TimestampUtc);
        Assert.Equal(bars[0].Close, s.Close);
    }

    [Fact]
    public void InvalidThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarBuilder().BuildVolumeBars([], 0m));
    }
}
