using ProcioneMGR.Data;
using ProcioneMGR.Services.Sentiment;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="SentimentAlphaFactor"/>: media rolling delle notizie nella finestra,
/// null in assenza di notizie, filtro per simbolo, e l'invariante anti-look-ahead (stesso
/// contratto degli altri <c>IAlphaFactor</c> — verificato per troncamento).
/// </summary>
public class SentimentAlphaFactorTests
{
    private static List<OhlcvData> MakeHourlyCandles(DateTime start, int n)
    {
        var list = new List<OhlcvData>(n);
        for (var i = 0; i < n; i++)
        {
            list.Add(new OhlcvData
            {
                Symbol = "BTC/USDT",
                Timeframe = "1h",
                TimestampUtc = start.AddHours(i),
                Open = 100m,
                High = 101m,
                Low = 99m,
                Close = 100m,
                Volume = 10m,
            });
        }
        return list;
    }

    [Fact]
    public void Compute_AveragesNewsWithinLookbackWindow()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeHourlyCandles(t0, 10);

        var news = new List<ScoredNewsItem>
        {
            new(t0.AddHours(2).AddMinutes(-30), 1.0m, ["BTC"]),   // dentro la finestra di 24h per la candela a t=2
            new(t0.AddHours(2).AddMinutes(-10), -0.5m, ["BTC"]),  // idem
        };

        var factor = new SentimentAlphaFactor(news);
        var result = factor.Compute(candles, new Dictionary<string, decimal> { ["LookbackHours"] = 24m });

        Assert.Equal(0.25m, result[2]!.Value); // media di 1.0 e -0.5
    }

    [Fact]
    public void Compute_NoNewsInWindow_IsNull()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeHourlyCandles(t0, 5);
        var factor = new SentimentAlphaFactor([]);

        var result = factor.Compute(candles, new Dictionary<string, decimal> { ["LookbackHours"] = 24m });

        Assert.All(result, v => Assert.Null(v));
    }

    [Fact]
    public void Compute_NewsOutsideLookbackWindow_IsExcluded()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeHourlyCandles(t0, 30);

        var news = new List<ScoredNewsItem> { new(t0, 1.0m, ["BTC"]) }; // pubblicata a t0

        var factor = new SentimentAlphaFactor(news);
        var result = factor.Compute(candles, new Dictionary<string, decimal> { ["LookbackHours"] = 6m });

        Assert.Equal(1.0m, result[0]!.Value);   // t0: la notizia è appena uscita, dentro la finestra
        Assert.Equal(1.0m, result[5]!.Value);   // t0+5h: ancora dentro le 6h di lookback
        Assert.Null(result[7]);                  // t0+7h: fuori dalla finestra di 6h
    }

    [Fact]
    public void Compute_FiltersBySymbol_WhenSpecified()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeHourlyCandles(t0, 5);

        var news = new List<ScoredNewsItem>
        {
            new(t0, 1.0m, ["BTC"]),
            new(t0, -1.0m, ["ETH"]), // simbolo diverso: deve essere ignorata dal filtro "BTC"
        };

        var factor = new SentimentAlphaFactor(news, symbolFilter: "BTC");
        var result = factor.Compute(candles, new Dictionary<string, decimal> { ["LookbackHours"] = 24m });

        Assert.Equal(1.0m, result[0]!.Value); // solo la notizia BTC, non la media con quella ETH
    }

    [Fact]
    public void Compute_IsAntiLookAhead_TruncationDoesNotChangePastValues()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeHourlyCandles(t0, 50);
        var rnd = new Random(1);
        var news = Enumerable.Range(0, 30)
            .Select(i => new ScoredNewsItem(t0.AddHours(rnd.Next(0, 50)), (decimal)(rnd.NextDouble() * 2 - 1), (IReadOnlyList<string>)["BTC"]))
            .ToList();

        var factor = new SentimentAlphaFactor(news);
        var p = new Dictionary<string, decimal> { ["LookbackHours"] = 12m };
        var full = factor.Compute(candles, p);

        foreach (var cut in new[] { 10, 25, 49 })
        {
            var truncated = factor.Compute(candles.Take(cut + 1).ToList(), p);
            Assert.Equal(full[cut].HasValue, truncated[cut].HasValue);
            if (full[cut].HasValue)
            {
                Assert.Equal(full[cut]!.Value, truncated[cut]!.Value);
            }
        }
    }

    [Fact]
    public void Compute_ReturnsSeriesAlignedToInputLength()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = MakeHourlyCandles(t0, 17);
        var factor = new SentimentAlphaFactor([new ScoredNewsItem(t0, 0.5m, ["BTC"])]);

        var result = factor.Compute(candles, new Dictionary<string, decimal>());
        Assert.Equal(17, result.Count);
    }
}
