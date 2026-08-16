using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// Implementazione della sincronizzazione incrementale delle serie tracciate.
/// </summary>
public sealed class MarketDataSyncService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOhlcvIngestionService ingestion,
    IConfiguration configuration,
    ILogger<MarketDataSyncService> logger) : IMarketDataSyncService
{
    // Un solo sync per serie alla volta nel processo: il tick del worker e una richiesta manuale
    // (pulsante UI, o POST /sync del servizio Ingestion) sulla stessa serie non devono correre in
    // parallelo — l'upsert è SELECT-poi-INSERT e la collisione sull'indice unico sporcherebbe
    // LastSyncStatus (verificato dal vivo in E2E). Statico: i lock sono di processo, condivisi
    // tra le istanze scoped; mai rimossi (bounded dal numero di serie, costo trascurabile).
    // In modalità remota tutti i percorsi di sync convergono nell'unico processo Ingestion
    // (replicas: 1), quindi questo lock chiude la corsa davvero, non solo in-process.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> SeriesLocks = new();

    public async Task<int> SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default)
    {
        // Attesa LIMITATA, non infinita: se il gate è tenuto da un ciclo abbandonato dal backstop
        // (un task zombie parcheggiato su un await che non riprende mai), un'attesa senza tetto
        // qui appenderebbe la sync manuale per l'intero timeout HTTP. Meglio dirlo subito.
        var gate = SeriesLocks.GetOrAdd(trackedSeriesId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(10), ct))
        {
            throw new InvalidOperationException(
                "Un'altra sincronizzazione di questa serie è già in corso (o è rimasta appesa): riprova fra poco.");
        }
        try
        {
            return await SyncSeriesLockedAsync(trackedSeriesId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Variante del CICLO: se il gate della serie è occupato NON aspetta — salta e riprova al tick
    /// successivo. È la lezione della review post-incidente: un gate tenuto per sempre da un ciclo
    /// abbandonato (backstop) non deve affamare tutte le serie che vengono dopo nell'elenco — una
    /// serie bloccata deve costare UNA serie, mai il resto del ciclo. Il vincolo un-solo-scrittore
    /// resta intatto: chi non prende il gate non scrive.
    /// </summary>
    private async Task<bool> TrySyncSeriesForCycleAsync(int trackedSeriesId, CancellationToken ct)
    {
        var gate = SeriesLocks.GetOrAdd(trackedSeriesId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(2), ct))
        {
            logger.LogWarning(
                "Serie {Id}: gate occupato (sync manuale in corso o ciclo abbandonato che non l'ha mai rilasciato); "
                + "saltata in questo ciclo, si riprova al prossimo.", trackedSeriesId);
            return false;
        }
        try
        {
            await SyncSeriesLockedAsync(trackedSeriesId, ct);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> SyncSeriesLockedAsync(int trackedSeriesId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var series = await db.TrackedSeries.FirstOrDefaultAsync(s => s.Id == trackedSeriesId, ct);
        if (series is null)
        {
            return 0;
        }

        // Cursore incrementale: riparti dall'ultima candela salvata (la ri-scarichiamo
        // perche' potrebbe essere stata ancora "aperta"); se non c'e' nulla, backfill.
        var lastStored = await db.OhlcvData
            .Where(c => c.Symbol == series.Symbol && c.Timeframe == series.Timeframe)
            .MaxAsync(c => (DateTime?)c.TimestampUtc, ct);

        var backfillDays = configuration.GetValue("MarketData:DefaultBackfillDays", 7);
        var from = lastStored ?? DateTime.UtcNow.AddDays(-backfillDays);
        var to = DateTime.UtcNow;

        try
        {
            var result = await ingestion.IngestHistoricalDataAsync(
                series.Exchange.ToString(), series.Symbol, series.Timeframe, from, to, progress: null, ct);

            // [B2] L'esito NON si deduce dal numero di candele processate. Su una serie ferma il
            // cursore ri-chiede l'ultima candela nota, l'exchange la restituisce e l'upsert la
            // riscrive: "OK: 1 candele" a ogni giro, per sempre — che è come MKR/USDT ha
            // attraversato dieci mesi dichiarandosi sana. Conta dove è arrivata la serie, non
            // quante righe ha toccato il giro.
            var lastAfter = await db.OhlcvData
                .Where(c => c.Symbol == series.Symbol && c.Timeframe == series.Timeframe)
                .MaxAsync(c => (DateTime?)c.TimestampUtc, ct);

            var now = DateTime.UtcNow;
            // [2026-08-16] La tolleranza tiene conto della CADENZA di sync: una serie non può
            // essere più fresca di quanto il ciclo permetta, e sui timeframe più fini
            // dell'intervallo (1m) la soglia in barre era insoddisfacibile — «FERMA» scritto in
            // LastSyncStatus a ogni giro su una serie perfettamente sana.
            var interval = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("MarketData:SyncIntervalMinutes", 5)));
            var tolerance = SeriesFreshness.EffectiveToleranceBars(
                series.Timeframe, interval,
                configuration.GetValue("MarketData:StaleAfterBars", SeriesFreshness.DefaultToleranceBars));

            series.LastSyncUtc = now;
            series.LastSyncStatus = SeriesFreshness.Describe(
                series.Timeframe, lastAfter, now, result.CandlesProcessed, tolerance);

            if (SeriesFreshness.IsStale(series.Timeframe, lastAfter, now, tolerance))
            {
                // A voce alta: una serie ferma che nessuno nota è un buco nei dati che diventa un
                // buco nelle decisioni. Il worker non la disabilita da solo — potrebbe essere un
                // guasto temporaneo dell'exchange, e spegnere una serie è una scelta umana.
                logger.LogWarning(
                    "Serie FERMA: {Symbol} {Timeframe} su {Exchange} — ultima candela {Last}, {Behind} barre indietro. "
                    + "La sync riesce ma non porta dati nuovi: simbolo delistato, rinominato o sospeso?",
                    series.Symbol, series.Timeframe, series.Exchange,
                    lastAfter?.ToString("u") ?? "nessuna",
                    SeriesFreshness.BarsBehind(series.Timeframe, lastAfter, now)?.ToString() ?? "?");
            }

            await db.SaveChangesAsync(ct);
            return (int)result.CandlesProcessed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellazione VERA del chiamante (budget di ciclo o shutdown): risale. Il filtro sul
            // token è l'unico discriminatore affidabile — l'incidente del 2026-08-14 (22:44, 122
            // serie ferme per 6 ore) è nato da una TaskCanceledException di TIMEOUT DI RETE
            // (ExchangeRateLimitHandler la sintetizza con Token=None) che, rilanciata da qui senza
            // filtro, il worker leggeva come shutdown e usciva dal loop per sempre. Un timeout di
            // rete è un errore della serie come gli altri: cade nel catch sotto, scrive «Errore:»
            // e il ciclo prosegue con la serie successiva.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync fallita per {Symbol} {Timeframe} su {Exchange}.",
                series.Symbol, series.Timeframe, series.Exchange);
            series.LastSyncUtc = DateTime.UtcNow;
            series.LastSyncStatus = $"Errore: {Trunc(ex.Message)}";
            await db.SaveChangesAsync(CancellationToken.None);
            return 0;
        }
    }

    public async Task SyncAllEnabledAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.TrackedSeries
            .Where(s => s.Enabled)
            .Select(s => s.Id)
            .ToListAsync(ct);

        logger.LogInformation("Sync ciclo: {Count} serie abilitate.", ids.Count);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var skipped = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            // Il percorso per-serie e' resiliente agli errori della singola serie, INCLUSI i
            // timeout di rete travestiti da cancellazione (filtrati sul token in
            // SyncSeriesLockedAsync) e il gate occupato (saltato, non atteso): qui risale solo la
            // cancellazione vera del token di ciclo.
            if (!await TrySyncSeriesForCycleAsync(id, ct))
            {
                skipped++;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        // Il log di ciclo COMPLETATO è il battito che mancava nell'incidente del 2026-08-14:
        // c'era solo quello di inizio, e un worker morto dopo un ciclo riuscito era
        // indistinguibile, nei log, da uno vivo fra un tick e l'altro.
        logger.LogInformation("Sync ciclo completato: {Count} serie ({Skipped} saltate per gate occupato) in {Elapsed:hh\\:mm\\:ss}.",
            ids.Count, skipped, sw.Elapsed);
    }

    private static string Trunc(string s) => s.Length <= 200 ? s : s[..200];
}
