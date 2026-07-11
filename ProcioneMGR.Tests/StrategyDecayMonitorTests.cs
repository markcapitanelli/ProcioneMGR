using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Monitoring;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test unitari (dati sintetici, nessun DB) per <see cref="StrategyDecayMonitor"/>. Dal fix M5 lo
/// Sharpe realizzato è su base PER-CANDELA (bucket del timeframe, vuoti = 0) come lo Sharpe
/// atteso dell'holdout — prima era "a trade" con sqrt(trade/anno), un'unità di misura diversa che
/// rendeva la soglia percentuale di alert priva di significato. Il vecchio numero sopravvive come
/// <see cref="DecayReport.RealizedTradeSharpe"/> informativo.
/// </summary>
public class StrategyDecayMonitorTests
{
    private const string Timeframe = "1h";

    private static EnsembleStrategy Strategy(decimal? expectedSharpe = 1.5m) => new()
    {
        StrategyId = "strat-1",
        StrategyName = "RsiOversold",
        DisplayName = "RsiOversold BTC/USDT 1h",
        ExpectedSharpe = expectedSharpe,
        ExpectedProfitFactor = 1.4m,
    };

    /// <summary>N trade con PnlPercent ciclico per gamba, distribuiti su spanDays a partire da 2026-01-01, ogni trade con StrategyId "strat-1".</summary>
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

        var report = monitor.Analyze(Strategy(), trades, Timeframe);

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

        var report = monitor.Analyze(Strategy(expectedSharpe: null), trades, Timeframe);

