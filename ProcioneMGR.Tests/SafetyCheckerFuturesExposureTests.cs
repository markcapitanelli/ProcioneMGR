using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D-02, Fase 1 PRD-RISANAMENTO] <c>MaxTotalExposurePercent</c> deve vincolare l'esposizione
/// NOZIONALE aggregata anche sui Futures. Prima del fix lo stato di safety sommava il MARGINE
/// delle posizioni aperte al NOZIONALE del nuovo ordine (unita' diverse): con leva 5x e
/// MaxOpenPositions alzato, il capitale esposto raggiungeva il 100% contro un limite dichiarato
/// del 50% senza che il check scattasse — coi default la coincidenza 10%×5=50% mascherava tutto.
/// Qui si riproduce ESATTAMENTE lo scenario numerico dell'audit
/// (docs/audit/20_DEEP_DIVE_CODE_ANALYSIS.md §3) e si verifica che ora il limite morda.
/// </summary>
public sealed class SafetyCheckerFuturesExposureTests
{
    private static OpenPosition FuturesPosition(decimal notional, int leverage) => new()
    {
        Symbol = "BTC/USDT",
        Side = OrderSide.Buy,
        Quantity = 1m,
        EntryPrice = notional,                 // Quantity × EntryPrice = nozionale
        MarginBalance = notional / leverage,   // il margine e' 1/leva del nozionale
        Leverage = leverage,
    };

    private static Order FuturesOrder(decimal notional) => new()
    {
        Symbol = "BTC/USDT",
        Side = OrderSide.Buy,
        Type = OrderType.Market,
        Quantity = 1m,
        Price = notional,
        Mode = TradingMode.Paper,
        MarketType = MarketType.Futures,
        Leverage = 5,
    };

    [Fact]
    public void ExposedNotional_UsesNotional_NotMargin()
    {
        // 9 posizioni futures, nozionale 1.000 l'una a leva 5 (margine 200 l'una).
        var positions = Enumerable.Range(0, 9).Select(_ => FuturesPosition(1_000m, 5)).ToList();

        // Il calcolo pre-fix (margine) avrebbe dato 1.800: meno di un quinto dell'esposizione vera.
        Assert.Equal(9_000m, SafetyExposure.ExposedNotional(positions));
    }

    [Fact]
    public void AuditScenario_TenthPosition_IsNowRejected()
    {
        // Scenario dell'audit: capitale 10.000, MaxTotalExposure 50% (=5.000), 9 posizioni da
        // 1.000 nozionali gia' aperte. Col margine (1.800 + 1.000 = 2.800 ≤ 5.000) la decima
        // passava e l'esposizione reale toccava il 100% del capitale.
        var cfg = new SafetyConfiguration
        {
            MaxTotalExposurePercent = 50m,
            MaxPositionSizePercent = 100m, // fuori gioco: qui si testa il SOLO check aggregato
            MaxOpenPositions = 100,
            MaxLeverageAllowed = 10,
        };
        var positions = Enumerable.Range(0, 9).Select(_ => FuturesPosition(1_000m, 5)).ToList();
        var status = new TradingEngineStatus
        {
            TotalCapital = 10_000m,
            UsedCapital = SafetyExposure.ExposedNotional(positions), // la semantica del fix
            OpenPositionCount = positions.Count,
        };

        var check = SafetyChecker.Evaluate(FuturesOrder(1_000m), status, cfg, DateTime.UtcNow);

        Assert.False(check.IsAllowed);
        Assert.Contains(check.Violations, v => v.Contains("Esposizione totale"));
    }

    [Fact]
    public void WithinDeclaredLimit_IsStillAllowed()
    {
        // 3 posizioni da 1.000 + una quarta da 1.000 = 4.000 ≤ 5.000: il limite non deve
        // diventare piu' severo di quanto dichiara.
        var cfg = new SafetyConfiguration
        {
            MaxTotalExposurePercent = 50m,
            MaxPositionSizePercent = 100m,
            MaxOpenPositions = 100,
            MaxLeverageAllowed = 10,
        };
        var positions = Enumerable.Range(0, 3).Select(_ => FuturesPosition(1_000m, 5)).ToList();
        var status = new TradingEngineStatus
        {
            TotalCapital = 10_000m,
            UsedCapital = SafetyExposure.ExposedNotional(positions),
            OpenPositionCount = positions.Count,
        };

        var check = SafetyChecker.Evaluate(FuturesOrder(1_000m), status, cfg, DateTime.UtcNow);

        Assert.True(check.IsAllowed);
    }

    [Fact]
    public void SpotSemantics_Unchanged()
    {
        // Sullo Spot margine e nozionale coincidono da sempre: il fix non cambia nulla.
        var spot = new OpenPosition { Symbol = "BTC/USDT", Quantity = 2m, EntryPrice = 500m, MarginBalance = 1_000m, Leverage = 1 };
        Assert.Equal(1_000m, SafetyExposure.ExposedNotional([spot]));
    }
}
