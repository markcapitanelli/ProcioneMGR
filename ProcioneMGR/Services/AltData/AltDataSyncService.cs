using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Sentiment;

namespace ProcioneMGR.Services.AltData;

public interface IAltDataSyncService
{
    /// <summary>Interroga tutte le fonti registrate, classifica/scora le notizie nuove e le salva. Restituisce quante ne ha inserite.</summary>
    Task<int> SyncAllAsync(CancellationToken ct);
}

/// <summary>
/// Implementazione di <see cref="IAltDataSyncService"/>. Deduplica per Source+Url (o Source+Title
/// se una fonte non fornisce un link), tollera fonti temporaneamente irraggiungibili (le salta
/// con un warning, non fa fallire l'intera sync — stesso spirito resiliente di
/// <c>MarketDataSyncService</c> per l'OHLCV).
/// </summary>
public sealed class AltDataSyncService(
    IEnumerable<IAltDataSource> sources,
    ISentimentScorer scorer,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<AltDataSyncService> logger,
    SentimentSourceHealthRegistry? health = null,
    ISentimentNewsProvider? newsProvider = null) : IAltDataSyncService
{
    /// <summary>
    /// [I15] Giorni di storia da cui leggere le chiavi di deduplicazione. Non e' una retention: e'
    /// l'ampiezza entro cui un feed puo' plausibilmente ripubblicare la stessa notizia. Oltre,
    /// l'indice unico del database resta l'unica (e sufficiente) garanzia.
    /// </summary>
    private const int DedupeWindowDays = 90;

    public async Task<int> SyncAllAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // [I15, 2026-08-19] Le chiavi si leggono su una FINESTRA, non su tutta la tabella.
        //
        // Questo caricamento era limitato senza saperlo: la purge teneva AltDataPoints a
        // NewsRetentionDays, e qui si leggeva quel tanto. Dall'esenzione del corpus la tabella
        // cresce senza tetto — e questo giro parte ogni tick del worker, per sempre. Rileggere
        // l'intero archivio a ogni sincronizzazione sarebbe un costo che aumenta da solo.
        //
        // Restringere e' sicuro perche' la HashSet e' solo un'OTTIMIZZAZIONE: la garanzia contro i
        // duplicati e' l'indice UNICO su DedupeKey (ApplicationDbContext), che resta. Un feed non
        // ripubblica notizie di mesi fa; se lo facesse, l'inserimento verrebbe rifiutato dal
        // database invece che scartato qui — piu' lento, mai sbagliato.
        var dedupeWindow = DateTime.UtcNow.AddDays(-Math.Max(30, DedupeWindowDays));
        var existingKeys = (await db.AltDataPoints
            .Where(a => a.TimestampUtc >= dedupeWindow)
            .Select(a => a.DedupeKey)
            .ToListAsync(ct)).ToHashSet();

        // Le fetch HTTP sono indipendenti fra loro (I/O-bound): eseguirle in parallelo evita che
        // una fonte lenta/irraggiungibile ritardi in sequenza tutte le altre.
        var fetches = sources.Select(source => FetchSafeAsync(source, ct));
        var results = await Task.WhenAll(fetches);

        var inserted = 0;
        foreach (var (source, items) in results)
        {
            if (items is null) continue;

            var freshFromSource = 0;
            foreach (var item in items)
            {
                var dedupeKey = $"{source.Name}:{item.Url ?? item.Title}";
                if (!existingKeys.Add(dedupeKey))
                {
                    continue; // già presente (in DB o in questo stesso batch, fra fonti diverse)
                }

                // Le fonti strutturali (calendario economico, sentiment retail) forniscono i
                // propri override: non sono testo libero da classificare/scorare.
                var category = item.CategoryOverride ?? NewsImpactClassifier.Classify(item.Title, item.Summary);
                var symbols = item.SymbolsOverride ?? NewsImpactClassifier.DetectSymbols(item.Title, item.Summary);
                decimal sentiment;
                try
                {
                    sentiment = item.SentimentScoreOverride ?? await scorer.ScoreAsync(item.Title, item.Summary, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Gli scorer per contratto non lanciano (ripiegano da soli sul lessico), ma se
                    // uno lo facesse il punteggio sbagliato resterebbe per sempre (la dedupe non
                    // rivisita mai un elemento salvato): meglio SALTARE l'elemento — al prossimo
                    // giro non sarà in DB e verrà ritentato — che salvarlo con uno zero inventato.
                    logger.LogWarning(ex, "Scoring fallito per '{Title}' ({Source}): elemento saltato, ritenterà al prossimo sync.",
                        item.Title, source.Name);
                    continue;
                }

                db.AltDataPoints.Add(new AltDataPoint
                {
                    TimestampUtc = item.PublishedUtc,
                    Source = source.Name,
                    Title = item.Title,
                    Summary = item.Summary,
                    Url = item.Url,
                    Category = category.ToString(),
                    SymbolsJson = JsonSerializer.Serialize(symbols),
                    SentimentScore = sentiment,
                    DedupeKey = dedupeKey,
                });
                inserted++;
                freshFromSource++;
            }
            health?.ReportSuccess(source.Name, freshFromSource);
        }

        if (inserted > 0)
        {
            await db.SaveChangesAsync(ct);
            // Snapshot per le feature ML (Sentiment 2.0): si aggiorna qui perché OGNI percorso di
            // sync (worker, stage pipeline, bottone UI) passa da questo metodo.
            if (newsProvider is not null)
            {
                await newsProvider.RefreshAsync(ct);
            }
        }
        return inserted;
    }

    private async Task<(IAltDataSource Source, IReadOnlyList<RawNewsItem>? Items)> FetchSafeAsync(IAltDataSource source, CancellationToken ct)
    {
        try
        {
            return (source, await source.FetchLatestAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AltData sync: fonte '{Source}' non raggiungibile, salto.", source.Name);
            health?.ReportError(source.Name, ex.Message);
            return (source, null);
        }
    }
}
