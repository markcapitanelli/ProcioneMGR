using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Sentiment;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="LlmSentimentScorer"/> (Fase B): parsing difensivo, clamp, fallback sul
/// lessico per OGNI esito non-Ok (il contratto "mai un'eccezione verso il chiamante"), batch con
/// allineamento pretenzioso. Il guard è quello VERO (fake solo il client): il comportamento
/// breaker/timeout testato è quello di produzione.
/// </summary>
public class LlmSentimentScorerTests
{
    private sealed class FakeLlmClient(Func<string, string> respond, bool configured = true) : ILlmClient
    {
        public int Calls { get; private set; }
        public List<string> UserPrompts { get; } = new();
        public bool IsConfigured => configured;
        public string Model => "fake-model";

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            Calls++;
            UserPrompts.Add(userPrompt);
            return Task.FromResult(respond(userPrompt));
        }
    }

    private static readonly KeywordSentimentScorer Keyword = new();

    private static LlmSentimentScorer Make(ILlmClient llm)
    {
        var monitor = new LlmOptions().AsMonitor();
        var guard = new LlmCallGuard(llm, monitor, NullLogger<LlmCallGuard>.Instance);
        return new LlmSentimentScorer(llm, guard, Keyword, NullLogger<LlmSentimentScorer>.Instance);
    }

    // ---- ScoreAsync (singolo) ----

    [Fact]
    public async Task ScoreAsync_ParsesPlainNumber()
    {
        var scorer = Make(new FakeLlmClient(_ => "0.7"));
        Assert.Equal(0.7m, await scorer.ScoreAsync("Bitcoin ETF news", null));
    }

    [Fact]
    public async Task ScoreAsync_ToleratesSurroundingTextAndComma()
    {
        var scorer = Make(new FakeLlmClient(_ => "Il punteggio è: -0,45 (moderatamente ribassista)"));
        Assert.Equal(-0.45m, await scorer.ScoreAsync("Exchange under investigation", null));
    }

    [Fact]
    public async Task ScoreAsync_ClampsOutOfRange()
    {
        var scorer = Make(new FakeLlmClient(_ => "3.5"));
        Assert.Equal(1m, await scorer.ScoreAsync("Massive rally", null));
    }

    [Fact]
    public async Task ScoreAsync_GarbageResponse_FallsBackToKeyword()
    {
        const string title = "Bitcoin crash after exchange hack and fraud";
        var scorer = Make(new FakeLlmClient(_ => "non posso valutare questa notizia"));

        var score = await scorer.ScoreAsync(title, null);

        Assert.Equal(Keyword.Score(title, null), score);
        Assert.True(score < 0m); // il lessico su questo titolo è nettamente negativo
    }

    [Fact]
    public async Task ScoreAsync_UnconfiguredClient_FallsBack_WithoutCalling()
    {
        const string title = "Record inflows drive surge";
        var fake = new FakeLlmClient(_ => "0.9", configured: false);
        var scorer = Make(fake);

        var score = await scorer.ScoreAsync(title, null);

        Assert.Equal(0, fake.Calls);
        Assert.Equal(Keyword.Score(title, null), score);
    }

    [Fact]
    public async Task ScoreAsync_ClientThrows_FallsBack_NeverThrows()
    {
        const string title = "Partnership milestone announced";
        var scorer = Make(new FakeLlmClient(_ => throw new HttpRequestException("rete giù")));

        var score = await scorer.ScoreAsync(title, null);

        Assert.Equal(Keyword.Score(title, null), score);
    }

    // ---- ScoreBatchAsync ----

    private static int CountNumberedLines(string prompt) =>
        Regex.Matches(prompt, @"^\d+\. ", RegexOptions.Multiline).Count;

    [Fact]
    public async Task ScoreBatchAsync_AlignedArray_UsesLlmForAll_InTwoCalls()
    {
        var fake = new FakeLlmClient(prompt =>
            "[" + string.Join(",", Enumerable.Repeat("0.5", CountNumberedLines(prompt))) + "]");
        var scorer = Make(fake);
        var items = Enumerable.Range(0, 25).Select(i => ($"Notizia {i}", (string?)null)).ToList();

        var (scores, fromLlm) = await scorer.ScoreBatchAsync(items);

        Assert.Equal(25, scores.Count);
        Assert.All(scores, s => Assert.Equal(0.5m, s));
        Assert.Equal(25, fromLlm);
        Assert.Equal(2, fake.Calls); // 20 + 5 con BatchSize=20
    }

    [Fact]
    public async Task ScoreBatchAsync_MisalignedArray_FallsBackForThatBatch()
    {
        const string negative = "Exchange collapse and lawsuit fears";
        var scorer = Make(new FakeLlmClient(_ => "[0.1, 0.2]")); // sempre 2 valori: mai allineato
        var items = Enumerable.Repeat((negative, (string?)null), 5).ToList();

        var (scores, fromLlm) = await scorer.ScoreBatchAsync(items);

        Assert.Equal(0, fromLlm);
        Assert.All(scores, s => Assert.Equal(Keyword.Score(negative, null), s));
    }

    [Fact]
    public async Task ScoreBatchAsync_ClientFails_FallsBack_SameLength()
    {
        var scorer = Make(new FakeLlmClient(_ => throw new HttpRequestException("giù")));
        var items = Enumerable.Range(0, 3).Select(i => ($"Titolo {i}", (string?)null)).ToList();

        var (scores, fromLlm) = await scorer.ScoreBatchAsync(items);

        Assert.Equal(3, scores.Count);
        Assert.Equal(0, fromLlm);
    }

    // ---- Parser (unit puri) ----

    [Theory]
    [InlineData("0.25", 0.25)]
    [InlineData("-1", -1)]
    [InlineData("score: 0,8 rialzista", 0.8)]
    [InlineData("il valore è -0.15.", -0.15)]
    [InlineData("42", 1)]      // clamp alto
    [InlineData("-99.9", -1)]  // clamp basso
    public void TryParseScore_ValidInputs(string raw, decimal expected)
    {
        Assert.True(LlmSentimentScorer.TryParseScore(raw, out var score));
        Assert.Equal(expected, score);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nessun numero qui")]
    public void TryParseScore_InvalidInputs(string raw)
    {
        Assert.False(LlmSentimentScorer.TryParseScore(raw, out _));
    }

    [Fact]
    public void TryParseScoreArray_AcceptsExactLength_AndClamps()
    {
        var parsed = LlmSentimentScorer.TryParseScoreArray("ecco: [0.1, -5, 2] fine", 3);
        Assert.NotNull(parsed);
        Assert.Equal([0.1m, -1m, 1m], parsed);
    }

    [Theory]
    [InlineData("[0.1, 0.2]", 3)]           // troppo corto
    [InlineData("[0.1, 0.2, 0.3, 0.4]", 3)] // troppo lungo
    [InlineData("[0.1, \"x\", 0.3]", 3)]    // non numerico
    [InlineData("{\"a\":1}", 1)]            // non array
    [InlineData("nessun json", 2)]
    public void TryParseScoreArray_RejectsMisaligned(string raw, int expected)
    {
        Assert.Null(LlmSentimentScorer.TryParseScoreArray(raw, expected));
    }
}
