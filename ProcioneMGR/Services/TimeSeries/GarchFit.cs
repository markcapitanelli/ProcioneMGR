namespace ProcioneMGR.Services.TimeSeries;

/// <summary>Risultato della stima MLE di un GARCH(1,1).</summary>
public sealed class GarchFit
{
    public required double Omega { get; init; }
    public required double Alpha { get; init; }
    public required double Beta { get; init; }

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
}
