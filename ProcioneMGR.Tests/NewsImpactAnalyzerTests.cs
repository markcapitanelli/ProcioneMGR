using ProcioneMGR.Data;
using ProcioneMGR.Services.AltData;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="NewsImpactAnalyzer"/> su OHLCV sintetici con un pattern di impatto NOTO
/// (candele costruite apposta perché il ritorno atteso a ogni orizzonte sia calcolabile a mano),
/// così le asserzioni verificano il numero esatto, non solo "diverso da zero"/"non NaN".
/// </summary>
public class NewsImpactAnalyzerTests
{
    private readonly NewsImpactAnalyzer _analyzer = new();

    /// <summary>Candele orarie con prezzo costante finché non raddoppia in un colpo solo a <paramref name="jumpAtHour"/> e resta lì: isola l'effetto di un singolo evento su tutti e tre gli orizzonti.</summary>
    private static List<OhlcvData> MakeStepCandles(DateTime start, int hours, int jumpAtHour, decimal before, decimal after)
    {
        var list = new List<OhlcvData>(hours);
        for (var i = 0; i < hours; i++)
        {
            var price = i >= jumpAtHour ? after : before;
            list.Add(new OhlcvData
            {
                Symbol = "BTC/USDT",
                Timeframe = "1h",
                TimestampUtc = start.AddHours(i),
                Open = price,
                High = price,
                Low = price,
                Close = price,
                Volume = 1m,
            });
        }
        return list;
    }

    private static AltDataPoint MakeNews(DateTime t, string category, string source, decimal? sentiment = null, string symbolsJson = "[]") => new()
    {
        TimestampUtc = t,
        Source = source,
        Title = "test",
        Category = category,
        SymbolsJson = symbolsJson,
        SentimentScore = sentiment,
        DedupeKey = Guid.NewGuid().ToString(),
    };

    [Fact]
    public void Analyze_ComputesExactReturn_ForKnownPriceStep()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Prezzo 100 fino all'ora 10 inclusa, poi salta a 110 (rendimento noto: +10%).
        var candles = MakeStepCandles(t0, 40, jumpAtHour: 11, before: 100m, after: 110m);
        var news = new List<AltDataPoint> { MakeNews(t0.AddHours(10), "Macro", "FXStreet") };

        var report = _analyzer.Analyze("BTC/USDT", news, candles);

