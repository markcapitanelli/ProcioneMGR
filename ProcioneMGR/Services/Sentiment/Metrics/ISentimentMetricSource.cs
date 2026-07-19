namespace ProcioneMGR.Services.Sentiment.Metrics;

/// <summary>Un punto di metrica prodotto da una fonte, pronto per <c>SentimentMetricPoints</c>.</summary>
/// <param name="TimestampUtc">Timestamp del punto (dalla fonte), UTC.</param>
/// <param name="Metric">Nome della metrica (costanti in <c>SentimentMetrics</c>).</param>
/// <param name="Symbol">Ticker base ("BTC"); stringa vuota = mercato intero.</param>
/// <param name="Value">Valore (convenzioni in <c>SentimentMetricPoint</c>: funding in percento ×100).</param>
public sealed record SentimentMetricSample(DateTime TimestampUtc, string Metric, string Symbol, decimal Value);

/// <summary>
/// Una fonte di serie numeriche di market mood (Sentiment 2.0) — l'equivalente "denso" di
/// <c>IAltDataSource</c>, che resta per gli eventi testuali (notizie). Stesso principio additivo:
/// una nuova fonte è una nuova implementazione registrata, senza toccare schema né orchestrazione.
/// </summary>
public interface ISentimentMetricSource
{
    /// <summary>Nome della fonte (colonna Source, costanti in <c>SentimentMetricSources</c>).</summary>
    string Name { get; }

    /// <summary>Gli ultimi punti disponibili (finestra corta: la dedupe mangia le sovrapposizioni).</summary>
    Task<IReadOnlyList<SentimentMetricSample>> FetchLatestAsync(CancellationToken ct);
}

/// <summary>
/// Fonte che sa fornire anche l'INTERO storico: usata dal sync service una sola volta, quando la
/// tabella non ha ancora righe per quella fonte (es. Fear &amp; Greed: ~2500 punti giornalieri).
/// </summary>
public interface IBackfillableMetricSource : ISentimentMetricSource
{
    Task<IReadOnlyList<SentimentMetricSample>> FetchFullHistoryAsync(CancellationToken ct);
}
