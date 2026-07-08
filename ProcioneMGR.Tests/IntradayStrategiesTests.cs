using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test puri (senza DB) delle strategie intraday nuove — Supertrend, Stochastic, VwapReversion —
/// e dell'indicatore ATR che le abilita. Su serie sintetiche costruite per innescare un
/// comportamento noto, così le asserzioni sono deterministiche e verificabili.
/// </summary>
public class IntradayStrategiesTests
{
    private readonly TechnicalIndicatorsService _svc = new();

    private static OhlcvData Candle(DateTime t, decimal o, decimal h, decimal l, decimal c, decimal v = 100m)
        => new() { Symbol = "X", Timeframe = "5m", TimestampUtc = t, Open = o, High = h, Low = l, Close = c, Volume = v };

    // -------------------------------------------------------------------- ATR

    [Fact]
    public async Task Atr_KnownVector_MatchesHandComputation()
    {
        // Serie semplice: 6 barre con TR calcolabile a mano.
        //  H,L,Cprec -> TR = max(H-L, |H-Cprec|, |L-Cprec|)
        var highs = new List<decimal> { 10m, 11m, 12m, 11m, 13m, 12m };
        var lows = new List<decimal> { 8m, 9m, 10m, 9m, 11m, 10m };
        var closes = new List<decimal> { 9m, 10m, 11m, 10m, 12m, 11m };
        // TR[1]=max(11-9,|11-9|,|9-9|)=2; TR[2]=max(2,|12-10|,|10-10|)=2; TR[3]=max(2,|11-11|,|9-11|)=2;
        // ATR(period=3) primo valore all'indice 3 = media(TR1,TR2,TR3) = (2+2+2)/3 = 2.
        var atr = await _svc.CalculateAtrAsync(highs, lows, closes, period: 3);

        Assert.Null(atr[0]);
        Assert.Null(atr[1]);
        Assert.Null(atr[2]);
        Assert.NotNull(atr[3]);
        Assert.Equal(2m, atr[3]!.Value, 6);

        // TR[4]=max(13-11,|13-10|,|11-10|)=3; Wilder: ATR4=(ATR3*2+TR4)/3=(2*2+3)/3=2.3333...
        Assert.Equal(2.3333333m, atr[4]!.Value, 5);
    }

