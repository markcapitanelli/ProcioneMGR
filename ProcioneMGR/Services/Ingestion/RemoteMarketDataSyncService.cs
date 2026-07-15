using System.Net.Http.Json;

namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// Implementazione di <see cref="IMarketDataSyncService"/> che delega la sincronizzazione al
/// microservizio remoto <c>ProcioneMGR.Ingestion</c> via HTTP (Fase 1 microservizi). Attiva nel
/// monolite solo con <c>MarketData:UseRemoteIngestion=true</c>. Trasparente per i consumer
/// (es. il pulsante "Sync now" in Watchlist.razor), che iniettano sempre l'interfaccia.
/// </summary>
public sealed class RemoteMarketDataSyncService(
    HttpClient http,
    ILogger<RemoteMarketDataSyncService> logger) : IMarketDataSyncService
{
    private sealed record SyncResponse(int CandlesProcessed);

    public async Task<int> SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default)
    {
        var resp = await http.PostAsync($"/sync/{trackedSeriesId}", content: null, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<SyncResponse>(ct);
        var n = body?.CandlesProcessed ?? 0;
        logger.LogInformation("Sync remota serie {Id}: {Count} candele.", trackedSeriesId, n);
        return n;
    }

    // Il ciclo periodico su tutte le serie è gestito dal MarketDataSyncWorker DENTRO il servizio
    // Ingestion remoto (che scrive direttamente sul Postgres condiviso), non via HTTP. In modalità
    // UseRemoteIngestion il worker non è registrato nel monolite, quindi questo metodo non ha
    // chiamanti: lanciamo esplicitamente invece di duplicare lo scheduling (che aprirebbe la porta
    // a doppie scritture concorrenti sulla stessa serie).
    public Task SyncAllEnabledAsync(CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Il ciclo periodico è gestito dal worker del servizio Ingestion remoto " +
            "(MarketData:UseRemoteIngestion=true); MarketDataSyncWorker non è registrato nel " +
            "monolite in questa modalità, quindi SyncAllEnabledAsync non deve essere invocato.");
}
