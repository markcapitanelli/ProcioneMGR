using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [F1 PRD Valore] La cache della simulazione in <see cref="EnsembleManager"/>: prima di questa
/// cache, il poll della pagina Ensemble rieseguiva due simulazioni complete (un backtest per
/// gamba) ogni 15 secondi. Il contratto verificato qui è triplice: (1) a parità di candele e
/// configurazione la simulazione NON si ripete; (2) una candela nuova invalida; (3) una modifica
/// di configurazione invalida. Il motore è un fake che conta le chiamate: il valore dei numeri
/// simulati lo verificano i test dell'EnsembleManager, qui conta SOLO quante volte si paga.
/// </summary>
[Collection("Postgres")]
public sealed class EnsembleSimulationCacheTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public EnsembleSimulationCacheTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class UnusedRegimeDetector : IRegimeDetector
    {
        public Task<RegimeModel> TrainAsync(TrainingConfiguration config, bool activate = true, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ActivateModelAsync(RegimeModel model, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MarketFeatures>> LabelFeaturesAsync(List<MarketFeatures> features, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RegimeModel?> LoadLatestModelAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MarketFeatures>> LabelFeaturesAsync(List<MarketFeatures> features, string symbol, string timeframe, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RegimeModel?> LoadActiveModelAsync(string symbol, string timeframe, CancellationToken ct = default) => Task.FromResult<RegimeModel?>(null);
    }

    private sealed class UnusedFeatureExtractor : IMarketFeatureExtractor
    {
        public Task<List<MarketFeatures>> ExtractFeaturesAsync(string exchangeName, string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(new List<MarketFeatures>());
    }

    /// <summary>Motore fake: risultato deterministico minimale, conta ogni backtest pagato.</summary>
    private sealed class CountingBacktestEngine : IBacktestEngine
    {
        private int _runs;
        public int Runs => _runs;

        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, CancellationToken ct)
            => throw new NotImplementedException("il percorso ensemble passa le candele già caricate");

        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            var eq = candles.Select(c => new EquityPoint { Timestamp = c.TimestampUtc, Capital = 10_000m }).ToList();
            return Task.FromResult(new BacktestResult { EquityCurve = eq, TotalTrades = 0, WinRate = 0m });
        }

        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, IStrategy strategy, CancellationToken ct)
            => RunBacktestAsync(config, candles, ct);
    }

    private async Task<(EnsembleManager Manager, CountingBacktestEngine Engine, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync()
    {
        var engine = new CountingBacktestEngine();
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        services.AddSingleton<IBacktestEngine>(engine);
        var provider = services.BuildServiceProvider();
        _provider = provider;

        var dbf = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbf.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            // Serie minima ma sopra la soglia n>=3 di SimulateAsync: 10 candele orarie recenti.
            var t0 = DateTime.UtcNow.AddHours(-10);
            for (var i = 0; i < 10; i++)
            {
                db.OhlcvData.Add(new OhlcvData
                {
                    Symbol = "BTC/USDT", Timeframe = "1h",
                    TimestampUtc = t0.AddHours(i),
                    Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 1m,
                });
            }
            await db.SaveChangesAsync();
        }

        var manager = new EnsembleManager(
            0,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new UnusedRegimeDetector(),
            new UnusedFeatureExtractor(),
            new StrategyDecayMonitor(),
            NullLogger<EnsembleManager>.Instance);

        var cfg = await manager.GetConfigurationAsync();
        cfg.ExchangeName = "Binance";
        cfg.Symbol = "BTC/USDT";
        cfg.Timeframe = "1h";
        cfg.Strategies =
        [
            new EnsembleStrategy { StrategyId = "leg-a", StrategyName = "RsiOversold", DisplayName = "A", IsActive = true },
            new EnsembleStrategy { StrategyId = "leg-b", StrategyName = "Momentum", DisplayName = "B", IsActive = true },
        ];
        await manager.UpdateConfigurationAsync(cfg, ProcioneMGR.Services.Ensemble.ConfigWriteContext.Create("test", "prova"));

        return (manager, engine, dbf);
    }

    [Fact]
    public async Task RepeatedReads_SameCandlesAndConfig_SimulateOnlyOnce()
    {
        var (manager, engine, _) = await BuildAsync();

        await manager.GetPerformanceAsync(DateTime.UtcNow.AddDays(-120));
        var afterFirst = engine.Runs;
        Assert.Equal(2, afterFirst); // un backtest per gamba, la prima volta si paga

        // Il giro del poll: status + performance sulla stessa finestra, più volte.
        await manager.GetStatusAsync();
        await manager.GetPerformanceAsync(DateTime.UtcNow.AddDays(-120));
        await manager.GetStatusAsync();

        Assert.Equal(afterFirst, engine.Runs); // cache calda: nessun backtest in più
    }

    [Fact]
    public async Task NewCandle_InvalidatesCache()
    {
        var (manager, engine, dbf) = await BuildAsync();
        await manager.GetStatusAsync();
        var warm = engine.Runs;

        await using (var db = await dbf.CreateDbContextAsync())
        {
            db.OhlcvData.Add(new OhlcvData
            {
                Symbol = "BTC/USDT", Timeframe = "1h",
                TimestampUtc = DateTime.UtcNow.AddMinutes(5), // più recente di tutte
                Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 1m,
            });
            await db.SaveChangesAsync();
        }

        await manager.GetStatusAsync();
        Assert.True(engine.Runs > warm, "una candela nuova deve invalidare la cache");
    }

    [Fact]
    public async Task ConfigChange_InvalidatesCache()
    {
        var (manager, engine, _) = await BuildAsync();
        await manager.GetStatusAsync();
        var warm = engine.Runs;

        var cfg = await manager.GetConfigurationAsync();
        cfg.Strategies[0].Parameters["Period"] = 21m; // qualunque cambio di config cambia la chiave
        await manager.UpdateConfigurationAsync(cfg, ProcioneMGR.Services.Ensemble.ConfigWriteContext.Create("test", "prova"));

        await manager.GetStatusAsync();
        Assert.True(engine.Runs > warm, "una configurazione cambiata deve invalidare la cache");
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
