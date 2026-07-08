using ProcioneMGR.Services.Optimization.Bayesian;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'ottimizzazione bayesiana (Fase 6): il surrogato Gaussian Process + Expected Improvement
/// converge all'ottimo di funzioni analitiche note in poche valutazioni, rispetta i confini/interi
/// dello spazio, ed è deterministico a parità di seme (requisito non negoziabile della piattaforma).
/// </summary>
public class BayesianOptimizerTests
{
    private static BayesianSearch NewSearch(int seed = 42)
        => new(new BayesianOptimizationEngine(new BayesianOptions { Seed = seed, AcquisitionSamples = 512 }));

    [Fact]
    public void Maximize_1D_ConvergesNearOptimum()
    {
        // f(x) = -(x-3)² su [0,5]: massimo in x=3, valore 0.
        var space = new ParameterSpace([new ParameterDimension("x", 0, 5)]);
        double Objective(double[] p) => -Math.Pow(p[0] - 3.0, 2);

        var result = NewSearch(seed: 1).Maximize(space, Objective, iterations: 30, initialRandom: 8, seed: 1);

        Assert.True(Math.Abs(result.BestParameters[0] - 3.0) < 0.4,
            $"atteso ~3, ottenuto {result.BestParameters[0]:F3}");
        Assert.True(result.BestScore > -0.2, $"punteggio {result.BestScore:F4} troppo lontano dall'ottimo 0");
    }

    [Fact]
    public void Maximize_2D_ConvergesNearOptimum()
    {
        // f(x,y) = -((x-2)² + (y-8)²) su [0,5]×[0,10]: massimo in (2,8).
        var space = new ParameterSpace([new ParameterDimension("x", 0, 5), new ParameterDimension("y", 0, 10)]);
        double Objective(double[] p) => -(Math.Pow(p[0] - 2.0, 2) + Math.Pow(p[1] - 8.0, 2));

        var result = NewSearch(seed: 3).Maximize(space, Objective, iterations: 45, initialRandom: 12, seed: 3);

        Assert.True(Math.Abs(result.BestParameters[0] - 2.0) < 0.7, $"x atteso ~2, ottenuto {result.BestParameters[0]:F3}");
        Assert.True(Math.Abs(result.BestParameters[1] - 8.0) < 0.9, $"y atteso ~8, ottenuto {result.BestParameters[1]:F3}");
    }

    [Fact]
    public void Maximize_IsDeterministic_ForSameSeed()
    {
        var space = new ParameterSpace([new ParameterDimension("x", 0, 5)]);
        double Objective(double[] p) => -Math.Pow(p[0] - 3.0, 2);

        var a = NewSearch().Maximize(space, Objective, iterations: 20, initialRandom: 5, seed: 7);
        var b = NewSearch().Maximize(space, Objective, iterations: 20, initialRandom: 5, seed: 7);

        Assert.Equal(a.BestScore, b.BestScore);
        Assert.Equal(a.BestParameters, b.BestParameters);
        Assert.Equal(a.History.Count, b.History.Count);
    }

    [Fact]
    public void Maximize_BeatsPureRandomSearch_OnSameBudget()
    {
        // Con lo stesso numero di valutazioni, la ricerca guidata non dovrebbe fare peggio di una
        // pura casuale sullo stesso spazio (a parità di seme dell'inizializzazione).
        var space = new ParameterSpace([new ParameterDimension("x", 0, 5), new ParameterDimension("y", 0, 10)]);
        double Objective(double[] p) => -(Math.Pow(p[0] - 2.0, 2) + Math.Pow(p[1] - 8.0, 2));

        var guided = NewSearch(seed: 5).Maximize(space, Objective, iterations: 30, initialRandom: 5, seed: 5);

        // Baseline: 35 punti puramente casuali (stesso budget totale).
        var rng = new Random(5);
        var bestRandom = double.NegativeInfinity;
        for (var i = 0; i < 35; i++)
        {
            var p = space.Denormalize([rng.NextDouble(), rng.NextDouble()]);
            bestRandom = Math.Max(bestRandom, Objective(p));
        }

        Assert.True(guided.BestScore >= bestRandom, $"guidata {guided.BestScore:F3} < casuale {bestRandom:F3}");
    }

    [Fact]
    public void SuggestNext_StaysWithinBounds_AndSnapsIntegers()
    {
        var space = new ParameterSpace([new ParameterDimension("period", 5, 50, IsInteger: true)]);
        var engine = new BayesianOptimizationEngine(new BayesianOptions { Seed = 1 });
        var history = new List<EvaluatedPoint>
        {
            new([10.0], -1.0), new([20.0], -0.2), new([30.0], -0.5),
        };

        for (var i = 0; i < 10; i++)
        {
            var next = engine.SuggestNext(history, space);
            Assert.InRange(next[0], 5.0, 50.0);
            Assert.Equal(Math.Round(next[0]), next[0]); // intero
            history.Add(new EvaluatedPoint(next, -Math.Pow(next[0] - 22.0, 2)));
        }
    }

    [Fact]
    public void SuggestNext_EmptyHistory_ReturnsInBoundsPoint()
    {
        var space = new ParameterSpace([new ParameterDimension("x", -3, 7)]);
        var engine = new BayesianOptimizationEngine();
        var next = engine.SuggestNext([], space);
        Assert.InRange(next[0], -3.0, 7.0);
    }
}
