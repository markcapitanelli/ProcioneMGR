using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Regime;

namespace ProcioneMGR.Tests;

/// <summary>
/// [3.8a roadmap macchina-ricerca] OBV/MFI/VWAP riusabili + volume/breadth nei regimi.
///
/// Fissa: (a) la correttezza numerica dei tre indicatori contro conti a mano; (b) i due segnali
/// nuovi del catalogo (append-only, anti-look-ahead per troncamento); (c) la retro-compatibilità
/// del clustering — con i flag OFF i vettori sono IDENTICI a prima e un FeatureScaling salvato
/// senza "Names" deserializza alle 4 feature storiche.
/// </summary>
public class VolumeSignalsAndRegimeFeaturesTests
{
    // ------------------------------------------------------------------ OBV

    [Fact]
    public async Task Obv_MatchesHandComputedCumulativeSignedVolume()
    {
        // Chiusure 100→101→99→99→102 con volumi 10,20,30,40,50:
        // OBV = 0, +20, -10, -10 (invariato: prezzo fermo), +40.
        var svc = new TechnicalIndicatorsService();
        var obv = await svc.CalculateObvAsync([100m, 101m, 99m, 99m, 102m], [10m, 20m, 30m, 40m, 50m]);

        Assert.Equal([0m, 20m, -10m, -10m, 40m], obv.Select(v => v!.Value));
    }

    // ------------------------------------------------------------------ MFI

    [Fact]
    public async Task Mfi_ExtremesAndWarmup_BehaveLikeAVolumeWeightedRsi()
    {
        var svc = new TechnicalIndicatorsService();
        // Typical price sempre crescente ⇒ nessun flusso negativo ⇒ MFI = 100.
        var up = Enumerable.Range(1, 20).Select(i => (decimal)(100 + i)).ToList();
        var mfiUp = await svc.CalculateMfiAsync(up, up, up, Enumerable.Repeat(10m, 20).ToList(), 14);
        Assert.Null(mfiUp[13]);            // warm-up: servono 14 variazioni
        Assert.Equal(100m, mfiUp[14]);
        Assert.Equal(100m, mfiUp[^1]);

        // Sempre decrescente ⇒ MFI = 0.
        var down = Enumerable.Range(1, 20).Select(i => (decimal)(200 - i)).ToList();
        var mfiDown = await svc.CalculateMfiAsync(down, down, down, Enumerable.Repeat(10m, 20).ToList(), 14);
        Assert.Equal(0m, mfiDown[^1]);
    }

    [Fact]
    public async Task Mfi_WeighsByVolume_NotJustDirection()
    {
        var svc = new TechnicalIndicatorsService();
        // 7 barre su, 7 barre giù degli stessi importi, ma i giorni UP hanno volume doppio:
        // il flusso positivo domina ⇒ MFI > 50 (un RSI non pesato starebbe ~50).
        var closes = new List<decimal> { 100m };
        var volumes = new List<decimal> { 10m };
        for (var i = 0; i < 7; i++) { closes.Add(closes[^1] + 1m); volumes.Add(20m); }
        for (var i = 0; i < 7; i++) { closes.Add(closes[^1] - 1m); volumes.Add(10m); }

        var mfi = await svc.CalculateMfiAsync(closes, closes, closes, volumes, 14);

        Assert.NotNull(mfi[^1]);
        Assert.True(mfi[^1]!.Value > 55m, $"MFI atteso >55 coi volumi sbilanciati sui rialzi, trovato {mfi[^1]:F1}");
    }

    // ------------------------------------------------------------------ Rolling VWAP

    [Fact]
    public async Task RollingVwap_IsVolumeWeightedTypicalPrice_OverTheWindow()
    {
        var svc = new TechnicalIndicatorsService();
        // Typical = close (H=L=C). Finestra 2: VWAP[1] = (100·10 + 200·30)/40 = 175.
        var vwap = await svc.CalculateRollingVwapAsync(
            [100m, 200m, 300m], [100m, 200m, 300m], [100m, 200m, 300m], [10m, 30m, 10m], 2);

        Assert.Null(vwap[0]);
        Assert.Equal(175m, vwap[1]);
        Assert.Equal(225m, vwap[2]);   // (200·30 + 300·10)/40
    }

