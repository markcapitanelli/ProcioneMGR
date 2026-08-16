namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// Battito in-process del LOOP di <see cref="MarketDataSyncWorker"/>, letto dall'endpoint
/// <c>/health</c> dell'host ingestion.
///
/// <para>Perché esiste: nell'incidente del 2026-08-14 il worker è morto alle 22:44 UTC (una
/// <c>TaskCanceledException</c> di timeout di rete letta come shutdown) e il pod è rimasto
/// «healthy» per 6 ore — <c>/health</c> era statico e provava solo che Kestrel rispondeva. La
/// liveness di Kubernetes può riavviare un pod col worker parcheggiato SOLO se l'health lo vede.</para>
///
/// <para>Il battito dice «il loop è vivo», non «il ciclo è riuscito»: si scrive a ogni giro del
/// loop, anche con <c>MarketData:Enabled=false</c> (un worker spento di proposito non è un guasto
/// da riavviare) e anche quando il ciclo viene interrotto dal budget (un recupero profondo
/// legittimo non deve costare un SIGKILL — lezione delle probe a 1s del 2026-08-13).</para>
/// </summary>
public sealed class IngestionSyncHeartbeat
{
    private long _lastLoopTickUtcTicks;

    /// <summary>Registra un giro del loop. Chiamato dal worker, mai dall'health.</summary>
    public void BeatLoop(DateTime utcNow) => Interlocked.Exchange(ref _lastLoopTickUtcTicks, utcNow.Ticks);

    /// <summary>Ultimo giro del loop (UTC); <c>null</c> se il worker non è mai partito.</summary>
    public DateTime? LastLoopTickUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastLoopTickUtcTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// Regola pura (testabile con orologio finto, stile <c>HeartbeatMonitorLogic</c>): parcheggiato
    /// = mai battuto, oppure ultimo battito più vecchio della soglia. Il null NON vale «sano»:
    /// un worker mai registrato è esattamente il guasto che questa regola deve vedere.
    /// </summary>
    public static bool IsParked(DateTime? lastLoopTickUtc, DateTime nowUtc, TimeSpan staleAfter) =>
        lastLoopTickUtc is not DateTime last || nowUtc - last > staleAfter;

    /// <summary>
    /// Soglia di parcheggio derivata dall'intervallo di sync, MAI sotto i 30 minuti. Il silenzio
    /// legittimo più lungo del loop è un ciclo abbandonato dal backstop (2× budget = 4× intervallo,
    /// 20 min col default 5): la soglia deve stargli sopra con margine, così scatta solo un loop
    /// davvero morto — mai un recupero lento. Il parcheggio vero dura ore (6h il 2026-08-14):
    /// 30 minuti di latenza di rilevamento sono un buon compromesso contro i falsi riavvii.
    /// </summary>
    public static TimeSpan StaleAfter(TimeSpan syncInterval) =>
        TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(30).Ticks, syncInterval.Ticks * 6));
}
