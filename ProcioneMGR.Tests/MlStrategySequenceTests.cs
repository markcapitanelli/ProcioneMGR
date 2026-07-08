using Microsoft.ML;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica l'innesto dei modelli SEQUENZIALI (attention) nel backtest tramite
/// <see cref="MlStrategy"/>: la strategia riconosce l'<see cref="ISequencePredictor"/> e costruisce
/// la finestra degli ultimi T timestep a inferenza (nessun buffer stateful). Test di non-regressione
/// del cablaggio: warm-up → Hold, nessuna eccezione lungo la serie, segnali deterministici.
///
/// Assunzione (come per gli altri modelli): dopo il warm-up i fattori sono completi in modo
/// contiguo, così la finestra costruita per indice di candela coincide col layout di training.
/// </summary>
public class MlStrategySequenceTests
{
    private static List<OhlcvData> MakeCandles(int n, int seed = 11)
    {
        var rnd = new Random(seed);
        var list = new List<OhlcvData>(n);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var price = 100m;
        for (var i = 0; i < n; i++)
        {
            var open = price;
            price = Math.Max(1m, open + (decimal)((rnd.NextDouble() - 0.48) * 2));
            list.Add(new OhlcvData
            {
                Symbol = "T", Timeframe = "1h", TimestampUtc = t0.AddHours(i),
                Open = open, High = Math.Max(open, price) + 0.2m, Low = Math.Min(open, price) - 0.2m, Close = price,
                Volume = 100m + i % 10,
            });
        }
        return list;
    }

    [Fact]
    public async Task MlStrategy_WithAttention_RunsAcrossSeries_WarmupIsHold()
    {
        var candles = MakeCandles(300);
        var factory = new AlphaFactorFactory();
        var factors = new List<FactorSpec>
        {
            new("Momentum", factory.Create("Momentum"), new Dictionary<string, decimal> { ["Lookback"] = 10m, ["Skip"] = 0m }),
            new("RsiFactor", factory.Create("RsiFactor"), new Dictionary<string, decimal> { ["Period"] = 14m }),
        };

        var mlContext = new MLContext(seed: 1);
        var dataset = new DatasetBuilder().Build(candles, factors, forwardHorizon: 1);
        const int window = 5;
        var windowed = SequenceWindowing.Build(dataset, window);

        var predictor = new AttentionReturnPredictor(windowLength: window, epochs: 40, seed: 42);
        predictor.Fit(mlContext, windowed.ToDataView(mlContext));

        var strategy = new MlStrategy(predictor, factors);
        await strategy.InitializeAsync(
            candles.Select(c => c.Close).ToList(), candles,
            new Dictionary<string, decimal> { ["LongThreshold"] = 0.002m, ["ShortThreshold"] = 0.002m },
            indicators: null!, CancellationToken.None);

        // Nessuna eccezione su tutta la serie; ogni indice restituisce un segnale valido.
        var signals = new List<Signal>();
        for (var i = 0; i < candles.Count; i++)
        {
            var s = strategy.EvaluateSignal(i, candles[i].Close, candles[i].TimestampUtc);
            signals.Add(s);
        }

        // I primi indici (finestra non ancora completa) sono Hold.
        Assert.All(signals.Take(window - 1), s => Assert.Equal(Signal.Hold, s));

        // Determinismo: rivalutare dà lo stesso segnale.
        var mid = candles.Count - 5;
        var first = strategy.EvaluateSignal(mid, candles[mid].Close, candles[mid].TimestampUtc);
        var again = strategy.EvaluateSignal(mid, candles[mid].Close, candles[mid].TimestampUtc);
        Assert.Equal(first, again);
    }
}
