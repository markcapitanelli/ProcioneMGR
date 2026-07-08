namespace ProcioneMGR.Services.Validation;

/// <summary>
/// Verdetto di rigore sulla selezione di UN candidato scelto tra molti: incapsula lo Sharpe
/// osservato, la soglia SR* attesa per puro effetto del test multiplo, e il Deflated Sharpe (la
/// probabilità che l'edge sia reale dopo la correzione). Pensato per essere loggato via
/// <c>IExperimentTracker</c> e mostrato nelle UI di selezione accanto allo Sharpe grezzo.
/// </summary>
public sealed record SelectionValidation(
    double ObservedSharpePerPeriod,
    double ExpectedMaxSharpePerPeriod,
    double DeflatedSharpe,
    int Trials,
    int Observations,
    double Skewness,
    double Kurtosis)
{
    /// <summary>Convenzione Bailey–López de Prado: DSR &gt; 0.95 ⇒ risultato difendibile.</summary>
    public bool IsSignificant => DeflatedSharpe > 0.95;

    /// <summary>Metriche in forma piatta (chiave→valore) per il logging sull'experiment tracker.</summary>
    public IReadOnlyDictionary<string, decimal> ToMetrics() => new Dictionary<string, decimal>
    {
        ["DeflatedSharpe"] = (decimal)DeflatedSharpe,
        ["ExpectedMaxSharpePerPeriod"] = (decimal)ExpectedMaxSharpePerPeriod,
        ["ObservedSharpePerPeriod"] = (decimal)ObservedSharpePerPeriod,
        ["Trials"] = Trials,
        ["Observations"] = Observations,
    };
}

/// <summary>
/// Applica il Deflated Sharpe al pattern ricorrente della piattaforma: "ho provato N combinazioni,
/// ho scelto la migliore — è significativa?". Centralizza la conversione da Sharpe <b>annualizzato</b>
/// (come lo calcola <c>Optimization.Statistics</c>) a <b>per-periodo</b> (come lo richiede il DSR),
/// così i chiamanti (OptimizationEngine, Discovery, AlphaMining) non ripetono la de-annualizzazione.
/// Puro e deterministico.
/// </summary>
public static class SelectionValidator
{
    /// <summary>
    /// Verdetto DSR dato l'insieme degli Sharpe OOS <b>annualizzati</b> di tutti i tentativi e la
    /// serie di rendimenti periodici del candidato scelto. <paramref name="periodsPerYear"/> serve a
    /// riportare gli Sharpe alla stessa scala per-periodo dei momenti calcolati sui rendimenti.
    /// <paramref name="trials"/> (default = numero di Sharpe passati) permette di dichiarare un conteggio
    /// di test maggiore (es. combinazioni × finestre) se più conservativo.
    /// </summary>
    public static SelectionValidation Validate(
        IReadOnlyList<decimal> annualizedTrialSharpes,
        IReadOnlyList<double> chosenPeriodicReturns,
        int periodsPerYear,
        int? trials = null)
    {
        ArgumentNullException.ThrowIfNull(annualizedTrialSharpes);
        ArgumentNullException.ThrowIfNull(chosenPeriodicReturns);

        var annualizationFactor = periodsPerYear > 0 ? Math.Sqrt(periodsPerYear) : 1.0;

        var trialSharpesPerPeriod = new double[annualizedTrialSharpes.Count];
        for (var i = 0; i < annualizedTrialSharpes.Count; i++)
            trialSharpesPerPeriod[i] = (double)annualizedTrialSharpes[i] / annualizationFactor;

        var observed = ReturnMoments.PerPeriodSharpe(chosenPeriodicReturns);
        var skew = ReturnMoments.Skewness(chosenPeriodicReturns);
        var kurt = ReturnMoments.Kurtosis(chosenPeriodicReturns);
        var variance = DeflatedSharpeRatio.TrialVariance(trialSharpesPerPeriod);
        var n = trials ?? annualizedTrialSharpes.Count;

        var srStar = DeflatedSharpeRatio.ExpectedMaxSharpe(variance, n);
        var dsr = DeflatedSharpeRatio.ProbabilisticSharpe(observed, srStar, chosenPeriodicReturns.Count, skew, kurt);

        return new SelectionValidation(observed, srStar, dsr, n, chosenPeriodicReturns.Count, skew, kurt);
    }
}
