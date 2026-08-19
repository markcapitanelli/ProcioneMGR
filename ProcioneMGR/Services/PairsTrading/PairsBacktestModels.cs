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

    /// <summary>
    /// [E1] FILTRO DI VOLATILITÀ dello spread. Salta l'apertura di una nuova posizione quando la
    /// volatilità RECENTE dello spread (finestra <see cref="ZScoreLookback"/>) supera di questo
    /// rapporto la sua volatilità di BASE (finestra <see cref="SpreadVolBaselineWindow"/>): è il
    /// regime in cui la relazione si sta rompendo e la mean-reversion diventa un blow-up. La
    /// letteratura stat-arb che ottiene Sharpe alti usa questo filtro insieme al lookback dinamico e
    /// allo stop di divergenza. 0 = disattivo (comportamento storico). Valore tipico: 1,5-2,0.
    /// </summary>
    public decimal MaxSpreadVolRatio { get; set; }

    /// <summary>Finestra di base della volatilità dello spread per il filtro (vedi <see cref="MaxSpreadVolRatio"/>).</summary>
    public int SpreadVolBaselineWindow { get; set; } = 120;

    /// <summary>
    /// [C2] Estimatore dell'hedge ratio. Default <see cref="PairsHedgeRatioEstimator.Kalman"/> per
    /// esito del gate C2 MISURATO (2026-07-26, fase `pairs 1d` di PlatformExpand, holdout
    /// 2026-03-01→oggi sulle 5 coppie operabili in selezione): spread OOS più stazionario in 5/5
    /// (mediana ΔADF −0,98, stabile con δ da 1e-5 a 1e-3) e MaxDD minore in 5/5 (mediana −0,9 pt).
    /// <see cref="PairsHedgeRatioEstimator.RollingOls"/> resta selezionabile (comportamento storico,
    /// byte-identico). NB: la classe pairs resta NON schierata (0 sopravvissuti all'holdout).
    /// </summary>
    public PairsHedgeRatioEstimator HedgeRatioEstimator { get; set; } = PairsHedgeRatioEstimator.Kalman;

    /// <summary>[C2] δ del filtro di Kalman (rumore di stato, adimensionale). Vedi <see cref="KalmanPairsSpreadAnalyzer"/>.</summary>
    public double KalmanDelta { get; set; } = KalmanPairsSpreadAnalyzer.DefaultDelta;
}

/// <summary>[C2] Come viene stimato l'hedge ratio del pairs, a parità di tutto il resto.</summary>
public enum PairsHedgeRatioEstimator
{
    /// <summary>Rolling OLS su finestra fissa, ristimata a intervalli (comportamento storico).</summary>
    RollingOls,

    /// <summary>Filtro di Kalman con β a passeggiata aleatoria, aggiornato a ogni barra.</summary>
    Kalman,
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

    /// <summary>
    /// [I10] L'analisi dello spread che il motore ha <b>davvero</b> usato per decidere: hedge ratio,
    /// spread e z-score dell'estimatore scelto in configurazione.
    ///
    /// <para>Nasce da un difetto trovato il 2026-08-18 e presente dal 2026-07-26 (adozione del
    /// Kalman): la pagina <c>/pairs-trading</c> passava al motore l'estimatore selezionato ma
    /// disegnava il grafico dello z-score con un <c>RollingPairsSpreadAnalyzer</c> <b>fisso</b>.
    /// Scegliendo Kalman si vedeva la curva dell'OLS — il grafico descriveva un backtest diverso da
    /// quello eseguito, e la doc di pagina dichiarava proprio che questo non poteva succedere
    /// («nessuna doppia verità»).</para>
    ///
    /// <para>Esporla invece di far ricalcolare la pagina toglie la possibilità stessa della
    /// divergenza: non c'è un secondo calcolo che possa usare parametri diversi.</para>
    /// </summary>
    public RollingPairsAnalysis? Analysis { get; set; }

    /// <summary>L'estimatore usato, per dichiararlo accanto al grafico invece di lasciarlo intuire.</summary>
    public PairsHedgeRatioEstimator EstimatorUsed { get; set; }
}
