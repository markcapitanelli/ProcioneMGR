using System.Text.Json;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Un <b>piano di esecuzione live</b> di un'apertura di posizione, distribuita in fette nel tempo
/// reale (TWAP/VWAP/Iceberg) su Testnet/Live (rif. <c>docs/ROADMAP-QLIB.md §1.2</c>). Una riga per
/// corsia, persistita così che un piano sopravviva a un riavvio del processo e sia ispezionabile in
/// UI. Le fette vivono in <see cref="SlicesJson"/> (blob, stesso pattern di
/// <c>PipelineRun.StageSummariesJson</c>): poche fette per job, pochi job attivi per corsia — nessun
/// vantaggio relazionale a questo volume.
///
/// INVARIANTE: solo le APERTURE guidate da segnale diventano un ExecutionJob; ogni chiusura resta
/// sempre immediata. Un job viene creato SOLO dopo che la prima fetta ha effettivamente creato la
/// posizione, così <see cref="PositionId"/> è sempre valido per i job Running.
/// </summary>
public class ExecutionJob
{
    public Guid Id { get; set; }

    /// <summary>Corsia di trading isolata (0 = corsia di default).</summary>
    public int LaneId { get; set; }

    public string StrategyId { get; set; } = string.Empty;

    /// <summary>PositionId della posizione aperta/accresciuta da questo piano (chiave di correlazione).</summary>
    public string PositionId { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;
    public MarketType MarketType { get; set; } = MarketType.Spot;
    public OrderSide Side { get; set; }

    /// <summary>Quantità totale prevista dal piano.</summary>
    public decimal TotalQuantity { get; set; }

    /// <summary>Quantità effettivamente riempita finora (somma dei fill delle fette).</summary>
    public decimal FilledQuantity { get; set; }

    /// <summary>Prezzo medio ponderato di ingresso della posizione dopo i fill accumulati.</summary>
    public decimal EntryPriceWeightedAvg { get; set; }

    /// <summary>"Twap" | "Vwap" | "Iceberg" (mai "Immediate": quello non genera un job).</summary>
    public string Algorithm { get; set; } = string.Empty;

    /// <summary>Ampiezza della finestra di esecuzione, in secondi.</summary>
    public int WindowSeconds { get; set; }

    /// <summary>"Running" | "Completed" | "Cancelled" | "Failed".</summary>
    public string Status { get; set; } = "Running";

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>JSON: List&lt;ExecutionJobSlice&gt; (le fette del piano con il loro stato).</summary>
    public string SlicesJson { get; set; } = "[]";
}

/// <summary>Una fetta del piano: quantità da eseguire a un dato offset dalla creazione del job.</summary>
public sealed class ExecutionJobSlice
{
    /// <summary>Secondi dopo <see cref="ExecutionJob.CreatedAtUtc"/> in cui la fetta è dovuta.</summary>
    public int OffsetSeconds { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>"Pending" | "Filled" | "MergedIntoNext" (dust assorbita) | "Abandoned".</summary>
    public string Status { get; set; } = "Pending";

    public string? ClientOrderId { get; set; }
    public decimal? FilledPrice { get; set; }
    public decimal? FilledQty { get; set; }
}

/// <summary>Serializzazione delle fette dentro <see cref="ExecutionJob.SlicesJson"/>.</summary>
public static class ExecutionJobSlices
{
    private static readonly JsonSerializerOptions Options = new();

    public static string Serialize(IReadOnlyList<ExecutionJobSlice> slices)
        => JsonSerializer.Serialize(slices, Options);

    public static List<ExecutionJobSlice> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<ExecutionJobSlice>>(json, Options) ?? new(); }
        catch (JsonException) { return new(); }
    }
}
