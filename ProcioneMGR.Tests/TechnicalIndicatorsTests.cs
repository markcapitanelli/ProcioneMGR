using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di correttezza degli indicatori su dati noti + cross-validation con
/// implementazioni di riferimento "ingenue" + invarianti strutturali.
/// </summary>
public class TechnicalIndicatorsTests
{
    private readonly TechnicalIndicatorsService _svc = new();

    /// <summary>
    /// Dataset classico di Wilder (StockCharts) per RSI(14). Il primo RSI calcolabile
    /// (indice 14) vale ~70.46 (verificato a mano: avgGain=3.34/14, avgLoss=1.40/14,
    /// RS=2.3857, RSI=100-100/(1+RS)).
    /// </summary>
    private static readonly decimal[] WilderCloses =
    [
        44.34m, 44.09m, 44.15m, 43.61m, 44.33m, 44.83m, 45.10m, 45.42m, 45.84m, 46.08m,
        45.89m, 46.03m, 45.61m, 46.28m, 46.28m, 46.00m, 46.03m, 46.41m, 46.22m, 45.64m,
        46.21m, 46.25m, 45.71m, 46.45m, 45.78m, 45.35m, 44.03m, 44.18m, 44.22m, 44.57m,
        43.42m, 42.66m, 43.13m
    ];

    [Fact]
    public async Task Rsi_KnownVector_FirstValue_Is_70_46()
    {
        var rsi = await _svc.CalculateRsiAsync([.. WilderCloses], period: 14);

        // Warm-up: i primi 14 (0..13) non sono calcolabili.
        for (var i = 0; i < 14; i++)
        {
            Assert.Null(rsi[i]);
        }

        Assert.NotNull(rsi[14]);
        // Valore di riferimento calcolato a mano con precisione piena:
        //   avgGain = 3.34/14, avgLoss = 1.40/14, RS = 2.3857142857,
        //   RSI = 100 - 100/(1+RS) = 70.46414...
        // Requisito spec: corretto a ±0.01.
        const decimal expectedRsi = 70.46414m;
        Assert.True(Math.Abs(rsi[14]!.Value - expectedRsi) < 0.01m,
            $"RSI[14]={rsi[14]!.Value} atteso {expectedRsi} ±0.01");
    }

    [Fact]
    public async Task Rsi_AllValues_StayWithin_0_100()
    {
        var rsi = await _svc.CalculateRsiAsync([.. WilderCloses]);
        foreach (var v in rsi.Where(v => v.HasValue))
        {
            Assert.InRange(v!.Value, 0m, 100m);
        }
    }

    [Fact]
    public async Task Rsi_MatchesNaiveReference()
    {
        var closes = WilderCloses.ToList();
        var fast = await _svc.CalculateRsiAsync(closes, 14);
        var reference = NaiveWilderRsi(closes, 14);

        for (var i = 0; i < closes.Count; i++)
        {
            Assert.Equal(reference[i].HasValue, fast[i].HasValue);
            if (fast[i].HasValue)
            {
                Assert.True(Math.Abs((double)(fast[i]!.Value - reference[i]!.Value)) < 1e-6);
            }
        }
    }

    [Fact]
    public async Task Ema_Seed_Equals_Sma_AndMatchesNaive()
    {
        var values = WilderCloses.ToList();
        const int period = 10;
        var ema = await _svc.CalculateEmaAsync(values, period);

        // Warm-up: 0..period-2 null; il seed e' la SMA all'indice period-1.
        for (var i = 0; i < period - 1; i++)
        {
            Assert.Null(ema[i]);
        }
        var sma = values.Take(period).Sum() / period;
        Assert.Equal((double)sma, (double)ema[period - 1]!.Value, precision: 8);

        // Confronto con EMA di riferimento ingenua.
        var reference = NaiveEma(values, period);
        for (var i = 0; i < values.Count; i++)
        {
            Assert.Equal(reference[i].HasValue, ema[i].HasValue);
            if (ema[i].HasValue)
            {
                Assert.True(Math.Abs((double)(ema[i]!.Value - reference[i]!.Value)) < 1e-9);
            }
        }
    }

