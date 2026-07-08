using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Risk;

namespace ProcioneMGR.Tests;

/// <summary>Test del Performance/Equity Control (Trombetta cap. 8).</summary>
public class PerformanceControlTests
{
    private static List<BacktestTrade> Trades(params decimal[] pnls)
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return pnls.Select((p, i) => new BacktestTrade
        {
            EntryTime = t0.AddDays(i),
            ExitTime = t0.AddDays(i).AddHours(6),
            Pnl = p,
        }).ToList();
    }

    [Fact]
    public void WindowProfitControl_InhibitsAfterLosingWindow_ReactivatesAfterRecovery()
    {
        // Finestra 2, soglia 0: il trade i e' eseguito se la somma degli ultimi 2 PnL
        // originali (fino a i-1) e' > 0. Sequenza: +10, +10, -30, -30, +50, +50.
        //  - i=0,1: buffer in riempimento -> eseguiti.
        //  - i=2: finestra (10,10)=20 > 0 -> eseguito (-30).
        //  - i=3: finestra (10,-30)=-20 -> INIBITO (-30 evitato).
        //  - i=4: finestra (-30,-30)=-60 -> INIBITO (+50 perso).
        //  - i=5: finestra (-30,+50)=20 > 0 -> eseguito (+50).
        var trades = Trades(10m, 10m, -30m, -30m, 50m, 50m);
        var result = new PerformanceControlService().ApplyWindowProfitControl(trades, windowPeriod: 2, threshold: 0m);

        Assert.Equal([true, true, true, false, false, true], result.ExecutedFlags);
        Assert.Equal(60m, result.OriginalProfit);            // somma di tutti
        Assert.Equal(40m, result.ControlledProfit);          // 10+10-30+50
        Assert.Equal(4, result.ControlledTradeCount);
        // Il controllo evita il secondo -30: il DD controllato e' minore dell'originale.
        Assert.True(result.ControlledMaxDrawdown < result.OriginalMaxDrawdown);
    }

    [Fact]
    public void WindowProfitControl_AllWinners_ExecutesEverything()
    {
        var trades = Trades(10m, 20m, 30m, 40m);
        var result = new PerformanceControlService().ApplyWindowProfitControl(trades, windowPeriod: 2);

        Assert.All(result.ExecutedFlags, f => Assert.True(f));
        Assert.Equal(result.OriginalProfit, result.ControlledProfit);
        Assert.Equal(1m, result.ProfitRetention);
    }

    [Fact]
    public void EquityMovingAverageControl_StopsInDrawdown()
    {
        // Trend positivo poi crollo: l'equity scende sotto la propria SMA e i trade
        // successivi vengono inibiti.
        var trades = Trades(10m, 10m, 10m, 10m, -50m, -50m, -50m, -50m);
        var result = new PerformanceControlService().ApplyEquityMovingAverageControl(trades, smaPeriod: 3);

        // I primi 3 sono nel warm-up; dopo il crollo l'equity finisce sotto SMA e inibisce.
        Assert.True(result.ControlledTradeCount < result.OriginalTradeCount);
        Assert.True(result.ControlledMaxDrawdown < result.OriginalMaxDrawdown);
        Assert.True(result.ControlledProfit > result.OriginalProfit); // qui il controllo evita perdite
    }

    [Fact]
    public void Result_Reports_AreConsistentWithCurves()
    {
        var trades = Trades(100m, -50m, 80m, -120m, 60m);
        var result = new PerformanceControlService().ApplyWindowProfitControl(trades, windowPeriod: 2, threshold: 0m);

        Assert.Equal(result.OriginalProfit, result.OriginalReport.NetProfit);
        Assert.Equal(result.ControlledProfit, result.ControlledReport.NetProfit);
        Assert.Equal(result.ControlledTradeCount, result.ControlledReport.OperationCount);
        Assert.Equal(trades.Count, result.OriginalEquity.Count);
        Assert.Equal(trades.Count, result.ControlledEquity.Count);
    }

    [Fact]
    public void NestedReports_DrawdownConsistentWithTopLevel()
    {
        // Primo trade in perdita: il draw down deve partire dal picco 0 (prima di ogni trade)
        // sia nei campi top-level sia nei TradeReport annidati.
        var trades = Trades(-100m, 50m, 200m);
        var result = new PerformanceControlService().ApplyWindowProfitControl(trades, windowPeriod: 2, threshold: 0m);

        Assert.Equal(result.OriginalMaxDrawdown, result.OriginalReport.MaxDrawdownMoney);
        Assert.Equal(100m, result.OriginalMaxDrawdown); // da 0 a -100
    }

    [Fact]
    public void EmptyTrades_NoThrow()
    {
        var result = new PerformanceControlService().ApplyWindowProfitControl([], windowPeriod: 5);
        Assert.Equal(0, result.OriginalTradeCount);
        Assert.Equal(0m, result.ProfitRetention);
    }
}
