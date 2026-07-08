namespace ProcioneMGR.Services.Ingestion;

/// <summary>Avanzamento dell'ingestione, riportato alla UI/log.</summary>
public readonly record struct IngestionProgress(long Ingested, long Estimated, string Symbol, string Timeframe)
{
    public int Percent => Estimated <= 0 ? 0 : (int)Math.Min(100, Ingested * 100 / Estimated);
}

/// <summary>Esito sintetico di un'operazione di ingestione.</summary>
public readonly record struct IngestionResult(long CandlesProcessed, bool Cancelled);

/// <summary>
/// Scarica e persiste dati OHLCV storici da un exchange, gestendo paginazione,
/// rate-limit e upsert idempotente sulla tabella OHLCV.
/// </summary>
public interface IOhlcvIngestionService
{
    Task<IngestionResult> IngestHistoricalDataAsync(
        string exchangeName,
        string symbol,
        string timeframe,
        DateTime from,
        DateTime to,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken ct = default);
}
