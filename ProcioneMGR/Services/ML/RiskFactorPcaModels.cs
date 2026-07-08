namespace ProcioneMGR.Services.ML;

/// <summary>Una componente principale: quota di varianza spiegata, loading per simbolo, e la serie temporale del fattore.</summary>
public sealed class PrincipalComponent
{
    /// <summary>Indice della componente, 1-based (1 = quella con più varianza spiegata).</summary>
    public required int Index { get; init; }

    /// <summary>Quota di varianza totale spiegata da questa componente, in [0,1].</summary>
    public required double ExplainedVarianceRatio { get; init; }

    /// <summary>Peso (coefficiente dell'autovettore) di ciascun simbolo su questa componente.</summary>
    public required IReadOnlyDictionary<string, double> Loadings { get; init; }

    /// <summary>Il "risk factor" stesso: punteggio della componente per ogni osservazione temporale.</summary>
    public required IReadOnlyList<double> Scores { get; init; }
}

public sealed class RiskFactorPcaResult
{
    public required IReadOnlyList<string> Symbols { get; init; }
    public required IReadOnlyList<PrincipalComponent> Components { get; init; }

    /// <summary>Somma delle quote di varianza spiegata dalle componenti estratte (quanto del rischio comune è catturato).</summary>
    public double TotalExplainedVarianceRatio => Components.Sum(c => c.ExplainedVarianceRatio);
}
