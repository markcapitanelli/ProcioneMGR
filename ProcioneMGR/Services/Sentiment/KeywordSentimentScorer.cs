using System.Text.RegularExpressions;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// Sentiment lessicale: conta parole positive/negative (word-boundary) nel testo e restituisce
/// (positive-negative)/(positive+negative). Semplicistico ma reale e testabile SENZA alcuna
/// chiave API — è il fallback sempre disponibile del layer sentiment: gli scorer che dipendono da
/// un canale esterno (<see cref="LlmSentimentScorer"/>) ripiegano qui quando il canale manca.
/// </summary>
public sealed class KeywordSentimentScorer : ISentimentScorer
{
    private static readonly string[] PositiveWords =
    [
        "surge", "surges", "rally", "rallies", "adoption", "approval", "approved", "approves",
        "bullish", "gain", "gains", "soar", "soars", "record", "partnership", "upgrade",
        "breakthrough", "inflow", "inflows", "growth", "milestone", "outperform",
    ];

    private static readonly string[] NegativeWords =
    [
        "crash", "crashes", "hack", "hacked", "ban", "banned", "lawsuit", "plunge", "plunges",
        "bearish", "loss", "losses", "decline", "declines", "exploit", "exploited", "fraud",
        "collapse", "collapses", "sued", "sues", "fear", "selloff", "outflow", "outflows",
        "delisted", "delisting",
    ];

    /// <summary>
    /// Percorso sincrono, senza I/O per costruzione: resta pubblico perché è il fallback che gli
    /// altri scorer invocano inline (e il riferimento indipendente nei loro test).
    /// </summary>
    public decimal Score(string title, string? summary)
    {
        var text = $"{title} {summary}".ToLowerInvariant();

        var positive = CountMatches(text, PositiveWords);
        var negative = CountMatches(text, NegativeWords);
        var total = positive + negative;

        return total == 0 ? 0m : (decimal)(positive - negative) / total;
    }

    public Task<decimal> ScoreAsync(string title, string? summary, CancellationToken ct = default) =>
        Task.FromResult(Score(title, summary));

    private static int CountMatches(string text, string[] words) =>
        words.Sum(w => Regex.Matches(text, $@"\b{Regex.Escape(w)}\b").Count);
}
