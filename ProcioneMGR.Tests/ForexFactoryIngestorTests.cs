using ProcioneMGR.Services.AltData;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="ForexFactoryIngestor.ParseCalendar"/> su un frammento HTML di fixture
/// ricalcato dalla pagina reale (nessuna chiamata di rete — stesso principio di
/// <c>RssNewsSourceTests</c>: solo il fetch HTTP è non deterministico/esterno, non il parsing).
/// </summary>
public class ForexFactoryIngestorTests
{
    private static string LoadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "forexfactory_calendar_sample.html"));

    [Fact]
    public void ParseCalendar_ExtractsAllRealEvents_SkippingDayBreakers()
    {
        var items = ForexFactoryIngestor.ParseCalendar(LoadFixture());
        // 4 eventi reali (data-event-id) nella fixture; le righe day-breaker/borderfix non contano.
        Assert.Equal(4, items.Count);
    }

    [Fact]
    public void ParseCalendar_AllItemsTaggedAsEconomicCalendar()
    {
        var items = ForexFactoryIngestor.ParseCalendar(LoadFixture());
        Assert.All(items, i => Assert.Equal(NewsCategory.EconomicCalendar, i.CategoryOverride));
    }

    [Fact]
    public void ParseCalendar_ExtractsTitleCurrencyAndImpact()
    {
        var items = ForexFactoryIngestor.ParseCalendar(LoadFixture());

        var nfp = items.Single(i => i.Title == "Non-Farm Employment Change");
        Assert.Contains("USD", nfp.Symbols());
        Assert.Contains("High", nfp.Summary);

        var rba = items.Single(i => i.Title == "RBA Gov Bullock Speaks");
        Assert.Contains("AUD", rba.Symbols());
        Assert.Contains("Medium", rba.Summary);
    }

    [Fact]
    public void ParseCalendar_MapsImpactIconClasses_ToHumanReadableLevels()
    {
        var items = ForexFactoryIngestor.ParseCalendar(LoadFixture());

        Assert.Contains(items, i => i.Summary!.Contains("Low"));    // FOMC Member Barkin Speaks (yel)
        Assert.Contains(items, i => i.Summary!.Contains("Holiday")); // Bank Holiday (gra)
    }

    [Fact]
    public void ParseCalendar_CarriesDayForwardAcrossRowsWithoutDataDayDateline()
    {
        var items = ForexFactoryIngestor.ParseCalendar(LoadFixture());

        // "RBA Gov Bullock Speaks" ha data-day-dateline esplicito (Sun 28 Jun 2026 UTC).
        // "FOMC Member Barkin Speaks" NON ce l'ha (rowspan sulla data) ma deve ereditare lo stesso giorno.
        var rba = items.Single(i => i.Title == "RBA Gov Bullock Speaks");
        var fomc = items.Single(i => i.Title == "FOMC Member Barkin Speaks");
        Assert.Equal(rba.PublishedUtc.Date, fomc.PublishedUtc.Date);
    }

    [Fact]
    public void ParseCalendar_EachEventHasAUniqueUrl_ForDedupe()
    {
        // Tutti gli eventi vengono dalla stessa pagina calendario: senza un Url univoco per
        // evento, AltDataSyncService li tratterebbe come duplicati e ne inserirebbe solo uno.
        var items = ForexFactoryIngestor.ParseCalendar(LoadFixture());
        var distinctUrls = items.Select(i => i.Url).Distinct().Count();
        Assert.Equal(items.Count, distinctUrls);
    }

    [Fact]
    public void ParseCalendar_EmptyHtml_ReturnsEmptyList()
    {
        var items = ForexFactoryIngestor.ParseCalendar("<html><body>No calendar here</body></html>");
        Assert.Empty(items);
    }
}

internal static class RawNewsItemTestExtensions
{
    /// <summary>Helper di test: gli eventi ForexFactory codificano la valuta in SymbolsOverride.</summary>
    public static IReadOnlyList<string> Symbols(this RawNewsItem item) => item.SymbolsOverride ?? [];
}
