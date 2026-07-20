using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Discovery;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [R2] REGRESSIONE su un'asimmetria trovata preparando l'ingestione a 1m.
///
/// Il percorso di SELEZIONE (Optimization, e a cascata Discovery) costruiva i backtest senza mai
/// impostare <c>SlippagePercent</c>: i parametri e i candidati venivano scelti a sole commissioni,
/// mentre la successiva validazione holdout della pipeline applicava i costi pieni.
///
/// Non era solo un errore di contabilità, era un errore di SELEZIONE: ottimizzando senza attrito si
/// premiano i parametri ad alto turnover, il cui vantaggio apparente è esattamente il costo che non
/// si sta pagando. Sui timeframe lenti l'ottimismo è modesto; a 1m lo slippage pesa quanto la
/// commissione, e la classifica dei candidati si riempirebbe di strategie che perdono denaro prima
/// ancora che il gate onesto le veda.
/// </summary>
[Collection("Postgres")]
public sealed class CostPropagationTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public CostPropagationTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>Registra ogni configurazione ricevuta: è la spia che dimostra cosa arriva davvero al backtest.</summary>
    private sealed class RecordingBacktest : IBacktestEngine
    {
        public ConcurrentBag<BacktestConfiguration> Seen { get; } = [];

        private BacktestResult Eval(BacktestConfiguration cfg)
        {
            Seen.Add(cfg);
            var eq = new List<EquityPoint>();
            var cap = cfg.InitialCapital;
            var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            eq.Add(new EquityPoint { Timestamp = t0, Capital = cap });
            for (var k = 1; k <= 24; k++)
            {
                cap *= 1m + (k % 2 == 0 ? 0.002m : -0.001m);
                eq.Add(new EquityPoint { Timestamp = t0.AddHours(k), Capital = cap });
            }
            return new BacktestResult
            {
                EquityCurve = eq,
                TotalReturnPercent = (cap - cfg.InitialCapital) / cfg.InitialCapital * 100m,
                TotalTrades = 10,
                Trades = [],
            };
        }

        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration c, CancellationToken ct) => Task.FromResult(Eval(c));
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration c, IReadOnlyList<OhlcvData> candles, CancellationToken ct) => Task.FromResult(Eval(c));
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration c, IReadOnlyList<OhlcvData> candles, IStrategy s, CancellationToken ct) => Task.FromResult(Eval(c));
    }

    /// <summary>Spia sull'ottimizzatore: serve a vedere cosa Discovery gli passa.</summary>
    private sealed class RecordingOptimizer : IOptimizationEngine
    {
        public ConcurrentBag<OptimizationConfiguration> Seen { get; } = [];

        public Task<OptimizationResult> OptimizeAsync(
            OptimizationConfiguration config, IProgress<OptimizationProgress>? progress, CancellationToken ct)
        {
            Seen.Add(config);
            return Task.FromResult(new OptimizationResult());
        }
    }

    private async Task<OptimizationEngine> BuildOptimizerAsync(RecordingBacktest backtest)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
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

        return new OptimizationEngine(backtest, factory, NullLogger<OptimizationEngine>.Instance);
    }

    private static OptimizationConfiguration OptConfig(decimal? slippage = null)
    {
        var cfg = new OptimizationConfiguration
        {
            ExchangeName = "Binance",
            Symbol = "OPT/USDT",
            Timeframe = "1h",
            From = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            StrategyName = "EmaCross",
            ParameterRanges = [new ParameterRange { Name = "X", Min = 1m, Max = 3m, Step = 1m, IsInteger = true }],
            WalkForward = new WalkForwardConfiguration { InSampleMonths = 6, OutOfSampleMonths = 3, StepMonths = 3 },
        };
        if (slippage is decimal s) cfg.SlippagePercent = s;
        return cfg;
    }

    [Fact]
    public void OptimizationConfiguration_DefaultsToHonestSlippage_NotZero()
    {
        // Il default deve essere l'attrito realistico, non zero: chi vuole il vecchio comportamento
        // ottimista deve chiederlo esplicitamente, non ottenerlo per dimenticanza.
        Assert.Equal(PipelineCosts.DefaultSlippagePercent, new OptimizationConfiguration().SlippagePercent);
        Assert.Equal(PipelineCosts.DefaultSlippagePercent, new StrategyDiscoveryConfiguration().SlippagePercent);
        Assert.True(PipelineCosts.DefaultSlippagePercent > 0m);
    }

    [Fact]
    public async Task Optimizer_PropagatesSlippage_ToEveryBacktest()
    {
        var backtest = new RecordingBacktest();
        var engine = await BuildOptimizerAsync(backtest);

        await engine.OptimizeAsync(OptConfig(slippage: 0.07m), null, CancellationToken.None);

        Assert.NotEmpty(backtest.Seen);
        // OGNI valutazione, non solo la prima: basterebbe una finestra senza attrito per inquinare
        // la selezione con parametri che sembrano migliori solo perché non pagano.
        Assert.All(backtest.Seen, cfg => Assert.Equal(0.07m, cfg.SlippagePercent));
    }

    [Fact]
    public async Task Optimizer_WithHonestDefault_NeverBacktestsWithoutFriction()
    {
        var backtest = new RecordingBacktest();
        var engine = await BuildOptimizerAsync(backtest);

        await engine.OptimizeAsync(OptConfig(), null, CancellationToken.None);   // nessuno slippage esplicito

        Assert.NotEmpty(backtest.Seen);
        Assert.All(backtest.Seen, cfg => Assert.True(cfg.SlippagePercent > 0m,
            "senza attrito la selezione premia il turnover che non paga: è il bug che questo test blocca"));
    }

    [Fact]
    public async Task Discovery_PropagatesCosts_ToTheOptimizer()
    {
        var optimizer = new RecordingOptimizer();
        var discovery = new StrategyDiscoveryEngine(
            optimizer, new StrategyFactory(), NullLogger<StrategyDiscoveryEngine>.Instance);

        await discovery.DiscoverAsync(new StrategyDiscoveryConfiguration
        {
            ExchangeName = "Binance",
            Symbols = ["BTC/USDT"],
            Timeframes = ["1m"],
            Strategies = ["EmaCross", "RsiOversold"],
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            CommissionPercent = 0.06m,
            SlippagePercent = 0.09m,
        }, null, CancellationToken.None);

        Assert.NotEmpty(optimizer.Seen);
        Assert.All(optimizer.Seen, cfg =>
        {
            Assert.Equal(0.09m, cfg.SlippagePercent);
            Assert.Equal(0.06m, cfg.CommissionPercent);
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
