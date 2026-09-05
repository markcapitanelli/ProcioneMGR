using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Carry;

/// <summary>
/// [2026-09-05] <b>Il registro persistito del forward test del carry.</b> Scrive un episodio per
/// apertura, accredita il funding a ogni evento nuovo mentre la posizione è aperta, chiude l'episodio
/// col netto, e al riavvio del pod restituisce al motore le posizioni ancora aperte — così un
/// rischieramento non «riapre» sei carry già aperti e non azzera ciò che avevano incassato.
/// </summary>
public interface ICarryLedger
{
    /// <summary>Ripristina nel motore gli episodi ancora aperti della modalità indicata. Restituisce quanti.</summary>
    Task<int> RestoreAsync(CarryEngine engine, string mode, CancellationToken ct = default);

    /// <summary>Apre un episodio. Se ne esiste già uno aperto per lo stesso simbolo e modalità non ne crea un secondo.</summary>
    Task OpenAsync(string symbol, string mode, decimal notionalQuote, decimal entryAnnualizedPercent, decimal costPercent,
        DateTime? lastFundingUtc, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Accredita un evento di funding all'episodio aperto del simbolo. Idempotente sul timestamp:
    /// lo stesso evento non si conta due volte. Restituisce il totale accreditato (%) o <c>null</c>
    /// se non c'è un episodio aperto.
    /// </summary>
    Task<decimal?> AccrueAsync(string symbol, string mode, DateTime fundingUtc, decimal ratePercent, CancellationToken ct = default);

    /// <summary>Chiude l'episodio aperto del simbolo scrivendo il netto (funding − costi).</summary>
    Task CloseAsync(string symbol, string mode, decimal exitAnnualizedPercent, DateTime nowUtc, string reason, CancellationToken ct = default);
}

/// <summary>L'aritmetica del registro, pura e provata a parte.</summary>
public static class CarryLedgerMath
{
    /// <summary>Funding di un evento in valuta quote: lo short perp incassa <c>nozionale × tasso</c> (paga se il tasso è negativo).</summary>
    public static decimal FundingQuote(decimal notionalQuote, decimal ratePercent) => notionalQuote * ratePercent / 100m;

    /// <summary>Il costo del giro completo del modello del backtest: quattro fill su due gambe.</summary>
    public static decimal RoundTripCostPercent(CarryConfiguration config)
        => 2m * (config.SpotFeePercent + config.SlippagePercent) + 2m * (config.PerpFeePercent + config.SlippagePercent);

    /// <summary>Netto dell'episodio: funding incassato meno il costo del giro sul nozionale di una gamba.</summary>
    public static decimal NetQuote(decimal fundingCollectedQuote, decimal notionalQuote, decimal costPercent)
        => fundingCollectedQuote - notionalQuote * costPercent / 100m;
}

/// <inheritdoc cref="ICarryLedger"/>
public sealed class CarryLedger(IDbContextFactory<ApplicationDbContext> dbFactory, ILogger<CarryLedger> logger) : ICarryLedger
{
    public async Task<int> RestoreAsync(CarryEngine engine, string mode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var open = await db.CarryLedger.AsNoTracking()
            .Where(e => e.Mode == mode && e.ClosedUtc == null)
            .OrderBy(e => e.OpenedUtc)
            .ToListAsync(ct);

        foreach (var e in open)
        {
            engine.Restore(e.Symbol, new CarrySymbolState
            {
                InPosition = true,
                OpenedUtc = e.OpenedUtc,
                NotionalQuote = e.NotionalQuote,
                FundingCollectedPercent = e.FundingCollectedPercent,
            });
        }
        if (open.Count > 0)
        {
            logger.LogInformation("Carry [{Mode}]: {N} episodi aperti ripristinati dal registro ({Symbols}).",
                mode, open.Count, string.Join(", ", open.Select(e => e.Symbol)));
        }
        return open.Count;
    }

    public async Task OpenAsync(string symbol, string mode, decimal notionalQuote, decimal entryAnnualizedPercent, decimal costPercent,
        DateTime? lastFundingUtc, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var giaAperto = await db.CarryLedger.AnyAsync(e => e.Symbol == symbol && e.Mode == mode && e.ClosedUtc == null, ct);
        if (giaAperto)
        {
            logger.LogWarning("Carry [{Mode}] {Sym}: episodio già aperto nel registro, non ne apro un secondo.", mode, symbol);
            return;
        }
        db.CarryLedger.Add(new CarryLedgerEntry
        {
            Symbol = symbol,
            Mode = mode,
            OpenedUtc = nowUtc,
            NotionalQuote = notionalQuote,
            EntryAnnualizedPercent = entryAnnualizedPercent,
            CostPercent = costPercent,
            // L'evento che ha fatto aprire non si incassa: si incassa dal prossimo.
            LastFundingUtc = lastFundingUtc,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<decimal?> AccrueAsync(string symbol, string mode, DateTime fundingUtc, decimal ratePercent, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var e = await db.CarryLedger.FirstOrDefaultAsync(x => x.Symbol == symbol && x.Mode == mode && x.ClosedUtc == null, ct);
        if (e is null) return null;
        if (e.LastFundingUtc is DateTime ultimo && fundingUtc <= ultimo) return e.FundingCollectedPercent;

        e.FundingEventsAccrued++;
        e.FundingCollectedPercent += ratePercent;
        e.FundingCollectedQuote += CarryLedgerMath.FundingQuote(e.NotionalQuote, ratePercent);
        e.LastFundingUtc = fundingUtc;
        await db.SaveChangesAsync(ct);
        return e.FundingCollectedPercent;
    }

    public async Task CloseAsync(string symbol, string mode, decimal exitAnnualizedPercent, DateTime nowUtc, string reason, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var e = await db.CarryLedger.FirstOrDefaultAsync(x => x.Symbol == symbol && x.Mode == mode && x.ClosedUtc == null, ct);
        if (e is null)
        {
            logger.LogWarning("Carry [{Mode}] {Sym}: chiusura senza episodio aperto nel registro.", mode, symbol);
            return;
        }
        e.ClosedUtc = nowUtc;
        e.ExitAnnualizedPercent = exitAnnualizedPercent;
        e.ClosedReason = reason;
        e.NetQuote = CarryLedgerMath.NetQuote(e.FundingCollectedQuote, e.NotionalQuote, e.CostPercent);
        await db.SaveChangesAsync(ct);
    }
}
