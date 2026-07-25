using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>
/// Trasforma un segnale di strategia in un ordine dimensionato — Intervento B, Fase 1
/// (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5). Estratto da <see cref="TradingEngine"/> senza alcun
/// cambio di comportamento: stesso guard anti-short-su-spot-reale, stesso dimensionamento
/// (margine isolato sui Futures, nozionale pieno sullo Spot), stesso arrotondamento al LOT_SIZE
/// reale (Testnet/Live) o a precisione fissa (Paper), stessa coda di conferma manuale in Live.
/// <paramref name="tryBuildAndStartExecutionPlanAsync"/> resta un delegato verso
/// <see cref="TradingEngine"/>: decide immediata vs. a fette, fuori da questo collaboratore.
/// </summary>
internal sealed class SignalOrderBuilder(
    ILogger logger,
    TradingPersistence persistence,
    IOptionsMonitor<SafetyConfiguration> safety)
{
    /// <param name="recentCloses">
    /// Chiusure recenti della corsia (solo passato e presente), usate dal dosaggio sulla volatilità.
    /// Null o troppo corte = moltiplicatore 1, cioè comportamento invariato.
    /// </param>
    public async Task TryOpenAsync(
        TradingEngineState state, SymbolFilters? filters,
        Func<Order, EnsembleStrategy?, string, decimal, DateTime, CancellationToken, bool, Task> tryBuildAndStartExecutionPlanAsync,
        EnsembleStrategy strat, OrderSide side, decimal price, DateTime ts, CancellationToken ct,
        IReadOnlyList<decimal>? recentCloses = null)
    {
        if (price <= 0m) return;

        // Guard: sullo SPOT reale non esiste la vendita allo scoperto — un SELL di apertura
        // su Testnet/Live fallirebbe sull'exchange (saldo insufficiente) o, peggio, venderebbe
        // asset del conto NON tracciati da questa corsia. In Paper lo short simulato resta
        // permesso (utile per valutare strategie long/short prima di passarle ai Futures).
        if (state.MarketType == MarketType.Spot && side == OrderSide.Sell && state.Mode != TradingMode.Paper)
        {
            logger.LogWarning("Segnale SHORT su SPOT {Mode} ignorato per {Strategy}: vendita allo scoperto non supportata (usa i Futures).",
                state.Mode, strat.StrategyName);
            await persistence.AuditAsync("ShortOnSpotBlocked", new { strat.StrategyId, strat.StrategyName, price }, state.Mode, ts, ct);
            return;
        }

        // Spot: PositionSizePercent è il nozionale investito (leva implicita 1x).
        // Futures: PositionSizePercent è il MARGINE isolato; il nozionale (e quindi
        // l'esposizione reale) è margine × leva — stessa logica del motore di backtest.
        // La coerenza con MaxPositionSizePercent/MaxTotalExposurePercent è validata a StartAsync.
        //
        // Sopra a questo, il DOSAGGIO sulla volatilità: col default (tetto 1,0) può solo ridurre la
        // dimensione, quindi non può far superare i cap validati a StartAsync. Spento di default.
        var cfg = safety.CurrentValue;
        var volMultiplier = VolatilityScaler.Compute(recentCloses ?? [], state.Timeframe, cfg);
        var margin = state.TotalCapital * cfg.PositionSizePercent / 100m * volMultiplier;
        var notional = state.MarketType == MarketType.Futures ? margin * state.Leverage : margin;
        var qty = notional / price;

        if (volMultiplier != 1m)
        {
            logger.LogDebug("Dosaggio volatilità: dimensione × {Mult:F2} per {Strategy} su {Symbol}.",
                volMultiplier, strat.StrategyName, state.Symbol);
        }

        // Arrotonda al LOT_SIZE reale del simbolo (da exchangeInfo) per Testnet/Live;
        // in Paper usa una precisione fissa ragionevole.
        if (state.Mode != TradingMode.Paper && filters is not null)
        {
            qty = filters.RoundQuantity(qty);
            if (!filters.IsTradable(qty, price))
            {
                logger.LogWarning("Ordine sotto i minimi del simbolo (qty {Qty}, notional {N}): saltato.", qty, qty * price);
                return;
            }
        }
        else
        {
            qty = Math.Round(qty, 5, MidpointRounding.ToZero);
        }
        if (qty <= 0m) return;

        var order = new Order
        {
            PositionId = Guid.NewGuid().ToString("N"),
            StrategyId = strat.StrategyId,
            Symbol = state.Symbol,
            Side = side,
            Type = OrderType.Market,
            Quantity = qty,
            Price = price,
            Status = OrderStatus.Pending,
            CreatedAtUtc = ts,
            Mode = state.Mode,
            MarketType = state.MarketType,
            Leverage = state.MarketType == MarketType.Futures ? state.Leverage : 1,
        };

        // Live: l'apertura richiede conferma manuale dell'operatore -> resta Pending in coda.
        if (state.Mode == TradingMode.Live && safety.CurrentValue.RequireManualConfirmationForLive)
        {
            // Un solo ordine in coda per strategia (niente duplicati se non si conferma subito).
            var pending = await persistence.GetPendingOrdersAsync(ct);
            if (pending.Any(o => o.StrategyId == strat.StrategyId))
            {
                return;
            }
            await persistence.PersistOrderAsync(order, ct);
            await persistence.AuditAsync("PendingConfirmation",
                new { order.ClientOrderId, strat.StrategyName, side = side.ToString(), qty, price }, state.Mode, ts, ct);
            logger.LogInformation("Ordine Live {Cid} in attesa di conferma manuale.", order.ClientOrderId);
            return;
        }

        await tryBuildAndStartExecutionPlanAsync(order, strat, strat.StrategyName, price, ts, ct, false);
    }
}
