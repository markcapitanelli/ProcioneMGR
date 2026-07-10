using ProcioneMGR.Data;
using ProcioneMGR.Services.AlphaMining;

namespace ProcioneMGR.Tests;

/// <summary>
/// E1 — miner genetico: fitness CROSS-VALIDATA (l'IC misurato su fold temporali, la fitness premia
/// consistenza non un |IC| di finestra unica gonfiabile) + gate PBO BLOCCANTE (se la selezione è
/// complessivamente overfit il batch viene svuotato). Verifica la discriminazione della CV su serie
/// sintetiche e il meccanismo di blocco deterministico.
/// </summary>
public class GeneticMinerCvGateTests
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

    // --- Fitness cross-validata ----------------------------------------------------------------

    [Fact]
    public void CrossValidatedIc_RewardsConsistentFactor_OverFoldLuckyOne()
    {
        const int n = 400;
        var rnd = new Random(1);
        var forward = new decimal?[n];
        for (var i = 0; i < n; i++) forward[i] = (decimal)(rnd.NextDouble() - 0.5);

        // Consistente: predice il forward in OGNI fold (IC≈1, dev.std≈0).
        var consistent = new decimal?[n];
        for (var i = 0; i < n; i++) consistent[i] = forward[i];

        // Fortunato su un fold: predice solo nel primo quarto, rumore altrove ⇒ dev.std alta.
        var foldLucky = new decimal?[n];
        var foldSize = n / 4;
        for (var i = 0; i < n; i++) foldLucky[i] = i < foldSize ? forward[i] : (decimal)(rnd.NextDouble() - 0.5);

        var cfg = new MiningConfig { CvFolds = 4, MinObservations = 40, CvStabilityPenalty = 0.5 };
        var c = GeneticAlphaMiner.CrossValidatedIc(consistent, forward, cfg, n);
        var f = GeneticAlphaMiner.CrossValidatedIc(foldLucky, forward, cfg, n);

        Assert.Equal(4, c.FoldsUsed);
        Assert.Equal(4, f.FoldsUsed);
        Assert.True(c.Std < 0.02, $"il fattore consistente ha dev.std bassa (std={c.Std:F3})");
        Assert.True(f.Std > c.Std, $"il fattore fortunato-su-un-fold è più disperso (std {f.Std:F3} > {c.Std:F3})");

        var signalConsistent = Math.Abs(c.Mean) - cfg.CvStabilityPenalty * c.Std;
        var signalFoldLucky = Math.Abs(f.Mean) - cfg.CvStabilityPenalty * f.Std;
        Assert.True(signalConsistent > signalFoldLucky,
            $"la fitness CV deve preferire il consistente: {signalConsistent:F3} > {signalFoldLucky:F3}");
    }

    [Fact]
    public void Miner_WithCvFitness_StillDeterministicAndNonEmpty()
    {
        var candles = MakeCandles(MomentumCloses(500));
        var a = new GeneticAlphaMiner().Mine(candles, SmallConfig(seed: 5));
        var b = new GeneticAlphaMiner().Mine(candles, SmallConfig(seed: 5));

        Assert.NotEmpty(a);
        Assert.Equal(a[0].Expression, b[0].Expression);
        Assert.Equal(a[0].Fitness, b[0].Fitness, 10);
    }

    // --- Gate PBO bloccante --------------------------------------------------------------------

    [Fact]
    public void BlockingPboGate_EmptiesBatch_AtOrBelowPanelPbo_PassesAbove()
    {
        var candles = MakeCandles(MomentumCloses(500));
        var miner = new GeneticAlphaMiner();

        // Il mining è identico per ogni soglia (il gate è solo un post-filtro); calcolo il PBO del pannello.
        var full = miner.Mine(candles, SmallConfig());
        Assert.True(full.Count >= 2);
        var pbo = miner.ComputeSelectionPbo(candles, full.Select(m => m.Expression).ToList(), horizon: 1)!
            .ProbabilityOfBacktestOverfitting;
        Assert.InRange(pbo, 0.0, 0.99);

        // Soglia = PBO del pannello ⇒ pbo ≥ soglia ⇒ batch bloccato (vuoto).
        var blocking = SmallConfig();
        blocking.MaxSelectionPbo = pbo;
        Assert.Empty(miner.Mine(candles, blocking));

        // Soglia appena sopra ⇒ non bloccato.
        var passing = SmallConfig();
        passing.MaxSelectionPbo = pbo + 0.005;
        Assert.NotEmpty(miner.Mine(candles, passing));
    }

    [Fact]
    public void BlockingPboGate_Disabled_ByDefault()
    {
        Assert.Equal(1.0, new MiningConfig().MaxSelectionPbo);
        var candles = MakeCandles(MomentumCloses(400));
        Assert.NotEmpty(new GeneticAlphaMiner().Mine(candles, SmallConfig())); // default 1.0 ⇒ mai bloccato
    }
}
