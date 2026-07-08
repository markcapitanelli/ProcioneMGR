using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.Optimization;

/// <summary>
/// Metrica con cui scegliere i parametri "migliori" di ogni finestra walk-forward.
/// Default = InSampleSharpe (corretto: si seleziona sul train, si misura sul test).
/// OutOfSampleSharpe seleziona sul test set: ottimistico/peeking, da usare con cautela.
/// </summary>
public enum OptimizationSelectionMetric
{
    InSampleSharpe,
    OutOfSampleSharpe,
}

public class OptimizationConfiguration
{
    public string ExchangeName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public decimal InitialCapital { get; set; } = 10000m;
    public decimal CommissionPercent { get; set; } = 0.1m;

    /// <summary>% del capitale impegnata per trade durante l'ottimizzazione.</summary>
    public decimal PositionSizePercent { get; set; } = 100m;

    public string StrategyName { get; set; } = string.Empty;
    public List<ParameterRange> ParameterRanges { get; set; } = new();
    public WalkForwardConfiguration WalkForward { get; set; } = new();

    /// <summary>Come selezionare i parametri della finestra. Default = in-sample (corretto).</summary>
    public OptimizationSelectionMetric SelectionMetric { get; set; } = OptimizationSelectionMetric.InSampleSharpe;
}

public class ParameterRange
{
    public string Name { get; set; } = string.Empty;
    public decimal Min { get; set; }
    public decimal Max { get; set; }
    public decimal Step { get; set; }
    public bool IsInteger { get; set; }
}

public class WalkForwardConfiguration
{
    public int InSampleMonths { get; set; } = 12;
    public int OutOfSampleMonths { get; set; } = 3;
    public int StepMonths { get; set; } = 3;
}

public class OptimizationResult
{
    /// <summary>Top 10 combinazioni per Sharpe out-of-sample medio sulle finestre.</summary>
    public List<ParameterSet> BestParameters { get; set; } = new();
    public WalkForwardResult WalkForwardAnalysis { get; set; } = new();

    /// <summary>key = "param1=val1,param2=val2" -> Sharpe out-of-sample medio sulle finestre.</summary>
    public Dictionary<string, decimal> AllResults { get; set; } = new();
    public TimeSpan ExecutionTime { get; set; }
    public int TotalCombinationsTested { get; set; }

    /// <summary>
    /// Verdetto anti-overfitting sul migliore selezionato: Deflated Sharpe che corregge lo Sharpe
    /// grezzo per il selection bias (aver provato N combinazioni). null se non calcolabile
    /// (curva combinata troppo corta o meno di 2 combinazioni). Rif. docs/ROADMAP-QLIB / Fase 1.
    /// </summary>
    public Validation.SelectionValidation? Validation { get; set; }
}

public class ParameterSet
{
    public Dictionary<string, decimal> Parameters { get; set; } = new();
    public decimal InSampleSharpe { get; set; }
    public decimal OutOfSampleSharpe { get; set; }
    public decimal TotalReturn { get; set; }
    public decimal MaxDrawdown { get; set; }
    public int TotalTrades { get; set; }
}

public class WalkForwardResult
{
    public List<WalkForwardWindow> Windows { get; set; } = new();
    public decimal AverageOutOfSampleSharpe { get; set; }

    /// <summary>Equity curve concatenata (compounded) dei soli test out-of-sample.</summary>
    public List<EquityPoint> CombinedEquityCurve { get; set; } = new();
}

public class WalkForwardWindow
{
    public int WindowIndex { get; set; }
    public DateTime InSampleStart { get; set; }
    public DateTime InSampleEnd { get; set; }
    public DateTime OutOfSampleStart { get; set; }
    public DateTime OutOfSampleEnd { get; set; }
    public Dictionary<string, decimal> BestParameters { get; set; } = new();
    public decimal InSampleSharpe { get; set; }
    public decimal OutOfSampleSharpe { get; set; }
    public decimal OutOfSampleReturn { get; set; }
}

public class OptimizationProgress
{
    public int CombinationsTested { get; set; }
    public int TotalCombinations { get; set; }
    public int CurrentWindow { get; set; }
    public int TotalWindows { get; set; }
    public decimal BestSharpeSoFar { get; set; }
    public string Message { get; set; } = string.Empty;
}
