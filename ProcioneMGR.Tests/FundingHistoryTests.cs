using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Tests;

/// <summary>
/// [T0.2 roadmap macchina-ricerca] Funding storico e FIRMATO nel motore di backtest.
///
/// Il difetto corretto: il motore addebitava una costante senza segno a qualunque posizione. Nella
/// realtà il funding è firmato e va per lato — con funding positivo il long paga e lo short
/// INCASSA. La costante penalizzava sistematicamente gli short, cioè metà del catalogo, ed era
/// esattamente il tipo di distorsione invisibile che una selezione "onesta sui costi" non può
/// permettersi.
/// </summary>
public class FundingHistoryTests
{
    // --- FundingRateLookup: il gradino fa quello che dichiara --------------------------------

    [Fact]
    public void Lookup_ReturnsLatestEventAtOrBeforeTimestamp()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lookup = FundingRateLookup.BuildOrNull(
        [
            new FundingRatePoint(t0, 0.01m),
            new FundingRatePoint(t0.AddHours(8), -0.02m),
            new FundingRatePoint(t0.AddHours(16), 0.03m),
        ])!;

        Assert.Equal(0.0001m, lookup.RateFracAt(t0, 999m));                    // evento esatto
        Assert.Equal(0.0001m, lookup.RateFracAt(t0.AddHours(4), 999m));        // fra i primi due
        Assert.Equal(-0.0002m, lookup.RateFracAt(t0.AddHours(8), 999m));       // il segno passa
        Assert.Equal(0.0003m, lookup.RateFracAt(t0.AddHours(100), 999m));      // oltre l'ultimo
    }

    [Fact]
    public void Lookup_BeforeFirstEvent_FallsBackToConstant_InsteadOfInventing()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lookup = FundingRateLookup.BuildOrNull([new FundingRatePoint(t0, 0.01m)])!;

        Assert.Equal(0.5m, lookup.RateFracAt(t0.AddHours(-1), 0.5m));
    }

    [Fact]
    public void Lookup_NullOrEmptyHistory_YieldsNull_SoTheConstantPathIsUsed()
    {
        Assert.Null(FundingRateLookup.BuildOrNull(null));
        Assert.Null(FundingRateLookup.BuildOrNull([]));
    }

    // --- Il motore: firmato per lato ---------------------------------------------------------

    private sealed class AlwaysSide(Signal side) : IStrategy
    {
        public string Name => "AlwaysSide";
        public string DisplayName => "AlwaysSide";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];
        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct)
            => Task.CompletedTask;
        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
            => index == 10 ? side : Signal.Hold;
    }

    private static List<OhlcvData> FlatCandles(int n)
    {
        var list = new List<OhlcvData>();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            list.Add(new OhlcvData
            {
                Symbol = "FND/USDT", Timeframe = "1d", TimestampUtc = t0.AddDays(i),
                Open = 100m, High = 100m, Low = 100m, Close = 100m, Volume = 100m,
            });
        }
        return list;
    }

    private static BacktestConfiguration Cfg(decimal constantRate, List<FundingRatePoint>? history = null) => new()
    {
        ExchangeName = "Binance", Symbol = "FND/USDT", Timeframe = "1d",
        InitialCapital = 10_000m, PositionSizePercent = 100m, FeePercent = 0m,
        FundingRatePercentPer8h = constantRate,
        FundingHistory = history,
    };

    private static Task<BacktestResult> RunAsync(Signal side, BacktestConfiguration cfg, List<OhlcvData> candles) =>
        new BacktestEngine(null!, null!, new TechnicalIndicatorsService(), null!, NullLogger<BacktestEngine>.Instance)
            .RunBacktestAsync(cfg, candles, new AlwaysSide(side), CancellationToken.None);

    [Fact]
    public async Task PositiveFunding_LongPays_ShortReceives()
    {
        // Prezzi piatti e fee zero: l'UNICO flusso è il funding. 40 candele, ingresso all'indice 10.
        var candles = FlatCandles(40);

        var longRes = await RunAsync(Signal.Long, Cfg(0.01m), candles);
        var shortRes = await RunAsync(Signal.Short, Cfg(0.01m), candles);

        Assert.True(longRes.TotalFundingPaid > 0m, $"il long deve PAGARE il funding positivo, pagato {longRes.TotalFundingPaid}");
        Assert.True(shortRes.TotalFundingPaid < 0m, $"lo short deve INCASSARE il funding positivo, netto {shortRes.TotalFundingPaid}");

        // Simmetria esatta: stessa posizione, stesso nozionale, rate opposto per lato.
        Assert.Equal(longRes.TotalFundingPaid, -shortRes.TotalFundingPaid);

        // E il conto torna a mano: nozionale 10.000, rate 0,01%/8h, candele 1d = 3 periodi/giorno.
        // Funding addebitato per ogni candela con posizione aperta (dalla 11 alla 39 = 29 candele).
        var expected = 10_000m * 0.0001m * 3m * 29m;
        Assert.Equal(expected, longRes.TotalFundingPaid);
    }

    [Fact]
    public async Task HistoricalSeries_OverridesTheConstant_AndRespectsSign()
    {
        var candles = FlatCandles(40);
        var t0 = candles[0].TimestampUtc;

        // Storia: funding fortemente NEGATIVO per tutto il periodo (short paga, long incassa) —
        // l'opposto della costante positiva, così se vincesse la costante il test lo vede.
        var history = new List<FundingRatePoint> { new(t0, -0.05m) };

        var longRes = await RunAsync(Signal.Long, Cfg(0.01m, history), candles);

        Assert.True(longRes.TotalFundingPaid < 0m,
            $"con la serie storica negativa il long deve INCASSARE, netto {longRes.TotalFundingPaid}");
        var expected = -(10_000m * 0.0005m * 3m * 29m);
        Assert.Equal(expected, longRes.TotalFundingPaid);
    }

    [Fact]
    public async Task SeriesStartingMidRun_UsesConstantBeforeAndSeriesAfter()
    {
        var candles = FlatCandles(40);
        // La serie parte alla candela 25: prima vale la costante (0,01), dopo il rate storico (0,03).
        var history = new List<FundingRatePoint> { new(candles[25].TimestampUtc, 0.03m) };

        var res = await RunAsync(Signal.Long, Cfg(0.01m, history), candles);

        // Candele 11..24 a costante (14), candele 25..39 a storico (15).
        var expected = 10_000m * 3m * (0.0001m * 14m + 0.0003m * 15m);
        Assert.Equal(expected, res.TotalFundingPaid);
    }

    [Fact]
    public async Task ZeroConstantAndNoHistory_ChargesNothing_BehaviourUnchanged()
    {
        var candles = FlatCandles(40);
        var res = await RunAsync(Signal.Long, Cfg(0m), candles);

        Assert.Equal(0m, res.TotalFundingPaid);
        Assert.Equal(10_000m, res.FinalCapital);
    }

    // --- Provider: estrazione del ticker base -------------------------------------------------

    [Theory]
    [InlineData("BTC/USDT", "BTC")]
    [InlineData("eth/usdt", "ETH")]
    [InlineData("SOL", "SOL")]
    public void ProviderBaseTicker_MatchesSentimentConvention(string symbol, string expected)
        => Assert.Equal(expected, FundingHistoryProvider.ToBaseTicker(symbol));
}
