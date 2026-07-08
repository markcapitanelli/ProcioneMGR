namespace ProcioneMGR.Services.Optimization.Bayesian;

/// <summary>Una dimensione dello spazio di ricerca: intervallo [Min,Max], eventualmente intero o a passo.</summary>
public sealed record ParameterDimension(string Name, double Min, double Max, bool IsInteger = false, double Step = 0);

/// <summary>Un punto valutato: i parametri (nello spazio reale) e il punteggio osservato dell'obiettivo.</summary>
public sealed record EvaluatedPoint(double[] Parameters, double Score);

/// <summary>
/// Spazio dei parametri per l'ottimizzazione bayesiana. Mappa fra coordinate <b>reali</b> (quelle
/// che il backtest riceve) e <b>normalizzate</b> [0,1]^d (quelle su cui lavora il Gaussian Process,
/// dove una singola lengthscale ha senso su tutte le dimensioni). La denormalizzazione "aggancia"
/// i valori a interi/passo e li limita all'intervallo. Puro/deterministico.
/// </summary>
public sealed class ParameterSpace(IReadOnlyList<ParameterDimension> dimensions)
{
    public IReadOnlyList<ParameterDimension> Dimensions { get; } =
        dimensions is { Count: > 0 } ? dimensions : throw new ArgumentException("Serve almeno una dimensione.", nameof(dimensions));

    /// <summary>Reale → [0,1]^d (per il GP). Dimensioni degeneri (Min==Max) mappano a 0.5.</summary>
    public double[] Normalize(double[] actual)
    {
        var z = new double[Dimensions.Count];
        for (var i = 0; i < z.Length; i++)
        {
            var dm = Dimensions[i];
            var range = dm.Max - dm.Min;
            z[i] = range > 0 ? Math.Clamp((actual[i] - dm.Min) / range, 0.0, 1.0) : 0.5;
        }
        return z;
    }

    /// <summary>[0,1]^d → reale, con snap a intero/passo e clamp all'intervallo.</summary>
    public double[] Denormalize(double[] unit)
    {
        var a = new double[Dimensions.Count];
        for (var i = 0; i < a.Length; i++)
        {
            var dm = Dimensions[i];
            var v = dm.Min + unit[i] * (dm.Max - dm.Min);
            a[i] = Snap(dm, v);
        }
        return a;
    }

    private static double Snap(ParameterDimension dm, double v)
    {
        v = Math.Clamp(v, dm.Min, dm.Max);
        if (dm.IsInteger) v = Math.Round(v);
        else if (dm.Step > 0) v = dm.Min + Math.Round((v - dm.Min) / dm.Step) * dm.Step;
        return Math.Clamp(v, dm.Min, dm.Max);
    }
}