    [Fact]
    public async Task Atr_MismatchedLengths_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _svc.CalculateAtrAsync([1m, 2m], [1m], [1m, 2m], period: 2));
    }

    // -------------------------------------------------------------------- helpers

    private static async Task<Signal[]> RunStrategyAsync(IStrategy strategy, IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> pars)
    {
        var closes = candles.Select(c => c.Close).ToList();
        var svc = new TechnicalIndicatorsService();
        await strategy.InitializeAsync(closes, candles, pars, svc, CancellationToken.None);
        var signals = new Signal[candles.Count];
        for (var i = 0; i < candles.Count; i++)
        {
            signals[i] = strategy.EvaluateSignal(i, candles[i].Close, candles[i].TimestampUtc);
        }
        return signals;
    }

    // -------------------------------------------------------------------- Stochastic

    [Fact]
    public async Task Stochastic_CloseAtBottomOfRange_IsOversold_Long()
    {
        // Range costante [90,110] con la close che crolla al fondo -> %K vicino a 0 -> Long.
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>();
        for (var i = 0; i < 30; i++)
        {
            candles.Add(Candle(start.AddMinutes(5 * i), 100m, 110m, 90m, 100m));
        }
        // Ultime barre: close al minimo del range.
        for (var i = 25; i < 30; i++) candles[i] = Candle(candles[i].TimestampUtc, 100m, 110m, 90m, 91m);

        var pars = new Dictionary<string, decimal> { ["KPeriod"] = 14m, ["DPeriod"] = 3m, ["OversoldThreshold"] = 20m, ["OverboughtThreshold"] = 80m };
        var signals = await RunStrategyAsync(new StochasticStrategy(), candles, pars);

        Assert.Contains(Signal.Long, signals[^3..]);
        Assert.DoesNotContain(Signal.Short, signals);
    }

    [Fact]
    public async Task Stochastic_IsDeterministic()
    {
        var candles = SyntheticSeries(200, seed: 5);
        var pars = new Dictionary<string, decimal> { ["KPeriod"] = 14m, ["DPeriod"] = 3m, ["OversoldThreshold"] = 20m, ["OverboughtThreshold"] = 80m };
        var a = await RunStrategyAsync(new StochasticStrategy(), candles, pars);
        var b = await RunStrategyAsync(new StochasticStrategy(), candles, pars);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Stochastic_InvalidThresholds_Throws()
    {
        var candles = SyntheticSeries(30, seed: 1);
        var pars = new Dictionary<string, decimal> { ["KPeriod"] = 14m, ["OversoldThreshold"] = 80m, ["OverboughtThreshold"] = 20m };
        await Assert.ThrowsAsync<ArgumentException>(() => RunStrategyAsync(new StochasticStrategy(), candles, pars));
    }

    // -------------------------------------------------------------------- VWAP reversion

    [Fact]
    public async Task VwapReversion_PriceBelowSessionVwap_Long_AboveShort()
    {
        // Sessione unica (stesso giorno UTC): prime barre a prezzo alto (VWAP alto per volume),
        // poi una barra molto sotto -> Long; una molto sopra -> Short.
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>();
        for (var i = 0; i < 10; i++)
        {
            candles.Add(Candle(start.AddMinutes(5 * i), 100m, 100m, 100m, 100m, v: 1000m));
        }
        // Barra molto sotto il VWAP (~100): deviazione < -threshold.
        candles.Add(Candle(start.AddMinutes(55), 100m, 100m, 90m, 92m, v: 1m));
        // Barra molto sopra il VWAP.
        candles.Add(Candle(start.AddMinutes(60), 100m, 110m, 100m, 108m, v: 1m));

        var pars = new Dictionary<string, decimal> { ["Threshold"] = 0.02m, ["AllowShort"] = 1m };
        var signals = await RunStrategyAsync(new VwapReversionStrategy(), candles, pars);

        Assert.Equal(Signal.Long, signals[10]);
        Assert.Equal(Signal.Short, signals[11]);
    }

    [Fact]
    public async Task VwapReversion_ResetsEachUtcDay()
    {
        // Due giorni: il VWAP del secondo giorno NON deve ereditare l'accumulo del primo.
        var d1 = new DateTime(2025, 1, 1, 23, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>
        {
            Candle(d1, 200m, 200m, 200m, 200m, v: 1000m),                 // giorno 1, prezzo alto
            Candle(d1.AddHours(1), 100m, 100m, 100m, 100m, v: 1000m),     // giorno 2 (00:00), prezzo 100
            Candle(d1.AddHours(1).AddMinutes(5), 100m, 100m, 100m, 100m, v: 1000m),
        };
        var pars = new Dictionary<string, decimal> { ["Threshold"] = 0.05m, ["AllowShort"] = 1m };
        var strat = new VwapReversionStrategy();
        var closes = candles.Select(c => c.Close).ToList();
        await strat.InitializeAsync(closes, candles, pars, new TechnicalIndicatorsService(), CancellationToken.None);

        // Alla 2ª barra (primo bar del giorno 2) il VWAP = 100 (reset), non una media con 200:
        // close 100 == vwap 100 -> nessuna deviazione -> Hold (se avesse ereditato 200, sarebbe Long).
        Assert.Equal(Signal.Hold, strat.EvaluateSignal(1, 100m, candles[1].TimestampUtc));
    }

    [Fact]
    public async Task VwapReversion_AllowShortFalse_NoShort()
    {
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>();
        for (var i = 0; i < 10; i++) candles.Add(Candle(start.AddMinutes(5 * i), 100m, 100m, 100m, 100m, v: 1000m));
        candles.Add(Candle(start.AddMinutes(55), 100m, 120m, 100m, 115m, v: 1m)); // molto sopra

        var pars = new Dictionary<string, decimal> { ["Threshold"] = 0.02m, ["AllowShort"] = 0m };
        var signals = await RunStrategyAsync(new VwapReversionStrategy(), candles, pars);
        Assert.DoesNotContain(Signal.Short, signals);
    }

    // -------------------------------------------------------------------- Supertrend

    [Fact]
    public async Task Supertrend_SustainedUptrend_EmitsLong_Deterministic()
    {
        // Trend rialzista netto: il Supertrend deve entrare Long a un certo punto.
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>();
        decimal price = 100m;
        for (var i = 0; i < 60; i++)
        {
            var open = price;
            price += 1m; // salita costante
            candles.Add(Candle(start.AddMinutes(5 * i), open, price + 0.5m, open - 0.5m, price));
        }
        var pars = new Dictionary<string, decimal> { ["AtrPeriod"] = 10m, ["Multiplier"] = 3m, ["AllowShort"] = 1m };
        var a = await RunStrategyAsync(new SupertrendStrategy(), candles, pars);
        var b = await RunStrategyAsync(new SupertrendStrategy(), candles, pars);

        Assert.Equal(a, b);                 // determinismo
        Assert.Contains(Signal.Long, a);    // trend rialzista -> almeno un Long
    }

    [Fact]
    public async Task Supertrend_AllowShortFalse_DownSwitchIsClose_NotShort()
    {
        // Su -> giù: con AllowShort=0 il rovescio ribassista deve essere Close, mai Short.
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>();
        decimal price = 100m;
        for (var i = 0; i < 40; i++) { var o = price; price += 1m; candles.Add(Candle(start.AddMinutes(5 * i), o, price + 0.5m, o - 0.5m, price)); }
        for (var i = 40; i < 80; i++) { var o = price; price -= 1m; candles.Add(Candle(start.AddMinutes(5 * i), o, o + 0.5m, price - 0.5m, price)); }

        var pars = new Dictionary<string, decimal> { ["AtrPeriod"] = 10m, ["Multiplier"] = 3m, ["AllowShort"] = 0m };
        var signals = await RunStrategyAsync(new SupertrendStrategy(), candles, pars);
        Assert.DoesNotContain(Signal.Short, signals);
        Assert.Contains(Signal.Close, signals);
    }

    [Fact]
    public async Task Supertrend_InvalidParams_Throws()
    {
        var candles = SyntheticSeries(30, seed: 2);
        var pars = new Dictionary<string, decimal> { ["AtrPeriod"] = 1m, ["Multiplier"] = 3m };
        await Assert.ThrowsAsync<ArgumentException>(() => RunStrategyAsync(new SupertrendStrategy(), candles, pars));
    }

    // -------------------------------------------------------------------- shared synthetic series

    private static List<OhlcvData> SyntheticSeries(int count, int seed)
    {
        var rng = new Random(seed);
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>(count);
        decimal price = 100m;
        for (var i = 0; i < count; i++)
        {
            var open = price;
            var close = Math.Max(1m, price + (decimal)(rng.NextDouble() * 4 - 2));
            var high = Math.Max(open, close) + (decimal)rng.NextDouble();
            var low = Math.Min(open, close) - (decimal)rng.NextDouble();
            candles.Add(Candle(start.AddMinutes(5 * i), open, high, low, close, v: 100m + (decimal)rng.NextDouble() * 50m));
            price = close;
        }
        return candles;
    }
}
