namespace ProcioneMGR.Services.Execution;

/// <summary>Direzione di un ordine di esecuzione (disaccoppiata da OrderSide del layer Trading).</summary>
public enum ExecutionSide
{
    Buy,
    Sell,
}

/// <summary>
/// "Intenzione" di ordine decisa al timeframe di DECISIONE (es. una barra 4h): symbol, lato,
/// quantità totale e prezzo di arrivo (il prezzo di riferimento all'istante della decisione, contro
/// cui si misura l'implementation shortfall). Non contiene ancora COME eseguire: è l'input degli
/// <see cref="IExecutionAlgorithm"/> che producono un piano sul timeframe di ESECUZIONE (es. 5m).
/// </summary>
public sealed record ExecutionIntent(
    string Symbol,
    ExecutionSide Side,
    decimal TotalQuantity,
    decimal ArrivalPrice);

/// <summary>Un ordine figlio: quantità da eseguire nella candela fine di indice <paramref name="CandleIndex"/>.</summary>
public sealed record ExecutionSlice(int CandleIndex, decimal Quantity);

/// <summary>Forma del modello di impatto di mercato in funzione della partecipazione al volume.</summary>
public enum MarketImpactModel
{
    /// <summary>Impatto ∝ partecipazione. Semplice ma sovrastima le grandi partecipazioni.</summary>
    Linear,

    /// <summary>Impatto ∝ √(partecipazione), la legge "square-root" empirica (Almgren et al.): concava,
    /// il costo per unità cresce col volume ma decrescentemente. È l'evidenza empirica standard.</summary>
    SquareRoot,
}

/// <summary>Piano di esecuzione: la sequenza di ordini figli prodotta da un algoritmo.</summary>
public sealed class ExecutionPlan
{
    public required string Algorithm { get; init; }
    public required IReadOnlyList<ExecutionSlice> Slices { get; init; }

    public decimal PlannedQuantity => Slices.Sum(s => s.Quantity);
    public int SliceCount => Slices.Count;
}

/// <summary>
/// Parametri di esecuzione (algoritmi + modello di fill del simulatore). I valori di default sono
/// illustrativi: l'impatto di mercato reale va calibrato/validato in Paper (rif. ROADMAP-QLIB §1.2),
/// non assunto. Il modello di impatto (default √partecipazione, cfr. <see cref="MarketImpactModel"/>)
/// dipende dalla "partecipazione" (quota del volume di candela assorbita dall'ordine figlio),
/// premiando la distribuzione dell'ordine nel tempo.
/// </summary>
public sealed class ExecutionParameters
{
    /// <summary>Numero massimo di fette per TWAP/VWAP.</summary>
    public int MaxSlices { get; set; } = 12;

    /// <summary>Dimensione del clip Iceberg come frazione della quantità totale.</summary>
    public decimal IcebergClipFraction { get; set; } = 0.1m;

    /// <summary>Impatto di mercato per unità di partecipazione (quota del volume di candela).</summary>
    public decimal ImpactCoefficient { get; set; } = 0.1m;

    /// <summary>Forma del modello di impatto (default √partecipazione, la legge empirica di Almgren).</summary>
    public MarketImpactModel ImpactModel { get; set; } = MarketImpactModel.SquareRoot;

    /// <summary>Tetto all'impatto di una singola fetta (evita valori assurdi su candele a volume nullo).</summary>
    public decimal MaxImpactPct { get; set; } = 0.10m;

    /// <summary>Costo fisso di attraversamento dello spread per fill (metà spread).</summary>
    public decimal HalfSpreadPct { get; set; } = 0.0005m;

    /// <summary>
    /// Volatilità di riferimento (deviazione standard dei log-return) usata da Adaptive per calibrare
    /// l'urgenza: sigma_realizzata/ReferenceVolatility &gt; 1 ⇒ mercato più volatile del normale ⇒
    /// esecuzione più front-loaded. Valore illustrativo, da calibrare in Paper (rif. ROADMAP-QLIB §1.2).
    /// </summary>
    public decimal ReferenceVolatility { get; set; } = 0.01m;

    /// <summary>
    /// Tasso di decadimento base per Adaptive, moltiplicato per l'urgency ratio clampato in [0.25, 4.0]
    /// per ottenere il lambda effettivo del peso esponenziale. A urgency=1 (volatilità pari al riferimento)
    /// e lambda=0.15, il rapporto fra la prima e l'ultima fetta è ~1.16x su un profilo di 12 candele.
    /// </summary>
    public decimal DecayBaseRate { get; set; } = 0.15m;
}

/// <summary>Un fill simulato di una fetta.</summary>
public sealed record ExecutionFill(int CandleIndex, decimal Quantity, decimal Price, decimal ParticipationPct);

/// <summary>
/// Esito della simulazione di un piano: prezzo medio di riempimento e implementation shortfall
/// (scostamento dal prezzo di arrivo, segnato come COSTO: positivo = peggio dell'arrivo).
/// </summary>
public sealed class ExecutionResult
{
    public required string Algorithm { get; init; }
    public decimal FilledQuantity { get; init; }
    public decimal AverageFillPrice { get; init; }
    public decimal ArrivalPrice { get; init; }

    /// <summary>Implementation shortfall in punti base (bps), segnato come costo per il lato dell'ordine.</summary>
    public decimal SlippageBps { get; init; }

    public IReadOnlyList<ExecutionFill> Fills { get; init; } = [];
}
