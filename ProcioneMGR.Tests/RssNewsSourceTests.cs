using System.ServiceModel.Syndication;
using System.Xml;
using ProcioneMGR.Services.AltData;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="RssNewsSource.ParseFeed"/> su un feed RSS 2.0 campione (nessuna chiamata
/// di rete: il parsing è testato in isolamento, come raccomandato per l'ingestion — la parte
/// realmente non deterministica/esterna è solo il fetch HTTP, non la logica di estrazione).
/// </summary>
public class RssNewsSourceTests
{
    private const string SampleRss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>Sample Crypto News</title>
            <item>
              <title>Bitcoin rallies past key resistance</title>
              <description>Analysts see continued momentum after ETF inflows.</description>
              <link>https://example.com/news/1</link>
              <pubDate>Mon, 01 Jan 2024 12:00:00 GMT</pubDate>
            </item>
            <item>
              <title>Exchange reports security incident</title>
              <description>Investigation ongoing after reported breach.</description>
              <link>https://example.com/news/2</link>
              <pubDate>Tue, 02 Jan 2024 08:30:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    private static SyndicationFeed LoadFeed(string xml)
    {
        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader);
        return SyndicationFeed.Load(xmlReader);
    }

    [Fact]
    public void ParseFeed_ExtractsAllItems()
    {
        var feed = LoadFeed(SampleRss);
        var items = RssNewsSource.ParseFeed(feed);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void ParseFeed_ExtractsTitleSummaryUrl()
    {
        var feed = LoadFeed(SampleRss);
        var items = RssNewsSource.ParseFeed(feed);

        var first = items[0];
        Assert.Equal("Bitcoin rallies past key resistance", first.Title);
        Assert.Contains("ETF inflows", first.Summary);
        Assert.Equal("https://example.com/news/1", first.Url);
    }

    [Fact]
    public void ParseFeed_ExtractsPublishDate_AsUtc()
    {
        var feed = LoadFeed(SampleRss);
        var items = RssNewsSource.ParseFeed(feed);

        Assert.Equal(new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc), items[0].PublishedUtc);
        Assert.Equal(DateTimeKind.Utc, items[0].PublishedUtc.Kind);
    }

    [Fact]
    public void ParseFeed_EmptyChannel_ReturnsEmptyList()
    {
        const string emptyRss = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel><title>Empty</title></channel></rss>
            """;
        var feed = LoadFeed(emptyRss);
        var items = RssNewsSource.ParseFeed(feed);
        Assert.Empty(items);
    }

    [Fact]
    public void NewsFeeds_KnownFeeds_AreAllHttpsUrls()
    {
        Assert.NotEmpty(NewsFeeds.KnownFeeds);
        Assert.All(NewsFeeds.KnownFeeds.Values, url => Assert.StartsWith("https://", url));
    }

    [Fact]
    public void NewsFeeds_IncludesFxStreetGeneralAndCentralBanksFeeds()
    {
        Assert.True(NewsFeeds.KnownFeeds.ContainsKey("FXStreet"));
        Assert.True(NewsFeeds.KnownFeeds.ContainsKey("FXStreet-CentralBanks"));
    }

    /// <summary>
    /// Campione ricalcato dal feed FXStreet reale (verificato dal vivo, Fase D.2): a differenza
    /// del campione RSS generico sopra, la <c>pubDate</c> usa il suffisso "Z" invece di "GMT" —
    /// forma valida ma diversa, verificata qui perché <c>SyndicationFeed</c> potrebbe non
    /// gestirla allo stesso modo.
    /// </summary>
    private const string FxStreetSampleRss = """
        <?xml version="1.0" encoding="utf-8"?><rss xmlns:a10="http://www.w3.org/2005/Atom" version="2.0"><channel><title>FXStreet Forex &amp; Commodities News</title><link>https://www.fxstreet.com/news/feed</link><description>Real-time Forex News.</description><item><guid isPermaLink="false">d52bdb89-3536-47c9-9ba1-e4e94bdf2254</guid><link>https://www.fxstreet.com/news/thailand-stable-conditions-support-thai-baht-commerzbank-202607011922</link><title>Thailand: Stable conditions support Thai Baht &#8211; Commerzbank</title><description>Commerzbank&#8217;s Thailand update on Fed and ECB rate decision impact.</description><pubDate>Wed, 01 Jul 2026 19:22:00 Z</pubDate></item></channel></rss>
        """;

    [Fact]
    public void ParseFeed_FxStreetPubDateFormat_ParsesCorrectly()
    {
        var feed = LoadFeed(FxStreetSampleRss);
        var items = RssNewsSource.ParseFeed(feed);

        var item = Assert.Single(items);
        Assert.Equal(new DateTime(2026, 7, 1, 19, 22, 0, DateTimeKind.Utc), item.PublishedUtc);
        Assert.Equal(DateTimeKind.Utc, item.PublishedUtc.Kind);
        Assert.Contains("Thai Baht", item.Title);
    }
}
