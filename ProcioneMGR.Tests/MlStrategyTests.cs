using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test end-to-end della Fase A: dataset dai fattori -> addestramento -> <see cref="MlStrategy"/>
/// -> backtest reale via <see cref="BacktestEngine"/>. Chiude l'anello "modello addestrabile e
/// back-testabile" descritto nella roadmap (§3.8).
/// </summary>
public class MlStrategyTests
{
    /// <summary>Predittore fittizio a valore costante, per isolare la logica di soglia di MlStrategy.</summary>
    private sealed class FixedReturnPredictor(float value) : IReturnPredictor
    {
        public string Name => "Fixed";
        public bool IsFitted => true;
        public void Fit(MLContext mlContext, IDataView trainingData) { }
        public float Predict(float[] features) => value;
        public void Save(MLContext mlContext, string path) { }
        public void Load(MLContext mlContext, string path) { }
        public IReadOnlyList<FeatureImportance> ComputeFeatureImportance(MLContext mlContext, IDataView evaluationData, IReadOnlyList<string> featureNames) => [];
        public void Dispose() { }
    }

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

    private static List<decimal> SyntheticMomentumCloses(int n, int seed)
    {
        // Stessa costruzione di AlphaFactorTests.InformationCoefficient_IsStronglyPositive:
        // rendimento persistente -> il momentum passato predice il rendimento futuro.
        var rnd = new Random(seed);
        var closes = new List<decimal> { 100m };
        for (var i = 1; i < n; i++)
        {
            var prevRet = i >= 2 ? (double)(closes[i - 1] / closes[i - 2] - 1m) : 0.0;
            var drift = 0.5 * prevRet;
            var noise = (rnd.NextDouble() - 0.5) * 0.01;
            var next = (double)closes[i - 1] * (1.0 + drift + noise);
            closes.Add((decimal)Math.Max(1.0, next));
        }
        return closes;
    }

    private static BacktestEngine MakeEngine() =>
        new(null!, null!, new TechnicalIndicatorsService(), new AlphaFactorFactory(), NullLogger<BacktestEngine>.Instance);

    // --- Logica di soglia (predittore fittizio, isolata da ML.NET) ---------------------------

    [Fact]
    public async Task EvaluateSignal_AboveLongThreshold_ReturnsLong()
    {
        var predictor = new FixedReturnPredictor(0.02f);
        var factors = new List<FactorSpec> { new("Mom", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 5m }) };
        var strategy = new MlStrategy(predictor, factors);
        var candles = MakeCandles(Enumerable.Range(0, 30).Select(i => 100m + i).ToList());

        await strategy.InitializeAsync([], candles, new Dictionary<string, decimal> { ["LongThreshold"] = 0.01m, ["ShortThreshold"] = 0.01m }, new TechnicalIndicatorsService(), CancellationToken.None);

        Assert.Equal(Signal.Long, strategy.EvaluateSignal(20, 100m, candles[20].TimestampUtc));
    }

    [Fact]
    public async Task EvaluateSignal_BelowShortThreshold_ReturnsShort()
    {
        var predictor = new FixedReturnPredictor(-0.02f);
        var factors = new List<FactorSpec> { new("Mom", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 5m }) };
        var strategy = new MlStrategy(predictor, factors);
        var candles = MakeCandles(Enumerable.Range(0, 30).Select(i => 100m + i).ToList());

        await strategy.InitializeAsync([], candles, new Dictionary<string, decimal> { ["LongThreshold"] = 0.01m, ["ShortThreshold"] = 0.01m }, new TechnicalIndicatorsService(), CancellationToken.None);

        Assert.Equal(Signal.Short, strategy.EvaluateSignal(20, 100m, candles[20].TimestampUtc));
    }

    [Fact]
    public async Task EvaluateSignal_DuringWarmup_ReturnsHold()
    {
        var predictor = new FixedReturnPredictor(0.5f); // valore enorme: se non fosse warm-up sarebbe Long
        var factors = new List<FactorSpec> { new("Mom", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 10m }) };
        var strategy = new MlStrategy(predictor, factors);
        var candles = MakeCandles(Enumerable.Range(0, 30).Select(i => 100m + i).ToList());

        await strategy.InitializeAsync([], candles, new Dictionary<string, decimal>(), new TechnicalIndicatorsService(), CancellationToken.None);

        // Momentum(Lookback=10) e' null per i primi 10 indici -> Hold indipendentemente dalla predizione.
        Assert.Equal(Signal.Hold, strategy.EvaluateSignal(5, 100m, candles[5].TimestampUtc));
    }

    [Fact]
    public async Task InitializeAsync_UnfittedPredictor_Throws()
    {
        var predictor = new LinearReturnPredictor(); // mai addestrato
        var factors = new List<FactorSpec> { new("Mom", new MomentumFactor(), new Dictionary<string, decimal>()) };
        var strategy = new MlStrategy(predictor, factors);
        var candles = MakeCandles(SyntheticMomentumCloses(50, 1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            strategy.InitializeAsync([], candles, new Dictionary<string, decimal>(), new TechnicalIndicatorsService(), CancellationToken.None));
    }

    // --- End-to-end: dataset -> training -> backtest reale -------------------------------------

    [Fact]
    public async Task EndToEnd_TrainedPredictor_ProducesTradesThroughRealBacktestEngine()
    {
        var closes = SyntheticMomentumCloses(600, seed: 11);
        var candles = MakeCandles(closes);

        var factors = new List<FactorSpec> { new("Mom1", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 1m, ["Skip"] = 0m }) };
        var datasetBuilder = new DatasetBuilder();
        var dataset = datasetBuilder.Build(candles, factors, forwardHorizon: 1);

        var mlContext = new MLContext(seed: 1);
        var predictor = new LinearReturnPredictor();
        predictor.Fit(mlContext, dataset.ToDataView(mlContext));
        Assert.True(predictor.IsFitted);

        var strategy = new MlStrategy(predictor, factors);
        var engine = MakeEngine();
        var config = new BacktestConfiguration
        {
            Symbol = "TEST/USDT",
            Timeframe = "1h",
            InitialCapital = 10_000m,
            PositionSizePercent = 20m,
            FeePercent = 0.05m,
            StrategyName = "Ml",
            StrategyParameters = new Dictionary<string, decimal> { ["LongThreshold"] = 0.001m, ["ShortThreshold"] = 0.001m },
        };

        var result = await engine.RunBacktestAsync(config, candles, strategy, CancellationToken.None);

        Assert.Equal(candles.Count, result.CandlesEvaluated);
        Assert.Equal(candles.Count, result.EquityCurve.Count);
        Assert.True(result.FinalCapital > 0m);
        // La serie e' costruita apposta con momentum persistente/predittivo: il modello lineare
        // addestrato su di essa deve produrre almeno qualche trade (non e' sempre Hold).
        Assert.True(result.TotalTrades > 0, "Il modello addestrato non ha generato alcun trade");
    }
}
