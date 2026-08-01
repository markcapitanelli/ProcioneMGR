namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// Assegna un punteggio di sentiment a un testo. Interfaccia pensata per essere intercambiabile:
/// <see cref="KeywordSentimentScorer"/> (lessicale, testabile senza alcuna chiave API),
/// <see cref="LlmSentimentScorer"/> (LLM via provider attivo del layer AI) e
/// <see cref="OnnxSentimentScorer"/> (inferenza locale) — stesso contratto, i consumatori restano
/// ignari (stesso principio di <c>IReturnPredictor</c>/<c>IPortfolioOptimizer</c>).
///
/// <para>Il contratto è asincrono perché un'implementazione può fare I/O di rete (LLM). Chi
/// implementa NON deve mai lasciar propagare un fallimento del canale: un errore va assorbito
/// ripiegando su un punteggio calcolabile localmente (vedi <see cref="LlmSentimentScorer"/>) —
/// il chiamante (<c>AltDataSyncService</c>) tratta comunque un'eccezione come "salta l'elemento e
/// ritenta al prossimo giro", mai come fallimento dell'intera sync.</para>
/// </summary>
public interface ISentimentScorer
{
    /// <summary>Punteggio in [-1, +1]: negativo = notizia ribassista, positivo = rialzista, 0 = neutra/non determinabile.</summary>
    Task<decimal> ScoreAsync(string title, string? summary, CancellationToken ct = default);
}
