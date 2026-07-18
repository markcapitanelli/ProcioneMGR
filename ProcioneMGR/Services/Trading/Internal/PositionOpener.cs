using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Observability;
using ProcioneMGR.Services.Risk;

namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>
/// Apertura di posizioni Spot e Futures — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md
/// §4.5). Estratto da <see cref="TradingEngine"/> senza alcun cambio di comportamento: stessa
/// gestione della riconciliazione di rete incerta, stesso calcolo margine/liquidazione sui Futures,
/// stessa fusione via media ponderata quando <c>mergeInto</c> non è null (fetta 2..K di un
/// ExecutionJob). Riceve <paramref name="state"/>/<paramref name="positions"/>/<paramref name="active"/>
/// come riferimenti diretti: le mutazioni sono visibili a <see cref="TradingEngine"/> esattamente
/// come quando il codice viveva inline.
/// </summary>
internal sealed class PositionOpener(
    IExchangeClientFactory exchangeFactory,
    ILogger logger,
    TradingPersistence persistence,
    ProcioneMetrics? metrics,
    IOptionsMonitor<SafetyConfiguration> safety)
{
    private BracketOrderManager BracketManager(TradingMode mode) => new(
        exchangeFactory, logger,
        (action, details, ts, ct) => persistence.AuditAsync(action, details, mode, ts, ct),
        persistence.UpdatePositionRowAsync);

    /// <summary>Apertura SPOT. mergeInto=null crea una nuova posizione (INVARIATO); non-null fonde il fill via media ponderata.</summary>
    public async Task<bool> ExecuteSpotOpenAsync(
        TradingEngineState state, List<OpenPosition> positions, List<EnsembleStrategy> active, TradingCredentials? credsOrNull, decimal feeFrac,
        Order order, string strategyName, decimal currentPrice, DateTime ts, CancellationToken ct, bool isExisting, OpenPosition? mergeInto = null)
    {
        var side = order.Side;
        var qty = order.Quantity;

        var fillPrice = currentPrice;
        var fillQty = qty;
        string? exchangeOrderId = null;

        if (state.Mode != TradingMode.Paper && credsOrNull is TradingCredentials creds)
        {
            var client = exchangeFactory.Create(state.ExchangeName);
            var res = await client.PlaceOrderAsync(new PlaceOrderRequest
            {
                Symbol = state.Symbol,
                Side = side == OrderSide.Buy ? "BUY" : "SELL",
                Type = "MARKET",
                Quantity = qty,
                ClientOrderId = order.ClientOrderId,
                Credentials = creds,
            }, ct);

            if (res.NetworkUncertain)
            {
                var outcome = await new OrderReconciler(exchangeFactory)
                    .ReconcileUncertainOrderAsync(state.ExchangeName, state.Symbol, order.ClientOrderId, futures: false, creds, ct);
                switch (outcome.Status)
                {
                    case ReconcileStatus.Filled:
                        logger.LogWarning("Ordine {Cid} riconciliato come ESEGUITO dopo errore di rete (fill {Price} x {Qty}).",
                            order.ClientOrderId, outcome.FillPrice, outcome.FillQty);
                        await persistence.AuditAsync("OrderReconciledFilled",
                            new { order.ClientOrderId, fillPrice = outcome.FillPrice, fillQty = outcome.FillQty }, state.Mode, ts, ct);
                        res = new PlaceOrderResult
                        {
                            Success = true,
                            FilledPrice = outcome.FillPrice,
                            FilledQuantity = outcome.FillQty,
                            ExchangeOrderId = outcome.ExchangeOrderId,
                        };
                        break;

                    case ReconcileStatus.Uncertain:
                        order.Status = OrderStatus.Rejected;
                        order.ErrorMessage = "Errore di rete: stato dell'ordine NON verificabile dopo la riconciliazione. " + res.Error;
                        await persistence.SaveOrderAsync(order, isExisting, ct);
                        await persistence.AuditAsync("OrderReconcileUncertain", new { order.ClientOrderId, res.Error }, state.Mode, ts, ct);
                        logger.LogCritical(
                            "Ordine {Cid}: stato NON verificabile dopo la riconciliazione (cancellazione best-effort inviata). VERIFICARE MANUALMENTE sull'exchange.",
                            order.ClientOrderId);
                        return false;

                    default: // NotFound / TerminalUnfilled: sicuro ritentare alla prossima candela
                        order.Status = OrderStatus.Rejected;
                        order.ErrorMessage = "Errore di rete: ordine NON riscontrato sull'exchange. " + res.Error;
                        await persistence.SaveOrderAsync(order, isExisting, ct);
                        await persistence.AuditAsync("OrderRejected", new
                        {
                            order.ClientOrderId,
                            reason = outcome.Status == ReconcileStatus.NotFound ? "network-uncertain-not-found" : "network-uncertain-terminal",
                            res.Error,
                        }, state.Mode, ts, ct);
                        return false;
                }
            }
            else if (!res.Success)
            {
                order.Status = OrderStatus.Rejected;
                order.ErrorMessage = res.Error;
                await persistence.SaveOrderAsync(order, isExisting, ct);
                await persistence.AuditAsync("OrderRejected", new { order.ClientOrderId, res.Error }, state.Mode, ts, ct);
                return false;
            }

            fillPrice = res.FilledPrice ?? currentPrice;
            fillQty = res.FilledQuantity ?? qty;
            exchangeOrderId = res.ExchangeOrderId;
        }

        var realNotional = fillQty * fillPrice;
        var fee = realNotional * feeFrac;
        if (side == OrderSide.Buy) state.AvailableCapital -= realNotional + fee;
        else state.AvailableCapital += realNotional - fee;

        order.Status = OrderStatus.Filled;
        order.FilledPrice = fillPrice;
        order.FilledQuantity = fillQty;
        order.FilledAtUtc = ts;
        order.ExchangeOrderId = exchangeOrderId;
        metrics?.RecordTradeExecuted(state.Mode.ToString(), side.ToString(), "Open");

        OpenPosition pos;
        if (mergeInto is null)
        {
            pos = new OpenPosition
            {
                PositionId = order.PositionId,
                StrategyId = order.StrategyId,
                Symbol = state.Symbol,
                Side = side,
                EntryPrice = fillPrice,
                Quantity = fillQty,
                OpenedAtUtc = ts,
                CurrentPrice = fillPrice,
                ExchangeOrderId = exchangeOrderId,
                OpenedInMode = state.Mode,
                Leverage = 1,
                MarginBalance = realNotional,
            };
            AutoStopApplier.Apply(pos, order, active);   // SOLO alla creazione — mai su merge
            positions.Add(pos);
        }
        else
        {
            pos = mergeInto;
            var newQty = pos.Quantity + fillQty;
            pos.EntryPrice = newQty > 0m ? (pos.Quantity * pos.EntryPrice + fillQty * fillPrice) / newQty : fillPrice;
            pos.Quantity = newQty;
            pos.MarginBalance += realNotional;
            pos.CurrentPrice = fillPrice;
            pos.ExchangeOrderId = exchangeOrderId;
            // StopLoss/TakeProfit/TrailingStopPercent NON toccati: restano quelli della 1ª fetta.
        }
        state.LastOrderUtc = ts;

        await persistence.SaveOrderAsync(order, isExisting, ct);
        if (mergeInto is null) await persistence.PersistNewPositionAsync(pos, ct); else await persistence.UpdatePositionRowAsync(pos, ct);
        await persistence.AuditAsync("PlaceOrder", new
        {
            order.ClientOrderId, strategyName, side = side.ToString(), qty = fillQty, price = fillPrice, merged = mergeInto is not null,
            autoStopLoss = pos.StopLoss, autoTakeProfit = pos.TakeProfit, autoTrailingStopPercent = pos.TrailingStopPercent,
        }, state.Mode, ts, ct);
        return true;
    }

    /// <summary>
    /// Apertura FUTURES: margine ISOLATO (solo il margine viene sottratto ad AvailableCapital,
    /// non l'intero nozionale leveraged), prezzo di liquidazione dalla fonte di verità
    /// dell'exchange (con fallback alla stima locale <see cref="MarginMath"/>).
    /// </summary>
    public async Task<bool> ExecuteFuturesOpenAsync(
        TradingEngineState state, List<OpenPosition> positions, List<EnsembleStrategy> active, TradingCredentials? credsOrNull, decimal feeFrac,
        Order order, string strategyName, decimal currentPrice, DateTime ts, CancellationToken ct, bool isExisting, OpenPosition? mergeInto = null)
    {
        var side = order.Side;
        var qty = order.Quantity;
        var leverage = Math.Max(1, order.Leverage);

        var fillPrice = currentPrice;
        var fillQty = qty;
        string? exchangeOrderId = null;
        decimal? liquidationPrice = null;

        if (state.Mode != TradingMode.Paper && credsOrNull is TradingCredentials creds)
        {
            var futuresClient = exchangeFactory.CreateFutures(state.ExchangeName);
            var res = await futuresClient.PlaceFuturesOrderAsync(new PlaceOrderRequest
            {
                Symbol = state.Symbol,
                Side = side == OrderSide.Buy ? "BUY" : "SELL",
                Type = "MARKET",
                Quantity = qty,
                ClientOrderId = order.ClientOrderId,
                Credentials = creds,
            }, reduceOnly: false, ct);

            if (res.NetworkUncertain)
            {
                var outcome = await new OrderReconciler(exchangeFactory)
                    .ReconcileUncertainOrderAsync(state.ExchangeName, state.Symbol, order.ClientOrderId, futures: true, creds, ct);
                switch (outcome.Status)
                {
                    case ReconcileStatus.Filled:
                        logger.LogWarning("Ordine futures {Cid} riconciliato come ESEGUITO dopo errore di rete (fill {Price} x {Qty}).",
                            order.ClientOrderId, outcome.FillPrice, outcome.FillQty);
                        await persistence.AuditAsync("OrderReconciledFilled",
                            new { order.ClientOrderId, fillPrice = outcome.FillPrice, fillQty = outcome.FillQty }, state.Mode, ts, ct);
                        res = new PlaceOrderResult
                        {
                            Success = true,
                            FilledPrice = outcome.FillPrice,
                            FilledQuantity = outcome.FillQty,
                            ExchangeOrderId = outcome.ExchangeOrderId,
                        };
                        break;

                    case ReconcileStatus.Uncertain:
                        order.Status = OrderStatus.Rejected;
                        order.ErrorMessage = "Errore di rete: stato dell'ordine futures NON verificabile dopo la riconciliazione. " + res.Error;
                        await persistence.SaveOrderAsync(order, isExisting, ct);
                        await persistence.AuditAsync("OrderReconcileUncertain", new { order.ClientOrderId, res.Error }, state.Mode, ts, ct);
                        logger.LogCritical(
                            "Ordine futures {Cid}: stato NON verificabile dopo la riconciliazione (cancellazione best-effort inviata). VERIFICARE MANUALMENTE sull'exchange.",
                            order.ClientOrderId);
                        return false;

                    default: // NotFound / TerminalUnfilled: sicuro ritentare alla prossima candela
                        order.Status = OrderStatus.Rejected;
                        order.ErrorMessage = "Errore di rete: ordine futures NON riscontrato sull'exchange. " + res.Error;
                        await persistence.SaveOrderAsync(order, isExisting, ct);
                        await persistence.AuditAsync("OrderRejected", new
                        {
                            order.ClientOrderId,
                            reason = outcome.Status == ReconcileStatus.NotFound ? "network-uncertain-not-found" : "network-uncertain-terminal",
                            res.Error,
                        }, state.Mode, ts, ct);
                        return false;
                }
            }
            else if (!res.Success)
            {
                order.Status = OrderStatus.Rejected;
                order.ErrorMessage = res.Error;
                await persistence.SaveOrderAsync(order, isExisting, ct);
                await persistence.AuditAsync("OrderRejected", new { order.ClientOrderId, res.Error }, state.Mode, ts, ct);
                return false;
            }

            fillPrice = res.FilledPrice ?? currentPrice;
            fillQty = res.FilledQuantity ?? qty;
            exchangeOrderId = res.ExchangeOrderId;

            // Prezzo di liquidazione: fonte di verità è l'exchange. Se la posizione non è
            // ancora visibile (race condition tra fill e query) si ricade sulla stima locale.
            try
            {
                var remotePos = await futuresClient.GetPositionAsync(state.Symbol, creds, ct);
                liquidationPrice = remotePos?.LiquidationPrice;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Lettura prezzo di liquidazione dall'exchange fallita: uso la stima locale.");
            }
        }

        var margin = fillQty * fillPrice / leverage;
        var notional = fillQty * fillPrice;
        var fee = notional * feeFrac;

        // Margine ISOLATO: sia long sia short bloccano lo STESSO margine (a differenza dello
        // Spot, qui non c'è "incasso della vendita allo scoperto").
        state.AvailableCapital -= margin + fee;

        liquidationPrice ??= MarginMath.LiquidationPrice(
            fillPrice, fillQty, margin, notional, isLong: side == OrderSide.Buy,
            safety.CurrentValue.MaintenanceMarginPercent / 100m);

        order.Status = OrderStatus.Filled;
        order.FilledPrice = fillPrice;
        order.FilledQuantity = fillQty;
        order.FilledAtUtc = ts;
        order.ExchangeOrderId = exchangeOrderId;
        metrics?.RecordTradeExecuted(state.Mode.ToString(), side.ToString(), "Open");

        OpenPosition pos;
        if (mergeInto is null)
        {
            pos = new OpenPosition
            {
                PositionId = order.PositionId,
                StrategyId = order.StrategyId,
                Symbol = state.Symbol,
                Side = side,
                EntryPrice = fillPrice,
                Quantity = fillQty,
                OpenedAtUtc = ts,
                CurrentPrice = fillPrice,
                ExchangeOrderId = exchangeOrderId,
                OpenedInMode = state.Mode,
                Leverage = leverage,
                LiquidationPrice = liquidationPrice,
                MarginBalance = margin,
            };
            AutoStopApplier.Apply(pos, order, active);
            positions.Add(pos);
        }
        else
        {
            pos = mergeInto;
            var newQty = pos.Quantity + fillQty;
            pos.EntryPrice = newQty > 0m ? (pos.Quantity * pos.EntryPrice + fillQty * fillPrice) / newQty : fillPrice;
            pos.Quantity = newQty;
            pos.MarginBalance += margin;
            pos.CurrentPrice = fillPrice;
            pos.ExchangeOrderId = exchangeOrderId;
            // Liquidazione ricalcolata sui valori FUSI (fonte exchange se disponibile, altrimenti stima locale).
            pos.LiquidationPrice = liquidationPrice ?? MarginMath.LiquidationPrice(
                pos.EntryPrice, pos.Quantity, pos.MarginBalance, pos.Quantity * pos.EntryPrice,
                isLong: pos.Side == OrderSide.Buy, safety.CurrentValue.MaintenanceMarginPercent / 100m);
        }
        state.LastOrderUtc = ts;

        await persistence.SaveOrderAsync(order, isExisting, ct);
        if (mergeInto is null) await persistence.PersistNewPositionAsync(pos, ct); else await persistence.UpdatePositionRowAsync(pos, ct);
        await persistence.AuditAsync("PlaceOrder",
            new
            {
                order.ClientOrderId, strategyName, side = side.ToString(), qty = fillQty, price = fillPrice, leverage,
                liquidationPrice = pos.LiquidationPrice, merged = mergeInto is not null,
                autoStopLoss = pos.StopLoss, autoTakeProfit = pos.TakeProfit, autoTrailingStopPercent = pos.TrailingStopPercent,
            }, state.Mode, ts, ct);

        // [P0-5 follow-up] Protezione "resting" sull'exchange: solo se abilitata (default OFF), su nuova
        // posizione Testnet/Live. Non blocca mai l'apertura — se un trigger reduce-only non viene piazzato
        // (rifiuto dell'exchange), registra un warning e restano gli stop software (fonte di verità).
        // Vedi SafetyConfiguration.UseExchangeRestingStops.
        if (mergeInto is null && state.Mode != TradingMode.Paper
            && safety.CurrentValue.UseExchangeRestingStops && credsOrNull is TradingCredentials restingCreds)
        {
            await BracketManager(state.Mode).TryPlaceRestingBracketAsync(pos, restingCreds, state.ExchangeName, ts, ct);
        }
        return true;
    }
}
