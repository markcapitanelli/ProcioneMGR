using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;

namespace ProcioneMGR.Tests;

/// <summary>Test del riconoscimento dei pattern candlestick (McAllen cap. 4-6 e 14).</summary>
public class CandlestickPatternDetectorTests
{
    private static OhlcvData Bar(decimal open, decimal high, decimal low, decimal close, decimal volume = 100m) => new()
    {
        Symbol = "TEST",
        Timeframe = "1d",
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = volume,
    };

    /// <summary>Barre in trend: ognuna avanza (o declina) di "step" con corpo pieno.</summary>
    private static List<OhlcvData> Trend(decimal start, decimal step, int count)
    {
        var list = new List<OhlcvData>(count);
        var price = start;
        for (var i = 0; i < count; i++)
        {
            var open = price;
            var close = price + step;
            list.Add(Bar(open, Math.Max(open, close) + Math.Abs(step) * 0.1m,
                Math.Min(open, close) - Math.Abs(step) * 0.1m, close));
            price = close;
        }
        // Timestamp progressivi.
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < list.Count; i++) list[i].TimestampUtc = t0.AddDays(i);
        return list;
    }

    private static void Stamp(List<OhlcvData> candles)
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < candles.Count; i++) candles[i].TimestampUtc = t0.AddDays(i);
    }

    [Fact]
    public void Doji_AfterUptrend_IsBearishAlert()
    {
        var candles = Trend(100m, 1m, 6);            // +6% circa: trend rialzista
        candles.Add(Bar(106m, 107m, 105m, 106.05m)); // doji: corpo 0.05 su range 2
        Stamp(candles);

        var patterns = new CandlestickPatternDetector().Detect(candles);
        var doji = patterns.Single(p => p.Type == CandlePatternType.Doji && p.Index == candles.Count - 1);
        Assert.False(doji.IsBullish);
    }

    [Fact]
    public void HammerShape_ContextDecidesName()
    {
        // Forma a martello: corpo compatto in alto, lunga ombra inferiore (>= 2 corpi),
        // corpo > 10% del range (altrimenti sarebbe classificato doji).
        var hammerAfterDecline = Trend(100m, -1m, 6);
        hammerAfterDecline.Add(Bar(94m, 95m, 92m, 94.8m)); // corpo 0.8, ombra inferiore 2
        Stamp(hammerAfterDecline);
        var p1 = new CandlestickPatternDetector().Detect(hammerAfterDecline);
        Assert.Contains(p1, p => p.Type == CandlePatternType.Hammer && p.IsBullish == true);

        var hangingAfterAdvance = Trend(100m, 1m, 6);
        hangingAfterAdvance.Add(Bar(106m, 107m, 104m, 106.8m));
        Stamp(hangingAfterAdvance);
        var p2 = new CandlestickPatternDetector().Detect(hangingAfterAdvance);
        Assert.Contains(p2, p => p.Type == CandlePatternType.HangingMan && p.IsBullish == false);
    }

    [Fact]
    public void ShootingStar_OnlyAfterAdvance()
    {
        var candles = Trend(100m, 1m, 6);
        candles.Add(Bar(106m, 109m, 105.9m, 106.8m)); // lunga ombra superiore, corpo 0.8
        Stamp(candles);
        var patterns = new CandlestickPatternDetector().Detect(candles);
        Assert.Contains(patterns, p => p.Type == CandlePatternType.ShootingStar);

        // Stessa forma senza trend precedente: nessuna shooting star.
        var flat = new List<OhlcvData> { Bar(100m, 100.5m, 99.5m, 100m) };
        for (var i = 0; i < 6; i++) flat.Add(Bar(100m, 100.5m, 99.5m, 100m));
        flat.Add(Bar(100m, 103m, 99.9m, 100.8m));
        Stamp(flat);
        var patterns2 = new CandlestickPatternDetector().Detect(flat);
        Assert.DoesNotContain(patterns2, p => p.Type == CandlePatternType.ShootingStar);
    }

    [Fact]
    public void Engulfing_RequiresOppositeTrend()
    {
        // Downtrend, poi candela bianca che ingloba il corpo precedente.
        var candles = Trend(100m, -1m, 6);
        candles.Add(Bar(94m, 94.2m, 92.8m, 93m));    // ultima candela nera del declino
        candles.Add(Bar(92.5m, 95.5m, 92m, 95m));    // bianca, corpo 92.5-95 ingloba 93-94
        Stamp(candles);

        var patterns = new CandlestickPatternDetector().Detect(candles);
        Assert.Contains(patterns, p => p.Type == CandlePatternType.BullishEngulfing && p.IsBullish == true);
    }

    [Fact]
    public void Harami_LargeThenSmallInsideBody()
    {
        // Uptrend + grande candela bianca + piccola candela dentro il corpo -> bearish harami.
        var candles = Trend(100m, 1m, 6);
        candles.Add(Bar(106m, 111m, 105.5m, 110.5m)); // grande bianca (corpo 4.5)
        candles.Add(Bar(108.5m, 109.3m, 107.7m, 108m)); // piccola dentro il corpo
        Stamp(candles);

        var patterns = new CandlestickPatternDetector().Detect(candles);
        Assert.Contains(patterns, p => p.Type == CandlePatternType.BearishHarami && p.IsBullish == false);
    }

    [Fact]
    public void ThreeWhiteSoldiers_Detected()
    {
        var candles = new List<OhlcvData>();
        for (var i = 0; i < 3; i++) candles.Add(Bar(100m, 100.5m, 99.5m, 100m)); // base piatta
        candles.Add(Bar(100m, 103.2m, 99.8m, 103m));
        candles.Add(Bar(102m, 106.2m, 101.8m, 106m));  // apre dentro il corpo precedente
        candles.Add(Bar(105m, 109.2m, 104.8m, 109m));
        Stamp(candles);

        var patterns = new CandlestickPatternDetector().Detect(candles);
        Assert.Contains(patterns, p => p.Type == CandlePatternType.ThreeWhiteSoldiers && p.IsBullish == true);
    }

    [Fact]
    public void KeyReversal_NewHighButCloseBelowPrevClose()
    {
        // Uptrend di 12 barre, poi barra che segna il massimo di periodo ma chiude sotto la
        // chiusura precedente -> key reversal ribassista (McAllen cap. 14).
        var candles = Trend(100m, 1m, 12);
        var prevClose = candles[^1].Close; // 112
        candles.Add(Bar(112m, 115m, 110.5m, prevClose - 1m, volume: 300m)); // volume 3x
        Stamp(candles);

        var patterns = new CandlestickPatternDetector().Detect(candles);
        var kr = patterns.Single(p => p.Type == CandlePatternType.KeyReversalBearish);
        Assert.False(kr.IsBullish);
        Assert.True(kr.VolumeConfirmed); // 300 contro media ~100
    }

    [Fact]
    public void RisingThreeMethods_ContinuationPattern()
    {
        var candles = Trend(100m, 1m, 6);
        candles.Add(Bar(106m, 111.2m, 105.8m, 111m));       // grande candela bianca
        candles.Add(Bar(110.5m, 110.8m, 109m, 109.5m));     // 3 piccole negative sopra il low
        candles.Add(Bar(109.5m, 109.8m, 108m, 108.5m));
        candles.Add(Bar(108.5m, 108.8m, 107m, 107.5m));
        candles.Add(Bar(107.5m, 112.5m, 107.3m, 112m));     // bianca che chiude oltre la prima
        Stamp(candles);

        var patterns = new CandlestickPatternDetector().Detect(candles);
        var rtm = patterns.Single(p => p.Type == CandlePatternType.RisingThreeMethods);
        Assert.True(rtm.IsBullish);
        Assert.False(rtm.IsReversal);
    }

    [Fact]
    public void EmptySeries_NoThrow()
    {
        Assert.Empty(new CandlestickPatternDetector().Detect([]));
    }
}
