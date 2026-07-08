using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del modulo Alpha: invariante ANTI-LOOK-AHEAD (il valore a i non cambia troncando la
/// serie dopo i), correttezza dell'Information Coefficient su dati sintetici a segno noto,
/// e proprietà strutturali di quantili / forward returns.
/// </summary>
public class AlphaFactorTests
{
    private readonly IAlphaFactorFactory _factory = new AlphaFactorFactory();
    private readonly FactorEvaluator _eval = new();

    // --- Helpers -----------------------------------------------------------------------------

    private static List<OhlcvData> MakeCandles(IReadOnlyList<decimal> closes, IReadOnlyList<decimal>? vols = null)
    {
        var list = new List<OhlcvData>(closes.Count);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < closes.Count; i++)
        {
            var c = closes[i];
            var prev = i > 0 ? closes[i - 1] : c;
            list.Add(new OhlcvData
            {
                Symbol = "TEST/USDT",
                Timeframe = "1h",
                TimestampUtc = t0.AddHours(i),
                Open = prev,
                High = Math.Max(prev, c) * 1.01m,
                Low = Math.Min(prev, c) * 0.99m,
                Close = c,
                Volume = vols is not null ? vols[i] : 100m,
            });
        }
        return list;
    }

    /// <summary>Serie pseudo-casuale deterministica ma con struttura (trend + rumore).</summary>
    private static List<decimal> SyntheticCloses(int n, int seed = 42)
    {
        var rnd = new Random(seed);
        var closes = new List<decimal>(n);
        var price = 100m;
        for (var i = 0; i < n; i++)
        {
            var shock = (decimal)(rnd.NextDouble() - 0.5) * 2m; // [-1, +1]
            price = Math.Max(1m, price + shock);
            closes.Add(price);
        }
        return closes;
    }

    // --- Anti-look-ahead ---------------------------------------------------------------------

    [Theory]
    [InlineData("Momentum")]
    [InlineData("MeanReversion")]
    [InlineData("RealizedVol")]
    [InlineData("ParkinsonVol")]
    [InlineData("RelativeVolume")]
    [InlineData("RsiFactor")]
    [InlineData("MacdFactor")]
    [InlineData("DistanceFromMa")]
    public void Factor_IsAntiLookAhead(string name)
    {
        var closes = SyntheticCloses(300);
        var vols = closes.Select((_, i) => 50m + i % 20).ToList();
        var candles = MakeCandles(closes, vols);
        var factor = _factory.Create(name);
        var p = new Dictionary<string, decimal>();

        var full = factor.Compute(candles, p);

        // Per diversi punti di troncamento, il valore a `cut` deve essere IDENTICO a quello
        // calcolato sulla serie completa: prova che nessuna feature legge dati futuri.
        foreach (var cut in new[] { 100, 150, 220, 299 })
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
    public void AllFactors_ReturnSeriesAlignedToInput()
    {
        var candles = MakeCandles(SyntheticCloses(120));
        foreach (var proto in _factory.Prototypes)
        {
            var series = proto.Compute(candles, new Dictionary<string, decimal>());
            Assert.Equal(candles.Count, series.Count);
        }
    }

    [Fact]
    public void Factories_CreateAndPrototypes_AreConsistent()
    {
        foreach (var proto in _factory.Prototypes)
        {
            var created = _factory.Create(proto.Name);
            Assert.Equal(proto.Name, created.Name);
            Assert.NotEmpty(created.DisplayName);
        }
        Assert.Throws<NotSupportedException>(() => _factory.Create("DoesNotExist"));
    }

    // --- Correttezza del fattore -------------------------------------------------------------

    [Fact]
    public void Momentum_OnMonotonicSeries_IsPositive_AndWarmupIsNull()
    {
        // Prezzo strettamente crescente -> momentum sempre positivo dove definito.
        var closes = Enumerable.Range(0, 60).Select(i => 100m + i).ToList();
        var candles = MakeCandles(closes);
        var f = _factory.Create("Momentum");
        var p = new Dictionary<string, decimal> { ["Lookback"] = 10m, ["Skip"] = 0m };
        var v = f.Compute(candles, p);

        for (var i = 0; i < 10; i++) Assert.Null(v[i]);          // warm-up
        for (var i = 10; i < 60; i++) Assert.True(v[i] > 0m);     // trend up
    }

    [Fact]
    public void MeanReversion_IsNegative_WhenPriceAboveMean()
    {
        // Serie crescente: prezzo corrente sopra la media rolling -> z>0 -> fattore (-z) < 0.
        var closes = Enumerable.Range(0, 60).Select(i => 100m + i).ToList();
        var candles = MakeCandles(closes);
        var f = _factory.Create("MeanReversion");
        var v = f.Compute(candles, new Dictionary<string, decimal> { ["Lookback"] = 20m });
        Assert.True(v[59] < 0m);
    }

    [Fact]
    public void RsiFactor_StaysWithinMinusOneToOne()
    {
        var candles = MakeCandles(SyntheticCloses(200));
        var v = _factory.Create("RsiFactor").Compute(candles, new Dictionary<string, decimal>());
        foreach (var x in v.Where(x => x.HasValue))
        {
            Assert.InRange(x!.Value, -1m, 1m);
        }
    }

    // --- Forward returns & Information Coefficient -------------------------------------------

    [Fact]
    public void ForwardReturns_LastHorizonEntries_AreNull()
    {
        var candles = MakeCandles(SyntheticCloses(50));
        var fwd = _eval.ForwardReturns(candles, horizon: 5);
        for (var i = 0; i < 45; i++) Assert.True(fwd[i].HasValue);
        for (var i = 45; i < 50; i++) Assert.Null(fwd[i]);
    }

    [Fact]
    public void ForwardReturns_AreComputedCorrectly()
    {
        var closes = new List<decimal> { 100m, 110m, 121m, 133.1m };
        var candles = MakeCandles(closes);
        var fwd = _eval.ForwardReturns(candles, horizon: 1);
        Assert.Equal(0.10m, fwd[0]!.Value, 6);  // 110/100 - 1
        Assert.Equal(0.10m, fwd[1]!.Value, 6);  // 121/110 - 1
    }

    [Fact]
    public void InformationCoefficient_IsStronglyPositive_ForPredictiveFactor()
    {
        // Costruiamo un fattore che PREDICE il rendimento successivo: se il fattore alla
        // candela i è alto, il prezzo sale al passo i+1. Usiamo il momentum e una serie in cui
        // il momentum passato predice il rendimento futuro (trend persistente + rumore).
        var rnd = new Random(7);
        var n = 500;
        var closes = new List<decimal> { 100m };
        for (var i = 1; i < n; i++)
        {
            // Rendimento persistente: dipende dal segno del rendimento precedente (momentum reale).
            var prevRet = i >= 2 ? (double)(closes[i - 1] / closes[i - 2] - 1m) : 0.0;
            var drift = 0.5 * prevRet;                     // persistenza -> momentum predittivo
            var noise = (rnd.NextDouble() - 0.5) * 0.01;   // rumore
            var next = (double)closes[i - 1] * (1.0 + drift + noise);
            closes.Add((decimal)Math.Max(1.0, next));
        }
        var candles = MakeCandles(closes);
        var f = _factory.Create("Momentum");
        var res = _eval.Evaluate(f, candles,
            new Dictionary<string, decimal> { ["Lookback"] = 1m },
            new FactorEvaluationConfig { ForwardHorizon = 1, Quantiles = 5 });

        Assert.True(res.Observations > 100);
        // Il momentum a 1 barra deve mostrare IC positivo su una serie a momentum reale.
        Assert.True(res.InformationCoefficient > 0.1,
            $"IC atteso positivo per fattore predittivo, ottenuto {res.InformationCoefficient:F3}");
        // Monotonicità: il quantile alto rende più del quantile basso.
        Assert.True(res.TopMinusBottomSpread > 0m,
            $"Spread top-bottom atteso positivo, ottenuto {res.TopMinusBottomSpread}");
    }

    [Fact]
    public void InformationCoefficient_IsNearZero_ForRandomFactor()
    {
        // Fattore su serie puramente casuale (nessun momentum reale): IC ~ 0.
        var candles = MakeCandles(SyntheticCloses(600, seed: 123));
        var f = _factory.Create("Momentum");
        var res = _eval.Evaluate(f, candles,
            new Dictionary<string, decimal> { ["Lookback"] = 5m },
            new FactorEvaluationConfig { ForwardHorizon = 1, Quantiles = 5 });

        Assert.True(Math.Abs(res.InformationCoefficient) < 0.15,
            $"IC atteso vicino a zero su serie casuale, ottenuto {res.InformationCoefficient:F3}");
    }

    [Fact]
    public void Evaluate_ProducesQuantilesAndDecay()
    {
        var candles = MakeCandles(SyntheticCloses(400));
        var res = _eval.Evaluate(_factory.Create("RsiFactor"), candles,
            new Dictionary<string, decimal>(),
            new FactorEvaluationConfig { Quantiles = 5, DecayHorizons = [1, 3, 5] });

        Assert.Equal(5, res.QuantileReturns.Count);
        Assert.Equal(3, res.IcDecay.Count);
        // I quantili sono numerati 1..5 in ordine.
        Assert.Equal(1, res.QuantileReturns[0].Quantile);
        Assert.Equal(5, res.QuantileReturns[^1].Quantile);
    }

    // --- Correlation helper (Spearman) -------------------------------------------------------

    [Fact]
    public void Spearman_PerfectMonotonic_IsOne()
    {
        var x = new double[] { 1, 2, 3, 4, 5, 6, 7 };
        var y = new double[] { 10, 20, 25, 40, 55, 60, 90 }; // monotona crescente (non lineare)
        Assert.Equal(1.0, Correlation.Spearman(x, y), 6);
    }

    [Fact]
    public void Spearman_PerfectInverse_IsMinusOne()
    {
        var x = new double[] { 1, 2, 3, 4, 5 };
        var y = new double[] { 50, 40, 30, 20, 10 };
        Assert.Equal(-1.0, Correlation.Spearman(x, y), 6);
    }
}
