using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Sentiment;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test end-to-end del pilota ONNX (PRD-ONNX-SENTIMENT-PILOT, Livello 1): addestramento ML.NET su
/// etichette deboli → export ConvertToOnnx → caricamento in ONNX Runtime → PARITÀ fra i due
/// runtime attraverso lo scorer reale. È il livello 1 dello standard di verifica: il riferimento
/// indipendente dell'inferenza ONNX è il framework che ha addestrato il modello.
/// </summary>
[Collection("Postgres")]
public class OnnxSentimentPilotTests : IAsyncDisposable
{
    private readonly string _connString;
    private readonly string _modelDir;
    private ServiceProvider? _provider;

    public OnnxSentimentPilotTests(PostgresFixture pg)
    {
        _connString = pg.CreateDatabase();
        _modelDir = Path.Combine(Path.GetTempPath(), "procione-onnx-test-" + Guid.NewGuid().ToString("N"));
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private static readonly string[] PositiveTemplates =
    [
        "Bitcoin surges to new record after ETF approval",
        "Institutional inflows drive massive rally in crypto markets",
        "Partnership milestone marks breakthrough for adoption",
        "Ethereum gains on upgrade news, analysts bullish",
        "Record growth as market soars past resistance",
    ];

    private static readonly string[] NegativeTemplates =
    [
        "Exchange hack triggers crash and massive selloff",
        "Regulator ban sparks fear of broader crackdown",
        "Lawsuit and fraud allegations cause token collapse",
        "Bearish outflows continue as prices plunge",
        "Delisting announcement leads to steep losses",
    ];

    private static readonly string[] NeutralTemplates =
    [
        "Weekly market recap and notable onchain movements",
        "Conference schedule announced for next quarter",
        "Interview with protocol developer about roadmap",
        "New wallet feature enters public beta today",
        "Miner distribution report shows steady hashrate",
    ];

    private async Task<(IDbContextFactory<ApplicationDbContext> DbFactory, OnnxSentimentPilotService Pilot, OnnxSentimentScorer Scorer, KeywordSentimentScorer Keyword)> BuildAsync(int newsCount = 150)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();
        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var t0 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            string[] subjects = ["Bitcoin", "Ethereum", "Solana", "Market", "Token"];
            for (var i = 0; i < newsCount; i++)
            {
                // Corpus SENZA scorciatoie: niente token unici per riga (un indice nel titolo
                // permetterebbe al fit di memorizzare la riga invece di pesare il vocabolario —
                // misurato: probe a ~0 col suffisso "caso {i}") e niente leak dell'etichetta nel
                // sommario. La variazione viene dal soggetto; i titoli possono ripetersi.
                var template = (i % 3) switch
                {
                    0 => PositiveTemplates[(i / 3) % PositiveTemplates.Length],
                    1 => NegativeTemplates[(i / 3) % NegativeTemplates.Length],
                    _ => NeutralTemplates[(i / 3) % NeutralTemplates.Length],
                };
                db.AltDataPoints.Add(new AltDataPoint
                {
                    TimestampUtc = t0.AddHours(i),
                    Source = "TestSource",
                    Title = $"{subjects[(i / 7) % subjects.Length]} update: {template}",
                    Summary = null,
                    Category = "Other",
                    SymbolsJson = """["BTC"]""",
                    SentimentScore = 0m,
                    DedupeKey = $"TestSource:{i}",
                });
            }
            await db.SaveChangesAsync();
        }

        var keyword = new KeywordSentimentScorer();
        var options = new SentimentOptions { OnnxModelPath = Path.Combine(_modelDir, "sentiment-pilot.onnx") }.AsMonitor();
        var scorer = new OnnxSentimentScorer(options, keyword, NullLogger<OnnxSentimentScorer>.Instance);
        var pilot = new OnnxSentimentPilotService(dbFactory, options, keyword, scorer, NullLogger<OnnxSentimentPilotService>.Instance);
        return (dbFactory, pilot, scorer, keyword);
    }

    [Fact]
    public async Task Train_Export_Load_Parity_EndToEnd()
    {
        var (_, pilot, scorer, _) = await BuildAsync();

        var result = await pilot.TrainAsync(CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(result.ModelPath), "il file .onnx deve esistere dopo l'addestramento");
        Assert.True(result.ParityChecked >= 10, "la parità deve essere verificata su un campione reale");
        Assert.True(result.ParityMaxAbsDiff <= OnnxSentimentPilotService.ParityTolerance,
            $"parità oltre tolleranza: {result.ParityMaxAbsDiff}");
        Assert.True(scorer.IsAvailable);
    }

    [Fact]
    public async Task TrainedModel_PreservesSentimentDirection()
    {
        var (_, pilot, scorer, _) = await BuildAsync();
        var trained = await pilot.TrainAsync(CancellationToken.None);
        Assert.True(trained.Success, trained.Message);

        // Frasi MAI viste in training che ricombinano token VISTI (rally/inflows/adoption vs
        // hack/lawsuit/fraud/collapse): la distillazione deve almeno conservare la direzione — è
        // il minimo sindacale del pilota. Niente stemming per costruzione, quindi i token devono
        // coincidere esattamente con quelli del corpus.
        var bullish = await scorer.ScoreAsync("Analysts note adoption and inflows as rally extends", null);
        var bearish = await scorer.ScoreAsync("Another hack and a lawsuit deepen fraud fears of collapse", null);

        Assert.True(bullish > bearish,
            $"direzione persa: bullish {bullish} <= bearish {bearish}");
        Assert.True(bullish > 0m, $"titolo rialzista scorato non-positivo: {bullish}");
        Assert.True(bearish < 0m, $"titolo ribassista scorato non-negativo: {bearish}");
    }

    [Fact]
    public async Task Train_WithTooFewRows_FailsHonestly()
    {
        var (_, pilot, scorer, _) = await BuildAsync(newsCount: 10);

        var result = await pilot.TrainAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("almeno", result.Message);
        Assert.False(scorer.IsAvailable);
    }

    [Fact]
    public async Task Scorer_WithoutModel_FallsBackToKeyword()
    {
        var (_, _, scorer, keyword) = await BuildAsync();
        const string title = "Exchange collapse triggers selloff";

        Assert.False(scorer.IsAvailable);
        Assert.Equal(keyword.Score(title, null), await scorer.ScoreAsync(title, null));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        try { if (Directory.Exists(_modelDir)) Directory.Delete(_modelDir, recursive: true); }
        catch (IOException) { /* file lock residuo: la temp dir verrà ripulita dal sistema */ }
    }
}
