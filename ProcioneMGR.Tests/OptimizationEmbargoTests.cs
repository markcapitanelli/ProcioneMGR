using System.Collections.Concurrent;
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
/// [T0.1 roadmap macchina-ricerca] Test dell'embargo nel walk-forward dell'ottimizzatore.
///
/// Il difetto che l'embargo corregge: <c>GenerateWindows</c> produce finestre IS/OOS CONTIGUE
/// (<c>oosStart = isEnd</c>), quindi una posizione aperta a fine in-sample prosegue
/// nell'out-of-sample e un indicatore con lookback L vede fino a L barre di in-sample — la misura
/// "fuori campione" non lo è del tutto. La piattaforma possedeva già lo strumento giusto
/// (<c>PurgedTimeSeriesCv</c>) ma lo usava solo nel percorso ML.
///
/// Il test più importante è <see cref="DefaultZero_KeepsHistoricalContiguousBehaviour"/>: con
/// embargo 0 il comportamento resta bit-identico a prima — nessuno sweep esistente cambia
/// risultato per l'introduzione del campo.
/// </summary>
[Collection("Postgres")]
public sealed class OptimizationEmbargoTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public OptimizationEmbargoTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>
    /// Backtest fittizio che REGISTRA ogni chiamata (range e numero di candele ricevute): è il
    /// testimone di cosa l'ottimizzatore ha davvero passato al motore, IS e OOS.
    /// </summary>
    private sealed class RecordingBacktest : IBacktestEngine
    {
        public ConcurrentBag<(DateTime From, DateTime To, int Candles)> Calls { get; } = new();

        private BacktestResult Eval(BacktestConfiguration cfg, int candleCount)
        {
            Calls.Add((cfg.From, cfg.To, candleCount));
            var eq = new List<EquityPoint>();
            var cap = cfg.InitialCapital;
            var t0 = cfg.From == default ? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) : cfg.From;
            eq.Add(new EquityPoint { Timestamp = t0, Capital = cap });
            for (var k = 1; k <= 10; k++)
            {
                cap *= k % 2 == 0 ? 1.002m : 0.999m;   // varianza > 0, Sharpe calcolabile
                eq.Add(new EquityPoint { Timestamp = t0.AddHours(k), Capital = cap });
            }
            return new BacktestResult
            {
                EquityCurve = eq,
                TotalReturnPercent = 1m,
                MaxDrawdownPercent = 0m,
                TotalTrades = 10,
                Trades = new(),
            };
        }

        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, CancellationToken ct)
            => Task.FromResult(Eval(config, -1));
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, CancellationToken ct)
            => Task.FromResult(Eval(config, candles.Count));
        public Task<BacktestResult> RunBacktestAsync(BacktestConfiguration config, IReadOnlyList<OhlcvData> candles, IStrategy strategy, CancellationToken ct)
            => Task.FromResult(Eval(config, candles.Count));
    }

    private async Task<(OptimizationEngine Engine, RecordingBacktest Recorder)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            // Candele SETTIMANALI (timeframe dichiarato 1h, irrilevante per il fake): la spaziatura
            // larga rende contabile a mano quante barre entrano in ogni finestra. Semina solo al
            // primo Build: i test che costruiscono due motori condividono lo stesso DB di classe.
            if (!await db.OhlcvData.AnyAsync())
            {
                var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                for (var w = 0; w < 60; w++)
                {
                    db.OhlcvData.Add(new OhlcvData
                    {
                        Symbol = "EMB/USDT", Timeframe = "1h", TimestampUtc = t0.AddDays(w * 7),
                        Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 100m,
                    });
                }
                await db.SaveChangesAsync();
            }
        }

        var recorder = new RecordingBacktest();
        return (new OptimizationEngine(recorder, factory, NullLogger<OptimizationEngine>.Instance), recorder);
    }

    private static OptimizationConfiguration Config(int embargoBars) => new()
    {
        ExchangeName = "Binance",
        Symbol = "EMB/USDT",
        Timeframe = "1h",
        From = new DateTime(2024, 1, 1),
        To = new DateTime(2025, 1, 1),
        InitialCapital = 10_000m,
        StrategyName = "EmaCross",
        ParameterRanges = [new ParameterRange { Name = "X", Min = 1m, Max = 2m, Step = 1m, IsInteger = true }],
        WalkForward = new WalkForwardConfiguration
        {
            InSampleMonths = 6, OutOfSampleMonths = 3, StepMonths = 3, EmbargoBars = embargoBars,
        },
    };

    [Fact]
    public async Task DefaultZero_KeepsHistoricalContiguousBehaviour()
    {
        var (engine, _) = await BuildAsync();
        var result = await engine.OptimizeAsync(Config(embargoBars: 0), null, CancellationToken.None);

        Assert.NotEmpty(result.WalkForwardAnalysis.Windows);
        foreach (var w in result.WalkForwardAnalysis.Windows)
        {
            // Il comportamento storico: OOS inizia ESATTAMENTE dove finisce l'IS.
            Assert.Equal(w.InSampleEnd, w.OutOfSampleStart);
        }
    }

    [Fact]
    public async Task Embargo_TrimsExactlyThatManyBarsFromEachOosWindow()
    {
        const int embargo = 2;

        // Le chiamate OOS si identificano con la COPPIA (From, To) della finestra: il solo To è
        // ambiguo, perché l'in-sample della finestra successiva finisce dove finisce l'OOS
        // della precedente (IS finestra k+1: apr→ott; OOS finestra k: lug→ott).
        static Dictionary<DateTime, int> OosCandles(RecordingBacktest rec, OptimizationResult r) =>
            r.WalkForwardAnalysis.Windows.ToDictionary(
                w => w.OutOfSampleEnd,
                w => rec.Calls.Where(c => c.From == w.OutOfSampleStart && c.To == w.OutOfSampleEnd)
                              .Select(c => c.Candles).Distinct().Single());

        var (engine0, rec0) = await BuildAsync();
        var r0 = await engine0.OptimizeAsync(Config(0), null, CancellationToken.None);
        var oos0 = OosCandles(rec0, r0);

        var (engine1, rec1) = await BuildAsync();
        var r1 = await engine1.OptimizeAsync(Config(embargo), null, CancellationToken.None);
        var oos1 = OosCandles(rec1, r1);

        Assert.Equal(oos0.Keys.OrderBy(x => x), oos1.Keys.OrderBy(x => x));
        foreach (var key in oos0.Keys)
        {
            // Ogni OOS perde ESATTAMENTE 'embargo' barre, né una di più né una di meno.
            Assert.Equal(oos0[key] - embargo, oos1[key]);
        }

        // E il report riflette l'inizio EFFETTIVO, non la finestra nominale: la prima candela
        // dell'OOS senza embargo, più 2 barre settimanali. (Le candele NON sono allineate al bordo
        // finestra: la prima candela dell'OOS nominale può cadere giorni dopo l'inizio nominale.)
        static DateTime FirstWeeklyCandleAtOrAfter(DateTime t)
        {
            var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var days = (int)Math.Ceiling((t - t0).TotalDays / 7.0) * 7;
            return t0.AddDays(days);
        }

        var nominalStarts = r0.WalkForwardAnalysis.Windows.ToDictionary(w => w.OutOfSampleEnd, w => w.OutOfSampleStart);
        foreach (var w in r1.WalkForwardAnalysis.Windows)
        {
            var expectedStart = FirstWeeklyCandleAtOrAfter(nominalStarts[w.OutOfSampleEnd]).AddDays(7 * embargo);
            Assert.Equal(expectedStart, w.OutOfSampleStart);
        }
    }

    [Fact]
    public async Task EmbargoConsumingTheWholeOos_SkipsTheWindowInsteadOfMeasuringNoise()
    {
        // Un OOS di 3 mesi settimanali ha ~13 barre: un embargo di 1000 lo consuma per intero.
        // La finestra va SALTATA, non misurata su 1-2 barre residue: uno Sharpe su due punti
        // sarebbe rumore spacciato per misura.
        var (engine, rec) = await BuildAsync();
        var result = await engine.OptimizeAsync(Config(embargoBars: 1000), null, CancellationToken.None);

        Assert.Empty(result.WalkForwardAnalysis.Windows);
        Assert.Empty(rec.Calls);   // nessun backtest eseguito su finestre degeneri
    }

    [Fact]
    public async Task NegativeEmbargo_IsRejectedByValidation()
    {
        var (engine, _) = await BuildAsync();
        await Assert.ThrowsAsync<ArgumentException>(
            () => engine.OptimizeAsync(Config(embargoBars: -1), null, CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
