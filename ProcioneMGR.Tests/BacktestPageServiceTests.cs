using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Experiments;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'orchestrazione estratta da <c>Backtest.razor</c> (P1-5, PRD-CONSOLIDAMENTO-ARCHITETTURA.md
/// §3.3): prima di questa estrazione tutta la logica — validazione, run del backtest con analitiche
/// derivate, suggerimento SL/TP, handoff dall'Optimization, preset validati e CRUD delle strategie
/// salvate — viveva nel blocco <c>@code</c> del componente, senza test indipendenti da Blazor. Qui è
/// esercitata direttamente su <see cref="BacktestPageService"/> con le dipendenze reali (motore di
/// backtest, StrategyFactory, analitiche di rischio, Postgres effimero) e un tracker no-op.
/// </summary>
[Collection("Postgres")]
public sealed class BacktestPageServiceTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public BacktestPageServiceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private const string UserA = "user-a";
    private const string UserB = "user-b";

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

    // --- Setup ---------------------------------------------------------------------------------

    private async Task<(BacktestPageService Svc, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync(bool ensureSchema = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();
        services.AddSingleton<IAlphaFactorFactory, AlphaFactorFactory>();
        services.AddSingleton<IStrategyFactory, StrategyFactory>();
        services.AddScoped<IBacktestEngine>(sp => new BacktestEngine(
            sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            sp.GetRequiredService<IStrategyFactory>(),
            sp.GetRequiredService<ITechnicalIndicatorsService>(),
            sp.GetRequiredService<IAlphaFactorFactory>(),
            sp.GetRequiredService<ILogger<BacktestEngine>>()));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        if (ensureSchema)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
        }

        var svc = new BacktestPageService(
            dbFactory,
            _provider.GetRequiredService<IBacktestEngine>(),
            _provider.GetRequiredService<IStrategyFactory>(),
            new NoopTracker(),
            new MonteCarloAnalyzer(),
            new PerformanceControlService(),
            new KellyCalculator(),
            new LeverageAdvisor(),
            new ExcursionAnalyzer());
        return (svc, dbFactory);
    }

    private static BacktestConfigSnapshot DefaultSnapshot(string symbol = "TEST/USDT", string timeframe = "1h") => new(
        ExchangeName.Binance, symbol, timeframe,
        new DateTime(2023, 12, 1), new DateTime(2024, 6, 1),
        10_000m, 20m, 0.1m,
        0m, 0m, 0m, 1m, 0m, 0m,
        "EmaCross", new Dictionary<string, decimal>());

    private static List<decimal> SyntheticMomentumCloses(int n, int seed)
    {
        var rnd = new Random(seed);
        var closes = new List<decimal> { 100m };
        for (var i = 1; i < n; i++)
        {
            var prevRet = i >= 2 ? (double)(closes[i - 1] / closes[i - 2] - 1m) : 0.0;
            var next = (double)closes[i - 1] * (1.0 + 0.5 * prevRet + (rnd.NextDouble() - 0.5) * 0.01);
            closes.Add((decimal)Math.Max(1.0, next));
        }
        return closes;
    }

    private async Task SeedCandlesAsync(IDbContextFactory<ApplicationDbContext> dbFactory, int count, string symbol = "TEST/USDT", string timeframe = "1h")
    {
        var closes = SyntheticMomentumCloses(count, seed: 7);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var list = new List<OhlcvData>(count);
        for (var i = 0; i < count; i++)
        {
            var c = closes[i];
            var prev = i > 0 ? closes[i - 1] : c;
            list.Add(new OhlcvData
            {
                Symbol = symbol, Timeframe = timeframe, TimestampUtc = t0.AddHours(i),
                Open = prev, High = Math.Max(prev, c) * 1.01m, Low = Math.Min(prev, c) * 0.99m, Close = c, Volume = 100m,
            });
        }
        await using var db = await dbFactory.CreateDbContextAsync();
        db.OhlcvData.AddRange(list);
        await db.SaveChangesAsync();
    }

    private static async Task SeedUserAsync(IDbContextFactory<ApplicationDbContext> dbFactory, string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = userId + "@t.io", Email = userId + "@t.io" });
            await db.SaveChangesAsync();
        }
    }

    // --- Catalogo strategie --------------------------------------------------------------------

    [Fact]
    public async Task ParameterDefinitionsFor_UnknownStrategy_FallsBackToFirstPrototype()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var known = svc.ParameterDefinitionsFor("EmaCross");
        var fallback = svc.ParameterDefinitionsFor("StrategiaInventata");
        Assert.NotEmpty(known);
        Assert.NotEmpty(fallback); // primo prototipo del catalogo, mai vuoto
    }

    // --- Preset --------------------------------------------------------------------------------

    [Fact]
    public async Task ApplyConfig_RoundTrip_AppliesSerializedValues_WithParameterOverlay()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var emaDefs = svc.ParameterDefinitionsFor("EmaCross");
        var firstKey = emaDefs[0].Key;
        var saved = DefaultSnapshot() with
        {
            Timeframe = "4h",
            StrategyName = "EmaCross",
            Parameters = new Dictionary<string, decimal> { [firstKey] = 99m, ["ChiaveInesistente"] = 1m },
            Leverage = 3m,
            StopLossPercent = 2.5m,
        };

        var applied = svc.ApplyConfig(svc.SerializeConfig(saved), DefaultSnapshot() with { Timeframe = "1h" });

        Assert.Equal("4h", applied.Timeframe);
        Assert.Equal(3m, applied.Leverage);
        Assert.Equal(2.5m, applied.StopLossPercent);
        Assert.Equal(99m, applied.Parameters[firstKey]);                       // overlay applicato
        Assert.False(applied.Parameters.ContainsKey("ChiaveInesistente"));     // chiave sconosciuta scartata
        Assert.Equal(emaDefs.Count, applied.Parameters.Count);                 // tutti i default presenti
    }

    [Fact]
    public async Task ApplyConfig_InvalidStrategyAndTimeframe_KeepsCurrent_MalformedJson_Unchanged()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var current = DefaultSnapshot() with { Timeframe = "1h", StrategyName = "EmaCross" };

        Assert.Same(current, svc.ApplyConfig("{ not json", current));

        var raw = svc.SerializeConfig(DefaultSnapshot() with { StrategyName = "Fasulla", Timeframe = "77x" });
        var applied = svc.ApplyConfig(raw, current);
        Assert.Equal("EmaCross", applied.StrategyName);   // strategia non valida → resta la corrente
        Assert.Equal("1h", applied.Timeframe);            // timeframe non supportato → resta il corrente
    }

    // --- Handoff dall'Optimization -------------------------------------------------------------

    [Fact]
    public async Task ApplyHandoff_FullContext_AppliesAndReturnsMessage()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var defs = svc.ParameterDefinitionsFor("RsiOversold");
        var firstKey = defs[0].Key;
        var q = new BacktestHandoffQuery(
            "Binance", "ETH/USDT", "4h", "RsiOversold",
            "2024-02-01", "2024-03-01",
            $"{{\"{firstKey}\": 42}}");

        var (snap, message) = svc.ApplyHandoff(q, DefaultSnapshot());

        Assert.Equal("ETH/USDT", snap.Symbol);
        Assert.Equal("4h", snap.Timeframe);
        Assert.Equal("RsiOversold", snap.StrategyName);
        Assert.Equal(new DateTime(2024, 2, 1), snap.From);
        Assert.Equal(42m, snap.Parameters[firstKey]);
        Assert.NotNull(message);
        Assert.Contains("ETH/USDT", message);
    }

    [Fact]
    public async Task ApplyHandoff_NoContext_NoMessage_ParametersAreDefaults()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var q = new BacktestHandoffQuery(null, null, null, null, null, null, null);

        var (snap, message) = svc.ApplyHandoff(q, DefaultSnapshot());

        Assert.Null(message);
        Assert.Equal("EmaCross", snap.StrategyName);
        var defaults = svc.ParameterDefinitionsFor("EmaCross").ToDictionary(d => d.Key, d => d.Default);
        Assert.Equal(defaults, snap.Parameters);
    }

    [Fact]
    public async Task ApplyHandoff_InvalidStrategy_MalformedParameters_KeepsCurrentWithDefaults()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var q = new BacktestHandoffQuery(null, "BTC/USDT", null, "NonEsiste", null, null, "{ rotto");

        var (snap, message) = svc.ApplyHandoff(q, DefaultSnapshot());

        Assert.Equal("EmaCross", snap.StrategyName);   // strategia sconosciuta ignorata
        Assert.NotNull(message);                        // il symbol c'era: il messaggio arriva comunque
        var defaults = svc.ParameterDefinitionsFor("EmaCross").ToDictionary(d => d.Key, d => d.Default);
        Assert.Equal(defaults, snap.Parameters);        // JSON rotto → solo default
    }

    [Fact]
    public async Task OptimizationHandoffUrl_ContainsContext()
    {
        var url = BacktestPageService.OptimizationHandoffUrl(DefaultSnapshot() with { Symbol = "BTC/USDT", Timeframe = "1h", StrategyName = "EmaCross" });
        Assert.StartsWith("optimization?", url);
        Assert.Contains("symbol=BTC%2FUSDT", url);
        Assert.Contains("strategy=EmaCross", url);
        Assert.Contains("from=2023-12-01", url);
    }

    // --- Run: guardie ed esecuzione ------------------------------------------------------------

    [Fact]
    public async Task RunAsync_BlankSymbolOrBadRange_ReturnsError()
    {
        var (svc, _) = await BuildAsync();
        var res1 = await svc.RunAsync(DefaultSnapshot() with { Symbol = " " }, CancellationToken.None);
        var res2 = await svc.RunAsync(DefaultSnapshot() with { From = new DateTime(2024, 6, 1), To = new DateTime(2024, 1, 1) }, CancellationToken.None);
        Assert.True(res1.IsError);
        Assert.True(res2.IsError);
        Assert.Null(svc.Result);
    }

    [Fact]
    public async Task RunAsync_NoCandlesInRange_ReturnsFetchError()
    {
        var (svc, _) = await BuildAsync();   // DB vuoto

        var res = await svc.RunAsync(DefaultSnapshot(), CancellationToken.None);

        Assert.True(res.IsError);
        Assert.Contains("Nessuna candela", res.Message);
        Assert.NotNull(svc.Result);          // il risultato (vuoto) resta visibile, come nell'originale
    }

    [Fact]
    public async Task RunAsync_HappyPath_PopulatesResultReportAndAnalytics()
    {
        var (svc, db) = await BuildAsync();
        await SeedCandlesAsync(db, 600);

        var res = await svc.RunAsync(DefaultSnapshot(), CancellationToken.None);

        Assert.False(res.IsError);
        Assert.Contains("completato", res.Message);
        Assert.NotNull(svc.Result);
        Assert.Equal(600, svc.Result!.CandlesEvaluated);
        Assert.NotEmpty(svc.EquitySeries);
        Assert.NotNull(svc.TradeReport);
        Assert.NotNull(svc.Kelly);
        Assert.True(svc.Result.Trades.Count > 0, "la serie sintetica con EmaCross default deve produrre trade");

        // Analisi di rischio sul run corrente: popolate on-demand.
        Assert.Null(svc.McResult);
        svc.RunMonteCarlo(shuffles: 50, noisePercent: 0m);
        Assert.NotNull(svc.McResult);
        svc.RunPerformanceControl(windowSize: 10, threshold: 0m);
        Assert.NotNull(svc.PcResult);
    }

    [Fact]
    public async Task RunMonteCarloAndPerformanceControl_WithoutRun_AreNoOps()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        svc.RunMonteCarlo(500, 0m);
        svc.RunPerformanceControl(10, 0m);
        Assert.Null(svc.McResult);
        Assert.Null(svc.PcResult);
    }

    // --- Suggerimento SL/TP --------------------------------------------------------------------

    [Fact]
    public async Task SuggestBracket_InsufficientData_ReturnsError()
    {
        var (svc, db) = await BuildAsync();
        await SeedCandlesAsync(db, 50); // < 100

        var res = await svc.SuggestBracketAsync(DefaultSnapshot());

        Assert.True(res.IsError);
        Assert.Contains("insufficienti", res.Message);
        Assert.Null(res.StopLossPercent);
    }

    [Fact]
    public async Task SuggestBracket_HappyPath_ReturnsPositiveLevels()
    {
        var (svc, db) = await BuildAsync();
        await SeedCandlesAsync(db, 600);

        var res = await svc.SuggestBracketAsync(DefaultSnapshot());

        Assert.False(res.IsError);
        Assert.True(res.StopLossPercent > 0m);
        Assert.True(res.TakeProfitPercent > 0m);
    }

    // --- Strategie salvate ---------------------------------------------------------------------

    [Fact]
    public async Task SaveStrategy_BlankName_Error_ThenRoundTripWithUserIsolation()
    {
        var (svc, db) = await BuildAsync();
        await SeedUserAsync(db, UserA);
        var parameters = svc.ParameterDefinitionsFor("EmaCross").ToDictionary(d => d.Key, d => d.Default);
        parameters[parameters.Keys.First()] = 77m;

        var blank = await svc.SaveStrategyAsync("  ", "EmaCross", parameters, UserA);
        Assert.True(blank.IsError);

        var save = await svc.SaveStrategyAsync("La mia EmaCross", "EmaCross", parameters, UserA);
        Assert.False(save.IsError);

        int savedId;
        await using (var ctx = await db.CreateDbContextAsync())
        {
            savedId = (await ctx.SavedStrategies.SingleAsync()).Id;
        }

        Assert.Null(await svc.LoadSavedStrategyAsync(savedId, UserB));     // isolamento per utente

        var loaded = await svc.LoadSavedStrategyAsync(savedId, UserA);
        Assert.NotNull(loaded);
        Assert.Equal("La mia EmaCross", loaded!.Name);
        Assert.Equal("EmaCross", loaded.StrategyName);
        Assert.Equal(77m, loaded.Parameters[parameters.Keys.First()]);     // overlay dai valori salvati
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
