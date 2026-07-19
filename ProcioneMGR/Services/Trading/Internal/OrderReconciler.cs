using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Services.Trading.Internal;

internal enum ReconcileStatus { Filled, NotFound, TerminalUnfilled, Uncertain }

internal sealed record ReconcileOutcome(ReconcileStatus Status, decimal? FillPrice, decimal? FillQty, string? ExchangeOrderId);

/// <summary>
/// Riconcilia un ordine MARKET dall'esito di rete incerto — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-
/// ARCHITETTURA.md §4.5). Estratto da <see cref="TradingEngine"/> senza alcun cambio di comportamento.
/// Interroga lo STATO per clientOrderId (fino a 3 tentativi, pausa 2s): GetOpenOrders non basta, un
/// MARKET riempito durante il blip non è tra gli ordini "aperti" e verrebbe scambiato per "mai
/// piazzato" — posizione reale non tracciata E ordine duplicato alla candela successiva. Se l'ordine
/// risulta ancora vivo viene CANCELLATO e ricontrollato, così non può riempirsi "alle nostre spalle"
/// dopo che lo abbiamo dichiarato assente.
///
/// [B1] I valori di fill nell'<see cref="ReconcileOutcome"/> sono riportati COSÌ COME arrivano
/// dall'exchange e quindi NON fidati: chi li adotta (PositionOpener/PositionCloser) DEVE prima
/// passarli da <see cref="FillSanityCheck"/> — un testnet può rispondere "Filled" con quantità
/// cumulative o prezzo 0 (docs/TEST-UI-2026-07-18.md).
/// </summary>
internal sealed class OrderReconciler(IExchangeClientFactory exchangeFactory)
{
    public async Task<ReconcileOutcome> ReconcileUncertainOrderAsync(
        string exchangeName, string symbol, string clientOrderId, bool futures, TradingCredentials creds, CancellationToken ct)
    {
        var spotClient = futures ? null : exchangeFactory.Create(exchangeName);
        var futuresClient = futures ? exchangeFactory.CreateFutures(exchangeName) : null;

        Task<OrderStatusResult> LookupAsync() => futures
            ? futuresClient!.GetFuturesOrderStatusAsync(symbol, clientOrderId, creds, ct)
            : spotClient!.GetOrderStatusAsync(symbol, clientOrderId, creds, ct);

        Task<CancelOrderResult> CancelAsync() => futures
            ? futuresClient!.CancelFuturesOrderAsync(symbol, clientOrderId, creds, ct)
            : spotClient!.CancelOrderAsync(symbol, clientOrderId, creds, ct);

        static bool HasFill(OrderStatusResult s) =>
            s.Status is "Filled" or "PartiallyFilled" && s.FilledQuantity is > 0m;

        const int MaxAttempts = 3;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var status = await LookupAsync();

            if (status.NetworkUncertain)
            {
                if (attempt < MaxAttempts) await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            if (!status.Found)
            {
                return new ReconcileOutcome(ReconcileStatus.NotFound, null, null, null);
            }
            if (HasFill(status))
            {
                return new ReconcileOutcome(ReconcileStatus.Filled, status.FilledPrice, status.FilledQuantity, status.ExchangeOrderId);
            }
            if (status.IsTerminalUnfilled)
            {
                return new ReconcileOutcome(ReconcileStatus.TerminalUnfilled, null, null, null);
            }

            // Ancora vivo: cancella per chiudere la finestra di duplicazione, poi un ultimo
            // lookup per catturare un fill avvenuto tra la query e la cancellazione.
            await CancelAsync();
            var after = await LookupAsync();
            if (!after.NetworkUncertain)
            {
                if (after.Found && HasFill(after))
                {
                    return new ReconcileOutcome(ReconcileStatus.Filled, after.FilledPrice, after.FilledQuantity, after.ExchangeOrderId);
                }
                return new ReconcileOutcome(ReconcileStatus.TerminalUnfilled, null, null, null);
            }
            if (attempt < MaxAttempts) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        // Stato ancora ignoto dopo tutti i tentativi: cancellazione best-effort per chiudere
        // comunque la finestra; il chiamante deve loggare CRITICAL per la verifica manuale.
        await CancelAsync();
        return new ReconcileOutcome(ReconcileStatus.Uncertain, null, null, null);
    }
}
