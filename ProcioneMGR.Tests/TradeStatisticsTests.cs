using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Tests;

/// <summary>Test del performance report basato sui trade (TradeStatistics, Trombetta cap. 6-7).</summary>
public class TradeStatisticsTests
{
    private static BacktestTrade Trade(decimal pnl, DateTime? exit = null) => new()
    {
        EntryTime = (exit ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)).AddDays(-1),
        ExitTime = exit ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Pnl = pnl,
    };

    private static List<EquityPoint> CurveFromPnls(IReadOnlyList<decimal> pnls)
    {
        var list = new List<EquityPoint>(pnls.Count);
        decimal cum = 0m;
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < pnls.Count; i++)
        {
            cum += pnls[i];
            list.Add(new EquityPoint { Timestamp = t0.AddDays(i), Capital = cum });
        }
        return list;
    }

    [Fact]
    public void TradeReport_BasicMetrics_HandComputed()
    {
        // 3 vincenti (100, 200, 300), 2 perdenti (-100, -200).
        var pnls = new List<decimal> { 100m, -100m, 200m, -200m, 300m };
        var trades = pnls.Select(p => Trade(p)).ToList();
        var report = TradeStatistics.ComputeTradeReport(trades, CurveFromPnls(pnls));

        Assert.Equal(300m, report.NetProfit);
        Assert.Equal(600m, report.GrossProfit);
        Assert.Equal(-300m, report.GrossLoss);
        Assert.Equal(2m, report.ProfitFactor);             // 600 / 300
        Assert.Equal(5, report.OperationCount);
        Assert.Equal(60m, report.AverageTrade);            // 300 / 5
        Assert.Equal(60m, report.PercentWin);              // 3/5
        Assert.Equal(200m, report.AverageWin);             // 600/3
        Assert.Equal(-150m, report.AverageLoss);           // -300/2
        Assert.Equal(200m / 150m, report.RewardRiskRatio);
        Assert.Equal(300m, report.MaxWin);
        Assert.Equal(-200m, report.MaxLoss);
    }

    [Fact]
    public void TradeReport_EmptyTrades_AllZero_NoThrow()
    {
        var report = TradeStatistics.ComputeTradeReport([], []);
        Assert.Equal(0m, report.NetProfit);
        Assert.Equal(0m, report.ProfitFactor);
        Assert.Equal(0, report.OperationCount);
        Assert.Equal(0m, report.KestnerRatio);
    }

    [Fact]
    public void DrawdownMoney_KnownCurve_HandComputed()
    {
        // Equity: 100, 150, 120, 90, 160 -> picco 150, valle 90 -> MaxDD = 60.
        // Punti in DD: 120 (30) e 90 (60) -> AvgDD = 45.
        var curve = CurveFromPnls([100m, 50m, -30m, -30m, 70m]);
        var (maxDd, avgDd) = TradeStatistics.DrawdownMoney(curve);
        Assert.Equal(60m, maxDd);
        Assert.Equal(45m, avgDd);
    }

    [Fact]
    public void DelayBetweenPeaks_KnownCurve_HandComputed()
    {
        // Equity: 10, 20, 15, 18, 25, 22 -> ritardo di 2 (15,18) chiuso dal nuovo massimo 25,
        // poi ritardo finale di 1 (22) ancora aperto. Max = 2, media = 1.5.
        var curve = CurveFromPnls([10m, 10m, -5m, 3m, 7m, -3m]);
        var (maxDelay, avgDelay) = TradeStatistics.DelayBetweenPeaks(curve);
        Assert.Equal(2, maxDelay);
        Assert.Equal(1.5m, avgDelay);
    }

    [Fact]
    public void KestnerRatio_LinearGrowth_IsHigh_ErraticIsLower()
    {
        // Crescita perfettamente lineare su 12 mesi -> residui ~0 -> ratio molto alto.
        var linear = Enumerable.Range(1, 12)
            .Select(m => Trade(100m, new DateTime(2024, m, 15, 0, 0, 0, DateTimeKind.Utc)))
            .ToList();

        // Stesso profitto totale ma erratico.
        var erraticPnls = new decimal[] { 500m, -400m, 600m, -300m, 400m, -200m, 300m, -100m, 200m, -100m, 300m, 0m };
        var erratic = erraticPnls
            .Select((p, i) => Trade(p, new DateTime(2024, i + 1, 15, 0, 0, 0, DateTimeKind.Utc)))
            .ToList();

        var krLinear = TradeStatistics.KestnerRatio(linear);
        var krErratic = TradeStatistics.KestnerRatio(erratic);
        Assert.True(krLinear > krErratic);
        Assert.True(krLinear > 0m);
    }

    [Fact]
    public void AnnualAndMonthlyAggregates_GroupCorrectly()
    {
        var trades = new List<BacktestTrade>
        {
            Trade(100m, new DateTime(2023, 3, 10, 0, 0, 0, DateTimeKind.Utc)),
            Trade(-50m, new DateTime(2023, 3, 20, 0, 0, 0, DateTimeKind.Utc)),
            Trade(200m, new DateTime(2024, 3, 5, 0, 0, 0, DateTimeKind.Utc)),
            Trade(80m, new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc)),
        };

        var annual = TradeStatistics.AnnualProfits(trades);
        Assert.Equal([(2023, 50m), (2024, 280m)], annual);

        // Marzo: 50 (2023) e 200 (2024) -> media 125. Luglio: solo 80.
        var monthly = TradeStatistics.MonthlyAverageProfits(trades);
        Assert.Equal(125m, monthly.Single(m => m.Month == 3).AverageProfit);
        Assert.Equal(80m, monthly.Single(m => m.Month == 7).AverageProfit);
        Assert.Equal(0m, monthly.Single(m => m.Month == 1).AverageProfit);

        var matrix = TradeStatistics.MonthlyProfitMatrix(trades);
        Assert.Equal(3, matrix.Count);
        Assert.Contains(matrix, c => c is { Year: 2023, Month: 3, Profit: 50m });
    }

    [Fact]
    public void Gpdi_IdenticalDistributions_Is_Zero_BetterOos_Is100()
    {
        var pnls = new List<decimal> { -100m, -50m, 0m, 50m, 100m, 150m, 200m };

        // Identiche: nessun livello in cui OOS sia STRETTAMENTE migliore -> 0.
        Assert.Equal(0m, TradeStatistics.Gpdi(pnls, pnls));

        // OOS traslata verso l'alto -> migliore su tutti i livelli -> 100.
        var shifted = pnls.Select(p => p + 10m).ToList();
        Assert.Equal(100m, TradeStatistics.Gpdi(pnls, shifted));
    }

    [Fact]
    public void Gpdi_EmptyInput_IsZero()
    {
        Assert.Equal(0m, TradeStatistics.Gpdi([], [1m]));
        Assert.Equal(0m, TradeStatistics.Gpdi([1m], []));
    }
}
