using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica il calcolo automatico del bracket SL+TP data-driven (percentili di escursione avversa/
/// favorevole) aggiunto a <see cref="ExcursionAnalyzer"/>. È la base del nuovo comportamento
/// "calcola/proponi/applica automaticamente stop loss e take profit".
/// </summary>
public class ExcursionBracketTests
{
    // 100 barre POSITIVE (close>open): escursione favorevole open->high = 1%..100%, nessuna avversa.
    private static List<OhlcvData> PositiveBarsWithUpExcursion()
    {
        var list = new List<OhlcvData>(100);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var k = 1; k <= 100; k++)
        {
            list.Add(new OhlcvData
            {
                Symbol = "X/USDT", Timeframe = "4h", TimestampUtc = t0.AddHours(4 * k),
                Open = 100m, Close = 100.5m,               // positiva
                High = 100m * (1m + k / 100m),             // up-excursion = k%
                Low = 100m,                                 // nessuna ricorrezione avversa
                Volume = 1m,
            });
        }
        return list;
    }

    [Fact]
    public void SuggestTakeProfit_LongPercentile_ReflectsFavorableExcursion()
    {
        var tp = new ExcursionAnalyzer().SuggestTakeProfit(PositiveBarsWithUpExcursion());

        Assert.Equal(100, tp.PositiveBars);
        // 95° percentile della distribuzione 1..100 ≈ 95 (tolleranza per l'interpolazione).
        Assert.InRange(tp.LongTakeProfitPercentile95, 92m, 98m);
        Assert.InRange(tp.LongTakeProfitPercentile99, 96m, 100m);
        Assert.True(tp.LongTakeProfitPercentile99 >= tp.LongTakeProfitPercentile95);
    }

    [Fact]
    public void SuggestBracket_Long_CombinesStopAndTargetPercentiles()
    {
        var analyzer = new ExcursionAnalyzer();
        var candles = PositiveBarsWithUpExcursion();

        var bracket = analyzer.SuggestBracket(candles, OrderSide.Buy);
        var sl = analyzer.SuggestStopLoss(candles);
        var tp = analyzer.SuggestTakeProfit(candles);

        // Il bracket combina esattamente i percentili 95° dei due lati.
        Assert.Equal(sl.LongStopPercentile95, bracket.StopLossPercent);
        Assert.Equal(tp.LongTakeProfitPercentile95, bracket.TakeProfitPercent);
        // Nessuna escursione avversa in queste barre → stop ~0; target ampio > 0.
        Assert.Equal(0m, bracket.StopLossPercent);
        Assert.True(bracket.TakeProfitPercent > 0m);
    }

    [Fact]
    public void SuggestBracket_Use99thPercentile_IsWiderThan95th()
    {
        var analyzer = new ExcursionAnalyzer();
        var candles = PositiveBarsWithUpExcursion();

        var b95 = analyzer.SuggestBracket(candles, OrderSide.Buy, use99thPercentile: false);
        var b99 = analyzer.SuggestBracket(candles, OrderSide.Buy, use99thPercentile: true);

        Assert.True(b99.TakeProfitPercent >= b95.TakeProfitPercent);
    }
}
