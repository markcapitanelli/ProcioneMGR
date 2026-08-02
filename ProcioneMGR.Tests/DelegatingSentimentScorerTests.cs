using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Sentiment;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="DelegatingSentimentScorer"/> e <see cref="HashingTextVectorizer"/>:
/// instradamento hot-reload sullo scorer configurato (default = lessico, il comportamento
/// storico) e determinismo del vettorizzatore (la premessa di parità del pilota ONNX).
/// </summary>
public class DelegatingSentimentScorerTests
{
    private sealed class FakeLlmClient(string reply) : ILlmClient
    {
        public bool IsConfigured => true;
        public string Model => "fake-model";
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct) =>
            Task.FromResult(reply);
    }

    private static (DelegatingSentimentScorer Scorer, MutableOptionsMonitor<SentimentOptions> Monitor) Make(string llmReply = "0.33")
    {
        var keyword = new KeywordSentimentScorer();
        var llmClient = new FakeLlmClient(llmReply);
        var guard = new LlmCallGuard(llmClient, new LlmOptions().AsMonitor(), NullLogger<LlmCallGuard>.Instance);
        var llm = new LlmSentimentScorer(llmClient, guard, keyword, NullLogger<LlmSentimentScorer>.Instance);

        var monitor = new MutableOptionsMonitor<SentimentOptions>(new SentimentOptions
        {
            // Percorso inesistente di proposito: lo scorer Onnx deve ripiegare sul lessico.
            OnnxModelPath = Path.Combine(Path.GetTempPath(), "onnx-inesistente", Guid.NewGuid().ToString("N") + ".onnx"),
        });
        var onnx = new OnnxSentimentScorer(monitor, keyword, NullLogger<OnnxSentimentScorer>.Instance);
        return (new DelegatingSentimentScorer(keyword, llm, onnx, monitor), monitor);
    }

    private const string BullishTitle = "Record inflows drive surge and rally"; // lessico: +1

    [Fact]
    public async Task Default_RoutesToKeyword()
    {
        var (scorer, _) = Make();
        Assert.Equal(1m, await scorer.ScoreAsync(BullishTitle, null));
    }

    [Fact]
    public async Task LlmProvider_RoutesToLlm_CaseInsensitive()
    {
        var (scorer, monitor) = Make(llmReply: "0.33");
        monitor.CurrentValue = new SentimentOptions { ScorerProvider = "llm" };
        Assert.Equal(0.33m, await scorer.ScoreAsync(BullishTitle, null));
    }

    [Fact]
    public async Task OnnxProvider_WithoutModel_FallsBackToKeyword()
    {
        var (scorer, monitor) = Make();
        monitor.CurrentValue.ScorerProvider = SentimentScorerProviders.Onnx;
        Assert.Equal(1m, await scorer.ScoreAsync(BullishTitle, null));
    }

    [Fact]
    public async Task UnknownProvider_FallsBackToKeyword()
    {
        var (scorer, monitor) = Make(llmReply: "0.33");
        monitor.CurrentValue = new SentimentOptions { ScorerProvider = "Inventato" };
        Assert.Equal(1m, await scorer.ScoreAsync(BullishTitle, null));
    }

    [Fact]
    public async Task HotSwap_TakesEffectOnNextCall()
    {
        var (scorer, monitor) = Make(llmReply: "0.33");
        Assert.Equal(1m, await scorer.ScoreAsync(BullishTitle, null));

        monitor.CurrentValue = new SentimentOptions { ScorerProvider = SentimentScorerProviders.Llm };
        Assert.Equal(0.33m, await scorer.ScoreAsync(BullishTitle, null));

        monitor.CurrentValue = new SentimentOptions { ScorerProvider = SentimentScorerProviders.Keyword };
        Assert.Equal(1m, await scorer.ScoreAsync(BullishTitle, null));
    }

    // ---- HashingTextVectorizer: il determinismo È la garanzia di parità train/inference ----

    [Fact]
    public void Vectorizer_IsDeterministic()
    {
        var a = HashingTextVectorizer.Vectorize("Bitcoin surges after ETF approval", "Record inflows");
        var b = HashingTextVectorizer.Vectorize("Bitcoin surges after ETF approval", "Record inflows");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Vectorizer_EmptyText_IsZeroVector()
    {
        var v = HashingTextVectorizer.Vectorize("", null);
        Assert.All(v, x => Assert.Equal(0f, x));
    }

    [Fact]
    public void Vectorizer_NonEmpty_IsL2Normalized()
    {
        var v = HashingTextVectorizer.Vectorize("Bitcoin crash fears trigger selloff", null);
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, 6);
    }

    [Fact]
    public void Vectorizer_DifferentTexts_DifferentVectors()
    {
        var a = HashingTextVectorizer.Vectorize("bullish rally record", null);
        var b = HashingTextVectorizer.Vectorize("bearish crash lawsuit", null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Tokenize_LowercasesAndDropsSingleChars()
    {
        Assert.Equal(["bitcoin", "etf", "approved"], HashingTextVectorizer.Tokenize("Bitcoin's ETF, approved!"));
    }

    [Fact]
    public void Fnv1a_IsStable()
    {
        // Valore FNV-1a a 32 bit noto per "a" (offset ^ 0x61 * prime): àncora contro regressioni
        // dell'algoritmo — se cambia, ogni modello già addestrato diventa silenziosamente invalido.
        Assert.Equal(0xE40C292Cu, HashingTextVectorizer.Fnv1a("a"));
        Assert.Equal(HashingTextVectorizer.Fnv1a("bitcoin"), HashingTextVectorizer.Fnv1a("bitcoin"));
    }
}
