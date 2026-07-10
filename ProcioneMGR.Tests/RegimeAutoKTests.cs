using Microsoft.ML;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Regime;

namespace ProcioneMGR.Tests;

/// <summary>
/// R1.2 — robustezza del rilevamento regimi: auto-selezione di K per Silhouette (senza DB, solo
/// ML.NET su matrice sintetica) e invariante anti-look-ahead del feature extractor (la feature alla
/// candela i è identica sia sull'intera serie sia su una serie troncata dopo i).
/// </summary>
public class RegimeAutoKTests
{
    // ------------------------------------------------------------------ auto-K

    private static float[][] MakeBlobs(int clusters, int perCluster, int dim, int seed)
    {
        var rnd = new Random(seed);
        var rows = new List<float[]>();
        for (var c = 0; c < clusters; c++)
        {
            // Centro ben separato per cluster: (c*20, c*20, ...) con segno alternato per spargere.
            var center = new float[dim];
            for (var d = 0; d < dim; d++) center[d] = (c + 1) * 20f * (d % 2 == 0 ? 1 : -1);
            for (var p = 0; p < perCluster; p++)
            {
                var row = new float[dim];
                for (var d = 0; d < dim; d++) row[d] = center[d] + (float)(rnd.NextDouble() - 0.5); // rumore << separazione
                rows.Add(row);
            }
        }
        return rows.ToArray();
    }

    [Fact]
    public void SelectBestK_PicksTrueClusterCount_OnWellSeparatedBlobs()
    {
        var matrix = MakeBlobs(clusters: 3, perCluster: 60, dim: 4, seed: 7);
        var ml = new MLContext(seed: 1);

        var (bestK, centroids, silhouette, scores) =
            RegimeDetector.SelectBestK(ml, matrix, minK: 2, maxK: 6, maxIterations: 100);

        Assert.Equal(3, bestK);
        Assert.Equal(3, centroids.Length);
        Assert.True(silhouette > 0.5, $"silhouette={silhouette}");
        Assert.Equal(5, scores.Count); // K=2..6
        Assert.All(scores, s => Assert.InRange(s.Silhouette, -1.0, 1.0001));
    }

    // ------------------------------------------------------------------ no look-ahead

    private static List<OhlcvData> SyntheticCandles(int n, int seed)
    {
        var rnd = new Random(seed);
        var list = new List<OhlcvData>(n);
        var price = 100m;
        var t0 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            var drift = (decimal)((rnd.NextDouble() - 0.48) * 1.5); // random walk con lieve deriva
            var open = price;
            price = Math.Max(1m, price + drift);
            var high = Math.Max(open, price) + (decimal)(rnd.NextDouble() * 0.5);
            var low = Math.Min(open, price) - (decimal)(rnd.NextDouble() * 0.5);
            list.Add(new OhlcvData
            {
                Symbol = "TST/USDT",
                Timeframe = "1h",
                TimestampUtc = t0.AddHours(i),
                Open = open,
                High = high,
                Low = Math.Max(0.5m, low),
                Close = price,
                Volume = 100m + (decimal)(rnd.NextDouble() * 50),
            });
        }
        return list;
    }

    [Fact]
    public void ComputeFeatures_IsCausal_TruncationInvariant()
    {
        // dbFactory non è usato da ComputeFeatures → null è sicuro.
        var extractor = new MarketFeatureExtractor(null!, new TechnicalIndicatorsService());
        var candles = SyntheticCandles(160, seed: 11);

        var full = extractor.ComputeFeatures(candles, "1h");
        var truncated = extractor.ComputeFeatures(candles.Take(120).ToList(), "1h");

        Assert.NotEmpty(truncated);
        var fullByTs = full.ToDictionary(f => f.Timestamp);

        // Ogni feature calcolata sulla serie troncata deve coincidere con quella sulla serie intera
        // (stesso timestamp): prova che nessuna feature legge dati futuri.
        foreach (var t in truncated)
        {
            Assert.True(fullByTs.TryGetValue(t.Timestamp, out var f), $"timestamp {t.Timestamp} mancante nella serie intera");
            Assert.Equal((double)f!.Volatility, (double)t.Volatility, precision: 10);
            Assert.Equal((double)f.TrendStrength, (double)t.TrendStrength, precision: 10);
            Assert.Equal((double)f.TrendDirection, (double)t.TrendDirection, precision: 10);
            Assert.Equal((double)f.DistanceFromMa, (double)t.DistanceFromMa, precision: 10);
            Assert.Equal((double)f.AtrNormalized, (double)t.AtrNormalized, precision: 10);
            Assert.Equal((double)f.RsiLevel, (double)t.RsiLevel, precision: 10);
            Assert.Equal((double)f.VolumeRatio, (double)t.VolumeRatio, precision: 10);
        }
    }
}
