using ProcioneMGR.Services.Risk;

namespace ProcioneMGR.Tests;

/// <summary>
/// MarginMath è condivisa tra BacktestEngine e TradingEngine: se questa formula è sbagliata,
/// sia il backtest sia il monitoraggio live del rischio di liquidazione sarebbero sbagliati
/// nello stesso modo. Valori attesi calcolati indipendentemente a mano.
/// </summary>
public class MarginMathTests
{
    [Fact]
    public void LiquidationPrice_Long_10x_MatchesHandComputation()
    {
        // entry=100, qty=10, notional=1000 (10x su margine=100), maintenance=0.5%
        // buffer = (100 - 0.005*1000) / 10 = (100-5)/10 = 9.5 -> liq = 100 - 9.5 = 90.5
        var liq = MarginMath.LiquidationPrice(entryPrice: 100m, quantity: 10m, margin: 100m, notional: 1000m, isLong: true, maintenanceMarginFraction: 0.005m);
        Assert.Equal(90.5m, liq);
    }

    [Fact]
    public void LiquidationPrice_Short_10x_MatchesHandComputation()
    {
        // Stesso scenario ma short: liq = 100 + 9.5 = 109.5
        var liq = MarginMath.LiquidationPrice(entryPrice: 100m, quantity: 10m, margin: 100m, notional: 1000m, isLong: false, maintenanceMarginFraction: 0.005m);
        Assert.Equal(109.5m, liq);
    }

    [Fact]
    public void LiquidationPrice_HigherLeverage_IsCloserToEntry()
    {
        // A parità di prezzo/margine di mantenimento, più leva -> meno margine per unità di
        // nozionale -> liquidazione più vicina all'entry (più rischiosa).
        var liq5x = MarginMath.LiquidationPrice(entryPrice: 100m, quantity: 5m, margin: 100m, notional: 500m, isLong: true, maintenanceMarginFraction: 0.005m);
        var liq20x = MarginMath.LiquidationPrice(entryPrice: 100m, quantity: 20m, margin: 100m, notional: 2000m, isLong: true, maintenanceMarginFraction: 0.005m);
        Assert.True(liq20x > liq5x, "Con leva più alta la liquidazione (long) deve essere più vicina all'entry (prezzo di liquidazione più alto).");
    }

    [Fact]
    public void LiquidationPrice_ZeroQuantity_ReturnsZero()
    {
        Assert.Equal(0m, MarginMath.LiquidationPrice(100m, 0m, 100m, 0m, true, 0.005m));
    }

    [Fact]
    public void LiquidationDistanceFraction_10x_MatchesHandComputation()
    {
        // 1/10 - 0.005 = 0.095
        var f = MarginMath.LiquidationDistanceFraction(leverage: 10m, maintenanceMarginFraction: 0.005m);
        Assert.Equal(0.095m, f);
    }

    [Fact]
    public void LiquidationDistanceFraction_NonPositiveLeverage_ReturnsMaxCaution()
    {
        Assert.Equal(1m, MarginMath.LiquidationDistanceFraction(0m, 0.005m));
        Assert.Equal(1m, MarginMath.LiquidationDistanceFraction(-5m, 0.005m));
    }
}
