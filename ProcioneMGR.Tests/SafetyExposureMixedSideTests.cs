using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [T5, PRD memoria-caccia 2026-08-14] Con più gambe concorrenti sulla stessa corsia (ensemble
/// multi-strategia, incluse le gambe di fascia grigia) possono coesistere posizioni long e short
/// sullo stesso simbolo — fra strategie DIVERSE, mai dentro una (il segnale opposto prima chiude).
/// La semantica dell'esposizione era LORDA ma nessun test la fissava: questi la scolpiscono.
///   1. long+short si SOMMANO in valore assoluto (un "hedge" fra strategie non è un hedge
///      garantito: le gambe escono in momenti diversi) — fail-closed, mai 0;
///   2. ogni gamba conta 1 verso MaxOpenPositions, anche se opposte;
///   3. una Quantity negativa (mai prevista, la direzione sta in Side) PESA, non sconta:
///      la cintura Math.Abs impedisce che riduca l'esposizione conteggiata in silenzio.
/// </summary>
public sealed class SafetyExposureMixedSideTests
{
    private static OpenPosition Position(OrderSide side, decimal notional) => new()
    {
        Symbol = "XLM/USDT",
        Side = side,
        Quantity = 1m,
        EntryPrice = notional,
        MarginBalance = notional,
        Leverage = 1,
    };

    [Fact]
    public void OppositeSides_SameSymbol_SumGross_NeverNet()
    {
        var positions = new List<OpenPosition>
        {
            Position(OrderSide.Buy, 1_000m),
            Position(OrderSide.Sell, 1_000m),
        };
        // Nettarle darebbe 0 e lascerebbe aprire all'infinito "coppie coperte" che coperte non
        // sono: la lettura lorda consuma 2.000 di budget.
        Assert.Equal(2_000m, SafetyExposure.ExposedNotional(positions));
    }

    [Fact]
    public void OppositeLegs_BothCount_TowardMaxOpenPositions()
    {
        var cfg = new SafetyConfiguration
        {
            MaxOpenPositions = 2,
            MaxTotalExposurePercent = 100m,
            MaxPositionSizePercent = 100m,
        };
        var positions = new List<OpenPosition>
        {
            Position(OrderSide.Buy, 1_000m),
            Position(OrderSide.Sell, 1_000m),
        };
        var status = new TradingEngineStatus
        {
            TotalCapital = 10_000m,
            UsedCapital = SafetyExposure.ExposedNotional(positions),
            OpenPositionCount = positions.Count, // come BuildSafetyStatus: _positions.Count, il Side non c'entra
        };
        var order = new Order
        {
            Symbol = "XLM/USDT", Side = OrderSide.Buy, Type = OrderType.Market,
            Quantity = 1m, Price = 500m, Mode = TradingMode.Paper,
        };

        var check = SafetyChecker.Evaluate(order, status, cfg, DateTime.UtcNow);

        Assert.False(check.IsAllowed);
        Assert.Contains(check.Violations, v => v.Contains("Troppe posizioni aperte"));
    }

    [Fact]
    public void NegativeQuantity_WeighsIn_NeverDiscounts()
    {
        // Quantity e' non-segnata per convenzione; se un difetto a monte ne facesse arrivare una
        // negativa, senza l'abs SCONTEREBBE 1.000 dall'esposizione — il verso sbagliato di un
        // guasto sulla sicurezza.
        var positions = new List<OpenPosition>
        {
            Position(OrderSide.Buy, 1_000m),
            new() { Symbol = "XLM/USDT", Side = OrderSide.Sell, Quantity = -1m, EntryPrice = 1_000m, MarginBalance = 1_000m, Leverage = 1 },
        };
        Assert.Equal(2_000m, SafetyExposure.ExposedNotional(positions));
    }

    [Fact]
    public void SingleSideAggregate_Regression()
    {
        // La lettura storica a lato unico non cambia: 3 long da 1.000 = 3.000.
        var positions = Enumerable.Range(0, 3).Select(_ => Position(OrderSide.Buy, 1_000m)).ToList();
        Assert.Equal(3_000m, SafetyExposure.ExposedNotional(positions));
    }
}
