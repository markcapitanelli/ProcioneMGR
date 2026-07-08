namespace ProcioneMGR.Data;

/// <summary>
/// Stato persistito dell'ensemble (configurazione + ultimo status), riga singola.
/// I payload sono serializzati in JSON per non vincolare lo schema a strutture in evoluzione.
/// </summary>
public class EnsembleState
{
    public int Id { get; set; }

    /// <summary>Corsia di trading isolata (0 = corsia di default, esistente prima del supporto multi-coppia).</summary>
    public int LaneId { get; set; }

    /// <summary>JSON di EnsembleConfiguration.</summary>
    public string ConfigurationJson { get; set; } = "{}";

    /// <summary>JSON dell'ultimo EnsembleStatus calcolato.</summary>
    public string StatusJson { get; set; } = "{}";

    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Storico dei rebalancing dell'ensemble.</summary>
public class EnsembleRebalanceHistory
{
    public int Id { get; set; }

    /// <summary>Corsia di trading isolata (0 = corsia di default).</summary>
    public int LaneId { get; set; }

    public DateTime Timestamp { get; set; }

    /// <summary>JSON di List&lt;RebalanceAllocation&gt;.</summary>
    public string AllocationsJson { get; set; } = "[]";

    public string Reason { get; set; } = string.Empty;
}
