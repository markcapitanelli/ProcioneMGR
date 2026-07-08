using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test della selezione feature per Information Coefficient (Fase 3): l'ordinamento è per |IC|
/// decrescente, i filtri (|IC| minimo, TopN) si applicano correttamente, ed è deterministico —
/// così la scelta delle feature dei modelli ML diventa guidata dalla misura, non manuale.
/// </summary>
public class IcFeatureSelectorTests
{
    private static List<OhlcvData> MomentumCandles(int n, int seed = 11)
    {
        var rnd = new Random(seed);
        var closes = new List<decimal> { 100m };
        for (var i = 1; i < n; i++)
        {
            var prevRet = i >= 2 ? (double)(closes[i - 1] / closes[i - 2] - 1m) : 0.0;
            var drift = 0.6 * prevRet;                        // momentum reale
            var noise = (rnd.NextDouble() - 0.5) * 0.01;
            closes.Add((decimal)Math.Max(1.0, (double)closes[i - 1] * (1.0 + drift + noise)));
        }

        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var list = new List<OhlcvData>(n);
        for (var i = 0; i < n; i++)
        {
            var c = closes[i];
            var prev = i > 0 ? closes[i - 1] : c;
            list.Add(new OhlcvData
            {
                Symbol = "TEST", Timeframe = "1h", TimestampUtc = t0.AddHours(i),
                Open = prev, High = Math.Max(prev, c) * 1.01m, Low = Math.Min(prev, c) * 0.99m, Close = c,
                Volume = 100m + i % 20,
            });
        }
        return list;
    }

    private static List<FactorSpec> Candidates() =>
    [
        new("Momentum", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 3m, ["Skip"] = 0m }),
        new("Rsi", new RsiFactor(), new Dictionary<string, decimal> { ["Period"] = 14m }),
        new("RealizedVol", new RealizedVolatilityFactor(), new Dictionary<string, decimal> { ["Window"] = 20m }),
        new("MeanReversion", new MeanReversionFactor(), new Dictionary<string, decimal> { ["Lookback"] = 10m }),
    ];

    [Fact]
    public void Rank_IsSortedByDescendingAbsIc()
    {
        var selector = new IcFeatureSelector();
        var ranked = selector.Rank(Candidates(), MomentumCandles(600), new IcFeatureSelectionConfig { ForwardHorizon = 1 });

        Assert.Equal(4, ranked.Count);
        for (var i = 1; i < ranked.Count; i++)
            Assert.True(ranked[i - 1].AbsIc >= ranked[i].AbsIc, $"non ordinato a {i}: {ranked[i - 1].AbsIc} < {ranked[i].AbsIc}");
    }

    [Fact]
    public void Select_TopN_CapsCountAndKeepsTheStrongest()
    {
        var selector = new IcFeatureSelector();
        var candles = MomentumCandles(600);
        var ranked = selector.Rank(Candidates(), candles, new IcFeatureSelectionConfig());

        var selected = selector.Select(Candidates(), candles, new IcFeatureSelectionConfig { TopN = 1 });

        Assert.Single(selected);
        Assert.Equal(ranked[0].Spec.FeatureName, selected[0].FeatureName); // il migliore per |IC|
    }

    [Fact]
    public void Select_MinAbsIc_FiltersOutWeakFactors()
    {
        var selector = new IcFeatureSelector();
        var candles = MomentumCandles(600);

        // Nessun fattore ha |IC| > 0.99 su dati reali → la soglia estrema svuota la selezione.
        var none = selector.Select(Candidates(), candles, new IcFeatureSelectionConfig { MinAbsIc = 0.99, TopN = 10 });
        Assert.Empty(none);

        // Soglia nulla → tutti passano (fino a TopN).
        var all = selector.Select(Candidates(), candles, new IcFeatureSelectionConfig { MinAbsIc = 0.0, TopN = 10 });
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void Rank_IsDeterministic()
    {
        var selector = new IcFeatureSelector();
        var candles = MomentumCandles(500);
        var a = selector.Rank(Candidates(), candles, new IcFeatureSelectionConfig());
        var b = selector.Rank(Candidates(), candles, new IcFeatureSelectionConfig());

        Assert.Equal(a.Select(s => s.Spec.FeatureName), b.Select(s => s.Spec.FeatureName));
        for (var i = 0; i < a.Count; i++)
            Assert.Equal(a[i].Evaluation.InformationCoefficient, b[i].Evaluation.InformationCoefficient, 10);
    }
}
