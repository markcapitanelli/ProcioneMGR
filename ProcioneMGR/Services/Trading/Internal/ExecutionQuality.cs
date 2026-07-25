using System.Diagnostics;

namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>
/// [Fase 1 — docs/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Qualità di esecuzione degli ordini di corsia.
///
/// Fino a questa fase la piattaforma misurava l'implementation shortfall <b>solo</b> sugli ordini
/// eseguiti a fette (TWAP/VWAP/Iceberg), dove <c>ExecutionJob.ArrivalPrice</c> era già fissato a t0.
/// Gli ordini di corsia normali — cioè la stragrande maggioranza — catturavano il prezzo di fill ma
/// lo usavano soltanto come <i>guardia</i> (<see cref="FillSanityCheck"/>): mai come misura di costo.
/// Il risultato era che il costo assunto in selezione (<c>PipelineCosts.DefaultSlippagePercent</c>)
/// non aveva alcun riscontro con quello pagato davvero.
///
/// Qui vivono le due primitive che chiudono quel cerchio, tenute deliberatamente stupide e senza
/// dipendenze così da essere testabili in isolamento e riusabili da entrambi i percorsi (apertura e
/// chiusura, Spot e Futures).
/// </summary>
internal static class ExecutionQuality
{
    /// <summary>
    /// Implementation shortfall in punti base fra il prezzo di arrivo (riferimento alla decisione) e
    /// il prezzo eseguito, <b>segnato come costo</b>: positivo = abbiamo eseguito peggio del
    /// riferimento (comprato più caro o venduto più a buon mercato), negativo = price improvement.
    ///
    /// La convenzione è identica a quella già usata per gli ExecutionJob in <c>TradingEngine</c> e in
    /// <c>ExecutionSimulator</c>, di proposito: le due misure devono poter finire nello stesso grafico
    /// senza che nessuno debba ricordarsi di girare un segno.
    ///
    /// Sulle chiusure va passato il lato dell'ordine di chiusura (opposto a quello della posizione),
    /// non il lato della posizione: è quell'ordine che paga lo slittamento.
    /// </summary>
    /// <returns>Lo shortfall in bps, oppure <c>null</c> se manca un termine di paragone valido.</returns>
    public static decimal? ShortfallBps(OrderSide side, decimal? arrivalPrice, decimal? fillPrice)
    {
        if (arrivalPrice is not decimal arrival || arrival <= 0m) return null;
        if (fillPrice is not decimal fill || fill <= 0m) return null;

        var sign = side == OrderSide.Buy ? 1m : -1m;
        return sign * (fill - arrival) / arrival * 10_000m;
    }

    /// <summary>
    /// Esegue la chiamata all'exchange misurandone la durata in millisecondi.
    ///
    /// La misura parte <i>prima</i> della chiamata e include quindi anche l'attesa imposta dal
    /// rate-limiter client-side (<see cref="Exchanges.ExchangeRateLimitHandler"/>). È voluto: il
    /// ritardo che conta per una strategia è quello fra la decisione e l'acknowledgment, e una coda
    /// interna lo produce esattamente come lo produce la rete. Separare i due contributi ha senso
    /// solo dopo aver constatato che la coda pesa — e per constatarlo serve prima questa misura.
    ///
    /// Se la chiamata solleva, non si misura nulla: i client traducono i guasti in risultati
    /// (<c>NetworkUncertain</c>), quindi un'eccezione qui è un caso anomalo e va propagata pulita.
    /// </summary>
    public static async Task<(T Result, int ElapsedMs)> MeasureAsync<T>(Func<Task<T>> call)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var result = await call();
        return (result, (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }
}
