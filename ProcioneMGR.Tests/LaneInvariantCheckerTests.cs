using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test PURI degli invarianti contabili (Fase 0-A3, PRD Autonomia Operativa §3): nessun DB,
/// nessun motore. Il caso guida è quello REALE della corsia 2 Testnet del 2026-07-18
/// (docs/TEST-UI-2026-07-18.md, bug B1): PnL -1.817.925 su capitale 10.000 con leva 2 —
/// uno stato che il watchdog deve riconoscere alla prima passata.
/// </summary>
public class LaneInvariantCheckerTests
{
    private static readonly LaneInvariantOptions Defaults = new();

    private static TradingEngineState HealthyState(decimal capital = 10_000m, int leverage = 1) => new()
    {
        LaneId = 2,
        Mode = TradingMode.Testnet,
        IsRunning = true,
        Leverage = leverage,
        TotalCapital = capital,
        AvailableCapital = capital,
        RealizedPnl = 0m,
    };

    private static OpenPosition Position(decimal qty, decimal price, decimal unrealized = 0m) => new()
    {
        LaneId = 2, Quantity = qty, EntryPrice = price, CurrentPrice = price, UnrealizedPnl = unrealized,
        OpenedInMode = TradingMode.Testnet,
    };

    [Fact]
    public void HealthyLane_NoViolations()
    {
        var state = HealthyState(leverage: 2);
        state.AvailableCapital = 9_200m;
        state.RealizedPnl = -350m;
        var positions = new[] { Position(0.4m, 2_000m, unrealized: 12m) }; // nozionale 800

        Assert.Empty(LaneInvariantChecker.Check(state, positions, Defaults));
    }

    [Fact]
    public void RealCorsia2Case_PnlAndCapital_BothViolated()
    {
        // I numeri VERI della sessione 2026-07-18: capitale 10k, leva 2,
        // Total PnL -1.817.925,81, Available -1.807.925,81.
        var state = HealthyState(capital: 10_000m, leverage: 2);
        state.AvailableCapital = -1_807_925.81m;
        state.RealizedPnl = -1_817_925.81m;

        var violations = LaneInvariantChecker.Check(state, [], Defaults);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.Contains("AvailableCapital negativo"));
        Assert.Contains(violations, v => v.Contains("PnL totale"));
    }

    [Fact]
    public void RealCorsia2Case_AbsurdNotional_ViolatesExposure()
    {
        // Il "Buy 1.039 ETH @ 1.748" adottato dal fill patologico: nozionale ~1,8M su
        // capitale 10k leva 2 → cap esposizione = 2 × 10k × 2 = 40k.
        var state = HealthyState(capital: 10_000m, leverage: 2);
        var positions = new[] { Position(1_039.77125m, 1_748.18m) };

        var violations = LaneInvariantChecker.Check(state, positions, Defaults);

        Assert.Contains(violations, v => v.Contains("Nozionale aperto fuori scala"));
    }

    [Fact]
    public void UnrealizedPnl_CountsTowardTotalPnl()
    {
        var state = HealthyState(capital: 10_000m); // cap PnL = 2 × 10k × 1 = 20k
        state.RealizedPnl = 0m;
        var positions = new[] { Position(1m, 100m, unrealized: -25_000m) };

        var violations = LaneInvariantChecker.Check(state, positions, Defaults);

        Assert.Contains(violations, v => v.Contains("PnL totale"));
    }

    [Fact]
    public void SmallNegativeAvailable_WithinTolerance_NotViolated()
    {
        var state = HealthyState();
        state.AvailableCapital = -0.5m; // entro ε = 1

        Assert.Empty(LaneInvariantChecker.Check(state, [], Defaults));
    }

    [Fact]
    public void NonPositiveCapital_IsViolation_AndShortCircuits()
    {
        var state = HealthyState(capital: 0m);
        state.AvailableCapital = -5_000m;

        var violations = LaneInvariantChecker.Check(state, [], Defaults);

        // Una sola violazione (capitale non positivo): i multipli del capitale senza base
        // sensata produrrebbero solo rumore.
        var v = Assert.Single(violations);
        Assert.Contains("TotalCapital non positivo", v);
    }

    [Fact]
    public void ExposureUsesEntryPrice_WhenMarkNotYetArrived()
    {
        var state = HealthyState(capital: 10_000m); // cap esposizione = 20k
        var pos = Position(30m, 1_000m); // nozionale 30k
        pos.CurrentPrice = 0m;           // mark-to-market non ancora arrivato

        var violations = LaneInvariantChecker.Check(state, [pos], Defaults);

        Assert.Contains(violations, v => v.Contains("Nozionale aperto fuori scala"));
    }

    [Fact]
    public void LeverageBelowOne_TreatedAsOne()
    {
        var state = HealthyState(capital: 10_000m);
        state.Leverage = 0; // riga storica/copertura: mai leva < 1 nei cap
        state.RealizedPnl = -25_000m; // cap = 2 × 10k × 1 = 20k

        var violations = LaneInvariantChecker.Check(state, [], Defaults);

        Assert.Contains(violations, v => v.Contains("PnL totale"));
    }
}
