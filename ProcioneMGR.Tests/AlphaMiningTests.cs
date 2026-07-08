using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.AlphaMining;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del formulaic alpha mining (rif. <c>docs/ROADMAP-QLIB.md §1.7</c>): gli alberi di espressione
/// sono anti-look-ahead per costruzione, la serializzazione fa round-trip, i fattori minati si
/// ricreano dal nome tramite la factory esistente, e il miner genetico è deterministico e trova un
/// segnale su una serie con momentum reale.
/// </summary>
public class AlphaMiningTests
{
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
                Symbol = "TEST", Timeframe = "1h", TimestampUtc = t0.AddHours(i),
                Open = prev, High = Math.Max(prev, c) * 1.01m, Low = Math.Min(prev, c) * 0.99m, Close = c,
                Volume = 100m + i % 25,
            });
        }
        return list;
    }

    /// <summary>Serie con momentum reale: il rendimento dipende dal segno del precedente.</summary>
    private static List<decimal> MomentumCloses(int n, int seed = 7)
    {
        var rnd = new Random(seed);
        var closes = new List<decimal> { 100m };
        for (var i = 1; i < n; i++)
        {
            var prevRet = i >= 2 ? (double)(closes[i - 1] / closes[i - 2] - 1m) : 0.0;
            var drift = 0.5 * prevRet;
            var noise = (rnd.NextDouble() - 0.5) * 0.01;
            closes.Add((decimal)Math.Max(1.0, (double)closes[i - 1] * (1.0 + drift + noise)));
        }
        return closes;
    }

    // --- Espressione: anti-look-ahead + serializzazione -------------------------------------

    [Fact]
    public void ExpressionTree_IsAntiLookAhead()
    {
        // z-score = (Close - Mean(Close,5)) / Std(Close,20)
        var expr = AlphaNode.Binary(AlphaOp.Div,
            AlphaNode.Binary(AlphaOp.Sub, AlphaNode.Variable("Close"), AlphaNode.TimeUnary(AlphaOp.Mean, AlphaNode.Variable("Close"), 5)),
            AlphaNode.TimeUnary(AlphaOp.Std, AlphaNode.Variable("Close"), 20));

        var candles = MakeCandles(MomentumCloses(300));
        var full = expr.Evaluate(candles);
        foreach (var cut in new[] { 100, 180, 260, 299 })
        {
            var truncated = expr.Evaluate(candles.Take(cut + 1).ToList());
            Assert.Equal(full[cut].HasValue, truncated[cut].HasValue);
            if (full[cut].HasValue) Assert.Equal(full[cut]!.Value, truncated[cut]!.Value);
        }
    }

    [Fact]
    public void Serialization_RoundTrips()
    {
        var expr = AlphaNode.Binary(AlphaOp.Div,
            AlphaNode.TimeUnary(AlphaOp.Delta, AlphaNode.Variable("Close"), 10),
            AlphaNode.TimeBinary(AlphaOp.Corr, AlphaNode.Variable("Close"), AlphaNode.Variable("Volume"), 20));

        var text = expr.ToExpression();
        var parsed = AlphaExpressionParser.Parse(text);
        Assert.Equal(text, parsed.ToExpression());

        var candles = MakeCandles(MomentumCloses(200));
        var a = expr.Evaluate(candles);
        var b = parsed.Evaluate(candles);
        for (var i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void MinedFactor_RoundTripsThrough_SavedFactorSpecDto()
    {
        // Percorso ESATTO di /ml: un fattore minato usato come feature viene persistito come
        // SavedFactorSpecDto(FeatureName, FactorName="expr:…", Parameters) e ricostruito al Load.
        var expr = AlphaNode.Binary(AlphaOp.Div, AlphaNode.Variable("Close"), AlphaNode.TimeUnary(AlphaOp.Mean, AlphaNode.Variable("Close"), 5));
        var factor = new AlphaExpressionFactor(expr);
        var dto = new ProcioneMGR.Services.ML.SavedFactorSpecDto("mined_1", factor.Name, new Dictionary<string, decimal>());

        var json = System.Text.Json.JsonSerializer.Serialize(new List<ProcioneMGR.Services.ML.SavedFactorSpecDto> { dto });
        var back = System.Text.Json.JsonSerializer.Deserialize<List<ProcioneMGR.Services.ML.SavedFactorSpecDto>>(json)![0];

        var recreated = new AlphaFactorFactory().Create(back.FactorName);
        var candles = MakeCandles(MomentumCloses(120));
        var a = factor.Compute(candles, new Dictionary<string, decimal>());
        var b = recreated.Compute(candles, new Dictionary<string, decimal>());
        for (var i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void MinedFactor_RoundTripsThroughAlphaFactory()
    {
        var expr = AlphaNode.Binary(AlphaOp.Sub, AlphaNode.Variable("Close"), AlphaNode.TimeUnary(AlphaOp.Mean, AlphaNode.Variable("Close"), 10));
        var factor = new AlphaExpressionFactor(expr);

        var recreated = new AlphaFactorFactory().Create(factor.Name);
        Assert.Equal(factor.Name, recreated.Name);

        var candles = MakeCandles(MomentumCloses(150));
        var a = factor.Compute(candles, new Dictionary<string, decimal>());
        var b = recreated.Compute(candles, new Dictionary<string, decimal>());
        for (var i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i]);
    }

    // --- Miner genetico ---------------------------------------------------------------------

    private static MiningConfig SmallConfig(int seed = 42) => new()
    {
        PopulationSize = 80,
        Generations = 8,
        MaxDepth = 4,
        TopN = 10,
        ForwardHorizon = 1,
        MinObservations = 50,
        Seed = seed,
    };

    [Fact]
    public void Miner_FindsPredictiveFactor_OnMomentumSeries()
    {
        var candles = MakeCandles(MomentumCloses(500));
        var miner = new GeneticAlphaMiner();
        var mined = miner.Mine(candles, SmallConfig());

        Assert.NotEmpty(mined);
        var best = mined[0];
        Assert.True(best.Observations >= 50);
        Assert.True(Math.Abs(best.SelectionIc) > 0.03,
            $"Il miner dovrebbe trovare un segnale su una serie a momentum reale, |IC|={Math.Abs(best.SelectionIc):F3}");

        // Consistenza: il fattore migliore, ricostruito dall'espressione, riproduce l'IC dichiarato.
        var node = AlphaExpressionParser.Parse(best.Expression);
        var ic = miner.EvaluateIc(node, candles, horizon: 1, minObs: 50, out var obs);
        Assert.Equal(best.SelectionIc, ic, 6);
        Assert.Equal(best.Observations, obs);
    }

    [Fact]
    public void Miner_IsDeterministic_ForSameSeed()
    {
        var candles = MakeCandles(MomentumCloses(400));
        var a = new GeneticAlphaMiner().Mine(candles, SmallConfig(seed: 123));
        var b = new GeneticAlphaMiner().Mine(candles, SmallConfig(seed: 123));

        Assert.Equal(a[0].Expression, b[0].Expression);
        Assert.Equal(a[0].SelectionIc, b[0].SelectionIc, 10);
    }

    // --- PBO sul pannello di formule (Fase 1) -----------------------------------------------

    [Fact]
    public void ComputeSelectionPbo_OnMinedPanel_IsValidProbability_AndDeterministic()
    {
        var candles = MakeCandles(MomentumCloses(500));
        var miner = new GeneticAlphaMiner();
        var expressions = miner.Mine(candles, SmallConfig()).Select(m => m.Expression).ToList();

        var pbo1 = miner.ComputeSelectionPbo(candles, expressions, horizon: 1);
        var pbo2 = miner.ComputeSelectionPbo(candles, expressions, horizon: 1);

        Assert.NotNull(pbo1);
        Assert.InRange(pbo1!.ProbabilityOfBacktestOverfitting, 0.0, 1.0);
        Assert.Equal(252, pbo1.Combinations); // C(10,5) con 10 partizioni di default
        Assert.Equal(pbo1.ProbabilityOfBacktestOverfitting, pbo2!.ProbabilityOfBacktestOverfitting); // deterministico
    }

    [Fact]
    public void ComputeSelectionPbo_FewerThanTwoFactors_ReturnsNull()
    {
        var candles = MakeCandles(MomentumCloses(300));
        var miner = new GeneticAlphaMiner();
        Assert.Null(miner.ComputeSelectionPbo(candles, new[] { "Mean($Close,5)" }, horizon: 1));
    }
}
