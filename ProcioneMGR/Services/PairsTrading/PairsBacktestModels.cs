using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.PairsTrading;

public class PairsBacktestConfiguration
{
    public string SymbolY { get; set; } = string.Empty;
    public string SymbolX { get; set; } = string.Empty;
    public decimal InitialCapital { get; set; } = 10_000m;

    /// <summary>% del capitale corrente impegnata per GAMBA (dollar-neutral: stesso notional su Y e X).</summary>
    public decimal PositionSizePercent { get; set; } = 10m;

    /// <summary>Commissione per lato, in percentuale del notional di ciascuna gamba.</summary>
    public decimal FeePercent { get; set; } = 0.1m;

    /// <summary>Ampiezza della finestra (barre) usata per ristimare l'hedge ratio ad ogni ricalibrazione.</summary>
    public int LookbackWindow { get; set; } = 90;

    /// <summary>Ogni quante barre ristimare l'hedge ratio (walk-forward, mai barre future).</summary>
    public int RecalibrationInterval { get; set; } = 30;

    /// <summary>Finestra per lo z-score rolling causale dello spread.</summary>
    public int ZScoreLookback { get; set; } = 20;

    /// <summary>|z| oltre questa soglia apre la posizione (spread anomalo).</summary>
    public decimal EntryZScore { get; set; } = 2.0m;

    /// <summary>|z| sotto questa soglia chiude la posizione (spread rientrato).</summary>
    public decimal ExitZScore { get; set; } = 0.5m;

    /// <summary>
    /// STOP DI DIVERGENZA: |z| AVVERSO oltre questa soglia forza l'uscita in perdita (il classico
    /// blow-up del pairs — lo spread può divergere all'infinito). Deve essere &gt; <see cref="EntryZScore"/>.
    /// 0 = disattivo (sconsigliato con denaro vero). Default 3.5.
    /// </summary>
    public decimal StopZScore { get; set; } = 3.5m;

    /// <summary>Stop temporale: chiude la posizione dopo questo numero di barre se non è ancora rientrata (0 = disattivo).</summary>
    public int MaxHoldBars { get; set; }

    /// <summary>Slippage sfavorevole (%) applicato al fill di OGNI gamba, in entrata e in uscita (0 = fill teorici).</summary>
    public decimal SlippagePercent { get; set; }
}

/// <summary>LongSpread = Long Y / Short X. ShortSpread = Short Y / Long X.</summary>
public enum PairsPositionSide
{
    Flat,
    LongSpread,
    ShortSpread,
}

public class PairsTrade
{
    public DateTime EntryTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public PairsPositionSide Side { get; set; }
    public decimal EntryPriceY { get; set; }
    public decimal EntryPriceX { get; set; }
    public decimal? ExitPriceY { get; set; }
    public decimal? ExitPriceX { get; set; }
    public decimal HedgeRatioAtEntry { get; set; }
    public decimal Pnl { get; set; }
    public decimal PnlPercent { get; set; }

    /// <summary>Motivo dell'uscita: "MeanReversion" (rientro), "StopZScore" (divergenza), "MaxHold" (tempo), "EndOfData".</summary>
    public string ExitReason { get; set; } = string.Empty;
}

public class PairsBacktestResult
{
    public decimal FinalCapital { get; set; }
    public decimal TotalReturnPercent { get; set; }
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public decimal WinRate { get; set; }
    public decimal MaxDrawdownPercent { get; set; }
    public int CandlesEvaluated { get; set; }
    public List<PairsTrade> Trades { get; set; } = new();
    public List<EquityPoint> EquityCurve { get; set; } = new();
}
