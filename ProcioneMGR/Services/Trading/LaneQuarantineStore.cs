using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Accesso alla quarantena corsie (Fase 0-A3): usato dal <see cref="LaneInvariantWatchdog"/>
/// (scrittura), dalla pagina /trading (lettura + rimozione Admin) e indirettamente da
/// <c>TradingEngine.StartAsync</c> (che però legge la tabella direttamente via dbFactory,
/// senza dipendenza in più). L'audit (LaneQuarantined / LaneQuarantineCleared) vive QUI,
/// così nessun chiamante può quarantenare/liberare una corsia senza lasciare traccia.
/// </summary>
public interface ILaneQuarantineStore
{
    Task<LaneQuarantine?> GetAsync(int laneId, CancellationToken ct = default);

    /// <summary>Tutte le quarantene attive (per il riallineamento di flotta e la UI multi-corsia).</summary>
    Task<IReadOnlyList<LaneQuarantine>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Mette la corsia in quarantena se non lo è già. <c>false</c> se una quarantena era già
    /// attiva (la prima vince: la riga esistente conserva l'evidenza originale).
    /// </summary>
    Task<bool> TryQuarantineAsync(int laneId, string reason, string detailsJson, CancellationToken ct = default);

    /// <summary>Rimuove la quarantena (azione umana, /trading solo Admin). <c>false</c> se non c'era.</summary>
    Task<bool> ClearAsync(int laneId, string? userId, CancellationToken ct = default);
}

public sealed class LaneQuarantineStore(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<LaneQuarantineStore> logger) : ILaneQuarantineStore
{
    public async Task<LaneQuarantine?> GetAsync(int laneId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LaneQuarantines.AsNoTracking().FirstOrDefaultAsync(q => q.LaneId == laneId, ct);
    }

    public async Task<IReadOnlyList<LaneQuarantine>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LaneQuarantines.AsNoTracking().OrderBy(q => q.LaneId).ToListAsync(ct);
    }

    public async Task<bool> TryQuarantineAsync(int laneId, string reason, string detailsJson, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.LaneQuarantines.Add(new LaneQuarantine
        {
            LaneId = laneId,
            CreatedAtUtc = DateTime.UtcNow,
            Reason = reason,
            DetailsJson = detailsJson,
        });
        db.TradingAuditLogs.Add(BuildAudit(laneId, "LaneQuarantined", detailsJson, userId: null,
            await CurrentModeAsync(db, laneId, ct)));
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // PK LaneId già presente: quarantena già attiva (tick concorrente o riavvio) — la
            // prima riga vince e conserva l'evidenza originale, questo esito non è un errore.
            return false;
        }
    }

    public async Task<bool> ClearAsync(int laneId, string? userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.LaneQuarantines.FirstOrDefaultAsync(q => q.LaneId == laneId, ct);
        if (row is null) return false;

        db.LaneQuarantines.Remove(row);
        db.TradingAuditLogs.Add(BuildAudit(laneId, "LaneQuarantineCleared",
            System.Text.Json.JsonSerializer.Serialize(new { row.Reason, quarantinedAtUtc = row.CreatedAtUtc }),
            userId, await CurrentModeAsync(db, laneId, ct)));
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Quarantena corsia {Lane} rimossa da {User} (era attiva dal {Since:u}: {Reason}).",
            laneId, userId ?? "sconosciuto", row.CreatedAtUtc, row.Reason);
        return true;
    }

    private static TradingAuditLog BuildAudit(int laneId, string action, string details, string? userId, TradingMode mode) => new()
    {
        LaneId = laneId,
        TimestampUtc = DateTime.UtcNow,
        Action = action,
        Details = details,
        UserId = userId,
        Mode = mode,
    };

    private static async Task<TradingMode> CurrentModeAsync(ApplicationDbContext db, int laneId, CancellationToken ct)
    {
        var state = await db.TradingEngineStates.AsNoTracking()
            .Where(s => s.LaneId == laneId).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        return state?.Mode ?? TradingMode.Paper;
    }
}
