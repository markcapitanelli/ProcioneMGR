namespace ProcioneMGR.Services.Risk;

/// <summary>
/// Matematica del margine isolato (leva/liquidazione), condivisa tra il motore di backtest
/// (<c>BacktestEngine.Portfolio</c>) e il trading live a futures (<c>TradingEngine</c>), così
/// il prezzo di liquidazione STIMATO in un contesto e quello nell'altro sono calcolati con la
/// STESSA formula — nessun rischio che backtest e live disegnino un rischio diverso per lo
/// stesso trade. Funzioni pure, nessuna dipendenza da I/O o da altri servizi.
///
/// Nota: per una posizione REALE su un exchange, il prezzo di liquidazione autoritativo è
/// quello riportato dall'exchange stesso (include fondo assicurativo, mark price vs last
/// price, eventuali fee di liquidazione) — queste formule sono una stima prudente usata per
/// il monitoraggio locale e per i pre-check di sicurezza PRIMA di interrogare l'exchange.
/// </summary>
public static class MarginMath
{
    /// <summary>
    /// Prezzo al quale margine + PnL non realizzato scende al margine di mantenimento
    /// (= liquidazione). Long: sotto l'entry; short: sopra.
    /// </summary>
    /// <param name="entryPrice">Prezzo di ingresso della posizione.</param>
    /// <param name="quantity">Quantità della posizione (sempre positiva).</param>
    /// <param name="margin">Margine isolato allocato alla posizione.</param>
    /// <param name="notional">Nozionale di apertura (quantity * entryPrice, con leva già applicata).</param>
    /// <param name="isLong">True per posizioni long, false per short.</param>
    /// <param name="maintenanceMarginFraction">Margine di mantenimento come frazione del nozionale (es. 0.005 = 0.5%).</param>
    public static decimal LiquidationPrice(
        decimal entryPrice, decimal quantity, decimal margin, decimal notional, bool isLong, decimal maintenanceMarginFraction)
    {
        if (quantity == 0m) return 0m;
        var buffer = (margin - maintenanceMarginFraction * notional) / quantity;
        return isLong ? entryPrice - buffer : entryPrice + buffer;
    }

    /// <summary>
    /// Distanza dalla liquidazione come frazione del prezzo di ingresso (sempre positiva se
    /// la leva è sostenibile): quanto può muoversi il prezzo, in %, prima della liquidazione.
    /// Dipende solo da leva e margine di mantenimento, non dal prezzo corrente.
    /// </summary>
    public static decimal LiquidationDistanceFraction(decimal leverage, decimal maintenanceMarginFraction)
    {
        if (leverage <= 0m) return 1m; // nessuna leva nota -> assume la massima prudenza
        return 1m / leverage - maintenanceMarginFraction;
    }
}
