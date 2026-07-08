using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Security;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test della leaderboard robusta di <see cref="OptimizationEngine"/> (fix "window coverage").
/// Contesto: la top-10 aggrega per combo su TUTTE le finestre in cui è stata valutata (medie). Con
/// la ricerca Bayesian ogni finestra campiona combo diverse, quindi una combo valutata in 1 sola
/// finestra "fortunata" ottiene medie non rappresentative e — ordinando per solo Sharpe OOS medio —
/// scavalcherebbe combo valutate ovunque. Il fix: espone la copertura e ordina/salva per uno score
/// scontato per copertura (<see cref="ParameterSet.RobustnessScore"/>).
///
/// Il tranello di copertura parziale qui è riprodotto in modo DETERMINISTICO anche con GridSearch:
/// il backtest fittizio lancia (combo "invalida") per X=9 in tutte le finestre tranne l'ultima, così
/// X=9 ottiene copertura 1 e il suo Sharpe OOS grezzo è il più alto — la trappola pre-fix.
/// </summary>
public sealed class OptimizationRobustnessTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"opt_robust_{Guid.NewGuid():N}.db");
    private ServiceProvider? _provider;

    private static readonly DateTime LastWindowCutoff = new(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>
    /// Backtest deterministico. Sharpe monotòno nel rendimento medio per passo (varianza costante):
    ///  - X ≠ 9: picco di qualità a X=7 (media 0.010), copertura piena in tutte le finestre;
    ///  - X == 9: media 0.012 (Sharpe grezzo PIÙ ALTO di X=7) MA lancia per le finestre la cui
    ///    valutazione inizia prima di <see cref="LastWindowCutoff"/> ⇒ valutato in 1 sola finestra.
    /// </summary>
    private sealed class LuckyNineBacktest : IBacktestEngine
    {
        private static BacktestResult Eval(BacktestConfiguration cfg)
        {
            var x = (double)cfg.StrategyParameters.GetValueOrDefault("X", 0m);
            if (x == 9.0 && cfg.From < LastWindowCutoff)
            {
                // Combo "invalida" nelle finestre precedenti: EvaluateAsync la scarta ⇒ copertura ridotta.
                throw new InvalidOperationException("combo scartata in questa finestra (simulazione copertura parziale)");
            }

            var quality = x == 9.0 ? 1.2 : Math.Max(0.0, 1.0 - Math.Abs(x - 7.0) / 10.0);
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
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
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

        return new OptimizationEngine(new LuckyNineBacktest(), factory, NullLogger<OptimizationEngine>.Instance);
    }

    // Range 5..9 ⇒ 5 combo (tutte entrano nella top-10). 3 finestre walk-forward come nel test grid.
    private static OptimizationConfiguration BaseConfig() => new()
    {
        ExchangeName = "Binance", Symbol = "OPT/USDT", Timeframe = "1h",
        From = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        To = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        InitialCapital = 10_000m,
        StrategyName = "Dummy",
        ParameterRanges = [new ParameterRange { Name = "X", Min = 5m, Max = 9m, Step = 1m, IsInteger = true }],
        WalkForward = new WalkForwardConfiguration { InSampleMonths = 3, OutOfSampleMonths = 1, StepMonths = 3 },
    };

    [Fact]
    public async Task LowCoverageLuckyCombo_DoesNotTopLeaderboard_AndCoverageIsExposed()
    {
        var engine = await BuildAsync();

        var result = await engine.OptimizeAsync(BaseConfig(), null, CancellationToken.None);

        Assert.Equal(3, result.TotalWindows);

        var byX = result.BestParameters.ToDictionary(s => s.Parameters["X"]);
        var nine = byX[9m];
        var seven = byX[7m];

        // Copertura esposta: X=9 valutato in 1 sola finestra "fortunata", X=7 in tutte e 3.
        Assert.Equal(1, nine.WindowCoverage);
        Assert.Equal(3, seven.WindowCoverage);

        // La trappola pre-fix: X=9 ha lo Sharpe OOS medio GREZZO più alto (di tutte le combo).
        Assert.True(nine.OutOfSampleSharpe > seven.OutOfSampleSharpe);
        Assert.Equal(nine.OutOfSampleSharpe, result.BestParameters.Max(s => s.OutOfSampleSharpe));

        // Post-fix: lo score robusto sconta X=9 sotto X=7 ⇒ la #1 (e quindi "Save Best") è X=7, non X=9.
        Assert.True(nine.RobustnessScore < seven.RobustnessScore);
        Assert.Equal(7m, result.BestParameters[0].Parameters["X"]);

        // Formula dello sconto: copertura piena ⇒ nessuno sconto; copertura parziale ⇒ Sharpe × cov/tot.
        Assert.Equal(seven.OutOfSampleSharpe, seven.RobustnessScore, precision: 10);
        Assert.Equal(nine.OutOfSampleSharpe * 1 / 3, nine.RobustnessScore, precision: 10);

        // La leaderboard è ordinata (non crescente) per score robusto.
        for (var i = 1; i < result.BestParameters.Count; i++)
        {
            Assert.True(result.BestParameters[i - 1].RobustnessScore >= result.BestParameters[i].RobustnessScore);
        }
    }

    [Fact]
    public async Task GridSearch_FullCoverage_LeavesRobustScoreEqualToOosSharpe()
    {
        var engine = await BuildAsync();

        // Range senza la combo "fortunata": ogni combo è valutata in tutte le finestre (copertura piena).
        var config = BaseConfig();
        config.ParameterRanges = [new ParameterRange { Name = "X", Min = 5m, Max = 8m, Step = 1m, IsInteger = true }];

        var result = await engine.OptimizeAsync(config, null, CancellationToken.None);

        Assert.All(result.BestParameters, s =>
        {
            Assert.Equal(result.TotalWindows, s.WindowCoverage);   // piena
            Assert.Equal(s.OutOfSampleSharpe, s.RobustnessScore);  // nessuno sconto ⇒ ordine storico invariato
        });
        Assert.Equal(7m, result.BestParameters[0].Parameters["X"]);   // ottimo noto, invariato
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best-effort */ }
    }
}
