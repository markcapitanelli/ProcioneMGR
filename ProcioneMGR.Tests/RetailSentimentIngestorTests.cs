using System.Text.Json;
using ProcioneMGR.Services.AltData;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="RetailSentimentIngestor.ParseRatios"/>: conversione long%→SentimentScore
/// in [-1,+1], mapping dei simboli crypto (BTCUSD→BTC) al ticker canonico già usato dalla
/// piattaforma, e verifica che il formato JSON reale dell'endpoint FXSSI (fixture) sia
/// deserializzabile come ci si aspetta (nessuna chiamata di rete).
/// </summary>
public class RetailSentimentIngestorTests
{
    [Fact]
    public void ParseRatios_ConvertsLongPercentToSentimentScoreRange()
    {
        var ratios = new Dictionary<string, string> { ["EURUSD"] = "65.21" };
        var items = RetailSentimentIngestor.ParseRatios(ratios);

        var item = Assert.Single(items);
        // (65.21 - 50) / 50 = 0.3042
        Assert.Equal(0.3042m, item.SentimentScoreOverride);
        Assert.InRange(item.SentimentScoreOverride!.Value, -1m, 1m);
    }

    [Fact]
    public void ParseRatios_50PercentLong_IsNeutralZero()
    {
        var ratios = new Dictionary<string, string> { ["GBPUSD"] = "50.0" };
        var items = RetailSentimentIngestor.ParseRatios(ratios);
        Assert.Equal(0m, items[0].SentimentScoreOverride);
    }

    [Fact]
    public void ParseRatios_100PercentLong_IsPlusOne()
    {
        var ratios = new Dictionary<string, string> { ["USDJPY"] = "100" };
        var items = RetailSentimentIngestor.ParseRatios(ratios);
        Assert.Equal(1m, items[0].SentimentScoreOverride);
    }

    [Fact]
    public void ParseRatios_ZeroPercentLong_IsMinusOne()
    {
        var ratios = new Dictionary<string, string> { ["USDCHF"] = "0" };
        var items = RetailSentimentIngestor.ParseRatios(ratios);
        Assert.Equal(-1m, items[0].SentimentScoreOverride);
    }

    [Fact]
    public void ParseRatios_MapsCryptoTickers_ToCanonicalSymbol()
    {
        var ratios = new Dictionary<string, string> { ["BTCUSD"] = "56.79", ["ETHUSD"] = "81.13" };
        var items = RetailSentimentIngestor.ParseRatios(ratios);

        Assert.Contains(items, i => i.SymbolsOverride!.Contains("BTC"));
        Assert.Contains(items, i => i.SymbolsOverride!.Contains("ETH"));
        Assert.DoesNotContain(items, i => i.SymbolsOverride!.Contains("BTCUSD"));
    }

    [Fact]
    public void ParseRatios_KeepsForexPairsAsIs_NoCryptoMapping()
    {
        var ratios = new Dictionary<string, string> { ["EURUSD"] = "60.0" };
        var items = RetailSentimentIngestor.ParseRatios(ratios);
        Assert.Contains("EURUSD", items[0].SymbolsOverride!);
    }

    [Fact]
    public void ParseRatios_AllItemsTaggedAsRetailSentiment()
    {
        var ratios = new Dictionary<string, string> { ["EURUSD"] = "60.0", ["GBPUSD"] = "45.0" };
        var items = RetailSentimentIngestor.ParseRatios(ratios);
        Assert.All(items, i => Assert.Equal(NewsCategory.RetailSentiment, i.CategoryOverride));
    }

    [Fact]
    public void ParseRatios_MalformedValue_IsSkippedNotThrown()
    {
        var ratios = new Dictionary<string, string> { ["EURUSD"] = "not-a-number", ["GBPUSD"] = "55.0" };
        var items = RetailSentimentIngestor.ParseRatios(ratios);

        var item = Assert.Single(items);
        Assert.Contains("GBPUSD", item.SymbolsOverride!);
    }

    [Fact]
    public void ParseRatios_EachItemHasAUniqueUrl_ForDedupe()
    {
        var ratios = new Dictionary<string, string> { ["EURUSD"] = "60.0", ["GBPUSD"] = "45.0", ["USDJPY"] = "30.0" };
        var items = RetailSentimentIngestor.ParseRatios(ratios);
        Assert.Equal(items.Count, items.Select(i => i.Url).Distinct().Count());
    }

    [Fact]
    public void RealApiJsonShape_DeserializesIntoBrokerSymbolDictionaries()
    {
        // Verifica che il formato JSON reale dell'endpoint FXSSI (fixture salvata dopo verifica
        // dal vivo) sia deserializzabile nella forma broker -> (simbolo -> percentuale) attesa.
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fxssi_current_ratio_sample.json"));
        using var doc = JsonDocument.Parse(json);
        var brokers = doc.RootElement.GetProperty("brokers");

        Assert.True(brokers.TryGetProperty("fxssi", out var fxssi));
        Assert.True(brokers.TryGetProperty("myfxbook", out var myfxbook));

        var fxssiRatios = fxssi.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var myfxbookRatios = myfxbook.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

        var fxssiItems = RetailSentimentIngestor.ParseRatios(fxssiRatios);
        var myfxbookItems = RetailSentimentIngestor.ParseRatios(myfxbookRatios);

        Assert.Contains(fxssiItems, i => i.SymbolsOverride!.Contains("BTC")); // solo "fxssi" ha crypto nella fixture
        Assert.DoesNotContain(myfxbookItems, i => i.SymbolsOverride!.Contains("BTC"));
        Assert.NotEmpty(myfxbookItems);
    }
}
