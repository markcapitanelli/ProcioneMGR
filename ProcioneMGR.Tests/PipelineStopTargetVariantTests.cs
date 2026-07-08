using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Pipeline.Stages;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica il parsing/applicazione delle varianti stop+target della prova di robustezza, esteso
/// per includere il TAKE PROFIT e le combinazioni SL+TP ("SL2_TP4"), e l'auto-inserimento delle
/// varianti TP per le cacce che elencano solo stop (autonomia).
/// </summary>
public class PipelineStopTargetVariantTests
{
    [Fact]
    public void ApplyVariant_Combined_SetsStopAndTakeProfit()
    {
        var cfg = new BacktestConfiguration();
        RobustnessProbeStage.ApplyVariant(cfg, "SL2_TP4");
        Assert.Equal(2m, cfg.StopLossPercent);
        Assert.Equal(4m, cfg.TakeProfitPercent);
        Assert.Equal(0m, cfg.TrailingStopPercent);
    }

    [Fact]
    public void ApplyVariant_TakeProfitOnly_SetsOnlyTakeProfit()
    {
        var cfg = new BacktestConfiguration();
        RobustnessProbeStage.ApplyVariant(cfg, "TP6");
        Assert.Equal(6m, cfg.TakeProfitPercent);
        Assert.Equal(0m, cfg.StopLossPercent);
    }

    [Fact]
    public void ApplyVariant_TrailingPlusTakeProfit_SetsBoth()
    {
        var cfg = new BacktestConfiguration();
        RobustnessProbeStage.ApplyVariant(cfg, "TRAIL5_TP8");
        Assert.Equal(5m, cfg.TrailingStopPercent);
        Assert.Equal(8m, cfg.TakeProfitPercent);
        Assert.Equal(0m, cfg.StopLossPercent);
    }

    [Fact]
    public void ApplyVariant_Base_SetsNothing()
    {
        var cfg = new BacktestConfiguration();
        RobustnessProbeStage.ApplyVariant(cfg, "base");
        Assert.Equal(0m, cfg.StopLossPercent);
        Assert.Equal(0m, cfg.TakeProfitPercent);
        Assert.Equal(0m, cfg.TrailingStopPercent);
    }

    [Fact]
    public void EnsureTakeProfitVariants_AddsTpGridWhenAbsent()
    {
        var result = RobustnessProbeStage.EnsureTakeProfitVariants(["base", "SL3", "TRAIL5"]);
        Assert.Contains("base", result);
        Assert.Contains("SL3", result);
        Assert.Contains(result, v => v.Contains("TP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureTakeProfitVariants_LeavesExplicitTpListUntouched()
    {
        var input = new List<string> { "base", "SL3_TP6" };
        var result = RobustnessProbeStage.EnsureTakeProfitVariants(input);
        Assert.Equal(input, result); // già presente un TP → nessuna aggiunta
    }
}
