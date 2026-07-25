using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R3] Test dell'orchestrazione della Modalità Semplice.
///
/// Le proprietà che contano di più sono due, entrambe di sicurezza:
///  - da qui si avvia SOLO in Paper, mai in Testnet o Live;
///  - avviare senza strategie viene rifiutato con una spiegazione, invece di accendere un motore
///    che non farebbe nulla e lasciare l'utente a fissare una pagina immobile.
/// </summary>
[Collection("Postgres")]
public sealed class BotPageServiceTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public BotPageServiceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>Ensemble manager in memoria: la corsia 0 della Modalità Semplice.</summary>
    private sealed class FakeEnsembleManager : IEnsembleManager
    {
        public EnsembleConfiguration Config { get; private set; } = new()
        {
            ExchangeName = "Binance", Symbol = "BTC/USDT", Timeframe = "4h", TotalCapital = 10_000m,
        };
        public int UpdateCount { get; private set; }

        public int LaneId => 0;
        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default) => Task.FromResult(Config);
        public Task UpdateConfigurationAsync(EnsembleConfiguration c, CancellationToken ct = default)
        {
            Config = c;
            UpdateCount++;
            return Task.CompletedTask;
        }
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StartAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task StopAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProcioneMGR.Services.Monitoring.DecayReport>> GetDecayReportsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class RecordingEngine : ITradingEngine
    {
        public List<TradingMode> Started { get; } = [];
        public bool StopCalled { get; private set; }
        public bool Running { get; set; }

        public int LaneId => 0;
        public Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new TradingEngineStatus { IsRunning = Running, Mode = TradingMode.Paper });
        public Task StartAsync(TradingMode mode, CancellationToken ct = default)
        {
            Started.Add(mode);
            Running = true;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct = default) { StopCalled = true; Running = false; return Task.CompletedTask; }
        public Task EmergencyStopAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default) => Task.FromResult(new List<OpenPosition>());
        public Task ClosePositionAsync(string positionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAllPositionsAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetStopLossTakeProfitAsync(string positionId, decimal? sl, decimal? tp, decimal? tsl = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new List<Order>());
        public Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default) => Task.FromResult(new TradingPerformance());
        public Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingApplier : IPipelineApplier
    {
        public List<Guid> Applied { get; } = [];
        public int LaneCount => 3;
        public Task<ApplyResult> ApplyRunAsync(Guid runId, CancellationToken ct = default)
        {
            Applied.Add(runId);
            return Task.FromResult(new ApplyResult { LanesUsed = 1, Message = "ok" });
        }
        public Task<ApplyResult> ApplyRecommendationAsync(PipelineRecommendation r, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<EnsembleSummary> GetCurrentEnsembleSummaryAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public EnsembleSummary SummarizeRecommendation(PipelineRecommendation r) => throw new NotImplementedException();
    }

    private async Task<(BotPageService Svc, FakeEnsembleManager Ens, RecordingEngine Engine, RecordingApplier Applier, IDbContextFactory<ApplicationDbContext> Db)>
        BuildAsync(bool withStrategies)
    {
        var ensemble = new FakeEnsembleManager();
        var engine = new RecordingEngine();
        var applier = new RecordingApplier();

        if (withStrategies)
        {
            var cfg = ensemble.Config;
            cfg.Strategies = [new EnsembleStrategy { StrategyId = "s1", StrategyName = "EmaCross", DisplayName = "EMA", IsActive = true }];
            await ensemble.UpdateConfigurationAsync(cfg);
        }

        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        services.AddKeyedSingleton<IEnsembleManager>(BotPageService.BotLaneId, ensemble);
        services.AddKeyedSingleton<ITradingEngine>(BotPageService.BotLaneId, engine);
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var svc = new BotPageService(_provider, dbFactory, applier, NullLogger<BotPageService>.Instance);
        return (svc, ensemble, engine, applier, dbFactory);
    }

    [Fact]
    public async Task Start_AlwaysUsesPaper_NeverTestnetOrLive()
    {
        // Confine non negoziabile: una vista "un pulsante" non deve poter avviare operatività con
        // soldi veri. Il passaggio a Testnet/Live resta un'azione esplicita da /trading.
        var (svc, _, engine, _, _) = await BuildAsync(withStrategies: true);
        await svc.LoadAsync();

        await svc.StartAsync();

        Assert.Equal([TradingMode.Paper], engine.Started);
        Assert.DoesNotContain(TradingMode.Live, engine.Started);
        Assert.DoesNotContain(TradingMode.Testnet, engine.Started);
    }

    [Fact]
    public async Task Start_WithoutStrategies_IsRefusedWithAnExplanation()
    {
        // Avviare un motore senza strategie produrrebbe una pagina che dice "IN FUNZIONE" mentre
        // non succede assolutamente nulla: il modo migliore per far credere all'utente che il
        // prodotto sia rotto.
        var (svc, _, engine, _, _) = await BuildAsync(withStrategies: false);
        await svc.LoadAsync();

        await svc.StartAsync();

        Assert.Empty(engine.Started);
        Assert.True(svc.IsError);
        Assert.Contains("strategia", svc.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_PersistsCapitalAndProfile_OnTheLane()
    {
        var (svc, ensemble, _, _, _) = await BuildAsync(withStrategies: true);
        await svc.LoadAsync();

        svc.Capital = 25_000m;
        svc.ProfileName = RiskProfiles.Dinamico;
        await svc.StartAsync();

        Assert.Equal(25_000m, ensemble.Config.TotalCapital);
        Assert.Equal(RiskProfiles.Dinamico, ensemble.Config.RiskProfileName);
    }

    [Fact]
    public async Task Save_PersistsWithoutStarting()
    {
        var (svc, ensemble, engine, _, _) = await BuildAsync(withStrategies: true);
        await svc.LoadAsync();

        svc.Capital = 7_500m;
        svc.ProfileName = RiskProfiles.Prudente;
        await svc.SaveAsync();

        Assert.Equal(7_500m, ensemble.Config.TotalCapital);
        Assert.Equal(RiskProfiles.Prudente, ensemble.Config.RiskProfileName);
        Assert.Empty(engine.Started);
    }

    [Fact]
    public async Task Load_RestoresPreviouslyChosenProfile()
    {
        var (svc, ensemble, _, _, _) = await BuildAsync(withStrategies: true);
        var cfg = ensemble.Config;
        cfg.RiskProfileName = RiskProfiles.Prudente;
        cfg.TotalCapital = 3_000m;
        await ensemble.UpdateConfigurationAsync(cfg);

        await svc.LoadAsync();

        Assert.Equal(RiskProfiles.Prudente, svc.ProfileName);
        Assert.Equal(3_000m, svc.Capital);
    }

    [Fact]
    public async Task Load_UnknownStoredProfile_FallsBackToDefault_WithoutThrowing()
    {
        // Un nome di profilo rimasto in configurazione dopo una rinomina non deve rompere la pagina.
        var (svc, ensemble, _, _, _) = await BuildAsync(withStrategies: true);
        var cfg = ensemble.Config;
        cfg.RiskProfileName = "ProfiloCheNonEsistePiù";
        await ensemble.UpdateConfigurationAsync(cfg);

        await svc.LoadAsync();

        Assert.Equal(RiskProfiles.Default.Name, svc.ProfileName);
    }

    [Fact]
    public async Task TimeframeMismatch_IsDetected_WhenLaneDivergesFromProfile()
    {
        // La corsia è a 4h. "Prudente" preferisce 4h/1d ⇒ nessun avviso; "Dinamico" preferisce
        // 15m/1h ⇒ avviso, perché il suo tetto di operazioni frenerà comunque una strategia lenta.
        var (svc, _, _, _, _) = await BuildAsync(withStrategies: true);
        await svc.LoadAsync();

        svc.ProfileName = RiskProfiles.Prudente;
        Assert.False(svc.TimeframeMismatch);

        svc.ProfileName = RiskProfiles.Dinamico;
        Assert.True(svc.TimeframeMismatch);
    }

    [Fact]
    public async Task ApplyLatestResearch_WithoutAvailableRun_ReportsInsteadOfThrowing()
    {
        var (svc, _, _, applier, _) = await BuildAsync(withStrategies: false);
        await svc.LoadAsync();

        await svc.ApplyLatestResearchAsync();

        Assert.Empty(applier.Applied);
        Assert.True(svc.IsError);
    }

    [Fact]
    public async Task ApplyLatestResearch_PicksTheMostRecentRunWithLegs()
    {
        var (svc, _, _, applier, dbFactory) = await BuildAsync(withStrategies: false);

        var oldRun = Guid.NewGuid();
        var emptyRecent = Guid.NewGuid();
        var goodRecent = Guid.NewGuid();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.PipelineRuns.AddRange(
                new PipelineRun { Id = oldRun, Status = "Completed", StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), RecommendationJson = """{"ensembleLegs":[{"symbol":"BTC/USDT"}]}""" },
                // Più recente ma SENZA gambe: applicarlo non cambierebbe nulla, va saltato.
                new PipelineRun { Id = emptyRecent, Status = "Completed", StartedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), RecommendationJson = """{"ensembleLegs":[]}""" },
                new PipelineRun { Id = goodRecent, Status = "Completed", StartedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), RecommendationJson = """{"ensembleLegs":[{"symbol":"ETH/USDT"}]}""" });
            await db.SaveChangesAsync();
        }

        await svc.LoadAsync();
        await svc.ApplyLatestResearchAsync();

        Assert.Equal([goodRecent], applier.Applied);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
