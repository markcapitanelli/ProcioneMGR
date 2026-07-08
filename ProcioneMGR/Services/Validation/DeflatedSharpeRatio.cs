using MathNet.Numerics.Distributions;

namespace ProcioneMGR.Services.Validation;

/// <summary>
/// Momenti di una serie di rendimenti periodici (double), calcolati in forma "population" (biased)
/// per coerenza con le formule di Bailey–López de Prado (che usano γ3 asimmetria e γ4 curtosi
/// <b>non in eccesso</b>, cioè normale = 3). Separato da <c>Optimization.Statistics</c> (che opera
/// su <c>EquityPoint</c> in decimal e annualizza): qui serve lo Sharpe <b>per-periodo</b> e i
/// momenti superiori grezzi che le formule PSR/DSR richiedono.
/// </summary>
public static class ReturnMoments
{
    /// <summary>
    /// Sharpe <b>per-periodo</b> (NON annualizzato): (media − rfPerPeriodo) / deviazione standard di
    /// popolazione. È la quantità richiesta da PSR/DSR (che poi correggono con T e i momenti). 0 se
    /// dati insufficienti o volatilità nulla (nessuna divisione per zero).
    /// </summary>
    public static double PerPeriodSharpe(IReadOnlyList<double> returns, double riskFreePerPeriod = 0.0)
    {
        if (returns is null || returns.Count < 2) return 0.0;
        var mean = Mean(returns);
        var variance = CentralMoment(returns, 2, mean);
        if (variance <= 0.0) return 0.0;
        return (mean - riskFreePerPeriod) / Math.Sqrt(variance);
    }

    /// <summary>Asimmetria (skewness) di popolazione: m3 / m2^(3/2). 0 se volatilità nulla.</summary>
    public static double Skewness(IReadOnlyList<double> returns)
    {
        if (returns is null || returns.Count < 2) return 0.0;
        var mean = Mean(returns);
        var m2 = CentralMoment(returns, 2, mean);
        if (m2 <= 0.0) return 0.0;
        var m3 = CentralMoment(returns, 3, mean);
        return m3 / Math.Pow(m2, 1.5);
    }

    /// <summary>
    /// Curtosi di popolazione <b>non in eccesso</b>: m4 / m2^2 (normale = 3). È la γ4 usata da PSR/DSR.
    /// Ritorna 3 (valore gaussiano) se volatilità nulla, così il termine di correzione è neutro.
    /// </summary>
    public static double Kurtosis(IReadOnlyList<double> returns)
    {
        if (returns is null || returns.Count < 2) return 3.0;
        var mean = Mean(returns);
        var m2 = CentralMoment(returns, 2, mean);
        if (m2 <= 0.0) return 3.0;
        var m4 = CentralMoment(returns, 4, mean);
        return m4 / (m2 * m2);
    }

    private static double Mean(IReadOnlyList<double> xs)
    {
        var sum = 0.0;
        for (var i = 0; i < xs.Count; i++) sum += xs[i];
        return sum / xs.Count;
    }

    private static double CentralMoment(IReadOnlyList<double> xs, int order, double mean)
    {
        var sum = 0.0;
        for (var i = 0; i < xs.Count; i++) sum += Math.Pow(xs[i] - mean, order);
        return sum / xs.Count;
    }
}

/// <summary>
/// <b>Probabilistic Sharpe Ratio</b> e <b>Deflated Sharpe Ratio</b> (Bailey &amp; López de Prado, 2014,
/// "The Deflated Sharpe Ratio", SSRN 2460551). Rispondono alla domanda centrale quando si sceglie il
/// "migliore" tra molti candidati: <i>lo Sharpe osservato è statisticamente significativo, o è il
/// massimo atteso per puro effetto del test multiplo (selection bias)?</i>
///
/// CONVENZIONE FONDAMENTALE: tutti gli Sharpe passati qui sono <b>per-periodo</b> (non annualizzati),
/// coerenti con <see cref="ReturnMoments.PerPeriodSharpe"/>. Se hai uno Sharpe annualizzato, dividi
/// per √(periodiPerAnno) prima di passarlo (la varianza dei trial va de-annualizzata dallo stesso
/// fattore — il rapporto resta corretto). Puro e deterministico.
/// </summary>
public static class DeflatedSharpeRatio
{
    /// <summary>Costante di Eulero–Mascheroni, usata nella stima del massimo atteso di N estrazioni.</summary>
    public const double EulerMascheroni = 0.5772156649015329;

