using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Exchanges;

/// <summary>Esito dell'impostazione della leva su un simbolo (richiesta PRIMA di ogni apertura).</summary>
public class SetLeverageResult
{
    public bool Success { get; set; }
    public int Leverage { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Posizione futures come riportata dall'EXCHANGE (fonte di verità): a differenza della stima
/// locale (<see cref="ProcioneMGR.Services.Risk.MarginMath"/>), il prezzo di liquidazione qui
/// è quello REALE calcolato dall'exchange (include fondo assicurativo, mark price, fee).
/// </summary>
public class FuturesPosition
{
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Sempre positiva; il lato è in <see cref="Side"/>.</summary>
    public decimal Quantity { get; set; }

    /// <summary>"LONG" | "SHORT".</summary>
    public string Side { get; set; } = string.Empty;

    public decimal EntryPrice { get; set; }
    public decimal MarkPrice { get; set; }
    public int Leverage { get; set; }
    public decimal LiquidationPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }

    /// <summary>Margine isolato allocato alla posizione.</summary>
    public decimal MarginBalance { get; set; }
}

/// <summary>Saldo del conto futures (margine, non asset spot).</summary>
public class FuturesBalance
{
    /// <summary>Margine disponibile per aprire nuove posizioni.</summary>
    public decimal AvailableMargin { get; set; }

    /// <summary>Equity totale del conto futures (margine + PnL non realizzato).</summary>
    public decimal TotalEquity { get; set; }
}

/// <summary>
/// Estensione futures (perpetual USDT-margined, margine ISOLATO) di <see cref="IExchangeClient"/>.
/// Interfaccia SEPARATA e non metodi opzionali sull'esistente: spot e futures hanno semantiche
/// diverse (saldo vs posizione, leva, margine, liquidazione) e mescolarle avrebbe reso
/// <see cref="IExchangeClient"/> ambiguo. <see cref="BinanceClient"/>/<see cref="BitgetClient"/>
/// implementano ENTRAMBE le interfacce sulla stessa classe (stesso HttpClient/firma HMAC).
///
/// Margine ISOLATO per scelta di sicurezza: ogni posizione rischia solo il proprio margine,
/// mai l'intero saldo del conto (a differenza del margine "cross") — coerente con l'uso di
/// leva alta su un capitale piccolo, dove l'isolamento del rischio per trade è essenziale.
/// </summary>
public interface IFuturesExchangeClient
{
    ExchangeName Exchange { get; }

    /// <summary>Imposta la leva (margine isolato) per il simbolo. Va chiamata PRIMA di ogni apertura.</summary>
    Task<SetLeverageResult> SetLeverageAsync(string symbol, int leverage, TradingCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Piazza un ordine futures. Riusa <see cref="PlaceOrderRequest"/>/<see cref="PlaceOrderResult"/>
    /// dello spot (stessa forma); la leva è già impostata separatamente via <see cref="SetLeverageAsync"/>.
    /// </summary>
    /// <param name="reduceOnly">
    /// True per gli ordini di CHIUSURA: impedisce all'exchange di aprire/aumentare una
    /// posizione se la quantità fosse per errore maggiore di quella effettivamente aperta.
    /// </param>
    Task<PlaceOrderResult> PlaceFuturesOrderAsync(PlaceOrderRequest request, bool reduceOnly, CancellationToken ct = default);

    /// <summary>
    /// [P0-5] Piazza un ordine TRIGGER reduce-only che vive sull'exchange come protezione "resting": uno
    /// stop-market (<paramref name="isStopLoss"/> = true) o un take-profit-market (false), attivato quando
    /// il mark price tocca <see cref="PlaceOrderRequest.TriggerPrice"/>. A differenza degli stop
    /// software-monitored su candela chiusa, resta valido anche se il processo va giù e protegge dai gap-through.
    ///
    /// Implementato su entrambi i client (plan-order Bitget / STOP_MARKET Binance) ma da VERIFICARE su
    /// Demo/Testnet prima di abilitare <c>UseExchangeRestingStops</c> in Live. Il chiamante deve comunque
    /// trattare un fallimento come NON bloccante e mantenere attivi gli stop software.
    /// </summary>
    Task<PlaceOrderResult> PlaceFuturesTriggerOrderAsync(PlaceOrderRequest request, bool isStopLoss, CancellationToken ct = default);

    /// <summary>Posizione aperta corrente per il simbolo (null se flat). Fonte di verità per la liquidazione.</summary>
    Task<FuturesPosition?> GetPositionAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default);

    Task<CancelOrderResult> CancelFuturesOrderAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = default);

    Task<List<OpenOrder>> GetOpenFuturesOrdersAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Stato di un ordine futures per client order id, INCLUSI gli ordini già eseguiti/terminati
    /// (a differenza di <see cref="GetOpenFuturesOrdersAsync"/>). Lookup autorevole per la
    /// riconciliazione dopo un <see cref="PlaceOrderResult.NetworkUncertain"/>.
    /// </summary>
    Task<OrderStatusResult> GetFuturesOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = default);

    /// <summary>Saldo del conto futures (margine disponibile, equity totale).</summary>
    Task<FuturesBalance> GetFuturesBalanceAsync(TradingCredentials credentials, CancellationToken ct = default);

    /// <summary>Filtri di trading del simbolo futures (LOT_SIZE/PRICE_FILTER/minNotional). Endpoint pubblico.</summary>
    Task<SymbolFilters> GetFuturesSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default);

    /// <summary>Funding rate corrente, in % per periodo di 8 ore (stessa convenzione di <c>FundingRatePercentPer8h</c> nel backtest). Endpoint pubblico.</summary>
    Task<decimal> GetFundingRateAsync(string symbol, bool testnet, CancellationToken ct = default);
}
