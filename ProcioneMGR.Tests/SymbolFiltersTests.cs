using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Tests;

/// <summary>Arrotondamento quantità/prezzo ai filtri LOT_SIZE/PRICE_FILTER (anti -1100).</summary>
public class SymbolFiltersTests
{
    [Fact]
    public void RoundQuantity_FloorsToStepSize()
    {
        var f = new SymbolFilters { StepSize = 0.00001m };
        Assert.Equal(0.01369m, f.RoundQuantity(0.0136958159m));
    }

    [Fact]
    public void RoundQuantity_CoarseStep()
    {
        var f = new SymbolFilters { StepSize = 0.001m };
        Assert.Equal(1.234m, f.RoundQuantity(1.2349m));
    }

    [Fact]
    public void RoundQuantity_NoStep_ReturnsAsIs()
    {
        var f = new SymbolFilters { StepSize = 0m };
        Assert.Equal(1.23456789m, f.RoundQuantity(1.23456789m));
    }

    [Fact]
    public void RoundPrice_FloorsToTick()
    {
        var f = new SymbolFilters { TickSize = 0.01m };
        Assert.Equal(58412.34m, f.RoundPrice(58412.347m));
    }

    [Fact]
    public void IsTradable_EnforcesMinQtyAndMinNotional()
    {
        var f = new SymbolFilters { MinQty = 0.0001m, MinNotional = 5m };
        Assert.True(f.IsTradable(0.0001m, 50_000m));   // notional 5, qty ok
        Assert.False(f.IsTradable(0.00001m, 50_000m)); // sotto minQty
        Assert.False(f.IsTradable(0.0001m, 10m));      // notional 0.001 < 5
    }
}
