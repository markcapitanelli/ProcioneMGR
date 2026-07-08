namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// Sincronizza le serie della watchlist: calcola il delta (dall'ultima candela
/// salvata fino a "ora") e lo ingerisce riusando <see cref="IOhlcvIngestionService"/>.
/// Usato sia dal worker schedulato sia dal pulsante "Sync now" della UI.
/// </summary>
public interface IMarketDataSyncService
{
    /// <summary>Sincronizza una singola serie tracciata. Ritorna le candele processate.</summary>
    Task<int> SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default);

    /// <summary>Sincronizza tutte le serie abilitate (resiliente: un errore non blocca le altre).</summary>
    Task SyncAllEnabledAsync(CancellationToken ct = default);
}
