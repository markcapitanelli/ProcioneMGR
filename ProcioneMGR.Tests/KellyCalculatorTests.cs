using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Risk;

namespace ProcioneMGR.Tests;

/// <summary>Test del criterio di Kelly (Jansen ML4T, cap. 5).</summary>
public class KellyCalculatorTests
{
    [Fact]
    public void BinaryKelly_HandComputed()
    {
        // p=0.6, b=1: f* = 0.6 - 0.4/1 = 0.2.
        Assert.Equal(0.2m, KellyCalculator.BinaryKelly(0.6m, 1m));
        // p=0.5, b=2: f* = 0.5 - 0.5/2 = 0.25.
        Assert.Equal(0.25m, KellyCalculator.BinaryKelly(0.5m, 2m));
        // Edge negativo -> 0 (mai scommettere).
        Assert.Equal(0m, KellyCalculator.BinaryKelly(0.4m, 1m));
        // Input degeneri -> 0.
        Assert.Equal(0m, KellyCalculator.BinaryKelly(0.6m, 0m));
        Assert.Equal(0m, KellyCalculator.BinaryKelly(1m, 2m));
    }

    [Fact]
    public void FromTradeHistory_UsesWinRateAndPayoff()
    {
        // 6 vincite da +200, 4 perdite da -100: p=0.6, b=2 -> f* = 0.6 - 0.4/2 = 0.4.
        var trades = new List<BacktestTrade>();
        for (var i = 0; i < 6; i++) trades.Add(new BacktestTrade { Pnl = 200m });
        for (var i = 0; i < 4; i++) trades.Add(new BacktestTrade { Pnl = -100m });

        var suggestion = new KellyCalculator().FromTradeHistory(trades);

        Assert.Equal(0.6m, suggestion.WinProbability);
        Assert.Equal(2m, suggestion.PayoffRatio);
        Assert.Equal(0.4m, suggestion.KellyFraction);
        Assert.Equal(0.2m, suggestion.HalfKelly);
        Assert.Equal(10, suggestion.DecidedTrades);
    }

    [Fact]
    public void FromTradeHistory_NoLosses_ReturnsZero()
    {
        // Senza perdite (o senza vincite) la stima non e' affidabile -> 0.
        var allWins = Enumerable.Range(0, 5).Select(_ => new BacktestTrade { Pnl = 100m }).ToList();
        Assert.Equal(0m, new KellyCalculator().FromTradeHistory(allWins).KellyFraction);
        Assert.Equal(0m, new KellyCalculator().FromTradeHistory([]).KellyFraction);
    }

    [Fact]
    public void ContinuousKelly_ClosedForm()
    {
        // f* = mu / sigma^2: 0.058 / 0.216^2 = 1.2431...
        var f = KellyCalculator.ContinuousKelly(0.058m, 0.216m);
        Assert.InRange(f, 1.24m, 1.25m);
        // Media negativa -> 0.
        Assert.Equal(0m, KellyCalculator.ContinuousKelly(-0.01m, 0.2m));
    }

    [Fact]
    public void ContinuousKellyNumeric_MatchesBookExample()
    {
        // Esempio del libro (cap. 5): m=0.058, s=0.216 -> f* = 1.1974.
        var f = KellyCalculator.ContinuousKellyNumeric(0.058, 0.216);
        Assert.InRange(f, 1.17, 1.22);
    }

    [Fact]
    public void MultiAssetKelly_PrefersHigherSharpeAsset()
    {
        // Due asset indipendenti: A con media alta e varianza bassa, B con media bassa e
        // varianza alta -> il peso (normalizzato) di A deve dominare.
        var rnd = new Random(42);
        var a = new List<double>();
        var b = new List<double>();
        for (var i = 0; i < 500; i++)
        {
            a.Add(0.002 + Gaussian(rnd) * 0.01);
            b.Add(0.0005 + Gaussian(rnd) * 0.03);
        }

        var weights = new KellyCalculator().MultiAssetKelly([a, b]);

        Assert.Equal(2, weights.Count);
        Assert.Equal(1m, Math.Abs(weights[0]) + Math.Abs(weights[1]), precision: 6);
        Assert.True(weights[0] > Math.Abs(weights[1]),
            $"atteso |peso A| dominante, trovato A={weights[0]}, B={weights[1]}");
    }

    [Fact]
    public void MultiAssetKelly_InvalidInput_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new KellyCalculator().MultiAssetKelly([[0.1, 0.2], [0.1]]));
    }

    private static double Gaussian(Random rnd)
    {
        // Box-Muller.
        var u1 = 1.0 - rnd.NextDouble();
        var u2 = rnd.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
