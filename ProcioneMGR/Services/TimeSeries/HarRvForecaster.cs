using MathNet.Numerics.LinearAlgebra;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.TimeSeries;

/// <summary>
/// [C3 roadmap integrazione] HAR-RV (Corsi 2009): previsione della varianza realizzata giornaliera
/// con una OLS a 3 regressori — RV di ieri, RV media dell'ultima settimana (5 gg), RV media
/// dell'ultimo mese (22 gg). L'idea è la "cascata eterogenea": operatori con orizzonti diversi
/// reagiscono a volatilità misurate su scale diverse. Sui crypto la letteratura la dà migliore del
/// GARCH(1,1) su QLIKE a 1-5 giorni; il gate C3 lo verifica sui NOSTRI dati prima di adottarla
/// (fase `volgate` di PlatformExpand). La GARCH Student-t resta comunque per i quantili di coda.
///
/// Convenzioni:
///  - input/output su scala VARIANZA giornaliera (RV = somma dei quadrati dei log-rendimenti 5m);
///  - <see cref="ForecastSeries"/> è CAUSALE: il valore all'indice i usa solo rv[0..i] (fit
///    incluso), e prevede la MEDIA della RV sui prossimi <c>horizon</c> giorni;
///  - stima sui LIVELLI di RV con pavimento di positività sulla previsione (la variante log esiste
///    in letteratura ma il modello base è sui livelli, e la OLS a 3 regressori è la promessa di C3).
/// </summary>
public static class HarRvForecaster
{
    /// <summary>Giorni della componente settimanale.</summary>
    public const int WeekWindow = 5;

    /// <summary>Giorni della componente mensile (giorni di trading della letteratura; i crypto quotano 365 ma la scala resta quella).</summary>
    public const int MonthWindow = 22;

    /// <summary>Minimo di osservazioni di fit perché una previsione venga emessa.</summary>
    public const int MinFitRows = 60;

    /// <summary>Pavimento della previsione di varianza (mai zero o negativa).</summary>
    public const double MinVariance = 1e-12;

    /// <summary>
    /// Serie causale delle previsioni: all'indice i la previsione della RV media sui giorni
    /// i+1..i+<paramref name="horizon"/>, con la OLS rifittata a ogni i sui soli dati passati.
    /// Null finché i regressori (22 gg) e le righe minime di fit non ci sono.
    /// <paramref name="onLogRv"/>: variante log-HAR (regressione su ln RV, ritorno in livello con
    /// correzione di smearing exp(σ²/2)) — robusta ai salti estremi di RV che sui livelli
    /// distorcono i coefficienti; misurata in `volgate` accanto al modello base.
    /// </summary>
    public static double?[] ForecastSeries(IReadOnlyList<double> rv, int horizon = 1, bool onLogRv = false)
    {
        ArgumentNullException.ThrowIfNull(rv);
        ArgumentOutOfRangeException.ThrowIfLessThan(horizon, 1);

        var n = rv.Count;
        var result = new double?[n];

        // Serie di lavoro: livelli o log (pavimentati: un log di zero avvelenerebbe tutto).
        var w = new double[n];
        for (var t = 0; t < n; t++) w[t] = onLogRv ? Math.Log(Math.Max(rv[t], MinVariance)) : rv[t];

        // Regressori causali per ogni giorno t (usano w[t] incluso: sono noti a fine giornata t).
        var xd = new double[n];
        var xw = new double[n];
        var xm = new double[n];
        for (var t = 0; t < n; t++)
        {
            xd[t] = w[t];
            if (t >= WeekWindow - 1) xw[t] = Mean(w, t - WeekWindow + 1, t);
            if (t >= MonthWindow - 1) xm[t] = Mean(w, t - MonthWindow + 1, t);
        }

        for (var i = MonthWindow - 1 + MinFitRows + horizon; i < n; i++)
        {
            // Righe di fit: s da (primo giorno coi regressori pieni) a i-horizon, target = media
            // RV(s+1..s+horizon) (nel log-HAR: ln della media). L'ultima riga usa solo dati fino
            // a i: nessun look-ahead.
            var firstS = MonthWindow - 1;
            var rows = i - horizon - firstS + 1;
            if (rows < MinFitRows) continue;

            var design = Matrix<double>.Build.Dense(rows, 4);
            var target = Vector<double>.Build.Dense(rows);
            for (var r = 0; r < rows; r++)
            {
                var s = firstS + r;
                design[r, 0] = 1.0;
                design[r, 1] = xd[s];
                design[r, 2] = xw[s];
                design[r, 3] = xm[s];
                var t = Mean(rv, s + 1, s + horizon);
                target[r] = onLogRv ? Math.Log(Math.Max(t, MinVariance)) : t;
            }

            var ols = OlsRegression.Fit(design, target);
            var forecast = ols.Coefficients[0]
                         + ols.Coefficients[1] * xd[i]
                         + ols.Coefficients[2] * xw[i]
                         + ols.Coefficients[3] * xm[i];
            if (onLogRv)
            {
                // Ritorno in livello: E[RV] = exp(pred)·exp(σ²/2) (smearing gaussiano sui residui).
                var sigma2 = ols.Residuals.DotProduct(ols.Residuals) / Math.Max(rows - 4, 1);
                forecast = Math.Exp(forecast + sigma2 / 2.0);
            }
            result[i] = Math.Max(forecast, MinVariance);
        }

        return result;
    }

    private static double Mean(IReadOnlyList<double> xs, int from, int toInclusive)
    {
        double sum = 0;
        for (var k = from; k <= toInclusive; k++) sum += xs[k];
        return sum / (toInclusive - from + 1);
    }
}

/// <summary>
/// [C3] Varianza realizzata giornaliera dai dati intraday: somma dei quadrati dei log-rendimenti
/// barra-a-barra, attribuiti al giorno UTC della barra di arrivo (i crypto quotano 24/7: il
/// rendimento che scavalca la mezzanotte appartiene al giorno in cui atterra). I giorni con troppi
/// buchi (meno di <see cref="MinReturnsPerDay"/> rendimenti su 288 barre 5m) vengono SCARTATI, non
/// riempiti: una RV su mezzi dati è una sottostima silenziosa.
/// </summary>
public static class RealizedVariance
{
    /// <summary>Minimo di rendimenti intraday perché il giorno conti (288 barre 5m piene = 288 rendimenti col chaining).</summary>
    public const int MinReturnsPerDay = 240;

    /// <summary>RV giornaliera dalle candele intraday ordinate per timestamp. Ritorna (giorno UTC, RV).</summary>
    public static IReadOnlyList<(DateOnly Day, double Rv)> DailyFromIntraday(IReadOnlyList<OhlcvData> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var result = new List<(DateOnly, double)>();
        if (candles.Count < 2) return result;

        var currentDay = default(DateOnly);
        double sum = 0;
        var count = 0;

        for (var i = 1; i < candles.Count; i++)
        {
            var prev = candles[i - 1].Close;
            var cur = candles[i].Close;
            if (prev <= 0m || cur <= 0m) continue;

            var day = DateOnly.FromDateTime(candles[i].TimestampUtc);
            if (day != currentDay)
            {
                if (count >= MinReturnsPerDay) result.Add((currentDay, sum));
                currentDay = day;
                sum = 0;
                count = 0;
            }

            var r = Math.Log((double)(cur / prev));
            sum += r * r;
            count++;
        }
        if (count >= MinReturnsPerDay) result.Add((currentDay, sum));

        return result;
    }
}
