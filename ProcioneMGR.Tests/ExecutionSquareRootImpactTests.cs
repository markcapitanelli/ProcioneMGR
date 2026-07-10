using ProcioneMGR.Data;
using ProcioneMGR.Services.Execution;

namespace ProcioneMGR.Tests;

/// <summary>
/// E1 — modello di impatto di mercato √(partecipazione) (legge empirica di Almgren) al posto del solo
/// lineare. Verifica la concavità: per piccole partecipazioni il √ costa PIÙ del lineare (per unità),
/// e raddoppiando la partecipazione l'impatto √ cresce di ×√-ratio (concavo) mentre il lineare cresce
/// proporzionalmente. Isola l'impatto azzerando lo spread e alzando il tetto.
/// </summary>
public class ExecutionSquareRootImpactTests
{
    private static ExecutionParameters Params(MarketImpactModel model) => new()
    {
        ImpactModel = model,
        ImpactCoefficient = 0.1m,
        HalfSpreadPct = 0m,       // isola l'impatto puro
        MaxImpactPct = 1.0m,      // niente tetto in questi range
    };

    /// <summary>Una candela piatta a 100 (typical=arrival) con volume dato, per leggere l'impatto in bps.</summary>
    private static List<OhlcvData> FlatCandle(decimal volume) =>
    [
        new OhlcvData
        {
            Symbol = "BTCUSDT", Timeframe = "5m", TimestampUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Open = 100m, High = 100m, Low = 100m, Close = 100m, Volume = volume,
        },
    ];

    private static decimal SlippageBps(decimal qty, decimal volume, MarketImpactModel model)
    {
        var intent = new ExecutionIntent("BTCUSDT", ExecutionSide.Buy, qty, 100m);
        var plan = new ExecutionPlan { Algorithm = "Test", Slices = [new ExecutionSlice(0, qty)] };
        return new ExecutionSimulator().Simulate(plan, intent, FlatCandle(volume), Params(model)).SlippageBps;
    }

    [Fact]
    public void SquareRoot_ExceedsLinear_ForSmallParticipation()
    {
        // participation = 10/1000 = 0.01. √: 0.1·0.1=0.01=100bps; lineare: 0.1·0.01=0.001=10bps.
        var sqrt = SlippageBps(10m, 1000m, MarketImpactModel.SquareRoot);
        var linear = SlippageBps(10m, 1000m, MarketImpactModel.Linear);

        Assert.True(sqrt > linear, $"√ {sqrt:F1}bps deve superare lineare {linear:F1}bps a piccola partecipazione");
        Assert.Equal(100m, sqrt, 0);
        Assert.Equal(10m, linear, 0);
    }

    [Fact]
    public void SquareRoot_IsConcave_QuadruplingParticipationDoublesImpact()
    {
        // part 0.01 → 0.04 (×4). √: impatto ×2 (√4); lineare: impatto ×4.
        var sqrtLow = SlippageBps(10m, 1000m, MarketImpactModel.SquareRoot);
        var sqrtHigh = SlippageBps(40m, 1000m, MarketImpactModel.SquareRoot);
        var linLow = SlippageBps(10m, 1000m, MarketImpactModel.Linear);
        var linHigh = SlippageBps(40m, 1000m, MarketImpactModel.Linear);

        Assert.Equal(2.0, (double)(sqrtHigh / sqrtLow), 3);  // concavo: ×√4 = ×2
        Assert.Equal(4.0, (double)(linHigh / linLow), 3);    // lineare: ×4
    }

    [Fact]
    public void SquareRoot_IsDefault()
    {
        Assert.Equal(MarketImpactModel.SquareRoot, new ExecutionParameters().ImpactModel);
    }
}
