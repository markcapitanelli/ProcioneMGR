using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Execution;

/// <summary>
/// Algoritmo di esecuzione: dato un ordine "intenzione" (<see cref="ExecutionIntent"/>) e le candele
/// del timeframe di esecuzione (es. 5m dentro una barra di decisione 4h), produce un
/// <see cref="ExecutionPlan"/> di ordini figli. È il layer che oggi manca fra "la strategia decide"
/// e "l'ordine parte" (rif. <c>docs/ROADMAP-QLIB.md §1.2</c>).
///
/// <see cref="ImmediateExecutionAlgorithm"/> riproduce il comportamento ODIERNO (un solo ordine) ed è
/// il default retrocompatibile; TWAP/VWAP/Iceberg distribuiscono l'ordine per ridurre l'impatto di
/// mercato. Puri/stateless → registrabili come Singleton.
/// </summary>
public interface IExecutionAlgorithm
{
    /// <summary>Nome tecnico: "Immediate" | "Twap" | "Vwap" | "Iceberg".</summary>
    string Name { get; }

    /// <summary>
    /// Costruisce il piano di ordini figli. Ogni implementazione garantisce che la somma delle
    /// quantità delle fette sia ESATTAMENTE <see cref="ExecutionIntent.TotalQuantity"/> (nessuna
    /// quantità persa o creata per arrotondamento) e che ogni indice di candela sia valido.
    /// </summary>
    ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList<OhlcvData> fineGrainedCandles, ExecutionParameters parameters);
}
