using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Risk;

namespace ProcioneMGR.Tests;

/// <summary>Test del consulente per la leva (bootstrap con pavimento di liquidazione).</summary>
public class LeverageAdvisorTests
{
    private static List<BacktestTrade> TradesFromReturns(params decimal[] pnlPercents)
        => pnlPercents.Select(p => new BacktestTrade { Pnl = p, PnlPercent = p }).ToList();

    /// <summary>Sistema con edge: 55% di +2% sul nozionale, 45% di -1.5%.</summary>
    private static List<BacktestTrade> EdgeSystem()
    {
        var trades = new List<BacktestTrade>();
        for (var i = 0; i < 55; i++) trades.Add(new BacktestTrade { Pnl = 2m, PnlPercent = 2m });
        for (var i = 0; i < 45; i++) trades.Add(new BacktestTrade { Pnl = -1.5m, PnlPercent = -1.5m });
        return trades;
    }

    [Fact]
    public void Advise_TooFewTrades_Warns()
    {
        var advice = new LeverageAdvisor().Advise(TradesFromReturns(1m, -1m, 2m));
        Assert.NotNull(advice.Warning);
        Assert.Equal(1m, advice.RecommendedLeverage);
    }

    [Fact]
    public void Advise_RiskGrowsWithLeverage()
    {
        var advice = new LeverageAdvisor().Advise(EdgeSystem());

        Assert.Null(advice.Warning);
        Assert.Equal(6, advice.Scenarios.Count);

        // La probabilita' di dimezzamento e la quota di liquidazioni sono monotone nella leva.
        for (var i = 1; i < advice.Scenarios.Count; i++)
        {
            Assert.True(advice.Scenarios[i].HalvingProbability >= advice.Scenarios[i - 1].HalvingProbability,
                $"P(halving) deve crescere con la leva: {advice.Scenarios[i - 1].Leverage}x -> {advice.Scenarios[i].Leverage}x");
            Assert.True(advice.Scenarios[i].LiquidationRate >= advice.Scenarios[i - 1].LiquidationRate);
        }

        // Con un edge moderato, a leva bassa non ci si dimezza quasi mai; a 20x spesso.
        Assert.True(advice.Scenarios[0].HalvingProbability < 0.05m);
        Assert.True(advice.Scenarios[^1].HalvingProbability > advice.Scenarios[0].HalvingProbability);
    }

    [Fact]
    public void Advise_RecommendationRespectsHalvingTolerance()
    {
        var advice = new LeverageAdvisor().Advise(EdgeSystem(), halvingTolerance: 0.10m);
        var recommendedRow = advice.Scenarios.Single(s => s.Leverage == advice.RecommendedLeverage);
        Assert.True(recommendedRow.HalvingProbability <= 0.10m,
            $"la leva consigliata ({advice.RecommendedLeverage}x) viola la tolleranza: P(halving)={recommendedRow.HalvingProbability}");
    }

    [Fact]
    public void Advise_NegativeEdge_RecommendsMinimumLeverage()
    {
        // Sistema perdente: nessuna leva e' consigliabile oltre il minimo accettabile.
        var trades = new List<BacktestTrade>();
        for (var i = 0; i < 40; i++) trades.Add(new BacktestTrade { Pnl = 1m, PnlPercent = 1m });
        for (var i = 0; i < 60; i++) trades.Add(new BacktestTrade { Pnl = -1.5m, PnlPercent = -1.5m });

        var advice = new LeverageAdvisor().Advise(trades);
        // Con edge negativo la crescita mediana e' < 1 ovunque e cala con la leva:
        // la raccomandazione deve restare sulla leva piu' bassa accettabile.
        Assert.Equal(advice.Scenarios.Where(s => s.HalvingProbability <= 0.10m).Min(s => s.Leverage),
            advice.RecommendedLeverage);
        Assert.True(advice.Scenarios[0].MedianGrowth < 1m);
    }

    [Fact]
    public void Advise_Deterministic_WithSeed()
    {
        var a1 = new LeverageAdvisor().Advise(EdgeSystem(), seed: 7);
        var a2 = new LeverageAdvisor().Advise(EdgeSystem(), seed: 7);
        Assert.Equal(a1.RecommendedLeverage, a2.RecommendedLeverage);
        Assert.Equal(a1.Scenarios.Select(s => s.MedianGrowth), a2.Scenarios.Select(s => s.MedianGrowth));
    }
}
