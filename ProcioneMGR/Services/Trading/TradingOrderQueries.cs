namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Composizione delle query di LETTURA sugli ordini, condivisa fra <see cref="TradingEngine"/> e
/// <see cref="RemoteTradingEngineClient"/> (che le esegue in bypass di gRPC: sono query pure su
/// Postgres, senza stato in-memory del motore).
///
/// Esiste per eliminare una deriva alla radice: in Fase 2b il client remoto portava una COPIA
/// riga-per-riga di queste query, tenuta allineata da un commento ("se cambia là, aggiorna qua") e
/// da test head-to-head. Ma quei test confrontano i risultati sui dati che il test semina: un
/// filtro aggiunto solo lato engine su una dimensione non seminata avrebbe prodotto risultati
/// identici comunque, e la divergenza sarebbe passata inosservata — le due modalità avrebbero
/// mostrato ordini diversi in produzione. Con la composizione unica la deriva è impossibile per
/// costruzione; i test head-to-head restano come cintura contro una futura re-duplicazione.
///
/// Prende IQueryable e non il DbContext: il chiamante decide tracking (AsNoTracking o no) e
/// materializzazione — qui vive solo il CRITERIO, cioè la parte che non deve divergere.
/// </summary>
public static class TradingOrderQueries
{
    /// <summary>
    /// Storico ordini della corsia, più recenti prima, cap a 500 righe (è ciò che la UI mostra;
    /// nessun consumer automatico legge lo storico). <paramref name="from"/> opzionale su
    /// CreatedAtUtc.
    /// </summary>
    public static IQueryable<Order> History(IQueryable<Order> orders, int laneId, DateTime? from)
    {
        var q = orders.Where(o => o.LaneId == laneId);
        if (from is DateTime f) q = q.Where(o => o.CreatedAtUtc >= f);
        return q.OrderByDescending(o => o.CreatedAtUtc).Take(500);
    }

    /// <summary>
    /// Ordini Live in attesa di conferma manuale dell'operatore, più recenti prima. Il filtro
    /// Mode==Live è sostanza, non ottimizzazione: Paper/Testnet non passano dalla coda di conferma.
    /// </summary>
    public static IQueryable<Order> PendingLive(IQueryable<Order> orders, int laneId) =>
        orders.Where(o => o.LaneId == laneId && o.Status == OrderStatus.Pending && o.Mode == TradingMode.Live)
              .OrderByDescending(o => o.CreatedAtUtc);
}
