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

/// <summary>
/// Come esplorare lo spazio dei parametri. GridSearch (default) = prodotto cartesiano esaustivo.
/// Bayesian = ricerca guidata (Gaussian Process + Expected Improvement) quando lo spazio è grande
/// e ogni valutazione (un walk-forward) è costosa. Entrambi restano vincolati allo stesso
/// walk-forward e allo stesso verdetto finale (Deflated Sharpe su tutti i punti visitati).
/// </summary>
public enum SearchStrategy
{
    GridSearch,
    Bayesian,
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

    /// <summary>
    /// [R2] Attrito sfavorevole applicato a OGNI fill, in % del prezzo. Prima non esisteva su questo
    /// modello e <c>BuildBacktestConfig</c> non lo impostava: l'intera SELEZIONE dei parametri (e,
    /// a cascata, quella dei candidati di Discovery) girava a sole commissioni, mentre la successiva
    /// validazione holdout della pipeline applicava i costi pieni.
    ///
    /// L'asimmetria non è solo contabile, è di SELEZIONE: ottimizzando senza attrito si premiano i
    /// parametri ad alto turnover, il cui vantaggio apparente è proprio il costo che non si sta
    /// pagando. Sui timeframe lenti è un'ottimismo modesto; a 1m, dove una strategia può girare
    /// decine di volte al giorno, lo slippage pesa quanto la commissione e la classifica dei
    /// candidati si riempie di strategie che perdono denaro.
    ///
    /// Default = <see cref="Pipeline.PipelineCosts.DefaultSlippagePercent"/>: il default ONESTO,
    /// non zero. Chi vuole il comportamento storico senza attrito deve chiederlo esplicitamente.
    /// </summary>
    public decimal SlippagePercent { get; set; } = Pipeline.PipelineCosts.DefaultSlippagePercent;

    /// <summary>% del capitale impegnata per trade durante l'ottimizzazione.</summary>
    public decimal PositionSizePercent { get; set; } = 100m;

    public string StrategyName { get; set; } = string.Empty;
    public List<ParameterRange> ParameterRanges { get; set; } = new();
    public WalkForwardConfiguration WalkForward { get; set; } = new();

    /// <summary>Come selezionare i parametri della finestra. Default = in-sample (corretto).</summary>
    public OptimizationSelectionMetric SelectionMetric { get; set; } = OptimizationSelectionMetric.InSampleSharpe;

    /// <summary>Strategia di ricerca. Default = GridSearch (comportamento storico bit-identico).</summary>
    public SearchStrategy SearchStrategy { get; set; } = SearchStrategy.GridSearch;

    /// <summary>Ramo Bayesian: passi guidati (Expected Improvement) DOPO l'esplorazione iniziale, per finestra.</summary>
    public int BayesianIterations { get; set; } = 40;

    /// <summary>Ramo Bayesian: punti iniziali casuali (esplorazione), per finestra.</summary>
    public int BayesianInitialRandom { get; set; } = 8;

    /// <summary>Ramo Bayesian: seme — la ricerca è deterministica a parità di seme e di storia.</summary>
    public int BayesianSeed { get; set; } = 42;
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

    /// <summary>
    /// [T0.1 roadmap macchina-ricerca] Barre di cuscinetto SALTATE all'inizio di ogni finestra
    /// out-of-sample. Senza, IS e OOS sono contigui (<c>oosStart = isEnd</c>) e l'informazione
    /// filtra attraverso il confine: una posizione aperta a fine IS prosegue nell'OOS, un
    /// indicatore con lookback L vede fino a L barre di IS. Stessa semantica dell'embargo di
    /// <c>PurgedTimeSeriesCv</c>, che finora la piattaforma usava solo nel percorso ML.
    /// Default 0 = comportamento storico invariato. Valore sensato: il lookback massimo degli
    /// indicatori della strategia (es. 200 per una SMA200).
    /// </summary>
    public int EmbargoBars { get; set; }
}

/// <summary>
/// [T1.6 roadmap macchina-ricerca] Configurazione della validazione CPCV per il percorso strategie:
/// invece di UN solo percorso out-of-sample (walk-forward + holdout), C(gruppi, gruppiTest)
/// combinazioni di gruppi contigui → una DISTRIBUZIONE di Sharpe fuori campione per candidato.
/// </summary>
public sealed class CpcvConfiguration
{
    /// <summary>Gruppi temporali contigui in cui dividere la serie.</summary>
    public int Groups { get; set; } = 8;

    /// <summary>Gruppi usati come test in ogni combinazione: C(Groups, TestGroups) percorsi.</summary>
    public int TestGroups { get; set; } = 2;

    /// <summary>Barre rimosse dal train PRIMA di ogni gruppo di test (stessa semantica di CombinatorialPurgedCv).</summary>
    public int PurgeBars { get; set; }

    /// <summary>Barre rimosse dal train DOPO ogni gruppo di test.</summary>
    public int EmbargoBars { get; set; }
}

/// <summary>Un percorso CPCV: la combinazione, i parametri scelti sul train e l'esito sul test mai visto.</summary>
public sealed class CpcvPathResult
{
    public int Combination { get; set; }
    public IReadOnlyList<int> TestGroups { get; set; } = [];
    public Dictionary<string, decimal> BestParameters { get; set; } = new();
    public decimal TrainSharpe { get; set; }
    public decimal OosSharpe { get; set; }
}

/// <summary>
/// Esito CPCV: la distribuzione degli Sharpe out-of-sample sui percorsi è il prodotto — non un
/// numero solo ma quanti percorsi reggono, con che mediana e con che code. Il PBO è calcolato sul
/// pannello dei rendimenti full-period dei candidati (CSCV, riusa BacktestOverfitting).
/// </summary>
public sealed class CpcvResult
{
    public List<CpcvPathResult> Paths { get; set; } = [];
    public decimal MedianOosSharpe { get; set; }
    public decimal P05OosSharpe { get; set; }
    public decimal P95OosSharpe { get; set; }
    public int PositivePaths { get; set; }
    public int TotalPaths { get; set; }
    public double? Pbo { get; set; }
    public int CombinationsTested { get; set; }

    /// <summary>Parametri più spesso scelti sui train dei percorsi (moda): il candidato "stabile".</summary>
    public Dictionary<string, decimal> ModalParameters { get; set; } = new();

    /// <summary>Quota di percorsi in cui la scelta del train coincide con la moda: stabilità della selezione.</summary>
    public decimal SelectionStability { get; set; }
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
