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
/// </summary>
public sealed class GarchModel : IGarchModel
{
    public GarchFit Fit(IReadOnlyList<decimal> returns)
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

        double NegativeLogLikelihood(Vector<double> theta)
        {
            var (omega, alpha, beta) = Unconstrain(theta);
            var sigma2 = unconditionalVariance;
            var ll = 0.0;
            for (var t = 1; t < n; t++)
            {
                sigma2 = omega + alpha * eps[t - 1] * eps[t - 1] + beta * sigma2;
                if (sigma2 <= 0 || double.IsNaN(sigma2) || double.IsInfinity(sigma2))
                {
                    return 1e10;
                }
                ll += -0.5 * (Math.Log(2 * Math.PI) + Math.Log(sigma2) + eps[t] * eps[t] / sigma2);
            }
            return double.IsNaN(ll) || double.IsInfinity(ll) ? 1e10 : -ll;
        }

        // Punto di partenza standard per dati finanziari: alpha piccolo, beta alto e persistente.
        const double alpha0 = 0.05;
        const double beta0 = 0.85;
        var omega0 = unconditionalVariance * (1 - alpha0 - beta0);
        var initialGuess = Vector<double>.Build.Dense(
        [
            Math.Log(Math.Max(omega0, 1e-15)),
            Logit(alpha0 / 0.999),
            Logit(beta0 / (0.999 - alpha0)),
        ]);

        var objective = ObjectiveFunction.Value(NegativeLogLikelihood);
        var result = NelderMeadSimplex.Minimum(objective, initialGuess, convergenceTolerance: 1e-10, maximumIterations: 3000);

        var (omegaHat, alphaHat, betaHat) = Unconstrain(result.MinimizingPoint);

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
            ConditionalVariances = variances,
            LogLikelihood = -NegativeLogLikelihood(result.MinimizingPoint),
        };
    }

    private static (double Omega, double Alpha, double Beta) Unconstrain(Vector<double> theta)
    {
        var omega = Math.Exp(theta[0]);
        var alpha = Sigmoid(theta[1]) * 0.999;
        var beta = Sigmoid(theta[2]) * (0.999 - alpha);
        return (omega, alpha, beta);
    }

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

    private static double Logit(double p) => Math.Log(p / (1.0 - p));
}
