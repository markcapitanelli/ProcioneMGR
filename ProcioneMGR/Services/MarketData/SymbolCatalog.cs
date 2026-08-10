using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.MarketData;

/// <summary>Una serie nota al catalogo: la coppia (simbolo, timeframe).</summary>
public readonly record struct SeriesKey(string Symbol, string Timeframe);

/// <summary>
/// [E-04, Fase 2 PRD-RISANAMENTO] L'elenco dei simboli noti, in UN posto solo. Prima SETTE pagine
/// eseguivano ciascuna per conto proprio <c>db.OhlcvData.Select(c => c.Symbol).Distinct()</c> —
/// una scansione (pur solo-indice) su ~12M righe per ottenere ~30 stringhe, ripetuta a ogni
/// apertura di pagina, con la POLITICA dei simboli decisa implicitamente da ogni copia.
///
/// POLITICA DICHIARATA: unione di <c>TrackedSeries</c> (le serie tracciate ora — copre quelle
/// appena aggiunte e ancora senza candele) e delle serie storiche presenti in <c>OhlcvData</c>
/// (una serie rimossa dalla watchlist resta selezionabile per l'analisi: i suoi dati esistono).
/// È la stessa semantica che le sette copie producevano di fatto, ora scritta e testabile.
/// La stessa politica vale per le COPPIE di <see cref="GetKnownSeriesAsync"/>: coppie realmente
/// presenti a DB più quelle tracciate — MAI il prodotto cartesiano simboli × timeframe, che
/// mentirebbe sulle serie senza dati.
/// </summary>
public interface ISymbolCatalog
{
    /// <summary>Simboli noti, ordinati. Cache condivisa: la scansione grossa gira al più una volta per finestra.</summary>
    ValueTask<IReadOnlyList<string>> GetKnownSymbolsAsync(CancellationToken ct = default);

    /// <summary>
    /// Le coppie (simbolo, timeframe) note, ordinate per simbolo e poi timeframe. Stessa cache e
    /// stessa politica dei simboli: serie con dati a DB più quelle tracciate, non il cartesiano.
    /// </summary>
    ValueTask<IReadOnlyList<SeriesKey>> GetKnownSeriesAsync(CancellationToken ct = default);

    /// <summary>Invalida la cache (es. dopo l'aggiunta di una serie in watchlist).</summary>
    void Invalidate();
}

/// <inheritdoc cref="ISymbolCatalog"/>
/// <remarks>
/// Singleton con cache a scadenza (default 5 minuti): i simboli cambiano solo quando si aggiunge o
/// ingerisce una serie nuova, non a ogni apertura di pagina. Il costo passa da "una scansione per
/// pagina per utente" a "una scansione per finestra per processo". La UI può forzare il refresh
/// via <see cref="Invalidate"/> (lo fa la watchlist al salvataggio).
/// Simboli e coppie vivono in UNO snapshot solo, caricato in un colpo: i simboli sono la
/// proiezione delle coppie, quindi chiedere entrambi non raddoppia la scansione.
/// </remarks>
public sealed class SymbolCatalog(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TimeSpan? ttl = null) : ISymbolCatalog
{
    private sealed record Snapshot(IReadOnlyList<string> Symbols, IReadOnlyList<SeriesKey> Series);

    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _cached;
    private DateTime _loadedAtUtc;

    public async ValueTask<IReadOnlyList<string>> GetKnownSymbolsAsync(CancellationToken ct = default)
        => (await GetSnapshotAsync(ct)).Symbols;

    public async ValueTask<IReadOnlyList<SeriesKey>> GetKnownSeriesAsync(CancellationToken ct = default)
        => (await GetSnapshotAsync(ct)).Series;

    private async ValueTask<Snapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var cached = _cached;
        if (cached is not null && DateTime.UtcNow - _loadedAtUtc < _ttl)
        {
            return cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Double-check dopo il lock: un altro chiamante può aver già ricaricato.
            if (_cached is not null && DateTime.UtcNow - _loadedAtUtc < _ttl)
            {
                return _cached;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var tracked = await db.TrackedSeries.AsNoTracking()
                .Select(t => new { t.Symbol, t.Timeframe }).Distinct().ToListAsync(ct);
            var historical = await db.OhlcvData.AsNoTracking()
                .Select(c => new { c.Symbol, c.Timeframe }).Distinct().ToListAsync(ct);

            var series = tracked.Concat(historical)
                .Select(p => new SeriesKey(p.Symbol, p.Timeframe))
                .Distinct(SeriesKeyIgnoreCase.Instance)
                .OrderBy(k => k.Symbol, StringComparer.OrdinalIgnoreCase)
                .ThenBy(k => k.Timeframe, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Le coppie sono già ordinate per simbolo: la proiezione resta ordinata da sé.
            var symbols = series.Select(k => k.Symbol)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _cached = new Snapshot(symbols, series);
            _loadedAtUtc = DateTime.UtcNow;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        _cached = null;
    }

    /// <summary>Uguaglianza case-insensitive sulla coppia, coerente con l'unione dei simboli.</summary>
    private sealed class SeriesKeyIgnoreCase : IEqualityComparer<SeriesKey>
    {
        public static readonly SeriesKeyIgnoreCase Instance = new();

        public bool Equals(SeriesKey x, SeriesKey y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Symbol, y.Symbol)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Timeframe, y.Timeframe);

        public int GetHashCode(SeriesKey k) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.Symbol),
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.Timeframe));
    }
}
