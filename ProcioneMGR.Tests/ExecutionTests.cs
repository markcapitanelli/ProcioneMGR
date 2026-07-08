using ProcioneMGR.Data;
using ProcioneMGR.Services.Execution;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test del layer di esecuzione (rif. <c>docs/ROADMAP-QLIB.md §1.2</c>): i piani conservano ESATTAMENTE
/// la quantità totale, VWAP segue il profilo di volume, e il simulatore mostra la tesi centrale —
/// distribuire l'ordine (TWAP/VWAP) riduce l'implementation shortfall rispetto all'esecuzione
/// immediata quando la size è significativa. Il default "Immediate" resta il comportamento odierno.
/// </summary>
public class ExecutionTests
{
    private readonly ExecutionParameters _p = new();

    private static List<OhlcvData> Fine(int n, decimal price, decimal volume)
    {
        var list = new List<OhlcvData>(n);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            list.Add(new OhlcvData
            {
                Symbol = "BTCUSDT",
                Timeframe = "5m",
                TimestampUtc = t0.AddMinutes(5 * i),
                Open = price,
                High = price + 0.5m,
                Low = price - 0.5m,
                Close = price,
                Volume = volume,
            });
        }
        return list;
    }

    private static List<OhlcvData> FineWithCloses(IReadOnlyList<decimal> closes, decimal volume)
    {
        var list = new List<OhlcvData>(closes.Count);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < closes.Count; i++)
        {
            var price = closes[i];
            list.Add(new OhlcvData
            {
                Symbol = "BTCUSDT",
                Timeframe = "5m",
                TimestampUtc = t0.AddMinutes(5 * i),
                Open = price,
                High = price + 0.5m,
                Low = price - 0.5m,
                Close = price,
                Volume = volume,
            });
        }
        return list;
    }

    // --- Piani: conservazione della quantità ------------------------------------------------

    [Fact]
    public void Immediate_IsSingleFullOrder()
    {
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Buy, 100m, 100m);
        var plan = new ImmediateExecutionAlgorithm().BuildPlan(intent, Fine(12, 100m, 1000m), _p);
        Assert.Equal("Immediate", plan.Algorithm);
        Assert.Single(plan.Slices);
        Assert.Equal(100m, plan.Slices[0].Quantity);
    }

    [Theory]
    [InlineData("Twap")]
    [InlineData("Vwap")]
    [InlineData("Iceberg")]
    public void EveryAlgorithm_PreservesTotalQuantityExactly(string algo)
    {
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Sell, 100m, 100m); // 100/3 non termina in decimal
        IExecutionAlgorithm a = new ExecutionAlgorithmFactory().Create(algo);
        var plan = a.BuildPlan(intent, Fine(12, 100m, 1000m), new ExecutionParameters { MaxSlices = 3, IcebergClipFraction = 0.3m });
        Assert.Equal(100m, plan.Slices.Sum(s => s.Quantity));
        Assert.All(plan.Slices, s => Assert.InRange(s.CandleIndex, 0, 11));
    }

    [Fact]
    public void Twap_UsesRequestedNumberOfSlices()
    {
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Buy, 120m, 100m);
        var plan = new TwapExecutionAlgorithm().BuildPlan(intent, Fine(12, 100m, 1000m), new ExecutionParameters { MaxSlices = 6 });
        Assert.Equal(6, plan.SliceCount);
        Assert.Equal(120m, plan.Slices.Sum(s => s.Quantity));
    }

    [Fact]
    public void Vwap_ConcentratesOnHighVolumeCandles()
    {
        var candles = Fine(6, 100m, 100m);
        candles[3].Volume = 900m; // candela ad alta liquidità
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Buy, 100m, 100m);
        var plan = new VwapExecutionAlgorithm().BuildPlan(intent, candles, _p);

        Assert.Equal(100m, plan.Slices.Sum(s => s.Quantity));
        var biggest = plan.Slices.MaxBy(s => s.Quantity)!;
        Assert.Equal(3, biggest.CandleIndex); // la fetta maggiore è sulla candela a volume più alto
    }

    [Fact]
    public void Iceberg_SplitsIntoClipsOfConfiguredSize()
    {
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Buy, 100m, 100m);
        var plan = new IcebergExecutionAlgorithm().BuildPlan(intent, Fine(20, 100m, 1000m), new ExecutionParameters { IcebergClipFraction = 0.25m });
        Assert.Equal(4, plan.SliceCount);          // 100 / (25) = 4 clip
        Assert.Equal(100m, plan.Slices.Sum(s => s.Quantity));
        Assert.All(plan.Slices, s => Assert.Equal(25m, s.Quantity));
    }

    // --- Adaptive (Almgren-Chriss semplificato) ---------------------------------------------

    [Fact]
    public void Adaptive_PreservesTotalQuantityExactly()
    {
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Sell, 100m, 100m); // 100/3 non termina in decimal
        var candles = Fine(12, 100m, 1000m);
        candles[3].Volume = 900m; // profilo di volume non uniforme
        var plan = new AdaptiveExecutionAlgorithm().BuildPlan(intent, candles, new ExecutionParameters { MaxSlices = 3 });
        Assert.Equal(100m, plan.Slices.Sum(s => s.Quantity));
        Assert.All(plan.Slices, s => Assert.InRange(s.CandleIndex, 0, 11));
    }

    [Fact]
    public void Adaptive_DegradesToVwapLike_WhenVolatilityIsZero()
    {
        var candles = Fine(6, 100m, 100m); // Close costante -> volatilità realizzata nulla
        candles[3].Volume = 900m; // candela ad alta liquidità
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Buy, 100m, 100m);
        var plan = new AdaptiveExecutionAlgorithm().BuildPlan(intent, candles, _p);

        Assert.Equal(100m, plan.Slices.Sum(s => s.Quantity));
        var biggest = plan.Slices.MaxBy(s => s.Quantity)!;
        Assert.Equal(3, biggest.CandleIndex); // come VWAP puro, segue il profilo di volume
    }

    [Fact]
    public void Adaptive_FrontLoadsMore_WithHigherVolatility()
    {
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Buy, 120m, 100m);
        var lowVolCloses = new List<decimal> { 100m, 100.1m, 100m, 100.1m, 100m, 100.1m, 100m, 100.1m, 100m, 100.1m, 100m, 100.1m };
        var highVolCloses = new List<decimal> { 100m, 110m, 95m, 108m, 92m, 112m, 90m, 115m, 88m, 118m, 85m, 120m };
        var lowVolCandles = FineWithCloses(lowVolCloses, 1000m); // volume uniforme in entrambi i casi
        var highVolCandles = FineWithCloses(highVolCloses, 1000m);

        var algo = new AdaptiveExecutionAlgorithm();
        var lowPlan = algo.BuildPlan(intent, lowVolCandles, _p);
        var highPlan = algo.BuildPlan(intent, highVolCandles, _p);

        var half = lowVolCandles.Count / 2;
        decimal FirstHalfShare(ExecutionPlan plan) =>
            plan.Slices.Where(s => s.CandleIndex < half).Sum(s => s.Quantity) / plan.PlannedQuantity;

        Assert.True(FirstHalfShare(highPlan) > FirstHalfShare(lowPlan),
            $"Quota prima metà alta volatilità {FirstHalfShare(highPlan):P1} atteso > bassa volatilità {FirstHalfShare(lowPlan):P1}");
    }

    [Fact]
    public void Adaptive_FallsBackToTwap_WhenVolumeIsZero()
    {
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Buy, 120m, 100m);
        var candles = Fine(12, 100m, 0m); // nessun volume -> nessun profilo utile
        var parameters = new ExecutionParameters { MaxSlices = 6 };

        var adaptivePlan = new AdaptiveExecutionAlgorithm().BuildPlan(intent, candles, parameters);
        var twapPlan = new TwapExecutionAlgorithm().BuildPlan(intent, candles, parameters);

        Assert.Equal(twapPlan.SliceCount, adaptivePlan.SliceCount);
        Assert.Equal(120m, adaptivePlan.Slices.Sum(s => s.Quantity));
    }

    [Fact]
    public void Adaptive_SingleCandle_ReturnsSingleSlice_NoThrow()
    {
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Buy, 100m, 100m);
        var plan = new AdaptiveExecutionAlgorithm().BuildPlan(intent, Fine(1, 100m, 1000m), _p);
        Assert.Equal("Adaptive", plan.Algorithm);
        Assert.Single(plan.Slices);
        Assert.Equal(100m, plan.Slices[0].Quantity);
    }

    // --- Simulatore: la tesi misurabile -----------------------------------------------------

    [Fact]
    public void Simulator_Twap_ReducesShortfall_VsImmediate_ForLargeBuy()
    {
        var candles = Fine(12, 100m, 1000m);
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Buy, 120m, 100m); // size non banale
        var sim = new ExecutionSimulator();

        var immediate = sim.Simulate(new ImmediateExecutionAlgorithm().BuildPlan(intent, candles, _p), intent, candles, _p);
        var twap = sim.Simulate(new TwapExecutionAlgorithm().BuildPlan(intent, candles, _p), intent, candles, _p);

        // Entrambi sono un COSTO per un buy (prezzo medio sopra l'arrivo)…
        Assert.True(immediate.AverageFillPrice > intent.ArrivalPrice);
        Assert.True(twap.AverageFillPrice > intent.ArrivalPrice);
        // …ma spargere l'ordine riduce l'impatto: TWAP costa meno dell'immediato.
        Assert.True(twap.SlippageBps < immediate.SlippageBps,
            $"TWAP {twap.SlippageBps:F1}bps atteso < Immediate {immediate.SlippageBps:F1}bps");
        Assert.Equal(120m, twap.FilledQuantity);
    }

    [Fact]
    public void Simulator_Sell_CostIsBelowArrival()
    {
        var candles = Fine(12, 100m, 1000m);
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Sell, 120m, 100m);
        var sim = new ExecutionSimulator();
        var res = sim.Simulate(new ImmediateExecutionAlgorithm().BuildPlan(intent, candles, _p), intent, candles, _p);

        Assert.True(res.AverageFillPrice < intent.ArrivalPrice); // vendendo si incassa meno del prezzo di arrivo
        Assert.True(res.SlippageBps > 0m);                        // lo shortfall è segnato come costo
    }
}