        Assert.Equal(20, report.TradeCount);
        Assert.False(report.IsAlert);
        Assert.Null(report.RealizedSharpe); // non calcolato: nessun confronto da fare
        Assert.Contains("non disponibili", report.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecayedStream_RealizedFarBelowExpected_TriggersAlert()
    {
        var monitor = new StrategyDecayMonitor();
        // Rendimenti piccoli e prevalentemente negativi: Sharpe realizzato negativo contro un atteso di 1.5.
        var trades = Trades(20, [-0.3m, 0.1m, -0.2m, 0.05m], spanDays: 190);

        var report = monitor.Analyze(Strategy(expectedSharpe: 1.5m), trades, Timeframe);

        Assert.True(report.IsAlert);
        Assert.NotNull(report.SharpeRatio);
        Assert.True(report.SharpeRatio < 0.5m);
        Assert.Contains("ALERT", report.StatusMessage);
    }

    [Fact]
    public void RealizedCloseToExpected_NoAlert()
    {
        var monitor = new StrategyDecayMonitor();
        // Rendimenti positivi e consistenti: Sharpe realizzato sano, sopra la soglia (50% di 1.5).
        var trades = Trades(20, [1.2m, 0.8m, 1.5m, 0.9m], spanDays: 190);

        var report = monitor.Analyze(Strategy(expectedSharpe: 1.5m), trades, Timeframe);

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

        var report = monitor.Analyze(Strategy(expectedSharpe: -1.2m), trades, Timeframe);

        Assert.False(report.IsAlert); // la soglia percentuale non e' applicabile con un atteso non positivo
        Assert.Null(report.SharpeRatio);
        Assert.NotNull(report.RealizedSharpe); // il realizzato viene comunque calcolato
        Assert.NotNull(report.SharpeDelta);
        Assert.Contains("non positivo", report.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllTradesInSingleBucket_RealizedSharpeIsZero_NoDivisionByZero()
    {
        var monitor = new StrategyDecayMonitor();
        // 20 trade chiusi nello STESSO minuto → un solo bucket 1h: nessuna varianza calcolabile.
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var trades = Trades(20, [0.5m], spanDays: 190);
        foreach (var t in trades) t.ClosedAtUtc = start;

        var report = monitor.Analyze(Strategy(expectedSharpe: 1.5m), trades, Timeframe);

        Assert.Equal(0m, report.RealizedSharpe);
    }

    [Fact]
    public void UniformReturnsOnePerBucket_ZeroVariance_RealizedSharpeIsZero()
    {
        var monitor = new StrategyDecayMonitor();
        // 1 trade per candela 1h, tutti identici → ogni bucket vale 0.5%: deviazione 0 → Sharpe 0.
        var start = new DateTime(2026, 1, 1, 0, 30, 0, DateTimeKind.Utc);
        var trades = Trades(20, [0.5m], spanDays: 1);
        for (var i = 0; i < trades.Count; i++) trades[i].ClosedAtUtc = start.AddHours(i);

        var report = monitor.Analyze(Strategy(expectedSharpe: 1.5m), trades, Timeframe);

        Assert.Equal(0m, report.RealizedSharpe);
    }

    [Fact]
    public void RealizedProfitFactor_MatchesGrossProfitOverGrossLoss()
    {
        var monitor = new StrategyDecayMonitor();
        // 10 vincenti da +2 (Pnl money = +2), 10 perdenti da -1 (Pnl money = -1): PF atteso = 20/10 = 2.
        var trades = Trades(20, [2m, -1m], spanDays: 190);

        var report = monitor.Analyze(Strategy(), trades, Timeframe);

        Assert.Equal(2m, report.RealizedProfitFactor);
    }

    /// <summary>
    /// Il vecchio Sharpe "a trade" (sqrt(trade/anno) stimati dalla cadenza reale) sopravvive come
    /// campo INFORMATIVO <see cref="DecayReport.RealizedTradeSharpe"/>: il test ricalcola la
    /// formula sui dati grezzi e verifica l'identità, così un refactor che la cambiasse
    /// silenziosamente verrebbe intercettato.
    /// </summary>
    [Fact]
    public void RealizedTradeSharpe_MatchesIndependentlyComputedPerTradeAnnualization()
    {
        var monitor = new StrategyDecayMonitor();
        var pnls = new[] { 1.5m, -0.8m, 2.1m, 0.3m, -1.2m, 1.8m, 0.6m, -0.4m, 1.1m, 0.9m,
                            1.5m, -0.8m, 2.1m, 0.3m, -1.2m, 1.8m, 0.6m, -0.4m, 1.1m, 0.9m };
        var trades = Trades(20, pnls, spanDays: 190);

        var report = monitor.Analyze(Strategy(), trades, Timeframe);

        var returns = pnls.Select(p => p / 100m).ToList();
        var mean = returns.Average();
        var variance = returns.Select(r => (r - mean) * (r - mean)).Sum() / returns.Count;
        var stdDev = (decimal)Math.Sqrt((double)variance);
        var tradesPerYear = 20m / 190m * 365m;
        var expectedTradeSharpe = mean / stdDev * (decimal)Math.Sqrt((double)tradesPerYear);

        Assert.NotNull(report.RealizedTradeSharpe);
        Assert.Equal(expectedTradeSharpe, report.RealizedTradeSharpe!.Value, 6);
    }

    /// <summary>
    /// Blocca la formula M5: RealizedSharpe = mean/std (popolazione) dei rendimenti PER BUCKET
    /// (vuoti inclusi come 0) × sqrt(bucket/anno). Ricalcolo indipendente sui dati grezzi.
    /// </summary>
    [Fact]
    public void RealizedSharpe_MatchesIndependentlyComputedPeriodAnnualization()
    {
        var monitor = new StrategyDecayMonitor();
        var pnls = new[] { 1.5m, -0.8m, 2.1m, 0.3m, -1.2m, 1.8m, 0.6m, -0.4m, 1.1m, 0.9m,
                            1.5m, -0.8m, 2.1m, 0.3m, -1.2m, 1.8m, 0.6m, -0.4m, 1.1m, 0.9m };
        var trades = Trades(20, pnls, spanDays: 190);

        var report = monitor.Analyze(Strategy(), trades, Timeframe);

        var (buckets, bucketsPerYear) = StrategyDecayMonitor.BuildPeriodReturns(trades, Timeframe);
        var mean = buckets.Average();
        var variance = buckets.Select(r => (r - mean) * (r - mean)).Sum() / buckets.Count;
        var stdDev = (decimal)Math.Sqrt((double)variance);
        var expected = mean / stdDev * (decimal)Math.Sqrt((double)bucketsPerYear);

        Assert.NotNull(report.RealizedSharpe);
        Assert.Equal(expected, report.RealizedSharpe!.Value, 6);
    }

    [Fact]
    public void BuildPeriodReturns_ExactBucketVector()
    {
        // 3 trade su timeframe 1h: t0 (bucket 0), t0+30min (ancora bucket 0), t0+2h10min (bucket 2).
        var t0 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var trades = new List<TradeRecord>
        {
            new() { StrategyId = "s", PnlPercent = 1m, ClosedAtUtc = t0 },
            new() { StrategyId = "s", PnlPercent = 0.5m, ClosedAtUtc = t0.AddMinutes(30) },
            new() { StrategyId = "s", PnlPercent = -2m, ClosedAtUtc = t0.AddMinutes(130) },
        };

        var (returns, bucketsPerYear) = StrategyDecayMonitor.BuildPeriodReturns(trades, "1h");

        Assert.Equal(3, returns.Count);                    // bucket 0,1,2
        Assert.Equal(0.015m, returns[0]);                  // 1% + 0.5% nello stesso bucket
        Assert.Equal(0m, returns[1]);                      // bucket vuoto = candela piatta
        Assert.Equal(-0.02m, returns[2]);
        Assert.Equal(365m * 24m, bucketsPerYear);          // k=1: bucket = 1 candela 1h
    }

    [Fact]
    public void BuildPeriodReturns_SpanBeyondMaxBuckets_CoarsensBucketAndScalesAnnualization()
    {
        // 10 candele 1h di span con maxBuckets=5 → k=2 (bucket da 2h), bucket/anno dimezzati.
        var t0 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var trades = new List<TradeRecord>();
        for (var i = 0; i < 10; i++)
        {
            trades.Add(new TradeRecord { StrategyId = "s", PnlPercent = 1m, ClosedAtUtc = t0.AddHours(i) });
        }

        var (returns, bucketsPerYear) = StrategyDecayMonitor.BuildPeriodReturns(trades, "1h", maxBuckets: 5);

        Assert.Equal(5, returns.Count);                    // 10 candele in 5 bucket da 2 candele
        Assert.All(returns, r => Assert.Equal(0.02m, r));  // 2 trade da 1% per bucket
        Assert.Equal(365m * 24m / 2m, bucketsPerYear);
    }

    /// <summary>
    /// Sanity same-basis (piano M5): uno stream con ESATTAMENTE 1 trade per candela e rendimento
    /// per-trade = rendimento per-candela deve produrre uno Sharpe realizzato nell'intorno dello
    /// Sharpe holdout calcolato da <see cref="Statistics.SharpeRatio"/> sull'equity curve
    /// equivalente (stessa convenzione: per-candela, popolazione, sqrt(candele/anno)).
    /// </summary>
    [Fact]
    public void SameBasisSanity_OneTradePerCandle_MatchesHoldoutSharpeWithin25Percent()
    {
        var monitor = new StrategyDecayMonitor();
        var rng = new Random(42);
        var t0 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var trades = new List<TradeRecord>();
        var equity = new List<EquityPoint> { new() { Timestamp = t0.AddHours(-1), Capital = 10_000m } };
        for (var i = 0; i < 20; i++)
        {
            var r = (decimal)(rng.NextDouble() * 0.02 - 0.005);   // rendimenti orari in [-0.5%, +1.5%]
            trades.Add(new TradeRecord { StrategyId = "strat-1", PnlPercent = r * 100m, Pnl = r, ClosedAtUtc = t0.AddHours(i) });
            equity.Add(new EquityPoint { Timestamp = t0.AddHours(i), Capital = equity[^1].Capital * (1m + r) });
        }

        var report = monitor.Analyze(Strategy(expectedSharpe: 1m), trades, Timeframe);
        var holdoutSharpe = Statistics.SharpeRatio(equity, Statistics.PeriodsPerYear(Timeframe), riskFreeRateAnnual: 0m);

        Assert.NotNull(report.RealizedSharpe);
        Assert.NotEqual(0m, holdoutSharpe);
        var ratio = report.RealizedSharpe!.Value / holdoutSharpe;
        Assert.InRange(ratio, 0.8m, 1.25m);
    }

    [Fact]
    public void TradesForOtherStrategies_AreIgnored()
    {
        var monitor = new StrategyDecayMonitor();
        var ownTrades = Trades(20, [1.2m, 0.8m, 1.5m, 0.9m], spanDays: 190);
        var otherTrades = Trades(30, [-5m], spanDays: 90).Select(t => { t.StrategyId = "other-strat"; return t; }).ToList();

        var report = monitor.Analyze(Strategy(), ownTrades.Concat(otherTrades).ToList(), Timeframe);

        Assert.Equal(20, report.TradeCount); // le 30 di "other-strat" non devono contaminare il conteggio
    }
}
