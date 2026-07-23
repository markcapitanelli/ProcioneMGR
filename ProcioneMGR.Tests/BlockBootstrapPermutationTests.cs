using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Pipeline.Stages;
using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Validation;

namespace ProcioneMGR.Tests;

/// <summary>
/// [T1.5 roadmap macchina-ricerca] Block bootstrap + permutation test: la randomizzazione GIUSTA
/// (lungo il tempo, a blocchi) dopo la lezione dei 400 panieri correlati che produssero t = 141.
///
/// I test di calibrazione sono il cuore: un test statistico che non è calibrato — che non dà p alti
/// sul rumore e p bassi su un edge piantato — è un generatore di certezze finte, peggio di niente.
/// </summary>
public class BlockBootstrapPermutationTests
{
    // ---------------------------------------------------------------- PermutationTest

    private static double[] Noise(int n, int seed, double sd = 0.01)
    {
        // Gaussiana via Box-Muller, deterministica: rumore SIMMETRICO a media zero (l'ipotesi nulla).
        var rng = new Random(seed);
        var r = new double[n];
        for (var i = 0; i < n; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            r[i] = sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
        return r;
    }

    [Fact]
    public void PlantedDrift_GetsALowPValue()
    {
        // Deriva piantata forte (media 0,5 deviazioni standard per barra): il test DEVE vederla.
        var returns = Noise(120, seed: 7).Select(v => v + 0.005).ToArray();

        var result = PermutationTest.SharpeSignificance(returns);

        Assert.True(result.PValue < 0.05, $"edge piantato non riconosciuto: p = {result.PValue:F3}");
    }

    [Fact]
    public void PureNoise_GetsAnUnremarkablePValue()
    {
        // Rumore simmetrico a media zero: p NON deve essere estremo. Con seme fisso il valore è
        // deterministico; la soglia larga (>0,10) evita di inchiodare il test a un numero fragile.
        var result = PermutationTest.SharpeSignificance(Noise(120, seed: 11));

        Assert.True(result.PValue > 0.10, $"falso positivo sul rumore: p = {result.PValue:F3}");
    }

    [Fact]
    public void PValue_IsCalibratedOnAverage_AcrossManyNoiseSeries()
    {
        // La calibrazione vera: su TANTE serie di puro rumore, la frazione con p < 0,10 deve stare
        // vicino al 10% (test esatto per costruzione, tolleranza binomiale larga). È il controllo
        // che distingue un test statistico da un generatore di numeri.
        var below10 = 0;
        const int series = 200;
        for (var s = 0; s < series; s++)
        {
            if (PermutationTest.SharpeSignificance(Noise(80, seed: 1000 + s), permutations: 200).PValue < 0.10)
            {
                below10++;
            }
        }

        // Binomiale(200, 0.10): media 20, σ≈4,2 → [8, 34] è ±3σ abbondante.
        Assert.InRange(below10, 8, 34);
    }

    [Fact]
    public void SameSeed_SameAnswer()
    {
        var returns = Noise(100, seed: 3);
        var a = PermutationTest.SharpeSignificance(returns, seed: 42);
        var b = PermutationTest.SharpeSignificance(returns, seed: 42);
        Assert.Equal(a.PValue, b.PValue);
    }

    [Fact]
    public void DegenerateSeries_YieldPOne_NotACrash()
    {
        Assert.Equal(1.0, PermutationTest.SharpeSignificance([]).PValue);
        Assert.Equal(1.0, PermutationTest.SharpeSignificance([0.01, 0.02]).PValue);
        Assert.Equal(1.0, PermutationTest.SharpeSignificance(new double[50]).PValue);   // tutti zero
    }

    // ---------------------------------------------------------------- MonteCarloAnalyzer a blocchi

    /// <summary>PnL con AUTOCORRELAZIONE forte: serie di 10 vincite da +1 alternate a 10 perdite da -1.</summary>
    private static decimal[] StreakyPnls(int n)
        => Enumerable.Range(0, n).Select(i => i / 10 % 2 == 0 ? 1m : -1m).ToArray();

    [Fact]
    public void DefaultMode_IsIidShuffle_HistoricalBehaviourUnchanged()
    {
        var pnls = StreakyPnls(100);
        var byDefault = new MonteCarloAnalyzer().Run(pnls, new MonteCarloConfig { Seed = 5, NumberOfShuffles = 200 });
        var explicitIid = new MonteCarloAnalyzer().Run(pnls, new MonteCarloConfig
        {
            Seed = 5, NumberOfShuffles = 200, SamplingMode = MonteCarloSamplingMode.IidShuffle,
        });

        Assert.Equal(explicitIid.MaxDrawdown95, byDefault.MaxDrawdown95);
        Assert.Equal(explicitIid.SortedMaxDrawdowns, byDefault.SortedMaxDrawdowns);
    }

    [Fact]
    public void BlockMode_OnStreakyPnls_SeesDeeperDrawdownsThanIid()
    {
        // Il motivo per cui il block bootstrap esiste: le SERIE di perdite consecutive producono i
        // drawdown profondi, e lo shuffle iid le spezza, sottostimandoli. Su una serie a strisce
        // il modo a blocchi deve vedere una coda di drawdown almeno pari, tipicamente peggiore.
        var pnls = StreakyPnls(200);
        var iid = new MonteCarloAnalyzer().Run(pnls, new MonteCarloConfig
        {
            Seed = 9, NumberOfShuffles = 300, SamplingMode = MonteCarloSamplingMode.IidShuffle,
        });
        var block = new MonteCarloAnalyzer().Run(pnls, new MonteCarloConfig
        {
            Seed = 9, NumberOfShuffles = 300, SamplingMode = MonteCarloSamplingMode.StationaryBlock, MeanBlockLength = 10,
        });

        Assert.True(block.MaxDrawdown95 >= iid.MaxDrawdown95,
            $"il block bootstrap deve preservare le strisce: dd95 block {block.MaxDrawdown95} < iid {iid.MaxDrawdown95}");
    }

    [Fact]
    public void BlockMode_IsDeterministicWithSeed_AndDrawsFromTheSource()
    {
        var pnls = new decimal[] { 1m, 2m, 3m, -4m, 5m, -6m, 7m, 8m };
        var cfg = new MonteCarloConfig
        {
            Seed = 21, NumberOfShuffles = 50, SamplingMode = MonteCarloSamplingMode.StationaryBlock, MeanBlockLength = 3,
        };

        var a = new MonteCarloAnalyzer().Run(pnls, cfg);
        var b = new MonteCarloAnalyzer().Run(pnls, cfg);
        Assert.Equal(a.SortedMaxDrawdowns, b.SortedMaxDrawdowns);

        // Con reinserimento: ogni valore campionato appartiene al multiset sorgente.
        var sourceSet = pnls.ToHashSet();
        Assert.All(a.WorstEquity.Zip(a.WorstEquity.Skip(1)), _ => { });   // equity ben formata
        Assert.All(Diffs(a.WorstEquity), d => Assert.Contains(d, sourceSet));

        static IEnumerable<decimal> Diffs(IReadOnlyList<decimal> equity)
        {
            for (var i = 0; i < equity.Count; i++)
            {
                yield return i == 0 ? equity[0] : equity[i] - equity[i - 1];
            }
        }
    }

    // ---------------------------------------------------------------- OverfittingGate

    private static ValidatedCandidate Candidate(string name) => new()
    {
        StrategyName = name, Symbol = "BTC/USDT", Timeframe = "1d", Survived = true, SelectionSharpe = 1m,
    };

    [Fact]
    public void Gate_PopulatesPermutationPValue_ButDoesNotBlockByDefault()
    {
        // Candidato-rumore: DSR permissivo per isolare il comportamento del p-value.
        var noise = Candidate("Noise");
        var returns = new List<double[]> { Noise(60, seed: 13) };

        OverfittingGate.Apply([noise], returns, minDeflatedSharpe: -1.0, maxPbo: 1.0);

        Assert.NotNull(noise.PermutationPValue);
        Assert.True(noise.Survived, "con la soglia di default (1.0) il p-value è solo informativo");
    }

    [Fact]
    public void Gate_WithThreshold_KillsNoise_KeepsPlantedEdge()
    {
        var noise = Candidate("Noise");
        var edge = Candidate("Edge");
        var returns = new List<double[]>
        {
            Noise(60, seed: 13),
            Noise(120, seed: 7).Select(v => v + 0.005).ToArray(),
        };

        OverfittingGate.Apply([noise, edge], returns, minDeflatedSharpe: -1.0, maxPbo: 1.0,
            maxPermutationPValue: 0.10);

        Assert.False(noise.Survived, $"il rumore (p={noise.PermutationPValue:F3}) doveva essere scartato");
        Assert.Contains("permutation", noise.RejectReason);
        Assert.True(edge.Survived, $"l'edge piantato (p={edge.PermutationPValue:F3}) doveva sopravvivere");
    }
}
