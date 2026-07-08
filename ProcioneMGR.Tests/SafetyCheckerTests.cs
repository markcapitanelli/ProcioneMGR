using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica che OGNI safety check rifiuti correttamente l'ordine pericoloso e che un
/// ordine valido passi. Logica pura (SafetyChecker.Evaluate), deterministica.
/// </summary>
public class SafetyCheckerTests
{
    private static readonly DateTime Now = new(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);

    private static SafetyConfiguration Cfg() => new()
    {
        MaxPositionSizePercent = 10m,
        MaxTotalExposurePercent = 50m,
        MaxDailyLossPercent = 5m,
        MaxDrawdownPercent = 20m,
        MaxOpenPositions = 5,
        MinOrderIntervalSeconds = 10,
        RequireManualConfirmationForLive = true,
    };

    // Ordine valido baseline: notional 500 (5% di 10.000), Paper.
    private static Order ValidOrder() => new()
    {
        Symbol = "BTC/USDT", Side = OrderSide.Buy, Type = OrderType.Market,
        Quantity = 0.01m, Price = 50_000m, Mode = TradingMode.Paper,
    };

    private static TradingEngineStatus OkStatus() => new()
    {
        TotalCapital = 10_000m, UsedCapital = 0m, DailyPnl = 0m, MaxDrawdown = 0m,
        OpenPositionCount = 0, LastOrderUtc = Now.AddSeconds(-60), IsEmergencyStopped = false,
    };

    [Fact]
    public void ValidOrder_IsAllowed()
    {
        var r = SafetyChecker.Evaluate(ValidOrder(), OkStatus(), Cfg(), Now);
        Assert.True(r.IsAllowed, string.Join("; ", r.Violations));
        Assert.Empty(r.Violations);
        Assert.False(r.RequiresEmergencyStop);
    }

    [Fact]
    public void PositionTooLarge_IsRejected()
    {
        var o = ValidOrder();
        o.Quantity = 0.05m; // notional 2.500 = 25% > 10%
        var r = SafetyChecker.Evaluate(o, OkStatus(), Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.Contains(r.Violations, v => v.Contains("troppo grande"));
        Assert.False(r.RequiresEmergencyStop); // non critica
    }

    [Fact]
    public void TotalExposureExceeded_IsRejected()
    {
        var status = OkStatus();
        status.UsedCapital = 4_800m; // + 500 = 5.300 > 50% (5.000)
        var r = SafetyChecker.Evaluate(ValidOrder(), status, Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.Contains(r.Violations, v => v.Contains("Esposizione"));
    }

    [Fact]
    public void DailyLossExceeded_IsRejected_AndTriggersEmergencyStop()
    {
        var status = OkStatus();
        status.DailyPnl = -600m; // 6% > 5%
        var r = SafetyChecker.Evaluate(ValidOrder(), status, Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.Contains(r.Violations, v => v.Contains("Perdita giornaliera"));
        Assert.True(r.RequiresEmergencyStop); // CRITICA
    }

    [Fact]
    public void DrawdownExceeded_IsRejected_AndTriggersEmergencyStop()
    {
        var status = OkStatus();
        status.MaxDrawdown = 25m; // > 20%
        var r = SafetyChecker.Evaluate(ValidOrder(), status, Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.Contains(r.Violations, v => v.Contains("Drawdown"));
        Assert.True(r.RequiresEmergencyStop); // CRITICA
    }

    [Fact]
    public void TooManyOpenPositions_IsRejected()
    {
        var status = OkStatus();
        status.OpenPositionCount = 5; // == limite
        var r = SafetyChecker.Evaluate(ValidOrder(), status, Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.Contains(r.Violations, v => v.Contains("posizioni aperte"));
    }

    [Fact]
    public void OrdersTooClose_IsRejected()
    {
        var status = OkStatus();
        status.LastOrderUtc = Now.AddSeconds(-3); // < 10s
        var r = SafetyChecker.Evaluate(ValidOrder(), status, Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.Contains(r.Violations, v => v.Contains("ravvicinati"));
    }

    [Fact]
    public void LiveOrderWithoutConfirmation_IsRejected()
    {
        var o = ValidOrder();
        o.Mode = TradingMode.Live;
        o.ManuallyConfirmed = false;
        var r = SafetyChecker.Evaluate(o, OkStatus(), Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.Contains(r.Violations, v => v.Contains("conferma manuale"));
    }

    [Fact]
    public void LiveOrderWithConfirmation_PassesConfirmationRule()
    {
        var o = ValidOrder();
        o.Mode = TradingMode.Live;
        o.ManuallyConfirmed = true;
        var r = SafetyChecker.Evaluate(o, OkStatus(), Cfg(), Now);
        Assert.True(r.IsAllowed, string.Join("; ", r.Violations));
    }

    [Fact]
    public void WhenEmergencyStopped_AnyOrder_IsRejected()
    {
        var status = OkStatus();
        status.IsEmergencyStopped = true;
        status.EmergencyStopReason = "test";
        var r = SafetyChecker.Evaluate(ValidOrder(), status, Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.Contains(r.Violations, v => v.Contains("Emergency stop"));
    }

    [Fact]
    public void InvalidQuantityOrPrice_IsRejected()
    {
        var o = ValidOrder();
        o.Quantity = 0m;
        o.Price = 0m;
        var r = SafetyChecker.Evaluate(o, OkStatus(), Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.Contains(r.Violations, v => v.Contains("Quantità"));
        Assert.Contains(r.Violations, v => v.Contains("Prezzo"));
    }

    [Fact]
    public void MultipleViolations_AllCollected()
    {
        var o = ValidOrder();
        o.Quantity = 0.05m; // troppo grande
        var status = OkStatus();
        status.OpenPositionCount = 5;        // troppe posizioni
        status.LastOrderUtc = Now.AddSeconds(-1); // troppo ravvicinato
        var r = SafetyChecker.Evaluate(o, status, Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.True(r.Violations.Count >= 3, $"violations={r.Violations.Count}");
    }
}
