using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Guida il trading engine alimentandolo con le candele. Quando l'engine viene avviato
/// (nuova sessione), riproduce progressivamente le ultime <c>ReplayDays</c> giornate di
/// dati storici (a piccoli batch per tick) così l'attività è osservabile in tempo reale
/// nella UI; una volta raggiunto il presente, elabora le nuove candele man mano che
/// arrivano dal MarketDataSyncWorker.
/// </summary>
public sealed class TradingWorker(
    ITradingEngine engine,
    IEnsembleManager ensemble,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<TradingWorker> logger,
    ILaneLeaseFactory? leaseFactory = null) : BackgroundService
{
    private const int ReplayDays = 30;
    private const int BatchPerTick = 25;
    /// <summary>Ogni quanti tick riverificare che il lease sia ancora vivo (~1 minuto a tick di 2s).</summary>
    private const int LeaseCheckEveryTicks = 30;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LeaseWarnInterval = TimeSpan.FromMinutes(5);

    private DateTime? _sessionStart;
    private DateTime _cursor = DateTime.MinValue;
    private DateTime _lastLeaseWarnUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TradingWorker avviato (tick {Tick}).", Tick);
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        // [B0 PRD core-caldo] Senza lease NON si alimenta il motore: se un altro processo detiene
        // la corsia (deploy incoerente monolite+servizio), qui si resta fermi gridandolo — mai due
        // esecutori sulla stessa corsia per costruzione, applicata dal database.
        ILaneLease? lease = null;
        var ticksSinceLeaseCheck = 0;
        try
        {
            using var timer = new PeriodicTimer(Tick);
            do
            {
                try
                {
                    if (leaseFactory is not null)
                    {
                        if (lease is not null && ++ticksSinceLeaseCheck >= LeaseCheckEveryTicks)
                        {
                            ticksSinceLeaseCheck = 0;
                            if (!await lease.IsAliveAsync(stoppingToken))
                            {
                                logger.LogCritical("Corsia {LaneId}: lease di esecuzione PERSO (connessione caduta) — feed sospeso finché non viene riacquisito.", engine.LaneId);
                                await lease.DisposeAsync();
                                lease = null;
                            }
                        }
                        lease ??= await TryAcquireLeaseAsync(stoppingToken);
                        if (lease is null) continue;
                    }

                    await FeedAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { logger.LogError(ex, "TradingWorker: errore nel feed."); }
            }
            while (await SafeWaitAsync(timer, stoppingToken));
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
    }

    private async Task<ILaneLease?> TryAcquireLeaseAsync(CancellationToken ct)
    {
        try
        {
            var lease = await leaseFactory!.TryAcquireAsync(engine.LaneId, ct);
            if (lease is null && DateTime.UtcNow - _lastLeaseWarnUtc >= LeaseWarnInterval)
            {
                _lastLeaseWarnUtc = DateTime.UtcNow;
                logger.LogCritical(
                    "Corsia {LaneId}: lease di esecuzione detenuto da un ALTRO processo — questo host non alimenta il motore. "
                    + "Verificare Trading:UseRemoteTrading e lo stato del Deployment procionemgr-trading: non devono essere vivi entrambi.",
                    engine.LaneId);
            }
            return lease;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // DB irraggiungibile: senza database non c'è comunque nulla da alimentare. Si ritenta al
            // prossimo tick, con lo stesso rate-limit del warning.
            if (DateTime.UtcNow - _lastLeaseWarnUtc >= LeaseWarnInterval)
            {
                _lastLeaseWarnUtc = DateTime.UtcNow;
                logger.LogError(ex, "Corsia {LaneId}: acquisizione del lease fallita (database irraggiungibile?).", engine.LaneId);
            }
            return null;
        }
    }

    private async Task FeedAsync(CancellationToken ct)
    {
        var status = await engine.GetStatusAsync(ct);
        if (!status.IsRunning || status.IsEmergencyStopped)
        {
            _sessionStart = null; // reset: alla prossima partenza si riparte dal replay
            return;
        }

        // Nuova sessione di trading.
        if (status.StartedAtUtc != _sessionStart)
        {
            _sessionStart = status.StartedAtUtc;
            // Paper: replay osservabile delle ultime giornate. Testnet/Live: SOLO candele nuove
            // (niente replay, altrimenti si piazzerebbero ordini reali in massa sullo storico).
            _cursor = status.Mode == TradingMode.Paper ? DateTime.UtcNow.AddDays(-ReplayDays) : DateTime.UtcNow;
            logger.LogInformation("TradingWorker: nuova sessione {Mode}, cursore da {From:u}.", status.Mode, _cursor);
        }

        var cfg = await ensemble.GetConfigurationAsync(ct);

        // [2026-08-06] SOLO BARRE CHIUSE. Vedi <see cref="LastClosedBarOpenUtc"/>: la riga della
        // candela in formazione è già a database, e consumarla qui valuta stop e target su un
        // High/Low parziale — poi la versione definitiva viene RIFIUTATA da ProcessCandleAsync
        // perché quel timestamp è già nel buffer.
        var lastClosed = LastClosedBarOpenUtc(cfg.Timeframe, DateTime.UtcNow);
        if (lastClosed is not DateTime chiusaFinoA)
        {
            // Timeframe sconosciuto: meglio non alimentare che alimentare barre non chiuse.
            logger.LogWarning("Corsia {LaneId}: timeframe \"{Timeframe}\" non riconosciuto, nessuna candela alimentata.",
                engine.LaneId, cfg.Timeframe);
            return;
        }

        List<OhlcvData> batch;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            batch = await db.OhlcvData
                .Where(c => c.Symbol == cfg.Symbol && c.Timeframe == cfg.Timeframe
                            && c.TimestampUtc > _cursor && c.TimestampUtc <= chiusaFinoA)
                .OrderBy(c => c.TimestampUtc)
                .Take(BatchPerTick)
                .ToListAsync(ct);
        }

        foreach (var c in batch)
        {
            await engine.ProcessCandleAsync(c, ct);
            _cursor = c.TimestampUtc;
        }
    }

    /// <summary>
    /// [2026-08-06] L'istante di APERTURA dell'ultima barra che ha già chiuso. Null se il
    /// timeframe non è riconosciuto.
    ///
    /// <para><b>Il guasto che questa funzione chiude</b>, trovato dal proprietario il 2026-08-06 sulla
    /// corsia 3: uno short ETC/USDT con take profit a 6,3786 non si è chiuso benché il minimo della
    /// barra 4h delle 08:00 fosse 6,31. Il motore aveva valutato quella barra pochi secondi dopo le
    /// 08:00 — quando il minimo era ancora sopra il target — perché l'ingestione REST scrive anche
    /// l'ultima kline INCOMPLETA (Binance la restituisce, e il filtro sull'intervallo la lascia
    /// passare). Il cursore era così avanzato oltre le 08:00, e quando la barra ha chiuso davvero
    /// col minimo vero, <c>ProcessCandleAsync</c> l'ha scartata: <c>candle.TimestampUtc &lt;=
    /// _buffer[^1].TimestampUtc</c> è la guardia anti-replay, e non distingue «già vista» da
    /// «già vista ma incompleta».</para>
    ///
    /// <para>Il risultato non era un errore visibile ma il contrario: il battito diceva «ultima
    /// candela 16:00 · 0 barre indietro» in verde, mentre quella barra chiudeva alle 20:00. Su 4h il
    /// punto cieco arriva a quattro ore di prezzi.</para>
    ///
    /// <para>Il feed real-time non copriva il buco: instrada solo barre CHIUSE, che arrivavano dopo
    /// e venivano rifiutate dalla stessa guardia.</para>
    ///
    /// <para>La regola vive in <see cref="Ingestion.SeriesFreshness.LastClosedBarOpenUtc"/>, accanto
    /// a quella che misura il ritardo: sono la stessa nozione, e separarle rimetterebbe in piedi il
    /// difetto delle «due regole, due verdetti».</para>
    /// </summary>
    internal static DateTime? LastClosedBarOpenUtc(string timeframe, DateTime nowUtc)
        => Ingestion.SeriesFreshness.LastClosedBarOpenUtc(timeframe, nowUtc);

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
