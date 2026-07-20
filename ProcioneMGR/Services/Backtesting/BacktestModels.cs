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

    /// <summary>
    /// Come viene eseguito l'INGRESSO. <see cref="EntryExecutionStyle.Taker"/> è il default e
    /// lascia il comportamento invariato. Le uscite restano sempre taker: uno stop protettivo è
    /// un ordine a mercato per natura — non lo si può appoggiare passivamente al book e sperare.
    /// </summary>
    public EntryExecutionStyle EntryExecution { get; set; } = EntryExecutionStyle.Taker;

    /// <summary>
    /// Quanto passivo si mette il limite, in % sotto (long) o sopra (short) la close del segnale.
    /// Più è passivo, meglio si compra QUANDO si viene riempiti — e meno spesso si viene riempiti.
    /// </summary>
    public decimal MakerOffsetPercent { get; set; } = 0.05m;

    /// <summary>Per quante candele il limite resta appoggiato prima di scadere.</summary>
    public int MakerMaxWaitBars { get; set; } = 3;

    /// <summary>Commissione per lato di un eseguito MAKER, in % del nozionale (tipicamente &lt; <see cref="FeePercent"/>).</summary>
    public decimal MakerFeePercent { get; set; } = 0.02m;

    /// <summary>
    /// Alla scadenza del limite non riempito: true = si attraversa lo spread e si entra comunque
    /// a mercato (taker), false = il segnale si perde. Sono due strategie diverse, non due
    /// sfumature della stessa: la prima paga il taker proprio sui casi in cui il prezzo è scappato,
    /// la seconda rinuncia al trade.
    /// </summary>
    public bool MakerFallbackToTaker { get; set; }
}

/// <summary>Come viene piazzato l'ordine di INGRESSO nel backtest.</summary>
public enum EntryExecutionStyle
{
    /// <summary>Attraversa lo spread: fill certo alla close del segnale, commissione taker.</summary>
    Taker,

    /// <summary>
    /// Limite passivo: commissione maker e prezzo migliore, ma il fill NON è garantito e avviene
    /// solo se il mercato viene a prendere l'ordine — cioè, per un long, solo se il prezzo scende.
    /// </summary>
    Maker,
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

    /// <summary>[R2] Commissioni pagate in valuta, su entrambi i lati di ogni trade.</summary>
    public decimal TotalFeesPaid { get; set; }

    /// <summary>[R2] Attrito di slippage in valuta, stimato sul nozionale di ogni fill.</summary>
    public decimal TotalSlippagePaid { get; set; }

    /// <summary>[R2] Funding perpetual addebitato in valuta (0 senza leva/derivati).</summary>
    public decimal TotalFundingPaid { get; set; }

    /// <summary>[R3] Ingressi tentati come limite maker (0 in modalità Taker).</summary>
    public int MakerEntriesAttempted { get; set; }

    /// <summary>[R3] Di quelli, quanti sono stati effettivamente riempiti al prezzo limite.</summary>
    public int MakerEntriesFilled { get; set; }

    /// <summary>[R3] Limiti scaduti senza fill e poi entrati comunque a mercato (fallback taker).</summary>
    public int MakerEntriesFallbackTaker { get; set; }

    /// <summary>[R3] Segnali PERSI perché il limite non è stato riempito e non c'era fallback.</summary>
    public int MakerEntriesMissed { get; set; }

    /// <summary>
    /// [R3] Frazione di limiti riempiti. È il numero che smonta o conferma l'ipotesi ottimistica
    /// "maker = commissione più bassa": un tasso di riempimento alto su una strategia che insegue
    /// il prezzo sarebbe sospetto, uno basso dice quanti segnali il maker semplicemente non prende.
    /// </summary>
    public decimal MakerFillRate => MakerEntriesAttempted == 0
        ? 0m
        : (decimal)MakerEntriesFilled / MakerEntriesAttempted * 100m;

    /// <summary>Capitale iniziale, ripetuto qui perché i rapporti sotto siano leggibili da soli.</summary>
    public decimal InitialCapital { get; set; }

    /// <summary>[R2] Attrito totale: commissioni + slippage + funding.</summary>
    public decimal TotalCosts => TotalFeesPaid + TotalSlippagePaid + TotalFundingPaid;

    /// <summary>
    /// [R2] Costi in % del capitale iniziale. È il numero che decide se un timeframe è operabile:
    /// un rendimento netto del 3% con un cost drag del 40% non è una strategia mediocre, è una
    /// strategia che regala all'exchange tredici volte quello che tiene.
    /// </summary>
    public decimal CostDragPercent => InitialCapital > 0m ? TotalCosts / InitialCapital * 100m : 0m;

    /// <summary>
    /// [R2] Rendimento che ci sarebbe stato SENZA attrito. Il divario con
    /// <see cref="TotalReturnPercent"/> è esattamente ciò che i costi hanno eroso.
    /// </summary>
    public decimal GrossReturnPercent => TotalReturnPercent + CostDragPercent;

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
