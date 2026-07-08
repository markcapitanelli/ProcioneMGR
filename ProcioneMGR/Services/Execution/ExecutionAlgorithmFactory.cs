namespace ProcioneMGR.Services.Execution;

/// <summary>
/// Crea gli algoritmi di esecuzione per nome ed espone l'elenco per la UI. Stesso pattern di
/// <c>StrategyFactory</c>/<c>AlphaFactorFactory</c>: aggiungere un algoritmo = nuova classe + un case.
/// </summary>
public interface IExecutionAlgorithmFactory
{
    IReadOnlyList<IExecutionAlgorithm> All { get; }

    /// <summary>Ritorna l'algoritmo richiesto; "Immediate" come fallback retrocompatibile per nomi ignoti.</summary>
    IExecutionAlgorithm Create(string name);
}

public sealed class ExecutionAlgorithmFactory : IExecutionAlgorithmFactory
{
    public IReadOnlyList<IExecutionAlgorithm> All { get; } =
    [
        new ImmediateExecutionAlgorithm(),
        new TwapExecutionAlgorithm(),
        new VwapExecutionAlgorithm(),
        new IcebergExecutionAlgorithm(),
        new AdaptiveExecutionAlgorithm(),
    ];

    public IExecutionAlgorithm Create(string name) => name switch
    {
        "Twap" => new TwapExecutionAlgorithm(),
        "Vwap" => new VwapExecutionAlgorithm(),
        "Iceberg" => new IcebergExecutionAlgorithm(),
        "Adaptive" => new AdaptiveExecutionAlgorithm(),
        // "Immediate" e qualunque nome non riconosciuto ⇒ comportamento odierno (un solo ordine).
        _ => new ImmediateExecutionAlgorithm(),
    };
}
