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

        // [2026-08-17] Si azzera SOLO ciò che è stato davvero cancellato. Prima l'azzeramento
        // stava fuori dal controllo di esito: una DELETE fallita (5xx, timeout, rate limit)
        // lasciava il trigger ARMATO sull'exchange e buttava via il suo clientOrderId, cioè
        // l'unica chiave con cui qualcuno avrebbe potuto cancellarlo dopo. Un ordine di protezione
        // orfano su una posizione che non esiste più può aprirne una nuova, al contrario.
        // Tenere l'id è ciò che rende il guasto rimediabile; il LogCritical + audit è ciò che lo
        // rende visibile, perché qui nessuno ha un secondo tentativo automatico.
        async Task<bool> CancelAsync(string clientId)
        {
            var res = await futuresClient.CancelFuturesOrderAsync(pos.Symbol, clientId, creds, ct);
            if (res.Success) return true;

            logger.LogCritical(
                "Cancellazione ordine resting {Cid} per {Pid} FALLITA: {Err}. Il trigger può essere ancora "
                + "armato sull'exchange: l'id viene CONSERVATO per poterlo cancellare a mano.",
                clientId, pos.PositionId, res.Error);
            await auditAsync("RestingStopCancelFailed",
                new { pos.PositionId, pos.Symbol, clientId, error = res.Error }, DateTime.UtcNow, ct);
            return false;
        }

        if (!string.IsNullOrEmpty(pos.StopOrderId) && await CancelAsync(pos.StopOrderId))
        {
            pos.StopOrderId = null;
        }
        if (!string.IsNullOrEmpty(pos.TakeProfitOrderId) && await CancelAsync(pos.TakeProfitOrderId))
        {
            pos.TakeProfitOrderId = null;
        }
        await updatePositionRowAsync(pos, ct);   // [M3] azzeramento persistito come il piazzamento
    }
}
