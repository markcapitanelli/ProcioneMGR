using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Security;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica il punto di aggancio che rende i modelli ML utilizzabili da Optimization/Discovery/
/// Ensemble: <see cref="BacktestEngine"/> con <c>StrategyName="Ml"</c> deve risolvere la
/// strategia caricando un <see cref="SavedMlModel"/> dal DB (via "SavedModelId" nei parametri),
/// esattamente come già fa per nome con le strategie a regole — nessun cambiamento richiesto a
/// Optimization/Ensemble, che passano solo <c>BacktestConfiguration</c>.
/// </summary>
[Collection("Postgres")]
public class MlSavedModelIntegrationTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public MlSavedModelIntegrationTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private static List<OhlcvData> MakeCandles(IReadOnlyList<decimal> closes, string symbol, string timeframe)
    {
        var list = new List<OhlcvData>(closes.Count);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < closes.Count; i++)
        {
            var c = closes[i];
            var prev = i > 0 ? closes[i - 1] : c;
            list.Add(new OhlcvData
            {
                Symbol = symbol,
                Timeframe = timeframe,
                TimestampUtc = t0.AddHours(i),
                Open = prev,
                High = Math.Max(prev, c) * 1.01m,
                Low = Math.Min(prev, c) * 0.99m,
                Close = c,
                Volume = 100m,
            });
        }
        return list;
    }

    private static List<decimal> SyntheticMomentumCloses(int n, int seed)
    {
        var rnd = new Random(seed);
        var closes = new List<decimal> { 100m };
        for (var i = 1; i < n; i++)
        {
            var prevRet = i >= 2 ? (double)(closes[i - 1] / closes[i - 2] - 1m) : 0.0;
            var drift = 0.5 * prevRet;
            var noise = (rnd.NextDouble() - 0.5) * 0.01;
            var next = (double)closes[i - 1] * (1.0 + drift + noise);
            closes.Add((decimal)Math.Max(1.0, next));
        }
        return closes;
    }

    private async Task<ServiceProvider> BuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();
        services.AddSingleton<IAlphaFactorFactory, AlphaFactorFactory>();
        services.AddScoped<IBacktestEngine>(sp => new BacktestEngine(
            sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            null!, // strategyFactory: non usato dal percorso "Ml"
            sp.GetRequiredService<ITechnicalIndicatorsService>(),
            sp.GetRequiredService<IAlphaFactorFactory>(),
            sp.GetRequiredService<ILogger<BacktestEngine>>()));
        var provider = services.BuildServiceProvider();

        // EnsureCreated (non Migrate): il DB di test è effimero e non serve tracciare lo storico
        // migrazioni; Migrate confronterebbe il modello con lo snapshot generato dall'host reale
        // (che configura Identity SchemaVersion=Version3 in Program.cs) e questo DI minimale non
        // lo replica, causando un falso "pending changes" (vedi nota GOTCHA sulle migrazioni).
        await using var db = await provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _provider = provider;
        return provider;
    }

    /// <summary>Addestra un modello lineare e lo inserisce come SavedMlModel, restituendone l'Id.</summary>
    private static async Task<int> SeedSavedModelAsync(IDbContextFactory<ApplicationDbContext> dbFactory, string symbol, string timeframe)
    {
        var closes = SyntheticMomentumCloses(600, seed: 42);
        var candles = MakeCandles(closes, symbol, timeframe);

        var factors = new List<FactorSpec> { new("Mom1", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 1m, ["Skip"] = 0m }) };
        var dataset = new DatasetBuilder().Build(candles, factors, forwardHorizon: 1);

        var mlContext = new MLContext(seed: 1);
        var predictor = new LinearReturnPredictor();
        predictor.Fit(mlContext, dataset.ToDataView(mlContext));

        var tempPath = Path.Combine(Path.GetTempPath(), $"seed_model_{Guid.NewGuid():N}.zip");
        byte[] bytes;
        try
        {
            predictor.Save(mlContext, tempPath);
            bytes = await File.ReadAllBytesAsync(tempPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        var factorsJson = System.Text.Json.JsonSerializer.Serialize(
            factors.Select(f => new SavedFactorSpecDto(f.FeatureName, f.Factor.Name, new Dictionary<string, decimal>(f.Parameters))).ToList());

        await using var db = await dbFactory.CreateDbContextAsync();
        const string userId = "test-user";
        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = "test@example.com", NormalizedUserName = "TEST@EXAMPLE.COM" });
            await db.SaveChangesAsync();
        }

        var saved = new SavedMlModel
        {
            UserId = userId,
            Name = "Test model",
            ModelType = "Linear",
            Symbol = symbol,
            Timeframe = timeframe,
            TrainingDataFrom = candles[0].TimestampUtc,
            TrainingDataTo = candles[^1].TimestampUtc,
            ForwardHorizon = 1,
            FactorsJson = factorsJson,
            ModelBytes = bytes,
            TrainRowCount = dataset.RowCount,
            TrainCorrelation = 0.5,
        };
        db.SavedMlModels.Add(saved);
        await db.SaveChangesAsync();
        return saved.Id;
    }

    [Fact]
    public async Task RunBacktestAsync_WithMlStrategyName_ResolvesSavedModel_AndCompletes()
    {
        var provider = await BuildProviderAsync();
        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var modelId = await SeedSavedModelAsync(dbFactory, "TEST/USDT", "1h");

        var closes = SyntheticMomentumCloses(600, seed: 99); // serie diversa da quella di training
        var candles = MakeCandles(closes, "TEST/USDT", "1h");

        var engine = provider.GetRequiredService<IBacktestEngine>();
        var config = new BacktestConfiguration
        {
            Symbol = "TEST/USDT",
            Timeframe = "1h",
            InitialCapital = 10_000m,
            PositionSizePercent = 20m,
            FeePercent = 0.05m,
            StrategyName = "Ml",
            StrategyParameters = new Dictionary<string, decimal>
            {
                ["SavedModelId"] = modelId,
                ["LongThreshold"] = 0.0005m,
                ["ShortThreshold"] = 0.0005m,
            },
        };

        var result = await engine.RunBacktestAsync(config, candles, CancellationToken.None);

        Assert.Equal(candles.Count, result.CandlesEvaluated);
        Assert.Equal(candles.Count, result.EquityCurve.Count);
        Assert.True(result.FinalCapital > 0m);
    }

    [Fact]
    public async Task RunBacktestAsync_MlStrategy_MissingSavedModelId_Throws()
    {
        var provider = await BuildProviderAsync();
        var candles = MakeCandles(SyntheticMomentumCloses(100, 1), "TEST/USDT", "1h");
        var engine = provider.GetRequiredService<IBacktestEngine>();

        var config = new BacktestConfiguration
        {
            Symbol = "TEST/USDT",
            Timeframe = "1h",
            StrategyName = "Ml",
            StrategyParameters = new Dictionary<string, decimal>(), // manca SavedModelId
        };

        await Assert.ThrowsAsync<ArgumentException>(() => engine.RunBacktestAsync(config, candles, CancellationToken.None));
    }

    [Fact]
    public async Task RunBacktestAsync_MlStrategy_NonExistentSavedModelId_Throws()
    {
        var provider = await BuildProviderAsync();
        var candles = MakeCandles(SyntheticMomentumCloses(100, 1), "TEST/USDT", "1h");
        var engine = provider.GetRequiredService<IBacktestEngine>();

        var config = new BacktestConfiguration
        {
            Symbol = "TEST/USDT",
            Timeframe = "1h",
            StrategyName = "Ml",
            StrategyParameters = new Dictionary<string, decimal> { ["SavedModelId"] = 999999m },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunBacktestAsync(config, candles, CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }
}
