namespace ProcioneMGR.Services.Validation;

/// <summary>
/// <b>Potenza del gate anti-overfitting</b>: qual è l'edge più piccolo che questa piattaforma è in
/// grado di CONFERMARE, date la lunghezza dell'holdout e l'ampiezza della ricerca.
///
/// Nasce da un'osservazione del proprietario (2026-07-28): «di candidati se ne trovano, ma non
/// consolidano mai». Le spiegazioni possibili sono due, opposte, e finora nessuna misura le
/// separava:
///  (a) <b>non c'è edge</b>, e i gate fanno il loro mestiere;
///  (b) <b>i gate non hanno la potenza</b> per confermare un edge della grandezza che esiste
///      davvero, e quindi «zero sopravvissuti» non è un'informazione sul mercato ma sullo strumento.
///
/// L'esperimento di controllo esistente (fase <c>control</c>) pianta un edge e la pipeline lo trova
/// con DSR 1,00 — ma quell'edge oscilla del ±4,6% attorno alla media, cioè <b>quindici volte</b> il
/// round-turn dello 0,30%. Dimostra che la macchina non è rotta; non dice niente sugli edge
/// realistici. La differenza fra «non è rotta» e «ha la potenza» è esattamente ciò che manca.
///
/// Qui la domanda si chiude in forma esatta, invertendo NUMERICAMENTE il gate vero
/// (<see cref="DeflatedSharpeRatio"/>) invece di modellarlo: si cerca lo Sharpe osservato che porta
/// il DSR esattamente alla soglia. Invertire la funzione reale, e non una mia formula equivalente,
/// garantisce che la risposta descriva <i>quel</i> gate — se un giorno la formula cambia, questa
/// misura cambia con lei invece di raccontare il gate di ieri.
/// </summary>
public static class GatePowerAnalyzer
{
    /// <summary>Soglia di DSR oltre la quale il candidato è difendibile (default della pipeline).</summary>
    public const double DefaultDsrThreshold = 0.95;

    /// <summary>
    /// Sharpe <b>per-periodo</b> minimo perché il DSR raggiunga la soglia. Bisezione sul gate reale:
    /// il DSR è monotòno crescente nello Sharpe osservato a parità di tutto il resto, quindi la
    /// bisezione converge e non può restituire un punto che il gate non confermerebbe.
    ///
    /// Restituisce <c>null</c> se nemmeno uno Sharpe per-periodo di 5 (assurdo: ~70 annualizzato su
    /// dati giornalieri) basta — cioè se con quel numero di tentativi e quella lunghezza di track
    /// <b>nessun edge</b> è confermabile. Che è un esito, non un errore.
    /// </summary>
    public static double? MinDetectablePerPeriodSharpe(
        int observations,
        double varianceOfTrialSharpes,
        int trials,
        double skewness = 0.0,
        double kurtosis = 3.0,
        double dsrThreshold = DefaultDsrThreshold)
    {
        if (observations < 3) return null;

        double Dsr(double sr) => DeflatedSharpeRatio.Deflated(
            sr, observations, skewness, kurtosis, varianceOfTrialSharpes, trials);

        const double hi0 = 5.0;
        if (Dsr(hi0) < dsrThreshold) return null;   // irraggiungibile a qualunque grandezza sensata

        double lo = 0.0, hi = hi0;
        for (var i = 0; i < 200; i++)
        {
            var mid = (lo + hi) / 2.0;
            if (Dsr(mid) < dsrThreshold) lo = mid; else hi = mid;
        }
        return hi;
    }

    /// <summary>
    /// Lo stesso numero, ANNUALIZZATO: è l'unità in cui si ragiona di strategie, e in cui si può
    /// dire «esiste al mondo qualcosa del genere?». Sharpe per-periodo × √(periodi all'anno).
    /// </summary>
    public static double? MinDetectableAnnualSharpe(
        int observations,
        double varianceOfTrialSharpes,
        int trials,
        int periodsPerYear,
        double skewness = 0.0,
        double kurtosis = 3.0,
        double dsrThreshold = DefaultDsrThreshold)
    {
        var perPeriod = MinDetectablePerPeriodSharpe(
            observations, varianceOfTrialSharpes, trials, skewness, kurtosis, dsrThreshold);
        return perPeriod is double sr ? sr * Math.Sqrt(periodsPerYear) : null;
    }

