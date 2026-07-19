namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Limiti di sicurezza del trading. Bindato da appsettings.json sezione "Trading:Safety".
/// I default sono CONSERVATIVI: in caso di config mancante il sistema resta prudente.
/// </summary>
public class SafetyConfiguration
{
    /// <summary>Max % del capitale totale per una singola posizione.</summary>
    public decimal MaxPositionSizePercent { get; set; } = 10m;

    /// <summary>
    /// % del capitale impiegata per ogni apertura: NOZIONALE investito per lo Spot (leva
    /// implicita 1x), MARGINE isolato per i Futures — il nozionale reale è margine × leva,
    /// stessa semantica del motore di backtest. Coerenza richiesta (validata a StartAsync):
    /// PositionSizePercent × leva ≤ <see cref="MaxPositionSizePercent"/> ≤
    /// <see cref="MaxTotalExposurePercent"/>, altrimenti il SafetyChecker rifiuterebbe OGNI
    /// ordine e la corsia non farebbe mai trading.
    /// </summary>
    public decimal PositionSizePercent { get; set; } = 8m;

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

    /// <summary>
    /// [P0-5] Se true, all'apertura di una posizione FUTURES in Testnet/Live il motore piazza
    /// sull'exchange ordini TRIGGER reduce-only (stop-market e take-profit-market) come protezione
    /// "resting": restano validi sull'exchange anche se il processo va giù o perde connettività,
    /// chiudendo la posizione contro i gap-through — cosa che gli stop software-monitored su
    /// candela chiusa NON garantiscono.
    ///
    /// Default FALSE: gli stop restano software-monitored (fonte di verità <see cref="OpenPosition.StopLoss"/>).
    /// Gli endpoint trigger sono IMPLEMENTATI su Bitget/Binance
    /// (<see cref="ProcioneMGR.Services.Exchanges.IFuturesExchangeClient.PlaceFuturesTriggerOrderAsync"/>)
    /// e gli id dei trigger sono PERSISTITI sulla posizione (M3, sopravvivono al riavvio), ma la
    /// semantica va VERIFICATA su Demo/Testnet prima di attivarli in Live. Il branch guardato non
    /// blocca mai l'apertura: se il piazzamento del trigger fallisce, logga un warning e restano
    /// gli stop software.
    /// </summary>
    public bool UseExchangeRestingStops { get; set; }

    /// <summary>
    /// [B1] Banda massima (± % dal prezzo corrente di mercato) entro cui il prezzo di fill
    /// riportato dall'exchange è considerato plausibile. Fuori banda (o ≤ 0) il fill è SOSPETTO
    /// e non viene mai adottato: vedi <see cref="Internal.FillSanityCheck"/> e il bug B1 in
    /// docs/TEST-UI-2026-07-18.md (testnet che risponde "Filled @ 0"). Il default è largo
    /// apposta: lo slippage reale di un market order è ben sotto, ma il prezzo di riferimento
    /// è la chiusura dell'ultima candela e può ritardare il mercato fino a una candela intera.
    /// </summary>
    public decimal MaxFillPriceDeviationPercent { get; set; } = 20m;

    /// <summary>
    /// [B1] Tolleranza massima (± % dalla quantità RICHIESTA) entro cui la quantità di fill
    /// riportata dall'exchange è considerata plausibile. Fuori tolleranza (es. quantità
    /// cumulative 100x dal testnet, bug B1) il fill è SOSPETTO e non viene mai adottato.
    /// NB deliberatamente stretta: anche un parziale GENUINO fuori tolleranza finisce in
    /// verifica manuale — su market order è un'anomalia, meglio prudenti.
    /// </summary>
    public decimal MaxFillQuantityDeviationPercent { get; set; } = 5m;

    /// <summary>
    /// [P2-8] Fee dell'exchange in % del nozionale, applicata sia in apertura sia in chiusura.
    /// Prima era una costante fissa in TradingEngine (stesso valore di default, 0.1%), scollegata
    /// dal fee reale e dal parametro equivalente e già configurabile del backtest
    /// (<c>BacktestConfiguration.FeePercent</c>) — un'asimmetria silenziosa fra backtest e live
    /// della stessa classe di problema già corretta per lo stop-loss intrabar. Hot-reload via
    /// IOptionsMonitor: se il fee tier reale cambia (o si cambia exchange), si aggiorna qui senza
    /// riavviare la corsia.
    /// </summary>
    public decimal FeePercent { get; set; } = 0.1m;
}
