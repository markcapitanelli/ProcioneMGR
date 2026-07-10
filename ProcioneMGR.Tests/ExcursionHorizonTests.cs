using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// R1.5 — Auto SL/TP da MAE/MFE sull'ORIZZONTE di detenzione, condizionato per regime di volatilità.
/// Le escursioni a barra singola sottostimano il rischio di stop; qui ogni barra è un ingresso tenuto
/// per H barre e MAE/MFE si accumulano. Verifica: i tre regimi sono popolati; il regime ad alta
/// volatilità produce uno stop più largo di quello a bassa volatilità (il cuore del condizionamento);
/// l'orizzonte più lungo non riduce l'escursione; il bracket adattivo ripiega sul complessivo quando
/// il regime corrente è troppo sparso; input degenere ⇒ zeri; horizon non valido ⇒ eccezione.
/// </summary>
public class ExcursionHorizonTests
{
    private readonly ExcursionAnalyzer _analyzer = new();

    /// <summary>
    /// Costruisce barre con trend lieve verso l'alto e ampiezza intrabar = rangeFrac (± piccolo rumore):
    /// rangeFrac piccolo = regime calmo (ATR% basso, MAE contenuta), grande = regime agitato.
    /// </summary>
    private static void AppendBars(List<OhlcvData> into, int count, decimal drift, decimal rangeFrac, int seed, ref decimal price)
    {
        var rnd = new Random(seed);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(4 * into.Count);
        for (var k = 0; k < count; k++)
        {
            var open = price;
            var close = open * (1m + drift);
            var noise = 1m + (decimal)(rnd.NextDouble() - 0.5) * 0.1m; // ±5% sulla semi-ampiezza
            var half = rangeFrac / 2m * noise;
            var hi = Math.Max(open, close) * (1m + half);
            var lo = Math.Min(open, close) * (1m - half);
            into.Add(new OhlcvData
            {
                Symbol = "X/USDT", Timeframe = "4h", TimestampUtc = t0.AddHours(4 * k),
                Open = open, Close = close, High = hi, Low = lo, Volume = 1m,
            });
            price = close;
        }
    }

    /// <summary>400 barre calme (ampiezza ~0.4%) poi 400 agitate (~6%), entrambe in trend rialzista.</summary>
    private static List<OhlcvData> CalmThenVolatile()
    {
        var list = new List<OhlcvData>(800);
        var price = 100m;
        AppendBars(list, 400, drift: 0.0005m, rangeFrac: 0.004m, seed: 1, ref price);
        AppendBars(list, 400, drift: 0.0005m, rangeFrac: 0.06m, seed: 2, ref price);
        return list;
    }

    [Fact]
    public void HorizonBracket_PopulatesAllThreeRegimeKeys()
    {
        var b = _analyzer.SuggestHorizonBracket(CalmThenVolatile(), OrderSide.Buy, horizon: 10);

        Assert.True(b.ByRegime.ContainsKey(VolatilityRegime.Low));
        Assert.True(b.ByRegime.ContainsKey(VolatilityRegime.Normal));
        Assert.True(b.ByRegime.ContainsKey(VolatilityRegime.High));
        Assert.True(b.Overall.Samples > 0);
    }

    [Fact]
    public void HighVolRegime_HasWiderStop_ThanLowVol()
    {
        var b = _analyzer.SuggestHorizonBracket(CalmThenVolatile(), OrderSide.Buy, horizon: 10);

        var low = b.ByRegime[VolatilityRegime.Low];
        var high = b.ByRegime[VolatilityRegime.High];
        Assert.True(low.Samples > 0 && high.Samples > 0, $"low={low.Samples}, high={high.Samples}");
        Assert.True(high.StopPercentile > low.StopPercentile,
            $"lo stop del regime agitato deve essere più largo: high={high.StopPercentile:F3}% > low={low.StopPercentile:F3}%");
    }

