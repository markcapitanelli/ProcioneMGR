using MathNet.Numerics.LinearAlgebra;
using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.Risk;

/// <summary>
/// Criterio di Kelly per il position sizing (Jansen ML4T, cap. 5): la frazione di capitale
/// da impegnare che massimizza la crescita logaritmica della ricchezza a lungo termine.
///
///  - Caso binario (dai trade): f* = p - (1-p)/b, con p = probabilita' di vincita e
///    b = payoff ratio (guadagno medio / perdita media).
///  - Caso continuo (dai rendimenti, approssimazione normale): f* = mu / sigma^2; in
///    alternativa la massimizzazione numerica di E[log(1+f*r)] sotto Normal(mu, sigma).
///  - Multi-asset (Chan 2008): w = Sigma^-1 * mu, equivalente al portafoglio max-Sharpe
///    (potenzialmente a leva), poi normalizzato.
///
/// In pratica si usa una FRAZIONE del Kelly pieno (half-Kelly): il Kelly pieno e' ottimo
/// solo se le stime di p/b o mu/sigma sono esatte — non lo sono mai, e sbagliare per
/// eccesso costa piu' che sbagliare per difetto.
/// </summary>
public sealed class KellyCalculator
{
    /// <summary>Kelly binario: f* = p - (1-p)/b. Ritorna 0 se l'edge e' negativo o b non valido.</summary>
    public static decimal BinaryKelly(decimal winProbability, decimal payoffRatio)
    {
        if (payoffRatio <= 0m || winProbability is <= 0m or >= 1m) return 0m;
        var f = winProbability - (1m - winProbability) / payoffRatio;
        return f > 0m ? f : 0m;
    }

    /// <summary>
    /// Kelly binario dalla lista dei trade di un backtest: p = percent win,
    /// b = |guadagno medio| / |perdita media|.
    /// </summary>
    public KellySuggestion FromTradeHistory(IReadOnlyList<BacktestTrade> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);

        var wins = trades.Where(t => t.Pnl > 0m).Select(t => t.Pnl).ToList();
        var losses = trades.Where(t => t.Pnl < 0m).Select(t => t.Pnl).ToList();
        var decided = wins.Count + losses.Count;
        if (decided == 0 || losses.Count == 0 || wins.Count == 0)
        {
            return new KellySuggestion(0m, 0m, 0m, 0m, decided);
        }

        var p = (decimal)wins.Count / decided;
        var avgWin = wins.Sum() / wins.Count;
        var avgLoss = Math.Abs(losses.Sum() / losses.Count);
        var b = avgLoss == 0m ? 0m : avgWin / avgLoss;
        var kelly = BinaryKelly(p, b);

