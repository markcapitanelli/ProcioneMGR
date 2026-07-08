using Microsoft.ML;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Regime;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del regime one-hot appeso al vettore di feature (follow-up "regime nel meta-learner
/// dello stacking"). Copre: (a) regressione — feature disattivata ⇒ comportamento identico;
/// (b) dimensione +K quando attiva; (c) anti-look-ahead — candele future non cambiano l'etichetta
/// di un punto passato; (d) parità train/serve — <see cref="DatasetBuilder"/> (batch) e
/// <see cref="MlStrategy"/> (streaming) producono lo STESSO vettore aumentato sulla stessa serie.
/// </summary>
public class RegimeAugmentationTests
{
    // ---- Fakes --------------------------------------------------------------------------------

    /// <summary>Predittore che registra i vettori di input ricevuti (per ispezionare l'inferenza).</summary>
    private sealed class CapturingPredictor : IReturnPredictor
    {
        public List<float[]> Inputs { get; } = new();
        public string Name => "Capture";
        public bool IsFitted => true;
        public void Fit(MLContext mlContext, Microsoft.ML.IDataView trainingData) { }
        public float Predict(float[] features) { Inputs.Add((float[])features.Clone()); return 0f; }
        public void Save(MLContext mlContext, string path) { }
        public void Load(MLContext mlContext, string path) { }
        public IReadOnlyList<FeatureImportance> ComputeFeatureImportance(MLContext mlContext, Microsoft.ML.IDataView evaluationData, IReadOnlyList<string> featureNames) => [];
        public void Dispose() { }
    }

    /// <summary>
    /// Detector fittizio ma CAUSALE: l'etichetta di ogni punto dipende SOLO dalla feature di quel
    /// punto (già anti-look-ahead), quindi le candele future non possono cambiarla — esattamente
    /// l'invariante che vogliamo verificare, senza dover addestrare un K-means reale.
    /// </summary>
    private sealed class CausalFakeDetector(int k) : IRegimeDetector
    {
        private int RegimeOf(MarketFeatures f)
        {
            var d = f.TrendDirection;
            var r = d > 0m ? 1 : d < 0m ? 2 : 0;
            return r % k;
        }

        public Task<int> PredictRegimeAsync(MarketFeatures features, CancellationToken ct = default) => Task.FromResult(RegimeOf(features));
        public Task<List<MarketFeatures>> LabelFeaturesAsync(List<MarketFeatures> features, CancellationToken ct = default)
        {
            foreach (var f in features) f.RegimeId = RegimeOf(f);
            return Task.FromResult(features);
        }
        public Task<RegimeModel> TrainAsync(TrainingConfiguration config, bool activate = true, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ActivateModelAsync(RegimeModel model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RegimeModel?> LoadLatestModelAsync(CancellationToken ct = default) => Task.FromResult<RegimeModel?>(null);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static List<OhlcvData> MakeCandles(int n, int seed = 7)
    {
        var rnd = new Random(seed);
        var list = new List<OhlcvData>(n);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var price = 100m;
        for (var i = 0; i < n; i++)
        {
            var prev = price;
            price = Math.Max(1m, price + (decimal)(rnd.NextDouble() - 0.48));
            list.Add(new OhlcvData
            {
                Symbol = "TEST/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(i),
                Open = prev, High = Math.Max(prev, price) * 1.01m, Low = Math.Min(prev, price) * 0.99m,
                Close = price, Volume = 100m,
            });
        }
        return list;
    }

    private static MarketFeatureExtractor Extractor() => new(null!, new TechnicalIndicatorsService());

    private static List<FactorSpec> Factors() =>
    [
        new("Momentum10", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 10m, ["Skip"] = 0m }),
    ];

    // ---- (helper) -----------------------------------------------------------------------------

    [Fact]
    public void Append_SetsSingleOneHotColumn_UnknownIsAllZeros()
    {
        var baseVec = new[] { 1f, 2f };

        var r1 = RegimeAugmentation.Append(baseVec, regimeId: 1, regimeCount: 3);
        Assert.Equal([1f, 2f, 0f, 1f, 0f], r1);

        var unknown = RegimeAugmentation.Append(baseVec, regimeId: -1, regimeCount: 3);
        Assert.Equal([1f, 2f, 0f, 0f, 0f], unknown);   // regime sconosciuto/warm-up: nessuna colonna accesa

        var outOfRange = RegimeAugmentation.Append(baseVec, regimeId: 9, regimeCount: 3);
        Assert.Equal([1f, 2f, 0f, 0f, 0f], outOfRange);
    }

    [Fact]
    public void Append_DisabledOrEmpty_ReturnsInputUnchanged()
    {
        var baseVec = new[] { 1f, 2f };
        Assert.Same(baseVec, RegimeAugmentation.Append(baseVec, 0, regimeCount: 0));   // K<=0: no-op
        Assert.Same(baseVec, RegimeAugmentation.Append(baseVec, 0, regimeCount: -1));
        var empty = Array.Empty<float>();
        Assert.Same(empty, RegimeAugmentation.Append(empty, 0, regimeCount: 3));       // vettore vuoto (warm-up)
    }

    // ---- (a) regressione ----------------------------------------------------------------------

    [Fact]
    public void DatasetBuilder_Default_IsBitIdenticalToBefore()
    {
        var candles = MakeCandles(150);
        var factors = Factors();

        var baseline = new DatasetBuilder().Build(candles, factors, forwardHorizon: 3);
        var withNull = new DatasetBuilder().Build(candles, factors, forwardHorizon: 3, regimeIds: null, regimeCount: 0);

        Assert.Equal(1, baseline.FeatureCount);                  // solo il fattore, nessuna colonna regime
        Assert.Equal(baseline.FeatureCount, withNull.FeatureCount);
        Assert.Equal(baseline.RowCount, withNull.RowCount);
        Assert.All(withNull.Rows, r => Assert.Single(r.Features));
    }

    // ---- (b) dimensione +K --------------------------------------------------------------------

    [Fact]
    public async Task DatasetBuilder_WithRegime_AppendsKOneHotColumns()
    {
        var candles = MakeCandles(200);
        var factors = Factors();
        const int k = 3;
        var regimeIds = await RegimeAugmentation.LabelByCandleAsync(Extractor(), new CausalFakeDetector(k), candles, "1h");

        var dataset = new DatasetBuilder().Build(candles, factors, forwardHorizon: 3, regimeIds, k);

        Assert.Equal(1 + k, dataset.FeatureCount);
        Assert.Equal(["Momentum10", "Regime_0", "Regime_1", "Regime_2"], dataset.FeatureNames);
        Assert.All(dataset.Rows, r => Assert.Equal(1 + k, r.Features.Length));
        // Ogni riga ha esattamente 0 o 1 colonna regime accesa (one-hot valido).
        Assert.All(dataset.Rows, r => Assert.True(r.Features[1] + r.Features[2] + r.Features[3] <= 1f));
    }

    [Fact]
    public void DatasetBuilder_RegimeIdsMisaligned_Throws()
    {
        var candles = MakeCandles(120);
        var factors = Factors();
        var wrongLength = new int[candles.Count - 1];
        Assert.Throws<ArgumentException>(() => new DatasetBuilder().Build(candles, factors, 3, wrongLength, regimeCount: 3));
    }

    // ---- (c) anti-look-ahead ------------------------------------------------------------------

    [Fact]
    public async Task LabelByCandle_FutureCandlesDoNotChangePastRegime()
    {
        var candles = MakeCandles(200);
        var k = 3;

        var full = await RegimeAugmentation.LabelByCandleAsync(Extractor(), new CausalFakeDetector(k), candles, "1h");

        // Altera PESANTEMENTE le ultime 20 candele (futuro rispetto ai punti passati).
        var perturbed = candles.Select(c => new OhlcvData
        {
            Symbol = c.Symbol, Timeframe = c.Timeframe, TimestampUtc = c.TimestampUtc,
            Open = c.Open, High = c.High, Low = c.Low, Close = c.Close, Volume = c.Volume,
        }).ToList();
        for (var i = perturbed.Count - 20; i < perturbed.Count; i++)
        {
            perturbed[i].Close *= 5m; perturbed[i].High *= 5m; perturbed[i].Low *= 5m;
        }
        var afterPerturb = await RegimeAugmentation.LabelByCandleAsync(Extractor(), new CausalFakeDetector(k), perturbed, "1h");

        // I regimi dei punti PASSATI (fino a 30 candele prima della perturbazione) sono invariati.
        for (var i = 0; i < candles.Count - 30; i++)
        {
            Assert.Equal(full[i], afterPerturb[i]);
        }
    }

    // ---- (d) parità train/serve ---------------------------------------------------------------

    [Fact]
    public async Task TrainServe_Parity_DatasetAndMlStrategyProduceSameAugmentedVector()
    {
        var candles = MakeCandles(200);
        var closes = candles.Select(c => c.Close).ToList();
        var factors = Factors();
        const int k = 3;

        var regimeIds = await RegimeAugmentation.LabelByCandleAsync(Extractor(), new CausalFakeDetector(k), candles, "1h");
        var dataset = new DatasetBuilder().Build(candles, factors, forwardHorizon: 3, regimeIds, k);

        // Serve: MlStrategy con lo STESSO detector+extractor+K, che ricalcola i regimi internamente.
        var predictor = new CapturingPredictor();
        var strategy = new MlStrategy(predictor, factors, factorCache: null,
            regimeDetector: new CausalFakeDetector(k), featureExtractor: Extractor(), regimeCount: k);
        var parms = new Dictionary<string, decimal> { ["LongThreshold"] = 0.002m, ["ShortThreshold"] = 0.002m };
        await strategy.InitializeAsync(closes, candles, parms, new TechnicalIndicatorsService(), CancellationToken.None);

        // Candela 120: completa sia per il fattore (warm-up 10) sia per il regime (warm-up extractor 50).
        const int candleIdx = 120;
        strategy.EvaluateSignal(candleIdx, candles[candleIdx].Close, candles[candleIdx].TimestampUtc);
        var serveVec = Assert.Single(predictor.Inputs);

        // Riga del dataset per lo stesso timestamp.
        var ts = candles[candleIdx].TimestampUtc;
        var j = dataset.Timestamps.ToList().FindIndex(t => t == ts);
        Assert.True(j >= 0, "La candela scelta deve avere una riga nel dataset.");
        var trainVec = dataset.Rows[j].Features;

        Assert.Equal(1 + k, serveVec.Length);
        Assert.Equal(trainVec.Length, serveVec.Length);
        for (var c = 0; c < trainVec.Length; c++)
        {
            Assert.Equal(trainVec[c], serveVec[c], 5);   // stessi fattori E stessa colonna regime one-hot
        }
    }

    [Fact]
    public void MlStrategy_RegimeWithSequencePredictor_ThrowsAtConstruction()
    {
        // Guardia esplicita: il regime one-hot non è compatibile con i predittori sequenziali
        // (la finestra impacchetta i soli fattori). Deve fallire alla costruzione, non corrompere.
        var seqPredictor = new SequenceCapturingPredictor();
        Assert.Throws<NotSupportedException>(() => new MlStrategy(
            seqPredictor, Factors(), factorCache: null,
            regimeDetector: new CausalFakeDetector(3), featureExtractor: Extractor(), regimeCount: 3));
    }

    /// <summary>Predittore sequenziale minimale per verificare la guardia di incompatibilità.</summary>
    private sealed class SequenceCapturingPredictor : IReturnPredictor, ISequencePredictor
    {
        public int WindowLength => 4;
        public int FeaturesPerStep => 1;
        public string Name => "Seq";
        public bool IsFitted => true;
        public void Fit(MLContext mlContext, Microsoft.ML.IDataView trainingData) { }
        public float Predict(float[] features) => 0f;
        public void Save(MLContext mlContext, string path) { }
        public void Load(MLContext mlContext, string path) { }
        public IReadOnlyList<FeatureImportance> ComputeFeatureImportance(MLContext mlContext, Microsoft.ML.IDataView evaluationData, IReadOnlyList<string> featureNames) => [];
        public void Dispose() { }
    }
}
