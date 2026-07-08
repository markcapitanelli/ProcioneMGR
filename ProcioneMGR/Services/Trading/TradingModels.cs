using System.ComponentModel.DataAnnotations.Schema;
using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.Trading;

public enum TradingMode
{
    Paper,      // Simulazione con dati reali, no soldi veri
    Testnet,    // Testnet exchange (Binance Testnet, Bitget Demo)
    Live,       // Produzione con soldi veri
}

/// <summary>
/// Spot (proprietà dell'asset, leva implicita 1x) vs Futures perpetui a margine ISOLATO
/// (leva configurabile, rischio di liquidazione). Impostato per l'intera sessione di trading
/// (come Symbol/Timeframe), non cambia a runtime.
/// </summary>
public enum MarketType
{
    Spot,
    Futures,
}

public enum OrderSide
{
    Buy,        // = apertura/copertura Long
    Sell,       // = apertura/copertura Short
}

public enum OrderType
{
    Market,
    Limit,
    StopLoss,
    TakeProfit,
}

public enum OrderStatus
{
    Pending,
    Filled,
    PartiallyFilled,
    Cancelled,
    Rejected,
}

public class TradingEngineStatus
{
    public TradingMode Mode { get; set; }
    public MarketType MarketType { get; set; }

    /// <summary>Leva della sessione (1 per Spot; configurabile per Futures).</summary>
    public int Leverage { get; set; } = 1;

    public bool IsRunning { get; set; }
    public string ExchangeName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public decimal TotalCapital { get; set; }
    public decimal AvailableCapital { get; set; }

    /// <summary>
    /// Capitale impegnato in posizioni aperte: nozionale pieno per lo Spot, solo il MARGINE
    /// isolato per i Futures (è quanto viene realmente sottratto ad AvailableCapital — vedi
    /// <c>TradingEngine.ExecuteFuturesOpenAsync</c>).
    /// </summary>
    public decimal UsedCapital { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal TotalPnlPercent { get; set; }

    /// <summary>PnL realizzato nelle ultime 24h (negativo = perdita). Usato dal safety check daily-loss.</summary>
    public decimal DailyPnl { get; set; }

    /// <summary>Max drawdown corrente, in PERCENTUALE (0-100).</summary>
    public decimal MaxDrawdown { get; set; }

    public int TotalTrades { get; set; }

    /// <summary>Numero di posizioni attualmente aperte (per il safety check MaxOpenPositions).</summary>
    public int OpenPositionCount { get; set; }

    public decimal WinRate { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? LastOrderUtc { get; set; }
    public bool IsEmergencyStopped { get; set; }
    public string? EmergencyStopReason { get; set; }
}

public class OpenPosition
{
    public int Id { get; set; }

    /// <summary>Corsia di trading isolata (0 = corsia di default).</summary>
    public int LaneId { get; set; }

    public string PositionId { get; set; } = Guid.NewGuid().ToString("N");
    public string StrategyId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }

    /// <summary>
    /// Trailing stop in %, applicato automaticamente dall'EnsembleStrategy o impostato a mano.
    /// Il livello effettivo si ricalcola ogni candela da <see cref="BestPriceSinceEntry"/> (vedi
    /// <c>TradingEngine.ProcessCandleAsync</c>), sullo stesso schema causale del motore di backtest
    /// (livello calcolato sul best PRIMA di considerare il prezzo corrente).
    /// </summary>
    public decimal? TrailingStopPercent { get; set; }

    /// <summary>Massimo (long) / minimo (short) toccato dal prezzo dall'apertura, per il trailing stop. Null finché il trailing non è attivo.</summary>
    public decimal? BestPriceSinceEntry { get; set; }

    public DateTime OpenedAtUtc { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public decimal UnrealizedPnlPercent { get; set; }
    public string? ExchangeOrderId { get; set; }

    /// <summary>Leva della posizione (1 per Spot).</summary>
    public int Leverage { get; set; } = 1;

    /// <summary>
    /// Prezzo di liquidazione stimato/riportato dall'exchange (solo Futures, null per Spot).
    /// In Testnet/Live è la fonte di verità dell'exchange quando disponibile, altrimenti la
    /// stima locale via <see cref="ProcioneMGR.Services.Risk.MarginMath"/>; in Paper è sempre
    /// la stima locale.
    /// </summary>
    public decimal? LiquidationPrice { get; set; }

    /// <summary>Margine isolato allocato alla posizione (= Quantity*EntryPrice per lo Spot).</summary>
    public decimal MarginBalance { get; set; }
}

public class Order
{
    public int Id { get; set; }

    /// <summary>Corsia di trading isolata (0 = corsia di default).</summary>
    public int LaneId { get; set; }

    public string OrderId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Client order id idempotente inviato all'exchange (newClientOrderId/clientOid).</summary>
    public string ClientOrderId { get; set; } = Guid.NewGuid().ToString("N");

    public string PositionId { get; set; } = string.Empty;
    public string StrategyId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>Prezzo limite, oppure prezzo di riferimento stimato per i market order (per i safety check).</summary>
    public decimal? Price { get; set; }

    public OrderStatus Status { get; set; }
    public decimal? FilledPrice { get; set; }
    public decimal? FilledQuantity { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? FilledAtUtc { get; set; }
    public string? ExchangeOrderId { get; set; }
    public string? ErrorMessage { get; set; }
    public TradingMode Mode { get; set; }
    public MarketType MarketType { get; set; } = MarketType.Spot;

    /// <summary>Leva usata per questo ordine (1 per Spot).</summary>
    public int Leverage { get; set; } = 1;

    /// <summary>Conferma manuale dell'operatore (richiesta in Live se abilitata in SafetyConfiguration).</summary>
    public bool ManuallyConfirmed { get; set; }

    /// <summary>Notional stimato dell'ordine (Quantity × Price).</summary>
    [NotMapped]
    public decimal Notional => Quantity * (Price ?? 0m);
}

public class TradingPerformance
{
    public List<EquityPoint> EquityCurve { get; set; } = new();
    public decimal TotalReturn { get; set; }
    public decimal SharpeRatio { get; set; }
    public decimal MaxDrawdown { get; set; }
    public int TotalTrades { get; set; }
    public decimal WinRate { get; set; }
    public decimal AverageWin { get; set; }
    public decimal AverageLoss { get; set; }
    public decimal ProfitFactor { get; set; }
    public List<TradeRecord> Trades { get; set; } = new();
}

public class TradeRecord
{
    public int Id { get; set; }

    /// <summary>Corsia di trading isolata (0 = corsia di default).</summary>
    public int LaneId { get; set; }

    public string PositionId { get; set; } = string.Empty;
    public string StrategyId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal Pnl { get; set; }
    public decimal PnlPercent { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime ClosedAtUtc { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ExitReason { get; set; }
    public TradingMode Mode { get; set; }
    public MarketType MarketType { get; set; } = MarketType.Spot;

    /// <summary>Leva usata per il trade (1 per Spot).</summary>
    public int Leverage { get; set; } = 1;

    /// <summary>True se la chiusura è stata una liquidazione (forzata o rilevata per riconciliazione).</summary>
    public bool WasLiquidated { get; set; }
}
