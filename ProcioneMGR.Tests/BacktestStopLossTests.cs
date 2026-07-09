using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'overlay stop loss / take profit / trailing stop del motore di backtest
/// (McAllen cap. 17: "lo stop loss E' parte del trade"). Candele sintetiche + strategia
/// script che entra long alla prima barra e non emette altro.
/// </summary>
[Collection("Postgres")]
public class BacktestStopLossTests
{
    private readonly PostgresFixture _pg;

    public BacktestStopLossTests(PostgresFixture pg) => _pg = pg;

    /// <summary>Strategia script: emette i segnali indicati agli indici indicati, Hold altrove.</summary>
    private sealed class ScriptedStrategy(Dictionary<int, Signal> script) : IStrategy
    {
        public string Name => "Scripted";
        public string DisplayName => "Scripted";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } = [];

        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct)
            => Task.CompletedTask;

        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
            => script.GetValueOrDefault(index, Signal.Hold);
    }

    private static List<OhlcvData> Candles(params (decimal Open, decimal High, decimal Low, decimal Close)[] bars)
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return bars.Select((b, i) => new OhlcvData
        {
            Symbol = "TEST",
            Timeframe = "1h",
            TimestampUtc = t0.AddHours(i),
            Open = b.Open,
            High = b.High,
            Low = b.Low,
            Close = b.Close,
            Volume = 100m,
        }).ToList();
    }

    private async Task<BacktestResult> RunAsync(
        List<OhlcvData> candles, Dictionary<int, Signal> script,
        decimal stopLoss = 0m, decimal takeProfit = 0m, decimal trailing = 0m)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_pg.CreateDatabase()));
        services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();
        services.AddSingleton<IStrategyFactory, StrategyFactory>();
        services.AddSingleton<IAlphaFactorFactory, AlphaFactorFactory>();
        services.AddScoped<IBacktestEngine, BacktestEngine>();
        await using var provider = services.BuildServiceProvider();

        var config = new BacktestConfiguration
        {
            Symbol = "TEST",
            Timeframe = "1h",
            InitialCapital = 10_000m,
            PositionSizePercent = 100m,
            FeePercent = 0m,
            StopLossPercent = stopLoss,
            TakeProfitPercent = takeProfit,
            TrailingStopPercent = trailing,
        };

        var engine = provider.GetRequiredService<IBacktestEngine>();
        return await engine.RunBacktestAsync(config, candles, new ScriptedStrategy(script), CancellationToken.None);
    }

    [Fact]
    public async Task NoStops_BehaviorUnchanged_PositionClosedAtEnd()
    {
        // Long a 100, il prezzo crolla, senza stop la posizione chiude solo a fine serie.
        var candles = Candles(
            (100m, 101m, 99m, 100m),   // 0: entra long alla close 100
            (100m, 100m, 90m, 92m),    // 1: crollo
            (92m, 93m, 85m, 86m));     // 2: chiusura forzata a fine serie (86)
        var result = await RunAsync(candles, new() { [0] = Signal.Long });

        var trade = Assert.Single(result.Trades);
        Assert.Equal(86m, trade.ExitPrice);
        Assert.True(trade.Pnl < 0m);
    }

    [Fact]
    public async Task StopLoss_Long_ExitsAtStopLevel()
    {
        // Long a 100 con SL 5%: stop a 95. La barra 1 tocca low 90 -> uscita a 95, non a 86.
        var candles = Candles(
            (100m, 101m, 99m, 100m),
            (100m, 100m, 90m, 92m),
            (92m, 93m, 85m, 86m));
        var result = await RunAsync(candles, new() { [0] = Signal.Long }, stopLoss: 5m);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(95m, trade.ExitPrice);
        Assert.Equal(candles[1].TimestampUtc, trade.ExitTime);
    }

    [Fact]
    public async Task StopLoss_GapBelowStop_FillsAtOpen()
    {
        // La barra 1 APRE gia' sotto lo stop (apertura 92 < stop 95): fill realistico all'open.
        var candles = Candles(
            (100m, 101m, 99m, 100m),
            (92m, 93m, 90m, 91m),
            (91m, 92m, 90m, 91m));
        var result = await RunAsync(candles, new() { [0] = Signal.Long }, stopLoss: 5m);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(92m, trade.ExitPrice);
    }

    [Fact]
    public async Task TakeProfit_Long_ExitsAtTarget()
    {
        // Long a 100 con TP 10%: target 110. La barra 2 tocca high 112 -> uscita a 110.
        var candles = Candles(
            (100m, 101m, 99m, 100m),
            (100m, 104m, 99m, 103m),
            (103m, 112m, 102m, 111m),
            (111m, 112m, 110m, 111m));
        var result = await RunAsync(candles, new() { [0] = Signal.Long }, takeProfit: 10m);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(110m, trade.ExitPrice);
    }

    [Fact]
    public async Task TrailingStop_Long_LocksInGains()
    {
        // Long a 100, trailing 5%. Il massimo sale a 120 -> trailing a 114; il calo a 110
        // fa scattare l'uscita a 114 (guadagno protetto), non a fine serie.
        var candles = Candles(
            (100m, 101m, 99m, 100m),
            (100m, 110m, 99m, 109m),   // best = 110, trail = 104.5
            (109m, 120m, 108m, 119m),  // best = 120, trail = 114
            (119m, 119m, 110m, 111m),  // low 110 <= 114 -> stop a 114
            (111m, 112m, 110m, 111m));
        var result = await RunAsync(candles, new() { [0] = Signal.Long }, trailing: 5m);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(114m, trade.ExitPrice);
        Assert.True(trade.Pnl > 0m);
    }

    [Fact]
    public async Task StopLoss_Short_ExitsAboveEntry()
    {
        // Short a 100 con SL 5%: stop a 105. La barra 1 tocca high 108 -> uscita a 105.
        var candles = Candles(
            (100m, 101m, 99m, 100m),
            (100m, 108m, 99m, 107m),
            (107m, 109m, 106m, 108m));
        var result = await RunAsync(candles, new() { [0] = Signal.Short }, stopLoss: 5m);

        var trade = Assert.Single(result.Trades);
        Assert.Equal("Short", trade.Direction);
        Assert.Equal(105m, trade.ExitPrice);
        Assert.True(trade.Pnl < 0m);
    }

    [Fact]
    public async Task StopAndTarget_SameCandle_StopWins()
    {
        // Barra che tocca sia lo stop (95) sia il target (110): si assume l'esito peggiore.
        var candles = Candles(
            (100m, 101m, 99m, 100m),
            (100m, 112m, 94m, 100m),
            (100m, 101m, 99m, 100m));
        var result = await RunAsync(candles, new() { [0] = Signal.Long }, stopLoss: 5m, takeProfit: 10m);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(95m, trade.ExitPrice);
    }

    [Fact]
    public async Task EntryCandle_NotStoppedByOwnExcursion()
    {
        // La candela di ingresso ha un low profondo, ma il fill avviene alla sua close:
        // lo stop non deve scattare sulla candela di ingresso stessa.
        var candles = Candles(
            (100m, 101m, 80m, 100m),   // 0: low 80 ma entriamo alla close 100
            (100m, 102m, 99m, 101m),
            (101m, 102m, 100m, 101m));
        var result = await RunAsync(candles, new() { [0] = Signal.Long }, stopLoss: 5m);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(101m, trade.ExitPrice); // chiusura a fine serie, stop mai scattato
    }

    [Fact]
    public async Task PriceSmaCross_GeneratesLongAboveSma_AndClosesBelow()
    {
        // Serie: sotto la SMA, poi cross sopra, poi cross sotto.
        var strategy = new PriceSmaCrossStrategy();
        var closes = new List<decimal> { 100m, 99m, 98m, 97m, 96m, 104m, 106m, 108m, 90m, 89m };
        var candles = Candles(closes.Select(c => (c, c + 0.5m, c - 0.5m, c)).ToArray());

        await strategy.InitializeAsync(closes, candles,
            new Dictionary<string, decimal> { ["Period"] = 5m, ["AllowShort"] = 0m },
            new TechnicalIndicatorsService(), CancellationToken.None);

        var signals = new List<Signal>();
        for (var i = 0; i < closes.Count; i++)
        {
            signals.Add(strategy.EvaluateSignal(i, closes[i], candles[i].TimestampUtc));
        }

        Assert.Contains(Signal.Long, signals);
        Assert.Contains(Signal.Close, signals);
        Assert.True(signals.IndexOf(Signal.Long) < signals.IndexOf(Signal.Close));
    }
}
