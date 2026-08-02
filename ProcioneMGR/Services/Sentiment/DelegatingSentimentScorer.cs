using Microsoft.Extensions.Options;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>Nomi canonici degli scorer di sentiment. Stringhe (non enum): un provider nuovo non tocca lo schema di config.</summary>
public static class SentimentScorerProviders
{
    public const string Keyword = "Keyword";
    public const string Llm = "Llm";
    public const string Onnx = "Onnx";

    /// <summary>Gli scorer noti alla UI di configurazione, nell'ordine di presentazione.</summary>
    public static readonly IReadOnlyList<string> Known = [Keyword, Llm, Onnx];
}

/// <summary>
/// L'<see cref="ISentimentScorer"/> registrato: instrada OGNI chiamata sullo scorer scelto in
/// <see cref="SentimentOptions.ScorerProvider"/> (hot-reload: cambiare scorer dal pannello ha
/// effetto alla notizia successiva, senza riavvio). Stesso pattern di <c>DelegatingLlmClient</c>:
/// i consumatori (AltDataSyncService) restano ignari di quale scorer stia lavorando.
/// Default <see cref="SentimentScorerProviders.Keyword"/> = comportamento storico, zero costi:
/// passare all'LLM è una scelta esplicita dell'operatore (è il consenso al costo per chiamata).
/// </summary>
public sealed class DelegatingSentimentScorer(
    KeywordSentimentScorer keyword,
    LlmSentimentScorer llm,
    OnnxSentimentScorer onnx,
    IOptionsMonitor<SentimentOptions> options) : ISentimentScorer
{
    private ISentimentScorer Active => options.CurrentValue.ScorerProvider switch
    {
        var p when string.Equals(p, SentimentScorerProviders.Llm, StringComparison.OrdinalIgnoreCase) => llm,
        var p when string.Equals(p, SentimentScorerProviders.Onnx, StringComparison.OrdinalIgnoreCase) => onnx,
        _ => keyword, // default e fallback: il comportamento storico
    };

    public Task<decimal> ScoreAsync(string title, string? summary, CancellationToken ct = default) =>
        Active.ScoreAsync(title, summary, ct);
}