    /// <summary>
    /// Quante osservazioni servirebbero perché un edge di <paramref name="targetAnnualSharpe"/>
    /// superi il gate, a parità di tentativi. È la leva più economica: la soglia scende come
    /// 1/√T, mentre scende solo come √(log N) togliendo tentativi.
    ///
    /// <c>null</c> se nemmeno un track record di 100.000 osservazioni basta — succede quando la
    /// varianza cross-trial è così alta che SR* supera l'edge cercato: lì non è questione di
    /// pazienza, è la RICERCA a essere troppo larga.
    /// </summary>
    public static int? ObservationsNeededFor(
        double targetAnnualSharpe,
        double varianceOfTrialSharpes,
        int trials,
        int periodsPerYear,
        double skewness = 0.0,
        double kurtosis = 3.0,
        double dsrThreshold = DefaultDsrThreshold)
    {
        if (targetAnnualSharpe <= 0 || periodsPerYear <= 0) return null;
        var perPeriod = targetAnnualSharpe / Math.Sqrt(periodsPerYear);

        // Il DSR cresce con T a parità di Sharpe: si cerca il primo T che basta, per raddoppi
        // successivi e poi bisezione — nessuna formula chiusa, nessun rischio di divergere dal gate.
        const int cap = 100_000;
        int Feasible(int t) =>
            DeflatedSharpeRatio.Deflated(perPeriod, t, skewness, kurtosis, varianceOfTrialSharpes, trials)
                >= dsrThreshold ? 1 : 0;

        if (Feasible(cap) == 0) return null;

        int lo = 3, hi = cap;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (Feasible(mid) == 1) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>
    /// <b>Anni di fuori campione</b> necessari perché un edge di <paramref name="trueAnnualSharpe"/>
    /// superi il gate. È la domanda nell'unità in cui si decide: non «quante osservazioni» ma
    /// «quanto devo aspettare».
    ///
    /// Diverso da <see cref="ObservationsNeededFor"/> in un punto sostanziale: lì la varianza
    /// cross-trial è un INPUT FISSO, qui è <b>accoppiata</b> a T (σ ≈ 1/√T sotto il nullo), che è la
    /// situazione reale — allungare l'holdout riduce sia l'incertezza sullo Sharpe osservato sia la
    /// dispersione fra i tentativi, e quindi l'asticella SR*. Trattarle come indipendenti e iterare
    /// produce un punto fisso che OSCILLA e non converge mai: è l'errore che ho fatto scrivendo la
    /// prima versione di questa misura, e il motivo per cui la tabella diceva «oltre 50 anni» ovunque.
    ///
    /// Qui si risolve per bisezione diretta su T, con la varianza ricalcolata a ogni valutazione. Il
    /// DSR è monotòno crescente in T a parità di edge (l'incertezza scende e SR* pure), quindi la
    /// bisezione è ben posta.
    /// </summary>
    public static double? YearsNeededFor(
        double trueAnnualSharpe,
        int trials,
        int periodsPerYear,
        double skewness = 0.0,
        double kurtosis = 3.0,
        double dsrThreshold = DefaultDsrThreshold,
        double maxYears = 200.0)
    {
        if (trueAnnualSharpe <= 0 || periodsPerYear <= 0) return null;
        var perPeriod = trueAnnualSharpe / Math.Sqrt(periodsPerYear);

        bool Ok(double years)
        {
            var t = (int)Math.Round(years * periodsPerYear);
            if (t < 3) return false;
            // La varianza cross-trial NON è un parametro libero: sotto il nullo lo Sharpe stimato su
            // t osservazioni ha deviazione standard ~1/√t, ed è quella che alimenta SR*.
            return DeflatedSharpeRatio.Deflated(perPeriod, t, skewness, kurtosis, 1.0 / t, trials)
                   >= dsrThreshold;
        }

        if (!Ok(maxYears)) return null;

        double lo = 1.0 / 365.0, hi = maxYears;
        for (var i = 0; i < 200; i++)
        {
            var mid = (lo + hi) / 2.0;
            if (Ok(mid)) hi = mid; else lo = mid;
        }
        return hi;
    }

    /// <summary>
    /// Una riga della curva di potenza: a parità di holdout, quanto sale l'asticella al crescere
    /// dei tentativi.
    /// </summary>
    public readonly record struct PowerPoint(int Trials, double? MinAnnualSharpe);

    /// <summary>
    /// Curva di potenza sull'ampiezza della ricerca. È il grafico che risponde alla domanda «ci
    /// stiamo sbarrando la strada da soli cercando troppo?».
    /// </summary>
    public static IReadOnlyList<PowerPoint> PowerCurveOverTrials(
        int observations,
        double varianceOfTrialSharpes,
        int periodsPerYear,
        IEnumerable<int> trialCounts,
        double skewness = 0.0,
        double kurtosis = 3.0,
        double dsrThreshold = DefaultDsrThreshold)
        => trialCounts
            .Select(n => new PowerPoint(n, MinDetectableAnnualSharpe(
                observations, varianceOfTrialSharpes, n, periodsPerYear, skewness, kurtosis, dsrThreshold)))
            .ToList();
}
