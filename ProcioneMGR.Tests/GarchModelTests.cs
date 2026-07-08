using ProcioneMGR.Services.TimeSeries;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="GarchModel"/>: recupero (approssimato) dei parametri di un processo
/// GARCH(1,1) simulato con parametri noti, vincoli di stazionarietà rispettati, "volatility
/// clustering" (uno shock alza la varianza prevista), e mean-reversion della previsione verso
/// la varianza di lungo periodo.
/// </summary>
public class GarchModelTests
{
    private readonly IGarchModel _model = new GarchModel();

    /// <summary>Simula un processo GARCH(1,1) esatto con parametri noti (Box-Muller per gli shock gaussiani).</summary>
    private static List<decimal> SimulateGarch(int n, double omega, double alpha, double beta, int seed)
    {
        var rnd = new Random(seed);
        var returns = new List<decimal>(n);
        var prevEps = 0.0;
        var prevSigma2 = omega / (1 - alpha - beta);

        for (var t = 0; t < n; t++)
        {
            var sigma2 = omega + alpha * prevEps * prevEps + beta * prevSigma2;
            var z = NextGaussian(rnd);
            var eps = Math.Sqrt(sigma2) * z;
            returns.Add((decimal)eps);
            prevEps = eps;
            prevSigma2 = sigma2;
        }
        return returns;
    }

    private static double NextGaussian(Random rnd)
    {
        var u1 = 1.0 - rnd.NextDouble();
        var u2 = rnd.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    [Fact]
    public void Fit_OnSimulatedGarchProcess_RecoversPersistenceApproximately()
    {
        // Processo noto: omega=1e-6, alpha=0.10, beta=0.85 -> persistenza vera = 0.95.
        var returns = SimulateGarch(4000, omega: 1e-6, alpha: 0.10, beta: 0.85, seed: 1);

        var fit = _model.Fit(returns);

        Assert.True(Math.Abs(fit.Persistence - 0.95) < 0.15,
            $"Persistenza stimata={fit.Persistence:F3}, attesa ~0.95 (alpha={fit.Alpha:F3}, beta={fit.Beta:F3})");
    }

    [Fact]
    public void Fit_Parameters_SatisfyStationarityConstraints()
    {
        var returns = SimulateGarch(2000, omega: 2e-6, alpha: 0.08, beta: 0.88, seed: 2);
        var fit = _model.Fit(returns);

        Assert.True(fit.Omega > 0, $"omega={fit.Omega}");
        Assert.True(fit.Alpha >= 0, $"alpha={fit.Alpha}");
        Assert.True(fit.Beta >= 0, $"beta={fit.Beta}");
        Assert.True(fit.Persistence < 1.0, $"persistenza={fit.Persistence}");
    }

    [Fact]
    public void ConditionalVariances_AreAlignedWithInput_AndAllPositive()
    {
        var returns = SimulateGarch(1000, omega: 1e-6, alpha: 0.05, beta: 0.9, seed: 3);
        var fit = _model.Fit(returns);

        Assert.Equal(returns.Count, fit.ConditionalVariances.Count);
        Assert.All(fit.ConditionalVariances, v => Assert.True(v > 0, $"varianza non positiva: {v}"));
    }

    [Fact]
    public void VolatilityClustering_ShockRaisesForecastVariance()
    {
        // Serie calma (rendimenti piccoli) seguita da uno shock grande finale.
        var rnd = new Random(4);
        var calm = Enumerable.Range(0, 300).Select(_ => (decimal)((rnd.NextDouble() - 0.5) * 0.002)).ToList();
        var shocked = new List<decimal>(calm) { 0.08m, -0.06m, 0.05m }; // shock grande alla fine

        var calmFit = _model.Fit(calm);
        var shockedFit = _model.Fit(shocked);

        // La varianza condizionale sull'ultima osservazione (dopo lo shock) deve essere
        // chiaramente più alta di quella tipica del periodo calmo.
        Assert.True(shockedFit.ConditionalVariances[^1] > calmFit.ConditionalVariances.Average() * 2,
            $"varianza dopo shock={shockedFit.ConditionalVariances[^1]:E3}, media periodo calmo={calmFit.ConditionalVariances.Average():E3}");
    }

    [Fact]
    public void ForecastVariance_LongHorizon_ConvergesToLongRunVariance()
    {
        var returns = SimulateGarch(1500, omega: 1e-6, alpha: 0.08, beta: 0.85, seed: 5);
        var fit = _model.Fit(returns);

        var farForecast = fit.ForecastVariance(5000);
        Assert.True(Math.Abs(farForecast - fit.LongRunVariance) / fit.LongRunVariance < 0.01,
            $"forecast={farForecast:E3} longRun={fit.LongRunVariance:E3}");
    }

    [Fact]
    public void ForecastVariance_OneStep_MatchesGarchRecursion()
    {
        var returns = SimulateGarch(1000, omega: 1e-6, alpha: 0.08, beta: 0.85, seed: 6);
        var fit = _model.Fit(returns);

        // A orizzonte 1 la formula di mean-reversion deve coincidere con la varianza attuale
        // "attratta" verso quella di lungo periodo di un fattore pari alla persistenza.
        var expected = fit.LongRunVariance + fit.Persistence * (fit.ConditionalVariances[^1] - fit.LongRunVariance);
        Assert.Equal(expected, fit.ForecastVariance(1), 10);
    }

    [Fact]
    public void Fit_TooFewObservations_Throws()
    {
        var returns = Enumerable.Repeat(0.001m, 10).ToList();
        Assert.Throws<ArgumentException>(() => _model.Fit(returns));
    }

    [Fact]
    public void ForecastVariance_InvalidHorizon_Throws()
    {
        var returns = SimulateGarch(500, 1e-6, 0.05, 0.9, seed: 7);
        var fit = _model.Fit(returns);
        Assert.Throws<ArgumentOutOfRangeException>(() => fit.ForecastVariance(0));
    }
}
