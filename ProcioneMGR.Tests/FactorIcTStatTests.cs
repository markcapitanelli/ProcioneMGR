using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Tests;

/// <summary>
/// R1.3 — significatività dell'IC con t-stat Newey-West (HAC), robusta all'autocorrelazione dei
/// forward-return sovrapposti (horizon &gt; 1). Verifica che l'overlap ABBASSA la significatività
/// (|t_NW| &lt; |t_ingenua|), come atteso, e che l'evaluator popola i campi.
/// </summary>
public class FactorIcTStatTests
{
    [Fact]
    public void NeweyWest_WithOverlap_LowersSignificanceVsZeroLag()
    {
        const int n = 500, h = 5;
        var rnd = new Random(3);
        var r = new double[n + h];
        for (var i = 0; i < r.Length; i++) r[i] = rnd.NextDouble() - 0.5; // rendimenti iid

        // Forward-return sovrapposti su h passi → autocorrelazione MA(h-1).
        var y = new double[n];
        for (var i = 0; i < n; i++) { double sum = 0; for (var k = 0; k < h; k++) sum += r[i + k]; y[i] = sum; }

        // Fattore correlato col forward-return (eredita l'overlap).
        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = 0.4 * y[i] + (rnd.NextDouble() - 0.5) * 0.3;

        var tZeroLag = Correlation.SpearmanTStatNeweyWest(x, y, lags: 0);
        var tOverlap = Correlation.SpearmanTStatNeweyWest(x, y, lags: h - 1);

        Assert.True(tZeroLag > 0 && tOverlap > 0, $"t0={tZeroLag}, tL={tOverlap}");
        Assert.True(Math.Abs(tOverlap) < Math.Abs(tZeroLag),
            $"la correzione overlap deve ridurre |t|: |{tOverlap:F2}| < |{tZeroLag:F2}|");
    }

    [Fact]
    public void NeweyWest_NoOverlap_CloseToIndependentTStat()
    {
        const int n = 400;
        var rnd = new Random(9);
        var x = new double[n];
        var y = new double[n];
        for (var i = 0; i < n; i++) { x[i] = rnd.NextDouble(); y[i] = 0.5 * x[i] + (rnd.NextDouble() - 0.5) * 0.6; }

        var ic = Correlation.Spearman(x, y);
        var tNw = Correlation.SpearmanTStatNeweyWest(x, y, lags: 0);
        var tNaive = Correlation.TStatIndependent(ic, n);

        // Senza overlap (lag 0) le due stime sono nello stesso ordine di grandezza.
        Assert.True(tNw > 3 && tNaive > 3);
        Assert.InRange(tNw / tNaive, 0.6, 1.6);
    }

    [Fact]
    public void DegenerateSeries_ReturnsZero()
    {
        var x = Enumerable.Repeat(1.0, 50).ToList();
        var y = Enumerable.Range(0, 50).Select(i => (double)i).ToList();
        Assert.Equal(0d, Correlation.SpearmanTStatNeweyWest(x, y, 2));
    }

    // ------------------------------------------------------------------ evaluator end-to-end

    /// <summary>Fattore di test: valore = close (perfettamente crescente col prezzo).</summary>
    private sealed class CloseFactor : IAlphaFactor
    {
        public string Name => "CloseFactor";
        public string DisplayName => "Close";
        public FactorCategory Category => FactorCategory.Technical;
        public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions => [];
        public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> parameters)
            => candles.Select(c => (decimal?)c.Close).ToList();
    }

    [Fact]
    public void Evaluator_PopulatesNeweyWestFields()
    {
        var t0 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<OhlcvData>();
        var rnd = new Random(5);
        var price = 100m;
        for (var i = 0; i < 300; i++)
        {
            price = Math.Max(1m, price + (decimal)((rnd.NextDouble() - 0.45) * 2));
            candles.Add(new OhlcvData { Symbol = "T/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(i), Open = price, High = price + 1, Low = price - 1, Close = price, Volume = 10m });
        }

        var evaluator = new FactorEvaluator();
        var res = evaluator.Evaluate(new CloseFactor(), candles, new Dictionary<string, decimal>(),
            new FactorEvaluationConfig { ForwardHorizon = 5 });

        Assert.Equal(4, res.NeweyWestLags); // horizon-1
        Assert.NotEqual(0d, res.IcTStatistic);
        Assert.NotEqual(0d, res.IcTStatisticNaive);
    }
}
