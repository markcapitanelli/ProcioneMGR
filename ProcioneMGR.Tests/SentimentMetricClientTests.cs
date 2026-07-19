using ProcioneMGR.Data;
using ProcioneMGR.Services.Sentiment.Metrics;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dei parser PURI dei client di metriche sentiment (Sentiment 2.0): nessuna rete, fixture
/// JSON REALI catturate dal vivo il 2026-07-19 dalle API pubbliche (alternative.me /fng/ e
/// fapi.binance.com /futures/data/*) — la cattura stessa è la prova di raggiungibilità.
/// </summary>
public sealed class SentimentMetricClientTests
{
    private static string LoadFixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // --- Fear & Greed ---

    [Fact]
    public void ParseFng_RealFixture_ParsesAllDailyPoints()
    {
        var samples = FearGreedClient.ParseFng(LoadFixture("fng_sample.json"));

        Assert.Equal(7, samples.Count);
        Assert.All(samples, s =>
        {
            Assert.Equal(SentimentMetrics.FearGreedIndex, s.Metric);
            Assert.Equal(string.Empty, s.Symbol); // indice di mercato, non per-simbolo
            Assert.InRange(s.Value, 0m, 100m);
        });
        // Primo elemento della fixture: value 28, timestamp 1784419200 (unix s).
        Assert.Equal(28m, samples[0].Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784419200).UtcDateTime, samples[0].TimestampUtc);
    }

    [Fact]
    public void ParseFng_MalformedEntry_IsSkippedWithoutFailingTheSource()
    {
        const string json = """{"data":[{"value":"boom","timestamp":"1784419200"},{"value":"55","timestamp":"1784332800"}]}""";
        var samples = FearGreedClient.ParseFng(json);
        Assert.Single(samples);
        Assert.Equal(55m, samples[0].Value);
    }

    [Fact]
    public void ParseFng_EmptyOrMissingData_ReturnsEmpty()
    {
        Assert.Empty(FearGreedClient.ParseFng("""{"metadata":{"error":null}}"""));
        Assert.Empty(FearGreedClient.ParseFng("""{"data":[]}"""));
    }

    // --- Binance futures ---

    [Fact]
    public void ParseRatioSeries_RealGlobalLongShortFixture_MapsSymbolAndValues()
    {
        var samples = BinanceFuturesSentimentClient.ParseRatioSeries(
            LoadFixture("binance_global_long_short_sample.json"), "longShortRatio",
            SentimentMetrics.GlobalLongShortRatio, "BTC");

        Assert.Equal(4, samples.Count);
        Assert.All(samples, s => Assert.Equal("BTC", s.Symbol));
        Assert.Equal(1.3213m, samples[0].Value); // invariant culture, valore stringa dell'API
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1784462400000).UtcDateTime, samples[0].TimestampUtc);
    }

    [Fact]
    public void ParseRatioSeries_RealTakerFixture_UsesBuySellRatioField()
    {
        var samples = BinanceFuturesSentimentClient.ParseRatioSeries(
            LoadFixture("binance_taker_ratio_sample.json"), "buySellRatio",
            SentimentMetrics.TakerBuySellRatio, "BTC");

        Assert.Equal(4, samples.Count);
        Assert.Equal(0.8259m, samples[0].Value);
    }

    [Fact]
    public void ParseRatioSeries_RealTopTraderFixture_Parses()
    {
        var samples = BinanceFuturesSentimentClient.ParseRatioSeries(
            LoadFixture("binance_top_trader_ratio_sample.json"), "longShortRatio",
            SentimentMetrics.TopTraderLongShortRatio, "BTC");
        Assert.Equal(4, samples.Count);
    }

    [Fact]
    public void ParseOpenInterestSeries_RealFixture_EmitsBothMetricsPerPoint()
    {
        var samples = BinanceFuturesSentimentClient.ParseOpenInterestSeries(
            LoadFixture("binance_open_interest_hist_sample.json"), "BTC");

        Assert.Equal(8, samples.Count); // 4 punti × (OpenInterest + OpenInterestValue)
        Assert.Equal(4, samples.Count(s => s.Metric == SentimentMetrics.OpenInterest));
        Assert.Equal(4, samples.Count(s => s.Metric == SentimentMetrics.OpenInterestValue));
        Assert.Equal(102500.985m, samples.First(s => s.Metric == SentimentMetrics.OpenInterest).Value);
    }

    [Fact]
    public void ParseFundingRates_RealFixture_ConvertsToPercent()
    {
        var samples = BinanceFuturesSentimentClient.ParseFundingRates(
            LoadFixture("binance_funding_rate_sample.json"), "BTC");

        Assert.Equal(4, samples.Count);
        Assert.All(samples, s => Assert.Equal(SentimentMetrics.FundingRate, s.Metric));
        // 0.00006955 (frazione) → 0.006955 (percento): convenzione ×100 della piattaforma.
        Assert.Equal(0.006955m, samples[0].Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1784361600000).UtcDateTime, samples[0].TimestampUtc);
    }

    [Fact]
    public void ToBaseTicker_MapsUsdtMarketsToBaseAsset()
    {
        Assert.Equal("BTC", BinanceFuturesSentimentClient.ToBaseTicker("BTCUSDT"));
        Assert.Equal("ETH", BinanceFuturesSentimentClient.ToBaseTicker("ethusdt"));
        Assert.Equal("SOLBUSD", BinanceFuturesSentimentClient.ToBaseTicker("SOLBUSD")); // non-USDT: invariato
    }

    [Fact]
    public void ParseRatioSeries_MalformedEntry_IsSkipped()
    {
        const string json = """[{"longShortRatio":"x","timestamp":1784462400000},{"longShortRatio":"2.5","timestamp":1784466000000}]""";
        var samples = BinanceFuturesSentimentClient.ParseRatioSeries(json, "longShortRatio", SentimentMetrics.GlobalLongShortRatio, "BTC");
        Assert.Single(samples);
        Assert.Equal(2.5m, samples[0].Value);
    }
}