        return new KellySuggestion(kelly, kelly / 2m, p, b, decided);
    }

    /// <summary>
    /// Kelly continuo in forma chiusa (approssimazione normale): f* = mu / sigma^2.
    /// mu e sigma sono per periodo (stessa periodicita' dei rendimenti passati).
    /// </summary>
    public static decimal ContinuousKelly(decimal meanReturn, decimal returnStdDev)
    {
        if (returnStdDev <= 0m) return 0m;
        var f = meanReturn / (returnStdDev * returnStdDev);
        return f > 0m ? f : 0m;
    }

    /// <summary>
    /// Kelly continuo numerico (l'approccio esatto del libro): massimizza
    /// E[log(1+f*r)] con r ~ Normal(mean, std), integrale su [mean-3*std, mean+3*std]
    /// (Simpson) e ricerca golden-section di f in [0, maxFraction].
    /// </summary>
    public static double ContinuousKellyNumeric(double mean, double std, double maxFraction = 2.0)
    {
        if (std <= 0 || maxFraction <= 0) return 0;
        return MaximizeGrowth(f => GrowthRate(f, mean, std), maxFraction);
    }

    /// <summary>
    /// Kelly EMPIRICO (robusto alle code grasse): massimizza la crescita logaritmica attesa
    /// G(f) = media su i di log(1 + f·rᵢ) usando la distribuzione EMPIRICA dei rendimenti osservati,
    /// senza assumere normalità. Quando i dati contengono crash reali (code grasse tipiche delle
    /// cripto), il Kelly empirico è SISTEMATICAMENTE più prudente della versione normale: i pochi
    /// rendimenti molto negativi entrano direttamente nella media e abbassano f*. Rif. audit 2026-07 §4.
    /// </summary>
    /// <param name="returns">Rendimenti storici (per-trade o per-periodo), es. PnL% / 100.</param>
    /// <param name="maxFraction">Limite superiore alla frazione cercata (default 2 = leva 2 sul segnale).</param>
    public static double EmpiricalKelly(IReadOnlyList<double> returns, double maxFraction = 2.0)
    {
        ArgumentNullException.ThrowIfNull(returns);
        if (returns.Count == 0 || maxFraction <= 0) return 0;
        // Nessun edge (media non positiva) -> non scommettere.
        if (returns.Average() <= 0) return 0;
        return MaximizeGrowth(f => EmpiricalGrowthRate(f, returns), maxFraction);
    }

    /// <summary>
    /// Golden-section search del massimo di una crescita logaritmica concava G(f) su [0, maxFraction].
    /// Ritorna 0 se nemmeno il miglior f batte f=0 (edge non sfruttabile).
    /// </summary>
    private static double MaximizeGrowth(Func<double, double> growth, double maxFraction)
    {
        const double invPhi = 0.6180339887498949;
        double a = 0, b = maxFraction;
        var c = b - invPhi * (b - a);
        var d = a + invPhi * (b - a);
        var fc = growth(c);
        var fd = growth(d);

        for (var i = 0; i < 80 && b - a > 1e-7; i++)
        {
            if (fc > fd)
            {
                b = d;
                d = c;
                fd = fc;
                c = b - invPhi * (b - a);
                fc = growth(c);
            }
            else
            {
                a = c;
                c = d;
                fc = fd;
                d = a + invPhi * (b - a);
                fd = growth(d);
            }
        }

        var best = (a + b) / 2;
        return growth(best) > 0 ? best : 0;
    }

    /// <summary>(1/N)·Σ log(1+f·rᵢ) sui rendimenti osservati; forte penalità se una posizione porterebbe a rovina (1+f·r ≤ 0).</summary>
    private static double EmpiricalGrowthRate(double fraction, IReadOnlyList<double> returns)
    {
        var sum = 0.0;
        foreach (var r in returns)
        {
            var arg = 1 + fraction * r;
            sum += arg <= 1e-12 ? -30 : Math.Log(arg); // log(0-) -> bancarotta: penalità forte
        }
        return sum / returns.Count;
    }

    /// <summary>E[log(1+f*r)] sotto r ~ Normal(mean, std), integrata con Simpson su +/-3 sigma.</summary>
    private static double GrowthRate(double fraction, double mean, double std)
    {
        const int steps = 400; // pari, per Simpson
        var lower = mean - 3 * std;
        var upper = mean + 3 * std;
        var h = (upper - lower) / steps;

        double Integrand(double r)
        {
            var arg = 1 + fraction * r;
            if (arg <= 1e-12) return -30 * NormalPdf(r, mean, std); // log(0-) -> penalita' forte (protezione da bancarotta)
            return Math.Log(arg) * NormalPdf(r, mean, std);
        }

        var sum = Integrand(lower) + Integrand(upper);
        for (var i = 1; i < steps; i++)
        {
            var x = lower + i * h;
            sum += Integrand(x) * (i % 2 == 1 ? 4 : 2);
        }
        return sum * h / 3;
    }

    private static double NormalPdf(double x, double mean, double std)
    {
        var z = (x - mean) / std;
        return Math.Exp(-0.5 * z * z) / (std * Math.Sqrt(2 * Math.PI));
    }

    /// <summary>
    /// Kelly multi-asset (Chan): w = Sigma^-1 * mu sui rendimenti storici allineati per
    /// colonna (un asset per colonna), normalizzato perche' la somma dei |pesi| faccia 1.
    /// Equivale al portafoglio max-Sharpe non vincolato; pesi negativi = posizioni short.
    /// </summary>
    /// <param name="returnsByAsset">Per ogni asset, la serie dei rendimenti (stessa lunghezza).</param>
    public IReadOnlyList<decimal> MultiAssetKelly(IReadOnlyList<IReadOnlyList<double>> returnsByAsset)
    {
        ArgumentNullException.ThrowIfNull(returnsByAsset);
        var n = returnsByAsset.Count;
        if (n == 0) return [];
        var len = returnsByAsset[0].Count;
        if (len < 3 || returnsByAsset.Any(r => r.Count != len))
        {
            throw new ArgumentException("Servono almeno 3 osservazioni e serie della stessa lunghezza.");
        }

        var means = returnsByAsset.Select(r => r.Average()).ToArray();

        // Matrice di covarianza campionaria + regularizzazione diagonale (come PortfolioMath).
        var cov = Matrix<double>.Build.Dense(n, n);
        for (var i = 0; i < n; i++)
        {
            for (var j = i; j < n; j++)
            {
                double sum = 0;
                for (var t = 0; t < len; t++)
                {
                    sum += (returnsByAsset[i][t] - means[i]) * (returnsByAsset[j][t] - means[j]);
                }
                var c = sum / (len - 1);
                cov[i, j] = c;
                cov[j, i] = c;
            }
        }
        var trace = cov.Trace();
        var ridge = Math.Max(trace / n * 1e-6, 1e-12);
        for (var i = 0; i < n; i++) cov[i, i] += ridge;

        var weights = cov.Solve(Vector<double>.Build.Dense(means));
        var sumAbs = weights.Sum(w => Math.Abs(w));
        if (sumAbs <= 0) return Enumerable.Repeat(0m, n).ToList();

        return weights.Select(w => (decimal)(w / sumAbs)).ToList();
    }
}

/// <summary>Frazione di Kelly suggerita dai trade storici (con la meta' prudenziale).</summary>
public sealed record KellySuggestion(
    decimal KellyFraction,
    decimal HalfKelly,
    decimal WinProbability,
    decimal PayoffRatio,
    int DecidedTrades);
