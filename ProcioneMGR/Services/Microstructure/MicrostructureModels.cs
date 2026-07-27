namespace ProcioneMGR.Services.Microstructure;

// =============================================================================================
//  [D3 roadmap scoperta-pattern / C5 passo 3.3] Microstruttura: i tipi elementari.
//
//  D3 chiede una cosa precisa: l'OFI VERO (imbalance firmato al top-of-book, stile
//  Cont-Kukanov-Stoikov) confrontato col proxy che già abbiamo (TakerImbalanceFactor, calcolato
//  dai campi estesi delle klines). La domanda del gate è una sola: **il book aggiunge informazione
//  predittiva OLTRE al proxy trade-flow, o è ridondante?**
//
//  PERCHÉ QUESTI TIPI E NON UNA TABELLA. Il pilota C5 prevedeva di raccogliere dati dal vivo per
//  90 giorni prima di poter rispondere. Ma i dump pubblici di Binance contengono già tape e book
//  storici: si può misurare ORA, senza pagare un costo permanente di raccolta e senza aspettare
//  tre mesi. Quindi qui non c'è nessuna entità EF, nessuna migrazione, nessun writer nuovo: sono
//  tipi di sola lettura che vivono per la durata di una misura.
// =============================================================================================

/// <summary>
/// Un trade aggregato del tape (una riga di <c>aggTrades</c>): tutti gli scambi consecutivi allo
/// stesso prezzo, dallo stesso lato, in un colpo solo.
///
/// <see cref="IsBuyerMaker"/> è la convenzione di Binance e va letta al contrario di come suona:
/// se il COMPRATORE era il maker, allora l'aggressore — chi ha attraversato lo spread — era il
/// venditore. Sbagliare questo segno capovolgerebbe l'intero order flow, ed è il primo errore che
/// i test verificano.
/// </summary>
public sealed record AggTrade(
    long Id,
    decimal Price,
    decimal Quantity,
    DateTime TimestampUtc,
    bool IsBuyerMaker)
{
    /// <summary>Vero se l'aggressore era in acquisto (taker buy): è il volume "informato" della letteratura.</summary>
    public bool IsTakerBuy => !IsBuyerMaker;
}

/// <summary>
/// Barra del tape aggregata su un intervallo fisso (nel pilota: 10 secondi). È l'unità che il
/// piano C5 §9.2 aveva scelto proprio per non archiviare tick: l'informazione che serve a un
/// segnale di order flow (volume ai due lati, conteggio, prezzo) sopravvive all'aggregazione.
/// </summary>
public sealed record TapeBar(
    DateTime StartUtc,
    TimeSpan Duration,
    decimal BuyVolume,
    decimal SellVolume,
    int TradeCount,
    decimal? Close)
{
    public decimal Volume => BuyVolume + SellVolume;

    /// <summary>
    /// Sbilanciamento in [-1, +1]: (buy − sell) / (buy + sell). <c>null</c> su una barra senza
    /// scambi — uno zero finto sarebbe un "equilibrio perfetto" mai osservato, e la piattaforma ha
    /// già la regola di non inventare valori dove il dato manca (vedi OrderFlowFactors).
    /// </summary>
    public decimal? Imbalance => Volume > 0m ? (BuyVolume - SellVolume) / Volume : null;

    public bool IsEmpty => TradeCount == 0;
}

/// <summary>
/// Miglior bid/ask con le rispettive quantità: l'input dell'OFI VERO. Non è ricostruibile dai dump
/// storici (i file <c>bookTicker</c> non esistono su data.binance.vision), ma è esattamente ciò che
/// il feed R1 riceve già oggi da <c>{sym}@bookTicker</c> e che <c>BinanceStreamMapper</c> scarta:
/// se il pilota andrà accesso dal vivo, la formula che lo consuma è già qui e già verificata.
/// </summary>
public sealed record BestQuote(
    DateTime TimestampUtc,
    decimal BidPrice,
    decimal BidSize,
    decimal AskPrice,
    decimal AskSize)
{
    public decimal Mid => (BidPrice + AskPrice) / 2m;

    /// <summary>Sbilanciamento statico del top-of-book: (qB − qA)/(qB + qA), in [-1, +1].</summary>
    public decimal? QueueImbalance =>
        BidSize + AskSize > 0m ? (BidSize - AskSize) / (BidSize + AskSize) : null;
}

/// <summary>
/// Fotografia della PROFONDITÀ a bande percentuali dal mid, che è la forma in cui il book esiste
/// storicamente (dump <c>bookDepth</c>: uno snapshot ogni 30 secondi, bande ±0,20% e ±1…5%).
///
/// Non è il top-of-book: è la liquidità cumulata entro una distanza dal mid. La banda più fine
/// (±0,20%) è la più vicina alla domanda "com'è messo il book adesso" e quella su cui si misura.
/// La differenza col top-of-book va dichiarata, non nascosta: vedi la nota in
/// <see cref="OrderFlowImbalance"/>.
/// </summary>
public sealed record BookDepthSnapshot(
    DateTime TimestampUtc,
    IReadOnlyDictionary<decimal, decimal> NotionalByPercentage)
{
    /// <summary>Notional disponibile sul lato BID entro <paramref name="band"/>% dal mid (banda negativa nel file).</summary>
    public decimal? BidNotional(decimal band) =>
        NotionalByPercentage.TryGetValue(-Math.Abs(band), out var v) ? v : null;

    /// <summary>Notional disponibile sul lato ASK entro <paramref name="band"/>% dal mid.</summary>
    public decimal? AskNotional(decimal band) =>
        NotionalByPercentage.TryGetValue(Math.Abs(band), out var v) ? v : null;

    /// <summary>Sbilanciamento di profondità nella banda: (bid − ask)/(bid + ask), in [-1, +1].</summary>
    public decimal? Imbalance(decimal band)
    {
        if (BidNotional(band) is not decimal bid || AskNotional(band) is not decimal ask) return null;
        var total = bid + ask;
        return total > 0m ? (bid - ask) / total : null;
    }
}
