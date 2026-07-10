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
/// Test della contabilita' a margine del motore: leva, liquidazione intrabar, funding e
/// slippage. Con leva 1 e tutto a 0 il comportamento deve restare IDENTICO allo spot.
/// </summary>
[Collection("Postgres")]
public class BacktestLeverageTests
{
    private readonly PostgresFixture _pg;

    public BacktestLeverageTests(PostgresFixture pg) => _pg = pg;

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
        decimal leverage = 1m, decimal fee = 0m, decimal slippage = 0m, decimal funding = 0m,
        decimal positionSize = 100m, decimal maintenance = 0.5m)
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
            PositionSizePercent = positionSize,
            FeePercent = fee,
            Leverage = leverage,
            SlippagePercent = slippage,
            FundingRatePercentPer8h = funding,
            MaintenanceMarginPercent = maintenance,
        };
        var engine = provider.GetRequiredService<IBacktestEngine>();
        return await engine.RunBacktestAsync(config, candles, new ScriptedStrategy(script), CancellationToken.None);
    }

    [Fact]
    public async Task Leverage1_MatchesLegacySpotAccounting()
    {
        // Long a 100, chiusura a 110, fee 0.1%: contabilita' identica allo spot storico.
        // notional = 10000, qty = 100, entryFee = 10, exitFee = 11 -> pnl = 1000 - 21 = 979.
        var candles = Candles((100m, 101m, 99m, 100m), (100m, 111m, 100m, 110m), (110m, 111m, 109m, 110m));
        var result = await RunAsync(candles, new() { [0] = Signal.Long, [2] = Signal.Close }, fee: 0.1m);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(979m, trade.Pnl);
        Assert.Equal(10_979m, result.FinalCapital);
        Assert.Equal(0, result.LiquidationCount);
    }

    [Fact]
    public async Task Leverage5_PnlIsFiveTimesNotionalReturn()
    {
        // Margine 100% del capitale, leva 5: nozionale 50000, +10% sul prezzo -> +5000 (senza fee).
        var candles = Candles((100m, 101m, 99m, 100m), (100m, 111m, 100m, 110m), (110m, 111m, 109m, 110m));
        var result = await RunAsync(candles, new() { [0] = Signal.Long, [2] = Signal.Close }, leverage: 5m);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(5_000m, trade.Pnl);
        Assert.Equal(15_000m, result.FinalCapital);
    }

    [Fact]
    public async Task Leverage10_Long_LiquidatedOnAdverseMove()
    {
        // Leva 10, mantenimento 0.5%: liquidazione a entry*(1 - (0.1 - 0.005*... )):
        // margine 10000, nozionale 100000, qty 1000, buffer = (10000 - 500)/1000 = 9.5 -> liq a 90.5.
        // La candela 1 scende a 85: chiusura FORZATA a 90.5, perdita = 9500 (il margine meno il mantenimento).
        var candles = Candles(
            (100m, 101m, 99m, 100m),
            (100m, 100m, 85m, 88m),
            (88m, 89m, 87m, 88m));
        var result = await RunAsync(candles, new() { [0] = Signal.Long }, leverage: 10m);

        var trade = Assert.Single(result.Trades);
        Assert.True(trade.WasLiquidated);
        Assert.Equal(1, result.LiquidationCount);
        Assert.Equal(90.5m, trade.ExitPrice);
        Assert.Equal(-9_500m, trade.Pnl);
        Assert.Equal(500m, result.FinalCapital); // resta solo il margine di mantenimento
    }

    [Fact]
    public async Task Leverage10_Short_LiquidatedOnRally()
    {
        // Short a 100, leva 10: liquidazione a 100 + 9.5 = 109.5. La candela 1 sale a 115.
        var candles = Candles(
            (100m, 101m, 99m, 100m),
            (100m, 115m, 100m, 112m),
            (112m, 113m, 111m, 112m));
        var result = await RunAsync(candles, new() { [0] = Signal.Short }, leverage: 10m);

        var trade = Assert.Single(result.Trades);
        Assert.True(trade.WasLiquidated);
        Assert.Equal(109.5m, trade.ExitPrice);
        Assert.Equal("Short", trade.Direction);
    }

    [Fact]
    public async Task StopLoss_PreventsLiquidation()
    {
        // Stesso scenario della liquidazione long, ma con SL 3%: lo stop a 97 scatta prima
        // del prezzo di liquidazione (90.5) e salva la maggior parte del margine.
        var candles = Candles(
            (100m, 101m, 99m, 100m),
            (100m, 100m, 85m, 88m),
            (88m, 89m, 87m, 88m));

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
            Leverage = 10m,
            StopLossPercent = 3m,
        };
        var engine = provider.GetRequiredService<IBacktestEngine>();
        var result = await engine.RunBacktestAsync(config, candles,
            new ScriptedStrategy(new() { [0] = Signal.Long }), CancellationToken.None);

        var trade = Assert.Single(result.Trades);
        Assert.False(trade.WasLiquidated);
        Assert.Equal(97m, trade.ExitPrice);        // stop, non liquidazione
        Assert.Equal(-3_000m, trade.Pnl);          // 3% su nozionale 100k = 3k (30% del margine)
    }

    [Fact]
    public async Task Slippage_WorsensBothFills()
    {
        // Slippage 0.1%: entry long a 100*1.001, exit a 110*0.999 (fee 0).
        var candles = Candles((100m, 101m, 99m, 100m), (100m, 111m, 100m, 110m), (110m, 111m, 109m, 110m));
        var result = await RunAsync(candles, new() { [0] = Signal.Long, [2] = Signal.Close }, slippage: 0.1m);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(100.1m, trade.EntryPrice);
        Assert.Equal(109.89m, trade.ExitPrice);
        Assert.True(trade.Pnl < 1_000m); // peggiore del fill teorico
    }

    [Fact]
    public async Task Funding_ChargedProRata_EntersTradePnl()
    {
        // Funding 0.01%/8h su 1h: 3 candele con posizione aperta (1,2 e ancora aperta alla 3
        // chiusura forzata). Nozionale 10000 -> 10000 * 0.0001 * (1/8) = 0.125 per candela.
        var candles = Candles(
            (100m, 101m, 99m, 100m),
            (100m, 101m, 99m, 100m),
            (100m, 101m, 99m, 100m),
            (100m, 101m, 99m, 100m));
        var noFunding = await RunAsync(candles, new() { [0] = Signal.Long });
        var withFunding = await RunAsync(candles, new() { [0] = Signal.Long }, funding: 0.01m);

        var diff = noFunding.Trades[0].Pnl - withFunding.Trades[0].Pnl;
        Assert.True(diff > 0m, "il funding deve ridurre il PnL");
        Assert.InRange(diff, 0.3m, 0.6m); // ~0.125 x 3-4 candele
    }
}