    /// <summary>40 blocchi da 10 barre: 5 di discesa (−1%/barra) + 5 di risalita (+1.3%/barra). Un dip
    /// PLURI-BARRA che il singolo-barra non vede ma che la MAE su orizzonte cattura.</summary>
    private static List<OhlcvData> RepeatingVDips(int blocks)
    {
        var list = new List<OhlcvData>();
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var price = 100m;
        for (var blk = 0; blk < blocks; blk++)
        {
            for (var s = 0; s < 10; s++)
            {
                var open = price;
                var close = s < 5 ? open * 0.99m : open * 1.013m;
                list.Add(new OhlcvData
                {
                    Symbol = "V/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(list.Count),
                    Open = open, Close = close, High = Math.Max(open, close), Low = Math.Min(open, close), Volume = 1m,
                });
                price = close;
            }
        }
        return list;
    }

    [Fact]
    public void MultiBarHorizon_CapturesMoreRisk_ThanSingleBar()
    {
        var candles = RepeatingVDips(40);
        var h10 = _analyzer.SuggestHorizonBracket(candles, OrderSide.Buy, horizon: 10).Overall;
        var h1 = _analyzer.SuggestHorizonBracket(candles, OrderSide.Buy, horizon: 1).Overall;

        Assert.True(h10.Samples > 0 && h1.Samples > 0);
        // Il dip pluri-barra (~5%) è invisibile al singolo-barra (~1%): è la ragione di R1.5.
        Assert.True(h10.StopPercentile > h1.StopPercentile * 1.5m,
            $"la MAE su orizzonte deve superare il singolo-barra: H10={h10.StopPercentile:F3}% vs H1={h1.StopPercentile:F3}%");
    }

    [Fact]
    public void AdaptiveBracket_FallsBackToOverall_WhenRegimeSparse()
    {
        var candles = CalmThenVolatile();
        var full = _analyzer.SuggestHorizonBracket(candles, OrderSide.Buy, horizon: 10);

        // Soglia di campioni irraggiungibile ⇒ usa il complessivo.
        var adaptive = _analyzer.SuggestAdaptiveBracket(candles, OrderSide.Buy, horizon: 10, minRegimeSamples: 1_000_000);

        Assert.Equal(full.Overall.StopPercentile, adaptive.StopLossPercent);
        Assert.Equal(full.Overall.TakeProfitPercentile, adaptive.TakeProfitPercent);
    }

    [Fact]
    public void ShortSide_ProducesPositiveBracket_OnDowntrend()
    {
        // Trend ribassista: gli short sono i vincitori ⇒ MAE/MFE definite e positive.
        var list = new List<OhlcvData>(300);
        var price = 100m;
        AppendBars(list, 300, drift: -0.0008m, rangeFrac: 0.02m, seed: 7, ref price);

        var adaptive = _analyzer.SuggestAdaptiveBracket(list, OrderSide.Sell, horizon: 8);
        Assert.True(adaptive.StopLossPercent > 0m && adaptive.TakeProfitPercent > 0m,
            $"SL={adaptive.StopLossPercent}, TP={adaptive.TakeProfitPercent}");
    }

    [Fact]
    public void DegenerateInput_ReturnsZeros()
    {
        var few = new List<OhlcvData>();
        var price = 100m;
        AppendBars(few, 10, drift: 0.001m, rangeFrac: 0.01m, seed: 3, ref price); // < atrPeriod+horizon

        var b = _analyzer.SuggestHorizonBracket(few, OrderSide.Buy, horizon: 10);
        Assert.Equal(0, b.Overall.Samples);

        var adaptive = _analyzer.SuggestAdaptiveBracket(few, OrderSide.Buy, horizon: 10);
        Assert.Equal(0m, adaptive.StopLossPercent);
        Assert.Equal(0m, adaptive.TakeProfitPercent);
    }

    [Fact]
    public void InvalidHorizon_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _analyzer.SuggestHorizonBracket(CalmThenVolatile(), OrderSide.Buy, horizon: 0));
    }
}
