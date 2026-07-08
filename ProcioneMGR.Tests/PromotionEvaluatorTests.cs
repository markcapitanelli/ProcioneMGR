using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica la logica pura di promozione/retrocessione delle corsie. Il criterio di SICUREZZA più
/// importante: nessuna metrica, per quanto eccellente, produce mai una promozione a Live — è
/// verificato esplicitamente. Copre anche i singoli criteri di promozione, il blocco assoluto sul
/// drawdown, e la retrocessione Testnet→Paper quando l'edge svanisce.
/// </summary>
public class PromotionEvaluatorTests
{
    private static PromotionEvaluatorOptions Opt() => new(); // default: Sharpe>=0.8, 30 trade, DD<=15%, 3 sett, win>=45%

    private static LaneMetrics Excellent() => new()
    {
        RealizedSharpe = 1.2m,
        RealizedProfitFactor = 1.8m,
        MaxDrawdown = 8m,
        TradeCount = 50,
        WinRate = 0.55m,
        ObservationPeriod = TimeSpan.FromDays(28),
    };

    [Fact]
    public void ExcellentPaperLane_PromotesToTestnet()
    {
        var d = PromotionEvaluator.Decide(Excellent(), TradingMode.Paper, isRunning: true, Opt());
        Assert.True(d.ShouldPromote);
        Assert.Equal(TradingMode.Testnet, d.SuggestedMode);
    }

    [Fact]
    public void LowSharpe_DoesNotPromote()
    {
        var m = Excellent(); m.RealizedSharpe = 0.5m;
        var d = PromotionEvaluator.Decide(m, TradingMode.Paper, true, Opt());
        Assert.False(d.ShouldPromote);
    }

    [Fact]
    public void FewTrades_DoesNotPromote()
    {
        var m = Excellent(); m.TradeCount = 10;
        Assert.False(PromotionEvaluator.Decide(m, TradingMode.Paper, true, Opt()).ShouldPromote);
    }

    [Fact]
    public void HighDrawdown_DoesNotPromote()
    {
        var m = Excellent(); m.MaxDrawdown = 18m; // oltre 15% ma sotto il blocco assoluto 20%
        Assert.False(PromotionEvaluator.Decide(m, TradingMode.Paper, true, Opt()).ShouldPromote);
    }

    [Fact]
    public void TooFewWeeks_DoesNotPromote()
    {
        var m = Excellent(); m.ObservationPeriod = TimeSpan.FromDays(7);
        Assert.False(PromotionEvaluator.Decide(m, TradingMode.Paper, true, Opt()).ShouldPromote);
    }

    [Fact]
    public void LowWinRate_DoesNotPromote()
    {
        var m = Excellent(); m.WinRate = 0.30m;
        Assert.False(PromotionEvaluator.Decide(m, TradingMode.Paper, true, Opt()).ShouldPromote);
    }

    [Fact]
    public void HardDrawdownBlock_NeverPromotes_EvenIfOtherwiseGreat()
    {
        var m = Excellent(); m.MaxDrawdown = 25m; // oltre il blocco assoluto
        var d = PromotionEvaluator.Decide(m, TradingMode.Paper, true, Opt());
        Assert.False(d.ShouldPromote);
        Assert.False(d.ReadyForTestnet);
    }

    [Fact]
    public void AutoPromoteDisabled_MarksReadyButDoesNotPromote()
    {
        var opt = Opt(); opt.AutoPromoteToTestnet = false;
        var d = PromotionEvaluator.Decide(Excellent(), TradingMode.Paper, true, opt);
        Assert.True(d.ReadyForTestnet);
        Assert.False(d.ShouldPromote);
    }

    // ---- SICUREZZA: mai a Live ----

    [Fact]
    public void TestnetLane_WithGodTierMetrics_IsNeverPromotedToLive()
    {
        var m = Excellent(); m.RealizedSharpe = 5.0m; m.TradeCount = 1000; m.WinRate = 0.95m;
        var d = PromotionEvaluator.Decide(m, TradingMode.Testnet, true, Opt());
        Assert.NotEqual(TradingMode.Live, d.SuggestedMode);
        Assert.False(d.ShouldPromote);
        Assert.Equal(TradingMode.Testnet, d.SuggestedMode); // resta dov'è: Testnet→Live è manuale
    }

    [Fact]
    public void LiveLane_IsNeverTouched()
    {
        var d = PromotionEvaluator.Decide(Excellent(), TradingMode.Live, true, Opt());
        Assert.Equal(TradingMode.Live, d.SuggestedMode);
        Assert.False(d.ShouldPromote);
        Assert.False(d.ShouldDemote);
    }

    // ---- Reversibilità: Testnet→Paper ----

    [Fact]
    public void TestnetLane_EdgeGone_DemotesToPaper()
    {
        var m = Excellent(); m.RealizedSharpe = 0.3m; m.ObservationPeriod = TimeSpan.FromDays(21);
        var d = PromotionEvaluator.Decide(m, TradingMode.Testnet, true, Opt());
        Assert.True(d.ShouldDemote);
        Assert.Equal(TradingMode.Paper, d.SuggestedMode);
    }

    [Fact]
    public void SameMetrics_SameDecision_Deterministic()
    {
        var a = PromotionEvaluator.Decide(Excellent(), TradingMode.Paper, true, Opt());
        var b = PromotionEvaluator.Decide(Excellent(), TradingMode.Paper, true, Opt());
        Assert.Equal(a.ShouldPromote, b.ShouldPromote);
        Assert.Equal(a.SuggestedMode, b.SuggestedMode);
    }
}
