using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>Tipo di uscita protettiva scattata su una posizione.</summary>
internal enum ProtectiveExitKind
{
    None,

    /// <summary>Liquidazione forzata (solo Futures): il prezzo ha raggiunto il livello di liquidazione.</summary>
    Liquidation,

    /// <summary>Stop loss, incluso il livello dinamico prodotto dal trailing.</summary>
    StopLoss,

    /// <summary>Take profit.</summary>
    TakeProfit,
}

/// <summary>
/// Esito della valutazione. <see cref="Reason"/> alimenta <c>ClosePositionAsync</c> e finisce in
/// <see cref="TradeRecord.ExitReason"/>: le stringhe devono restare "Liquidation"/"StopLoss"/
/// "TakeProfit" perché <c>PositionCloser</c> deduce <see cref="TradeRecord.WasLiquidated"/> dal
/// prefisso "Liquidation".
/// </summary>
internal readonly record struct ProtectiveExit(ProtectiveExitKind Kind, decimal FillPrice)
{
    public static ProtectiveExit None => new(ProtectiveExitKind.None, 0m);

    public bool ShouldClose => Kind != ProtectiveExitKind.None;

    public string Reason => Kind.ToString();
}

/// <summary>
/// Valutazione PURA (nessun I/O, nessuna mutazione di stato) delle uscite protettive di una
/// posizione: liquidazione, stop loss, take profit e stop dinamico da trailing.
///
/// Estratta da <c>TradingEngine.ProcessCandleAsync</c> senza cambio di comportamento perché ora ha
/// DUE chiamanti che devono decidere in modo identico:
///  - il percorso a candela chiusa, che passa l'OHLC reale della barra;
///  - il percorso a tick real-time (<c>TradingEngine.ProcessPriceTickAsync</c>), che passa una barra
///    degenere <c>open = high = low = close = prezzo corrente</c>.
///
/// Sul tick la degenerazione produce spontaneamente la semantica giusta: il fill calcolato come
/// "esito peggiore fra livello e apertura" collassa sul prezzo corrente di mercato, che è appunto
/// il prezzo realistico di esecuzione in quell'istante. Nessun ramo speciale per il real-time.
/// </summary>
internal static class ProtectiveExitEvaluator
{
    /// <summary>
    /// Livello di stop EFFETTIVO: lo stop fisso, sostituito dal livello di trailing quando questo è
    /// più favorevole. Il trailing è CAUSALE — si calcola sul best-since-entry accumulato dalle
    /// barre PRECEDENTI, mai includendo quella in valutazione (stessa convenzione del backtest).
    /// </summary>
    public static decimal? EffectiveStop(OpenPosition pos)
    {
        var stop = pos.StopLoss;
        if (pos.TrailingStopPercent is not decimal trailPct || trailPct <= 0m)
        {
            return stop;
        }

        var best = pos.BestPriceSinceEntry ?? pos.EntryPrice;
        var trailLevel = pos.Side == OrderSide.Buy
            ? best * (1m - trailPct / 100m)
            : best * (1m + trailPct / 100m);

        var trailIsTighter = stop is null
            || (pos.Side == OrderSide.Buy && trailLevel > stop.Value)
            || (pos.Side == OrderSide.Sell && trailLevel < stop.Value);

        return trailIsTighter ? trailLevel : stop;
    }

    /// <summary>
    /// Liquidazione (solo Futures, e solo con un prezzo di liquidazione noto e positivo). Valutata
    /// PRIMA di tutto il resto: se il mercato ha toccato il livello, la posizione non esiste più
    /// sull'exchange e ogni altra valutazione sarebbe finzione.
    /// </summary>
    public static ProtectiveExit EvaluateLiquidation(OpenPosition pos, decimal high, decimal low, bool isFutures)
    {
        if (!isFutures || pos.LiquidationPrice is not decimal liq || liq <= 0m)
        {
            return ProtectiveExit.None;
        }

        var hit = pos.Side == OrderSide.Buy ? low <= liq : high >= liq;
        return hit ? new ProtectiveExit(ProtectiveExitKind.Liquidation, liq) : ProtectiveExit.None;
    }

    /// <summary>
    /// Stop e target sulla barra. Lo STOP ha la precedenza: se entrambi i livelli cadono nella stessa
    /// barra non si può sapere quale sia stato toccato per primo, e si assume l'esito peggiore.
    /// Il fill è al LIVELLO, oppure all'apertura se la barra ha già aperto oltre (gap) — di nuovo
    /// l'esito peggiore per la posizione, mai il più ottimista.
    /// </summary>
    public static ProtectiveExit EvaluateStopAndTarget(OpenPosition pos, decimal open, decimal high, decimal low)
    {
        var isLong = pos.Side == OrderSide.Buy;

        if (EffectiveStop(pos) is decimal sl && (isLong ? low <= sl : high >= sl))
        {
            var fill = isLong ? Math.Min(sl, open) : Math.Max(sl, open);
            return new ProtectiveExit(ProtectiveExitKind.StopLoss, fill);
        }

        if (pos.TakeProfit is decimal tp && (isLong ? high >= tp : low <= tp))
        {
            var fill = isLong ? Math.Max(tp, open) : Math.Min(tp, open);
            return new ProtectiveExit(ProtectiveExitKind.TakeProfit, fill);
        }

        return ProtectiveExit.None;
    }

    /// <summary>
    /// Aggiorna il best-since-entry usato dal trailing. Va chiamato SOLO se nessuna uscita è
    /// scattata su questa barra: includere la barra che ha già chiuso la posizione sposterebbe il
    /// livello a posteriori, rompendo la causalità del trailing.
    /// Inerte se il trailing non è attivo sulla posizione.
    /// </summary>
    public static void UpdateBestSinceEntry(OpenPosition pos, decimal high, decimal low)
    {
        if (pos.TrailingStopPercent is not > 0m)
        {
            return;
        }

        pos.BestPriceSinceEntry = pos.Side == OrderSide.Buy
            ? Math.Max(pos.BestPriceSinceEntry ?? pos.EntryPrice, high)
            : Math.Min(pos.BestPriceSinceEntry ?? pos.EntryPrice, low);
    }
}
