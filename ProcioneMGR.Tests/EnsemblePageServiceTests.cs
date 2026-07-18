using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Monitoring.Drift;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'orchestrazione estratta da <c>Ensemble.razor</c> (P1-5, PRD-CONSOLIDAMENTO-
/// ARCHITETTURA.md §3.3): caricamento per corsia (keyed DI), composizione delle gambe
/// (predefinita/salvata/ML/Champion), ciclo di vita save/start/stop/rebalance, serie di
/// performance, monitor drift e piani di esecuzione — prima tutto nel <c>@code</c> del componente,
/// senza test indipendenti da Blazor. I manager di ensemble sono fake keyed per corsia che
/// catturano le chiamate; cataloghi/candele/job vivono su Postgres effimero reale.
/// </summary>
[Collection("Postgres")]
public sealed class EnsemblePageServiceTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public EnsemblePageServiceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    // --- Fakes ---------------------------------------------------------------------------------

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class FakeEnsembleManager(int laneId) : IEnsembleManager
    {
        public int LaneId => laneId;
        public EnsembleConfiguration ConfigToReturn { get; set; } = new()
        {
            ExchangeName = "Binance", Symbol = $"LANE{laneId}/USDT", Timeframe = "1h", TotalCapital = 10_000m,
        };
        public EnsembleStatus StatusToReturn { get; set; } = new();
        public EnsemblePerformance PerformanceToReturn { get; set; } = new();
        public List<DecayReport> DecayToReturn { get; set; } = [];
        public List<string> Calls { get; } = [];
        public EnsembleConfiguration? LastUpdatedConfig { get; private set; }

        public Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default)
        { Calls.Add("GetConfiguration"); return Task.FromResult(ConfigToReturn); }
        public Task UpdateConfigurationAsync(EnsembleConfiguration c, CancellationToken ct = default)
        { Calls.Add("Update"); LastUpdatedConfig = c; return Task.CompletedTask; }
        public Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default)
        { Calls.Add("GetStatus"); return Task.FromResult(StatusToReturn); }
        public Task StartAsync(CancellationToken ct = default) { Calls.Add("Start"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { Calls.Add("Stop"); return Task.CompletedTask; }
        public Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)
        { Calls.Add("GetPerformance"); return Task.FromResult(PerformanceToReturn); }
        public Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default)
        { Calls.Add($"Rebalance:{reason}"); return Task.CompletedTask; }
        public Task<IReadOnlyList<DecayReport>> GetDecayReportsAsync(CancellationToken ct = default)
        { Calls.Add("GetDecay"); return Task.FromResult<IReadOnlyList<DecayReport>>(DecayToReturn); }
    }

    private sealed class FakeRegistry : IModelRegistry
    {
        public SavedMlModel? ChampionToReturn { get; set; }
        public (string Symbol, string Timeframe)? LastChampionQuery { get; private set; }

        public Task<SavedMlModel?> GetChampionAsync(string symbol, string timeframe, CancellationToken ct = default)
        { LastChampionQuery = (symbol, timeframe); return Task.FromResult(ChampionToReturn); }
        public Task<IReadOnlyList<SavedMlModel>> ListGroupAsync(string symbol, string timeframe, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PromoteToChallengerAsync(int modelId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PromotionOutcome> TryPromoteToChampionAsync(int modelId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RetireAsync(int modelId, string reason, bool requestRetrain, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeDriftMonitor : IFeatureDriftMonitor
    {
        public IReadOnlyList<FactorDriftReport> ReportsToReturn { get; set; } = [];
        public int ReceivedCandleCount { get; private set; }

        public Task<IReadOnlyList<FactorDriftReport>> EvaluateAsync(SavedMlModel model, IReadOnlyList<OhlcvData> recentCandles, DriftThresholds? thresholds = null, CancellationToken ct = default)
        {
            ReceivedCandleCount = recentCandles.Count;
            return Task.FromResult(ReportsToReturn);
        }
    }

    // --- Setup ---------------------------------------------------------------------------------

    private FakeEnsembleManager _lane0 = null!;
    private FakeEnsembleManager _lane1 = null!;
    private FakeRegistry _registry = null!;
    private FakeDriftMonitor _drift = null!;

    private async Task<(EnsemblePageService Svc, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync(bool ensureSchema = true)
    {
        _lane0 = new FakeEnsembleManager(0);
        _lane1 = new FakeEnsembleManager(1);
        _registry = new FakeRegistry();
        _drift = new FakeDriftMonitor();

        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        services.AddKeyedSingleton<IEnsembleManager>(0, _lane0);
        services.AddKeyedSingleton<IEnsembleManager>(1, _lane1);
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        if (ensureSchema)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
        }

        var svc = new EnsemblePageService(_provider, new StrategyFactory(), _drift, _registry, dbFactory);
        return (svc, dbFactory);
    }

    // --- Caricamento per corsia ----------------------------------------------------------------

    [Fact]
    public async Task LoadConfigAndChampion_UsesKeyedManagerOfLane_AndQueriesChampionForConfigSymbol()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        _registry.ChampionToReturn = new SavedMlModel { Id = 9, Name = "Campione", Symbol = "LANE1/USDT", Timeframe = "1h" };

        await svc.LoadConfigAndChampionAsync(1);

        Assert.Equal("LANE1/USDT", svc.Config!.Symbol);         // manager della corsia 1, non 0
        Assert.Empty(_lane0.Calls);
        Assert.Equal(("LANE1/USDT", "1h"), _registry.LastChampionQuery);
        Assert.Equal(9, svc.Champion!.Id);
    }

    [Fact]
    public async Task RefreshAsync_BuildsPerfSeries_TotalPlusPerStrategy()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _lane0.PerformanceToReturn = new EnsemblePerformance
        {
            TotalEquityCurve = [new EquityPoint { Timestamp = t0, Capital = 10_000m }],
            StrategyCurves =
            [
                new StrategyEquityCurve { DisplayName = "Ema", EquityCurve = [new EquityPoint { Timestamp = t0, Capital = 5_000m }] },
                new StrategyEquityCurve { DisplayName = "Rsi", EquityCurve = [new EquityPoint { Timestamp = t0, Capital = 5_000m }] },
            ],
        };

        await svc.RefreshAsync(0);

        Assert.Equal(3, svc.PerfSeries.Count);
        Assert.Equal("Totale", svc.PerfSeries[0].Title);
        Assert.Equal(["Ema", "Rsi"], svc.PerfSeries.Skip(1).Select(s => s.Title));
        Assert.NotEqual(svc.PerfSeries[1].Color, svc.PerfSeries[2].Color);   // palette progressiva
    }

    // --- Cataloghi -----------------------------------------------------------------------------

    [Fact]
    public async Task LoadSavedCatalogs_OrdersOptimizedFirst()
    {
        var (svc, db) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.Users.Add(new ApplicationUser { Id = "u", UserName = "u@t.io" });
            ctx.SavedStrategies.Add(new SavedStrategy { UserId = "u", Name = "Semplice", StrategyName = "EmaCross", ParametersJson = "{}", IsOptimized = false, CreatedAt = new DateTime(2026, 3, 1) });
            ctx.SavedStrategies.Add(new SavedStrategy { UserId = "u", Name = "Ottimizzata", StrategyName = "EmaCross", ParametersJson = "{}", IsOptimized = true, OptimizationDate = new DateTime(2026, 1, 1), OptimizationSharpe = 1.2m });
            await ctx.SaveChangesAsync();
        }

        await svc.LoadSavedCatalogsAsync();

        Assert.Equal(["Ottimizzata", "Semplice"], svc.SavedStrategies.Select(s => s.Name));
    }

    // --- Composizione gambe --------------------------------------------------------------------

    [Fact]
    public async Task AddPredefined_AddsWithDefaultParameters()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        await svc.LoadConfigAndChampionAsync(0);

        svc.AddPredefined("EmaCross");

        var leg = Assert.Single(svc.Config!.Strategies);
        Assert.Equal("EmaCross", leg.StrategyName);
        Assert.NotEmpty(leg.Parameters);
    }

    [Fact]
    public async Task AddFromSaved_CopiesParams_ExpectedSharpeOnlyIfOptimized()
    {
        var (svc, db) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.Users.Add(new ApplicationUser { Id = "u", UserName = "u@t.io" });
            ctx.SavedStrategies.Add(new SavedStrategy { UserId = "u", Name = "Opt", StrategyName = "EmaCross", ParametersJson = "{\"FastPeriod\": 7}", IsOptimized = true, OptimizationSharpe = 1.5m });
            ctx.SavedStrategies.Add(new SavedStrategy { UserId = "u", Name = "Manuale", StrategyName = "RsiOversold", ParametersJson = "{}", IsOptimized = false });
            await ctx.SaveChangesAsync();
        }
        await svc.LoadSavedCatalogsAsync();
        await svc.LoadConfigAndChampionAsync(0);
        var optId = svc.SavedStrategies.First(s => s.Name == "Opt").Id;
        var manId = svc.SavedStrategies.First(s => s.Name == "Manuale").Id;

        svc.AddFromSaved(optId);
        svc.AddFromSaved(manId);
        svc.AddFromSaved(0);          // id 0: no-op

        Assert.Equal(2, svc.Config!.Strategies.Count);
        var opt = svc.Config.Strategies[0];
        Assert.Equal(7m, opt.Parameters["FastPeriod"]);
        Assert.Equal(1.5m, opt.ExpectedSharpe);               // baseline per il decay monitor
        Assert.Contains("(opt)", opt.DisplayName);
        Assert.Null(svc.Config.Strategies[1].ExpectedSharpe); // non ottimizzata: "in attesa"
    }

    [Fact]
    public async Task AddFromMlModel_PinsModelIdAndThresholds()
    {
        var (svc, db) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.Users.Add(new ApplicationUser { Id = "u", UserName = "u@t.io" });
            ctx.SavedMlModels.Add(new SavedMlModel { UserId = "u", Name = "Modello", ModelType = "Linear", Symbol = "BTC/USDT", Timeframe = "1h", FactorsJson = "[]", ModelBytes = [] });
            await ctx.SaveChangesAsync();
        }
        await svc.LoadSavedCatalogsAsync();
        await svc.LoadConfigAndChampionAsync(0);
        var modelId = svc.SavedMlModels.Single().Id;

        svc.AddFromMlModel(modelId, 0.01m, 0.02m);

        var leg = Assert.Single(svc.Config!.Strategies);
        Assert.Equal("Ml", leg.StrategyName);
        Assert.Equal(modelId, leg.SavedMlModelId);
        Assert.Equal(modelId, (int)leg.Parameters["SavedModelId"]);
        Assert.Equal(0.01m, leg.Parameters["LongThreshold"]);
        Assert.Equal(0.02m, leg.Parameters["ShortThreshold"]);
    }

    [Fact]
    public async Task AddChampion_AddsSentinel_OncePerLane_NoOpWithoutChampion()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        await svc.LoadConfigAndChampionAsync(0);

        svc.AddChampion(0.002m, 0.003m);                       // nessun Champion → no-op
        Assert.Empty(svc.Config!.Strategies);

        _registry.ChampionToReturn = new SavedMlModel { Id = 5, Name = "Champ", Symbol = "LANE0/USDT", Timeframe = "1h" };
        await svc.LoadConfigAndChampionAsync(0);

        svc.AddChampion(0.002m, 0.003m);
        svc.AddChampion(0.002m, 0.003m);                       // duplicato → no-op

        var leg = Assert.Single(svc.Config!.Strategies);
        Assert.Equal(TradingEngine.ChampionStrategyName, leg.StrategyName);   // sentinella, NON pinnato per Id
        Assert.Equal(0.002m, leg.Parameters["LongThreshold"]);
        Assert.False(leg.Parameters.ContainsKey("SavedModelId"));
    }

    [Fact]
    public async Task RemoveStrategy_RemovesById()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        await svc.LoadConfigAndChampionAsync(0);
        svc.AddPredefined("EmaCross");
        var id = svc.Config!.Strategies[0].StrategyId;

        svc.RemoveStrategy(id);

        Assert.Empty(svc.Config.Strategies);
    }

    // --- Ciclo di vita -------------------------------------------------------------------------

    [Fact]
    public async Task SaveStartStopRebalance_DriveKeyedManagerAndFlags()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        await svc.LoadConfigAndChampionAsync(0);

        Assert.Equal("Configurazione salvata.", await svc.SaveAsync(0));
        Assert.Same(svc.Config, _lane0.LastUpdatedConfig);

        Assert.Equal("Ensemble avviato.", await svc.StartEnsembleAsync(0));
        Assert.True(svc.Config!.IsEnabled);
        Assert.Contains("Start", _lane0.Calls);

        Assert.Equal("Ensemble fermato.", await svc.StopEnsembleAsync(0));
        Assert.False(svc.Config.IsEnabled);

        var before = _lane0.Calls.Count(c => c == "GetConfiguration");
        Assert.Equal("Rebalancing eseguito.", await svc.RebalanceNowAsync(0));
        Assert.Contains("Rebalance:Manual", _lane0.Calls);
        Assert.Equal(before + 1, _lane0.Calls.Count(c => c == "GetConfiguration"));   // config ricaricata dopo il rebalance
    }

    // --- Drift ---------------------------------------------------------------------------------

    [Fact]
    public async Task EvaluateDrift_ModelMissing_InsufficientCandles_HappyPath()
    {
        var (svc, db) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.Users.Add(new ApplicationUser { Id = "u", UserName = "u@t.io" });
            ctx.SavedMlModels.Add(new SavedMlModel { UserId = "u", Name = "M", ModelType = "Linear", Symbol = "DRIFT/USDT", Timeframe = "1h", FactorsJson = "[]", ModelBytes = [] });
            await ctx.SaveChangesAsync();
        }
        await svc.LoadSavedCatalogsAsync();
        var modelId = svc.SavedMlModels.Single().Id;

        var missing = await svc.EvaluateDriftAsync(modelId + 999);
        Assert.True(missing.IsError);

        var tooFew = await svc.EvaluateDriftAsync(modelId);   // zero candele nel DB
        Assert.True(tooFew.IsError);
        Assert.Contains("insufficienti", tooFew.Message);

        await using (var ctx = await db.CreateDbContextAsync())
        {
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 50; i++)
            {
                ctx.OhlcvData.Add(new OhlcvData { Symbol = "DRIFT/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(i), Open = 1, High = 1, Low = 1, Close = 1, Volume = 1 });
            }
            await ctx.SaveChangesAsync();
        }
        _drift.ReportsToReturn =
        [
            new FactorDriftReport { FeatureName = "Momentum", Results = [new DriftResult("Psi", 0.5, null, DriftSeverity.Alert, "test")] },
            new FactorDriftReport { FeatureName = "Rsi", Results = [new DriftResult("Psi", 0.01, null, DriftSeverity.None, "test")] },
        ];

        var ok = await svc.EvaluateDriftAsync(modelId);

        Assert.False(ok.IsError);
        Assert.Contains("1/2 fattori in drift", ok.Message);
        Assert.Equal(50, _drift.ReceivedCandleCount);          // tutte le candele disponibili (< finestra max)
        Assert.Equal(2, svc.DriftReports.Count);
    }

    // --- Piani di esecuzione -------------------------------------------------------------------

    [Fact]
    public async Task LoadExecutionJobs_FiltersByLane_Take20Desc()
    {
        var (svc, db) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 25; i++)
            {
                ctx.ExecutionJobs.Add(new ExecutionJob { LaneId = 0, Algorithm = "Twap", Side = OrderSide.Buy, Status = "Completed", CreatedAtUtc = t0.AddMinutes(i) });
            }
            ctx.ExecutionJobs.Add(new ExecutionJob { LaneId = 1, Algorithm = "Vwap", Side = OrderSide.Sell, Status = "Running", CreatedAtUtc = t0.AddDays(1) });
            await ctx.SaveChangesAsync();
        }

        await svc.LoadExecutionJobsAsync(0);

        Assert.Equal(20, svc.ExecutionJobs.Count);                       // cap a 20
        Assert.All(svc.ExecutionJobs, j => Assert.Equal(0, j.LaneId));   // solo la corsia richiesta
        Assert.True(svc.ExecutionJobs[0].CreatedAtUtc > svc.ExecutionJobs[^1].CreatedAtUtc);   // più recenti prima
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
