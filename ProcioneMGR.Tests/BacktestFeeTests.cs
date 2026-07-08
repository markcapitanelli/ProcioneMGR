using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Tests;

/// <summary>
/// Regressione: una commissione negativa (bug d'uso in Backtest.razor, Fee % = -1) non deve
/// comportarsi come un rebate che gonfia i rendimenti. Il motore la clampa a >= 0, quindi una
/// fee negativa deve produrre lo stesso rendimento di fee 0, mai uno superiore.
/// </summary>
public class BacktestFeeTests
{
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

    private static async Task<BacktestResult> RunAsync(
        List<OhlcvData> candles, Dictionary<int, Signal> script, decimal feePercent)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite("DataSource=:memory:"));
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
            FeePercent = feePercent,
        };

        var engine = provider.GetRequiredService<IBacktestEngine>();
        return await engine.RunBacktestAsync(config, candles, new ScriptedStrategy(script), CancellationToken.None);
    }

    [Fact]
    public async Task NegativeFee_DoesNotBoostReturnAboveZeroFee()
    {
        // Piu' round-trip perdenti: con fee 0 la strategia perde; con una fee negativa trattata
        // come rebate (bug) la perdita si ribalterebbe in profitto. Il clamp lo impedisce.
        var candles = Candles(
            (100m, 101m, 99m, 100m),  // 0: entra long
            (100m, 100m, 96m, 97m),   // 1: scende
            (97m, 98m, 96m, 98m),     // 2: chiude in perdita
            (98m, 99m, 97m, 98m),     // 3: entra long
            (98m, 98m, 94m, 95m),     // 4: scende
            (95m, 96m, 94m, 96m));    // 5: chiusura forzata a fine serie
        var script = new Dictionary<int, Signal>
        {
            [0] = Signal.Long,
            [2] = Signal.Close,
            [3] = Signal.Long,
        };

        var zeroFee = await RunAsync(candles, script, feePercent: 0m);
        var negativeFee = await RunAsync(candles, script, feePercent: -1m);

        // La fee negativa e' clampata a 0: rendimento identico, mai superiore.
        Assert.True(negativeFee.TotalReturnPercent <= zeroFee.TotalReturnPercent,
            $"fee negativa ha aumentato il rendimento: {negativeFee.TotalReturnPercent:F2}% > {zeroFee.TotalReturnPercent:F2}%");
        Assert.Equal(zeroFee.FinalCapital, negativeFee.FinalCapital);
        Assert.True(zeroFee.TotalTrades > 0, "il test richiede almeno un trade per esercitare le fee");
    }

    [Fact]
    public async Task PositiveFee_ReducesReturnVsZeroFee()
    {
        // Sanita': una fee positiva (0,1%) deve invece ridurre il rendimento rispetto a fee 0,
        // confermando che le commissioni incidono davvero sui trade generati.
        var candles = Candles(
            (100m, 101m, 99m, 100m),
            (100m, 105m, 99m, 104m),
            (104m, 106m, 103m, 105m),
            (105m, 106m, 104m, 105m));
        var script = new Dictionary<int, Signal> { [0] = Signal.Long, [2] = Signal.Close };

        var zeroFee = await RunAsync(candles, script, feePercent: 0m);
        var withFee = await RunAsync(candles, script, feePercent: 0.1m);

        Assert.True(withFee.TotalReturnPercent < zeroFee.TotalReturnPercent);
    }
}