        var macro = Assert.Single(report.ByCategory);
        Assert.Equal("Macro", macro.Category);
        Assert.Equal(1, macro.Stats.Observations);
        // Notizia a t=10 (prezzo 100), t+1h=11 (prezzo salta a 110): +10% già visibile a 1h e oltre.
        Assert.Equal(0.10, macro.Stats.AvgReturn1h, precision: 6);
        Assert.Equal(0.10, macro.Stats.AvgReturn4h, precision: 6);
        Assert.Equal(0.10, macro.Stats.AvgReturn24h, precision: 6);
    }

    [Fact]
    public void Analyze_NoPriceMovement_ReturnsExactlyZero_NotNaN()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeStepCandles(t0, 40, jumpAtHour: 1000, before: 100m, after: 100m); // mai salta
        var news = new List<AltDataPoint> { MakeNews(t0.AddHours(5), "Regulatory", "CoinDesk") };

        var report = _analyzer.Analyze("BTC/USDT", news, candles);

        var stats = report.ByCategory.Single().Stats;
        Assert.Equal(0.0, stats.AvgReturn1h);
        Assert.Equal(0.0, stats.AvgReturn4h);
        Assert.Equal(0.0, stats.AvgReturn24h);
        Assert.False(double.IsNaN(stats.AvgReturn24h));
    }

    [Fact]
    public void Analyze_NewsBeyondCandleRange_IsExcludedNotThrown()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeStepCandles(t0, 10, jumpAtHour: 5, before: 100m, after: 105m);
        // Notizia troppo vicina alla fine della serie: nessuna candela disponibile a t+24h.
        var news = new List<AltDataPoint> { MakeNews(t0.AddHours(9), "Macro", "FXStreet") };

        var report = _analyzer.Analyze("BTC/USDT", news, candles);

        var stats = report.ByCategory.Single().Stats;
        Assert.Equal(1, stats.Observations);
        Assert.Equal(0.0, stats.AvgReturn24h); // nessuna osservazione valida -> 0, non un'eccezione
    }

    [Fact]
    public void Analyze_GroupsByCategoryAndSource_Independently()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeStepCandles(t0, 40, jumpAtHour: 20, before: 100m, after: 100m);
        var news = new List<AltDataPoint>
        {
            MakeNews(t0.AddHours(1), "Macro", "FXStreet"),
            MakeNews(t0.AddHours(2), "Macro", "FXStreet"),
            MakeNews(t0.AddHours(3), "CentralBanks", "FXStreet-CentralBanks"),
        };

        var report = _analyzer.Analyze("BTC/USDT", news, candles);

        Assert.Equal(2, report.ByCategory.Count);
        Assert.Contains(report.ByCategory, c => c.Category == "Macro" && c.Stats.Observations == 2);
        Assert.Contains(report.ByCategory, c => c.Category == "CentralBanks" && c.Stats.Observations == 1);

        Assert.Equal(2, report.BySource.Count);
        Assert.Contains(report.BySource, s => s.Source == "FXStreet" && s.Stats.Observations == 2);
    }

    [Fact]
    public void Analyze_RetailSentimentCrossSource_SeparatesAgreeingFromDisagreeing()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Prezzo sale del 5% esattamente all'ora del match "concorde long", resta piatto altrove.
        var candles = MakeStepCandles(t0, 40, jumpAtHour: 11, before: 100m, after: 105m);

        var news = new List<AltDataPoint>
        {
            // Ora 10: entrambe le fonti fortemente long sullo stesso simbolo -> "concordi long".
            MakeNews(t0.AddHours(10), "RetailSentiment", "FXSSI", sentiment: 0.6m, symbolsJson: "[\"EURUSD\"]"),
            MakeNews(t0.AddHours(10), "RetailSentiment", "MyFxBook", sentiment: 0.5m, symbolsJson: "[\"EURUSD\"]"),
            // Ora 25: le due fonti divergono (una long, una short) -> "in disaccordo".
            MakeNews(t0.AddHours(25), "RetailSentiment", "FXSSI", sentiment: 0.5m, symbolsJson: "[\"EURUSD\"]"),
            MakeNews(t0.AddHours(25), "RetailSentiment", "MyFxBook", sentiment: -0.5m, symbolsJson: "[\"EURUSD\"]"),
        };

        var report = _analyzer.Analyze("BTC/USDT", news, candles);

        var eurusd = Assert.Single(report.RetailSentimentCrossSource);
        Assert.Equal("EURUSD", eurusd.Symbol);
        Assert.Equal(2, eurusd.MatchedSnapshots);
        Assert.Equal(1, eurusd.AgreementCount);
        // Il salto di prezzo (+5%) cade esattamente nella finestra 1h dopo il match concorde-long.
        Assert.Equal(0.05, eurusd.WhenBothLong.AvgReturn1h, precision: 6);
        Assert.Equal(1, eurusd.WhenBothLong.Observations);
        Assert.Equal(1, eurusd.WhenDisagree.Observations);
        Assert.Equal(0, eurusd.WhenBothShort.Observations);
    }

    [Fact]
    public void Analyze_RetailSentiment_RequiresBothSourcesInSameHour_ToCountAsMatch()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeStepCandles(t0, 40, jumpAtHour: 20, before: 100m, after: 100m);
        // Solo FXSSI ha un dato per GBPUSD: nessun match cross-source, la coppia non deve comparire.
        var news = new List<AltDataPoint> { MakeNews(t0.AddHours(5), "RetailSentiment", "FXSSI", sentiment: 0.6m, symbolsJson: "[\"GBPUSD\"]") };

        var report = _analyzer.Analyze("BTC/USDT", news, candles);

        Assert.Empty(report.RetailSentimentCrossSource);
    }

    [Fact]
    public void Analyze_EmptyNews_ReturnsEmptyReport_NotThrown()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeStepCandles(t0, 10, jumpAtHour: 5, before: 100m, after: 105m);

        var report = _analyzer.Analyze("BTC/USDT", [], candles);

        Assert.Empty(report.ByCategory);
        Assert.Empty(report.BySource);
        Assert.Empty(report.RetailSentimentCrossSource);
    }
}
