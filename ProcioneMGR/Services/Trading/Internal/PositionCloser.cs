using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Observability;

namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>
/// Chiusura di posizioni Spot e Futures — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md
/// §4.5). Estratto da <see cref="TradingEngine"/> senza alcun cambio di comportamento: stesse
/// chiamate exchange, stessa gestione della riconciliazione di rete incerta, stesso calcolo di PnL/
/// capitale disponibile. Riceve <paramref name="state"/> e <paramref name="positions"/> come
/// riferimenti diretti (non copie): le mutazioni (AvailableCapital, RealizedPnl, DailyPnl,
/// rimozione da positions) sono visibili a <see cref="TradingEngine"/> esattamente
/// come quando il codice viveva inline.
///
/// NON tocca <see cref="TradingEngineState.LastOrderUtc"/>, di proposito: quel timestamp alimenta
/// SOLO l'anti-spam n.6 del <see cref="SafetyChecker"/>, che gira esclusivamente sul percorso di
/// APERTURA. Segnarlo anche in chiusura faceva pagare alla successiva apertura il throttle di una
/// chiusura appena avvenuta, e su un'inversione di segnale (chiudi long → apri short sulla stessa
/// candela) l'apertura opposta veniva rifiutata con elapsed = 0. Vedi docs/REPORT-RICERCA-2026-07.md.
/// </summary>
internal sealed class PositionCloser(
    IExchangeClientFactory exchangeFactory,
    ILogger logger,
    TradingPersistence persistence,
    ProcioneMetrics? metrics,
    IOptionsMonitor<SafetyConfiguration> safety)
{
    /// <summary>[Fase 1] Vedi <see cref="PositionOpener"/>: stessa etichetta d'esito, stessa metrica.</summary>
    private static string OrderOutcome(PlaceOrderResult res) =>
        res.NetworkUncertain ? "network_uncertain" : res.Success ? "ok" : "rejected";

    /// <summary>
    /// [Fase 1] Shortfall della chiusura, azione <c>Close</c>. Il lato passato è quello dell'ordine
    /// di chiusura (già dentro <see cref="Order.Side"/>), non quello della posizione: è l'ordine a
    /// pagare lo slittamento.
    /// </summary>
    private void RecordShortfall(Order closeOrder, TradingMode mode)
    {
        if (closeOrder.ShortfallBps is decimal bps)
        {
            metrics?.RecordTradingSlippage((double)bps, mode.ToString(), "Close");
        }
    }

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

        // [Fase 1] Il prezzo di riferimento della decisione, prima che `exitPrice` venga sostituito
        // dal fill poco sotto: era esattamente questo passaggio a rendere il costo di una chiusura
        // non misurabile a posteriori. Resta null in Paper, dove fill e riferimento coincidono.
        decimal? arrivalPrice = null;
        int? submitLatencyMs = null;

        // Testnet/Live: piazza l'ordine di chiusura reale (market opposto).
        if (state.Mode != TradingMode.Paper && credsOrNull is TradingCredentials creds)
        {
            arrivalPrice = exitPrice;

            var client = exchangeFactory.Create(state.ExchangeName);
            var (res, latencyMs) = await ExecutionQuality.MeasureAsync(() => client.PlaceOrderAsync(new PlaceOrderRequest
            {
                Symbol = pos.Symbol,
                Side = closeSide == OrderSide.Buy ? "BUY" : "SELL",
                Type = "MARKET",
                Quantity = qty,
                ClientOrderId = closeClientId,
                Credentials = creds,
            }, ct));

            submitLatencyMs = latencyMs;
            metrics?.RecordOrderLatency(latencyMs, state.ExchangeName.ToString(), "spot", OrderOutcome(res));

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
            ArrivalPrice = arrivalPrice,
            SubmitLatencyMs = submitLatencyMs,
        };
        RecordShortfall(closeOrder, state.Mode);

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

        // [Fase 1] Come nel ramo Spot: il riferimento va salvato prima che il fill lo sostituisca.
        decimal? arrivalPrice = null;
        int? submitLatencyMs = null;

        if (!alreadyClosedOnExchange && state.Mode != TradingMode.Paper && credsOrNull is TradingCredentials creds)
        {
            var futuresClient = exchangeFactory.CreateFutures(state.ExchangeName);

            // [P0-5 follow-up] Cancella eventuali ordini TRIGGER resting prima del market close, per non
            // lasciarli orfani sull'exchange. Inerte se non ce ne sono (feature off/stub → id sempre null).
            if (pos.StopOrderId is not null || pos.TakeProfitOrderId is not null)
            {
                await BracketManager(state.Mode).TryCancelRestingBracketAsync(pos, creds, state.ExchangeName, ct);
            }

            // Il riferimento si fissa DOPO la cancellazione del bracket: quella è una chiamata a sé,
            // e includerla nella latenza dell'ordine di chiusura falserebbe la misura.
            arrivalPrice = exitPrice;

            var (res, latencyMs) = await ExecutionQuality.MeasureAsync(() => futuresClient.PlaceFuturesOrderAsync(new PlaceOrderRequest
            {
                Symbol = pos.Symbol,
                Side = closeSide == OrderSide.Buy ? "BUY" : "SELL",
                Type = "MARKET",
                Quantity = qty,
                ClientOrderId = closeClientId,
                Credentials = creds,
            }, reduceOnly: true, ct));

            submitLatencyMs = latencyMs;
            metrics?.RecordOrderLatency(latencyMs, state.ExchangeName.ToString(), "futures", OrderOutcome(res));

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

        var grossPnl = pos.Side == OrderSide.Buy
            ? (exitPrice - entry) * qty
            : (entry - exitPrice) * qty;
        var pnl = grossPnl - entryFee - exitFee;

        // Margine ISOLATO: si restituisce il margine bloccato + PnL (guadagno o perdita),
        // MAI il nozionale intero (a differenza dello Spot).
        //
        // [2026-08-17] Si riaccredita il PnL LORDO meno la sola fee d'USCITA, non `pnl`: la fee
        // d'INGRESSO è già uscita dalla cassa all'apertura (PositionOpener: `-= margin + fee`).
        // Sommare qui `pnl`, che la contiene di nuovo, la faceva pagare DUE volte: la cassa
        // divergeva da RealizedPnl di una fee a ogni chiusura, in modo cumulativo e permanente, e
        // la curva equity — da cui GetPerformanceAsync ricava Sharpe e MaxDrawdown, cioè i numeri
        // su cui PromotionEvaluator decide — faceva uno scalino verso il basso a ogni round trip
        // anche a prezzo invariato. Il ramo Spot faceva già così (flusso di cassa, non PnL), e
        // BacktestEngine.Close è il riferimento: `Cash += _margin + pnlRaw - exitFee`.
        // RealizedPnl e TradeRecord.Pnl restano al netto di ENTRAMBE le fee: sono corretti, era la
        // cassa a essere sbagliata.
        state.AvailableCapital += pos.MarginBalance + grossPnl - exitFee;

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
            ArrivalPrice = arrivalPrice,
            SubmitLatencyMs = submitLatencyMs,
        };
        RecordShortfall(closeOrder, state.Mode);

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

        await persistence.PersistOrderAsync(closeOrder, ct);
        await persistence.RemovePositionAsync(pos, ct);
        await persistence.PersistTradeAsync(trade, ct);
        await persistence.AuditAsync("ClosePosition", new { pos.PositionId, pnl, reason, wasLiquidated }, state.Mode, ts, ct);
    }
}
