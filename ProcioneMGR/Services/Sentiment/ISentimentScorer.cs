namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// Assegna un punteggio di sentiment a un testo. Interfaccia pensata per essere intercambiabile:
/// oggi <see cref="KeywordSentimentScorer"/> (lessicale, testabile senza alcuna chiave API);
/// domani un'implementazione basata su LLM (stesso contratto, nessun altro codice da toccare —
/// stesso principio già seguito per <c>IReturnPredictor</c>/<c>IPortfolioOptimizer</c>).
/// </summary>
public interface ISentimentScorer
{
    /// <summary>Punteggio in [-1, +1]: negativo = notizia ribassista, positivo = rialzista, 0 = neutra/non determinabile.</summary>
    decimal Score(string title, string? summary);
}
