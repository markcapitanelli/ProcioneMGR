using ProcioneMGR.Data;
using ProcioneMGR.Services.Sentiment;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test PURI di <see cref="SentimentCompositeCalculator"/>: z-score sul baseline, rinormalizzazione
/// dei pesi con componenti mancanti, bounds del composite, flag contrarian esattamente alle soglie,
/// Δ7d del Fear &amp; Greed, variazione % dell'open interest, input vuoto → neutro senza flag.
/// </summary>
public sealed class SentimentCompositeCalculatorTests
{
    private static readonly DateTime Now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Unspecified);

    private static SentimentOptions Options() => new(); // default: soglie 20/80, z 2.0

    private static SentimentMetricPoint Point(string metric, string symbol, decimal value, int hoursAgo, string source = "BinanceFutures")
        => new() { Source = source, Metric = metric, Symbol = symbol, Value = value, TimestampUtc = Now.AddHours(-hoursAgo) };

    /// <summary>Serie piatta a <paramref name="baseline"/> con ultimo punto a <paramref name="latest"/>; la serie NON è a σ=0 grazie a un'alternanza ±<paramref name="wiggle"/>.</summary>
    private static List<SentimentMetricPoint> SeriesWithLatest(string metric, string symbol, decimal baseline, decimal latest, decimal wiggle = 0.1m, int count = 30)
    {
        var points = new List<SentimentMetricPoint>();
        for (var i = count; i >= 1; i--)
        {
            var v = baseline + (i % 2 == 0 ? wiggle : -wiggle);
            points.Add(Point(metric, symbol, v, hoursAgo: i));
        }
        points.Add(Point(metric, symbol, latest, hoursAgo: 0));
        return points;
    }

    [Fact]
    public void EmptyInput_ProducesNeutralSnapshot_WithoutExtremes()
    {
        var snap = SentimentCompositeCalculator.Compute(Options(), Now, [], new Dictionary<string, double>(), null, ["BTC"]);

        Assert.Equal(0.0, snap.CompositeScore);
        Assert.Null(snap.FearGreedValue);
        Assert.Empty(snap.Extremes);
        var btc = Assert.Single(snap.Symbols);
        Assert.Null(btc.FundingZ);
        Assert.Equal(0.0, btc.Composite);
    }

    [Fact]
    public void FearGreed_ExtremeFear_FlagsContrarian_AndComputesDelta7d()
    {
        var metrics = new List<SentimentMetricPoint>();
        for (var d = 20; d >= 0; d--)
        {
            // Discesa da 60 a 15: oggi 15 (extreme fear), 7 giorni fa ~ un valore più alto.
            metrics.Add(Point(SentimentMetrics.FearGreedIndex, "", 15m + d * 2m, hoursAgo: d * 24, source: "FearGreed"));
        }

        var snap = SentimentCompositeCalculator.Compute(Options(), Now, metrics, new Dictionary<string, double>(), null, []);

        Assert.Equal(15.0, snap.FearGreedValue);
        Assert.Equal("Extreme Fear", snap.FearGreedLabel);
        Assert.Equal(15.0 - 29.0, snap.FearGreedDelta7d); // 7 giorni fa: 15 + 7*2 = 29
        Assert.Contains(snap.Extremes, e => e.Contains("extreme fear", StringComparison.OrdinalIgnoreCase));
        // Composite solo da F&G: (15-50)/50 = -0.7
        Assert.Equal(-0.7, snap.CompositeScore, precision: 10);
    }

    [Fact]
    public void FearGreed_AtThresholds_FlagsExactlyAtBoundaries()
    {
        var opt = Options(); // low 20, high 80
        List<SentimentMetricPoint> Fng(decimal v) => [Point(SentimentMetrics.FearGreedIndex, "", v, 0, "FearGreed")];

        Assert.Contains(SentimentCompositeCalculator.Compute(opt, Now, Fng(20m), new Dictionary<string, double>(), null, []).Extremes,
            e => e.Contains("extreme fear", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(SentimentCompositeCalculator.Compute(opt, Now, Fng(21m), new Dictionary<string, double>(), null, []).Extremes);
        Assert.Contains(SentimentCompositeCalculator.Compute(opt, Now, Fng(80m), new Dictionary<string, double>(), null, []).Extremes,
            e => e.Contains("extreme greed", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(SentimentCompositeCalculator.Compute(opt, Now, Fng(79m), new Dictionary<string, double>(), null, []).Extremes);
    }

    [Fact]
    public void ZScore_RequiresEnoughObservations_AndNonFlatSeries()
    {
        // 5 punti: sotto la soglia minima → z null.
        var few = SeriesWithLatest(SentimentMetrics.FundingRate, "BTC", 0.01m, 0.05m, count: 4);
        Assert.Null(SentimentCompositeCalculator.ZScore(few.Take(5).ToList()));

        // Serie piatta (σ=0) → z null.
        var flat = Enumerable.Range(1, 30).Select(i => Point(SentimentMetrics.FundingRate, "BTC", 0.01m, i)).ToList();
        Assert.Null(SentimentCompositeCalculator.ZScore(flat));

        // Serie con varianza e ultimo punto fuori scala → z ben definito e positivo.
        var spiked = SeriesWithLatest(SentimentMetrics.FundingRate, "BTC", 0.01m, 0.5m);
        var z = SentimentCompositeCalculator.ZScore(spiked);
        Assert.NotNull(z);
        Assert.True(z > 2.0, $"z atteso > 2, ottenuto {z}");
    }

    [Fact]
    public void FundingSpike_FlagsLongSqueezeRisk_AndPushesCompositePositive()
    {
        var metrics = SeriesWithLatest(SentimentMetrics.FundingRate, "BTC", 0.01m, 0.5m);

        var snap = SentimentCompositeCalculator.Compute(Options(), Now, metrics, new Dictionary<string, double>(), null, ["BTC"]);

        var btc = Assert.Single(snap.Symbols);
        Assert.NotNull(btc.FundingZ);
        Assert.True(btc.FundingZ > 2.0);
        Assert.Contains(btc.Extremes, e => e.Contains("long squeeze"));
        // Unico componente disponibile (funding): contributo z/2 clampato a +1 → composite +1.
        Assert.Equal(1.0, btc.Composite, precision: 10);
        Assert.Equal(0.5, btc.FundingPercent!.Value, precision: 10); // FundingPercent = ultimo valore della serie
    }

    [Fact]
    public void Weights_AreRenormalizedOverAvailableComponents()
    {
        // Solo news (peso 0.20) e F&G (peso 0.25) disponibili: composite = media pesata dei due.
        var metrics = new List<SentimentMetricPoint> { Point(SentimentMetrics.FearGreedIndex, "", 75m, 0, "FearGreed") };
        var opt = Options();

        var snap = SentimentCompositeCalculator.Compute(opt, Now, metrics,
            new Dictionary<string, double>(), marketNewsScore24h: -0.4, baseSymbols: []);

        var fngComponent = (75.0 - 50.0) / 50.0; // 0.5
        var expected = (opt.WeightNews * -0.4 + opt.WeightFearGreed * fngComponent) / (opt.WeightNews + opt.WeightFearGreed);
        Assert.Equal(expected, snap.CompositeScore, precision: 10);
        Assert.InRange(snap.CompositeScore, -1.0, 1.0);
    }

    [Fact]
    public void OpenInterest_Change24h_IsContextOnly_NeverInComposite()
    {
        // OI raddoppiato nelle 24h con serie storicamente piatta: flag sì, composite NO (0 senza altri componenti).
        var oi = SeriesWithLatest(SentimentMetrics.OpenInterestValue, "BTC", 1_000_000m, 5_000_000m, wiggle: 1000m, count: 60);

        var snap = SentimentCompositeCalculator.Compute(Options(), Now, oi, new Dictionary<string, double>(), null, ["BTC"]);

        var btc = Assert.Single(snap.Symbols);
        Assert.NotNull(btc.OiChange24hPercent);
        Assert.True(btc.OiChange24hPercent > 100.0, $"variazione attesa > 100%, ottenuta {btc.OiChange24hPercent}");
        Assert.Contains(btc.Extremes, e => e.Contains("open interest", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0.0, btc.Composite); // l'OI non entra mai nel composite
    }

    [Fact]
    public void SymbolNews_FeedsSymbolComposite_MarketNewsFeedsMarketComposite()
    {
        var snap = SentimentCompositeCalculator.Compute(Options(), Now, [],
            new Dictionary<string, double> { ["BTC"] = 0.6 }, marketNewsScore24h: 0.2, baseSymbols: ["BTC", "ETH"]);

        Assert.Equal(0.6, snap.Symbols.Single(s => s.Symbol == "BTC").Composite, precision: 10);
        Assert.Equal(0.0, snap.Symbols.Single(s => s.Symbol == "ETH").Composite); // nessun dato ETH
        Assert.Equal(0.2, snap.CompositeScore, precision: 10); // di mercato: solo news di mercato
    }
}
