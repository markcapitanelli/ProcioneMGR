using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>
/// Ogni candela (solo Futures, Testnet/Live), verifica sull'exchange che le posizioni locali siano
/// ancora aperte — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5). Estratto da
/// <see cref="TradingEngine"/> senza alcun cambio di comportamento. L'exchange può liquidare/
/// chiudere una posizione indipendentemente dal ciclo del motore: se risulta flat lato exchange ma
/// aperta localmente, la chiudiamo qui con il miglior prezzo noto. Difesa inversa: una posizione
/// aperta sull'exchange ma sconosciuta al motore NON viene mai chiusa d'ufficio, solo allertata una
/// volta finché la condizione persiste.
///
/// <c>untrackedRemoteAlerted</c> è passato/restituito per valore (non <c>ref</c>: non consentito in
/// un metodo <c>async</c>) — il chiamante riassegna il campo dell'engine con il valore restituito.
/// </summary>
internal sealed class FuturesPositionReconciler(
    IExchangeClientFactory exchangeFactory,
    ILogger logger,
    TradingPersistence persistence)
{
    public async Task<bool> ReconcileAsync(
        TradingEngineState state, List<OpenPosition> positions, TradingCredentials? credsOrNull,
        Func<OpenPosition, decimal, string, DateTime, CancellationToken, bool, Task> closePositionAsync,
        bool untrackedRemoteAlerted, decimal lastKnownPrice, DateTime ts, CancellationToken ct)
    {
        // NB: si interroga l'exchange anche a posizioni locali ZERO, per la difesa inversa qui
        // sotto (posizione remota che il motore non conosce) — una chiamata firmata per candela.
        if (state.MarketType != MarketType.Futures || state.Mode == TradingMode.Paper || credsOrNull is not TradingCredentials creds)
        {
            return untrackedRemoteAlerted;
        }

        var futuresClient = exchangeFactory.CreateFutures(state.ExchangeName);
        FuturesPosition? remote;
        try
        {
            remote = await futuresClient.GetPositionAsync(state.Symbol, creds, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Riconciliazione futures fallita (rete): salto questo ciclo.");
            return untrackedRemoteAlerted;
        }

        if (remote is not null)
        {
            // Difesa inversa: posizione APERTA sull'exchange ma sconosciuta al motore (es. esito
            // di un ordine dichiarato incerto, o apertura manuale fuori piattaforma). NESSUNA
            // auto-azione — chiuderla d'ufficio potrebbe distruggere un'operazione voluta
            // dall'operatore; si allerta una sola volta finché la condizione persiste.
            if (!positions.Any(p => p.Symbol == state.Symbol))
            {
                if (!untrackedRemoteAlerted)
                {
                    untrackedRemoteAlerted = true;
                    logger.LogCritical(
                        "Posizione {Side} {Qty} {Sym} APERTA sull'exchange ma SCONOSCIUTA al motore: VERIFICARE MANUALMENTE (nessuna azione automatica).",
                        remote.Side, remote.Quantity, state.Symbol);
                    await persistence.AuditAsync("UntrackedRemotePosition",
                        new { state.Symbol, remote.Side, remote.Quantity, remote.EntryPrice }, state.Mode, ts, ct);
                }
            }
            else
            {
                untrackedRemoteAlerted = false;
            }
            return untrackedRemoteAlerted;
        }

        untrackedRemoteAlerted = false;
        foreach (var pos in positions.Where(p => p.Symbol == state.Symbol).ToList())
        {
            logger.LogWarning("Posizione {Pid} risulta chiusa sull'exchange ma aperta localmente: riconciliazione (probabile liquidazione esterna).", pos.PositionId);
            await closePositionAsync(pos, lastKnownPrice, "Liquidation/ExternalClose", ts, ct, true);
        }
        return untrackedRemoteAlerted;
    }
}
