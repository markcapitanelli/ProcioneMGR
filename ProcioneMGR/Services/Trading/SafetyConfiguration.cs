namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Limiti di sicurezza del trading. Bindato da appsettings.json sezione "Trading:Safety".
/// I default sono CONSERVATIVI: in caso di config mancante il sistema resta prudente.
/// </summary>
public class SafetyConfiguration
{
    /// <summary>Max % del capitale totale per una singola posizione.</summary>
    public decimal MaxPositionSizePercent { get; set; } = 10m;

    /// <summary>Max % del capitale totale impegnata complessivamente in posizioni aperte.</summary>
    public decimal MaxTotalExposurePercent { get; set; } = 50m;

    /// <summary>Stop trading se la perdita giornaliera supera questa % del capitale.</summary>
    public decimal MaxDailyLossPercent { get; set; } = 5m;

    /// <summary>Stop trading se il drawdown supera questa %.</summary>
    public decimal MaxDrawdownPercent { get; set; } = 20m;

    /// <summary>Numero massimo di posizioni aperte contemporaneamente.</summary>
    public int MaxOpenPositions { get; set; } = 5;

    /// <summary>Intervallo minimo (secondi) tra un ordine e il successivo (anti-spam).</summary>
    public int MinOrderIntervalSeconds { get; set; } = 10;

    /// <summary>Se true, ogni ordine in modalità Live richiede conferma manuale dell'operatore.</summary>
    public bool RequireManualConfirmationForLive { get; set; } = true;

    /// <summary>
    /// Leva massima consentita per il trading Futures (default CONSERVATIVO: con un capitale
    /// piccolo la leva alta è attraente ma la crescita del rischio non è lineare — vedi
    /// <see cref="ProcioneMGR.Services.Risk.LeverageAdvisor"/>, che tipicamente sconsiglia oltre
    /// 3-5x anche per sistemi con un edge reale). L'utente può alzarla consapevolmente qui.
    /// </summary>
    public int MaxLeverageAllowed { get; set; } = 5;

    /// <summary>
    /// Margine di mantenimento in % del nozionale, usato per la STIMA locale del prezzo di
    /// liquidazione (<see cref="ProcioneMGR.Services.Risk.MarginMath"/>) quando l'exchange non
    /// la riporta ancora (es. subito dopo il fill, o in modalità Paper). Stessa convenzione e
    /// stesso default del motore di backtest (<c>BacktestConfiguration.MaintenanceMarginPercent</c>).
    /// </summary>
    public decimal MaintenanceMarginPercent { get; set; } = 0.5m;
}
