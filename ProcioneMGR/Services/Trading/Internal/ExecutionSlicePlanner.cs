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
    ILogger logger,
    int laneId)
{
    /// <summary>
    /// Quando l'apertura passerebbe da un piano a fette invece che da un market order immediato:
    /// mai in Paper (nessun impatto da ridurre su un fill simulato), mai senza un algoritmo diverso
    /// da <c>Immediate</c>, mai con l'esecuzione a fette spenta a configurazione.
    /// </summary>
    internal static bool WouldSlice(TradingMode mode, string? algorithmName, bool liveExecutionEnabled)
        => mode != TradingMode.Paper
           && !string.IsNullOrEmpty(algorithmName) && algorithmName != "Immediate"
           && liveExecutionEnabled;

    /// <summary>
    /// [M4] La combinazione vietata: un piano a fette mentre gli stop resting sull'exchange sono
    /// attivi. Vero ⇒ il piano NON si costruisce e si apre a quantità piena. Predicato separato
    /// perché è una regola, non un dettaglio di flusso: va potuta leggere e provare da sola.
    ///
    /// Vincolato ai <b>Futures</b> di proposito: il bracket resting viene piazzato solo nel percorso
    /// di apertura Futures (<c>PositionOpener.ExecuteFuturesOpenAsync</c>), quindi su una corsia Spot
    /// la spunta non arma alcun trigger e non c'è niente da proteggere. Sopprimere le fette anche lì
    /// peggiorerebbe l'esecuzione in cambio di nulla — un divieto che protegge da un rischio
    /// inesistente è solo un danno con una buona intenzione.
    /// </summary>
    internal static bool SlicingSuppressedByRestingStops(
        TradingMode mode, MarketType marketType, string? algorithmName, bool liveExecutionEnabled, bool useExchangeRestingStops)
        => useExchangeRestingStops
           && marketType == MarketType.Futures
           && WouldSlice(mode, algorithmName, liveExecutionEnabled);

    public async Task TryBuildAndStartExecutionPlanAsync(
        TradingEngineState state, List<OpenPosition> positions, List<ExecutionJob> executionJobs,
        Func<decimal, TradingEngineStatus> buildSafetyStatus,
        Func<Order, string, decimal, DateTime, CancellationToken, bool, OpenPosition?, Task<bool>> executeOpenAsync,
        Func<string, DateTime, CancellationToken, Task> emergencyInternalAsync,
        Order order, EnsembleStrategy? strat, string strategyName, decimal price, DateTime ts, CancellationToken ct, bool isExisting,
        Exchanges.SymbolFilters? filters = null)
    {
        var algoName = strat?.ExecutionAlgorithmName;
        var sliced = WouldSlice(state.Mode, algoName, liveExecution.CurrentValue.Enabled);

        // [M4, 2026-08-20] Fette ed exchange-resting-stop si contraddicono, e vince la protezione.
        //
        // Il bracket sull'exchange viene piazzato una volta sola, quando NASCE la posizione, cioè
        // sulla fetta #1, con la quantità di quell'istante (PositionOpener, ramo `mergeInto is null`
        // → BracketOrderManager, `Quantity = pos.Quantity`). Le fette 2..K fondono nella posizione e
        // fanno crescere pos.Quantity senza toccare il trigger: la sotto-copertura NON è temporanea,
        // resta per tutta la vita della posizione anche a piano completato. Non è nemmeno un ordine
        // "chiudi tutto": entrambi i client mandano una size esplicita con reduceOnly, quindi
        // l'exchange arma davvero solo quella frazione — con un TWAP a 12 fette, 1/12 della
        // posizione. Chi accende `UseExchangeRestingStops` sta comprando la protezione che sopravvive
        // alla morte del processo: dargliela sull'8% della posizione è peggio che non dargliela,
        // perché ci si crede coperti. Regola 4, fail-closed: si rinuncia alla riduzione d'impatto
        // dello slicing e si apre a quantità piena, che il bracket copre per intero. La combinazione
        // è dichiarata nel testo d'aiuto della spunta in /trading, così nessuno sceglie Twap e
        // ottiene Immediate senza saperlo.
        if (SlicingSuppressedByRestingStops(
                state.Mode, state.MarketType, algoName,
                liveExecution.CurrentValue.Enabled, safety.CurrentValue.UseExchangeRestingStops))
        {
            logger.LogInformation(
                "Corsia {Lane}: piano di esecuzione {Algo} NON costruito perché gli stop resting sull'exchange sono attivi "
                + "(coprirebbero solo la prima fetta, per sempre). Apertura immediata a quantità piena.",
                laneId, algoName);
            await persistence.AuditAsync("ExecutionPlanSkippedForRestingStops",
                new { strategyName, algoName, qty = order.Quantity, price }, state.Mode, ts, ct);
            sliced = false;
        }

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
            // [2026-08-17] La conferma manuale viaggia con l'ordine sintetico: senza, il check #7
            // del SafetyChecker vedeva sempre un ordine Live NON confermato e bocciava l'intero
            // piano con «Ordine Live senza conferma manuale dell'operatore» — proprio nell'istante
            // in cui l'operatore lo aveva appena confermato. L'esecuzione a fette in Live era
            // impossibile, e la diagnosi scritta a video contraddiceva il gesto compiuto.
            ManuallyConfirmed = order.ManuallyConfirmed,
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
        //
        // [2026-08-17] Arrotondata al LOT_SIZE come TUTTE le altre. SignalOrderBuilder arrotonda la
        // quantità TOTALE, poi il piano la divide (TWAP: total/k) ottenendo fette che multipli del
        // passo non sono: le fette 2..K venivano ri-arrotondate al momento dell'esecuzione, la #1
        // no — partiva verso l'exchange con la quantità grezza e veniva rifiutata (-1111). Se dopo
        // l'arrotondamento non è negoziabile si ricade sull'apertura immediata con la quantità
        // piena, che è già passata dal pre-check aggregato.
        var primaFetta = filters is not null ? filters.RoundQuantity(plan.Slices[0].Quantity) : plan.Slices[0].Quantity;
        if (primaFetta <= 0m || (filters is not null && !filters.IsTradable(primaFetta, price)))
        {
            await executeOpenAsync(order, strategyName, price, ts, ct, isExisting, null);
            return;
        }
        order.Quantity = primaFetta;
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
