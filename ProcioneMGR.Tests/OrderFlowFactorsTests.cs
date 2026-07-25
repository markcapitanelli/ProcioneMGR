using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Tests;

/// <summary>
/// [3.8b roadmap macchina-ricerca] Fattori order-flow sui campi klines recuperati da T0.3.
///
/// La proprietà più importante: NULL dove i campi estesi mancano — un fattore che leggesse zero su
/// una candela non reingerita produrrebbe un imbalance di -1 finto (tutto vendite) e il modello
/// imparerebbe un artefatto della migrazione, non il mercato.
/// </summary>
public class OrderFlowFactorsTests
{
    private static OhlcvData Candle(int i, decimal volume, decimal? takerBuy = null, long? trades = null) => new()
    {
        Symbol = "OF/USDT", Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = 100m, High = 101m, Low = 99m, Close = 100m,
        Volume = volume, TakerBuyVolume = takerBuy, TradeCount = trades,
    };

    [Fact]
    public void TakerImbalance_IsCenteredAndBounded()
    {
        // 3 candele: tutto buy (tb=vol) → +1; equilibrio (tb=vol/2) → 0; tutto sell (tb=0) → −1.
        var candles = new List<OhlcvData>
        {
            Candle(0, 100m, takerBuy: 100m),
            Candle(1, 100m, takerBuy: 50m),
            Candle(2, 100m, takerBuy: 0m),
        };

        var r = new TakerImbalanceFactor().Compute(candles, new Dictionary<string, decimal> { ["Lookback"] = 1m });

        Assert.Equal(1m, r[0]);
        Assert.Equal(0m, r[1]);
        Assert.Equal(-1m, r[2]);
    }

    [Fact]
    public void TakerImbalance_RollingMean_AveragesTheWindow()
    {
        // Due barre a +1 e due a −1, lookback 2: la media scorre 1, 0, −1.
        var candles = new List<OhlcvData>
        {
            Candle(0, 100m, takerBuy: 100m),
            Candle(1, 100m, takerBuy: 100m),
            Candle(2, 100m, takerBuy: 0m),
            Candle(3, 100m, takerBuy: 0m),
        };

        var r = new TakerImbalanceFactor().Compute(candles, new Dictionary<string, decimal> { ["Lookback"] = 2m });

        Assert.Null(r[0]);          // warm-up
        Assert.Equal(1m, r[1]);
        Assert.Equal(0m, r[2]);
        Assert.Equal(-1m, r[3]);
    }

    [Fact]
    public void MissingExtendedFields_YieldNull_NeverAFakeZero()
    {
        // La candela 1 non è stata reingerita (TakerBuyVolume null): ogni finestra che la tocca
        // deve dare null, MAI un valore inventato.
        var candles = new List<OhlcvData>
        {
            Candle(0, 100m, takerBuy: 60m),
            Candle(1, 100m, takerBuy: null),
            Candle(2, 100m, takerBuy: 60m),
            Candle(3, 100m, takerBuy: 60m),
        };

        var r = new TakerImbalanceFactor().Compute(candles, new Dictionary<string, decimal> { ["Lookback"] = 2m });

        Assert.Null(r[1]);
        Assert.Null(r[2]);          // finestra [1,2] tocca la candela senza dati
        Assert.NotNull(r[3]);       // finestra [2,3] è pulita
    }

    [Fact]
    public void AvgTradeSize_RelativeToItsOwnHistory()
    {
        // 4 barre con size media 1.0 (vol 100 / 100 trade), poi una barra con trade GROSSI
        // (vol 100 / 20 trade = size 5): il rapporto alla media della finestra deve saltare su.
        var candles = new List<OhlcvData>
        {
            Candle(0, 100m, trades: 100),
            Candle(1, 100m, trades: 100),
            Candle(2, 100m, trades: 100),
            Candle(3, 100m, trades: 100),
            Candle(4, 100m, trades: 20),
        };

        var r = new AvgTradeSizeFactor().Compute(candles, new Dictionary<string, decimal> { ["Lookback"] = 4m });

        Assert.Equal(0m, r[3]);                       // size costante = in linea con la storia
        Assert.NotNull(r[4]);
        Assert.True(r[4] > 1m, $"trade 5× più grossi della media devono dare un valore ben sopra zero, non {r[4]}");
    }

    [Fact]
    public void AvgTradeSize_ZeroTrades_YieldNull()
    {
        var candles = new List<OhlcvData>
        {
            Candle(0, 100m, trades: 100),
            Candle(1, 100m, trades: 0),
            Candle(2, 100m, trades: 100),
        };

        var r = new AvgTradeSizeFactor().Compute(candles, new Dictionary<string, decimal> { ["Lookback"] = 2m });
        Assert.All(r, v => Assert.Null(v));   // ogni finestra da 2 tocca la barra rotta o il warm-up
    }

    [Fact]
    public void Factory_ExposesAndRoundTrips_TheNewFactors()
    {
        var factory = new AlphaFactorFactory();

        Assert.Contains(factory.Prototypes, f => f.Name == "TakerImbalance");
        Assert.Contains(factory.Prototypes, f => f.Name == "AvgTradeSize");

        // Round-trip per nome: un SavedMlModel che li usa deve poterli ricostruire.
        Assert.IsType<TakerImbalanceFactor>(factory.Create("TakerImbalance"));
        Assert.IsType<AvgTradeSizeFactor>(factory.Create("AvgTradeSize"));
    }

    [Fact]
    public void AntiLookAhead_TruncationInvariance()
    {
        // Il contratto IAlphaFactor: value[i] identico su serie piena e troncata subito dopo i.
        var rnd = new Random(5);
        var full = Enumerable.Range(0, 60)
            .Select(i => Candle(i, 100m + i, takerBuy: (decimal)(rnd.NextDouble() * (100 + i)), trades: 50 + i))
            .ToList();
        var p = new Dictionary<string, decimal> { ["Lookback"] = 5m };

        var f1 = new TakerImbalanceFactor().Compute(full, p);
        var f2 = new TakerImbalanceFactor().Compute(full.Take(30).ToList(), p);
        for (var i = 0; i < 30; i++) Assert.Equal(f1[i], f2[i]);

        var g1 = new AvgTradeSizeFactor().Compute(full, p);
        var g2 = new AvgTradeSizeFactor().Compute(full.Take(30).ToList(), p);
        for (var i = 0; i < 30; i++) Assert.Equal(g1[i], g2[i]);
    }
}
