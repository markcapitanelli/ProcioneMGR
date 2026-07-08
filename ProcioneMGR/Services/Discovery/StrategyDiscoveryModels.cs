using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Services.Discovery;

/// <summary>
/// Configurazione della ricerca di strategie: spazza un universo di
/// (strategia × coppia × timeframe) e, per ciascuna, ottimizza i parametri in walk-forward.
/// </summary>
public class StrategyDiscoveryConfiguration
{
    public string ExchangeName { get; set; } = "Binance";
    public List<string> Symbols { get; set; } = new();
    public List<string> Timeframes { get; set; } = new();

    /// <summary>Nomi strategia da provare (vuoto = tutte quelle disponibili).</summary>
    public List<string> Strategies { get; set; } = new();

    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public decimal InitialCapital { get; set; } = 10000m;
    public decimal CommissionPercent { get; set; } = 0.1m;
    public WalkForwardConfiguration WalkForward { get; set; } = new();

    /// <summary>Quante candidate restituire (ordinate per Sharpe out-of-sample).</summary>
    public int TopN { get; set; } = 20;
}

/// <summary>Una candidata: la migliore combinazione di parametri per una (strategia, coppia, timeframe).</summary>
public class DiscoveryCandidate
{
    public string StrategyName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public Dictionary<string, decimal> Parameters { get; set; } = new();
    public decimal OutOfSampleSharpe { get; set; }
    public decimal InSampleSharpe { get; set; }
    public decimal TotalReturn { get; set; }
    public decimal MaxDrawdown { get; set; }
    public int TotalTrades { get; set; }
    public int Windows { get; set; }

    /// <summary>
    /// Verdetto anti-overfitting (Fase 1) ereditato dallo sweep di ottimizzazione della candidata:
    /// Deflated Sharpe che corregge lo Sharpe OOS per il numero di combinazioni provate. null se non
    /// calcolabile. Permette di ordinare/filtrare le candidate per significatività, non solo per Sharpe.
    /// </summary>
    public Validation.SelectionValidation? Validation { get; set; }
}

public class StrategyDiscoveryResult
{
    /// <summary>Candidate ordinate per Sharpe out-of-sample decrescente (le più "proficue e robuste").</summary>
    public List<DiscoveryCandidate> Candidates { get; set; } = new();
    public int JobsRun { get; set; }
    public int CombinationsTested { get; set; }
    public TimeSpan ExecutionTime { get; set; }
}

public class DiscoveryProgress
{
    public int Completed { get; set; }
    public int Total { get; set; }
    public string Message { get; set; } = string.Empty;
    public decimal BestSharpeSoFar { get; set; }
}
