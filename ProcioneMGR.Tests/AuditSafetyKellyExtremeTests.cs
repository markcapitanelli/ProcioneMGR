using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Audit FASE 1 — SafetyChecker sotto scenari estremi (capitale nullo/negativo, drawdown 100%,
/// perdita catastrofica, clock skew, boundary esatti dei limiti) e criterio di Kelly su
/// distribuzioni patologiche (wipeout totale, covarianza singolare). Principio della piattaforma:
/// fail-CLOSED — nel dubbio l'ordine si rifiuta.
/// </summary>
public class AuditSafetyKellyExtremeTests
{
    private static readonly DateTime Now = new(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);

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

    private static Order Order(decimal qty = 0.01m, decimal price = 50_000m) => new()
    {
        Symbol = "BTC/USDT", Side = OrderSide.Buy, Type = OrderType.Market,
        Quantity = qty, Price = price, Mode = TradingMode.Paper,
    };

    private static TradingEngineStatus Status() => new()
    {
        TotalCapital = 10_000m, UsedCapital = 0m, DailyPnl = 0m, MaxDrawdown = 0m,
        OpenPositionCount = 0, LastOrderUtc = Now.AddSeconds(-60), IsEmergencyStopped = false,
    };

    // --- SafetyChecker: scenari estremi ---------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5000)]
    public void ZeroOrNegativeCapital_HugeOrder_MustBeRejected_FailClosed(int capital)
    {
        // Con capitale non positivo i check di dimensione/esposizione/perdita (che confrontano
        // contro % del capitale) non hanno una base valida: il comportamento SICURO è rifiutare
        // l'ordine, non lasciarlo passare perché "i check non si applicano".
        var status = Status();
        status.TotalCapital = capital;
        var order = Order(qty: 10m, price: 50_000m); // notional 500.000 contro capitale <= 0

        var r = SafetyChecker.Evaluate(order, status, Cfg(), Now);

        Assert.False(r.IsAllowed,
            "FAIL-OPEN: ordine enorme consentito con capitale non positivo (i check % vengono saltati)");
    }

    [Fact]
    public void ExtremeDrawdown_100Percent_BlocksAndTriggersEmergencyStop()
    {
        var status = Status();
        status.MaxDrawdown = 100m;
        var r = SafetyChecker.Evaluate(Order(), status, Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.True(r.RequiresEmergencyStop);
    }

    [Fact]
    public void CatastrophicDailyLoss_95Percent_BlocksAndTriggersEmergencyStop()
    {
        var status = Status();
        status.DailyPnl = -9_500m; // -95% su 10.000, limite 5%
        var r = SafetyChecker.Evaluate(Order(), status, Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.True(r.RequiresEmergencyStop);
    }

    [Fact]
    public void ClockSkew_LastOrderInTheFuture_FailsSafe_Rejected()
    {
        // Orologio dell'exchange avanti rispetto al nostro: elapsed negativo. Il comportamento
        // atteso (fail-safe) è trattarlo come "troppo ravvicinato" e rifiutare.
        var status = Status();
        status.LastOrderUtc = Now.AddSeconds(30);
        var r = SafetyChecker.Evaluate(Order(), status, Cfg(), Now);
        Assert.False(r.IsAllowed);
    }

    [Fact]
    public void ExtremeLeverage_125x_IsRejected_WithoutEmergencyStop()
    {
        var order = Order();
        order.MarketType = MarketType.Futures;
        order.Leverage = 125;
        var r = SafetyChecker.Evaluate(order, Status(), Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.False(r.RequiresEmergencyStop);
    }

    [Fact]
    public void EmergencyStopActive_EvenPerfectOrder_IsRejected()
    {
        var status = Status();
        status.IsEmergencyStopped = true;
        status.EmergencyStopReason = "test";
        var r = SafetyChecker.Evaluate(Order(), status, Cfg(), Now);
        Assert.False(r.IsAllowed);
    }

    // --- Boundary esatti: documentano la convenzione dei confronti ------------------------------

    [Fact]
    public void DailyLoss_ExactlyAtLimit_IsRejected_AndTriggersEmergencyStop_FailClosed()
    {
        // Convenzione uniformata su ">=" (fail-closed) come il drawdown: alla soglia esatta si
        // blocca già, senza aspettare il centesimo successivo. Cambio consapevole dell'audit 2026-07.
        var status = Status();
        status.DailyPnl = -500m; // esattamente il 5% di 10.000
        var r = SafetyChecker.Evaluate(Order(), status, Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.True(r.RequiresEmergencyStop);
    }

    [Fact]
    public void DailyLoss_JustBelowLimit_IsStillAllowed()
    {
        // Un centesimo sotto la soglia resta consentito: il confronto morde solo DA] la soglia in su.
        var status = Status();
        status.DailyPnl = -499.99m; // poco sotto il 5% di 10.000
        var r = SafetyChecker.Evaluate(Order(), status, Cfg(), Now);
        Assert.True(r.IsAllowed, string.Join("; ", r.Violations));
    }

    [Fact]
    public void Drawdown_ExactlyAtLimit_IsRejected_GreaterOrEqualConvention()
    {
        var status = Status();
        status.MaxDrawdown = 20m; // esattamente il limite
        var r = SafetyChecker.Evaluate(Order(), status, Cfg(), Now);
        Assert.False(r.IsAllowed);
        Assert.True(r.RequiresEmergencyStop);
    }

    // --- Kelly: distribuzioni patologiche --------------------------------------------------------

    [Fact]
    public void EmpiricalKelly_DatasetWithTotalWipeout_IsDrasticallyMoreConservative()
    {
        // 150 vincite +2% e 50 perdite -1%: edge fortissimo, Kelly empirico saturato al cap.
        var baseline = Enumerable.Repeat(0.02, 150).Concat(Enumerable.Repeat(-0.01, 50)).ToList();
        var fBase = KellyCalculator.EmpiricalKelly(baseline, maxFraction: 2.0);
        Assert.True(fBase > 1.5, $"atteso Kelly quasi al cap sull'edge forte, ottenuto {fBase}");

        // Stesso storico + UN wipeout totale (-100%): la frazione deve crollare sotto 1
        // (scommettere tutto = bancarotta certa nel campione) e molto sotto il caso base.
        var withCrash = baseline.Append(-1.0).ToList();
        var fCrash = KellyCalculator.EmpiricalKelly(withCrash, maxFraction: 2.0);
        Assert.True(fCrash < 1.0, $"con un -100% nel campione f* deve essere < 1, ottenuto {fCrash}");
        Assert.True(fCrash < fBase - 1.0, $"crollo atteso: base {fBase}, con crash {fCrash}");
    }

    [Fact]
    public void BinaryKelly_EdgeCases_AlwaysInZeroOneRange()
    {
        Assert.Equal(0m, KellyCalculator.BinaryKelly(0m, 2m));       // p = 0
        Assert.Equal(0m, KellyCalculator.BinaryKelly(1m, 2m));       // p = 1 (input invalido)
        Assert.Equal(0m, KellyCalculator.BinaryKelly(0.6m, 0m));     // payoff nullo
        Assert.Equal(0m, KellyCalculator.BinaryKelly(0.6m, -3m));    // payoff negativo
        Assert.Equal(0m, KellyCalculator.BinaryKelly(0.1m, 0.5m));   // edge negativo -> 0

        var nearCertain = KellyCalculator.BinaryKelly(0.999m, 100m);
        Assert.InRange(nearCertain, 0m, 1m); // mai oltre il 100% del capitale
    }

    [Fact]
    public void ContinuousKelly_DegenerateInputs_ReturnZero()
    {
        Assert.Equal(0m, KellyCalculator.ContinuousKelly(-0.01m, 0.05m)); // media negativa
        Assert.Equal(0m, KellyCalculator.ContinuousKelly(0.01m, 0m));     // sigma zero
        Assert.Equal(0, KellyCalculator.ContinuousKellyNumeric(0.01, 0.0));
        Assert.Equal(0, KellyCalculator.ContinuousKellyNumeric(-0.05, 0.1));
        Assert.Equal(0, KellyCalculator.EmpiricalKelly(Array.Empty<double>()));
    }

    [Fact]
    public void MultiAssetKelly_DuplicatedAsset_SingularCovariance_NoThrow_FiniteNormalized()
    {
        var rnd = new Random(13);
        var a = Enumerable.Range(0, 100).Select(_ => (rnd.NextDouble() - 0.45) * 0.02).ToList();
        var assets = new List<IReadOnlyList<double>>
        {
            a,
            new List<double>(a), // duplicato perfetto -> covarianza singolare
            Enumerable.Range(0, 100).Select(_ => (rnd.NextDouble() - 0.5) * 0.03).ToList(),
        };

        var w = new KellyCalculator().MultiAssetKelly(assets);
        Assert.Equal(3, w.Count);
        var sumAbs = w.Sum(Math.Abs);
        Assert.InRange(sumAbs, 0.999m, 1.001m);
    }
}
