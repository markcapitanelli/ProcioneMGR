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
/// [T1.6 roadmap macchina-ricerca] CPCV esteso al percorso strategie: da UN percorso out-of-sample
/// (walk-forward + holdout) a una DISTRIBUZIONE di Sharpe su C(gruppi, gruppiTest) percorsi.
///
/// Il test che conta: con un ottimo PIANTATO (qualità massima a X=7, coerente su tutti i gruppi),
/// il CPCV deve sceglierlo su ogni percorso (stabilità 100%) e la distribuzione OOS deve essere
/// tutta positiva. È l'esperimento di controllo in miniatura: se il meccanismo non trova un edge
/// costruito per esserci, non può dire nulla sui dati veri.
/// </summary>
[Collection("Postgres")]
public sealed class OptimizationCpcvTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public OptimizationCpcvTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>
    /// Qualità dipende SOLO da X con picco a 7, identica su ogni gruppo di candele: l'edge piantato
    /// coerente. L'equity parte dalla prima candela ricevuta, quindi ogni gruppo produce la sua curva.
    /// </summary>
    private sealed class PeakAtSevenBacktest : IBacktestEngine
    {
        private static BacktestResult Eval(BacktestConfiguration cfg, int bars)
        {
            var x = (double)cfg.StrategyParameters.GetValueOrDefault("X", 0m);
            var quality = 1.0 - Math.Abs(x - 7.0) / 10.0;   // picco a 7, può andare sotto zero
            var eq = new List<EquityPoint>();
            var cap = cfg.InitialCapital;
            var t0 = cfg.From;
            eq.Add(new EquityPoint { Timestamp = t0, Capital = cap });
            for (var k = 1; k <= Math.Max(bars, 8); k++)
            {
                var wiggle = k % 2 == 0 ? 0.001m : -0.001m;
                cap *= 1m + (decimal)(quality * 0.01) + wiggle;
                eq.Add(new EquityPoint { Timestamp = t0.AddHours(k), Capital = cap });
            }
            return new BacktestResult
            {
                EquityCurve = eq, TotalReturnPercent = 1m, MaxDrawdownPercent = 0m, TotalTrades = 10, Trades = new(),
            };
        }

        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, CancellationToken ct)
            => Task.FromResult(Eval(config, 8));
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, CancellationToken ct)
            => Task.FromResult(Eval(config, candles.Count));
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, IStrategy strategy, CancellationToken ct)
            => Task.FromResult(Eval(config, candles.Count));
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
            if (!await db.OhlcvData.AnyAsync())
            {
                var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                for (var i = 0; i < 400; i++)
                {
                    db.OhlcvData.Add(new OhlcvData
                    {
                        Symbol = "CPCV/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(i),
                        Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 100m,
                    });
                }
                await db.SaveChangesAsync();
            }
        }
        return new OptimizationEngine(new PeakAtSevenBacktest(), factory, NullLogger<OptimizationEngine>.Instance);
    }

    private static OptimizationConfiguration Config() => new()
    {
        ExchangeName = "Binance", Symbol = "CPCV/USDT", Timeframe = "1h",
        From = new DateTime(2024, 1, 1), To = new DateTime(2024, 2, 1),
        InitialCapital = 10_000m, StrategyName = "EmaCross",
        ParameterRanges = [new ParameterRange { Name = "X", Min = 1m, Max = 9m, Step = 1m, IsInteger = true }],
        WalkForward = new WalkForwardConfiguration(),
    };

    [Fact]
    public async Task PlantedOptimum_IsChosenOnEveryPath_AndTheWholeOosDistributionIsPositive()
    {
        var engine = await BuildAsync();

        var result = await engine.OptimizeCpcvAsync(Config(),
            new CpcvConfiguration { Groups = 8, TestGroups = 2 }, null, CancellationToken.None);

        Assert.Equal(28, result.TotalPaths);                       // C(8,2)
        Assert.Equal(28, result.Paths.Count);
        Assert.Equal(9, result.CombinationsTested);                // X = 1..9

        // L'edge piantato è coerente su tutti i gruppi: OGNI train deve scegliere X=7.
        Assert.All(result.Paths, p => Assert.Equal(7m, p.BestParameters["X"]));
        Assert.Equal(1m, result.SelectionStability);
        Assert.Equal(7m, result.ModalParameters["X"]);

        // E ogni percorso out-of-sample deve essere positivo: la distribuzione non ha code negative.
        Assert.Equal(28, result.PositivePaths);
        Assert.True(result.P05OosSharpe > 0m, $"P05 {result.P05OosSharpe} dovrebbe essere positivo con l'edge piantato");
        Assert.True(result.MedianOosSharpe > 0m);
    }

    [Fact]
    public async Task PurgeAndEmbargo_ReduceTheTrainGroups_ButPathsStillResolve()
    {
        var engine = await BuildAsync();

        // Bande larghe quanto un gruppo intero (400/8 = 50 barre): i gruppi adiacenti ai test
        // vengono mutilati e quindi scartati dal train — i percorsi devono comunque risolversi.
        var result = await engine.OptimizeCpcvAsync(Config(),
            new CpcvConfiguration { Groups = 8, TestGroups = 2, PurgeBars = 50, EmbargoBars = 50 },
            null, CancellationToken.None);

        Assert.True(result.Paths.Count > 0, "con purge/embargo larghi i percorsi devono comunque esistere");
        Assert.All(result.Paths, p => Assert.Equal(7m, p.BestParameters["X"]));
    }

    [Fact]
    public async Task SameInput_SameDistribution_Deterministic()
    {
        var engine = await BuildAsync();
        var cpcv = new CpcvConfiguration { Groups = 6, TestGroups = 2 };

        var a = await engine.OptimizeCpcvAsync(Config(), cpcv, null, CancellationToken.None);
        var b = await engine.OptimizeCpcvAsync(Config(), cpcv, null, CancellationToken.None);

        Assert.Equal(a.Paths.Select(p => p.OosSharpe), b.Paths.Select(p => p.OosSharpe));
        Assert.Equal(a.Pbo, b.Pbo);
    }

    [Fact]
    public async Task TooFewCandles_FailsLoudly_InsteadOfMeasuringNoise()
    {
        var engine = await BuildAsync();
        var cfg = Config();
        cfg.To = cfg.From.AddHours(100);   // ~100 candele per 8 gruppi: sotto la soglia minima

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.OptimizeCpcvAsync(cfg, new CpcvConfiguration { Groups = 8, TestGroups = 2 }, null, CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
