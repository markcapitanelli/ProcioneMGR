using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di integrazione di <see cref="EnsembleManager.GetDecayReportsAsync"/>: carica la
/// configurazione reale (JSON su Postgres) e i TradeRecords reali dal DB, verificando che il
/// monitor riceva esattamente i dati giusti per ciascuna gamba.
/// </summary>
[Collection("Postgres")]
public class EnsembleManagerDecayTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public EnsembleManagerDecayTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

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
        public Task<RegimeModel?> LoadActiveModelAsync(string symbol, string timeframe, CancellationToken ct = default) => throw new NotImplementedException();
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
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
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

    private static TradeRecord Trade(string strategyId, decimal pnlPercent, DateTime closedAtUtc, string symbol = "BTC/USDT") => new()
    {
        StrategyId = strategyId,
        Symbol = symbol,
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

    /// <summary>
    /// [I13b, sotto rete dal 2026-08-21] <b>Il filtro sul simbolo attuale non aveva un solo test.</b>
    ///
    /// <para>È la sola difesa della piattaforma contro gli aggregati che mescolano le vite di una
    /// corsia, e una regressione che togliesse la riga <c>t.Symbol == cfg.Symbol</c> sarebbe passata
    /// per intera la suite. Il caso è reale e misurato: le corsie sono state riassegnate più volte —
    /// la 2 ha operato su SHIB, poi SUI, poi ADA — e la 0 ha 159 operazioni in totale con ZERO sulla
    /// coppia che ha configurata oggi.</para>
    /// </summary>
    [Fact]
    public async Task GetDecayReportsAsync_ScartaITradeDiUnAltroSimbolo_ELiDICHIARA()
    {
        var manager = await BuildAsync();
        var cfg = await manager.GetConfigurationAsync();
        cfg.Symbol = "BTC/USDT";                       // la vita ATTUALE della corsia
        cfg.Strategies = [new EnsembleStrategy { StrategyId = "leg-a", StrategyName = "RsiOversold", DisplayName = "Gamba A", IsActive = true, ExpectedSharpe = 1.5m }];
        await manager.UpdateConfigurationAsync(cfg);

        await using (var db = await _provider!.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 20; i++)
            {
                db.TradeRecords.Add(Trade("leg-a", i % 2 == 0 ? 1.2m : 0.8m, start.AddDays(i * 9)));
            }
            // Vita precedente della stessa corsia, stessa gamba, altro mercato. Senza il filtro
            // finirebbero nello Sharpe «realizzato» insieme agli altri, e nessuna riga lo direbbe.
            for (var i = 0; i < 7; i++)
            {
                db.TradeRecords.Add(Trade("leg-a", -40m, start.AddDays(-30 + i), symbol: "SUI/USDT"));
            }
            await db.SaveChangesAsync();
        }

        var report = Assert.Single(await manager.GetDecayReportsAsync());

        Assert.Equal(20, report.TradeCount);                    // solo la vita attuale
        Assert.Equal(7, report.TradesExcludedOtherSymbol);      // e gli altri si DICHIARANO
        Assert.Equal("BTC/USDT", report.Symbol);                // su quale mercato è stato misurato
        Assert.NotNull(report.RealizedSharpe);
        Assert.True(report.RealizedSharpe > 0m,
            $"Sharpe {report.RealizedSharpe}: i sette trade a -40% di un'altra coppia sono entrati nel calcolo");
    }

    /// <summary>
    /// [I13b] Il caso che il pannello di /ensemble esiste per spiegare: una corsia appena riassegnata
    /// non ha ancora nulla da misurare sulla coppia nuova, e senza il conteggio degli scarti «zero
    /// operazioni» si legge come un guasto invece che come «ha appena cambiato mestiere».
    /// </summary>
    [Fact]
    public async Task CorsiaAppenaRiassegnata_ZeroTradeSullaCoppiaNuova_MaLoSpiegaCoiScarti()
    {
        var manager = await BuildAsync();
        var cfg = await manager.GetConfigurationAsync();
        cfg.Symbol = "ADA/USDT";
        cfg.Strategies = [new EnsembleStrategy { StrategyId = "leg-a", StrategyName = "RsiOversold", DisplayName = "Gamba A", IsActive = true, ExpectedSharpe = 1.5m }];
        await manager.UpdateConfigurationAsync(cfg);

        await using (var db = await _provider!.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 12; i++) db.TradeRecords.Add(Trade("leg-a", 1m, start.AddDays(i), symbol: "SUI/USDT"));
            await db.SaveChangesAsync();
        }

        var report = Assert.Single(await manager.GetDecayReportsAsync());

        Assert.Equal(0, report.TradeCount);
        Assert.Equal(12, report.TradesExcludedOtherSymbol);
        Assert.False(report.IsMeasurable);
    }

    /// <summary>
    /// [C1b] Il fill rotto della corsia 2, riprodotto: un trade a <b>−227.340%</b> — SUI/USDT, 9 luglio
    /// 2026, entrato a 0,7694 e uscito a 1748,18. Il <c>FillSanityCheck</c> ha chiuso il buco il 18
    /// luglio, ma protegge le righe NUOVE: quella storica è ancora in tabella. Se la corsia torna su
    /// quel simbolo, una riga sola decide da sola lo Sharpe «realizzato» della gamba.
    /// </summary>
    [Fact]
    public async Task UnFillRotto_NonEntraNelCalcolo_ESiDICHIARA()
    {
        var manager = await BuildAsync();
        var cfg = await manager.GetConfigurationAsync();
        cfg.Symbol = "BTC/USDT";
        cfg.Strategies = [new EnsembleStrategy { StrategyId = "leg-a", StrategyName = "RsiOversold", DisplayName = "Gamba A", IsActive = true, ExpectedSharpe = 1.5m }];
        await manager.UpdateConfigurationAsync(cfg);

        await using (var db = await _provider!.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 20; i++)
            {
                db.TradeRecords.Add(Trade("leg-a", i % 2 == 0 ? 1.2m : 0.8m, start.AddDays(i * 9)));
            }
            // Stesso simbolo di oggi: il filtro I13b NON lo prende. Ed è il piu' recente, quindi
            // senza questo filtro entrerebbe di sicuro nella finestra delle ultime 20.
            db.TradeRecords.Add(Trade("leg-a", -227340.72m, start.AddDays(500)));
            await db.SaveChangesAsync();
        }

        var report = Assert.Single(await manager.GetDecayReportsAsync());

        Assert.Equal(20, report.TradeCount);
        Assert.Equal(1, report.TradesExcludedImplausible);
        Assert.Equal(0, report.TradesExcludedOtherSymbol);   // non è un problema di simbolo
        Assert.True(report.RealizedSharpe > 0m,
            $"Sharpe {report.RealizedSharpe}: il fill rotto è entrato nel calcolo");
    }

    /// <summary>
    /// [C1b] Il filtro deve togliere l'impossibile, <b>non le code</b>: una perdita grande ma reale
    /// è il segnale che il pannello esiste per mostrare, e silenziarla sarebbe il difetto opposto.
    /// </summary>
    [Fact]
    public async Task UnaPerditaGrandeMaPOSSIBILE_RestaNelCalcolo()
    {
        var manager = await BuildAsync();
        var cfg = await manager.GetConfigurationAsync();
        cfg.Symbol = "BTC/USDT";
        cfg.Strategies = [new EnsembleStrategy { StrategyId = "leg-a", StrategyName = "RsiOversold", DisplayName = "Gamba A", IsActive = true, ExpectedSharpe = 1.5m }];
        await manager.UpdateConfigurationAsync(cfg);

        await using (var db = await _provider!.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 19; i++) db.TradeRecords.Add(Trade("leg-a", 1m, start.AddDays(i * 9)));
            db.TradeRecords.Add(Trade("leg-a", -85m, start.AddDays(500)));   // -85%: brutale, ma possibile
            await db.SaveChangesAsync();
        }

        var report = Assert.Single(await manager.GetDecayReportsAsync());

        Assert.Equal(20, report.TradeCount);
        Assert.Equal(0, report.TradesExcludedImplausible);
        Assert.True(report.RealizedSharpe < 0m,
            $"Sharpe {report.RealizedSharpe}: la perdita reale è stata silenziata insieme ai fill rotti");
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
    }
}
