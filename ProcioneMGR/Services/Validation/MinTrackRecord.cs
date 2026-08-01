using MathNet.Numerics.Distributions;

namespace ProcioneMGR.Services.Validation;

/// <summary>
/// [F4 PRD Valore] Minimum Track Record Length e potenza statistica di un run di ricerca, da
/// Bailey &amp; López de Prado ("The Sharpe Ratio Efficient Frontier", J. of Risk 2012; "The
/// Deflated Sharpe Ratio", JPM 2014).
///
/// <para>Il punto non è aggiungere un giudice — ne abbiamo già (DSR, PBO, gemello nullo) — ma far
/// PARLARE l'aritmetica PRIMA di spendere i backtest: su una finestra corta, lo Sharpe minimo che
/// può superare la soglia è calcolabile a priori, e un run che non può produrre promossi deve
/// dirlo in testa, non scriverlo come «0 sopravvissuti» dopo ore di CPU. Le soglie NON si toccano:
/// il benchmark esterno (Harvey-Liu-Zhu, t&gt;3) le conferma.</para>
///
/// <para>Tutte le grandezze sono PER-PERIODO (la frequenza dei rendimenti): chi ragiona in Sharpe
/// annualizzato converte con √ppy ai bordi — <see cref="AnnualizedToPerPeriod"/>.</para>
/// </summary>
public static class MinTrackRecord
{
    /// <summary>Eulero-Mascheroni, per l'E[max] dei tentativi (Bailey-LdP 2014, eq. del DSR).</summary>
    private const double EulerMascheroni = 0.5772156649015329;

    /// <summary>Sharpe annualizzato → per-periodo (ppy = periodi per anno, es. 8760 per 1h).</summary>
    public static double AnnualizedToPerPeriod(double annualizedSharpe, int periodsPerYear) =>
        periodsPerYear > 0 ? annualizedSharpe / Math.Sqrt(periodsPerYear) : annualizedSharpe;

    /// <summary>Per-periodo → annualizzato.</summary>
    public static double PerPeriodToAnnualized(double perPeriodSharpe, int periodsPerYear) =>
        periodsPerYear > 0 ? perPeriodSharpe * Math.Sqrt(periodsPerYear) : perPeriodSharpe;

    /// <summary>
    /// MinTRL = 1 + [1 − γ₃·SR + ((γ₄−1)/4)·SR²] · (z_α / (SR − SR*))², in osservazioni della
    /// frequenza dei rendimenti. È il numero minimo di osservazioni perché uno Sharpe osservato
    /// <paramref name="observedSr"/> sia distinguibile dalla soglia <paramref name="benchmarkSr"/>
    /// con confidenza <paramref name="confidence"/>. Se SR ≤ SR*, nessuna lunghezza basta: +∞.
    /// </summary>
    /// <param name="skew">γ₃ dei rendimenti (0 = gaussiani).</param>
    /// <param name="kurtosis">γ₄ dei rendimenti (3 = gaussiani).</param>
    public static double MinTrl(
        double observedSr, double benchmarkSr, double confidence = 0.95,
        double skew = 0.0, double kurtosis = 3.0)
    {
        if (observedSr <= benchmarkSr) return double.PositiveInfinity;
        var z = Normal.InvCDF(0, 1, confidence);
        var adjust = 1.0 - skew * observedSr + (kurtosis - 1.0) / 4.0 * observedSr * observedSr;
        var ratio = z / (observedSr - benchmarkSr);
        return 1.0 + adjust * ratio * ratio;
    }

    /// <summary>
    /// L'INVERSA che serve al power check: date <paramref name="observations"/> osservazioni
    /// disponibili, quale Sharpe per-periodo serve perché il run possa promuovere qualcosa?
    /// Risolta per bisezione su MinTrl (monotona decrescente in SR sopra la soglia).
    /// Ritorna +∞ se nemmeno uno Sharpe per-periodo enorme basterebbe (finestra degenere).
    /// </summary>
    public static double MinDetectableSharpe(
        int observations, double benchmarkSr, double confidence = 0.95,
        double skew = 0.0, double kurtosis = 3.0)
    {
        if (observations < 2) return double.PositiveInfinity;

        // Limite alto largo: uno Sharpe PER-PERIODO di 5 è oltre qualunque cosa reale.
        var lo = benchmarkSr + 1e-9;
        var hi = benchmarkSr + 5.0;
        if (MinTrl(hi, benchmarkSr, confidence, skew, kurtosis) > observations)
        {
            return double.PositiveInfinity;
        }

        for (var i = 0; i < 200; i++)
        {
            var mid = (lo + hi) / 2.0;
            if (MinTrl(mid, benchmarkSr, confidence, skew, kurtosis) > observations) lo = mid;
            else hi = mid;
        }
        return hi;
    }

    /// <summary>
    /// E[max] dello Sharpe stimato su <paramref name="trials"/> tentativi indipendenti SENZA
    /// alcun edge (Bailey-LdP 2014): è la soglia SR* che il DSR usa come benchmark — con N
    /// tentativi, il puro caso arriva fin qui, e un candidato deve battere QUESTO, non lo zero.
    /// <paramref name="observations"/> determina la varianza dello stimatore (V[SR̂] ≈ 1/T sotto
    /// il nullo).
    /// </summary>
    public static double ExpectedMaxSharpeUnderNull(int trials, int observations)
    {
        if (trials <= 1 || observations < 2) return 0.0;
        var sd = Math.Sqrt(1.0 / observations);
        var n = (double)trials;
        var a = Normal.InvCDF(0, 1, 1.0 - 1.0 / n);
        var b = Normal.InvCDF(0, 1, 1.0 - 1.0 / (n * Math.E));
        return sd * ((1.0 - EulerMascheroni) * a + EulerMascheroni * b);
    }
}
