using ProcioneMGR.Services.Ensemble;

namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>
/// Applica automaticamente stop-loss/take-profit/trailing validati nel backtest alla posizione
/// appena aperta — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5). Estratto da
/// <see cref="TradingEngine"/> senza alcun cambio di comportamento: pura funzione di calcolo,
/// nessuna dipendenza da I/O. Gira SOLO alla creazione della posizione: nessun altro punto del
/// motore rimette mano a questi valori, quindi una modifica manuale successiva da
/// <c>/trading</c> resta sempre l'ultima parola.
/// </summary>
internal static class AutoStopApplier
{
    public static void Apply(OpenPosition pos, Order order, IReadOnlyList<EnsembleStrategy> active)
    {
        var strat = active.FirstOrDefault(s => s.StrategyId == order.StrategyId);
        if (strat is null) return;

        if (strat.StopLossPercent is decimal slPct && slPct > 0m)
        {
            pos.StopLoss = pos.Side == OrderSide.Buy
                ? pos.EntryPrice * (1m - slPct / 100m)
                : pos.EntryPrice * (1m + slPct / 100m);
        }
        if (strat.TakeProfitPercent is decimal tpPct && tpPct > 0m)
        {
            pos.TakeProfit = pos.Side == OrderSide.Buy
                ? pos.EntryPrice * (1m + tpPct / 100m)
                : pos.EntryPrice * (1m - tpPct / 100m);
        }
        if (strat.TrailingStopPercent is decimal trailPct && trailPct > 0m)
        {
            pos.TrailingStopPercent = trailPct;
            pos.BestPriceSinceEntry = pos.EntryPrice;
        }
    }
}