    /// <summary>
    /// Probabilistic Sharpe Ratio: probabilità che il vero Sharpe superi <paramref name="benchmarkSharpe"/>,
    /// dato lo Sharpe osservato, la lunghezza del track record e i momenti superiori dei rendimenti.
    /// PSR = Φ( (SR − SR*)·√(T−1) / √(1 − γ3·SR + (γ4−1)/4·SR²) ).
    /// </summary>
    public static double ProbabilisticSharpe(double observedSharpe, double benchmarkSharpe, int observations, double skewness, double kurtosis)
    {
        if (observations < 2) return double.NaN;
        var denom = 1.0 - skewness * observedSharpe + (kurtosis - 1.0) / 4.0 * observedSharpe * observedSharpe;
        if (denom <= 0.0) denom = 1e-12; // degenerazione numerica: evita radice di non-positivo
        var z = (observedSharpe - benchmarkSharpe) * Math.Sqrt(observations - 1) / Math.Sqrt(denom);
        return Normal.CDF(0.0, 1.0, z);
    }

    /// <summary>
    /// Massimo Sharpe atteso <b>sotto l'ipotesi nulla</b> (nessun edge) su <paramref name="trials"/>
    /// tentativi indipendenti, data la varianza cross-trial degli Sharpe stimati:
    /// SR* ≈ √V · [ (1−γ)·Φ⁻¹(1 − 1/N) + γ·Φ⁻¹(1 − 1/(N·e)) ]. È la soglia che lo Sharpe osservato
    /// deve battere per non essere un semplice artefatto della ricerca. N≤1 ⇒ 0 (nessuna selezione).
    /// </summary>
    public static double ExpectedMaxSharpe(double varianceOfTrialSharpes, int trials)
    {
        if (trials <= 1 || varianceOfTrialSharpes <= 0.0) return 0.0;
        var sigma = Math.Sqrt(varianceOfTrialSharpes);
        var a = Normal.InvCDF(0.0, 1.0, 1.0 - 1.0 / trials);
        var b = Normal.InvCDF(0.0, 1.0, 1.0 - 1.0 / (trials * Math.E));
        return sigma * ((1.0 - EulerMascheroni) * a + EulerMascheroni * b);
    }

    /// <summary>
    /// Deflated Sharpe Ratio = PSR valutato alla soglia SR* = <see cref="ExpectedMaxSharpe"/>. È la
    /// probabilità che l'edge sia reale <i>dopo</i> aver corretto per selection bias (N tentativi),
    /// non-normalità (γ3, γ4) e lunghezza del track record (T). Convenzione: DSR &gt; 0.95 ⇒ risultato
    /// difendibile; valori bassi ⇒ probabile fluke da data-snooping.
    /// </summary>
    public static double Deflated(double observedSharpe, int observations, double skewness, double kurtosis, double varianceOfTrialSharpes, int trials)
    {
        var srStar = ExpectedMaxSharpe(varianceOfTrialSharpes, trials);
        return ProbabilisticSharpe(observedSharpe, srStar, observations, skewness, kurtosis);
    }

    /// <summary>
    /// Overload di comodo: dato l'insieme degli Sharpe <b>per-periodo</b> di tutti i tentativi
    /// (<paramref name="allTrialSharpes"/>) e la serie di rendimenti del migliore
    /// (<paramref name="bestStrategyReturns"/>), calcola il DSR ricavando osservato/momenti/T dalla
    /// serie e la varianza cross-trial dall'insieme. <paramref name="trials"/> di default = numero di
    /// tentativi passati; sovrascrivibile se il conteggio reale dei test è maggiore.
    /// </summary>
    public static double Deflated(IReadOnlyList<double> allTrialSharpes, IReadOnlyList<double> bestStrategyReturns, int? trials = null)
    {
        if (bestStrategyReturns is null || bestStrategyReturns.Count < 2) return double.NaN;
        if (allTrialSharpes is null || allTrialSharpes.Count == 0) return double.NaN;

        var observed = ReturnMoments.PerPeriodSharpe(bestStrategyReturns);
        var skew = ReturnMoments.Skewness(bestStrategyReturns);
        var kurt = ReturnMoments.Kurtosis(bestStrategyReturns);
        var variance = TrialVariance(allTrialSharpes);
        var n = trials ?? allTrialSharpes.Count;
        return Deflated(observed, bestStrategyReturns.Count, skew, kurt, variance, n);
    }

    /// <summary>Varianza di popolazione degli Sharpe dei tentativi (input per <see cref="ExpectedMaxSharpe"/>).</summary>
    public static double TrialVariance(IReadOnlyList<double> trialSharpes)
    {
        if (trialSharpes is null || trialSharpes.Count < 2) return 0.0;
        var mean = 0.0;
        for (var i = 0; i < trialSharpes.Count; i++) mean += trialSharpes[i];
        mean /= trialSharpes.Count;
        var s = 0.0;
        for (var i = 0; i < trialSharpes.Count; i++)
        {
            var d = trialSharpes[i] - mean;
            s += d * d;
        }
        return s / trialSharpes.Count;
    }
}
