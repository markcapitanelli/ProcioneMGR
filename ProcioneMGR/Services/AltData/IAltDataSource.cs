namespace ProcioneMGR.Services.AltData;

/// <summary>
/// Una notizia/evento grezzo, prima di classificazione/sentiment (li applica
/// <c>AltDataSyncService</c>). Le fonti testuali (RSS) lasciano <c>CategoryOverride</c>/
/// <c>SentimentScoreOverride</c>/<c>SymbolsOverride</c> a <c>null</c> e si affidano alla
/// classificazione automatica (<see cref="NewsImpactClassifier"/>/<c>ISentimentScorer</c>).
/// Le fonti strutturali (es. <c>ForexFactoryIngestor</c> per il calendario economico,
/// <c>RetailSentimentIngestor</c> per i dati numerici di posizionamento retail) valorizzano gli
/// override perché il dato non è testo libero da classificare: la categoria è nota per
/// costruzione e il punteggio di sentiment (se applicabile) è calcolato direttamente dal dato
/// numerico, non da un lessico.
/// </summary>
public sealed record RawNewsItem(
    DateTime PublishedUtc,
    string Title,
    string? Summary,
    string? Url,
    NewsCategory? CategoryOverride = null,
    decimal? SentimentScoreOverride = null,
    IReadOnlyList<string>? SymbolsOverride = null);

/// <summary>
/// Fonte di dati alternativi (cap. 3): stesso spirito di <c>IExchangeClient</c> — un'astrazione
/// per fonte, così aggiungerne una nuova (social, on-chain) è "nuova classe + un case", non un
/// cambiamento strutturale.
/// </summary>
public interface IAltDataSource
{
    /// <summary>Nome tecnico della fonte, es. "CoinDesk".</summary>
    string Name { get; }

    Task<IReadOnlyList<RawNewsItem>> FetchLatestAsync(CancellationToken ct);
}
