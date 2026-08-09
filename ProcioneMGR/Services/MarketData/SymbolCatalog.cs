using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.MarketData;

/// <summary>
/// [E-04, Fase 2 PRD-RISANAMENTO] L'elenco dei simboli noti, in UN posto solo. Prima SETTE pagine
/// eseguivano ciascuna per conto proprio <c>db.OhlcvData.Select(c => c.Symbol).Distinct()</c> —
/// una scansione (pur solo-indice) su ~12M righe per ottenere ~30 stringhe, ripetuta a ogni
/// apertura di pagina, con la POLITICA dei simboli decisa implicitamente da ogni copia.
///
/// POLITICA DICHIARATA: unione di <c>TrackedSeries</c> (le serie tracciate ora — copre quelle
/// appena aggiunte e ancora senza candele) e dei simboli storici presenti in <c>OhlcvData</c>
/// (una serie rimossa dalla watchlist resta selezionabile per l'analisi: i suoi dati esistono).
/// È la stessa semantica che le sette copie producevano di fatto, ora scritta e testabile.
/// </summary>
public interface ISymbolCatalog
{
    /// <summary>Simboli noti, ordinati. Cache condivisa: la scansione grossa gira al più una volta per finestra.</summary>
    ValueTask<IReadOnlyList<string>> GetKnownSymbolsAsync(CancellationToken ct = default);

    /// <summary>Invalida la cache (es. dopo l'aggiunta di una serie in watchlist).</summary>
    void Invalidate();
}

/// <inheritdoc cref="ISymbolCatalog"/>
/// <remarks>
/// Singleton con cache a scadenza (default 5 minuti): i simboli cambiano solo quando si aggiunge o
/// ingerisce una serie nuova, non a ogni apertura di pagina. Il costo passa da "una scansione per
/// pagina per utente" a "una scansione per finestra per processo". La UI può forzare il refresh
/// via <see cref="Invalidate"/> (lo fa la watchlist al salvataggio).
/// </remarks>
public sealed class SymbolCatalog(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TimeSpan? ttl = null) : ISymbolCatalog
{
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<string>? _cached;
    private DateTime _loadedAtUtc;

    public async ValueTask<IReadOnlyList<string>> GetKnownSymbolsAsync(CancellationToken ct = default)
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
                .Select(t => t.Symbol).Distinct().ToListAsync(ct);
            var historical = await db.OhlcvData.AsNoTracking()
                .Select(c => c.Symbol).Distinct().ToListAsync(ct);

            _cached = tracked.Union(historical, StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
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
}
