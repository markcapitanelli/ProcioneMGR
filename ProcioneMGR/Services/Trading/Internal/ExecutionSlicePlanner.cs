using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Risk;

namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>
/// Decide fra apertura IMMEDIATA ed esecuzione a fette (TWAP/VWAP/Iceberg) — Intervento B, Fase 1
/// (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5, rif. ROADMAP-QLIB §1.2). Estratto da
/// <see cref="TradingEngine"/> senza alcun cambio di comportamento: stesso pre-check aggregato sulla
/// quantità PIENA (altrimenti <c>MaxPositionSizePercent</c> sarebbe bypassabile fetta per fetta),
/// stesso calcolo di finestra/numero di fette, stessa prima fetta immediata seguita dal piano per le
/// successive. Riceve <paramref name="buildSafetyStatus"/>/<paramref name="executeOpenAsync"/>/
/// <paramref name="emergencyInternalAsync"/> come delegati verso i metodi di <see cref="TradingEngine"/>
/// che restano lì (orchestrazione a livello di engine, fuori da questa cascata di apertura/chiusura).
/// </summary>
internal sealed class ExecutionSlicePlanner(
    IExecutionAlgorithmFactory executionAlgorithms,
    IOptionsMonitor<LiveExecutionOptions> liveExecution,
    IOptionsMonitor<SafetyConfiguration> safety,
    ProcioneMetrics? metrics,
    TradingPersistence persistence,
    int laneId)
{
    public async Task TryBuildAndStartExecutionPlanAsync(
        TradingEngineState state, List<OpenPosition> positions, List<ExecutionJob> executionJobs,
        Func<decimal, TradingEngineStatus> buildSafetyStatus,
        Func<Order, string, decimal, DateTime, CancellationToken, bool, OpenPosition?, Task<bool>> executeOpenAsync,
        Func<string, DateTime, CancellationToken, Task> emergencyInternalAsync,
        Order order, EnsembleStrategy? strat, string strategyName, decimal price, DateTime ts, CancellationToken ct, bool isExisting)
    {
        var algoName = strat?.ExecutionAlgorithmName;
        var sliced = state.Mode != TradingMode.Paper
                     && !string.IsNullOrEmpty(algoName) && algoName != "Immediate"
                     && liveExecution.CurrentValue.Enabled;

        if (!sliced)
        {
            await executeOpenAsync(order, strategyName, price, ts, ct, isExisting, null);   // percorso INVARIATO
            return;
        }

        // Pre-check AGGREGATO sulla quantità PIENA: senza, ogni fetta vedrebbe solo 1/N del nozionale
        // e MaxPositionSizePercent sarebbe bypassabile. Order sintetico (mai piazzato, solo per il check).
        var fullOrder = new Order
        {
            Quantity = order.Quantity, Price = price, MarketType = state.MarketType,
            Leverage = order.Leverage, Mode = state.Mode, Side = order.Side,
        };
        var aggregate = SafetyChecker.Evaluate(fullOrder, buildSafetyStatus(price), safety.CurrentValue, ts);
        if (!aggregate.IsAllowed)
        {
            order.Status = OrderStatus.Rejected;
            order.ErrorMessage = string.Join(" | ", aggregate.Violations);
            await persistence.SaveOrderAsync(order, isExisting, ct);
            await persistence.AuditAsync("ExecutionPlanRejected", new { strategyName, qty = order.Quantity, price, aggregate.Violations }, state.Mode, ts, ct);
            if (aggregate.RequiresEmergencyStop)
                await emergencyInternalAsync("Safety critico: " + string.Join("; ", aggregate.Violations), ts, ct);
            return;
        }

        // Finestra e numero massimo di fette: lo spacing minimo deve rispettare MinOrderIntervalSeconds
        // (non si bypassa il check, ci si pianifica dentro).
        var windowMinutes = strat?.ExecutionWindowMinutes is int m and > 0 ? m : liveExecution.CurrentValue.DefaultWindowMinutes;
        var windowSeconds = Math.Max(60, windowMinutes * 60);
        var minInterval = Math.Max(1, safety.CurrentValue.MinOrderIntervalSeconds);
        var maxSlices = Math.Max(1, windowSeconds / minInterval);
        var cap = (int)Math.Min(maxSlices, 12);

        var profile = await persistence.GetRecentCandlesAsync(state.Symbol, state.Timeframe, (int)Math.Min(maxSlices, 60), ct);

        var execParams = new ExecutionParameters
        {
            MaxSlices = cap,
            IcebergClipFraction = Math.Max(0.1m, 1m / cap),
        };
        var intent = new ExecutionIntent(state.Symbol,
            order.Side == OrderSide.Buy ? ExecutionSide.Buy : ExecutionSide.Sell, order.Quantity, price);
        var plan = profile.Count >= 2
            ? executionAlgorithms.Create(algoName!).BuildPlan(intent, profile, execParams)
            : null;
        var n = plan?.SliceCount ?? 0;
        if (plan is null || n <= 1)
        {
            // Nessun profilo utile o piano a una sola fetta: apertura immediata (meglio eseguire subito).
            await executeOpenAsync(order, strategyName, price, ts, ct, isExisting, null);
            return;
        }

        // Fetta #1 SUBITO: crea la posizione (mergeInto=null). Se rifiutata, nessun job.
        order.Quantity = plan.Slices[0].Quantity;
        var filled = await executeOpenAsync(order, strategyName, price, ts, ct, isExisting, null);
        if (!filled) return;

        var slices = new List<ExecutionJobSlice>(n - 1);
        for (var i = 1; i < n; i++)
        {
            slices.Add(new ExecutionJobSlice
            {
                OffsetSeconds = (int)((long)i * windowSeconds / n),
                Quantity = plan.Slices[i].Quantity,
                Status = "Pending",
            });
        }
        var pos = positions.First(p => p.PositionId == order.PositionId);
        var job = new ExecutionJob
        {
            Id = Guid.NewGuid(), LaneId = laneId, StrategyId = order.StrategyId, PositionId = order.PositionId,
            Symbol = state.Symbol, MarketType = state.MarketType, Side = order.Side,
            TotalQuantity = plan.PlannedQuantity, FilledQuantity = order.FilledQuantity ?? plan.Slices[0].Quantity,
            EntryPriceWeightedAvg = pos.EntryPrice, Algorithm = algoName!, WindowSeconds = windowSeconds,
            Status = "Running", CreatedAtUtc = ts, SlicesJson = ExecutionJobSlices.Serialize(slices),
            ArrivalPrice = price,   // t0 di decisione: base per l'implementation shortfall a fine job
        };
        executionJobs.Add(job);
        await persistence.PersistExecutionJobAsync(job, ct);
        metrics?.RecordExecutionJob(algoName!, "Started");
        await persistence.AuditAsync("ExecutionPlanStarted", new { job.Id, algoName, slices = n, windowSeconds }, state.Mode, ts, ct);
    }
}
