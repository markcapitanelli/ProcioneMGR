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
    // I fattori scritti a mano (rif. Jansen cap. 24): prototipi con parametri regolabili.
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
        // [3.8b] Order flow dai campi klines recuperati (T0.3): null sulle candele non reingerite,
        // quindi innocui sulle serie che non hanno ancora i campi estesi.
        new TakerImbalanceFactor(),
        new AvgTradeSizeFactor(),
    ];

    private readonly IReadOnlyList<IAlphaFactor> _basePrototypes;
    private readonly IReadOnlyList<IAlphaFactor>? _prototypesWithSentiment;
    private readonly ProcioneMGR.Services.Sentiment.ISentimentNewsProvider? _newsProvider;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<ProcioneMGR.Services.Sentiment.SentimentOptions>? _sentimentOptions;

    /// <summary>
    /// Parametri OPZIONALI di proposito: i molti call-site legacy (test, tool CLI, host Trading)
    /// continuano a usare <c>new AlphaFactorFactory()</c> senza fattore Sentiment; nel monolite la
    /// DI inietta provider e opzioni e il fattore "Sentiment" diventa disponibile — nei PROTOTIPI
    /// (UI/pipeline) solo con <c>Sentiment:EnableMlFeature=true</c> (opt-in, hot), in
    /// <see cref="Create"/> SEMPRE quando il provider c'è (round-trip dei SavedMlModel che l'hanno
    /// selezionato: un modello salvato col flag ON deve caricarsi anche a flag OFF).
    /// </summary>
    public AlphaFactorFactory(
        ProcioneMGR.Services.Sentiment.ISentimentNewsProvider? newsProvider = null,
        Microsoft.Extensions.Options.IOptionsMonitor<ProcioneMGR.Services.Sentiment.SentimentOptions>? sentimentOptions = null)
    {
        // Prototipi = 8 fattori storici + intero catalogo Alpha158 agli orizzonti di default.
        // Ogni voce del catalogo è già una feature concreta (orizzonte cotto nel nome), quindi
        // compare direttamente nel selettore fattori senza modifiche ai consumatori.
        _basePrototypes = [.. HandwrittenPrototypes, .. Alpha158Catalog.BuildCatalog()];
        _newsProvider = newsProvider;
        _sentimentOptions = sentimentOptions;
        if (newsProvider is not null)
        {
            _prototypesWithSentiment = [.. _basePrototypes, new ProcioneMGR.Services.Sentiment.SentimentFeatureFactor(newsProvider)];
        }
    }

    public IReadOnlyList<IAlphaFactor> Prototypes =>
        _prototypesWithSentiment is not null && _sentimentOptions?.CurrentValue.EnableMlFeature == true
            ? _prototypesWithSentiment
            : _basePrototypes;

    public IAlphaFactor Create(string factorName) => factorName switch
    {
        "Sentiment" when _newsProvider is not null => new ProcioneMGR.Services.Sentiment.SentimentFeatureFactor(_newsProvider),
        "Momentum" => new MomentumFactor(),
        "MeanReversion" => new MeanReversionFactor(),
        "RealizedVol" => new RealizedVolatilityFactor(),
        "ParkinsonVol" => new ParkinsonVolatilityFactor(),
        "RelativeVolume" => new RelativeVolumeFactor(),
        "TakerImbalance" => new TakerImbalanceFactor(),
        "AvgTradeSize" => new AvgTradeSizeFactor(),
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
