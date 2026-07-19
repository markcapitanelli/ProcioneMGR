using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// Il fattore "Sentiment" come feature ML di FABBRICA (Sentiment 2.0, opt-in): risolve da solo la
/// dipendenza dalle notizie via <see cref="ISentimentNewsProvider"/> e deriva il filtro simbolo
/// dalle candele ("BTC/USDT" → "BTC"), poi delega alla logica rolling anti-look-ahead di
/// <see cref="SentimentAlphaFactor"/> (che resta la classe usata direttamente da /sentiment).
/// Caveat FactorCache: la chiave di cache è nome+parametri+impronta candele, quindi una serie
/// sentiment aggiornata tra due chiamate identiche può restare stantia al massimo fino alla
/// candela successiva — staleness ≤ 1 barra, innocua per training e inferenza.
/// </summary>
public sealed class SentimentFeatureFactor(ISentimentNewsProvider newsProvider) : IAlphaFactor
{
    public string Name => "Sentiment";
    public string DisplayName => "Sentiment notizie (rolling)";
    public FactorCategory Category => FactorCategory.Sentiment;

    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } =
    [
        new("LookbackHours", "Finestra (ore)", 24m, 1m, 168m),
    ];

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> p)
    {
        if (candles.Count == 0) return [];
        var symbolFilter = ToBaseTicker(candles[0].Symbol);
        return new SentimentAlphaFactor(newsProvider.Snapshot, symbolFilter).Compute(candles, p);
    }

    /// <summary>"BTC/USDT" → "BTC" (il formato dei SymbolsJson delle news è il ticker base).</summary>
    private static string? ToBaseTicker(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        var slash = symbol.IndexOf('/');
        return slash > 0 ? symbol[..slash] : symbol;
    }
}
