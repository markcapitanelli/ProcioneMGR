using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Services.Trading.Internal;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del dosaggio della posizione sulla volatilità (<see cref="VolatilityScaler"/>), l'unico
/// risultato di ricerca sopravvissuto al controllo a esposizione media costante
/// (docs/REPORT-DOSAGGIO-VOLATILITA.md).
///
/// Il test che conta più di tutti è <see cref="WithDefaults_CanOnlyReduceExposure_NeverIncrease"/>:
/// il tetto a 1,0 è ciò che rende impossibile, accendendo questa funzione, superare i limiti di
/// sicurezza già validati a StartAsync. Se un giorno quel default cambia, quel test deve gridare.
/// </summary>
public class VolatilityScalerTests
{
    private static SafetyConfiguration Cfg(bool enabled = true, decimal target = 30m, int lookback = 30,
        decimal min = 0.25m, decimal max = 1.0m) => new()
    {
        VolatilityTargetingEnabled = enabled,
        TargetAnnualVolatilityPercent = target,
        VolatilityLookbackBars = lookback,
        MinExposureMultiplier = min,
        MaxExposureMultiplier = max,
    };

    /// <summary>Serie con volatilità controllata: alterna +p% e −p% attorno a un livello.</summary>
    private static List<decimal> Oscillating(int n, decimal pct)
    {
        var closes = new List<decimal> { 100m };
        for (var i = 1; i < n; i++)
            closes.Add(i % 2 == 1 ? 100m * (1m + pct) : 100m);
        return closes;
    }

    [Fact]
    public void Disabled_ReturnsOne_BehaviourUnchanged()
    {
        var m = VolatilityScaler.Compute(Oscillating(100, 0.05m), "1d", Cfg(enabled: false));
        Assert.Equal(1m, m);
    }

    [Fact]
    public void NotEnoughHistory_ReturnsOne_RatherThanGuessing()
    {
        // Con meno di lookback+1 prezzi non si stima niente: meglio invariato che una stima su
        // quattro punti, che sarebbe rumore travestito da misura.
        var m = VolatilityScaler.Compute(Oscillating(10, 0.05m), "1d", Cfg(lookback: 30));
        Assert.Equal(1m, m);
    }

    [Fact]
    public void FlatPrices_ReturnsOne_NoDivisionByZeroVolatility()
    {
        var flat = Enumerable.Repeat(100m, 60).ToList();
        var m = VolatilityScaler.Compute(flat, "1d", Cfg());
        Assert.Equal(1m, m);
    }

    [Fact]
    public void HigherVolatility_ProducesSmallerMultiplier()
    {
        // La proprietà centrale: più il mercato si agita, meno capitale si espone.
        var calm = VolatilityScaler.Compute(Oscillating(60, 0.005m), "1d", Cfg());
        var wild = VolatilityScaler.Compute(Oscillating(60, 0.05m), "1d", Cfg());

        Assert.True(wild < calm, $"atteso moltiplicatore minore in alta volatilità: calmo {calm}, agitato {wild}");
    }

    [Fact]
    public void WithDefaults_CanOnlyReduceExposure_NeverIncrease()
    {
        // PROPRIETÀ DI SICUREZZA. Col tetto di default a 1,0 il dosaggio non può mai ingrandire la
        // posizione decisa da PositionSizePercent, quindi accenderlo non può violare
        // MaxPositionSizePercent né MaxTotalExposurePercent: al più li rende più stringenti.
        // Vale anche su un mercato quasi immobile, dove il rapporto target/realizzata esploderebbe.
        foreach (var pct in new[] { 0.0001m, 0.001m, 0.005m, 0.02m, 0.05m, 0.15m })
        {
            var m = VolatilityScaler.Compute(Oscillating(60, pct), "1d", Cfg());
            Assert.InRange(m, 0.25m, 1.0m);
        }
    }

    [Fact]
    public void Floor_IsRespected_EvenInExtremeVolatility()
    {
        // Un mercato impazzito non deve azzerare l'operatività: sotto il pavimento non si scende.
        var m = VolatilityScaler.Compute(Oscillating(60, 0.40m), "1d", Cfg(min: 0.25m));
        Assert.Equal(0.25m, m);
    }

    [Fact]
    public void Timeframe_ChangesAnnualisation_SoTheSameSeriesScalesDifferently()
    {
        // La stessa oscillazione per barra è molto più volatile su base annua a 1h che a 1d:
        // annualizzare col timeframe sbagliato falserebbe il dosaggio di un fattore ~5.
        var series = Oscillating(60, 0.01m);
        var daily = VolatilityScaler.Compute(series, "1d", Cfg());
        var hourly = VolatilityScaler.Compute(series, "1h", Cfg());

        Assert.True(hourly < daily, $"a 1h la stessa serie è più volatile su base annua: 1d {daily}, 1h {hourly}");
    }