    [Fact]
    public async Task Macd_Histogram_Equals_Macd_Minus_Signal()
    {
        var closes = WilderCloses.ToList();
        var (macd, signal, hist) = await _svc.CalculateMacdAsync(closes, 3, 6, 4); // periodi piccoli per il dataset corto

        for (var i = 0; i < closes.Count; i++)
        {
            if (macd[i].HasValue && signal[i].HasValue)
            {
                Assert.True(hist[i].HasValue);
                Assert.True(Math.Abs((double)(hist[i]!.Value - (macd[i]!.Value - signal[i]!.Value))) < 1e-12);
            }
        }
    }

    [Fact]
    public async Task Macd_Line_Equals_FastEma_Minus_SlowEma()
    {
        var closes = WilderCloses.ToList();
        const int fast = 3, slow = 6, sig = 4;

        var (macd, _, _) = await _svc.CalculateMacdAsync(closes, fast, slow, sig);
        var emaFast = await _svc.CalculateEmaAsync(closes, fast);
        var emaSlow = await _svc.CalculateEmaAsync(closes, slow);

        for (var i = 0; i < closes.Count; i++)
        {
            if (emaFast[i].HasValue && emaSlow[i].HasValue)
            {
                Assert.True(macd[i].HasValue);
                Assert.True(Math.Abs((double)(macd[i]!.Value - (emaFast[i]!.Value - emaSlow[i]!.Value))) < 1e-12);
            }
        }
    }

    [Fact]
    public async Task Bollinger_Ordering_And_Middle_Is_Sma()
    {
        var closes = WilderCloses.ToList();
        const int period = 10;
        var (upper, middle, lower) = await _svc.CalculateBollingerAsync(closes, period, 2.0m);

        for (var i = 0; i < closes.Count; i++)
        {
            if (middle[i].HasValue)
            {
                // upper >= middle >= lower
                Assert.True(upper[i]!.Value >= middle[i]!.Value);
                Assert.True(middle[i]!.Value >= lower[i]!.Value);

                // middle == SMA della finestra
                var windowSma = closes.Skip(i - period + 1).Take(period).Sum() / period;
                Assert.True(Math.Abs((double)(middle[i]!.Value - windowSma)) < 1e-9);

                // simmetria delle bande attorno alla media
                var up = upper[i]!.Value - middle[i]!.Value;
                var dn = middle[i]!.Value - lower[i]!.Value;
                Assert.True(Math.Abs((double)(up - dn)) < 1e-9);
            }
        }
    }

    [Fact]
    public async Task ShortSeries_ReturnsAllNull_ButSameLength()
    {
        var closes = new List<decimal> { 1m, 2m, 3m }; // < period
        var ema = await _svc.CalculateEmaAsync(closes, 20);
        var rsi = await _svc.CalculateRsiAsync(closes, 14);

        Assert.Equal(closes.Count, ema.Count);
        Assert.Equal(closes.Count, rsi.Count);
        Assert.All(ema, v => Assert.Null(v));
        Assert.All(rsi, v => Assert.Null(v));
    }

    // --- implementazioni di riferimento (chiare, non ottimizzate) ---

    private static List<decimal?> NaiveEma(List<decimal> values, int period)
    {
        var result = Enumerable.Repeat((decimal?)null, values.Count).ToList();
        if (values.Count < period) return result;
        var k = 2m / (period + 1);
        decimal ema = values.Take(period).Sum() / period;
        result[period - 1] = ema;
        for (var i = period; i < values.Count; i++)
        {
            ema = values[i] * k + ema * (1 - k); // forma equivalente alla ricorsiva
            result[i] = ema;
        }
        return result;
    }

    private static List<decimal?> NaiveWilderRsi(List<decimal> closes, int period)
    {
        var result = Enumerable.Repeat((decimal?)null, closes.Count).ToList();
        if (closes.Count <= period) return result;

        decimal avgGain = 0, avgLoss = 0;
        for (var i = 1; i <= period; i++)
        {
            var ch = closes[i] - closes[i - 1];
            if (ch >= 0) avgGain += ch; else avgLoss -= ch;
        }
        avgGain /= period;
        avgLoss /= period;

        for (var i = period; i < closes.Count; i++)
        {
            if (i > period)
            {
                var ch = closes[i] - closes[i - 1];
                var g = ch >= 0 ? ch : 0m;
                var l = ch < 0 ? -ch : 0m;
                avgGain = (avgGain * (period - 1) + g) / period;
                avgLoss = (avgLoss * (period - 1) + l) / period;
            }
            result[i] = avgLoss == 0 ? 100m : avgGain == 0 ? 0m : 100m - 100m / (1m + avgGain / avgLoss);
        }
        return result;
    }
}
