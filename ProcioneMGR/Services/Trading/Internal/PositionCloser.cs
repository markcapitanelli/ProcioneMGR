using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>
/// Chiusura di posizioni Spot e Futures — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md
/// §4.5). Estratto da <see cref="TradingEngine"/> senza alcun cambio di comportamento: stesse
/// chiamate exchange, stessa gestione della riconciliazione di rete incerta, stesso calcolo di PnL/
/// capitale disponibile. Riceve <paramref name="state"/> e <paramref name="positions"/> come
/// riferimenti diretti (non copie): le mutazioni (AvailableCapital, RealizedPnl, DailyPnl,
/// LastOrderUtc, rimozione da positions) sono visibili a <see cref="TradingEngine"/> esattamente
/// come quando il codice viveva inline.
/// </summary>
internal sealed class PositionCloser(
    IExchangeClientFactory exchangeFactory,
    ILogger logger,
    TradingPersistence persistence,
    IOptionsMonitor<SafetyConfiguration> safety)
{
    /// <summary>
    /// [B1] Fill di chiusura implausibile (vedi <see cref="FillSanityCheck"/>): la chiusura si
    /// finalizza comunque — l'ordine è andato a buon fine e rifiutarla riaprirebbe il loop di
    /// oversell del bug H2 — ma al prezzo di riferimento locale, MAI ai valori riportati.
    /// Ritorna il prezzo da usare (il fill se plausibile e positivo, il riferimento altrimenti).
    /// </summary>
    private async Task<decimal> SanitizedExitPriceAsync(
        OpenPosition pos, string closeClientId, decimal? reportedPrice, decimal? reportedQty,
        decimal requestedQty, decimal referencePrice, TradingMode mode, DateTime ts, CancellationToken ct)
    {
        if (FillSanityCheck.IsSuspect(reportedPrice, reportedQty, requestedQty, referencePrice, safety.CurrentValue, out var reason))
        {
            logger.LogError(
                "Chiusura {Pid}: fill SOSPETTO dall'exchange ({Reason}): finalizzo al prezzo di riferimento {Ref}.",
                pos.PositionId, reason, referencePrice);
            await persistence.AuditAsync("FillSanityRejected", new
            {
                pos.PositionId, closeClientId, reportedPrice, reportedQty,
                requestedQty, referencePrice, reason,
            }, mode, ts, ct);
            return referencePrice;
        }
        return reportedPrice is decimal p && p > 0m ? p : referencePrice;
    }
    private BracketOrderManager BracketManager(TradingMode mode) => new(
        exchangeFactory, logger,
        (action, details, ts, ct) => persistence.AuditAsync(action, details, mode, ts, ct),
        persistence.UpdatePositionRowAsync);

    /// <summary>Chiusura SPOT (comportamento INVARIATO rispetto a prima dell'introduzione dei Futures).</summary>
    public async Task CloseSpotPositionAsync(
        TradingEngineState state, List<OpenPosition> positions, TradingCredentials? credsOrNull, decimal feeFrac,
        OpenPosition pos, decimal exitPrice, string reason, DateTime ts, CancellationToken ct)
    {
        var qty = pos.Quantity;
        var entry = pos.EntryPrice;
        var closeSide = pos.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var closeClientId = Guid.NewGuid().ToString("N");

        // Testnet/Live: piazza l'ordine di chiusura reale (market opposto).
        if (state.Mode != TradingMode.Paper && credsOrNull is TradingCredentials creds)
        {
            var client = exchangeFactory.Create(state.ExchangeName);
            var res = await client.PlaceOrderAsync(new PlaceOrderRequest
            {
                Symbol = pos.Symbol,
                Side = closeSide == OrderSide.Buy ? "BUY" : "SELL",
                Type = "MARKET",
                Quantity = qty,
                ClientOrderId = closeClientId,
                Credentials = creds,
            }, ct);
            if (res.NetworkUncertain)
            {
                var outcome = await new OrderReconciler(exchangeFactory)
                    .ReconcileUncertainOrderAsync(state.ExchangeName, pos.Symbol, closeClientId, futures: false, creds, ct);
                if (outcome.Status == ReconcileStatus.Filled)
                {
                    // La chiusura È avvenuta durante il blip: si finalizza con il fill reale.
                    // Prima (check sui soli open orders) la posizione restava aperta localmente
                    // PER SEMPRE: ogni retry rivendeva un asset già venduto (oversell rifiutato).
                    logger.LogWarning("Chiusura {Pid} riconciliata come ESEGUITA dopo errore di rete (fill {Price}).",
                        pos.PositionId, outcome.FillPrice);
                    await persistence.AuditAsync("CloseReconciledFilled",
                        new { pos.PositionId, closeClientId, fillPrice = outcome.FillPrice }, state.Mode, ts, ct);
                    exitPrice = await SanitizedExitPriceAsync(pos, closeClientId, outcome.FillPrice, outcome.FillQty, qty, exitPrice, state.Mode, ts, ct);
                }
                else
                {
                    // NotFound/terminale: mai eseguita → retry alla prossima candela (nuovo ordine).
                    // Uncertain: una chiusura NON si finalizza MAI da uno stato ignoto (il rischio
                    // di oversell è peggiore del retry); la cancellazione best-effort è già partita.
                    logger.LogError("Chiusura {Pid} incerta e non confermata dall'exchange (esito {Outcome}): la posizione resta aperta.",
                        pos.PositionId, outcome.Status);
                    await persistence.AuditAsync("CloseUncertain", new { pos.PositionId, outcome = outcome.Status.ToString(), res.Error }, state.Mode, ts, ct);
                    return;
                }
            }
            else if (!res.Success)
            {
                logger.LogError("Chiusura {Pid} rifiutata dall'exchange: {Err}. Posizione mantenuta.", pos.PositionId, res.Error);
                await persistence.AuditAsync("CloseRejected", new { pos.PositionId, res.Error }, state.Mode, ts, ct);
                return;
            }
            else if (res.FilledPrice is not null)
            {
                exitPrice = await SanitizedExitPriceAsync(pos, closeClientId, res.FilledPrice, res.FilledQuantity, qty, exitPrice, state.Mode, ts, ct);
            }
        }

        var entryFee = qty * entry * feeFrac;
        var exitFee = qty * exitPrice * feeFrac;

        decimal pnl;
        if (pos.Side == OrderSide.Buy)
        {
            state.AvailableCapital += qty * exitPrice - exitFee;
            pnl = (exitPrice - entry) * qty - entryFee - exitFee;
        }
        else
        {
            state.AvailableCapital -= qty * exitPrice + exitFee;
            pnl = (entry - exitPrice) * qty - entryFee - exitFee;
        }

        state.RealizedPnl += pnl;
        if ((ts - state.DailyAnchorUtc).TotalHours >= 24) { state.DailyPnl = 0m; state.DailyAnchorUtc = ts; }
        state.DailyPnl += pnl;

        var closeOrder = new Order
        {
            ClientOrderId = closeClientId,
            PositionId = pos.PositionId,
            StrategyId = pos.StrategyId,
            Symbol = pos.Symbol,
            Side = closeSide,
            Type = OrderType.Market,
            Quantity = qty,
            Price = exitPrice,
            Status = OrderStatus.Filled,
            FilledPrice = exitPrice,
            FilledQuantity = qty,
            CreatedAtUtc = ts,
            FilledAtUtc = ts,
            Mode = state.Mode,
        };

        var trade = new TradeRecord
        {
            PositionId = pos.PositionId,
            StrategyId = pos.StrategyId,
            Symbol = pos.Symbol,
            Side = pos.Side,
            EntryPrice = entry,
            ExitPrice = exitPrice,
            Quantity = qty,
            Pnl = pnl,
            PnlPercent = entry > 0m ? pnl / (qty * entry) * 100m : 0m,
            OpenedAtUtc = pos.OpenedAtUtc,
            ClosedAtUtc = ts,
            Duration = ts - pos.OpenedAtUtc,
            ExitReason = reason,
            Mode = state.Mode,
        };

        positions.Remove(pos);
        state.LastOrderUtc = ts;

        await persistence.PersistOrderAsync(closeOrder, ct);
        await persistence.RemovePositionAsync(pos, ct);
        await persistence.PersistTradeAsync(trade, ct);
        await persistence.AuditAsync("ClosePosition", new { pos.PositionId, pnl, reason }, state.Mode, ts, ct);
    }

    /// <summary>
    /// Chiusura FUTURES: ordine reduceOnly opposto (salvo <paramref name="alreadyClosedOnExchange"/>,
    /// usato dalla riconciliazione quando l'exchange ha già liquidato/chiuso la posizione), rimborso
    /// del margine isolato (non del nozionale) + PnL, PnL% calcolata sul margine.
    /// </summary>
    public async Task CloseFuturesPositionAsync(
        TradingEngineState state, List<OpenPosition> positions, TradingCredentials? credsOrNull, decimal feeFrac,
        OpenPosition pos, decimal exitPrice, string reason, DateTime ts, CancellationToken ct, bool alreadyClosedOnExchange)
    {
        var qty = pos.Quantity;
        var entry = pos.EntryPrice;
        var closeSide = pos.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var closeClientId = Guid.NewGuid().ToString("N");

        if (!alreadyClosedOnExchange && state.Mode != TradingMode.Paper && credsOrNull is TradingCredentials creds)
        {
            var futuresClient = exchangeFactory.CreateFutures(state.ExchangeName);

            // [P0-5 follow-up] Cancella eventuali ordini TRIGGER resting prima del market close, per non
            // lasciarli orfani sull'exchange. Inerte se non ce ne sono (feature off/stub → id sempre null).
            if (pos.StopOrderId is not null || pos.TakeProfitOrderId is not null)
            {
                await BracketManager(state.Mode).TryCancelRestingBracketAsync(pos, creds, state.ExchangeName, ct);
            }

            var res = await futuresClient.PlaceFuturesOrderAsync(new PlaceOrderRequest
            {
                Symbol = pos.Symbol,
                Side = closeSide == OrderSide.Buy ? "BUY" : "SELL",
                Type = "MARKET",
                Quantity = qty,
                ClientOrderId = closeClientId,
                Credentials = creds,
            }, reduceOnly: true, ct);
            if (res.NetworkUncertain)
            {
                var outcome = await new OrderReconciler(exchangeFactory)
                    .ReconcileUncertainOrderAsync(state.ExchangeName, pos.Symbol, closeClientId, futures: true, creds, ct);
                if (outcome.Status == ReconcileStatus.Filled)
                {
                    // La chiusura È avvenuta durante il blip: si finalizza con il fill reale.
                    // Prima la posizione restava aperta finché ReconcileFuturesPositionsAsync non
                    // la forzava a lastKnownPrice come "Liquidation/ExternalClose" — prezzo
                    // sbagliato e WasLiquidated fuorviante.
                    logger.LogWarning("Chiusura futures {Pid} riconciliata come ESEGUITA dopo errore di rete (fill {Price}).",
                        pos.PositionId, outcome.FillPrice);
                    await persistence.AuditAsync("CloseReconciledFilled",
                        new { pos.PositionId, closeClientId, fillPrice = outcome.FillPrice }, state.Mode, ts, ct);
                    exitPrice = await SanitizedExitPriceAsync(pos, closeClientId, outcome.FillPrice, outcome.FillQty, qty, exitPrice, state.Mode, ts, ct);
                }
                else
                {
                    // NotFound/terminale: mai eseguita → retry alla prossima candela (nuovo ordine).
                    // Uncertain: mai finalizzare da stato ignoto (cancellazione best-effort già partita).
                    logger.LogError("Chiusura futures {Pid} incerta e non confermata dall'exchange (esito {Outcome}): la posizione resta aperta.",
                        pos.PositionId, outcome.Status);
                    await persistence.AuditAsync("CloseUncertain", new { pos.PositionId, outcome = outcome.Status.ToString(), res.Error }, state.Mode, ts, ct);
                    return;
                }
            }
            else if (!res.Success)
            {
                logger.LogError("Chiusura futures {Pid} rifiutata dall'exchange: {Err}. Posizione mantenuta.", pos.PositionId, res.Error);
                await persistence.AuditAsync("CloseRejected", new { pos.PositionId, res.Error }, state.Mode, ts, ct);
                return;
            }
            else if (res.FilledPrice is not null)
            {
                exitPrice = await SanitizedExitPriceAsync(pos, closeClientId, res.FilledPrice, res.FilledQuantity, qty, exitPrice, state.Mode, ts, ct);
            }
        }

        var entryFee = qty * entry * feeFrac;
        var exitFee = qty * exitPrice * feeFrac;

        var pnl = pos.Side == OrderSide.Buy
            ? (exitPrice - entry) * qty - entryFee - exitFee
            : (entry - exitPrice) * qty - entryFee - exitFee;

        // Margine ISOLATO: si restituisce il margine bloccato + PnL (guadagno o perdita),
        // MAI il nozionale intero (a differenza dello Spot).
        state.AvailableCapital += pos.MarginBalance + pnl;

        state.RealizedPnl += pnl;
        if ((ts - state.DailyAnchorUtc).TotalHours >= 24) { state.DailyPnl = 0m; state.DailyAnchorUtc = ts; }
        state.DailyPnl += pnl;

        var wasLiquidated = reason.StartsWith("Liquidation", StringComparison.Ordinal);

        var closeOrder = new Order
        {
            ClientOrderId = closeClientId,
            PositionId = pos.PositionId,
            StrategyId = pos.StrategyId,
            Symbol = pos.Symbol,
            Side = closeSide,
            Type = OrderType.Market,
            Quantity = qty,
            Price = exitPrice,
            Status = OrderStatus.Filled,
            FilledPrice = exitPrice,
            FilledQuantity = qty,
            CreatedAtUtc = ts,
            FilledAtUtc = ts,
            Mode = state.Mode,
            MarketType = MarketType.Futures,
            Leverage = pos.Leverage,
        };

        var trade = new TradeRecord
        {
            PositionId = pos.PositionId,
            StrategyId = pos.StrategyId,
            Symbol = pos.Symbol,
            Side = pos.Side,
            EntryPrice = entry,
            ExitPrice = exitPrice,
            Quantity = qty,
            Pnl = pnl,
            PnlPercent = pos.MarginBalance > 0m ? pnl / pos.MarginBalance * 100m : 0m,
            OpenedAtUtc = pos.OpenedAtUtc,
            ClosedAtUtc = ts,
            Duration = ts - pos.OpenedAtUtc,
            ExitReason = reason,
            Mode = state.Mode,
            MarketType = MarketType.Futures,
            Leverage = pos.Leverage,
            WasLiquidated = wasLiquidated,
        };

        positions.Remove(pos);
        state.LastOrderUtc = ts;

        await persistence.PersistOrderAsync(closeOrder, ct);
        await persistence.RemovePositionAsync(pos, ct);
        await persistence.PersistTradeAsync(trade, ct);
        await persistence.AuditAsync("ClosePosition", new { pos.PositionId, pnl, reason, wasLiquidated }, state.Mode, ts, ct);
    }
}
