namespace ProcioneMGR.Services.ML;

/// <summary>
/// Crea i predittori di rendimento BASE per nome (Linear/RandomForest/GradientBoosting/Mlp).
/// Centralizza lo switch che prima viveva duplicato in /ml, così sia la UI sia lo
/// <see cref="StackedReturnPredictor"/> (che deve istanziare freschi i modelli base per l'OOF)
/// usano la stessa fonte. Non include "Stacked" per costruzione: un ensemble di ensemble non ha
/// senso qui e creerebbe ricorsione.
/// </summary>
public static class ReturnPredictorCatalog
{
    /// <summary>Tipi base combinabili in uno stacking.</summary>
    public static readonly IReadOnlyList<string> BaseTypes = ["Linear", "RandomForest", "GradientBoosting", "Mlp"];

    public static IReturnPredictor CreateBase(string modelType) => modelType switch
    {
        "RandomForest" => new RandomForestReturnPredictor(),
        "GradientBoosting" => new GradientBoostingReturnPredictor(),
        "Mlp" => new MlpReturnPredictor(),
        "Linear" => new LinearReturnPredictor(),
        _ => throw new NotSupportedException($"Modello base non supportato per lo stacking: '{modelType}'."),
    };
}
