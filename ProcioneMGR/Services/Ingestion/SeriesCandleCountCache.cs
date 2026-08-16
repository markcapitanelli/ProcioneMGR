using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// Conteggio candele per serie, calcolato UNA VOLTA per finestra e condiviso da tutti i circuiti.
///
/// <para>La storia di questa query è la lezione. Prima: una <c>CountAsync</c> per riga (N+1, collo
/// di bottiglia). Poi: una <c>GROUP BY</c> unica sull'intera <c>OhlcvData</c> — che risolveva
/// l'N+1 ma costava 15 secondi misurati su 12,6M righe, pagati a OGNI apertura di pagina. Poi
/// ancora (revisione 2026-08-15): di nuovo per-serie ma sull'indice e in background — e la
/// verifica nel browser ha mostrato il conto vero: <b>417 ms per serie × 234 serie ≈ 97 secondi</b>
/// per passata, una passata per ogni caricamento di pagina, tutte accavallate. Più lento del
/// problema che voleva risolvere.</para>
///
/// <para>La forma giusta è la terza: la <c>GROUP BY</c> unica (15 s, il totale più basso) fatta in
/// BACKGROUND, UNA alla volta nel processo (single-flight) e riusata per <see cref="Ttl"/>. Un
/// caricamento di pagina dentro la finestra costa zero; il primo dopo la scadenza paga una
/// passata, e nessuno la paga due volte insieme. Singleton di proposito: i circuiti sono tanti,
/// il numero di candele è uno solo.</para>
/// </summary>
public sealed class SeriesCandleCountCache(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<SeriesCandleCountCache> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>
    /// Quanto vale un conteggio prima di essere rifatto. Dieci minuti: il numero di candele si
    /// muove di poche unità per ciclo di sync (5 min) e nessuna decisione dipende dalla sua
    /// ultima cifra — mentre la passata costa secondi di database che tutti gli altri aspettano.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyDictionary<(string Symbol, string Timeframe), int> _counts =
        new Dictionary<(string, string), int>();

    private DateTimeOffset _computedAt = DateTimeOffset.MinValue;

    /// <summary>Ultimo calcolo riuscito (UTC); <c>null</c> se non è mai stato fatto.</summary>
    public DateTime? ComputedAtUtc => _computedAt == DateTimeOffset.MinValue ? null : _computedAt.UtcDateTime;

    /// <summary>
    /// I conteggi correnti. Ricalcola solo se scaduti; se un'altra passata è già in volo, ASPETTA
    /// quella invece di avviarne una seconda — due passate insieme raddoppierebbero il costo per
    /// produrre lo stesso numero (è esattamente ciò che faceva ogni ricarica di pagina).
    /// </summary>
    public async Task<IReadOnlyDictionary<(string Symbol, string Timeframe), int>> GetAsync(CancellationToken ct = default)
    {
        if (!IsExpired())
        {
            return _counts;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (!IsExpired())
            {
                return _counts; // l'ha appena calcolato chi era davanti in coda
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.OhlcvData
                .GroupBy(c => new { c.Symbol, c.Timeframe })
                .Select(g => new { g.Key.Symbol, g.Key.Timeframe, Count = g.Count() })
                .ToListAsync(ct);

            _counts = rows.ToDictionary(x => (x.Symbol, x.Timeframe), x => x.Count);
            _computedAt = _time.GetUtcNow();
            logger.LogInformation("Conteggio candele ricalcolato: {Serie} serie in {Ms}ms (valido {Ttl}).",
                _counts.Count, sw.ElapsedMilliseconds, Ttl);
            return _counts;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsExpired() => _time.GetUtcNow() - _computedAt > Ttl;
}
