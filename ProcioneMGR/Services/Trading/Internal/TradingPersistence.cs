using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>
/// Le operazioni di persistenza DB usate lungo tutta la cascata privata di <see cref="TradingEngine"/>
/// — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5). Estratte senza alcun cambio di
/// comportamento: stesse query, stesso ordine, stesse colonne aggiornate. Ogni collaboratore estratto
/// dalla stessa cascata (<see cref="BracketOrderManager"/>, gli esecutori Spot/Futures, il chiusore
/// posizioni) riceve un'istanza di questa classe invece di ripetere dbFactory/laneId nel proprio
/// costruttore.
/// </summary>
internal sealed class TradingPersistence(IDbContextFactory<ApplicationDbContext> dbFactory, int laneId)
{
    public async Task<List<Order>> GetPendingOrdersAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await TradingOrderQueries.PendingLive(db.Orders.AsNoTracking(), laneId).ToListAsync(ct);
    }

    public async Task SaveOrderAsync(Order order, bool isExisting, CancellationToken ct)
    {
        order.LaneId = laneId;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (isExisting)
        {
            var existing = await db.Orders.FirstOrDefaultAsync(o => o.LaneId == laneId && o.OrderId == order.OrderId, ct);
            if (existing is not null)
            {
                existing.Status = order.Status;
                existing.FilledPrice = order.FilledPrice;
                existing.FilledQuantity = order.FilledQuantity;
                existing.FilledAtUtc = order.FilledAtUtc;
                existing.ExchangeOrderId = order.ExchangeOrderId;
                existing.ErrorMessage = order.ErrorMessage;
                existing.ManuallyConfirmed = order.ManuallyConfirmed;
                await db.SaveChangesAsync(ct);
                return;
            }
        }
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
    }

    public async Task PersistOrderAsync(Order order, CancellationToken ct)
    {
        order.LaneId = laneId;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
    }

    public async Task PersistNewPositionAsync(OpenPosition pos, CancellationToken ct)
    {
        pos.LaneId = laneId;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.OpenPositions.Add(pos);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Aggiorna la riga di una posizione ESISTENTE dopo un fill fuso (media ponderata di una fetta).</summary>
    public async Task UpdatePositionRowAsync(OpenPosition pos, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.OpenPositions.FirstOrDefaultAsync(p => p.LaneId == laneId && p.PositionId == pos.PositionId, ct);
        if (row is null) return;
        row.Quantity = pos.Quantity;
        row.EntryPrice = pos.EntryPrice;
        row.MarginBalance = pos.MarginBalance;
        row.CurrentPrice = pos.CurrentPrice;
        row.LiquidationPrice = pos.LiquidationPrice;
        row.ExchangeOrderId = pos.ExchangeOrderId;
        row.StopOrderId = pos.StopOrderId;               // [M3] i trigger resting sopravvivono al riavvio
        row.TakeProfitOrderId = pos.TakeProfitOrderId;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemovePositionAsync(OpenPosition pos, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.OpenPositions.Where(p => p.LaneId == laneId && p.PositionId == pos.PositionId).ExecuteDeleteAsync(ct);
    }

    public async Task PersistTradeAsync(TradeRecord trade, CancellationToken ct)
    {
        trade.LaneId = laneId;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.TradeRecords.Add(trade);
        await db.SaveChangesAsync(ct);
    }

    public async Task PersistExecutionJobAsync(ExecutionJob job, CancellationToken ct)
    {
        job.LaneId = laneId;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ExecutionJobs.FirstOrDefaultAsync(j => j.Id == job.Id, ct);
        if (row is null) db.ExecutionJobs.Add(job);
        else db.Entry(row).CurrentValues.SetValues(job);
        await db.SaveChangesAsync(ct);
    }

    public async Task AuditAsync(string action, object details, TradingMode mode, DateTime ts, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.TradingAuditLogs.Add(new TradingAuditLog
        {
            LaneId = laneId,
            TimestampUtc = ts,
            Action = action,
            Details = JsonSerializer.Serialize(details),
            Mode = mode,
        });
        await db.SaveChangesAsync(ct);
    }
}
