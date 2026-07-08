using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del Champion del registry come strategia di una lane (follow-up "Champion → TradingEngine").
/// Copre: (a) CONFINE DI SICUREZZA — una lane Live rifiuta il Champion con throw esplicito, mai un
/// fallback silenzioso; (b) cache per-lane — il modello non si ricarica a ogni candela ma solo al
/// cambio di Champion; (c) parità batch/stream — lo stesso SavedMlModel caricato da MlModelLoader
/// dà lo stesso segnale su serie piena (backtest) e su buffer (streaming); (e) end-to-end — una
/// lane Paper col Champion apre posizioni coerentemente coi segnali del predittore.
/// La non-regressione delle lane a sole regole (d) è coperta dalla suite esistente (il ramo
/// Champion scatta SOLO per StrategyName=="MlChampion").
/// </summary>
public sealed class TradingEngineChampionTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"champion_{Guid.NewGuid():N}.db");
    private ServiceProvider? _provider;

    private const string Symbol = "ML/USDT";
    private const string Tf = "1h";

    // ---- Fakes --------------------------------------------------------------------------------

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class FakeEnsembleManager(EnsembleConfiguration config) : IEnsembleManager
    {
        public int LaneId => 0;
        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default) => Task.FromResult(config);
        public Task UpdateConfigurationAsync(EnsembleConfiguration c, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProcioneMGR.Services.Monitoring.DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class ThrowingStrategyFactory : IStrategyFactory
    {
        public IReadOnlyList<IStrategy> Prototypes => [];
        public IStrategy Create(string strategyName) => throw new InvalidOperationException($"StrategyFactory non deve essere chiamata per il Champion (nome: {strategyName}).");
    }

    private sealed class ThrowingExchangeFactory : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => throw new NotSupportedException();
        public IExchangeClient Create(string exchangeName) => throw new NotSupportedException();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotSupportedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotSupportedException();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;
        private sealed class NullDisposable : IDisposable { public static readonly NullDisposable Instance = new(); public void Dispose() { } }
    }

    // ---- Data helpers -------------------------------------------------------------------------

    /// <summary>Serie in trend rialzista deterministico: momentum &gt; 0 predice rendimenti forward &gt; 0.</summary>
    private static List<OhlcvData> TrendCandles(int n, DateTime t0, decimal start = 100m, decimal drift = 0.6m)
    {
        var list = new List<OhlcvData>(n);
        var price = start;
        for (var i = 0; i < n; i++)
        {
            var prev = price;
            price += drift + (i % 3 == 0 ? 0.1m : -0.05m);   // salita con micro-oscillazioni (varianza > 0)
            list.Add(new OhlcvData
            {
                Symbol = Symbol, Timeframe = Tf, TimestampUtc = t0.AddHours(i),
                Open = prev, High = Math.Max(prev, price) + 0.2m, Low = Math.Min(prev, price) - 0.2m,
                Close = price, Volume = 100m,
            });
        }
        return list;
    }

    private static List<FactorSpec> Factors() =>
    [
        new("Momentum10", new MomentumFactor(), new Dictionary<string, decimal> { ["Lookback"] = 10m, ["Skip"] = 0m }),
    ];

    private static List<SavedFactorSpecDto> FactorsDto() =>
    [
        new("Momentum10", "Momentum", new Dictionary<string, decimal> { ["Lookback"] = 10m, ["Skip"] = 0m }),
    ];

    /// <summary>Addestra un Linear su una serie in trend e lo persiste come Champion, restituendone l'Id.</summary>
    private static async Task<int> SeedChampionAsync(IDbContextFactory<ApplicationDbContext> factory, string userId, DateTime promotedAt, int version)
    {
        var candles = TrendCandles(300, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var dataset = new DatasetBuilder().Build(candles, Factors(), forwardHorizon: 1);
        var ml = new MLContext(seed: 1);
        using var predictor = new LinearReturnPredictor();
        predictor.Fit(ml, dataset.ToDataView(ml));

        var tmp = Path.Combine(Path.GetTempPath(), $"champ_{Guid.NewGuid():N}.zip");
        byte[] bytes;
        try { predictor.Save(ml, tmp); bytes = await File.ReadAllBytesAsync(tmp); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }

        await using var db = await factory.CreateDbContextAsync();
        var saved = new SavedMlModel
        {
            UserId = userId, Name = $"champ v{version}", ModelType = "Linear", Symbol = Symbol, Timeframe = Tf,
            ForwardHorizon = 1, FactorsJson = JsonSerializer.Serialize(FactorsDto()), ModelBytes = bytes,
            Stage = ModelStage.Champion, Version = version, PromotedAtUtc = promotedAt,
        };
        db.SavedMlModels.Add(saved);
        await db.SaveChangesAsync();
        return saved.Id;
    }

    private async Task<(TradingEngine Engine, IDbContextFactory<ApplicationDbContext> Factory)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _provider = services.BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        string userId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var user = new ApplicationUser { UserName = "t", Email = "t@t" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }
        await SeedChampionAsync(factory, userId, promotedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), version: 1);

        var config = new EnsembleConfiguration
        {
            ExchangeName = "Binance", Symbol = Symbol, Timeframe = Tf, TotalCapital = 100_000m,
            Strategies =
            [
                new EnsembleStrategy
                {
                    StrategyId = "champ", StrategyName = TradingEngine.ChampionStrategyName, DisplayName = "Champion",
                    IsActive = true,
                    Parameters = new Dictionary<string, decimal> { ["LongThreshold"] = 0.00001m, ["ShortThreshold"] = 0.00001m },
                },
            ],
        };

        var engine = new TradingEngine(
            0, factory, new ThrowingStrategyFactory(), new TechnicalIndicatorsService(),
            new ThrowingExchangeFactory(), new FakeEnsembleManager(config),
            new StaticOptionsMonitor<SafetyConfiguration>(new SafetyConfiguration { MinOrderIntervalSeconds = 0 }),
            new StaticOptionsMonitor<LiveExecutionOptions>(new LiveExecutionOptions()),
            new ExecutionAlgorithmFactory(), NullLogger<TradingEngine>.Instance,
            metrics: null,
            modelRegistry: new ModelRegistry(factory, new ModelRegistryOptions(), NullLogger<ModelRegistry>.Instance),
            alphaFactorFactory: new AlphaFactorFactory(),
            factorCache: new FactorCache());

        return (engine, factory);
    }

    private static object? GetChampionCache(TradingEngine engine)
        => typeof(TradingEngine).GetField("_championCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine);

    private static object GetCacheProp(object cache, string name)
        => cache.GetType().GetProperty(name)!.GetValue(cache)!;

    private static void ForceMode(TradingEngine engine, TradingMode mode)
    {
        var state = typeof(TradingEngine).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        state.GetType().GetProperty("Mode")!.SetValue(state, mode);
    }

    private static OhlcvData Candle(int i, decimal close) => new()
    {
        Symbol = Symbol, Timeframe = Tf, TimestampUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = close, High = close + 0.2m, Low = close - 0.2m, Close = close, Volume = 100m,
    };

    private static async Task FeedRisingAsync(TradingEngine engine, int count, decimal start = 200m, decimal step = 0.6m)
    {
        for (var i = 0; i < count; i++) await engine.ProcessCandleAsync(Candle(i, start + step * i));
    }

    // ---- (a) confine di sicurezza -------------------------------------------------------------

    [Fact]
    public async Task Champion_OnLiveLane_ThrowsExplicitly_NeverSilentFallback()
    {
        var (engine, _) = await BuildAsync();
        await engine.StartAsync(TradingMode.Paper);
        ForceMode(engine, TradingMode.Live);   // simula una lane Live che tenta di risolvere il Champion

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => FeedRisingAsync(engine, 15));
        Assert.Contains("CONFINE DI SICUREZZA", ex.Message);
        Assert.Empty(await engine.GetOpenPositionsAsync());   // nessuna posizione aperta in Live
    }

    // ---- (b) cache per-lane -------------------------------------------------------------------

    [Fact]
    public async Task Champion_Cache_ReloadsOnlyWhenModelChanges()
    {
        var (engine, factory) = await BuildAsync();
        await engine.StartAsync(TradingMode.Paper);

        await FeedRisingAsync(engine, 15);
        var cache1 = GetChampionCache(engine);
        Assert.NotNull(cache1);
        var strat1 = GetCacheProp(cache1!, "Strategy");
        var modelId1 = (int)GetCacheProp(cache1!, "ModelId");

        // Altre candele: stesso Champion → nessun ricaricamento (stessa istanza di strategia).
        await FeedRisingAsync(engine, 10, start: 210m);
        var cache2 = GetChampionCache(engine);
        Assert.Same(strat1, GetCacheProp(cache2!, "Strategy"));

        // Promuovo un NUOVO Champion (Id diverso, PromotedAtUtc più recente) per lo stesso simbolo/tf.
        string userId;
        await using (var db = await factory.CreateDbContextAsync())
            userId = (await db.Users.FirstAsync()).Id;
        var newId = await SeedChampionAsync(factory, userId, promotedAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), version: 2);

        await engine.ProcessCandleAsync(Candle(100, 260m));
        var cache3 = GetChampionCache(engine);
        Assert.NotSame(strat1, GetCacheProp(cache3!, "Strategy"));   // ricaricato
        Assert.NotEqual(modelId1, (int)GetCacheProp(cache3!, "ModelId"));
        Assert.Equal(newId, (int)GetCacheProp(cache3!, "ModelId"));
    }

    // ---- (c) parità batch/stream --------------------------------------------------------------

    [Fact]
    public async Task Champion_BatchAndStreamLoader_ProduceSameSignal_OnSameSeries()
    {
        var (_, factory) = await BuildAsync();
        SavedMlModel saved;
        await using (var db = await factory.CreateDbContextAsync())
            saved = await db.SavedMlModels.FirstAsync(m => m.Stage == ModelStage.Champion);

        // Stesso modello caricato due volte dallo STESSO MlModelLoader (batch e stream usano questo).
        var (batch, _) = await MlModelLoader.LoadAsync(saved, new AlphaFactorFactory(), null, CancellationToken.None);
        var (stream, _) = await MlModelLoader.LoadAsync(saved, new AlphaFactorFactory(), null, CancellationToken.None);

        var series = TrendCandles(120, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), start: 150m);
        var closes = series.Select(c => c.Close).ToList();
        var parms = new Dictionary<string, decimal> { ["LongThreshold"] = 0.00001m, ["ShortThreshold"] = 0.00001m };

        // Batch: inizializza sull'INTERA serie. Stream: sullo stesso buffer (qui coincidono → parità esatta).
        await batch.InitializeAsync(closes, series, parms, new TechnicalIndicatorsService(), CancellationToken.None);
        await stream.InitializeAsync(closes, series, parms, new TechnicalIndicatorsService(), CancellationToken.None);

        var last = series.Count - 1;
        var sigBatch = batch.EvaluateSignal(last, series[last].Close, series[last].TimestampUtc);
        var sigStream = stream.EvaluateSignal(last, series[last].Close, series[last].TimestampUtc);
        Assert.Equal(sigBatch, sigStream);
        Assert.Equal(Signal.Long, sigBatch);   // sull'uptrend il Champion è coerente col trend
    }

    // ---- (e) end-to-end -----------------------------------------------------------------------

    [Fact]
    public async Task Champion_PaperLane_OpensPosition_FromPredictorSignal()
    {
        var (engine, _) = await BuildAsync();
        await engine.StartAsync(TradingMode.Paper);

        await FeedRisingAsync(engine, 30);   // buffer sufficiente per Momentum10 + segnale Long dal Champion

        var positions = await engine.GetOpenPositionsAsync();
        var pos = Assert.Single(positions);
        Assert.Equal(OrderSide.Buy, pos.Side);   // uptrend → Champion apre Long
        Assert.Equal("champ", pos.StrategyId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best-effort */ }
    }
}