    [Fact]
    public void RealizedVolatility_MatchesAKnownCase()
    {
        // Ancoraggio numerico: rendimenti che alternano +1% e -0,990099% hanno deviazione standard
        // campionaria nota; qui si verifica solo che l'annualizzazione usi sqrt(365) su 1d e che
        // l'ordine di grandezza sia quello atteso, non un valore arbitrario.
        var series = Oscillating(61, 0.01m);
        var vol = VolatilityScaler.RealizedAnnualVolatility(series, 30, "1d");

        // ~1% per barra giornaliera -> ~19% annualizzato (0,01 * sqrt(365) ≈ 0,191), con la
        // correzione dovuta all'alternanza esatta.
        Assert.InRange(vol, 0.15, 0.25);
    }

    // --- Integrazione col motore di BACKTEST ---------------------------------------------------
    // Il dosaggio dev'essere misurabile sui propri dati PRIMA di accenderlo dal vivo: se il backtest
    // non sapesse dosare, accenderlo aprirebbe un divario backtest/live.

    private sealed class AlwaysLong : IStrategy
    {
        public string Name => "AlwaysLong";
        public string DisplayName => "AlwaysLong";
        public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];
        public Task InitializeAsync(IReadOnlyList<decimal> closes, IReadOnlyList<OhlcvData> candles,
            IReadOnlyDictionary<string, decimal> parameters, ITechnicalIndicatorsService indicators, CancellationToken ct)
            => Task.CompletedTask;
        public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
            => index == 40 ? Signal.Long : Signal.Hold;
    }

    private static List<OhlcvData> VolatileCandles(int n, decimal pct)
    {
        var list = new List<OhlcvData>();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            var close = i % 2 == 1 ? 100m * (1m + pct) : 100m;
            list.Add(new OhlcvData
            {
                Symbol = "TEST/USDT", Timeframe = "1d", TimestampUtc = t0.AddDays(i),
                Open = close, High = close, Low = close, Close = close, Volume = 100m,
            });
        }
        return list;
    }

    private static BacktestConfiguration BtConfig(bool enabled) => new()
    {
        ExchangeName = "Binance", Symbol = "TEST/USDT", Timeframe = "1d",
        InitialCapital = 10_000m, PositionSizePercent = 50m, FeePercent = 0m,
        VolatilityTargeting = new VolatilityTargetingOptions
        {
            Enabled = enabled, TargetAnnualVolatilityPercent = 30m, LookbackBars = 30,
            MinExposureMultiplier = 0.25m, MaxExposureMultiplier = 1.0m,
        },
    };

    private static Task<BacktestResult> RunBtAsync(BacktestConfiguration cfg, List<OhlcvData> candles) =>
        new BacktestEngine(null!, null!, new TechnicalIndicatorsService(), null!, NullLogger<BacktestEngine>.Instance)
            .RunBacktestAsync(cfg, candles, new AlwaysLong(), CancellationToken.None);

    [Fact]
    public async Task Backtest_WithTargetingOff_IsBitIdenticalToBefore()
    {
        // Rete di sicurezza sul default: spento, il motore deve dare esattamente il risultato di prima.
        var candles = VolatileCandles(80, 0.05m);
        var off = await RunBtAsync(BtConfig(enabled: false), candles);

        var trade = Assert.Single(off.Trades);
        // size 50% del capitale a prezzo 100 => 50 unità, senza alcun dosaggio
        Assert.Equal(50m, trade.Quantity, 5);
    }

    [Fact]
    public async Task Backtest_WithTargetingOn_OpensASmallerPositionInAVolatileMarket()
    {
        // Stessa serie, stesso segnale: acceso, la posizione dev'essere più piccola perché il
        // mercato è molto più volatile del target.
        var candles = VolatileCandles(80, 0.05m);
        var off = await RunBtAsync(BtConfig(enabled: false), candles);
        var on = await RunBtAsync(BtConfig(enabled: true), candles);

        var qOff = Assert.Single(off.Trades).Quantity;
        var qOn = Assert.Single(on.Trades).Quantity;

        Assert.True(qOn < qOff, $"attesa posizione ridotta: senza dosaggio {qOff}, con dosaggio {qOn}");
        // Col pavimento a 0,25 non può scendere sotto un quarto.
        Assert.True(qOn >= qOff * 0.25m - 0.001m, $"il pavimento del moltiplicatore non è stato rispettato: {qOn}");
    }
}
