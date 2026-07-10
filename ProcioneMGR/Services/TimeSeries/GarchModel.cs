using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;

namespace ProcioneMGR.Services.TimeSeries;

/// <summary>
/// Implementazione di <see cref="IGarchModel"/>: stima per massima verosimiglianza via
/// Nelder-Mead (derivative-free — la log-verosimiglianza del GARCH è ricorsiva, il gradiente
/// analitico è complesso e facile da sbagliare). I tre parametri (ω, α, β) sono
/// riparametrizzati in uno spazio libero ℝ³ tramite sigmoid/exp in modo che i vincoli
/// (ω&gt;0, α≥0, β≥0, α+β&lt;1 — necessari per varianza positiva e stazionarietà) siano SEMPRE
/// soddisfatti qualunque punto esplori l'ottimizzatore, senza bisogno di un solutore vincolato.
/// Con <see cref="GarchInnovation.StudentT"/> si aggiunge un quarto parametro libero per i gradi
/// di libertà ν (riparametrizzato come ν = 2 + exp(θ₃) &gt; 2, così la varianza resta finita).
/// </summary>
public sealed class GarchModel : IGarchModel
{
    public GarchFit Fit(IReadOnlyList<decimal> returns, GarchInnovation innovation = GarchInnovation.Gaussian)
    {
        ArgumentNullException.ThrowIfNull(returns);
        if (returns.Count < 30)
        {
            throw new ArgumentException("Servono almeno 30 osservazioni per stimare un GARCH(1,1).", nameof(returns));
        }

        var r = returns.Select(x => (double)x).ToArray();
        var n = r.Length;
        var mean = r.Average();
        var eps = r.Select(x => x - mean).ToArray();
        var unconditionalVariance = Math.Max(eps.Select(e => e * e).Average(), 1e-15);
        var studentT = innovation == GarchInnovation.StudentT;

        double NegativeLogLikelihood(Vector<double> theta)
        {
            var (omega, alpha, beta) = Unconstrain(theta);
            var nu = studentT ? Nu(theta) : double.PositiveInfinity;
            var sigma2 = unconditionalVariance;
            var ll = 0.0;
            for (var t = 1; t < n; t++)
            {
                sigma2 = omega + alpha * eps[t - 1] * eps[t - 1] + beta * sigma2;
                if (sigma2 <= 0 || double.IsNaN(sigma2) || double.IsInfinity(sigma2))
                {
                    return 1e10;
                }
                ll += studentT
                    ? StudentTLogDensity(eps[t], sigma2, nu)
                    : -0.5 * (Math.Log(2 * Math.PI) + Math.Log(sigma2) + eps[t] * eps[t] / sigma2);
            }
            return double.IsNaN(ll) || double.IsInfinity(ll) ? 1e10 : -ll;
        }

        // Punto di partenza standard per dati finanziari: alpha piccolo, beta alto e persistente.
        const double alpha0 = 0.05;
        const double beta0 = 0.85;
        var omega0 = unconditionalVariance * (1 - alpha0 - beta0);
        double[] guess =
        [
            Math.Log(Math.Max(omega0, 1e-15)),
            Logit(alpha0 / 0.999),
            Logit(beta0 / (0.999 - alpha0)),
        ];
        if (studentT) guess = [.. guess, Math.Log(8.0 - 2.0)]; // ν₀ = 8 (code moderatamente grasse)
        var initialGuess = Vector<double>.Build.Dense(guess);

        var objective = ObjectiveFunction.Value(NegativeLogLikelihood);
        var result = NelderMeadSimplex.Minimum(objective, initialGuess, convergenceTolerance: 1e-10, maximumIterations: 4000);

        var (omegaHat, alphaHat, betaHat) = Unconstrain(result.MinimizingPoint);
        var nuHat = studentT ? Nu(result.MinimizingPoint) : (double?)null;

        var variances = new double[n];
        variances[0] = unconditionalVariance;
        for (var t = 1; t < n; t++)
        {
            variances[t] = omegaHat + alphaHat * eps[t - 1] * eps[t - 1] + betaHat * variances[t - 1];
        }

        return new GarchFit
        {
            Omega = omegaHat,
            Alpha = alphaHat,
            Beta = betaHat,
            DegreesOfFreedom = nuHat,
            ConditionalVariances = variances,
            LogLikelihood = -NegativeLogLikelihood(result.MinimizingPoint),
        };
    }

    /// <summary>
    /// log-densità di εₜ ~ σₜ·zₜ con zₜ Student-t STANDARDIZZATA (varianza 1). La scala della t è
    /// s² = σ²·(ν-2)/ν, così che Var(εₜ) = σ²ₜ indipendentemente da ν (i tre parametri GARCH restano
    /// interpretabili come nel caso gaussiano, ν governa solo lo spessore delle code).
    /// </summary>
    private static double StudentTLogDensity(double eps, double sigma2, double nu)
    {
        var scale2 = sigma2 * (nu - 2.0) / nu;   // s² tale che Var(εₜ) = σ²ₜ
        return SpecialFunctions.GammaLn((nu + 1.0) / 2.0)
             - SpecialFunctions.GammaLn(nu / 2.0)
             - 0.5 * Math.Log(nu * Math.PI * scale2)
             - (nu + 1.0) / 2.0 * Math.Log(1.0 + eps * eps / (nu * scale2));
    }

    private static (double Omega, double Alpha, double Beta) Unconstrain(Vector<double> theta)
    {
        var omega = Math.Exp(theta[0]);
        var alpha = Sigmoid(theta[1]) * 0.999;
        var beta = Sigmoid(theta[2]) * (0.999 - alpha);
        return (omega, alpha, beta);
    }

    /// <summary>ν = 2 + exp(θ₃): vincola i gradi di libertà a &gt; 2 (varianza finita) senza solutore vincolato.</summary>
    private static double Nu(Vector<double> theta) => 2.0 + Math.Exp(theta[3]);

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

    private static double Logit(double p) => Math.Log(p / (1.0 - p));
}
