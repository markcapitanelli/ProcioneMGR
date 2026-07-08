using ProcioneMGR.Services.Alpha.Alpha158;
using ProcioneMGR.Services.AlphaMining;

namespace ProcioneMGR.Services.Alpha;

/// <summary>
/// Crea istanze di fattore per nome ed espone i "prototipi" per popolare la UI (elenco fattori +
/// definizioni parametri). Gli 8 fattori "storici" restano uno switch case esplicito; il catalogo
/// <see cref="Alpha158Catalog"/> (pochi operatori × molti orizzonti) si aggiunge in blocco senza
/// una classe per feature — stesso principio additivo del resto della piattaforma
/// (rif. <c>docs/ROADMAP-QLIB.md §1.1</c>).
/// </summary>
public interface IAlphaFactorFactory
{
    /// <summary>Istanze "vuote" per leggere DisplayName/Category/ParameterDefinitions nella UI.</summary>
    IReadOnlyList<IAlphaFactor> Prototypes { get; }

    IAlphaFactor Create(string factorName);
}

public sealed class AlphaFactorFactory : IAlphaFactorFactory
{
    // Gli 8 fattori scritti a mano (rif. Jansen cap. 24): prototipi con parametri regolabili.
    private static readonly IAlphaFactor[] HandwrittenPrototypes =
    [
        new MomentumFactor(),
        new MeanReversionFactor(),
        new RealizedVolatilityFactor(),
        new ParkinsonVolatilityFactor(),
        new RelativeVolumeFactor(),
        new RsiFactor(),
        new MacdFactor(),
        new DistanceFromMaFactor(),
    ];

    public AlphaFactorFactory()
    {
        // Prototipi = 8 fattori storici + intero catalogo Alpha158 agli orizzonti di default.
        // Ogni voce del catalogo è già una feature concreta (orizzonte cotto nel nome), quindi
        // compare direttamente nel selettore fattori senza modifiche ai consumatori.
        Prototypes = [.. HandwrittenPrototypes, .. Alpha158Catalog.BuildCatalog()];
    }

    public IReadOnlyList<IAlphaFactor> Prototypes { get; }

    public IAlphaFactor Create(string factorName) => factorName switch
    {
        "Momentum" => new MomentumFactor(),
        "MeanReversion" => new MeanReversionFactor(),
        "RealizedVol" => new RealizedVolatilityFactor(),
        "ParkinsonVol" => new ParkinsonVolatilityFactor(),
        "RelativeVolume" => new RelativeVolumeFactor(),
        "RsiFactor" => new RsiFactor(),
        "MacdFactor" => new MacdFactor(),
        "DistanceFromMa" => new DistanceFromMaFactor(),
        // Fattori Alpha158: ricostruiti dal nome (qualsiasi orizzonte), così il round-trip di
        // persistenza di un SavedMlModel resta valido anche fuori dagli orizzonti di default.
        _ when Alpha158Catalog.TryCreate(factorName, out var a158) => a158,
        // Alpha "minati" (§1.7): il nome è l'espressione serializzata con prefisso "expr:".
        _ when factorName.StartsWith(AlphaExpressionFactor.NamePrefix, StringComparison.Ordinal) => AlphaExpressionFactor.FromName(factorName),
        _ => throw new NotSupportedException($"Fattore non supportato: '{factorName}'."),
    };
}
