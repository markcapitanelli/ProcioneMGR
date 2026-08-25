using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [J8] Il registro dell'osservazione cumulata di una corsia. Vedi <see cref="FleetLaneObservation"/>
/// per il perché; qui vive la POLITICA di accredito, in un posto solo.
/// </summary>
public interface ILaneObservationLedger
{
    /// <summary>
    /// Registra il tick per la corsia e restituisce l'osservazione cumulata e l'ancora dei trade.
    /// Identità diversa dalla registrata ⇒ l'orologio riparte (nuovo esperimento).
    /// </summary>
    Task<(TimeSpan Observed, DateTime FirstSeenUtc)> AccumulateAsync(
        int laneId, string identity, bool isRunning, DateTime nowUtc, CancellationToken ct = default);
}

/// <inheritdoc cref="ILaneObservationLedger"/>
public sealed class LaneObservationLedger(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<LaneObservationLedger> logger) : ILaneObservationLedger
{
    /// <summary>
    /// Tetto di accredito per un singolo delta: tre tick dell'orchestratore (15′). Un buco più
    /// lungo (guscio spento) si accredita al massimo per questo: durante l'assenza il motore può
    /// aver operato oppure no, e non sapere non è un motivo per accreditare — l'errore resta nella
    /// direzione che SOTTOSTIMA l'osservazione, cioè ritarda un ritiro invece di inventarlo.
    /// </summary>
    internal static readonly TimeSpan MaxCreditPerGap = TimeSpan.FromMinutes(45);

    /// <summary>
    /// L'identità canonica dell'esperimento in corsia. Le gambe sono ORDINATE: lo stesso ensemble
    /// enumerato in due ordini è lo stesso esperimento, non due.
    /// </summary>
    internal static string BuildIdentity(string symbol, string timeframe, IReadOnlyList<string>? strategyIds) =>
        $"{symbol}|{timeframe}|{string.Join(",", (strategyIds ?? []).OrderBy(x => x, StringComparer.Ordinal))}";

    public async Task<(TimeSpan Observed, DateTime FirstSeenUtc)> AccumulateAsync(
        int laneId, string identity, bool isRunning, DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.FleetLaneObservations.FirstOrDefaultAsync(o => o.LaneId == laneId, ct);

        if (row is null)
        {
            row = new FleetLaneObservation
            {
                LaneId = laneId, Identity = identity, FirstSeenUtc = nowUtc,
                ObservedSeconds = 0, LastTickUtc = nowUtc,
            };
            db.FleetLaneObservations.Add(row);
            await SaveIgnoringRaceAsync(db, laneId, ct);
            return (TimeSpan.Zero, nowUtc);
        }

        if (!string.Equals(row.Identity, identity, StringComparison.Ordinal))
        {
            // Nuovo esperimento sulla corsia: l'orologio riparte. 20 giorni su un ensemble non
            // dicono nulla su quello che l'ha sostituito.
            logger.LogInformation(
                "Corsia {Lane}: identità cambiata ({Da} → {A}) — l'osservazione cumulata riparte da zero.",
                laneId, row.Identity, identity);
            row.Identity = identity;
            row.FirstSeenUtc = nowUtc;
            row.ObservedSeconds = 0;
            row.LastTickUtc = nowUtc;
            await SaveIgnoringRaceAsync(db, laneId, ct);
            return (TimeSpan.Zero, nowUtc);
        }

        var delta = nowUtc - row.LastTickUtc;
        if (delta <= TimeSpan.Zero)
        {
            // Orologio fermo o due letture nello stesso istante: niente da accreditare.
            return (TimeSpan.FromSeconds(row.ObservedSeconds), row.FirstSeenUtc);
        }

        if (isRunning)
        {
            var credit = delta <= MaxCreditPerGap ? delta : MaxCreditPerGap;
            row.ObservedSeconds += (long)credit.TotalSeconds;
        }
        // Corsia FERMA: si avanza il riferimento senza accreditare — il tempo da spenta non è
        // osservazione, e al riavvio il delta non deve inglobare la pausa.
        row.LastTickUtc = nowUtc;
        await SaveIgnoringRaceAsync(db, laneId, ct);

        return (TimeSpan.FromSeconds(row.ObservedSeconds), row.FirstSeenUtc);
    }

    /// <summary>
    /// Concorrenza ottimistica su <c>LastTickUtc</c>: se un altro lettore ha accreditato lo stesso
    /// delta un istante prima, QUESTO salvataggio si scarta — accreditare due volte gonfierebbe
    /// l'osservazione, che deve poter solo sottostimare.
    /// </summary>
    private async Task SaveIgnoringRaceAsync(ApplicationDbContext db, int laneId, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogDebug("Corsia {Lane}: tick concorrente sul registro di osservazione — delta scartato.", laneId);
        }
        catch (DbUpdateException)
        {
            // Inserimento concorrente della stessa corsia: la riga esiste già, il prossimo tick la troverà.
            logger.LogDebug("Corsia {Lane}: inserimento concorrente sul registro di osservazione — riga già presente.", laneId);
        }
    }
}
