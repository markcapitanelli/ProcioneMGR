using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Security;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'aggancio Bayesian a <see cref="OptimizationEngine"/> (follow-up "Bayesian in
/// /optimization"). Verifica: (a) GridSearch default = comportamento storico (numero esatto di
/// valutazioni, verdetto DSR popolato, trova l'ottimo); (b) ramo Bayesian deterministico a parità
/// di seme; (c) Validation (Deflated Sharpe) popolato anche nel ramo Bayesian; (d) a parità di
/// budget il ramo Bayesian non valuta più del grid equivalente.
/// </summary>
[Collection("Postgres")]
public sealed class OptimizationSearchStrategyTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public OptimizationSearchStrategyTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>
    /// Backtest deterministico: la qualità (Sharpe) dipende SOLO dal parametro "X", con un picco a
    /// X=7. La curva di equity ha rendimento medio ∝ qualità e varianza costante, così lo Sharpe è
    /// monotòno nella qualità — l'ottimo è a X=7 sia per il grid sia per la ricerca guidata.
    /// </summary>
    private sealed class PeakAtSevenBacktest : IBacktestEngine
    {
        private static BacktestResult Eval(BacktestConfiguration cfg)
        {
            var x = (double)cfg.StrategyParameters.GetValueOrDefault("X", 0m);
            var quality = Math.Max(0.0, 1.0 - Math.Abs(x - 7.0) / 10.0);
            var eq = new List<EquityPoint>();
            var cap = cfg.InitialCapital;
            var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            eq.Add(new EquityPoint { Timestamp = t0, Capital = cap });
            for (var k = 1; k <= 24; k++)
            {
                var wiggle = k % 2 == 0 ? 0.001m : -0.001m;    // varianza costante > 0
                var r = (decimal)(quality * 0.01) + wiggle;
                cap *= 1m + r;
                eq.Add(new EquityPoint { Timestamp = t0.AddHours(k), Capital = cap });
            }
            return new BacktestResult
            {
                EquityCurve = eq,
                TotalReturnPercent = cfg.InitialCapital > 0m ? (cap - cfg.InitialCapital) / cfg.InitialCapital * 100m : 0m,
                MaxDrawdownPercent = 0m,
                TotalTrades = 10,
                Trades = new(),
            };
        }

        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, CancellationToken ct) => Task.FromResult(Eval(config));
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, CancellationToken ct) => Task.FromResult(Eval(config));
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, IStrategy strategy, CancellationToken ct) => Task.FromResult(Eval(config));
    }

    private async Task<OptimizationEngine> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            // Candele settimanali che coprono tutto il 2024: bastano a LoadCandlesAsync (il backtest è fittizio).
            var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var w = 0; w < 53; w++)
            {
                db.OhlcvData.Add(new OhlcvData
                {
                    Symbol = "OPT/USDT", Timeframe = "1h", TimestampUtc = t0.AddDays(w * 7),
                    Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 100m,
                });
            }
            await db.SaveChangesAsync();
        }

        return new OptimizationEngine(new PeakAtSevenBacktest(), factory, NullLogger<OptimizationEngine>.Instance);
    }

    private static OptimizationConfiguration BaseConfig() => new()
    {
        ExchangeName = "Binance", Symbol = "OPT/USDT", Timeframe = "1h",
        From = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        To = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        InitialCapital = 10_000m,
        StrategyName = "Dummy",
        ParameterRanges = [new ParameterRange { Name = "X", Min = 0m, Max = 10m, Step = 1m, IsInteger = true }],
        WalkForward = new WalkForwardConfiguration { InSampleMonths = 3, OutOfSampleMonths = 1, StepMonths = 3 },
    };

    [Fact]
    public async Task GridSearch_Default_TestsFullProduct_FindsOptimum_AndPopulatesValidation()
    {
        var engine = await BuildAsync();
        var config = BaseConfig();   // SearchStrategy = GridSearch (default)

        var result = await engine.OptimizeAsync(config, null, CancellationToken.None);

        // 11 valori (0..10) × 3 finestre walk-forward = 33 valutazioni esaustive.
        Assert.Equal(33, result.TotalCombinationsTested);
        Assert.Equal(7m, result.BestParameters[0].Parameters["X"]);   // ottimo noto
        Assert.NotNull(result.Validation);                            // verdetto DSR come prima
    }

    [Fact]
    public async Task Bayesian_IsDeterministic_ForSameSeed_AndPopulatesValidation()
    {
        var engine = await BuildAsync();
        var config = BaseConfig();
        config.SearchStrategy = SearchStrategy.Bayesian;
        config.BayesianInitialRandom = 4;
        config.BayesianIterations = 6;
        config.BayesianSeed = 123;

        var r1 = await engine.OptimizeAsync(config, null, CancellationToken.None);
        var r2 = await engine.OptimizeAsync(config, null, CancellationToken.None);

        Assert.Equal(r1.TotalCombinationsTested, r2.TotalCombinationsTested);
        Assert.Equal(r1.BestParameters[0].Parameters["X"], r2.BestParameters[0].Parameters["X"]);
        Assert.Equal(r1.WalkForwardAnalysis.AverageOutOfSampleSharpe, r2.WalkForwardAnalysis.AverageOutOfSampleSharpe);
        Assert.NotNull(r1.Validation);   // DSR calcolato una volta a fine ricerca anche nel ramo Bayesian
    }

    [Fact]
    public async Task Bayesian_EqualBudget_DoesNotEvaluateMoreThanGrid()
    {
        var engine = await BuildAsync();

        var grid = await engine.OptimizeAsync(BaseConfig(), null, CancellationToken.None);

        var bayesConfig = BaseConfig();
        bayesConfig.SearchStrategy = SearchStrategy.Bayesian;
        bayesConfig.BayesianInitialRandom = 4;
        bayesConfig.BayesianIterations = 6;   // budget = (4+6) × 3 finestre = 30 < 33 del grid
        var bayes = await engine.OptimizeAsync(bayesConfig, null, CancellationToken.None);

        Assert.Equal(30, bayes.TotalCombinationsTested);
        Assert.True(bayes.TotalCombinationsTested <= grid.TotalCombinationsTested);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
