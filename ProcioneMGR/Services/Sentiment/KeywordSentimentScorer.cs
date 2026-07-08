using System.Text.RegularExpressions;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// Sentiment lessicale: conta parole positive/negative (word-boundary) nel testo e restituisce
/// (positive-negative)/(positive+negative). Semplicistico ma reale e testabile SENZA alcuna
/// chiave API — sblocca oggi l'intera pipeline (ingestion → classificazione → fattore alpha →
/// valutazione IC) mentre si decide il provider LLM. Sostituibile 1:1 con un'implementazione
/// LLM-based dietro <see cref="ISentimentScorer"/>.
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

    public decimal Score(string title, string? summary)
    {
        var text = $"{title} {summary}".ToLowerInvariant();

        var positive = CountMatches(text, PositiveWords);
        var negative = CountMatches(text, NegativeWords);
        var total = positive + negative;

        return total == 0 ? 0m : (decimal)(positive - negative) / total;
    }

    private static int CountMatches(string text, string[] words) =>
        words.Sum(w => Regex.Matches(text, $@"\b{Regex.Escape(w)}\b").Count);
}
