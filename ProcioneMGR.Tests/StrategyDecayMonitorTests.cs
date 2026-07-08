using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test unitari (dati sintetici, nessun DB) per <see cref="StrategyDecayMonitor"/>: lo Sharpe
/// realizzato "a trade" e il confronto con lo Sharpe atteso dal backtest/holdout.
/// </summary>
public class StrategyDecayMonitorTests
{
    private static EnsembleStrategy Strategy(decimal? expectedSharpe = 1.5m) => new()
    {
        StrategyId = "strat-1",
        StrategyName = "RsiOversold",
        DisplayName = "RsiOversold BTC/USDT 1h",
        ExpectedSharpe = expectedSharpe,
        ExpectedProfitFactor = 1.4m,
    };

    /// <summary>N trade con lo stesso PnlPercent per gamba, distribuiti su spanDays a partire da 2026-01-01, ogni trade con StrategyId "strat-1".</summary>
    private static List<TradeRecord> Trades(int count, decimal[] pnlPercents, int spanDays)
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var step = spanDays / (double)(count - 1);
        var trades = new List<TradeRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var pnlPercent = pnlPercents[i % pnlPercents.Length];
            trades.Add(new TradeRecord
            {
                StrategyId = "strat-1",
                Symbol = "BTC/USDT",
                EntryPrice = 100m,
                ExitPrice = 100m * (1m + pnlPercent / 100m),
                Quantity = 1m,
                Pnl = 100m * (pnlPercent / 100m),
                PnlPercent = pnlPercent,
                OpenedAtUtc = start.AddDays(i * step).AddHours(-1),
                ClosedAtUtc = start.AddDays(i * step),
                Mode = TradingMode.Paper,
            });
        }
        return trades;
    }

    [Fact]
    public void FewerTradesThanWindow_NoAlert_ReportsInsufficientCount()
    {
        var monitor = new StrategyDecayMonitor();
        var trades = Trades(10, [1m, -0.5m], spanDays: 90);

        var report = monitor.Analyze(Strategy(), trades);

        Assert.Equal(10, report.TradeCount);
        Assert.False(report.IsAlert);
        Assert.Null(report.RealizedSharpe);
        Assert.Contains("insufficienti", report.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoExpectedSharpe_NoAlert_ReportsMetricsUnavailable()
    {
        var monitor = new StrategyDecayMonitor();
        var trades = Trades(20, [1m, -0.5m], spanDays: 190);

        var report = monitor.Analyze(Strategy(expectedSharpe: null), trades);

        Assert.Equal(20, report.TradeCount);
        Assert.False(report.IsAlert);
        Assert.Null(report.RealizedSharpe); // non calcolato: nessun confronto da fare
        Assert.Contains("non disponibili", report.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RealizedFarBelowExpected_TriggersAlert()
    {
        var monitor = new StrategyDecayMonitor();
        // Rendimenti piccoli e prevalentemente negativi: Sharpe realizzato basso/negativo contro un atteso di 1.5.
        var trades = Trades(20, [-0.3m, 0.1m, -0.2m, 0.05m], spanDays: 190);

        var report = monitor.Analyze(Strategy(expectedSharpe: 1.5m), trades);

        Assert.True(report.IsAlert);
        Assert.NotNull(report.SharpeRatio);
        Assert.True(report.SharpeRatio < 0.5m);
        Assert.Contains("ALERT", report.StatusMessage);
    }

    [Fact]
    public void RealizedCloseToExpected_NoAlert()
    {
        var monitor = new StrategyDecayMonitor();
        // Rendimenti positivi e consistenti: ci si aspetta uno Sharpe realizzato sano, sopra la
        // soglia (50% di 1.5 = 0.75) qualunque sia la cadenza esatta stimata dei trade.
        var trades = Trades(20, [1.2m, 0.8m, 1.5m, 0.9m], spanDays: 190);

        var report = monitor.Analyze(Strategy(expectedSharpe: 1.5m), trades);

        Assert.False(report.IsAlert);
        Assert.NotNull(report.SharpeRatio);
        Assert.True(report.SharpeRatio >= 0.5m);
        Assert.StartsWith("In linea", report.StatusMessage);
    }

    [Fact]
    public void NonPositiveExpectedSharpe_SkipsRatioAlert_ButReportsDelta()
    {
        var monitor = new StrategyDecayMonitor();
        var trades = Trades(20, [1m, -0.5m], spanDays: 190);

        var report = monitor.Analyze(Strategy(expectedSharpe: -1.2m), trades);

        Assert.False(report.IsAlert); // la soglia percentuale non e' applicabile con un atteso non positivo
        Assert.Null(report.SharpeRatio);
        Assert.NotNull(report.RealizedSharpe); // il realizzato viene comunque calcolato
        Assert.NotNull(report.SharpeDelta);
        Assert.Contains("non positivo", report.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZeroVarianceReturns_RealizedSharpeIsZero_NoDivisionByZero()
    {
        var monitor = new StrategyDecayMonitor();
        var trades = Trades(20, [0.5m], spanDays: 190); // stesso PnlPercent su ogni trade -> deviazione 0

        var report = monitor.Analyze(Strategy(expectedSharpe: 1.5m), trades);

        Assert.Equal(0m, report.RealizedSharpe);
    }

    [Fact]
    public void RealizedProfitFactor_MatchesGrossProfitOverGrossLoss()
    {
        var monitor = new StrategyDecayMonitor();
        // 10 vincenti da +2 (Pnl money = +2), 10 perdenti da -1 (Pnl money = -1): PF atteso = 20/10 = 2.
        var trades = Trades(20, [2m, -1m], spanDays: 190);

        var report = monitor.Analyze(Strategy(), trades);

        Assert.Equal(2m, report.RealizedProfitFactor);
    }

    /// <summary>
    /// Blocca la formula di annualizzazione documentata in StrategyDecayMonitor: lo Sharpe "a
    /// trade" usa sqrt(trade/anno STIMATI dalla cadenza reale del campione), non sqrt(candele/anno)
    /// come lo Sharpe holdout — sono concetti diversi (vedi commento nel sorgente). Il test
    /// ricalcola indipendentemente la stessa formula sui dati grezzi e verifica che il monitor
    /// produca lo stesso numero, così un refactor futuro che cambiasse silenziosamente
    /// l'annualizzazione verrebbe intercettato qui.
    /// </summary>
    [Fact]
    public void RealizedSharpe_MatchesIndependentlyComputedAnnualization()
    {
        var monitor = new StrategyDecayMonitor();
        var pnls = new[] { 1.5m, -0.8m, 2.1m, 0.3m, -1.2m, 1.8m, 0.6m, -0.4m, 1.1m, 0.9m,
                            1.5m, -0.8m, 2.1m, 0.3m, -1.2m, 1.8m, 0.6m, -0.4m, 1.1m, 0.9m };
        var trades = Trades(20, pnls, spanDays: 190);

        var report = monitor.Analyze(Strategy(), trades);

        var returns = pnls.Select(p => p / 100m).ToList();
        var mean = returns.Average();
        var variance = returns.Select(r => (r - mean) * (r - mean)).Sum() / returns.Count;
        var stdDev = (decimal)Math.Sqrt((double)variance);
        var tradesPerYear = 20m / 190m * 365m;
        var expectedSharpe = mean / stdDev * (decimal)Math.Sqrt((double)tradesPerYear);

        Assert.NotNull(report.RealizedSharpe);
        Assert.Equal(expectedSharpe, report.RealizedSharpe!.Value, 6);
    }

    [Fact]
    public void TradesForOtherStrategies_AreIgnored()
    {
        var monitor = new StrategyDecayMonitor();
        var ownTrades = Trades(20, [1.2m, 0.8m, 1.5m, 0.9m], spanDays: 190);
        var otherTrades = Trades(30, [-5m], spanDays: 90).Select(t => { t.StrategyId = "other-strat"; return t; }).ToList();

        var report = monitor.Analyze(Strategy(), ownTrades.Concat(otherTrades).ToList());

        Assert.Equal(20, report.TradeCount); // le 30 di "other-strat" non devono contaminare il conteggio
    }
}
