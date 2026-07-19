using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Experiments;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'orchestrazione estratta da <c>Optimization.razor</c> (P1-5, PRD-CONSOLIDAMENTO-
/// ARCHITETTURA.md §3.3): prima di questa estrazione tutta la logica — range di default per
/// strategia, preset validati, handoff da Backtest/ML Lab col ricentraggio dei range, costruzione
/// della config di sweep (incluso il range "pinnato" SavedModelId per i modelli ML), parsing della
/// matrice heatmap e salvataggio della configurazione migliore — viveva nel blocco <c>@code</c> del
/// componente, senza test indipendenti da Blazor. Il motore di ottimizzazione qui è un fake che
/// cattura la config e restituisce un risultato predefinito: il walk-forward reale ha già i propri
/// test — questo file verifica l'ORCHESTRAZIONE, alla giusta altitudine.
/// </summary>
[Collection("Postgres")]
public sealed class OptimizationPageServiceTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public OptimizationPageServiceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private const string UserA = "user-a";

    // --- Fakes ---------------------------------------------------------------------------------

    private sealed class NoopTracker : IExperimentTracker
    {
        public Task<Guid> StartRunAsync(string kind, string name, object? parameters, string? symbol = null,
            string? timeframe = null, string? createdBy = null, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task LogMetricsAsync(Guid runId, IReadOnlyDictionary<string, decimal> metrics, CancellationToken ct = default) => Task.CompletedTask;
        public Task LogArtifactAsync(Guid runId, string kindTag, object payload, CancellationToken ct = default) => Task.CompletedTask;
        public Task CompleteAsync(Guid runId, string status, string? errorLog = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>Cattura la config passata e restituisce un risultato predefinito (il motore vero ha i propri test).</summary>
    private sealed class CapturingOptEngine : IOptimizationEngine
    {
        public OptimizationConfiguration? LastConfig { get; private set; }
        public OptimizationResult ResultToReturn { get; set; } = CannedResult();

        public Task<OptimizationResult> OptimizeAsync(OptimizationConfiguration config, IProgress<OptimizationProgress>? progress, CancellationToken ct)
        {
            LastConfig = config;
            progress?.Report(new OptimizationProgress { CombinationsTested = 1, TotalCombinations = 2, Message = "fake" });
            return Task.FromResult(ResultToReturn);
        }

        public static OptimizationResult CannedResult() => new()
        {
            BestParameters =
            [
                new ParameterSet
                {
                    Parameters = new Dictionary<string, decimal> { ["FastPeriod"] = 10m, ["SlowPeriod"] = 30m },
                    InSampleSharpe = 1.4m, OutOfSampleSharpe = 0.9m, TotalReturn = 12m, MaxDrawdown = 5m, TotalTrades = 33,
                },
            ],
            WalkForwardAnalysis = new WalkForwardResult
            {
                Windows = [new WalkForwardWindow { WindowIndex = 1 }, new WalkForwardWindow { WindowIndex = 2 }],
                AverageOutOfSampleSharpe = 0.8m,
                CombinedEquityCurve =
                [
                    new EquityPoint { Timestamp = new DateTime(2025, 1, 1), Capital = 10_000m },
                    new EquityPoint { Timestamp = new DateTime(2025, 2, 1), Capital = 10_500m },
                ],
            },
            AllResults = new Dictionary<string, decimal>
            {
                ["FastPeriod=10,SlowPeriod=30"] = 0.9m,
                ["FastPeriod=10,SlowPeriod=40"] = 0.5m,
                ["FastPeriod=12,SlowPeriod=30"] = -0.2m,
                // (12,40) mai valutata: nella heatmap deve risultare null.
            },
            TotalCombinationsTested = 3,
            ExecutionTime = TimeSpan.FromSeconds(1),
        };
    }

    // --- Setup ---------------------------------------------------------------------------------

    private async Task<(OptimizationPageService Svc, CapturingOptEngine Engine, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync(bool ensureSchema = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        if (ensureSchema)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
        }

        var engine = new CapturingOptEngine();
        var svc = new OptimizationPageService(dbFactory, engine, new StrategyFactory(), new NoopTracker());
        return (svc, engine, dbFactory);
    }

    private static OptimizationConfigSnapshot DefaultSnapshot(OptimizationPageService svc, string strategy = "EmaCross") => new(
        ExchangeName.Binance, "TEST/USDT", "1h",
        new DateTime(2024, 6, 1), new DateTime(2026, 6, 1),
        10_000m, 0.1m, 12, 3, 3,
        SearchStrategy.GridSearch, 40, 8, 42,
        strategy, MlModelId: 0, svc.DefaultRangesFor(strategy));

    // --- Range di default ----------------------------------------------------------------------

    [Fact]
    public async Task DefaultRangesFor_Ml_ReturnsThresholdRangesOnly()
    {
        var (svc, _, _) = await BuildAsync(ensureSchema: false);
        var ranges = svc.DefaultRangesFor(OptimizationPageService.MlStrategyName);
        Assert.Equal(["LongThreshold", "ShortThreshold"], ranges.Select(r => r.Key));
        Assert.All(ranges, r => Assert.False(r.IsInteger));
    }

    [Fact]
    public async Task DefaultRangesFor_RuleStrategy_AppliesIntegerHeuristicAndStepShape()
    {
        var (svc, _, _) = await BuildAsync(ensureSchema: false);
        var ranges = svc.DefaultRangesFor("EmaCross");
        Assert.NotEmpty(ranges);
        foreach (var r in ranges)
        {
            var expectInt = r.Key.Contains("Period") || r.Key.Contains("Lookback");
            Assert.Equal(expectInt, r.IsInteger);
            Assert.True(r.Step > 0m);
            Assert.Equal(r.Min + 4 * r.Step, r.Max);   // forma "min = default, max = default + 4 step"
        }
    }

    [Fact]
    public async Task TotalCombinations_CartesianProduct_AndZeroStepGuard()
    {
        var (svc, _, _) = await BuildAsync(ensureSchema: false);
        List<OptRange> ranges =
        [
            new("A", "A", 1m, 3m, 1m, true),    // 3 valori
            new("B", "B", 0m, 1m, 0.5m, false), // 3 valori
        ];
        Assert.Equal(9, OptimizationPageService.TotalCombinations(ranges));
        Assert.Equal(0, OptimizationPageService.TotalCombinations([new OptRange("A", "A", 1m, 3m, 0m, true)]));
    }

    // --- Preset --------------------------------------------------------------------------------

    [Fact]
    public async Task ApplyConfig_RoundTrip_WithRangeOverlay()
    {
        var (svc, _, _) = await BuildAsync(ensureSchema: false);
        var baseSnap = DefaultSnapshot(svc);
        var firstKey = baseSnap.Ranges[0].Key;
        var saved = baseSnap with
        {
            Timeframe = "4h",
            SearchStrategy = SearchStrategy.Bayesian,
            BayesIterations = 99,
            Ranges = [baseSnap.Ranges[0] with { Min = 5m, Max = 50m, Step = 5m }, .. baseSnap.Ranges.Skip(1)],
        };

        var applied = svc.ApplyConfig(svc.SerializeConfig(saved), DefaultSnapshot(svc) with { Timeframe = "1h" });

        Assert.Equal("4h", applied.Timeframe);
        Assert.Equal(SearchStrategy.Bayesian, applied.SearchStrategy);
        Assert.Equal(99, applied.BayesIterations);
        var overlaid = applied.Ranges.First(r => r.Key == firstKey);
        Assert.Equal((5m, 50m, 5m), (overlaid.Min, overlaid.Max, overlaid.Step));
    }

    [Fact]
    public async Task ApplyConfig_InvalidStrategy_KeepsCurrent_AndZeroesMlModelForNonMl()
    {
        var (svc, _, _) = await BuildAsync(ensureSchema: false);
        var current = DefaultSnapshot(svc) with { StrategyName = "EmaCross" };

        Assert.Same(current, svc.ApplyConfig("{ rotto", current));

        var raw = svc.SerializeConfig(DefaultSnapshot(svc) with { StrategyName = "NonEsiste", MlModelId = 7 });
        var applied = svc.ApplyConfig(raw, current);
        Assert.Equal("EmaCross", applied.StrategyName);   // strategia non a catalogo → resta la corrente
        Assert.Equal(0, applied.MlModelId);               // il modello ML si azzera se la strategia finale non è "Ml"
    }

    [Fact]
    public async Task ApplyConfig_MlStrategy_KeepsModelId_AndThresholdRanges()
    {
        var (svc, _, _) = await BuildAsync(ensureSchema: false);
        var raw = svc.SerializeConfig(DefaultSnapshot(svc, OptimizationPageService.MlStrategyName) with { MlModelId = 15 });

        var applied = svc.ApplyConfig(raw, DefaultSnapshot(svc));

        Assert.Equal(OptimizationPageService.MlStrategyName, applied.StrategyName);
        Assert.Equal(15, applied.MlModelId);
        Assert.Equal(["LongThreshold", "ShortThreshold"], applied.Ranges.Select(r => r.Key));
    }

    // --- Handoff -------------------------------------------------------------------------------

    [Fact]
    public async Task ApplyHandoff_FromBacktest_RecentersRangesOnParameters()
    {
        var (svc, _, _) = await BuildAsync(ensureSchema: false);
        var defs = svc.DefaultRangesFor("EmaCross");
        var intKey = defs.First(r => r.IsInteger).Key;
        var q = new OptimizationHandoffQuery(
            "Binance", "ETH/USDT", "4h", "EmaCross", "2024-02-01", "2024-08-01",
            $"{{\"{intKey}\": 20}}", ModelId: null);

        var (snap, message) = svc.ApplyHandoff(q, DefaultSnapshot(svc));

        Assert.Equal("ETH/USDT", snap.Symbol);
        Assert.NotNull(message);
        Assert.Contains("dal Backtest", message);
        var recentered = snap.Ranges.First(r => r.Key == intKey);
        Assert.Equal(20m, recentered.Min);                    // min = valore del run
        Assert.Equal(4m, recentered.Step);                    // step intero = max(1, round(20 * 0.2)) = 4
        Assert.Equal(20m + 4 * 4m, recentered.Max);           // max = valore + 4 step
    }

    [Fact]
    public async Task ApplyHandoff_FromMlLab_SelectsModelAndThresholds()
    {
        var (svc, _, _) = await BuildAsync(ensureSchema: false);
        var q = new OptimizationHandoffQuery(
            null, "BTC/USDT", "1h", OptimizationPageService.MlStrategyName, "2026-03-01", "2026-07-18", null, ModelId: 15);

        var (snap, message) = svc.ApplyHandoff(q, DefaultSnapshot(svc));

        Assert.Equal(OptimizationPageService.MlStrategyName, snap.StrategyName);
        Assert.Equal(15, snap.MlModelId);
        Assert.Equal(["LongThreshold", "ShortThreshold"], snap.Ranges.Select(r => r.Key));
        Assert.NotNull(message);
        Assert.Contains("dal ML Lab", message);
    }

    [Fact]
    public async Task ApplyHandoff_NoContext_NoMessage_MalformedParameters_DefaultRanges()
    {
        var (svc, _, _) = await BuildAsync(ensureSchema: false);
        var (_, noMessage) = svc.ApplyHandoff(new OptimizationHandoffQuery(null, null, null, null, null, null, null, null), DefaultSnapshot(svc));
        Assert.Null(noMessage);

        var (snap, _) = svc.ApplyHandoff(
            new OptimizationHandoffQuery(null, "BTC/USDT", null, "EmaCross", null, null, "{ rotto", null), DefaultSnapshot(svc));
        Assert.Equal(svc.DefaultRangesFor("EmaCross"), snap.Ranges);   // JSON rotto → default intatti
    }

    // --- Run -----------------------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_MlWithoutModel_ReturnsError_WithoutInvokingEngine()
    {
        var (svc, engine, _) = await BuildAsync();
        var res = await svc.RunAsync(DefaultSnapshot(svc, OptimizationPageService.MlStrategyName) with { MlModelId = 0 }, UserA, null, CancellationToken.None);
        Assert.True(res.IsError);
        Assert.Contains("modello ML", res.Message);
        Assert.Null(engine.LastConfig);
        Assert.Null(svc.Result);
    }

    [Fact]
    public async Task RunAsync_HappyPath_BuildsConfigAndPopulatesState()
    {
        var (svc, engine, _) = await BuildAsync();
        var snap = DefaultSnapshot(svc) with { SearchStrategy = SearchStrategy.Bayesian, BayesSeed = 7 };

        var res = await svc.RunAsync(snap, UserA, null, CancellationToken.None);

        Assert.False(res.IsError);
        Assert.Contains("2 finestre", res.Message);
        var cfg = engine.LastConfig!;
        Assert.Equal("EmaCross", cfg.StrategyName);
        Assert.Equal(SearchStrategy.Bayesian, cfg.SearchStrategy);
        Assert.Equal(7, cfg.BayesianSeed);
        Assert.Equal(12, cfg.WalkForward.InSampleMonths);
        Assert.Equal(snap.Ranges.Count, cfg.ParameterRanges.Count);   // nessun range pinnato per le strategie a regole
        Assert.NotNull(svc.Result);
        Assert.NotNull(svc.ResultConfig);
        var series = Assert.Single(svc.EquitySeries);
        Assert.Equal(2, series.Points.Count);                          // dalla CombinedEquityCurve
    }

    [Fact]
    public async Task RunAsync_MlStrategy_AppendsPinnedSavedModelIdRange()
    {
        var (svc, engine, _) = await BuildAsync();
        var snap = DefaultSnapshot(svc, OptimizationPageService.MlStrategyName) with { MlModelId = 15 };

        await svc.RunAsync(snap, UserA, null, CancellationToken.None);

        var pinned = engine.LastConfig!.ParameterRanges.Single(r => r.Name == "SavedModelId");
        Assert.Equal(15m, pinned.Min);
        Assert.Equal(15m, pinned.Max);      // Min=Max: veicola il riferimento senza sweepparlo
        Assert.True(pinned.IsInteger);
    }

    // --- Heatmap -------------------------------------------------------------------------------

    [Fact]
    public async Task BuildHeatmapMatrix_ParsesGrid_WithNullForUnvisitedCombos()
    {
        var (svc, _, _) = await BuildAsync();
        Assert.Null(svc.BuildHeatmapMatrix("FastPeriod", "SlowPeriod"));   // nessun run

        await svc.RunAsync(DefaultSnapshot(svc), UserA, null, CancellationToken.None);
        var m = svc.BuildHeatmapMatrix("FastPeriod", "SlowPeriod")!;

        Assert.Equal(["10", "12"], m.Xs);
        Assert.Equal(["30", "40"], m.Ys);
        Assert.Equal(0.9, m.Z[0][0]);      // (10,30)
        Assert.Equal(-0.2, m.Z[0][1]);     // (12,30)
        Assert.Equal(0.5, m.Z[1][0]);      // (10,40)
        Assert.Null(m.Z[1][1]);            // (12,40) mai valutata → buco (tipico del Bayesian)
    }

    // --- Salvataggio best ----------------------------------------------------------------------

    [Fact]
    public async Task SaveBestAsync_NoRun_ReturnsNull_BlankName_Error_HappyPath_PersistsOptimized()
    {
        var (svc, _, db) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.Users.Add(new ApplicationUser { Id = UserA, UserName = "a@t.io" });
            await ctx.SaveChangesAsync();
        }

        Assert.Null(await svc.SaveBestAsync("nome", "EmaCross", UserA));   // nessun run: silenzioso

        await svc.RunAsync(DefaultSnapshot(svc), UserA, null, CancellationToken.None);
        var blank = await svc.SaveBestAsync("  ", "EmaCross", UserA);
        Assert.True(blank!.IsError);

        var ok = await svc.SaveBestAsync("Ema ottimizzata", "EmaCross", UserA);
        Assert.False(ok!.IsError);
        Assert.Contains("0.90", ok.Message.Replace(',', '.'));   // Sharpe OOS del best nel messaggio

        await using var verify = await db.CreateDbContextAsync();
        var saved = await verify.SavedStrategies.SingleAsync();
        Assert.True(saved.IsOptimized);
        Assert.Equal(0.9m, saved.OptimizationSharpe);
        Assert.Contains("FastPeriod", saved.ParametersJson);
    }

    // --- Handoff URL verso il Backtest ---------------------------------------------------------

    [Fact]
    public async Task BacktestHandoffUrl_FallsBackWithoutRun_FullUrlAfterRun()
    {
        var (svc, _, _) = await BuildAsync();
        Assert.Equal("backtest", svc.BacktestHandoffUrl(new Dictionary<string, decimal> { ["X"] = 1m }));

        await svc.RunAsync(DefaultSnapshot(svc), UserA, null, CancellationToken.None);
        var url = svc.BacktestHandoffUrl(new Dictionary<string, decimal> { ["FastPeriod"] = 10m });

        Assert.StartsWith("backtest?", url);
        Assert.Contains("symbol=TEST%2FUSDT", url);
        Assert.Contains("strategy=EmaCross", url);
        Assert.Contains("FastPeriod", Uri.UnescapeDataString(url));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
