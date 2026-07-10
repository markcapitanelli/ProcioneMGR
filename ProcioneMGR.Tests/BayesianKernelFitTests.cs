using ProcioneMGR.Services.Optimization.Bayesian;

namespace ProcioneMGR.Tests;

/// <summary>
/// E1 — il kernel del GP bayesiano ora STIMA i suoi iperparametri via log-verosimiglianza marginale
/// invece di tenerli fissi (fissi ⇒ il surrogato non si adatta e la ricerca degenera verso il casuale).
/// Verifica che la stima recuperi la scala giusta: lengthscale grande su dati lisci, piccola su dati
/// oscillanti; che sotto il minimo di punti si usino i fallback; e che resti deterministica.
/// </summary>
public class BayesianKernelFitTests
{
    private static readonly ParameterSpace UnitSpace = new([new ParameterDimension("x", 0, 1)]);

    private static List<EvaluatedPoint> Sampled(int n, Func<double, double> f)
    {
        var pts = new List<EvaluatedPoint>(n);
        for (var k = 0; k < n; k++)
        {
            var x = (double)k / (n - 1);
            pts.Add(new EvaluatedPoint([x], f(x)));
        }
        return pts;
    }

    [Fact]
    public void FitKernel_SmoothData_PrefersLargeLengthscale()
    {
        // Funzione liscia (lineare): il GP la spiega meglio con lengthscale ampia.
        var engine = new BayesianOptimizationEngine();
        var (ls, sv) = engine.FitKernel(Sampled(10, x => 2.0 * x), UnitSpace);

        Assert.True(ls >= 0.5, $"lengthscale attesa ampia su dati lisci, ottenuta {ls}");
        Assert.True(sv > 0, $"varianza segnale {sv}");
    }

    [Fact]
    public void FitKernel_WigglyData_PrefersSmallLengthscale()
    {
        // Alta frequenza: serve una lengthscale piccola per interpolare le oscillazioni.
        var engine = new BayesianOptimizationEngine();
        var (ls, _) = engine.FitKernel(Sampled(24, x => Math.Sin(10.0 * Math.PI * x)), UnitSpace);

        Assert.True(ls <= 0.2, $"lengthscale attesa piccola su dati oscillanti, ottenuta {ls}");
    }

    [Fact]
    public void FitKernel_BelowMinPoints_ReturnsFallback()
    {
        var engine = new BayesianOptimizationEngine(new BayesianOptions { LengthScale = 0.2, SignalVariance = 1.0, MinPointsForHyperparameterFit = 4 });
        var (ls, sv) = engine.FitKernel(Sampled(3, x => x), UnitSpace);

        Assert.Equal(0.2, ls);
        Assert.Equal(1.0, sv);
    }

    [Fact]
    public void FitKernel_IsDeterministic()
    {
        var engine = new BayesianOptimizationEngine();
        var data = Sampled(12, x => Math.Cos(3.0 * x) + 0.1 * x);
        var a = engine.FitKernel(data, UnitSpace);
        var b = engine.FitKernel(data, UnitSpace);
        Assert.Equal(a, b);
    }
}
