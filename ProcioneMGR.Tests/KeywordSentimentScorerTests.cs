using ProcioneMGR.Services.Sentiment;

namespace ProcioneMGR.Tests;

/// <summary>Test di <see cref="KeywordSentimentScorer"/>: segno e range del punteggio, testo neutro, word-boundary.</summary>
public class KeywordSentimentScorerTests
{
    private readonly KeywordSentimentScorer _scorer = new();

    [Fact]
    public void Score_PositiveHeadline_IsPositive()
    {
        var score = _scorer.Score("Bitcoin rallies to new record as institutional adoption grows", null);
        Assert.True(score > 0m, $"score={score}");
    }

    [Fact]
    public void Score_NegativeHeadline_IsNegative()
    {
        var score = _scorer.Score("Exchange hacked, millions lost in exploit as investors fear crash", null);
        Assert.True(score < 0m, $"score={score}");
    }

    [Fact]
    public void Score_NeutralHeadline_IsZero()
    {
        var score = _scorer.Score("Quarterly report scheduled for next Tuesday", null);
        Assert.Equal(0m, score);
    }

    [Fact]
    public void Score_MixedHeadline_IsBetweenBounds()
    {
        var score = _scorer.Score("Rally fades as fears of a ban resurface", null);
        Assert.InRange(score, -1m, 1m);
    }

    [Fact]
    public void Score_IsAlwaysWithinRange()
    {
        var score = _scorer.Score("surge surge surge crash crash", "record gains amid losses and decline");
        Assert.InRange(score, -1m, 1m);
    }

    [Fact]
    public void Score_UsesSummaryToo()
    {
        var titleOnly = _scorer.Score("Market update", null);
        var withSummary = _scorer.Score("Market update", "Prices surge to record highs on ETF approval");
        Assert.Equal(0m, titleOnly);
        Assert.True(withSummary > 0m);
    }
}
