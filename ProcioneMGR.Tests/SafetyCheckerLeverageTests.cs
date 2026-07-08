using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Copre il check #10 di SafetyChecker.Evaluate (leva massima per Futures) e verifica che lo
/// Spot resti invariato: nessun controllo di leva si applica quando MarketType è Spot, anche se
/// per qualche motivo Order.Leverage fosse valorizzato oltre il limite.
/// </summary>
public class SafetyCheckerLeverageTests
{
    private static TradingEngineStatus BaseStatus() => new()
    {
        TotalCapital = 1000m,
        UsedCapital = 0m,
        DailyPnl = 0m,
        MaxDrawdown = 0m,
        OpenPositionCount = 0,
        IsEmergencyStopped = false,
    };

    [Fact]
    public void Futures_LeverageWithinLimit_IsAllowed()
    {
        var cfg = new SafetyConfiguration { MaxLeverageAllowed = 5, MaxPositionSizePercent = 100m, MaxTotalExposurePercent = 100m };
        // notional piccolo per non far scattare altri check: qty basso.
        var order = new Order { Symbol = "BTC/USDT", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 0.001m, Price = 50000m, Mode = TradingMode.Paper, MarketType = MarketType.Futures, Leverage = 5 };
        var result = SafetyChecker.Evaluate(order, BaseStatus(), cfg, DateTime.UtcNow);
        Assert.True(result.IsAllowed, string.Join("; ", result.Violations));
    }

    [Fact]
    public void Futures_LeverageOverLimit_IsRejected()
    {
        var cfg = new SafetyConfiguration { MaxLeverageAllowed = 5, MaxPositionSizePercent = 100m, MaxTotalExposurePercent = 100m };
        var order = new Order { Symbol = "BTC/USDT", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 0.001m, Price = 50000m, Mode = TradingMode.Paper, MarketType = MarketType.Futures, Leverage = 10 };
        var result = SafetyChecker.Evaluate(order, BaseStatus(), cfg, DateTime.UtcNow);
        Assert.False(result.IsAllowed);
        Assert.Contains(result.Violations, v => v.Contains("Leva"));
    }

    [Fact]
    public void Futures_LeverageExactlyAtLimit_IsAllowed()
    {
        var cfg = new SafetyConfiguration { MaxLeverageAllowed = 5, MaxPositionSizePercent = 100m, MaxTotalExposurePercent = 100m };
        var order = new Order { Symbol = "BTC/USDT", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 0.001m, Price = 50000m, Mode = TradingMode.Paper, MarketType = MarketType.Futures, Leverage = 5 };
        var result = SafetyChecker.Evaluate(order, BaseStatus(), cfg, DateTime.UtcNow);
        Assert.True(result.IsAllowed, string.Join("; ", result.Violations));
    }

    [Fact]
    public void Spot_LeverageFieldIgnored_EvenIfAboveLimit()
    {
        // Lo Spot ha sempre leva implicita 1x: il check #10 si applica SOLO se MarketType==Futures.
        var cfg = new SafetyConfiguration { MaxLeverageAllowed = 5, MaxPositionSizePercent = 100m, MaxTotalExposurePercent = 100m };
        var order = new Order { Symbol = "BTC/USDT", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 0.001m, Price = 50000m, Mode = TradingMode.Paper, MarketType = MarketType.Spot, Leverage = 50 };
        var result = SafetyChecker.Evaluate(order, BaseStatus(), cfg, DateTime.UtcNow);
        Assert.True(result.IsAllowed, string.Join("; ", result.Violations));
    }

    [Fact]
    public void Futures_LeverageViolation_DoesNotTriggerEmergencyStop()
    {
        // La leva eccessiva è un rifiuto "normale", non una condizione critica come daily-loss/drawdown.
        var cfg = new SafetyConfiguration { MaxLeverageAllowed = 3, MaxPositionSizePercent = 100m, MaxTotalExposurePercent = 100m };
        var order = new Order { Symbol = "BTC/USDT", Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 0.001m, Price = 50000m, Mode = TradingMode.Paper, MarketType = MarketType.Futures, Leverage = 20 };
        var result = SafetyChecker.Evaluate(order, BaseStatus(), cfg, DateTime.UtcNow);
        Assert.False(result.IsAllowed);
        Assert.False(result.RequiresEmergencyStop);
    }
}
