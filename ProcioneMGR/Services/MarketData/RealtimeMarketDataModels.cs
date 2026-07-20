using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.MarketData;

/// <summary>
/// Una sottoscrizione al feed real-time: exchange + simbolo canonico ("BTC/USDT") + timeframe
/// canonico ("5m") + tipo di mercato. Il timeframe serve solo allo stream delle candele: i tick
/// di prezzo non ne hanno uno.
/// </summary>
public readonly record struct StreamSubscription(
    ExchangeName Exchange,
    string Symbol,
    string Timeframe,
    MarketType MarketType);

/// <summary>
/// Aggiornamento del book in cima (best bid / best ask) per un simbolo.
///
/// <see cref="Mid"/> è il prezzo usato per valutare le uscite protettive. Scelta deliberata rispetto
/// al lato "onesto" (bid per chiudere un long, ask per chiudere uno short): su coppie liquide il
/// mezzo spread è trascurabile, mentre usare il lato farebbe scattare gli stop di long e short in
/// momenti diversi sullo stesso mercato, e renderebbe il livello sensibile a un allargamento
/// momentaneo del book. Il prezzo di ESECUZIONE resta comunque quello riportato dall'exchange sul
/// market order di chiusura, non questo.
/// </summary>
public readonly record struct PriceTick(
    ExchangeName Exchange,
    string Symbol,
    decimal Bid,
    decimal Ask,
    DateTime TimestampUtc)
{
    public decimal Mid => (Bid + Ask) / 2m;

    /// <summary>Spread relativo al mezzo, in percentuale. Usato per scartare quotazioni implausibili.</summary>
    public decimal SpreadPercent
    {
        get
        {
            var mid = Mid;
            return mid > 0m ? (Ask - Bid) / mid * 100m : decimal.MaxValue;
        }
    }

    /// <summary>
    /// Quotazione utilizzabile per una decisione. Un book incrociato (ask &lt; bid), un prezzo non
    /// positivo o uno spread abnorme indicano una quotazione stantia o corrotta — la stessa classe
    /// di spazzatura che il bug B1 ha mostrato arrivare dai testnet. Su queste NON si decide mai
    /// un'uscita: si scarta il tick e si aspetta il successivo.
    /// </summary>
    public bool IsPlausible(decimal maxSpreadPercent) =>
        Bid > 0m && Ask > 0m && Ask >= Bid && SpreadPercent <= maxSpreadPercent;
}

/// <summary>
/// Candela CHIUSA notificata dallo stream (Binance: <c>k.x == true</c>). Trasporta l'OHLCV completo,
/// quindi può essere consegnata al motore senza attendere il ciclo REST.
/// </summary>
public readonly record struct BarClosed(
    ExchangeName Exchange,
    string Symbol,
    string Timeframe,
    DateTime OpenTimeUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume)
{
    public OhlcvData ToOhlcv() => new()
    {
        Symbol = Symbol,
        Timeframe = Timeframe,
        TimestampUtc = OpenTimeUtc,
        Open = Open,
        High = High,
        Low = Low,
        Close = Close,
        Volume = Volume,
    };
}

/// <summary>Evento emesso dal parser di uno stream: un tick, una candela chiusa, o niente di utile.</summary>
public readonly record struct StreamEvent(PriceTick? Tick, BarClosed? Bar)
{
    public static StreamEvent None => default;

    public static StreamEvent FromTick(PriceTick tick) => new(tick, null);

    public static StreamEvent FromBar(BarClosed bar) => new(null, bar);

    public bool IsEmpty => Tick is null && Bar is null;
}

/// <summary>
/// Configurazione del feed real-time, sezione <c>MarketData:Realtime</c> di appsettings.json.
/// I default sono pensati per essere INERTI: a feature spenta il comportamento della piattaforma è
/// identico a prima del feed.
/// </summary>
public sealed class RealtimeFeedOptions
{
    public const string SectionName = "MarketData:Realtime";

    /// <summary>
    /// Interruttore generale. DEFAULT FALSE: il feed è additivo rispetto alla sincronizzazione REST
    /// già esistente, quindi spegnerlo riporta esattamente al comportamento a sole candele.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Se true i tick alimentano le uscite protettive del motore. Separato da <see cref="Enabled"/>
    /// apposta: permette di tenere il feed acceso in sola OSSERVAZIONE (log e metriche, nessuna
    /// decisione) per convincersi che i prezzi siano sani prima di dargli potere di chiudere
    /// posizioni. Stesso spirito del dual-read ML della Fase 2a.
    /// </summary>
    public bool DriveProtectiveExits { get; set; } = true;

    /// <summary>Ogni quanto rileggere le corsie per aggiornare l'insieme delle sottoscrizioni.</summary>
    public int SubscriptionRefreshSeconds { get; set; } = 30;

    /// <summary>
    /// Silenzio oltre il quale il feed è considerato STALE. Non blocca nulla (la sincronizzazione
    /// REST resta comunque attiva e indipendente), ma smette di essere considerato una fonte viva e
    /// genera un allarme: non si opera mai credendo di avere prezzi aggiornati quando non è vero.
    /// </summary>
    public int StaleAfterSeconds { get; set; } = 60;

    /// <summary>Attesa iniziale prima di un tentativo di riconnessione.</summary>
    public int ReconnectInitialDelayMs { get; set; } = 1_000;

    /// <summary>Tetto dell'attesa di riconnessione (backoff esponenziale con jitter).</summary>
    public int ReconnectMaxDelayMs { get; set; } = 60_000;

    /// <summary>Vedi <see cref="PriceTick.IsPlausible"/>: oltre questo spread il tick è scartato.</summary>
    public decimal MaxSpreadPercent { get; set; } = 2m;
}
