using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Sentiment;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="SentimentScorerComparisonService"/> (harness A/B/C): stesse notizie, stesse
/// candele, stesso giudice (<see cref="FactorEvaluator"/>) per ogni scorer; LLM interamente
/// ripiegato ⇒ riga dichiarata non disponibile (mai un confronto che non confronta); ONNX senza
/// modello ⇒ riga dichiarata; disaccordi calcolati solo dove esistono due punteggi veri.
/// </summary>
[Collection("Postgres")]
public class SentimentScorerComparisonServiceTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public SentimentScorerComparisonServiceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class FakeLlmClient(Func<string, string> respond) : ILlmClient
    {
        public bool IsConfigured => true;
        public string Model => "fake-model";
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct) =>
            Task.FromResult(respond(userPrompt));
    }

    private static readonly DateTime T0 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task<SentimentScorerComparisonService> BuildAsync(Func<string, string> llmRespond)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();
        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            if (!await db.OhlcvData.AnyAsync())
            {
                // 200 candele orarie con lieve trend + 40 notizie BTC alternate pos/neg.
                for (var i = 0; i < 200; i++)
                {
                    var px = 100m + i * 0.1m + (i % 7) * 0.3m;
                    db.OhlcvData.Add(new OhlcvData
                    {
                        Symbol = "BTC/USDT",
                        Timeframe = "1h",
                        TimestampUtc = T0.AddHours(i),
                        Open = px, High = px + 0.5m, Low = px - 0.5m, Close = px + 0.2m,
                        Volume = 1000m,
                    });
                }
                for (var i = 0; i < 40; i++)
                {
                    var positive = i % 2 == 0;
                    db.AltDataPoints.Add(new AltDataPoint
                    {
                        TimestampUtc = T0.AddHours(3 + i * 4),
                        Source = "TestSource",
                        Title = positive
                            ? $"Rally and record inflows continue (n. {i})"
                            : $"Crash fears and lawsuit risks mount (n. {i})",
                        Summary = null,
                        Category = "Other",
                        SymbolsJson = """["BTC"]""",
                        SentimentScore = 0m,
                        DedupeKey = $"TestSource:cmp-{i}",
                    });
                }
                await db.SaveChangesAsync();
            }
        }

        var keyword = new KeywordSentimentScorer();
        var llmClient = new FakeLlmClient(llmRespond);
        var guard = new LlmCallGuard(llmClient, new LlmOptions().AsMonitor(), NullLogger<LlmCallGuard>.Instance);
        var llm = new LlmSentimentScorer(llmClient, guard, keyword, NullLogger<LlmSentimentScorer>.Instance);
        var onnx = new OnnxSentimentScorer(
            new SentimentOptions { OnnxModelPath = Path.Combine(Path.GetTempPath(), "cmp-inesistente", Guid.NewGuid().ToString("N") + ".onnx") }.AsMonitor(),
            keyword, NullLogger<OnnxSentimentScorer>.Instance);

        return new SentimentScorerComparisonService(dbFactory, keyword, llm, onnx,
            new FactorEvaluator(), NullLogger<SentimentScorerComparisonService>.Instance);
    }

    private static ScorerComparisonRequest Request(bool includeLlm, bool includeOnnx, int maxItems = 100) => new(
        "BTC/USDT", "1h", T0, T0.AddHours(200), LookbackHours: 24, ForwardHorizon: 1,
        MaxItems: maxItems, IncludeLlm: includeLlm, IncludeOnnx: includeOnnx);

    private static int CountNumberedLines(string prompt) =>
        System.Text.RegularExpressions.Regex.Matches(prompt, @"^\d+\. ", System.Text.RegularExpressions.RegexOptions.Multiline).Count;

    [Fact]
    public async Task KeywordOnly_ProducesEvaluationOnSameJudge()
    {
        var service = await BuildAsync(_ => "irrilevante");

        var result = await service.CompareAsync(Request(includeLlm: false, includeOnnx: false), CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(SentimentScorerProviders.Keyword, entry.Scorer);
        Assert.True(entry.Available);
        Assert.NotNull(entry.Evaluation);
        Assert.True(entry.Evaluation!.Observations > 30, $"osservazioni: {entry.Evaluation.Observations}");
        Assert.Equal(40, result.NewsScored);
        Assert.Equal(200, result.CandleCount);
    }

    [Fact]
    public async Task LlmAvailable_SecondEntry_AndDisagreementsWhereScoresDiffer()
    {
        // L'LLM finto risponde con punteggi COSTANTI 0.9: diverge dal lessico sulle notizie negative.
        var service = await BuildAsync(prompt =>
            "[" + string.Join(",", Enumerable.Repeat("0.9", CountNumberedLines(prompt))) + "]");

        var result = await service.CompareAsync(Request(includeLlm: true, includeOnnx: false), CancellationToken.None);

        Assert.Equal(2, result.Entries.Count);
        var llmEntry = result.Entries.Single(e => e.Scorer == SentimentScorerProviders.Llm);
        Assert.True(llmEntry.Available);
        Assert.Equal(result.NewsScored, result.LlmScoredByLlm);
        Assert.NotEmpty(result.TopDisagreements);
        // Il disaccordo massimo è sulle notizie negative: lessico −1 contro LLM +0.9.
        Assert.Contains(result.TopDisagreements, d => d.KeywordScore < 0m && d.LlmScore == 0.9m);
    }

    [Fact]
    public async Task LlmEntirelyFallenBack_DeclaredUnavailable_NotADisguisedDuplicate()
    {
        var service = await BuildAsync(_ => "risposta senza numeri in formato array");

        var result = await service.CompareAsync(Request(includeLlm: true, includeOnnx: false), CancellationToken.None);

        var llmEntry = result.Entries.Single(e => e.Scorer == SentimentScorerProviders.Llm);
        Assert.False(llmEntry.Available);
        Assert.Null(llmEntry.Evaluation);
        Assert.Equal(0, result.LlmScoredByLlm);
    }

    [Fact]
    public async Task OnnxWithoutModel_DeclaredUnavailable()
    {
        var service = await BuildAsync(_ => "irrilevante");

        var result = await service.CompareAsync(Request(includeLlm: false, includeOnnx: true), CancellationToken.None);

        var onnxEntry = result.Entries.Single(e => e.Scorer == SentimentScorerProviders.Onnx);
        Assert.False(onnxEntry.Available);
        Assert.Contains("Addestra", onnxEntry.Note);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
