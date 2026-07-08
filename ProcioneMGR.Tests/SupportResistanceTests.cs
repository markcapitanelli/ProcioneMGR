using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;

namespace ProcioneMGR.Tests;

/// <summary>Test di pivot, livelli S/R, trend a swing, ritracciamenti e pattern grafici (McAllen cap. 7-10, 15).</summary>
public class SupportResistanceTests
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

    /// <summary>
    /// Serie che segue il percorso di chiusure dato (open = close precedente, wick di 0.2).
    /// Le ombre hanno un jitter decrescente con l'indice per evitare massimi/minimi
    /// perfettamente identici tra barre adiacenti (irrealistico e ambiguo per i pivot).
    /// </summary>
    private static List<OhlcvData> Path(params decimal[] closes)
    {
        var list = new List<OhlcvData>(closes.Length);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < closes.Length; i++)
        {
            var c = closes[i];
            var o = i == 0 ? c : closes[i - 1];
            var jitter = i * 0.003m;
            var bar = Bar(o, Math.Max(o, c) + 0.2m - jitter, Math.Min(o, c) - 0.2m + jitter, c);
            bar.TimestampUtc = t0.AddDays(i);
            list.Add(bar);
        }
        return list;
    }

    [Fact]
    public void FindPivots_DetectsLocalExtremes()
    {
        // Percorso: sale fino a 110 (indice 5), scende a 100 (indice 10), risale.
        var candles = Path(105m, 106m, 107m, 108m, 109m, 110m, 108m, 106m, 104m, 102m, 100m, 102m, 104m, 106m, 108m);
        var analyzer = new SupportResistanceAnalyzer { PivotWindow = 3 };
        var pivots = analyzer.FindPivots(candles);

        Assert.Contains(pivots, p => p.IsHigh && p.Index == 5);   // massimo locale a 110
        Assert.Contains(pivots, p => !p.IsHigh && p.Index == 10); // minimo locale a 100
    }

    [Fact]
    public void Levels_ClusterTouches_AndCountThem()
    {
        // Due massimi a ~110 (entro 1%) -> stesso livello con 2 tocchi.
        var candles = Path(
            100m, 104m, 108m, 110m, 108m, 104m, 100m, 96m,
            100m, 104m, 108m, 110.5m, 108m, 104m, 100m, 96m, 100m, 104m);
        var report = new SupportResistanceAnalyzer { PivotWindow = 2 }.Analyze(candles);

        var topLevel = report.Levels.OrderByDescending(l => l.Price).First();
        Assert.True(topLevel.Touches >= 2, $"attesi >=2 tocchi sul livello alto, trovati {topLevel.Touches}");
        Assert.InRange(topLevel.Price, 109m, 111.5m);
    }

    [Fact]
    public void Trend_HigherHighsAndLows_IsUptrend()
    {
        // Swing crescenti: H 110 (idx3), L 104 (idx6), H 114 (idx9), L 111 (idx12).
        var candles = Path(100m, 103m, 106m, 110m, 108m, 106m, 104m, 107m, 110m, 114m, 113m, 112m, 111m, 112m, 113m, 114m);
        var report = new SupportResistanceAnalyzer { PivotWindow = 2 }.Analyze(candles);
        Assert.Equal(SwingTrend.Uptrend, report.Trend);
    }

    [Fact]
    public void Retracement_MeasuresLastSwing()
    {
        // Minimo a 100 (idx2), gamba fino a 110 (idx7), poi ritraccia a 105 (~50%).
        var candles = Path(104m, 102m, 100m, 102m, 104m, 106m, 108m, 110m, 108.5m, 107m, 106m, 105m);
        var report = new SupportResistanceAnalyzer { PivotWindow = 2 }.Analyze(candles);

        Assert.NotNull(report.Retracement);
        // Swing rilevato dai pivot (prezzi pivot = high/low con offset 0.2).
        Assert.InRange(report.Retracement!.CurrentRetracementPercent, 40m, 60m);
        Assert.False(report.Retracement.IsReversalWarning);
        Assert.True(report.Retracement.Level33 > report.Retracement.Level50);
        Assert.True(report.Retracement.Level50 > report.Retracement.Level66);
    }

    [Fact]
    public void Breakout_AboveResistance_VolumeDecidesConfirmation()
    {
        // Doppio massimo a ~110 crea la resistenza; poi chiusura sopra 111 ad alto volume.
        var candles = Path(
            100m, 104m, 108m, 110m, 108m, 104m, 100m,
            104m, 108m, 110m, 108m, 104m, 100m, 104m, 108m, 112m, 113m, 114m);
        candles[15].Volume = 500m; // barra del breakout: volume 5x
        var report = new SupportResistanceAnalyzer { PivotWindow = 2, BreakoutVolumeFactor = 1.5m }.Analyze(candles);

        var upside = report.Breakouts.Where(b => b.IsUpside && b.LevelPrice > 109m).ToList();
        Assert.NotEmpty(upside);
        Assert.Contains(upside, b => b.VolumeConfirmed);
    }

    [Fact]
    public void ChartPattern_DoubleTop_WithConfirmation()
    {
        // Due massimi a 110/110.3 separati da un trough a 104, poi chiusura sotto 104.
        var candles = Path(
            100m, 104m, 108m, 110m, 108m, 106m, 104m, 106m, 108m, 110.3m,
            108m, 106m, 104m, 102m, 100m);
        var patterns = new ChartPatternDetector(pivotWindow: 2).Detect(candles);

        var dt = patterns.Single(p => p.Type == ChartPatternType.DoubleTop);
        Assert.False(dt.IsBullish);
        Assert.True(dt.Confirmed);
        Assert.NotNull(dt.ConfirmationIndex);
        Assert.InRange(dt.Neckline, 103m, 105m);
    }

    [Fact]
    public void ChartPattern_HeadAndShoulders_Detected()
    {
        // Spalla 110, testa 116, spalla 110.5; trough 104/105; conferma sotto la neckline.
        var candles = Path(
            100m, 105m, 110m, 106m, 104m, 108m, 112m, 116m, 112m, 108m,
            105m, 107m, 110.5m, 108m, 105m, 102m, 100m);
        var patterns = new ChartPatternDetector(pivotWindow: 2).Detect(candles);

        var hs = patterns.SingleOrDefault(p => p.Type == ChartPatternType.HeadAndShoulders);
        Assert.NotNull(hs);
        Assert.True(hs!.Confirmed);
        Assert.False(hs.IsBullish);
    }

    [Fact]
    public void ChartPattern_DoubleBottom_IsBullish()
    {
        var candles = Path(
            110m, 106m, 102m, 100m, 102m, 104m, 106m, 104m, 102m, 100.5m,
            102m, 104m, 106m, 108m, 110m);
        var patterns = new ChartPatternDetector(pivotWindow: 2).Detect(candles);

        var db = patterns.SingleOrDefault(p => p.Type == ChartPatternType.DoubleBottom);
        Assert.NotNull(db);
        Assert.True(db!.IsBullish);
        Assert.True(db.Confirmed); // chiusura sopra il massimo centrale (106)
    }

    [Fact]
    public void GapClassification_TrendContextDecidesType()
    {
        var analyzer = new GapLapAnalyzer();

        // Breakaway: gap up con trend precedente piatto.
        var flatThenGap = Path(100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m);
        var gapBar = Bar(103m, 105m, 102.5m, 104m);
        gapBar.TimestampUtc = flatThenGap[^1].TimestampUtc.AddDays(1);
        flatThenGap.Add(gapBar); // open 103 > prev high (100.2)
        var events1 = analyzer.ClassifyGaps(flatThenGap);
        var e1 = Assert.Single(events1);
        Assert.True(e1.IsUp);
        Assert.Equal(GapType.Breakaway, e1.Type);

        // Exhaustion: gap up dopo un trend gia' esteso (>10%).
        var strongTrend = Path(100m, 102m, 104m, 106m, 108m, 110m, 112m, 114m, 116m, 118m, 120m);
        var gapBar2 = Bar(123m, 125m, 122m, 121m);
        gapBar2.Volume = 500m;
        gapBar2.TimestampUtc = strongTrend[^1].TimestampUtc.AddDays(1);
        strongTrend.Add(gapBar2); // open 123 > prev high 120.2
        var events2 = analyzer.ClassifyGaps(strongTrend);
        var e2 = Assert.Single(events2);
        Assert.Equal(GapType.Exhaustion, e2.Type);
        Assert.True(e2.VolumeSpike);
    }

    [Fact]
    public void GapClassification_FilledDetection()
    {
        // Gap up poi il prezzo torna a coprire il livello del gap.
        var candles = Path(100m, 100m, 100m, 100m, 100m, 100m);
        var gap = Bar(103m, 104m, 102.5m, 103.5m);
        gap.TimestampUtc = candles[^1].TimestampUtc.AddDays(1);
        candles.Add(gap);
        var filler = Bar(103.5m, 103.8m, 99.9m, 100.5m); // low sotto il prev high 100.2
        filler.TimestampUtc = gap.TimestampUtc.AddDays(1);
        candles.Add(filler);

        var events = new GapLapAnalyzer().ClassifyGaps(candles);
        var e = Assert.Single(events);
        Assert.True(e.IsFilled);
        Assert.Equal(candles.Count - 1, e.FilledAtIndex);
    }

    [Fact]
    public void VolumeAnalyzer_DistributionWarning()
    {
        // Prezzo che sale ma con volume alto sui ribassi e basso sui rialzi -> divergenza.
        var candles = new List<OhlcvData>();
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        decimal price = 100m;
        for (var i = 0; i < 20; i++)
        {
            var up = i % 3 != 2; // due barre su, una giu': trend netto in salita
            var open = price;
            var close = up ? price + 1.5m : price - 1m;
            var bar = Bar(open, Math.Max(open, close) + 0.2m, Math.Min(open, close) - 0.2m, close,
                volume: up ? 50m : 300m); // volume pesa sui ribassi
            bar.TimestampUtc = t0.AddDays(i);
            candles.Add(bar);
            price = close;
        }

        var confirmations = new VolumeAnalyzer().ConfirmTrend(candles, window: 20);
        var last = confirmations[^1];
        Assert.True(last.PriceChangePercent > 0m);
        Assert.False(last.TrendConfirmed);
        Assert.True(last.DivergenceWarning);
    }
}
