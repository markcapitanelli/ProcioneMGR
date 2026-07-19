using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Sentiment;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'opt-in ML di Sentiment 2.0: <see cref="SentimentFeatureFactor"/> produce gli stessi
/// numeri del <see cref="SentimentAlphaFactor"/> diretto (delega pura, filtro simbolo dalle
/// candele) e <see cref="AlphaFactorFactory"/> lo espone nei prototipi SOLO col flag
/// EnableMlFeature, mentre Create("Sentiment") funziona sempre col provider (round-trip dei
/// modelli salvati) e il costruttore legacy resta invariato.
/// </summary>
public sealed class SentimentFeatureFactorTests
{
    private sealed class FakeNewsProvider(IReadOnlyList<ScoredNewsItem> items) : ISentimentNewsProvider
    {
        public IReadOnlyList<ScoredNewsItem> Snapshot { get; } = items;
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static readonly DateTime T0 = new(2026, 7, 10, 0, 0, 0, DateTimeKind.Unspecified);

    private static List<OhlcvData> Candles(string symbol, int count) =>
        Enumerable.Range(0, count).Select(i => new OhlcvData
        {
            Symbol = symbol,
            Timeframe = "1h",
            TimestampUtc = T0.AddHours(i),
            Open = 100, High = 101, Low = 99, Close = 100, Volume = 1000,
        }).ToList();

    private static List<ScoredNewsItem> News() =>
    [
        new(T0.AddHours(-2).AddTicks(1), 0.8m, ["BTC"]),
        new(T0.AddHours(1), -0.4m, ["BTC"]),
        new(T0.AddHours(1), 0.9m, ["ETH"]), // altro simbolo: il filtro deve escluderla
    ];

    [Fact]
    public void Compute_MatchesDirectSentimentAlphaFactor_WithSymbolFilterFromCandles()
    {
        var news = News();
        var candles = Candles("BTC/USDT", 6);
        var p = new Dictionary<string, decimal> { ["LookbackHours"] = 24m };

        var viaFactory = new SentimentFeatureFactor(new FakeNewsProvider(news)).Compute(candles, p);
        var direct = new SentimentAlphaFactor(news, "BTC").Compute(candles, p);

        Assert.Equal(direct, viaFactory);
        Assert.Equal(0.8m, viaFactory[0]); // solo la notizia BTC pre-T0, mai quella ETH
        Assert.Equal((0.8m - 0.4m) / 2m, viaFactory[1]); // anti-look-ahead ereditato
    }

    [Fact]
    public void Factory_Prototypes_ContainSentimentOnlyWithOptInFlag()
    {
        var provider = new FakeNewsProvider([]);

        var flagOff = new AlphaFactorFactory(provider, new SentimentOptions { EnableMlFeature = false }.AsMonitor());
        Assert.DoesNotContain(flagOff.Prototypes, f => f.Name == "Sentiment");

        var flagOn = new AlphaFactorFactory(provider, new SentimentOptions { EnableMlFeature = true }.AsMonitor());
        Assert.Contains(flagOn.Prototypes, f => f.Name == "Sentiment");
    }

    [Fact]
    public void Factory_Create_Sentiment_WorksRegardlessOfFlag_WhenProviderIsPresent()
    {
        // Round-trip dei SavedMlModel: un modello salvato col flag ON deve caricarsi anche a flag OFF.
        var factory = new AlphaFactorFactory(new FakeNewsProvider([]), new SentimentOptions { EnableMlFeature = false }.AsMonitor());
        var factor = factory.Create("Sentiment");
        Assert.Equal("Sentiment", factor.Name);
        Assert.IsType<SentimentFeatureFactor>(factor);
    }

    [Fact]
    public void Factory_LegacyConstructor_HasNoSentiment_AndCreateThrows()
    {
        var legacy = new AlphaFactorFactory(); // gli 11 call-site esistenti (test, tool, host Trading)
        Assert.DoesNotContain(legacy.Prototypes, f => f.Name == "Sentiment");
        Assert.Throws<NotSupportedException>(() => legacy.Create("Sentiment"));
    }

    [Fact]
    public void Factory_BaseCatalogIsUnchanged_ByTheOptionalDependencies()
    {
        var legacy = new AlphaFactorFactory();
        var withProvider = new AlphaFactorFactory(new FakeNewsProvider([]), new SentimentOptions().AsMonitor());
        Assert.Equal(legacy.Prototypes.Count, withProvider.Prototypes.Count); // flag off → stessi prototipi
        Assert.Equal("Momentum", withProvider.Create("Momentum").Name);       // switch storico intatto
    }
}
