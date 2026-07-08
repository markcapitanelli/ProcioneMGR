using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Trading;

public interface ITradingEngine
{
    /// <summary>Corsia di trading isolata a cui appartiene questa istanza (0 = corsia di default).</summary>
    int LaneId { get; }

    Task<TradingEngineStatus> GetStatusAsync(CancellationToken ct = default);
    Task StartAsync(TradingMode mode, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task EmergencyStopAsync(string reason, CancellationToken ct = default);
    Task<List<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default);

    /// <summary>Chiusura manuale di una posizione (market, al prezzo corrente).</summary>
    Task ClosePositionAsync(string positionId, CancellationToken ct = default);

    /// <summary>
    /// Imposta/aggiorna stop loss, take profit e trailing stop (%) di una posizione aperta.
    /// Una modifica manuale ha sempre priorità: da qui in poi l'automatismo di apertura non
    /// ritocca più questi valori (si applica solo alla creazione della posizione).
    /// </summary>
    Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct = default);

    /// <summary>Ordini Live in attesa di conferma manuale.</summary>
    Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct = default);

    /// <summary>Conferma un ordine Live in coda: passa la safety e viene piazzato realmente.</summary>
    Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default);

    /// <summary>Rifiuta un ordine Live in coda (non verrà piazzato).</summary>
    Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default);
    Task<List<Order>> GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default);
    Task<TradingPerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default);

    /// <summary>
    /// Elabora una candela (nuova o di replay storico): aggiorna posizioni/PnL, valuta i
    /// segnali delle strategie dell'ensemble, piazza/chiude ordini (con safety check) e
    /// aggiorna l'equity. Usata dal TradingWorker (live) e dal test (replay).
    /// </summary>
    Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default);

    /// <summary>
    /// Avanza le fette dovute dei piani di esecuzione live (TWAP/VWAP/Iceberg) di questa corsia.
    /// Chiamato periodicamente dall'ExecutionWorker; no-op in Paper o se l'esecuzione a fette è
    /// disabilitata. Rif. docs/ROADMAP-QLIB.md §1.2.
    /// </summary>
    Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default);
}
