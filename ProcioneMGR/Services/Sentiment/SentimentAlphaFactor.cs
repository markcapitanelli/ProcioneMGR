using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>Una notizia già classificata/scorata, pronta per essere allineata alle candele.</summary>
public sealed record ScoredNewsItem(DateTime PublishedUtc, decimal SentimentScore, IReadOnlyList<string> Symbols);

/// <summary>
/// Fattore alpha da sentiment (cap. 14, con LLM al posto di LDA/lessici tradizionali — qui il
/// fallback lessicale finché non c'è una chiave LLM): media rolling del sentiment delle notizie
/// pubblicate nelle ultime <c>LookbackHours</c> ore prima di ogni candela.
///
/// DEVIAZIONE FLAGGATA (stesso trattamento di <c>MlStrategy</c> per <c>IStrategy</c>): non è
/// nello switch di <see cref="AlphaFactorFactory"/> perché richiede le notizie già scorate come
/// dipendenza esterna (non rappresentabile come parametri decimali di default) — si costruisce
/// direttamente passando le notizie, ma implementa comunque <see cref="IAlphaFactor"/> per
/// restare compatibile con <c>FactorEvaluator</c>/<c>DatasetBuilder</c>/<c>MlStrategy</c> senza
/// modifiche.
/// </summary>
public sealed class SentimentAlphaFactor(IReadOnlyList<ScoredNewsItem> news, string? symbolFilter = null) : IAlphaFactor
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
        var lookback = TimeSpan.FromHours((double)p.GetOrDefault("LookbackHours", 24m));

        var relevant = (symbolFilter is null
                ? news
                : news.Where(n => n.Symbols.Contains(symbolFilter, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(n => n.PublishedUtc)
            .ToList();

        var n = candles.Count;
        var result = new decimal?[n];
        var startIdx = 0;

        for (var i = 0; i < n; i++)
        {
            var candleTime = DateTime.SpecifyKind(candles[i].TimestampUtc, DateTimeKind.Utc);
            var windowStart = candleTime - lookback;

            // Le candele sono in ordine cronologico -> la soglia inferiore della finestra avanza
            // monotonicamente: possiamo scartare per sempre le notizie troppo vecchie.
            while (startIdx < relevant.Count && relevant[startIdx].PublishedUtc < windowStart)
            {
                startIdx++;
            }

            decimal sum = 0m;
            var count = 0;
            for (var k = startIdx; k < relevant.Count && relevant[k].PublishedUtc <= candleTime; k++)
            {
                // Anti-look-ahead: solo notizie pubblicate fino ALLA candela corrente, mai dopo.
                sum += relevant[k].SentimentScore;
                count++;
            }

            result[i] = count > 0 ? sum / count : null;
        }
        return result;
    }
}
