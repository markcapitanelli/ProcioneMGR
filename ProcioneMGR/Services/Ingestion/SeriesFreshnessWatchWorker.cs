using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Notifications;

namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// [E7] Guardia di freschezza delle serie: applica la regola UNICA di <see cref="SeriesFreshness"/>
/// a tutte le serie abilitate della watchlist e NOTIFICA la TRANSIZIONE a ferma — una volta per
/// serie, non una per giro.
///
/// <para>Perché esiste: B2.a ha costruito la regola, ma il suo esito viveva in un
/// <c>LogWarning</c> del pod di ingestion e nel tool CLI <c>coverage</c> — MKR/USDT è stata ferma
/// DIECI MESI con `/watchlist` che diceva «Abilitata». La lezione di D2.a: di un guasto ci si deve
/// accorgere <em>senza doverci pensare</em>, non aprendo i log giusti al momento giusto.</para>
///
/// <para>Vive nel GUSCIO e legge solo il database, quindi funziona identico con l'ingestion locale
/// o remota — è di proposito indipendente da dove giri il sync, perché è il sync l'imputato che
/// deve sorvegliare. Nessuna azione automatica: disabilitare una serie resta una scelta umana
/// (un BREAK può essere temporaneo, decisione B2.a).</para>
/// </summary>
public sealed class SeriesFreshnessWatchWorker(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<SeriesFreshnessWatchWorker> logger,
    INotifier? notifier = null,
    TimeProvider? timeProvider = null,
    IConfiguration? configuration = null) : BackgroundService
{
    /// <summary>Cadenza fissa: la freschezza si muove al passo delle barre, non serve di più.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Serie già segnalate come ferme (per id): l'allarme è sulla transizione.</summary>
    private readonly HashSet<int> _staleAlerted = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SeriesFreshnessWatchWorker avviato (controllo ogni {Interval}).", Interval);
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "Controllo freschezza serie fallito; ritento al prossimo giro."); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    /// <summary>
    /// Un giro di controllo. Pubblico per i test. Restituisce le serie appena DIVENTATE ferme in
    /// questo giro (quelle per cui è partita la notifica): il chiamante di test può così
    /// distinguere «ferma e già nota» da «ferma e appena scoperta».
    /// </summary>
    public async Task<IReadOnlyList<string>> TickAsync(CancellationToken ct)
    {
        var nowUtc = _time.GetUtcNow().UtcDateTime;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var series = await db.TrackedSeries.AsNoTracking()
            .Where(s => s.Enabled)
            .ToListAsync(ct);

        // [F1-F3 PRD Valore] MAX per-serie sull'indice (Symbol, Timeframe, TimestampUtc) invece del
        // GROUP BY sull'INTERA tabella: quello era un seq scan da 15 secondi misurati (12,6M righe)
        // ogni 15 minuti, per rileggere al 99% serie che non cambiano verdetto. Così il costo scala
        // col numero di serie sorvegliate (~221 lookup da pochi ms), non con la storia accumulata.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastBySeries = new Dictionary<(string, string), DateTime?>(series.Count);
        foreach (var s in series)
        {
            if (lastBySeries.ContainsKey((s.Symbol, s.Timeframe))) continue;
            lastBySeries[(s.Symbol, s.Timeframe)] = await db.OhlcvData
                .Where(c => c.Symbol == s.Symbol && c.Timeframe == s.Timeframe)
                .MaxAsync(c => (DateTime?)c.TimestampUtc, ct);
        }
        logger.LogInformation("Freschezza: ultima candela letta per {Count} serie in {Ms}ms.",
            lastBySeries.Count, sw.ElapsedMilliseconds);

        var newlyStale = new List<string>();
        foreach (var s in series)
        {
            var last = lastBySeries.GetValueOrDefault((s.Symbol, s.Timeframe));
            if (!SeriesFreshness.IsStale(s.Timeframe, last, nowUtc))
            {
                // Tornata fresca: l'allarme si riarma, e il recupero si dice nel log (non via
                // notifica: il rientro di un guasto già notificato è informazione, non allarme).
                if (_staleAlerted.Remove(s.Id))
                {
                    logger.LogInformation("Serie {Exchange} {Symbol} {Timeframe}: tornata FRESCA.",
                        s.Exchange, s.Symbol, s.Timeframe);
                }
                continue;
            }

            if (!_staleAlerted.Add(s.Id)) continue; // ferma e già segnalata: silenzio

            var descr = SeriesFreshness.Describe(s.Timeframe, last, nowUtc, candlesProcessed: 0);
            newlyStale.Add($"{s.Exchange} {s.Symbol} {s.Timeframe} — {descr}");
        }

        if (newlyStale.Count > 0)
        {
            logger.LogWarning("Serie FERME appena rilevate ({Count}): {Series}",
                newlyStale.Count, string.Join("; ", newlyStale));

            if (notifier is not null)
            {
                // [2026-08-15] La notifica dice DOVE guardare, non solo cosa è fermo. Il timbro di
                // ciclo (HostHeartbeats, ruolo ingestion-sync) distingue «è il sync fermo» da
                // «è il simbolo sospeso»: nell'incidente del 2026-08-14 il consiglio fisso
                // «verifica BREAK» era sbagliato — l'imputato era il worker morto alle 22:44.
                var stamp = await db.HostHeartbeats.AsNoTracking()
                    .Where(h => h.Host == HostHeartbeat.IngestionSyncRole)
                    .Select(h => new { h.LastUtc, h.Version })
                    .FirstOrDefaultAsync(ct);
                // L'intervallo dichiarato NEL timbro vince sulla config locale: chi timbra (pod
                // ingestion) e chi giudica (guscio) hanno appsettings indipendenti.
                var interval = SyncPulse.TryParseStampedInterval(stamp?.Version)
                    ?? TimeSpan.FromMinutes(Math.Max(1,
                        configuration?.GetValue("MarketData:SyncIntervalMinutes", 5) ?? 5));
                var causa = SyncPulse.DescribeCause(
                    newlyStale.Count, stamp?.LastUtc, nowUtc, interval, stamp?.Version);

                // UNA notifica aggregata per giro, non una per serie: un'interruzione di rete che
                // ferma 200 serie insieme deve produrre un messaggio, non duecento.
                var elenco = string.Join("\n", newlyStale.Take(10));
                if (newlyStale.Count > 10) elenco += $"\n… e altre {newlyStale.Count - 10}";
                await notifier.NotifyAsync(NotificationSeverity.Warning,
                    $"{newlyStale.Count} serie della watchlist FERM{(newlyStale.Count == 1 ? "A" : "E")}",
                    $"L'ultima candela chiusa è oltre la tolleranza ({SeriesFreshness.DefaultToleranceBars} barre):\n"
                    + $"{elenco}\n{causa}", ct);
            }
        }

        return newlyStale;
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
