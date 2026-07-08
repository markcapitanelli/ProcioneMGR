using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di integrazione di <see cref="EnsembleManager.GetDecayReportsAsync"/>: carica la
/// configurazione reale (JSON su SQLite) e i TradeRecords reali dal DB, verificando che il
/// monitor riceva esattamente i dati giusti per ciascuna gamba.
/// </summary>
public class EnsembleManagerDecayTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ensemble_decay_test_{Guid.NewGuid():N}.db");
    private ServiceProvider? _provider;

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class UnusedRegimeDetector : IRegimeDetector
    {
        public Task<RegimeModel> TrainAsync(TrainingConfiguration config, bool activate = true, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ActivateModelAsync(RegimeModel model, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PredictRegimeAsync(MarketFeatures features, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<MarketFeatures>> LabelFeaturesAsync(List<MarketFeatures> features, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RegimeModel?> LoadLatestModelAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class UnusedFeatureExtractor : IMarketFeatureExtractor
    {
        public Task<List<MarketFeatures>> ExtractFeaturesAsync(string exchangeName, string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private async Task<EnsembleManager> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        var provider = services.BuildServiceProvider();
        _provider = provider;

        await using (var db = await provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new EnsembleManager(
            0,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new UnusedRegimeDetector(),
            new UnusedFeatureExtractor(),
            new StrategyDecayMonitor(),
            NullLogger<EnsembleManager>.Instance);
    }

    private static TradeRecord Trade(string strategyId, decimal pnlPercent, DateTime closedAtUtc) => new()
    {
        StrategyId = strategyId,
        Symbol = "BTC/USDT",
        EntryPrice = 100m,
        ExitPrice = 100m * (1m + pnlPercent / 100m),
        Quantity = 1m,
        Pnl = pnlPercent,
        PnlPercent = pnlPercent,
        OpenedAtUtc = closedAtUtc.AddHours(-1),
        ClosedAtUtc = closedAtUtc,
        Mode = TradingMode.Paper,
    };

    [Fact]
    public async Task GetDecayReportsAsync_OneReportPerLeg_OnlyItsOwnTradesCounted()
    {
        var manager = await BuildAsync();

        var cfg = await manager.GetConfigurationAsync();
        cfg.Strategies =
        [
            new EnsembleStrategy { StrategyId = "leg-a", StrategyName = "RsiOversold", DisplayName = "Gamba A", IsActive = true, ExpectedSharpe = 1.5m },
            new EnsembleStrategy { StrategyId = "leg-b", StrategyName = "Momentum", DisplayName = "Gamba B", IsActive = true, ExpectedSharpe = null },
        ];
        await manager.UpdateConfigurationAsync(cfg);

        await using (var db = await _provider!.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 20; i++)
            {
                db.TradeRecords.Add(Trade("leg-a", i % 2 == 0 ? 1.2m : 0.8m, start.AddDays(i * 9)));
            }
            for (var i = 0; i < 5; i++) // sotto la finestra minima (20): "leg-b" resta senza confronto
            {
                db.TradeRecords.Add(Trade("leg-b", 1m, start.AddDays(i)));
            }
            await db.SaveChangesAsync();
        }

        var reports = await manager.GetDecayReportsAsync();

        Assert.Equal(2, reports.Count);
        var legA = reports.Single(r => r.StrategyId == "leg-a");
        var legB = reports.Single(r => r.StrategyId == "leg-b");

        Assert.Equal(20, legA.TradeCount);
        Assert.NotNull(legA.RealizedSharpe); // ExpectedSharpe presente + 20 trade -> confronto calcolato
        Assert.Equal(5, legB.TradeCount);
        Assert.Null(legB.RealizedSharpe); // sotto la finestra minima di 20
    }

    [Fact]
    public async Task GetDecayReportsAsync_NoTrades_ReturnsReportsWithZeroCount()
    {
        var manager = await BuildAsync();
        var cfg = await manager.GetConfigurationAsync();
        cfg.Strategies = [new EnsembleStrategy { StrategyId = "leg-a", StrategyName = "RsiOversold", DisplayName = "Gamba A", IsActive = true, ExpectedSharpe = 1.5m }];
        await manager.UpdateConfigurationAsync(cfg);

        var reports = await manager.GetDecayReportsAsync();

        var report = Assert.Single(reports);
        Assert.Equal(0, report.TradeCount);
        Assert.False(report.IsAlert);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
