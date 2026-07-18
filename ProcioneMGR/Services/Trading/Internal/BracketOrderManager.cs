using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>
/// Piazzamento/cancellazione degli ordini trigger resting (stop-loss/take-profit lato exchange) sui
/// Futures — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5). Estratto da
/// <see cref="TradingEngine"/> senza alcun cambio di comportamento: stesse chiamate, stesso ordine,
/// stessa gestione degli errori (mai bloccante — ogni fallimento resta solo loggato, gli stop
/// software restano la fonte di verità). Riceve <paramref name="auditAsync"/>/
/// <paramref name="updatePositionRowAsync"/> come delegati verso i metodi privati di persistenza di
/// <see cref="TradingEngine"/> invece di duplicarli: stessa identica scrittura, testabile in
/// isolamento passando dei fake.
/// </summary>
internal sealed class BracketOrderManager(
    IExchangeClientFactory exchangeFactory,
    ILogger logger,
    Func<string, object, DateTime, CancellationToken, Task> auditAsync,
    Func<OpenPosition, CancellationToken, Task> updatePositionRowAsync)
{
    /// <summary>
    /// Piazza gli ordini trigger STOP_MARKET/TAKE_PROFIT_MARKET sul lato opposto della posizione.
    /// Invocato solo se <see cref="SafetyConfiguration.UseExchangeRestingStops"/> è attivo (default OFF).
    /// </summary>
    public async Task TryPlaceRestingBracketAsync(OpenPosition pos, TradingCredentials creds, string exchangeName, DateTime ts, CancellationToken ct)
    {
        var closeSide = pos.Side == OrderSide.Buy ? "SELL" : "BUY"; // ordine di protezione = lato opposto
        var futuresClient = exchangeFactory.CreateFutures(exchangeName);

        async Task PlaceAsync(decimal trigger, bool isStopLoss, Action<string> onPlaced)
        {
            var clientId = Guid.NewGuid().ToString("N");
            var res = await futuresClient.PlaceFuturesTriggerOrderAsync(new PlaceOrderRequest
            {
                Symbol = pos.Symbol,
                Side = closeSide,
                Type = isStopLoss ? "STOP_MARKET" : "TAKE_PROFIT_MARKET",
                Quantity = pos.Quantity,
                TriggerPrice = trigger,
                ClientOrderId = clientId,
                Credentials = creds,
            }, isStopLoss, ct);

            if (res.Success)
            {
                onPlaced(clientId);
                await auditAsync("RestingStopPlaced", new { pos.PositionId, kind = isStopLoss ? "stop" : "target", trigger, clientId }, ts, ct);
            }
            else
            {
                logger.LogWarning("Ordine resting {Kind} non piazzato per {Pid}: {Err}. Resta lo stop software.",
                    isStopLoss ? "stop" : "target", pos.PositionId, res.Error);
            }
        }

        if (pos.StopLoss is decimal sl && sl > 0m) await PlaceAsync(sl, isStopLoss: true, id => pos.StopOrderId = id);
        if (pos.TakeProfit is decimal tp && tp > 0m) await PlaceAsync(tp, isStopLoss: false, id => pos.TakeProfitOrderId = id);

        // [M3] Persistenza immediata degli id: senza, un riavvio perdeva i clientOrderId dei
        // trigger REALI ancora armati sull'exchange e la chiusura non poteva più cancellarli.
        if (pos.StopOrderId is not null || pos.TakeProfitOrderId is not null)
        {
            await updatePositionRowAsync(pos, ct);
        }
    }

    /// <summary>
    /// [P0-5] Cancella gli ordini TRIGGER resting prima di chiudere a mercato, così non restano
    /// ordini orfani sull'exchange. INERTE se non ci sono id (feature off, default).
    /// </summary>
    public async Task TryCancelRestingBracketAsync(OpenPosition pos, TradingCredentials creds, string exchangeName, CancellationToken ct)
    {
        var futuresClient = exchangeFactory.CreateFutures(exchangeName);
        foreach (var clientId in new[] { pos.StopOrderId, pos.TakeProfitOrderId })
        {
            if (string.IsNullOrEmpty(clientId)) continue;
            var res = await futuresClient.CancelFuturesOrderAsync(pos.Symbol, clientId, creds, ct);
            if (!res.Success)
            {
                logger.LogWarning("Cancellazione ordine resting {Cid} per {Pid} fallita: {Err}.", clientId, pos.PositionId, res.Error);
            }
        }
        pos.StopOrderId = null;
        pos.TakeProfitOrderId = null;
        await updatePositionRowAsync(pos, ct);   // [M3] azzeramento persistito come il piazzamento
    }
}
