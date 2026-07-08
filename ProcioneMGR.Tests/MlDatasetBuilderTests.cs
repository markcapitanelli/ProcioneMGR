using Microsoft.ML;
using Microsoft.ML.Data;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="DatasetBuilder"/>: allineamento feature/target, scarto delle righe
/// incomplete (warm-up/coda), e correttezza della conversione a IDataView di ML.NET.
/// </summary>
public class MlDatasetBuilderTests
{
    private readonly IDatasetBuilder _builder = new DatasetBuilder();

    private static List<OhlcvData> MakeCandles(IReadOnlyList<decimal> closes)
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
                Volume = 100m,
            });
        }
        return list;
    }

    private static List<decimal> SyntheticCloses(int n, int seed = 1)
    {
        var rnd = new Random(seed);
        var closes = new List<decimal>(n);
        var price = 100m;
        for (var i = 0; i < n; i++)
        {
            price = Math.Max(1m, price + (decimal)(rnd.NextDouble() - 0.5));
            closes.Add(price);
        }
        return closes;
    }

    [Fact]
    public void Build_DropsWarmupAndTailRows_KeepsOnlyCompleteRows()
    {
        var candles = MakeCandles(SyntheticCloses(120));
        var factors = new List<FactorSpec>
        {
            new("Momentum10", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 10m, ["Skip"] = 0m }),
        };

        var dataset = _builder.Build(candles, factors, forwardHorizon: 5);

        // Momentum(10) e' null per i primi 10 indici; ForwardReturns(5) e' null per gli ultimi 5.
        // Righe complete attese: 120 - 10 (warmup) - 5 (coda) = 105.
        Assert.Equal(105, dataset.RowCount);
        Assert.Equal(candles[10].TimestampUtc, dataset.Timestamps[0]);
        Assert.Equal(candles[114].TimestampUtc, dataset.Timestamps[^1]);
    }

    [Fact]
    public void Build_MultipleFactors_FeatureVectorMatchesFactorCount()
    {
        var candles = MakeCandles(SyntheticCloses(150));
        var factors = new List<FactorSpec>
        {
            new("Momentum10", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 10m }),
            new("Rsi14", new RsiFactor(), new Dictionary<string, decimal> { ["Period"] = 14m }),
            new("Vol20", new RealizedVolatilityFactor(), new Dictionary<string, decimal> { ["Lookback"] = 20m }),
        };

        var dataset = _builder.Build(candles, factors, forwardHorizon: 3);

        Assert.Equal(3, dataset.FeatureCount);
        Assert.Equal(["Momentum10", "Rsi14", "Vol20"], dataset.FeatureNames);
        Assert.All(dataset.Rows, r => Assert.Equal(3, r.Features.Length));
    }

    [Fact]
    public void Build_RowsAreInTemporalOrder()
    {
        var candles = MakeCandles(SyntheticCloses(100));
        var factors = new List<FactorSpec> { new("Mom", new MomentumFactor(), new Dictionary<string, decimal>()) };
        var dataset = _builder.Build(candles, factors, forwardHorizon: 2);

        for (var i = 1; i < dataset.Timestamps.Count; i++)
        {
            Assert.True(dataset.Timestamps[i] > dataset.Timestamps[i - 1]);
        }
    }

    [Fact]
    public void Build_LabelMatchesForwardReturn_Directly()
    {
        // Serie nota: close raddoppia ogni step -> forward return a 1 step sempre +100%.
        var closes = new List<decimal> { 100m, 200m, 400m, 800m, 1600m };
        var candles = MakeCandles(closes);
        var factors = new List<FactorSpec>
        {
            new("Mom1", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 1m, ["Skip"] = 0m }),
        };
        var dataset = _builder.Build(candles, factors, forwardHorizon: 1);

        Assert.All(dataset.Rows, r => Assert.Equal(1.0f, r.Label, 3));
    }

    [Fact]
    public void ToDataView_ProducesCorrectSchemaAndRowCount()
    {
        var candles = MakeCandles(SyntheticCloses(200));
        var factors = new List<FactorSpec>
        {
            new("Mom", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 10m }),
            new("Rsi", new RsiFactor(), new Dictionary<string, decimal>()),
        };
        var dataset = _builder.Build(candles, factors, forwardHorizon: 2);
        var mlContext = new MLContext(seed: 1);

        var dataView = dataset.ToDataView(mlContext);
        var featuresColumn = dataView.Schema["Features"];
        Assert.Equal(2, ((VectorDataViewType)featuresColumn.Type).Size);

        var loaded = mlContext.Data.CreateEnumerable<FeatureRow>(dataView, reuseRowObject: false).ToList();
        Assert.Equal(dataset.RowCount, loaded.Count);
    }

    [Fact]
    public void ToDataView_WithIndices_SelectsOnlyRequestedRows()
    {
        var candles = MakeCandles(SyntheticCloses(200));
        var factors = new List<FactorSpec> { new("Mom", new MomentumFactor(), new Dictionary<string, decimal>()) };
        var dataset = _builder.Build(candles, factors, forwardHorizon: 2);
        var mlContext = new MLContext(seed: 1);

        var indices = new[] { 0, 5, 10 };
        var dataView = dataset.ToDataView(mlContext, indices);
        var loaded = mlContext.Data.CreateEnumerable<FeatureRow>(dataView, reuseRowObject: false).ToList();

        Assert.Equal(3, loaded.Count);
        Assert.Equal(dataset.Rows[0].Label, loaded[0].Label);
        Assert.Equal(dataset.Rows[5].Label, loaded[1].Label);
        Assert.Equal(dataset.Rows[10].Label, loaded[2].Label);
    }

    [Fact]
    public void Build_NoFactors_Throws()
    {
        var candles = MakeCandles(SyntheticCloses(10));
        Assert.Throws<ArgumentException>(() => _builder.Build(candles, new List<FactorSpec>(), 1));
    }

    [Fact]
    public void Build_InvalidHorizon_Throws()
    {
        var candles = MakeCandles(SyntheticCloses(10));
        var factors = new List<FactorSpec> { new("Mom", new MomentumFactor(), new Dictionary<string, decimal>()) };
        Assert.Throws<ArgumentOutOfRangeException>(() => _builder.Build(candles, factors, 0));
    }
}
