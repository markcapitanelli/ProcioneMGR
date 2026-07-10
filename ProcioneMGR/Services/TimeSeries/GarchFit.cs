using MathNet.Numerics.Distributions;

namespace ProcioneMGR.Services.TimeSeries;

/// <summary>Risultato della stima MLE di un GARCH(1,1).</summary>
public sealed class GarchFit
{
    public required double Omega { get; init; }
    public required double Alpha { get; init; }
    public required double Beta { get; init; }

    /// <summary>
    /// Gradi di libertà ν delle innovazioni Student-t (null se il fit è gaussiano). ν basso = code
    /// più grasse; ν→∞ ≈ normale. Sotto ~10 le mosse estreme sono molto più probabili della normale.
    /// </summary>
    public double? DegreesOfFreedom { get; init; }

    /// <summary>Varianza condizionale in-sample (σ²ₜ), allineata per indice ai rendimenti usati in Fit.</summary>
    public required IReadOnlyList<double> ConditionalVariances { get; init; }

    public required double LogLikelihood { get; init; }

    /// <summary>α+β: quanto lentamente uno shock di volatilità decade. Vicino a 1 -> shock molto persistenti.</summary>
    public double Persistence => Alpha + Beta;

    /// <summary>Varianza di lungo periodo implicita dal modello: ω / (1 - α - β).</summary>
    public double LongRunVariance => Persistence < 1.0 ? Omega / (1.0 - Persistence) : double.NaN;

    /// <summary>
    /// Previsione della varianza a <paramref name="horizonSteps"/> passi dall'ultima osservazione,
    /// via la formula standard di mean-reversion del GARCH:
    ///   σ²ₜ₊ₕ = varianzaLungoPeriodo + persistenza^h · (σ²ₜ - varianzaLungoPeriodo)
    /// </summary>
    public double ForecastVariance(int horizonSteps)
    {
        if (horizonSteps < 1) throw new ArgumentOutOfRangeException(nameof(horizonSteps));
        if (ConditionalVariances.Count == 0) throw new InvalidOperationException("Nessuna varianza condizionale disponibile.");

        var lastVariance = ConditionalVariances[^1];
        if (Persistence >= 1.0)
        {
            return lastVariance; // processo non stazionario: nessuna mean-reversion affidabile
        }

        var longRun = LongRunVariance;
        return longRun + Math.Pow(Persistence, horizonSteps) * (lastVariance - longRun);
    }

    /// <summary>
    /// Quantile p del RENDIMENTO previsto a <paramref name="horizonSteps"/> passi (media ≈ 0),
    /// consapevole delle code grasse. Per p&lt;0.5 è negativo = perdita di coda (VaR / distanza di stop
    /// prudente). Sotto Student-t usa il quantile t·√((ν-2)/ν) — più ampio della normale a parità di σ;
    /// sotto fit gaussiano coincide col quantile normale zₚ·σ.
    /// </summary>
    /// <param name="p">Probabilità cumulata, es. 0.01 per il VaR all'1% (mossa avversa "1 su 100").</param>
    public double TailQuantile(double p, int horizonSteps)
    {
        if (p is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(p));
        var sigma = Math.Sqrt(Math.Max(0.0, ForecastVariance(horizonSteps)));
        if (DegreesOfFreedom is double nu)
        {
            // Student-t standardizzata a varianza 1: si scala il quantile t per √((ν-2)/ν).
            return StudentT.InvCDF(0.0, 1.0, nu, p) * Math.Sqrt((nu - 2.0) / nu) * sigma;
        }
        return Normal.InvCDF(0.0, 1.0, p) * sigma;
    }
}
