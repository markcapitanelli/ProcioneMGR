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

    /// <summary>
    /// [R2] Attrito per fill propagato all'ottimizzatore. Vedi
    /// <see cref="Optimization.OptimizationConfiguration.SlippagePercent"/> per il motivo per cui
    /// il default è onesto e non zero.
    /// </summary>
    public decimal SlippagePercent { get; set; } = Pipeline.PipelineCosts.DefaultSlippagePercent;

    /// <summary>
    /// [M3] Funding dei perpetual propagato all'ottimizzatore, stessa strada dello slippage e stesso
    /// motivo. Vedi <see cref="Optimization.OptimizationConfiguration.FundingRatePercentPer8h"/>.
    /// </summary>
    public decimal FundingRatePercentPer8h { get; set; } = Pipeline.PipelineCosts.DefaultFundingRatePercentPer8h;

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
    /// Da DOVE viene <see cref="OutOfSampleSharpe"/>.
    ///
    /// <para>[2026-08-22] Stesso rimedio adottato il 2026-08-20 per
    /// <c>SavedMlModel.DeflatedSharpeSource</c>: un numero senza provenienza è stato letto per un
    /// mese come un walk-forward mentre era lo Sharpe in-sample della selezione, arrotondato. Le
    /// sorgenti <b>non sono sulla stessa scala</b> e non andrebbero ordinate insieme senza saperlo —
    /// una è il MASSIMO su centinaia di combinazioni, l'altra una MEDIA su sottoperiodi.</para>
    /// </summary>
    public string? WalkForwardSource { get; set; }

    /// <summary>
    /// Walk-forward vero: parametri scelti sull'in-sample, giudicati sull'OOS che segue
    /// (<c>StrategyDiscoveryEngine</c>). È il <b>massimo</b> su centinaia di combinazioni, quindi
    /// ottimistico per selection bias — è la ragione per cui esiste il Deflated Sharpe.
    /// </summary>
    public const string SourceWalkForward = "WalkForward";

    /// <summary>
    /// <b>Media</b> su sottoperiodi contigui DENTRO il range di selezione, parametri congelati
    /// (<c>StrategyComposer</c>): è una misura di coerenza, non un fuori campione.
    /// </summary>
    public const string SourceSelectionSubPeriods = "SottoperiodiDiSelezione";

    /// <summary>
    /// Nessuna misura ESISTE: il candidato non viene da una discovery (i modelli <c>Ml</c>).
    /// Dichiarato esplicitamente, perché «non impostato» e «non misurato» non sono la stessa cosa —
    /// e lo <c>0m</c> del default era indistinguibile da una misura.
    /// </summary>
    public const string SourceNone = "NonMisurata";

    /// <summary>
    /// Un produttore che non dichiara la provenienza. Non è un valore da scrivere a mano: lo mette
    /// lo scrittore quando trova null, e lo dice nel log del run.
    /// </summary>
    public const string SourceUndeclared = "NonDichiarata";

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
