namespace ProcioneMGR.Services.Backtesting;

/// <summary>Segnale emesso da una strategia per ogni candela.</summary>
public enum Signal
{
    Hold,
    Long,
    Short,
    Close,
}

public class BacktestConfiguration
{
    public string ExchangeName { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public decimal InitialCapital { get; set; } = 10000m;

    /// <summary>% del capitale corrente impegnata per ogni trade.</summary>
    public decimal PositionSizePercent { get; set; } = 10m;

    /// <summary>Commissione per lato (entry/exit), in percentuale del notional. Default 0.1%.</summary>
    public decimal FeePercent { get; set; } = 0.1m;

    public string StrategyName { get; set; } = string.Empty;

    public Dictionary<string, decimal> StrategyParameters { get; set; } = new();

    /// <summary>
    /// Stop loss in % dal prezzo di ingresso (0 = disattivo). Overlay a livello di MOTORE
    /// (McAllen: "lo stop loss E' parte del trade"): controllato su high/low di ogni candela
    /// PRIMA del segnale di strategia, eseguito al livello di stop. La strategia non viene
    /// notificata: puo' rientrare coi propri segnali successivi.
    /// </summary>
    public decimal StopLossPercent { get; set; }

    /// <summary>Take profit in % dal prezzo di ingresso (0 = disattivo).</summary>
    public decimal TakeProfitPercent { get; set; }

    /// <summary>
    /// Trailing stop in % dal miglior prezzo raggiunto dall'ingresso (0 = disattivo).
    /// Sale con il prezzo e non scende mai: preserva i guadagni (McAllen cap. 17).
    /// </summary>
    public decimal TrailingStopPercent { get; set; }

    /// <summary>
    /// Leva finanziaria (futures/margin). Con leva L, <see cref="PositionSizePercent"/> e' la
    /// quota di capitale usata come MARGINE e il nozionale e' margine x L. A 1 (default) il
    /// comportamento coincide esattamente con lo spot attuale. Con L &gt; 1 il motore modella
    /// anche la LIQUIDAZIONE intrabar (vedi <see cref="MaintenanceMarginPercent"/>).
    /// </summary>
    public decimal Leverage { get; set; } = 1m;

    /// <summary>
    /// Margine di mantenimento in % del nozionale (default 0.5%, tipico dei perpetual su
    /// coppie liquide). La posizione viene liquidata quando margine + PnL non realizzato
    /// scende a questo livello: si perde quasi tutto il margine, come nella realta'.
    /// </summary>
    public decimal MaintenanceMarginPercent { get; set; } = 0.5m;

    /// <summary>
    /// Funding rate dei perpetual in % del nozionale per periodo di 8 ore (0 = disattivo;
    /// 0.01 e' il valore "neutro" storico). Addebitato pro-rata a ogni candela con posizione
    /// aperta: a leva alta su holding lunghi pesa piu' delle commissioni.
    /// </summary>
    public decimal FundingRatePercentPer8h { get; set; }

    /// <summary>
    /// Slippage in % applicato SFAVOREVOLMENTE a ogni eseguito (entry, exit, stop, target,
    /// liquidazione). 0 = fill teorici (default, comportamento invariato).
    /// </summary>
    public decimal SlippagePercent { get; set; }
}

public class BacktestResult
{
    public decimal FinalCapital { get; set; }
    public decimal TotalReturnPercent { get; set; }
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public decimal WinRate { get; set; }
    public decimal MaxDrawdownPercent { get; set; }
    public int CandlesEvaluated { get; set; }

    /// <summary>Numero di posizioni chiuse per liquidazione forzata (solo con leva &gt; 1).</summary>
    public int LiquidationCount { get; set; }

    public List<BacktestTrade> Trades { get; set; } = new();
    public List<EquityPoint> EquityCurve { get; set; } = new();
}

public class BacktestTrade
{
    public DateTime EntryTime { get; set; }
    public decimal EntryPrice { get; set; }
    public DateTime? ExitTime { get; set; }
    public decimal? ExitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal Pnl { get; set; }
    public decimal PnlPercent { get; set; }

    /// <summary>"Long" o "Short" (utile in tabella).</summary>
    public string Direction { get; set; } = "Long";

    /// <summary>True se la posizione e' stata chiusa per liquidazione forzata (margine esaurito).</summary>
    public bool WasLiquidated { get; set; }
}

public class EquityPoint
{
    public DateTime Timestamp { get; set; }
    public decimal Capital { get; set; }
}