    // ------------------------------------------------------------------ SignalCatalog 10/11

    private static List<OhlcvData> TrendingCandles(int n)
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rng = new Random(42);
        var price = 100m;
        return Enumerable.Range(0, n).Select(i =>
        {
            price += (decimal)(rng.NextDouble() - 0.45);
            return new OhlcvData
            {
                Symbol = "VOL/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(i),
                Open = price, High = price + 0.5m, Low = price - 0.5m, Close = price,
                Volume = 100m + (decimal)(rng.NextDouble() * 50),
            };
        }).ToList();
    }

    [Fact]
    public async Task CatalogSignals_MfiNative_ObvSlopePercentile_AreCausalByTruncation()
    {
        var full = TrendingCandles(400);
        var truncated = full.Take(320).ToList();
        var svc = new TechnicalIndicatorsService();

        var mFull = await SignalCatalog.GetMatrixAsync(full, svc, CancellationToken.None);
        var mTrunc = await SignalCatalog.GetMatrixAsync(truncated, svc, CancellationToken.None);

        foreach (var id in new[] { 10, 11 })
        {
            for (var i = 0; i < truncated.Count; i++)
            {
                Assert.Equal(mFull[id][i], mTrunc[id][i]);
            }
        }

        // MFI nativo 0-100 dove definito.
        for (var i = 0; i < full.Count; i++)
        {
            if (mFull[10][i] is { } v) Assert.InRange(v, 0m, 100m);
        }
    }

    // ------------------------------------------------------------------ Regimi: opt-in e compatibilità

    [Fact]
    public void ClusteringVector_DefaultUnchanged_OptInAppends()
    {
        var f = new MarketFeatures
        {
            Volatility = 1m, TrendStrength = 2m, TrendDirection = 3m, DistanceFromMa = 4m,
            VolumeRatio = 5m, MarketBreadth = 0.8m,
        };

        Assert.Equal([1d, 2d, 3d, 4d], f.ToClusteringVector());                       // storico bit-identico
        Assert.Equal([1d, 2d, 3d, 4d, 5d], f.ToClusteringVector(includeVolume: true));
        Assert.Equal([1d, 2d, 3d, 4d, 0.8d], f.ToClusteringVector(includeBreadth: true));
        Assert.Equal([1d, 2d, 3d, 4d, 5d, 0.8d], f.ToClusteringVector(true, true));
    }

    [Fact]
    public void FeatureScaling_DeserializedWithoutNames_FallsBackToTheFourHistoricalFeatures()
    {
        // Un modello salvato PRIMA di 3.8a non ha "Names" nel FeatureScalingJson: il default
        // dell'oggetto deve essere le 4 storiche, così l'inference ricostruisce il vettore giusto.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<FeatureScaling>(
            """{"Means":[0,0,0,0],"Stds":[1,1,1,1]}""")!;

        Assert.Equal(FeatureScaling.FeatureNames, legacy.Names);
        Assert.False(legacy.Uses("VolumeRatio"));
        Assert.False(legacy.Uses("MarketBreadth"));
    }

    [Fact]
    public void NormalizeFeatures_WithFlags_ScalesTheExtraColumns_AndRecordsNames()
    {
        var features = Enumerable.Range(0, 10).Select(i => new MarketFeatures
        {
            Volatility = i, TrendStrength = i, TrendDirection = i, DistanceFromMa = i,
            VolumeRatio = i * 2, MarketBreadth = 0.1m * i,
        }).ToList();

        var (matrix, scaling) = FeatureNormalizer.NormalizeFeatures(features, includeVolume: true, includeBreadth: true);

        Assert.Equal(6, matrix[0].Length);
        Assert.Equal(["Volatility", "TrendStrength", "TrendDirection", "DistanceFromMa", "VolumeRatio", "MarketBreadth"], scaling.Names);
        Assert.True(scaling.Uses("VolumeRatio"));

        // E senza flag: 4 colonne, nomi storici (comportamento invariato).
        var (legacyMatrix, legacyScaling) = FeatureNormalizer.NormalizeFeatures(features);
        Assert.Equal(4, legacyMatrix[0].Length);
        Assert.Equal(FeatureScaling.FeatureNames, legacyScaling.Names);
    }
}
